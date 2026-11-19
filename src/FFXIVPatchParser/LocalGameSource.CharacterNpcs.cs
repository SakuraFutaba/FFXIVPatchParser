using Lumina.Excel;

namespace FFXIVPatchParser;

public sealed record GameCharacterNpcPreset(uint RowId, string Name, string Code,
    IReadOnlyList<GameCharacterPartOption> Parts, GameCharacterColors Colors)
{
    public uint TribeId { get; init; }
}

public sealed partial class LocalGameSource
{
    private GameCharacterNpcPreset[]? characterNpcs;

    public IReadOnlyList<GameCharacterNpcPreset> ListCharacterNpcPresets(string? code = null, CancellationToken token = default)
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            characterNpcs ??= LoadCharacterNpcs(token);
            return code == null ? characterNpcs : characterNpcs.Where(p => p.Code == code).ToArray();
        }
    }

    public GameModelPreview ReadCharacterNpcPreview(GameCharacterNpcPreset preset, CancellationToken token = default, string? godbertDirectory = null)
    {
        var parts = string.IsNullOrWhiteSpace(godbertDirectory)
            ? preset.Parts.Where(p => p.Slot is not ("weapon" or "offhand")).ToArray() : preset.Parts;
        var result = ReadCharacterPreview(preset.Code, preset.Colors, parts, token: token,
            godbertDirectory: godbertDirectory, useStartingEquipment: false);
        return parts.Count == preset.Parts.Count ? result : result with
        {
            Warnings = [.. result.Warnings, "设置 Godbert 安装目录后可显示该 NPC 的武器。"]
        };
    }

    private GameCharacterNpcPreset[] LoadCharacterNpcs(CancellationToken token)
    {
        var bases = GetVerifiedRawSheet("ENpcBase", "5ba9e1a6");
        var residents = GetVerifiedRawSheet("ENpcResident", "f74fa88c");
        var outfits = GetVerifiedRawSheet("NpcEquip", "7eaeb95c");
        if (bases == null || residents == null || outfits == null) return [];
        var result = new List<GameCharacterNpcPreset>();
        var existing = new Dictionary<string, bool>(StringComparer.Ordinal);
        var wearables = new Dictionary<(string Code, int Model, string Slot), string?>();
        bool Exists(string path)
        {
            if (!existing.TryGetValue(path, out var found)) existing[path] = found = game.FileExists(path);
            return found;
        }
        string? Wearable(string body, int model, string slot)
        {
            var key = (body, model, slot);
            if (!wearables.TryGetValue(key, out var path)) wearables[key] = path = ResolveWearableModelPath(body, model, slot);
            return path;
        }
        foreach (var resident in residents)
        {
            token.ThrowIfCancellationRequested();
            if (ReadAnimationText(resident, 0) is not { Length: > 0 } name || !bases.TryGetRow(resident.RowId, out var row)) continue;
            int Value(int column) => Convert.ToInt32(row.ReadColumn(column));
            if (Value(35) != 0 || Value(38) is not (1 or 4)) continue;
            var race = Value(36);
            var tribe = Value(40);
            var prefix = race switch { 1 => tribe == 2 ? 3 : 1, 2 => 5, 3 => 11, 4 => 7, 5 => 9, 6 => 13, 7 => 15, 8 => 17, _ => 0 };
            if (prefix == 0 || Value(37) > 1) continue;
            var code = $"c{prefix + Value(37):D2}{Value(38):D2}";
            var face = Value(41);
            if (Value(38) == 1 && race <= 5 && tribe % 2 == 0 && face is > 0 and < 100) face += 100;
            var hair = Value(42);
            string Human(string type, char letter, int id, string slot) =>
                $"chara/human/{code}/obj/{type}/{letter}{id:D4}/model/{code}{letter}{id:D4}_{slot}.mdl";
            var facePath = Human("face", 'f', face, "fac");
            var hairPath = Human("hair", 'h', hair, "hir");
            if (!Exists(facePath) || !Exists(hairPath)) continue;
            var parts = new List<GameCharacterPartOption> { new("fac", facePath, $"脸型 {face}"), new("hir", hairPath, $"发型 {hair}") };
            var shape = Value(58);
            if (shape > 0 && race is 4 or 6 or 7 or 8)
            {
                var ear = race == 8;
                var slot = ear ? "zer" : "til";
                var path = Human(ear ? "zear" : "tail", ear ? 'z' : 't', shape, slot);
                if (Exists(path)) parts.Add(new(slot, path, $"{(ear ? "耳型" : "尾型")} {shape}"));
            }
            var equip = row;
            var start = 65;
            if (Value(63) != 0 && Convert.ToUInt32(row.ReadColumn(76)) == 0 && Convert.ToUInt32(row.ReadColumn(82)) == 0 && outfits.TryGetRow((uint)Value(63), out var outfit))
            {
                equip = outfit;
                start = 0;
            }
            foreach (var (slot, offset) in new (string, int)[] { ("met",6), ("top",11), ("glv",14), ("dwn",17), ("sho",20), ("ear",23), ("nek",26), ("wrs",29), ("ril",32), ("rir",35) })
            {
                var packed = Convert.ToUInt32(equip.ReadColumn(start + offset));
                var model = (ushort)packed;
                if (model == 0 || Wearable(code, model, slot) is not { } path || !Exists(path)) continue;
                parts.Add(new(slot, path, $"{slot} · {model}") { ImcSubset = (byte)(packed >> 16), DyeCount = 2,
                    Stain0Id = Convert.ToByte(equip.ReadColumn(start + offset + 1)), Stain1Id = Convert.ToByte(equip.ReadColumn(start + offset + 2)) });
            }
            foreach (var (slot, offset) in new[] { ("weapon", 0), ("offhand", 3) })
            {
                var packed = Convert.ToUInt64(equip.ReadColumn(start + offset));
                var id = (ushort)packed;
                var body = (ushort)(packed >> 16);
                if (id == 0 || body == 0) continue;
                var path = $"chara/weapon/w{id:D4}/obj/body/b{body:D4}/model/w{id:D4}b{body:D4}.mdl";
                if (Exists(path))
                    parts.Add(new(slot, path, $"{slot} · {id}") { ImcSubset = (ushort)(packed >> 32), DyeCount = 2,
                        Stain0Id = Convert.ToByte(equip.ReadColumn(start + offset + 1)), Stain1Id = Convert.ToByte(equip.ReadColumn(start + offset + 2)) });
            }
            var palette = ReadCharacterPalette(code[..3] + "01", token);
            var clan = palette?.Tribes.FirstOrDefault(t => t.Id == tribe);
            var colors = GameCharacterColors.Neutral;
            if (clan != null && palette != null)
            {
                GameRgb Color(IReadOnlyList<GameCharacterColorChoice> choices, int column, GameRgb fallback) =>
                    Value(column) < choices.Count ? choices[Value(column)].Color : fallback;
                colors = new(Color(clan.Skin, 44, colors.Skin), Color(clan.Hair, 46, colors.Hair),
                    Value(43) != 0 ? Color(palette.Highlights, 47, colors.Hair) : null, Color(palette.Eyes, 51, colors.Eyes));
            }
            result.Add(new(row.RowId, name, code, parts, colors) { TribeId = (uint)tribe });
        }
        return result.OrderBy(p => p.Name).ThenBy(p => p.RowId).ToArray();
    }
}
