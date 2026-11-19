using System.Globalization;

namespace FFXIVPatchParser;

public sealed record GameMapEffectEvent(DateTimeOffset Time, uint DirectorId, byte Index, ushort State, ushort TimelineIndex)
{
    public string Label => $"{Time:HH:mm:ss.fff} · 索引 {Index} · {State:X4}/{TimelineIndex:X4}";
}

public sealed partial class LocalGameSource
{
    // ACT 的 01 行标记区域，257 行记录 MapEffect。只取最近一次含事件的本区域记录。
    public static IReadOnlyList<GameMapEffectEvent> ReadMapEffectRecording(string path, uint territoryId, CancellationToken token)
    {
        var events = new List<GameMapEffectEvent>();
        uint? zone = null;
        var nextVisit = false;
        foreach (var line in File.ReadLines(path))
        {
            token.ThrowIfCancellationRequested();
            if (line.StartsWith("01|", StringComparison.Ordinal))
            {
                var fields = line.Split('|', 5);
                zone = fields.Length >= 3 && uint.TryParse(fields[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id) ? id : null;
                nextVisit = true;
                continue;
            }
            if (zone != territoryId || !line.StartsWith("257|", StringComparison.Ordinal)) continue;
            var parts = line.Split('|', 6);
            if (parts.Length < 5 ||
                !DateTimeOffset.TryParse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ||
                !uint.TryParse(parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var director) ||
                !uint.TryParse(parts[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var flags) ||
                !byte.TryParse(parts[4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var index)) continue;
            if (nextVisit)
            {
                events.Clear();
                nextVisit = false;
            }
            events.Add(new(time, director, index, (ushort)(flags & 0xffff), (ushort)(flags >> 16)));
        }
        return events.OrderBy(e => e.Time).ToArray();
    }
}
