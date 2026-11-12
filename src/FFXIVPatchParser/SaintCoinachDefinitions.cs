using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace FFXIVPatchParser;

public sealed record FieldDefinition(int Index, string Name, JsonElement? Converter);
public sealed record SheetDefinition(string Sheet, string? DefaultColumn, IReadOnlyDictionary<int, FieldDefinition> Fields);

/// <summary>Definitions are optional annotations, never a replacement for raw schema or cell identity.</summary>
public sealed class SaintCoinachDefinitions
{
    public static string DefaultDirectory => Path.Combine(Workspace.Temp, "definitions", "saintcoinach");
    private static string UpstreamDirectory => Path.Combine(Workspace.Temp, "definitions", "saintcoinach-upstream");
    public string DirectoryPath { get; }
    private const string UpstreamRevision = "0b22b78f97951bb66bfa78fb561aa4c7bf7af7cd";
    private readonly bool useUpstream;
    public SaintCoinachDefinitions(string? directory = null)
    {
        DirectoryPath = Path.GetFullPath(directory ?? DefaultDirectory);
        useUpstream = directory == null;
    }
    private static void CheckName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            throw new InvalidDataException("定义表名必须仅包含字母、数字、下划线。");
    }
    public SheetDefinition? Get(string sheet)
    {
        if (string.IsNullOrWhiteSpace(sheet) || sheet.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')) return null;
        CheckName(sheet);
        var path = FindFile(sheet);
        if (path == null) return null;
        try { var definition = Parse(File.ReadAllText(path)); return definition.Sheet == sheet ? definition : null; }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException) { return null; }
    }
    private string? FindFile(string sheet)
    {
        var path = Path.Combine(DirectoryPath, sheet + ".json");
        if (File.Exists(path)) return path;
        if (!useUpstream) return null;
        path = Path.Combine(UpstreamDirectory, sheet + ".json");
        return File.Exists(path) ? path : null;
    }
    public static SheetDefinition Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        var root = doc.RootElement;
        var fields = new Dictionary<int, FieldDefinition>();
        foreach (var entry in root.GetProperty("definitions").EnumerateArray())
        {
            var start = entry.TryGetProperty("index", out var index) ? index.GetInt32() : 0;
            foreach (var field in Flatten(entry))
            {
                if (start < 0 || start > 65535 || !fields.TryAdd(start, field with { Index = start }))
                    throw new InvalidDataException("定义列重复或越界。");
                start++;
            }
        }
        return new(root.GetProperty("sheet").GetString()!, root.TryGetProperty("defaultColumn", out var def) ? def.GetString() : null, fields);
    }
    private static IEnumerable<FieldDefinition> Flatten(JsonElement node, int depth = 0)
    {
        if (depth > 16) throw new InvalidDataException("定义嵌套过深。");
        var type = node.TryGetProperty("type", out var t) ? t.GetString() : "single";
        if (type == "group")
        {
            foreach (var member in node.GetProperty("members").EnumerateArray())
                foreach (var field in Flatten(member, depth + 1)) yield return field;
        }
        else if (type == "repeat")
        {
            var count = node.GetProperty("count").GetInt32();
            if (count < 0 || count > 65536) throw new InvalidDataException("重复定义数量越界。");
            var inner = Flatten(node.GetProperty("definition"), depth + 1).ToArray();
            if ((long)count * inner.Length > 65536) throw new InvalidDataException("重复定义过大。");
            for (var i = 0; i < count; i++) foreach (var field in inner) yield return field with { Name = field.Name + $"[{i}]" };
        }
        else if (type is "single" or null)
            yield return new(0, node.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "", node.TryGetProperty("converter", out var converter) ? converter.Clone() : null);
        else throw new InvalidDataException("不支持的定义类型：" + type);
    }
    public void ImportDirectory(string path)
    {
        Directory.CreateDirectory(DirectoryPath);
        foreach (var file in Directory.EnumerateFiles(path, "*.json"))
        {
            if (file.EndsWith(".binding.json", StringComparison.OrdinalIgnoreCase)) continue;
            var json = File.ReadAllText(file);
            var def = Parse(json);
            CheckName(def.Sheet);
            var target = Path.Combine(DirectoryPath, def.Sheet + ".json");
            var partial = target + "." + Guid.NewGuid().ToString("N") + ".partial";
            try { File.WriteAllText(partial, json); File.Move(partial, target, true); }
            finally { if (File.Exists(partial)) File.Delete(partial); }
        }
    }
    public async Task DownloadAsync(IEnumerable<string> sheets, CancellationToken token = default)
    {
        var revisionFile = Path.Combine(UpstreamDirectory, "revision.txt");
        if (useUpstream && File.Exists(revisionFile) &&
            (await File.ReadAllTextAsync(revisionFile, token)).Trim() == UpstreamRevision) return;
        Directory.CreateDirectory(DirectoryPath);
        foreach (var sheet in sheets.Distinct())
        {
            if (string.IsNullOrWhiteSpace(sheet) || sheet.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')) continue;
            CheckName(sheet);
            var path = Path.Combine(DirectoryPath, sheet + ".json");
            if (FindFile(sheet) != null) continue;
            using var response = await Workspace.Http.GetAsync($"https://raw.githubusercontent.com/xivapi/SaintCoinach/{UpstreamRevision}/SaintCoinach/Definitions/{sheet}.json", token);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) continue;
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(token);
            if (Parse(json).Sheet != sheet) throw new InvalidDataException("定义表名不匹配。");
            var partial = path + "." + Guid.NewGuid().ToString("N") + ".partial";
            try
            {
                await File.WriteAllTextAsync(partial, json, token);
                File.Move(partial, path, true);
            }
            finally { if (File.Exists(partial)) File.Delete(partial); }
        }
    }
    // A raw schema has no semantic names. Only an explicitly verified source/schema binding may interpret it.
    public void BindVerifiedSchema(string sheet, string sourceLabel, IReadOnlyList<SheetColumn> columns)
    {
        CheckName(sheet);
        var path = Path.Combine(DirectoryPath, sheet + ".json");
        File.WriteAllText(Path.Combine(DirectoryPath, sheet + ".binding.json"), JsonSerializer.Serialize(new Binding(sourceLabel, SchemaHash(columns), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))));
    }
    internal bool IsBound(string sheet, string sourceLabel, IReadOnlyList<SheetColumn> columns)
    {
        var path = Path.Combine(DirectoryPath, sheet + ".binding.json");
        if (!File.Exists(path)) return false;
        var value = JsonSerializer.Deserialize<Binding>(File.ReadAllText(path));
        return value == new Binding(sourceLabel, SchemaHash(columns), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(DirectoryPath, sheet + ".json")))));
    }
    private static string SchemaHash(IReadOnlyList<SheetColumn> columns) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(columns))));
    private sealed record Binding(string SourceLabel, string SchemaHash, string DefinitionHash);
}
