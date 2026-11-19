using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using Lumina.Excel;
using Lumina.Text.ReadOnly;

namespace FFXIVPatchParser;

public sealed record GameAnimationSlot(int Index, string Name);
public sealed record GameAnimationMetadata(string Path, IReadOnlyList<GameAnimationSlot> Slots, int HavokDataBytes);

public sealed partial class LocalGameSource
{
    private readonly Dictionary<string, IReadOnlyList<string>> modelAnimationPaths = new(StringComparer.Ordinal);

    private ILookup<string, string>? indexedAnimations;
    private Dictionary<string, SqPackFileSystemEntry> indexedAnimationEntries = new(StringComparer.Ordinal);
    private int animationCatalogRevision = -1;

    private string? AnimationOwner(string modelPath)
    {
        if (Regex.IsMatch(modelPath, @"^character:c[0-9]{4}$", RegexOptions.CultureInvariant)) return "chara/human/" + modelPath[10..];
        var path = fileSystemModelContexts.GetValueOrDefault(modelPath, modelPath).Replace('\\', '/');
        var match = Regex.Match(path, @"^chara/(?:human/c|monster/m|demihuman/d|weapon/w)\d{4}(?=/)");
        if (match.Success) return match.Value;
        match = Regex.Match(path, @"^chara/(?:equipment|accessory)/[ea]\d{4}/model/(c\d{4})");
        return match.Success ? "chara/human/" + match.Groups[1].Value : null;
    }

