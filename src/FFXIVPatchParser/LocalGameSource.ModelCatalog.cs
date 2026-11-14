using Lumina;
using Lumina.Data;
using Lumina.Data.Structs;

namespace FFXIVPatchParser;

public sealed record SqPackModelLocation(string Path, string Container, long Offset, long End);

public sealed partial class LocalGameSource
{
    private static readonly string[] EquipmentSlots = ["met", "top", "glv", "dwn", "sho"];
    private static readonly (string Part, char Prefix, string Suffix, int Count)[] HumanModelParts =
    [
        ("hair", 'h', "hir", 500), ("face", 'f', "fac", 300), ("body", 'b', "top", 200),
        ("tail", 't', "til", 100), ("zear", 'z', "zer", 100)
    ];
    private readonly Dictionary<string, IReadOnlyList<SqPackModelLocation>> modelLocations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (SqPack Pack, long Offset)> modelAddresses = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<ulong, string>? knownModelNames;

    public IReadOnlyList<SqPackModelLocation> ListModelLocations(CancellationToken token = default,
        IReadOnlySet<string>? containers = null, IProgress<string>? progress = null, bool charactersOnly = false,
        Func<string, long, long, bool>? includeRange = null)
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(catalogNamedModels, namedModels)) modelLocations.Clear();
            var result = new List<SqPackModelLocation>();
            var requested = containers?.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var repository in game.Repositories.Values)
            foreach (var (categoryId, categories) in repository.Categories.OrderBy(pair => pair.Key == 4 ? -1 : pair.Key))
            foreach (var category in categories)
            {
                token.ThrowIfCancellationRequested();
                if (charactersOnly && categoryId != 4) continue;
                var packs = new Dictionary<byte, (SqPack Pack, string Container)>();
                for (byte number = 0; number < 8; number++)
                {
                    if (category.GetDat(number) is not { } pack) continue;
                    var container = Path.GetRelativePath(SqPackPath, pack.FullName).Replace('\\', '/');
                    if (requested is not null && !requested.Contains(container)) continue;
                    if (modelLocations.TryGetValue(container, out var cached)) result.AddRange(includeRange is null ? cached : cached.Where(item => includeRange(item.Container, item.Offset, item.End)));
                    else packs.Add(number, (pack, container));
                }

                if (packs.Count == 0) continue;
                var addresses = packs.Keys.ToDictionary(number => number, _ => new Dictionary<long, ulong>());
                foreach (var hash in category.IndexHashTableEntries?.Keys.AsEnumerable() ?? [])
                {
                    token.ThrowIfCancellationRequested();
                    if (category.TryGetFileDatOffset(hash, out var number, out var offset) && addresses.TryGetValue(number, out var indexed))
                        indexed.TryAdd(offset, hash);
                }

                foreach (var (number, (pack, container)) in packs)
                {
                    progress?.Report("正在扫描模型容器 · " + container);
                    var found = ScanModelContainer(pack, container, addresses[number], categoryId == 4, token, includeRange);
                    if (includeRange is null) modelLocations.Add(container, found);
                    result.AddRange(found);
                }
            }
            return result;
        }
    }

    private List<SqPackModelLocation> ScanModelContainer(SqPack pack, string container, Dictionary<long, ulong> addresses,
        bool characterContainer, CancellationToken token, Func<string, long, long, bool>? includeRange)
    {
        using var stream = File.OpenRead(pack.FullName);
        using var reader = new BinaryReader(stream);
        var indexed = addresses.OrderBy(address => address.Key).ToArray();
        var result = new List<SqPackModelLocation>();
        for (var i = 0; i < indexed.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var (offset, hash) = indexed[i];
            if (offset < 0 || offset > stream.Length - 24) throw new InvalidDataException("SqPack 索引偏移越界。");
            var end = i + 1 < indexed.Length ? indexed[i + 1].Key : stream.Length;
            if (includeRange is not null && !includeRange(container, offset, end)) continue;
            stream.Position = offset + 4;
            if (reader.ReadUInt32() != (uint)FileType.Model) continue;
            var addressPath = $"sqpack:{container}@{offset:X}#{hash:X16}";
            var path = ResolveCatalogModelPath(container, hash, token, out var ambiguous);
            if (path is null && characterContainer && !ambiguous)
            {
                knownModelNames ??= BuildKnownModelNames(token);
                path = knownModelNames.GetValueOrDefault(hash);
            }
            path ??= addressPath;
            modelAddresses[addressPath] = (pack, offset);
            modelAddresses[path] = (pack, offset);
            result.Add(new(path, container, offset, end));
        }
        return result;
    }

    // 索引不保存原始路径。只收录当前安装版本中存在的候选路径。
    private Dictionary<ulong, string> BuildKnownModelNames(CancellationToken token, HashSet<ulong>? targetHashes = null)
    {
        var names = new Dictionary<ulong, string>();
        var hashes = game.Repositories.Values.SelectMany(r => r.Categories)
            .Where(pair => pair.Key == 4).SelectMany(pair => pair.Value)
            .SelectMany(category => category.IndexHashTableEntries?.Keys.AsEnumerable() ?? []).ToHashSet();
        if (targetHashes is not null) hashes.IntersectWith(targetHashes);
        var folders = hashes.Select(hash => hash >> 32).ToHashSet();
        void AddIfPresent(string path)
        {
            if (GameData.ParseFilePath(path) is { } parsed && hashes.Contains(parsed.IndexHash) && game.FileExists(path))
                names.TryAdd(parsed.IndexHash, path);
        }

        // 使用已核对的 ModelChara 布局补全怪物、亚人和武器路径；未知版本继续使用索引地址。
        try
        {
            var (header, language) = ReadSheetHeader("ModelChara");
            var signature = string.Join(";", header.ColumnDefinitions.Select((column, i) => $"{i}:{column.Type}:{column.Offset}"));
            if (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(signature))) == NamedModelSchemas["ModelChara"])
                foreach (var row in game.Excel.GetSheet<Lumina.Excel.RawRow>(language, "ModelChara"))
                {
                    token.ThrowIfCancellationRequested();
                    var type = Convert.ToInt32(row.ReadColumn(0));
                    var model = Convert.ToInt32(row.ReadColumn(1));
                    var body = Convert.ToInt32(row.ReadColumn(2));
                    if (type == 3) AddIfPresent($"chara/monster/m{model:D4}/obj/body/b{body:D4}/model/m{model:D4}b{body:D4}.mdl");
                    else if (type == 4) AddIfPresent($"chara/weapon/w{model:D4}/obj/body/b{body:D4}/model/w{model:D4}b{body:D4}.mdl");
                    else if (type == 2)
                        foreach (var slot in EquipmentSlots)
                            AddIfPresent($"chara/demihuman/d{model:D4}/obj/equipment/e{body:D4}/model/d{model:D4}e{body:D4}_{slot}.mdl");
                }
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException) { }

        var equipmentFolders = new List<(string Model, string Folder)>();
        for (var number = 1; number <= 9999; number++)
        {
            token.ThrowIfCancellationRequested();
            var model = $"e{number:D4}";
            var folder = $"chara/equipment/{model}/model";
            if (folders.Contains(GameData.ParseFilePath(folder + "/probe.mdl")!.IndexHash >> 32))
                equipmentFolders.Add((model, folder));
        }

        for (var race = 1; race <= 18; race++)
        {
            var character = $"c{race * 100 + 1:D4}";
            foreach (var (part, prefix, suffix, count) in HumanModelParts)
            for (var number = 1; number <= count; number++)
            {
                token.ThrowIfCancellationRequested();
                var model = $"{prefix}{number:D4}";
                AddIfPresent($"chara/human/{character}/obj/{part}/{model}/model/{character}{model}_{suffix}.mdl");
            }

            foreach (var (model, folder) in equipmentFolders)
            {
                token.ThrowIfCancellationRequested();
                foreach (var slot in EquipmentSlots)
                    AddIfPresent($"{folder}/{character}{model}_{slot}.mdl");
            }
        }
        return names;
    }

    private FileResource ReadModelFile(string path, CancellationToken token)
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            if (path.StartsWith("sqpack:", StringComparison.OrdinalIgnoreCase))
            {
                if (modelAddresses.TryGetValue(path, out var address)) return address.Pack.ReadFile<FileResource>(address.Offset);
                throw new FileNotFoundException("未找到模型索引地址，请先扫描模型列表：" + path);
            }
            if (!path.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase) || !path.Contains('/'))
                throw new ArgumentException("请输入 SqPack 内的 .mdl 路径。", nameof(path));
            return game.GetFile(path) ?? throw new FileNotFoundException("未找到模型：" + path);
        }
    }
}
