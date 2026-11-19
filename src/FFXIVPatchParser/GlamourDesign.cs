using System.Text.Json;

namespace FFXIVPatchParser;

public sealed record GlamourDesignSlot(string Slot, ulong ItemId, bool ApplyItem, bool ApplyStains,
    byte Stain0, byte Stain1);

public static class GlamourDesign
{
    private static readonly (string Name, string Slot)[] Slots =
    [
        ("MainHand", "weapon"), ("OffHand", "offhand"), ("Head", "met"), ("Body", "top"),
        ("Hands", "glv"), ("Legs", "dwn"), ("Feet", "sho"), ("Ears", "ear"),
        ("Neck", "nek"), ("Wrists", "wrs"), ("RFinger", "rir"), ("LFinger", "ril")
    ];

    public static IReadOnlyList<GlamourDesignSlot> Read(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > 16 * 1024 * 1024) throw new InvalidDataException("设计文件过大。");
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("FileVersion", out var version) || version.GetInt32() is not (1 or 3) ||
            !root.TryGetProperty("Equipment", out var equipment) || equipment.ValueKind != JsonValueKind.Object ||
            equipment.TryGetProperty("Array", out _))
            throw new InvalidDataException("请选择 Glamourer V1 或 V3 的人形装备设计 JSON。");
        var slots = new List<GlamourDesignSlot>();
        foreach (var (name, slot) in Slots)
        {
            if (!equipment.TryGetProperty(name, out var item)) continue;
            var apply = item.TryGetProperty("Apply", out var a) && a.GetBoolean();
            var dye = item.TryGetProperty("ApplyStain", out var d) && d.GetBoolean();
            if (!apply && !dye) continue;
            ulong id = item.TryGetProperty("ItemId", out var i) ? i.GetUInt64() : ulong.MaxValue;
            slots.Add(new(slot, id, apply, dye,
                item.TryGetProperty("Stain", out var s) ? s.GetByte() : (byte)0,
                item.TryGetProperty("Stain2", out var t) ? t.GetByte() : (byte)0));
        }
        return slots;
    }
}
