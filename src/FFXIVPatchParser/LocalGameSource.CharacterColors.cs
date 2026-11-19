namespace FFXIVPatchParser;

public readonly record struct GameRgb(byte R, byte G, byte B)
{
    public string Hex => $"#{R:X2}{G:X2}{B:X2}";
}

public sealed record GameCharacterColorChoice(int Index, GameRgb Swatch, GameRgb Color);
public sealed record GameCharacterTribe(uint Id, string Name, IReadOnlyList<GameCharacterColorChoice> Skin,
    IReadOnlyList<GameCharacterColorChoice> Hair)
{
    public IReadOnlyList<GameCharacterPartOption> Faces { get; init; } = [];
}
public sealed record GameCharacterPalette(IReadOnlyList<GameCharacterTribe> Tribes,
    IReadOnlyList<GameCharacterColorChoice> Highlights, IReadOnlyList<GameCharacterColorChoice> Eyes)
{
    public const int DefaultSkinIndex = 28, DefaultHairIndex = 7, DefaultEyeIndex = 27;
    public GameCharacterColors DefaultColors => new(Tribes[0].Skin[DefaultSkinIndex].Color,
        Tribes[0].Hair[DefaultHairIndex].Color, null, Eyes[DefaultEyeIndex].Color);
}

public readonly record struct GameCharacterColors(GameRgb Skin, GameRgb Hair, GameRgb? Highlights, GameRgb Eyes)
{
    public static GameCharacterColors Neutral => new(new(236, 194, 167), new(77, 58, 48), null, new(107, 131, 129));
}

public sealed partial class LocalGameSource
{
    private readonly Dictionary<string, GameCharacterPalette?> characterPalettes = new(StringComparer.Ordinal);
    private byte[]? humanColors;

    public GameCharacterPalette? ReadCharacterPalette(string code, CancellationToken token = default)
    {
        if (!CharacterPreviewCodes.Contains(code) || !TryDescribeHumanBody(code, out var race, out var gender, out var pair))
            return null;
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            if (characterPalettes.TryGetValue(code, out var cached)) return cached;
            try
            {
                humanColors ??= game.GetFile("chara/xls/charamake/human.cmp")?.Data ?? [];
                // 两套全局颜色、32 个部族与性别组合，末尾是体型缩放参数。
                if (humanColors.Length != 0x2D980) return characterPalettes[code] = null;
                var rows = ReadVerifiedAnimationSheet("CharaMakeType", "50cdbeef", token)
                    .Where(row => (int)row.ReadColumn(0) == race && (sbyte)row.ReadColumn(2) == gender)
                    .Where(row => (int)row.ReadColumn(1) is >= 1 and <= 16)
                    .Where(row => pair is not (1 or 2) || (int)row.ReadColumn(1) == pair)
                    .DistinctBy(row => (int)row.ReadColumn(1)).OrderBy(row => (int)row.ReadColumn(1)).ToArray();
                var names = ReadVerifiedAnimationSheet("Tribe", "e74759fb", token)
                    .ToDictionary(row => row.RowId, row => ReadAnimationText(row, gender));
                var tribes = new List<GameCharacterTribe>();
                foreach (var row in rows)
                {
                    var id = (int)row.ReadColumn(1);
                    var faces = new List<GameCharacterPartOption>();
                    var menu = Enumerable.Range(0, 28).FirstOrDefault(i => Convert.ToInt32(row.ReadColumn(171 + i)) == 5, -1);
                    if (menu >= 0)
                    {
                        var count = Math.Min(100, Convert.ToInt32(row.ReadColumn(87 + menu)));
                        var clanOffset = race <= 5 && id % 2 == 0 ? 100 : 0;
                        for (var i = 0; i < count; i++)
                        {
                            var icon = Convert.ToUInt32(row.ReadColumn(199 + menu + i * 28));
                            if (icon == 0) continue;
                            var faceId = clanOffset + icon % 100;
                            var path = $"chara/human/{code}/obj/face/f{faceId:D4}/model/{code}f{faceId:D4}_fac.mdl";
                            if (game.FileExists(path))
                                faces.Add(new("fac", path, $"脸型 {i + 1}（{faceId:D4}）") { IconId = icon });
                        }
                    }
                    var start = 0x4800 + ((id - 1) * 2 + gender) * 0x1400;
                    tribes.Add(new((uint)id, names.GetValueOrDefault((uint)id) ?? $"部族 #{id}",
                        ReadColors(humanColors, start + 0xC00, start, 4, 192),
                        ReadColors(humanColors, start + 0x1000, start + 0x400, 8, 208)) { Faces = faces });
                }
                return characterPalettes[code] = tribes.Count == 0 ? null : new(tribes.ToArray(),
                    ReadColors(humanColors, 0x2800, 0x400, 4, 192),
                    ReadColors(humanColors, 0x2400, 0, 4, 192));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
            {
                return characterPalettes[code] = null;
            }
        }
    }

    // CMP separates the swatch displayed by character creation from the color passed to the shader.
    private static GameCharacterColorChoice[] ReadColors(byte[] data, int swatchStart, int colorStart, int colorStride, int count)
    {
        GameRgb At(int offset) => new(data[offset], data[offset + 1], data[offset + 2]);
        return Enumerable.Range(0, count)
            .Select(i => new GameCharacterColorChoice(i, At(swatchStart + i * 4), At(colorStart + i * colorStride))).ToArray();
    }
}
