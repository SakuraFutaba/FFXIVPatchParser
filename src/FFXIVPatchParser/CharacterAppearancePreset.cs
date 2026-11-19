using System.Buffers.Binary;

namespace FFXIVPatchParser;

public sealed record CharacterAppearancePreset(string Code, byte Tribe, byte Face, byte Hair,
    byte SkinColor, byte HairColor, byte EyeColor, byte? Highlights)
{
    public static CharacterAppearancePreset Read(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length != 212) throw new InvalidDataException("外观存档长度不符。");
        Span<byte> data = stackalloc byte[212];
        file.ReadExactly(data);
        if (BinaryPrimitives.ReadUInt32LittleEndian(data) != 0x2013FF14 ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) != 5)
            throw new InvalidDataException("请选择第 5 版 FFXIV_CHARA 外观存档。");
        var custom = data.Slice(16, 32);
        uint checksum = 0;
        for (var i = 0; i < custom.Length; i++)
            checksum ^= (uint)custom[i] << (i % 24);
        if (checksum != BinaryPrimitives.ReadUInt32LittleEndian(data[8..]))
            throw new InvalidDataException("外观存档校验失败。");
        var race = custom[0];
        var tribe = custom[4];
        if (race is < 1 or > 8 || tribe is < 1 or > 16 || (tribe + 1) / 2 != race || custom[1] > 1)
            throw new InvalidDataException("种族、部族或性别字段无效。");
        int pair = race switch { 1 => tribe, 2 => 3, 3 => 6, 4 => 4, 5 => 5, _ => race + 1 };
        var code = $"c{2 * pair - 1 + custom[1]:D2}01";
        return new(code, tribe, custom[5], custom[6], custom[8], custom[10], custom[15],
            (custom[7] & 0x80) != 0 ? custom[11] : null);
    }
}
