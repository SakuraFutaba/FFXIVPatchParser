using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace FFXIVPatchParser;

public sealed class DataminingSource : ITableSource
{
    private const string Repository = "InfSein/ffxiv-datamining-mixed";
    private readonly string commit;
    public string Label { get; }
    public string Format => "DataminingCsv";
    public IReadOnlyDictionary<string, SheetEntry> Sheets { get; }
    private static string Cache => Path.Combine(Workspace.Temp, "ui", "datamining");
    private DataminingSource(string commit, string label, Dictionary<string, SheetEntry> sheets) { this.commit = commit; Label = label; Sheets = sheets; }

    public static async Task<(ITableSource Before, ITableSource After)> ForPatch(string from, string to, IProgress<string>? progress, CancellationToken token)
    {
        var versionPath = Path.Combine(Workspace.Temp, "patches", "metadata", "ver2.dat");
        if (!File.Exists(versionPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(versionPath)!);
            await File.WriteAllTextAsync(versionPath, await Workspace.Http.GetStringAsync(PatchCatalog.VersionUrl, token), token);
        }
        using var versions = JsonDocument.Parse(await File.ReadAllTextAsync(versionPath, token));
        string Build(string value)
        {
            var package = versions.RootElement.GetProperty("packages").EnumerateArray().FirstOrDefault(p => p.GetProperty("to").GetString() == value);
            var build = package.ValueKind == JsonValueKind.Undefined ? "" : package.GetProperty("versionView").GetString()!.Split('_')[0];
            if (!Regex.IsMatch(build, @"^\d{4}\.\d{2}\.\d{2}\.\d{4}\.\d{4}$")) throw new InvalidDataException($"官方元数据没有版本 {value} 的准确构建号，请选择本地快照。");
            return build;
        }
        progress?.Report("正在匹配 CHS 历史版本…");
        using var commits = await Json("commits-chs.json", $"commits?path=chs&per_page=100", false, token);
        (string Sha, string Title) Find(string build)
        {
            foreach (var c in commits.RootElement.EnumerateArray())
            {
                var message = c.GetProperty("commit").GetProperty("message").GetString()!;
                if (Regex.Matches(message, @"\d{4}\.\d{2}\.\d{2}\.\d{4}\.\d{4}").Any(m => m.Value == build))
                    return (c.GetProperty("sha").GetString()!, message.Split('\n')[0].Replace("data: ", ""));
            }
            throw new InvalidDataException($"CHS 仓库近期提交中没有构建 {build}，不能用邻近版本代替。可改用两份本地快照。");
        }
        var left = Find(Build(from)); var right = Find(Build(to));
        progress?.Report("正在比较两个版本的表文件索引…");
        return (await Create(left.Sha, left.Title, token), await Create(right.Sha, right.Title, token));
    }

    public static async Task<DataminingSource> Create(string commit, string label, CancellationToken token)
    {
        if (!Regex.IsMatch(commit, "^[0-9a-f]{40}$")) throw new ArgumentException("无效提交 ID。");
        using var root = await Json("root-" + commit + ".json", "git/trees/" + commit, true, token);
        var chs = root.RootElement.GetProperty("tree").EnumerateArray().Single(x => x.GetProperty("path").GetString() == "chs").GetProperty("sha").GetString();
        using var tree = await Json("tree-" + chs + ".json", "git/trees/" + chs + "?recursive=1", true, token);
        if (tree.RootElement.GetProperty("truncated").GetBoolean()) throw new InvalidDataException("GitHub 返回了不完整的表目录，已停止比较。");
        var sheets = tree.RootElement.GetProperty("tree").EnumerateArray().Where(x => x.GetProperty("type").GetString() == "blob" && x.GetProperty("path").GetString()!.EndsWith(".csv"))
            .Select(x => new SheetEntry(x.GetProperty("path").GetString()![..^4], x.GetProperty("sha").GetString()!)).ToDictionary(x => x.Name, StringComparer.Ordinal);
        return new DataminingSource(commit, $"{label} · {commit[..7]}", sheets);
    }

    public async Task<SheetData?> Read(string name, CancellationToken token)
    {
        if (!Sheets.ContainsKey(name)) return null;
        var target = Workspace.Within(Path.Combine(Cache, commit), name + ".csv");
        if (!File.Exists(target))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var uri = $"https://raw.githubusercontent.com/{Repository}/{commit}/chs/" + string.Join("/", name.Split('/').Select(Uri.EscapeDataString)) + ".csv";
            using var response = await Workspace.Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            var partial = target + "." + Guid.NewGuid().ToString("N") + ".partial";
            try
            {
                await using (var output = File.Create(partial))
                {
                    await using var input = await response.Content.ReadAsStreamAsync(token);
                    await input.CopyToAsync(output, token);
                }
                File.Move(partial, target, true);
            }
            finally { if (File.Exists(partial)) File.Delete(partial); }
        }
        // Pin downloaded and cached contents to the tree's Git blob hash.
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1))
        {
            await using var stream = File.OpenRead(target);
            hash.AppendData(Encoding.UTF8.GetBytes($"blob {stream.Length}\0"));
            var buffer = new byte[131072];
            while (true)
            {
                var count = await stream.ReadAsync(buffer, token);
                if (count == 0) break;
                hash.AppendData(buffer, 0, count);
            }
            if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(Sheets[name].Fingerprint, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("CSV 与固定版本的 Git blob 哈希不符，请移除对应 tmp/ui/datamining 缓存后重试。");
        }
        return await Task.Run(() => CsvSheetReader.Read(target, token), token);
    }

    private static async Task<JsonDocument> Json(string key, string path, bool immutable, CancellationToken token)
    {
        Directory.CreateDirectory(Cache);
        var target = Path.Combine(Cache, key);
        if (immutable && File.Exists(target)) return JsonDocument.Parse(await File.ReadAllTextAsync(target, token));
        string text;
        try
        {
            using var response = await Workspace.Http.GetAsync($"https://api.github.com/repos/{Repository}/{path}", token);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"GitHub 请求失败 ({(int)response.StatusCode})。可能达到访问频率上限，请稍后重试。");
            text = await response.Content.ReadAsStringAsync(token);
        }
        catch (HttpRequestException) when (File.Exists(target))
        { text = await File.ReadAllTextAsync(target, token); }
        var document = JsonDocument.Parse(text);
        await File.WriteAllTextAsync(target, text, token);
        return document;
    }
}
