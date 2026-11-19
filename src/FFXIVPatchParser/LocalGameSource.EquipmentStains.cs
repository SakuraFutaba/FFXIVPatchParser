using System.Buffers.Binary;
using System.Numerics;

namespace FFXIVPatchParser;

public sealed record GameStain(byte Id, string Name, uint Color);

public sealed partial class LocalGameSource
{
    private IReadOnlyList<GameStain>? characterStains;

    public IReadOnlyList<GameStain> ListCharacterStains()
    {
        lock (gate)
            return characterStains ??= ReadVerifiedAnimationSheet("Stain", "346c5926", default)
                .Where(row => row.RowId < 255 && (row.RowId == 0 || (byte)row.ReadColumn(1) > 0))
                .Select(row => new GameStain((byte)row.RowId, ReadAnimationText(row, 6) ?? "", (uint)row.ReadColumn(0)))
                .Where(stain => stain.Name.Length > 0).ToArray();
    }

    public IReadOnlyList<GameModelMaterial> ReadItemDyeMaterials(GameModelPreview model, GameCharacterPartOption part,
        int variant, CancellationToken token = default)
    {
        var dye = (part.DyeCount > 0 ? part.Stain0Id : (byte)0, part.DyeCount > 1 ? part.Stain1Id : (byte)0);
        if (dye == default) return model.Materials;
        return model.Materials.Select(material =>
        {
            token.ThrowIfCancellationRequested();
            if (material.ShaderPackage is not ("character.shpk" or "characterlegacy.shpk" or "characterglass.shpk" or "charactertransparency.shpk"))
                return material;
            return ReadModelMaterial(part.Path, material.Reference, variant, token, dye: dye);
        }).ToArray();
    }

    private byte[]? equipmentStainTemplates, legacyStainTemplates;

    private void ApplyEquipmentStains(byte[] material, int stringEnd, Vector3[] rows, (byte First, byte Second) dye)
    {
        if ((material[stringEnd] & 8) == 0 || BinaryPrimitives.ReadUInt16LittleEndian(material.AsSpan(6)) < 32 * 68) return;
        var offset = stringEnd + material[15] + 32 * 64;
        byte[] templates;
        byte[] legacy;
        lock (gate)
        {
            legacy = legacyStainTemplates ??= game.GetFile("chara/base_material/stainingtemplate.stm")?.Data ?? [];
            templates = equipmentStainTemplates ??= game.GetFile("chara/base_material/stainingtemplate_gud.stm")?.Data ?? [];
        }
        for (var i = 0; i < rows.Length; ++i)
        {
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(material.AsSpan(offset + i * 4));
            if ((flags & 1) == 0) continue;
            var stain = ((flags >> 27) & 3) switch { 0 => dye.First, 1 => dye.Second, _ => (byte)0 };
            var template = (flags >> 16) & 0x7ff;
            if (TryGetStainDiffuse(templates, template, stain, out var color) || TryGetStainDiffuse(legacy, template, stain, out color))
                rows[i] = color;
        }
    }

    private static bool TryGetStainDiffuse(ReadOnlySpan<byte> data, uint template, byte stain, out Vector3 color)
    {
        color = default;
        if (stain is 0 or 255 || data.Length < 8) return false;
        int header = BinaryPrimitives.ReadUInt32LittleEndian(data) switch
        {
            0x0201534d when data[6] == 3 && data[7] == 9 => 24,
            0x0101534d when data[6] == 0 && data[7] == 0 => 10,
            _ => 0
        };
        if (header == 0) return false;
        var count = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
        var start = 8 + 8 * count;
        if (start > data.Length) return false;
        for (var i = 0; i < count; i++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(data[(8 + 4 * i)..]) != template) continue;
            var offset = start + 2L * BinaryPrimitives.ReadUInt32LittleEndian(data[(8 + 4 * (count + i))..]);
            if (offset + header > data.Length) return false;
            var entry = data[(int)offset..];
            var length = 2 * BinaryPrimitives.ReadUInt16LittleEndian(entry);
            if (length > entry.Length - header) return false;
            var column = entry.Slice(header, length);
            if (length == 0) return true;
            var index = 0;
            if (length == 254 * 6) index = stain - 1;
            else if (length != 6)
            {
                // 压缩列以 0xff 起始，后续索引从 1 起计，0 表示黑色。
                if (length < 254 || (length - 254) % 6 != 0) return false;
                var values = (length - 254) / 6;
                index = stain == 254 ? -1 : column[6 * values + stain] - 1;
                if (index < 0) return true;
                if (index >= values) return false;
            }
            var rgb = column[(6 * index)..];
            color = new((float)BinaryPrimitives.ReadHalfLittleEndian(rgb), (float)BinaryPrimitives.ReadHalfLittleEndian(rgb[2..]),
                (float)BinaryPrimitives.ReadHalfLittleEndian(rgb[4..]));
            return float.IsFinite(color.X) && float.IsFinite(color.Y) && float.IsFinite(color.Z);
        }
        return false;
    }
}