    public string? FindDefaultModelAnimation(string modelPath, CancellationToken token = default, bool playerPose = false)
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            if (AnimationOwner(modelPath) is not { } owner) return null;
            if (playerPose && owner.StartsWith("chara/human/", StringComparison.Ordinal))
            {
                var pose = $"{owner}/animation/a0001/bt_common/emote/pose01_loop.pap";
                if (game.FileExists(pose)) return pose;
            }
            var file = owner.Split('/')[1] switch
            {
                "monster" => "monster.pap",
                "demihuman" => "demihuman.pap",
                "human" => "idle.pap",
                _ => null
            };
            if (file == null) return null;
            var path = $"{owner}/animation/a0001/bt_common/resident/{file}";
            if (game.FileExists(path)) return path;
            if (owner == "chara/human/c1201")
            {
                var lalafellIdle = $"{owner}/animation/a0129/bt_common/resident/idle.pap";
                return game.FileExists(lalafellIdle) ? lalafellIdle : null;
            }
            var shared = owner switch
            {
                "chara/human/c0204" or "chara/human/c0504" or "chara/human/c0604" or
                "chara/human/c0804" or "chara/human/c1304" or "chara/human/c1404" => "c0104",
                "chara/human/c0301" => "c1101",
                _ => null
            };
            if (shared == null) return null;
            var sharedPath = $"chara/human/{shared}/animation/a0001/bt_common/resident/idle.pap";
            return game.FileExists(sharedPath) ? sharedPath : null;
        }
    }

    public IReadOnlyList<string> ListModelAnimationPaths(string modelPath, CancellationToken token = default)
    {
        var owner = AnimationOwner(modelPath);
        if (owner == null) return [];
        var root = owner + "/animation/a0001/bt_common/";
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            if (animationCatalogRevision != MediaCatalogRevision)
            {
                var paths = new Dictionary<string, SqPackFileSystemEntry>(StringComparer.Ordinal);
                foreach (var container in ListFileSystemContainers(token).Where(c => c.Category == "chara"))
                foreach (var entry in EnumerateFileSystemEntries(container.Id, token))
                {
                    token.ThrowIfCancellationRequested();
                    if (entry.KnownPath is { } path && path.EndsWith(".pap", StringComparison.Ordinal) &&
                        path.Contains("/animation/", StringComparison.Ordinal)) paths.TryAdd(path, entry);
                }
                indexedAnimationEntries = paths;
                indexedAnimations = paths.Keys.ToLookup(path => path[..path.IndexOf("/animation/", StringComparison.Ordinal)], StringComparer.Ordinal);
                modelAnimationPaths.Clear();
                animationCatalogRevision = MediaCatalogRevision;
            }
            if (modelAnimationPaths.TryGetValue(root, out var cached)) return cached;
            var candidates = new HashSet<string>(indexedAnimations![owner], StringComparer.Ordinal);
            if (owner.StartsWith("chara/human/", StringComparison.Ordinal)) candidates.Add(root + "emote/pose01_loop.pap");
            // Monster idle/movement clips are slots in a shared resident bundle,
            // rather than separate idle.pap / walk.pap files (Godbert uses this too).
            if (owner.StartsWith("chara/monster/", StringComparison.Ordinal)) candidates.Add(root + "resident/monster.pap");
            void AddName(string name)
            {
                if (string.IsNullOrWhiteSpace(name) || name.Length > 256 || name.Contains("..", StringComparison.Ordinal)) return;
                var leaf = name[(name.LastIndexOf('/') + 1)..];
                foreach (var folder in new[] { "resident/", "nonresident/", "emote/" }) candidates.Add(root + folder + leaf + ".pap");
                candidates.Add(root + name + ".pap");
            }
            foreach (var name in new[] { "idle", "move", "walk", "run", "sprint", "jump", "turn", "sit", "battle", "damage", "death" }) AddName(name);
            if (SheetNames.Contains("ActionTimeline"))
            {
                var (header, language) = ReadSheetHeader("ActionTimeline");
                var stringColumns = header.ColumnDefinitions.Select((column, index) => (column, index))
                    .Where(item => item.column.Type.ToString() == "String").Select(item => item.index).ToArray();
                foreach (var row in game.Excel.GetSheet<RawRow>(language, "ActionTimeline"))
                {
                    token.ThrowIfCancellationRequested();
                    foreach (var column in stringColumns)
                        if (row.ReadColumn(column) is ReadOnlySeString value) AddName(value.ExtractText());
                }
            }
            var result = new List<string>();
            foreach (var path in candidates)
            {
                token.ThrowIfCancellationRequested();
                if (game.FileExists(path)) result.Add(path);
            }
            result.Sort(StringComparer.Ordinal);
            if (FindDefaultModelAnimation(modelPath, token) is { } idle)
            {
                result.Remove(idle);
                result.Insert(0, idle);
            }
            modelAnimationPaths.Add(root, result);
            return result;
        }
    }

    /// <summary>Reads the PAP slot table only. Does not decode Havok tracks, durations or poses.</summary>
    public GameAnimationMetadata ReadAnimationMetadata(string path, CancellationToken token = default)
    {
        path = path.Trim().Replace('\\', '/').TrimStart('/');
        if (!path.EndsWith(".pap", StringComparison.OrdinalIgnoreCase) || !path.Contains('/'))
            throw new ArgumentException("请输入 SqPack 内的 .pap 路径。", nameof(path));
        byte[] data;
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            data = (game.GetFile(path) ?? throw new FileNotFoundException("未找到动作资源：" + path)).Data;
        }
        return ParseAnimationMetadata(data, path, token);
    }

    public (GameAnimationMetadata Metadata, GameAnimationDescription Description) ReadFileSystemAnimation(
        SqPackFileSystemEntry entry, SqPackFileSystemPreview page, CancellationToken token = default)
    {
        var metadata = ReadFileSystemAnimationMetadata(entry, page, token);
        return (metadata, DescribeAnimation(metadata.Path, metadata.Slots, token));
    }

    public GameAnimationMetadata ReadFileSystemAnimationMetadata(
        SqPackFileSystemEntry entry, SqPackFileSystemPreview page, CancellationToken token = default)
    {
        if (page.Offset != 0 || page.Bytes.Length < 26 || !page.Bytes.AsSpan().StartsWith("pap "u8))
            throw new InvalidDataException("缺少 PAP 文件头。");
        var slotsEnd = BinaryPrimitives.ReadInt32LittleEndian(page.Bytes.AsSpan(14)) +
            40L * BinaryPrimitives.ReadUInt16LittleEndian(page.Bytes.AsSpan(8));
        if (slotsEnd > page.Bytes.Length && slotsEnd <= Math.Min(page.Size, 1048576))
            page = ReadFileSystemFile(entry, (int)slotsEnd, token);
        var path = entry.KnownPath ?? entry.Path;
        return ParseAnimationMetadata(page.Bytes, page.Size, path, token);
    }

    private static GameAnimationMetadata ParseAnimationMetadata(byte[] data, string path, CancellationToken token) =>
        ParseAnimationMetadata(data, data.Length, path, token);

    private static GameAnimationMetadata ParseAnimationMetadata(ReadOnlySpan<byte> data, long size, string path, CancellationToken token)
    {
        if (data.Length < 26 || BinaryPrimitives.ReadUInt32LittleEndian(data) != 0x20706170)
            throw new InvalidDataException("不是有效的 PAP 动作资源。");
        var count = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]);
        var table = BinaryPrimitives.ReadInt32LittleEndian(data[14..]);
        var havok = BinaryPrimitives.ReadInt32LittleEndian(data[18..]);
        var timeline = BinaryPrimitives.ReadInt32LittleEndian(data[22..]);
        var slotTableEnd = table + count * 40L;
        if (table < 26 || slotTableEnd > data.Length || slotTableEnd > havok || havok > timeline || timeline > size)
            throw new InvalidDataException("PAP 动作槽或 Havok 数据范围无效。");
        var slots = new List<GameAnimationSlot>(count);
        for (var i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            var record = data.Slice(table + i * 40, 40);
            var end = record[..32].IndexOf((byte)0);
            if (end < 0) end = 32;
            var name = Encoding.UTF8.GetString(record[..end]);
            var index = BinaryPrimitives.ReadUInt16LittleEndian(record[34..]);
            slots.Add(new(index, name));
        }
        return new(path, slots, timeline - havok);
    }
}
