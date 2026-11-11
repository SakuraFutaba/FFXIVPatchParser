using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FFXIVPatchParser;

public sealed class CatalogFile
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public long Size { get; set; }
    public string Md5 { get; set; } = "";
    public bool? Available { get; set; }
    public bool Verified { get; set; }
}
public sealed class CatalogPackage
{
    public string Name { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string VersionView { get; set; } = "";
    public bool IsLatest { get; set; }
    public string? Error { get; set; }
    public List<CatalogFile> Files { get; set; } = [];
    public string SizeLabel => Workspace.Size(Files.Sum(f => f.Size));
    public string AccessLabel => Error != null ? "查询失败" : Files.Count == 0 ? "空清单" : Files.All(f => f.Available == true) ? "可下载" : Files.Any(f => f.Available == false) ? "部分不可用" : "未探测";
    public string Badge => IsLatest ? "最新" : "";
}
public sealed class CatalogDocument
{
    public DateTimeOffset CheckedAt { get; set; }
    public string Target { get; set; } = "";
    public List<CatalogPackage> Packages { get; set; } = [];
}
public static class PatchCatalog
{
    public const string VersionUrl = "https://v3launcher.jijiagames.com/v3launcher/build/ver2data/100001900/8847/-1/ver2.dat";
    private static string Root => Path.Combine(Workspace.Temp, "patches");
    public static CatalogDocument? Cached() => File.Exists(Path.Combine(Root, "catalog.json"))
        ? JsonSerializer.Deserialize<CatalogDocument>(File.ReadAllText(Path.Combine(Root, "catalog.json")), Workspace.Json) : null;
    private static Uri Sign(string url)
    {
        var uri = new Uri(url);
        if (uri.Scheme != "https" || !uri.Host.EndsWith(".jijiagames.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("下载地址不属于预期的官方 HTTPS CDN。");
        var stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString("x");
        // Public launcher CDN signing constant, not a personal credential:
        // AtmoOmen/FFXIVQuickLauncher @ 65972f758b47615f321d4f3da509e3ade3b14489
        // src/XIVLauncher.GamePatchV3/CDNLinkSigner.cs
        var key = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes("EKUWRI5KXXAIDlQ0mBNLa7XkjU1JNFuL" + uri.AbsolutePath + stamp))).ToLowerInvariant();
        return new Uri($"{uri.Scheme}://{uri.Authority}/{key}/{stamp}{uri.AbsolutePath}");
    }
    private static string Combine(string baseUrl, string path) =>
        Uri.TryCreate(path, UriKind.Absolute, out var absolute) && absolute.Scheme == "https" ? absolute.AbsoluteUri : baseUrl.TrimEnd('/') + "/" + path.TrimStart('/');
    public static async Task<CatalogDocument> Refresh(IProgress<string>? progress, CancellationToken token)
    {
        var text = await Workspace.Http.GetStringAsync(VersionUrl, token);
        Directory.CreateDirectory(Path.Combine(Root, "metadata"));
        await File.WriteAllTextAsync(Path.Combine(Root, "metadata", "ver2.dat"), text, token);
        using var versions = JsonDocument.Parse(text);
        var area = versions.RootElement.GetProperty("areas").EnumerateArray().First(x => x.GetProperty("id").GetString() == "0");
        var result = new CatalogDocument { Target = area.GetProperty("must").GetString()!, CheckedAt = DateTimeOffset.Now };
        var baseUrl = versions.RootElement.GetProperty("baseUrl").GetString()!;
        foreach (var value in versions.RootElement.GetProperty("packages").EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            var package = new CatalogPackage { Name = value.GetProperty("name").GetString()!, From = value.GetProperty("from").GetString()!, To = value.GetProperty("to").GetString()!, VersionView = value.GetProperty("versionView").GetString()! };
            result.Packages.Add(package); package.IsLatest = package.To == result.Target;
            progress?.Report($"正在查询 {package.From} → {package.To}…");
            try
            {
                using var list = JsonDocument.Parse(await Workspace.Http.GetStringAsync(Sign(Combine(baseUrl, value.GetProperty("fileListUrl").GetString()!)), token));
                var fileBase = list.RootElement.TryGetProperty("baseUrl", out var b) && !string.IsNullOrEmpty(b.GetString()) ? b.GetString()! : baseUrl;
                foreach (var entry in list.RootElement.GetProperty("fileList").EnumerateArray())
                {
                    var file = new CatalogFile { Name = Path.GetFileName(entry.GetProperty("path").GetString()!.Replace('\\', '/')), Url = Combine(fileBase, entry.GetProperty("url").GetString()!), Size = long.Parse(entry.GetProperty("size").ToString()), Md5 = entry.GetProperty("md5").GetString()! };
                    package.Files.Add(file);
                    using var request = new HttpRequestMessage(HttpMethod.Get, Sign(file.Url));
                    request.Headers.Range = new RangeHeaderValue(0, 0);
                    using var response = await Workspace.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                    file.Available = response.IsSuccessStatusCode && (response.Content.Headers.ContentRange?.Length == null || response.Content.Headers.ContentRange.Length == file.Size);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException) { package.Error = ex.Message; }
        }
        await Workspace.SaveJson(Path.Combine(Root, "catalog.json"), result, token);
        return result;
    }
    public static async Task<string[]> Download(CatalogPackage package, IProgress<string>? progress, CancellationToken token)
    {
        if (!Regex.IsMatch(package.Name, @"^patch-[0-9.]+-[0-9.]+$") || package.Files.Count == 0) throw new InvalidDataException("补丁清单不可用。");
        var folder = Workspace.Within(Root, package.Name);
        Directory.CreateDirectory(folder);
        var paths = new List<string>();
        foreach (var file in package.Files)
        {
            if (Path.GetFileName(file.Name) != file.Name || !Regex.IsMatch(file.Md5, "^[0-9a-fA-F]{32}$") || file.Size < 0) throw new InvalidDataException("无效包文件信息。");
            var target = Workspace.Within(folder, file.Name);
            paths.Add(target);
            if (await Workspace.Matches(target, file.Size, file.Md5, token)) { file.Verified = true; continue; }
            using var response = await Workspace.Http.GetAsync(Sign(file.Url), HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            var partial = target + ".partial";
            await using (var output = File.Create(partial))
            {
                await using var input = await response.Content.ReadAsStreamAsync(token);
                var buffer = new byte[131072];
                long copied = 0; var last = Environment.TickCount64;
                while (true)
                {
                    var count = await input.ReadAsync(buffer, token);
                    if (count == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                    copied += count;
                    if (copied > file.Size) throw new InvalidDataException("下载内容超出清单大小。");
                    if (Environment.TickCount64 - last > 200) { progress?.Report($"下载 {file.Name} · {copied / 1048576d:N1} / {file.Size / 1048576d:N1} MiB"); last = Environment.TickCount64; }
                }
            }
            progress?.Report("正在校验 " + file.Name + "…");
            if (!await Workspace.Matches(partial, file.Size, file.Md5, token)) throw new InvalidDataException("下载的大小或 MD5 不匹配。");
            File.Move(partial, target, true); file.Verified = true;
        }
        return paths.ToArray();
    }
}
