using Lumina;

namespace FFXIVPatchParser;

public sealed partial class LocalGameSource
{
    // 只读取索引和已知路径；范围末端包含索引条目之间的对齐填充。
    internal IReadOnlyList<SqPackModelLocation> ListKnownMediaLocations(string kind,
        Dictionary<string, IReadOnlyList<VcdiffRanges.Range>> ranges, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (ranges.Count == 0) return [];
        lock (gate)
        {
            var locations = new List<(string Container, long Offset, long End, ulong Hash)>();
            foreach (var repository in game.Repositories.Values)
            foreach (var categories in repository.Categories.Values)
            foreach (var category in categories)
            {
                token.ThrowIfCancellationRequested();
                var packs = new Dictionary<byte, (string Container, long Length)>();
                for (byte dat = 0; dat < 8; dat++)
                {
                    if (category.GetDat(dat) is not { } pack) continue;
                    var container = Path.GetRelativePath(SqPackPath, pack.FullName).Replace('\\', '/');
                    if (ranges.ContainsKey(container)) packs.Add(dat, (container, new FileInfo(pack.FullName).Length));
                }
                if (packs.Count == 0) continue;
                var offsets = packs.Keys.ToDictionary(dat => dat, _ => new Dictionary<long, List<ulong>>());
                foreach (var hash in category.IndexHashTableEntries?.Keys.AsEnumerable() ?? [])
                {
                    token.ThrowIfCancellationRequested();
                    if (category.TryGetFileDatOffset(hash, out var dat, out var offset) && offsets.TryGetValue(dat, out var entries))
                        {
                        if (!entries.TryGetValue(offset, out var aliases)) entries.Add(offset, aliases = []);
                        aliases.Add(hash);
                    }
                }
                foreach (var (dat, entries) in offsets)
                {
                    var (container, length) = packs[dat];
                    var ordered = entries.OrderBy(entry => entry.Key).ToArray();
                    for (var i = 0; i < ordered.Length; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        var (offset, aliases) = ordered[i];
                        var end = i + 1 < ordered.Length ? ordered[i + 1].Key : length;
                        if (offset < 0 || end > length || end <= offset) throw new InvalidDataException("SqPack 索引偏移越界。");
                        if (PatchMediaScanner.Overlaps(ranges, container, offset, end))
                            foreach (var hash in aliases) locations.Add((container, offset, end, hash));
                    }
                }
            }
            if (locations.Count == 0) return [];
            var hashes = locations.Select(item => item.Hash).ToHashSet();
            var names = kind switch
            {
                "image" => ListIcons(token).GroupBy(path => GameData.ParseFilePath(path)!.IndexHash)
                    .Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single()),
                "audio" => GetMusicNames(token),
                _ => BuildKnownModelNames(token, hashes)
            };
            var result = new List<SqPackModelLocation>();
            foreach (var item in locations)
            {
                token.ThrowIfCancellationRequested();
                string? path = null;
                var ambiguous = false;
                if (kind == "model") path = ResolveCatalogModelPath(item.Container, item.Hash, token, out ambiguous);
                if (path is null && !ambiguous) path = names.GetValueOrDefault(item.Hash);
                if (path is null) continue;
                // 哈希只在所属仓库和类别内有意义，防止跨容器误认路径。
                var parsed = GameData.ParseFilePath(path)!;
                var split = item.Container.IndexOf('/');
                var categoryName = item.Container.Substring(split + 1, 2).ToLowerInvariant() switch
                {
                    "00" => "common", "01" => "bgcommon", "02" => "bg", "03" => "cut", "04" => "chara",
                    "06" => "ui", "07" => "sound", "08" => "vfx", "0c" => "music", _ => ""
                };
                if (!parsed.Repository.Equals(item.Container[..split], StringComparison.OrdinalIgnoreCase) ||
                    !parsed.Category.Equals(categoryName, StringComparison.OrdinalIgnoreCase)) continue;
                if (!game.Repositories.TryGetValue(parsed.Repository, out var repository) ||
                    !repository.Categories.TryGetValue(Convert.ToByte(item.Container.Substring(split + 1, 2), 16), out var categories)) continue;
                var matched = false;
                var conflicting = false;
                foreach (var category in categories)
                {
                    if (!category.TryGetFileDatOffset(parsed.IndexHash, out var dat, out var offset) || category.GetDat(dat) is not { } pack) continue;
                    var actualContainer = Path.GetRelativePath(SqPackPath, pack.FullName).Replace('\\', '/');
                    if (offset == item.Offset && actualContainer.Equals(item.Container, StringComparison.OrdinalIgnoreCase)) matched = true;
                    else conflicting = true;
                }
                // 同一逻辑路径在多个分片中指向不同地址时，保留给手动地址扫描。
                if (matched && !conflicting) result.Add(new(path, item.Container, item.Offset, item.End));
            }
            return result.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }
}
