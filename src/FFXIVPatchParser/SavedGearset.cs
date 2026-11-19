using System.Buffers.Binary;
using System.Text;

namespace FFXIVPatchParser;

public sealed record SavedGearPiece(string Slot, uint Item, uint Glamour, byte Stain1, byte Stain2)
{
    public uint AppearanceItem => Glamour != 0 ? Glamour : Item >= 1_000_000 ? Item - 1_000_000 : Item;
}

public sealed record SavedGearset(int Number, string Name, int Plate, bool HeadgearVisible, IReadOnlyList<SavedGearPiece> Pieces)
{
    public override string ToString() => $"{Number:D2} · {Name}" + (Plate > 0 ? $"（关联幻化模板 {Plate}）" : "");

    public static IReadOnlyList<SavedGearset> Read(string path)
    {
        const int fileSize = 45689, start = 21, stride = 452;
        using var file = File.OpenRead(path);
        if (file.Length != fileSize) throw new InvalidDataException("未知的套装配置长度。");
        var data = new byte[fileSize];
        file.ReadExactly(data);
        if (BinaryPrimitives.ReadUInt16LittleEndian(data) != 5 ||
            BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2)) != 110 ||
            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)) != fileSize - 32 ||
            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8)) != fileSize - 32)
            throw new InvalidDataException("当前仅支持已验证的第 110 版 GEARSET.DAT。");
        for (var i = 16; i < data.Length; i++) data[i] ^= 0x73;
        string?[] slots = ["weapon", "offhand", "met", "top", "glv", null, "dwn", "sho", "ear", "nek", "wrs", "rir", "ril"];
        var result = new List<SavedGearset>();
        for (var i = 0; i < 100; i++)
        {
            var record = data.AsSpan(start + i * stride, stride);
            if (record[0] != i) throw new InvalidDataException("套装编号与记录位置不符。");
            if ((record[55] & 1) == 0) continue;
            var name = record.Slice(1, 48);
            int end = name.IndexOf((byte)0);
            var pieces = new List<SavedGearPiece>();
            for (var j = 0; j < slots.Length; j++)
            {
                if (slots[j] is not { } slot) continue;
                var item = record[(56 + j * 28)..];
                uint id = BinaryPrimitives.ReadUInt32LittleEndian(item);
                if (id != 0) pieces.Add(new(slot, id, BinaryPrimitives.ReadUInt32LittleEndian(item[4..]), item[8], item[9]));
            }
            result.Add(new(i + 1, Encoding.UTF8.GetString(end < 0 ? name : name[..end]), record[50], (record[55] & 8) != 0, pieces));
        }
        return result;
    }
}
