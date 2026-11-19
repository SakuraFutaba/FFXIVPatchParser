using System.Text;

namespace FFXIVPatchParser;

public sealed record GameSharedGroupTimeline(int Slot, uint Id, string Name)
{
    public ushort Mask => (ushort)(1 << Slot);
    public string Label => $"0x{Mask:X4} · {Name}";
}

public sealed partial class LocalGameSource
{
    private static IReadOnlyList<GameSharedGroupTimeline> ParseSharedGroupTimelines(byte[] data)
    {
        const int scene = 20;
        bool Fits(int offset, int length) => offset >= 0 && length >= 0 && offset <= data.Length - length;
        if (!Fits(0, scene + 0x24) || !data.AsSpan(0, 4).SequenceEqual("SGB1"u8) ||
            !data.AsSpan(12, 4).SequenceEqual("SCN1"u8)) return [];
        int I(int offset) => BitConverter.ToInt32(data, offset);
        int Relative(int offset, int field)
        {
            var absolute = (long)offset + I(offset + field);
            return absolute >= 0 && absolute <= data.Length ? (int)absolute : -1;
        }
        var folder = Relative(scene, 0x10);
        var settings = Relative(scene, 0x20);
        if (folder <= scene || settings <= scene || !Fits(folder, 8) || !Fits(settings, 21)) return [];
        var entries = Relative(folder, 0);
        int count = I(folder + 4);
        if (count is < 1 or > 4096 || !Fits(entries, count * 0x2c)) return [];
        var names = new Dictionary<int, string>();
        for (int i = 0; i < count; ++i)
        {
            var record = entries + i * 0x2c;
            var id = I(record);
            var name = Relative(record, 4);
            if (id is < 1 or > 255 || !Fits(name, 1)) continue;
            var end = Array.IndexOf(data, (byte)0, name, Math.Min(1024, data.Length - name));
            if (end > name) names.TryAdd(id, Encoding.UTF8.GetString(data, name, end - name));
        }
        var result = new List<GameSharedGroupTimeline>();
        for (int slot = 0; slot < 16; slot++)
        {
            var id = data[settings + 5 + slot];
            if (names.TryGetValue(id, out var name)) result.Add(new(slot, id, name));
        }
        return result;
    }
}
