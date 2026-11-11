using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace FFXIVPatchParser;

public sealed record PatchResource(string Path, string Category, string Repository, string Entry,
    long DeltaBytes, long CompressedBytes, string OriginMd5, string ResultMd5, string DeltaMd5, bool Available)
{
    public string SizeLabel => Available ? Workspace.Size(DeltaBytes) : "缺少分卷";
    public string Status => Available ? "已定位" : "缺少分卷";
}
public sealed record LoadedPatch(string Name, string? From, string? To, IReadOnlyList<PatchResource> Resources, int ArchiveCount);

public static class PatchArchive
{
    private static readonly string[] Categories = ["common", "bgcommon", "bg", "cut", "chara", "shader", "ui", "sound", "vfx", "ui_script", "exd", "game_script", "music"];
    public static LoadedPatch Load(IEnumerable<string> paths)
    {
        var files = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0) throw new ArgumentException("请选择补丁 ZIP。");
        var entries = new Dictionary<string, (long Size, long Compressed)>(StringComparer.OrdinalIgnoreCase);
        var maps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var origins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var deltas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var versions = new HashSet<string>();
        string? from = null, to = null;
        foreach (var file in files)
        {
            var match = Regex.Match(Path.GetFileName(file), @"_(\d+(?:\.\d+){3})-(\d+(?:\.\d+){3})(?:\.|_|$)");
            if (match.Success) { from = match.Groups[1].Value; to = match.Groups[2].Value; versions.Add(from + "-" + to); }
            using var zip = ZipFile.OpenRead(file);
            foreach (var entry in zip.Entries)
            {
                var name = Normalize(entry.FullName);
                if (name.EndsWith(".delta", StringComparison.OrdinalIgnoreCase) && !entries.TryAdd(name, (entry.Length, entry.CompressedLength)))
                    throw new InvalidDataException($"重复差分条目：{name}");
            }
            var manifest = zip.GetEntry("patch_delta_direct.dat");
            if (manifest == null) continue; // A multipart package may have its manifest in another ZIP.
            using var stream = manifest.Open();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 });
            var document = XDocument.Load(reader);
            Add(document, "DeltaPathSubItem", maps, true);
            Add(document, "OriginMD5SubItem", origins, false);
            Add(document, "ResultMD5SubItem", results, false);
            Add(document, "DeltaMD5SubItem", deltas, false);
        }
        if (versions.Count > 1) throw new InvalidDataException("请只选择同一次版本更新的 ZIP 分卷。");
        if (maps.Count == 0) throw new InvalidDataException("未找到国服 V3 的 patch_delta_direct.dat，请选择包含清单的分卷。");
        var resources = maps.OrderBy(x => x.Key, StringComparer.Ordinal).Select(item =>
        {
            var category = "other";
            var repository = "—";
            var parts = item.Key.Split('/');
            if (parts.Length >= 4 && parts[0] == "game" && parts[1] == "sqpack" && parts[3].Length >= 6)
            {
                repository = parts[2];
                if (byte.TryParse(parts[3][..2], System.Globalization.NumberStyles.HexNumber, null, out var id))
                    category = id < Categories.Length ? Categories[id] : $"0x{id:x2}";
            }
            var exists = entries.TryGetValue(item.Value, out var data);
            return new PatchResource(item.Key, category, repository, item.Value, data.Size, data.Compressed,
                origins.GetValueOrDefault(item.Key, ""), results.GetValueOrDefault(item.Key, ""), deltas.GetValueOrDefault(item.Key + ".delta", ""), exists);
        }).ToArray();
        return new LoadedPatch(files.Length == 1 ? Path.GetFileName(files[0]) : $"{from} → {to} · {files.Length} 个分卷", from, to, resources, files.Length);
    }
    private static void Add(XDocument xml, string element, Dictionary<string, string> map, bool normalizeValue)
    {
        foreach (var node in xml.Descendants(element))
        {
            var key = Normalize((string?)node.Attribute("Key") ?? "");
            var value = (string?)node.Attribute("Value") ?? "";
            if (key.Length == 0 || value.Length == 0) throw new InvalidDataException("清单中包含空路径或校验值。");
            if (normalizeValue) value = Normalize(value);
            if (map.TryGetValue(key, out var previous) && previous != value) throw new InvalidDataException($"清单存在冲突：{key}");
            map[key] = value;
        }
    }
    private static string Normalize(string path)
    {
        var value = path.Replace('\\', '/');
        if (value.StartsWith('/') || value.Contains(':') || value.Split('/').Any(p => p is "." or ".."))
            throw new InvalidDataException("补丁中包含不合法的路径。");
        return value;
    }
}
