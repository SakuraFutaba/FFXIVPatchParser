using Lumina.Data.Files;
using ImageNode = Lumina.Data.Parsing.Uld.NodeData.ImageNode;

namespace FFXIVPatchParser;

public sealed partial class LocalGameSource
{
    private Dictionary<(uint Race, int Gender, int Tribe, int Style), uint>? hairstyleIcons;
    private readonly Dictionary<string, GameTexture?> emptyGearIcons = new(StringComparer.Ordinal);
    private GameTexture? characterIconAtlas;

    public GameTexture? ReadCharacterDyeGlyph()
    {
        lock (gate)
        {
            var uld = ReadUld("ui/uld/actionbar.uld");
            var node = uld.Components.Where(component => component.Id == 1001)
                .SelectMany(component => component.Nodes).FirstOrDefault(node => node.NodeId == 5);
            if (node.Node is not ImageNode image) return null;
            GameTexture? atlas = null;
            return CropUldPart(uld, image.PartListId, image.PartId, 18, "ui/uld/IconA_Frame.tex", ref atlas);
        }
    }

    public GameTexture? ReadCharacterSlotFrame()
    {
        const string path = "ui/uld/actionbar.uld";
        lock (gate)
        {
            if (!game.FileExists(path)) return null;
            var uld = ReadUld(path);
            var node = uld.Components.Where(component => component.Id == 1001)
                .SelectMany(component => component.Nodes).FirstOrDefault(node => node.NodeId == 18);
            if (node.Node is not ImageNode image) return null;
            GameTexture? atlas = null;
            return CropUldPart(uld, image.PartListId, image.PartId, 48, "ui/uld/IconA_Frame.tex", ref atlas);
        }
    }

    public GameTexture? ReadCharacterPartIcon(string code, GameCharacterPartOption? part, string slot,
        uint? tribe = null, CancellationToken token = default)
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            var icon = part?.IconId ?? 0;
            if (icon == 0 && part != null && slot == "hir")
                icon = FindHairstyleIcon(code, part.Path, tribe, token);
            if (icon != 0)
            {
                var path = $"ui/icon/{icon / 1000 * 1000:D6}/{icon:D6}";
                if (game.FileExists(path + "_hr1.tex")) return ReadTexture(path + "_hr1.tex");
                if (game.FileExists(path + ".tex")) return ReadTexture(path + ".tex");
            }
            return ReadEmptyGearIcon(slot);
        }
    }

    private uint FindHairstyleIcon(string code, string path, uint? tribe, CancellationToken token)
    {
        var match = HairPartPath.Match(path);
        if (!match.Success || !TryDescribeHumanBody(code, out var race, out var gender, out var pair)
            || !int.TryParse(match.Groups[2].ValueSpan[1..], out var style)) return 0;

        hairstyleIcons ??= BuildHairstyleIcons(token);
        var bodyTribe = pair is 1 or 2 ? pair : (int?)null;
        var preferredTribe = bodyTribe ?? (int?)tribe;
        if (preferredTribe is { } selected && hairstyleIcons.TryGetValue((race, gender, selected, style), out var icon))
            return icon;

        var matches = hairstyleIcons.Where(entry => entry.Key.Race == race && entry.Key.Gender == gender
            && entry.Key.Style == style && (bodyTribe == null || entry.Key.Tribe == bodyTribe)).Select(entry => entry.Value).Distinct().Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : 0;
    }

    private Dictionary<(uint Race, int Gender, int Tribe, int Style), uint> BuildHairstyleIcons(CancellationToken token)
    {
        var icons = new Dictionary<(uint Race, int Gender, int Tribe, int Style), uint>();
        var customs = ReadVerifiedAnimationSheet("CharaMakeCustomize", "c30e9b73", token)
            .ToDictionary(row => row.RowId, row => (Style: (byte)row.ReadColumn(0), Icon: (uint)row.ReadColumn(1)));
        foreach (var row in ReadVerifiedAnimationSheet("HairMakeType", "c9235fda", token))
        {
            var race = (uint)(int)row.ReadColumn(0);
            var tribe = (int)row.ReadColumn(1);
            var gender = (sbyte)row.ReadColumn(2);
            var count = Math.Min(106, (int)(byte)row.ReadColumn(30));
            for (var i = 0; i < count; i++)
            {
                var customizeId = (uint)row.ReadColumn(66 + i * 9);
                if (customs.TryGetValue(customizeId, out var customize) && customize.Style != 0 && customize.Icon != 0)
                    icons.TryAdd((race, gender, tribe, customize.Style), customize.Icon);
            }
        }
        return icons;
    }

    private GameTexture? ReadEmptyGearIcon(string slot)
    {
        if (emptyGearIcons.TryGetValue(slot, out var cached)) return cached;
        // Character.uld 的 PartList 12 使用同一张贴图保存装备槽占位图案。
        var index = slot switch
        {
            "weapon" => 17, "offhand" => 18, "met" => 19, "top" => 20, "glv" => 21,
            "dwn" => 23, "sho" => 24, "ear" => 25, "nek" => 26, "wrs" => 27,
            "rir" or "ril" => 28, _ => -1
        };
        const string uldPath = "ui/uld/character.uld";
        if (index < 0 || !game.FileExists(uldPath)) return emptyGearIcons[slot] = null;
        var icon = CropUldPart(ReadUld(uldPath), 12, (uint)index, 32, "ui/uld/Character.tex", ref characterIconAtlas);
        if (icon == null) return emptyGearIcons[slot] = null;
        // 保留 ULD 的透明轮廓，在浅色槽底使用更清楚的蓝色。
        for (var i = 0; i < icon.Bgra32.Length; i += 4)
        {
            icon.Bgra32[i] = 155;
            icon.Bgra32[i + 1] = 101;
            icon.Bgra32[i + 2] = 58;
            icon.Bgra32[i + 3] = (byte)Math.Min(255, icon.Bgra32[i + 3] * 2);
        }
        return emptyGearIcons[slot] = icon;
    }

    private GameTexture? CropUldPart(UldFile uld, uint listId, uint index, int size, string expectedPath, ref GameTexture? cachedAtlas)
    {
        var parts = uld.Parts.FirstOrDefault(list => list.Id == listId).Parts;
        if (parts == null || index >= parts.Length) return null;
        var part = parts[index];
        var asset = uld.AssetData.FirstOrDefault(entry => entry.Id == part.TextureId).Path;
        if (asset == null) return null;
        var path = new string(asset).TrimEnd('\0');
        if (!path.Equals(expectedPath, StringComparison.OrdinalIgnoreCase) || !game.FileExists(path)) return null;

        var atlas = cachedAtlas ??= ReadTexture(path);
        if (part.W != size || part.H != size || part.U + part.W > atlas.Width || part.V + part.H > atlas.Height) return null;
        var pixels = new byte[part.W * part.H * 4];
        for (var y = 0; y < part.H; y++)
        {
            var start = ((part.V + y) * atlas.Width + part.U) * 4;
            atlas.Bgra32.AsSpan(start, part.W * 4).CopyTo(pixels.AsSpan(y * part.W * 4));
        }
        return new($"{path}#{listId}:{index}", part.W, part.H, pixels);
    }
}
