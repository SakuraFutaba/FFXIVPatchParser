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

    /// <summary>Finds existing PAP candidates from ActionTimeline names and common resident names.
    /// This is not an exhaustive archive catalog or proof of skeleton compatibility.</summary>
    public IReadOnlyList<string> ListModelAnimationPaths(string modelPath, CancellationToken token = default)
    {
        var match = Regex.Match(modelPath.Replace('\\', '/'), @"chara/(human|monster|demihuman|weapon)/([cmdw]\d{4})/|chara/(?:equipment|accessory)/[ea]\d{4}/model/(c\d{4})", RegexOptions.CultureInvariant);
        if (!match.Success) return [];
        var family = match.Groups[3].Success ? "human" : match.Groups[1].Value;
        var id = match.Groups[3].Success ? match.Groups[3].Value : match.Groups[2].Value;
        var root = $"chara/{family}/{id}/animation/a0001/bt_common/";
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            if (modelAnimationPaths.TryGetValue(root, out var cached)) return cached;
            var candidates = new HashSet<string>(StringComparer.Ordinal);
            // Monster idle/movement clips are slots in a shared resident bundle,
            // rather than separate idle.pap / walk.pap files (Godbert uses this too).
            if (family == "monster") candidates.Add(root + "resident/monster.pap");
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
        if (data.Length < 26 || BinaryPrimitives.ReadUInt32LittleEndian(data) != 0x20706170)
            throw new InvalidDataException("不是有效的 PAP 动作资源。");
        var count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(8));
        var table = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(14));
        var havok = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(18));
        var timeline = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(22));
        if (table < 26 || (long)table + count * 40L > havok || havok > timeline || timeline > data.Length)
            throw new InvalidDataException("PAP 动作槽或 Havok 数据范围无效。");
        var slots = new List<GameAnimationSlot>(count);
        for (var i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            var record = data.AsSpan(table + i * 40, 40);
            var end = record[..32].IndexOf((byte)0);
            if (end < 0) end = 32;
            var name = Encoding.UTF8.GetString(record[..end]);
            var index = BinaryPrimitives.ReadInt32LittleEndian(record[34..]);
            slots.Add(new(index, name));
        }
        return new(path, slots, timeline - havok);
    }
}
