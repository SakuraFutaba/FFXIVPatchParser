using Lumina;

namespace FFXIVPatchParser;

public sealed partial class LocalGameSource
{
    private Dictionary<(string Repository, string Category, ulong Hash), string[]>? catalogModelPaths;
    private IReadOnlyList<NamedModelEntry>? catalogNamedModels;
    private static string ModelPathCatalog => Path.Combine(Workspace.Temp, "model-paths", "catalog.txt");

    /// <summary>导入每行一个游戏内路径的 UTF-8 文本；不复制游戏资源。</summary>
    public int ImportModelPaths(string file, CancellationToken token = default)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(file))
        {
            token.ThrowIfCancellationRequested();
            var path = NormalizeModelPath(line);
            if (path is not null) paths.Add(path);
        }
        if (paths.Count == 0) throw new InvalidDataException("列表中没有有效的游戏内 .mdl 路径。请使用每行一个路径的 UTF-8 文本。");
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            if (File.Exists(ModelPathCatalog))
                foreach (var line in File.ReadLines(ModelPathCatalog))
                {
                    token.ThrowIfCancellationRequested();
                    if (NormalizeModelPath(line) is { } path) paths.Add(path);
                }
            Directory.CreateDirectory(Path.GetDirectoryName(ModelPathCatalog)!);
            var staging = ModelPathCatalog + "." + Guid.NewGuid().ToString("N") + ".partial";
            try
            {
                File.WriteAllLines(staging, paths.Order(StringComparer.Ordinal));
                token.ThrowIfCancellationRequested();
                File.Move(staging, ModelPathCatalog, true);
            }
            finally { if (File.Exists(staging)) File.Delete(staging); }
            catalogModelPaths = null;
            modelLocations.Clear();
            return paths.Count;
        }
    }

    private static string? NormalizeModelPath(string line)
    {
        var path = line.Trim().Replace('\\', '/').ToLowerInvariant();
        if (path.Length > 1024 || !path.EndsWith(".mdl", StringComparison.Ordinal) ||
            !path.Contains('/') || path.StartsWith('/') || path.Contains(':') ||
            path.Split('/').Any(part => part is "" or "." or "..")) return null;
        return GameData.ParseFilePath(path) is null ? null : path;
    }

    private string? ResolveCatalogModelPath(string container, ulong hash, CancellationToken token, out bool ambiguous)
    {
        if (catalogModelPaths is null || !ReferenceEquals(catalogNamedModels, namedModels))
        {
            var paths = new List<string>();
            if (File.Exists(ModelPathCatalog))
                foreach (var line in File.ReadLines(ModelPathCatalog))
                {
                    token.ThrowIfCancellationRequested();
                    if (NormalizeModelPath(line) is { } path) paths.Add(path);
                }
            catalogNamedModels = namedModels;
            if (namedModels is not null) paths.AddRange(namedModels.Select(entry => entry.Path));
            catalogModelPaths = paths.Distinct(StringComparer.Ordinal).GroupBy(path =>
            {
                var parsed = GameData.ParseFilePath(path)!;
                return (parsed.Repository, parsed.Category, parsed.IndexHash);
            }).ToDictionary(group => group.Key, group => group.ToArray());
        }
        var split = container.IndexOf('/');
        var category = container.Substring(split + 1, 2) switch
        {
            "00" => "common", "01" => "bgcommon", "02" => "bg", "03" => "cut", "04" => "chara",
            "05" => "shader", "06" => "ui", "07" => "sound", "08" => "vfx", "09" => "ui_script",
            "0a" => "exd", "0b" => "game_script", "0c" => "music", _ => ""
        };
        var key = (container[..split], category, hash);
        var candidates = catalogModelPaths.GetValueOrDefault(key) ?? [];
        ambiguous = candidates.Length > 1;
        // 同一索引哈希对应多个已知路径时保留地址，避免把碰撞解释成确定名称。
        if (candidates.Length == 1 && game.FileExists(candidates[0])) return candidates[0];
        return null;
    }
}
