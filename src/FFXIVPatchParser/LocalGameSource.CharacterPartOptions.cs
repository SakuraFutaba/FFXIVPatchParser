using System.Numerics;
using System.Text.RegularExpressions;

namespace FFXIVPatchParser;

public sealed record GameCharacterPartOption(string Slot, string Path, string Label, int MaterialVariant = 1)
{
    public byte DyeCount { get; init; }
    public byte Stain0Id { get; init; }
    public byte Stain1Id { get; init; }
    public uint IconId { get; init; }
    public uint? RowId { get; init; }
    public int? ImcSubset { get; init; }
    public override string ToString() => Label;
}

public sealed partial class LocalGameSource
{
    private static readonly Regex EquipmentPartPath = new(
        @"^chara/equipment/(e\d{4})/model/(c\d{4})\1_(met|top|glv|dwn|sho)\.mdl$", RegexOptions.CultureInvariant);
    private static readonly string[] AccessorySlots = ["ear", "nek", "wrs", "rir", "ril"];
    private static readonly Regex AccessoryPartPath = new(
        @"^chara/accessory/(a\d{4})/model/(c\d{4})\1_(ear|nek|wrs|rir|ril)\.mdl$", RegexOptions.CultureInvariant);
    private static readonly Regex HairPartPath = new(
        @"^chara/human/(c\d{4})/obj/hair/(h\d{4})/model/\1\2_hir\.mdl$", RegexOptions.CultureInvariant);
    private static readonly Regex FacePartPath = new(
        @"^chara/human/(c\d{4})/obj/face/(f\d{4})/model/\1\2_fac\.mdl$", RegexOptions.CultureInvariant);
    private static readonly Regex WeaponPartPath = new(
        @"^chara/weapon/(w\d{4})/obj/body/(b\d{4})/model/\1\2\.mdl$", RegexOptions.CultureInvariant);
    private readonly Dictionary<string, IReadOnlyList<GameCharacterPartOption>> partOptions = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Code, string Decoder), Dictionary<string, Matrix4x4>> weaponGrips = new();
    private int partOptionsRevision = -1;

    public IReadOnlyList<GameCharacterPartOption> ListCharacterPartOptions(string code, CancellationToken token = default)
    {
        if (!CharacterSamples.ContainsKey(code)) throw new ArgumentOutOfRangeException(nameof(code));
        lock (gate)
        {
            if (partOptionsRevision != MediaCatalogRevision)
            {
                partOptions.Clear();
                partOptionsRevision = MediaCatalogRevision;
            }
            if (partOptions.TryGetValue(code, out var cached)) return cached;
            var options = new List<GameCharacterPartOption>();
            AddItemParts(code, options, token);
            var labeled = options.Select(o => o.Path).ToHashSet(StringComparer.Ordinal);
            foreach (var container in ListFileSystemContainers(token).Where(c => c.Category == "chara"))
            foreach (var entry in EnumerateFileSystemEntries(container.Id, token))
            {
                token.ThrowIfCancellationRequested();
                if (entry.KnownPath is not { } path || !path.EndsWith(".mdl", StringComparison.Ordinal) || labeled.Contains(path)) continue;
                var slot = CharacterPartSlot(code, path);
                if (slot == null) continue;
                labeled.Add(path);
                var label = slot == "hir" ? "发型 " + HairPartPath.Match(path).Groups[2].Value[1..]
                    : slot == "fac" ? "脸型 " + FacePartPath.Match(path).Groups[2].Value[1..]
                    : Path.GetFileNameWithoutExtension(path);
                options.Add(new(slot, path, label));
            }
            // 新安装尚未导入路径表时也能直接查本体型的发型目录。
            for (var i = 1; i <= 999; i++)
            {
                token.ThrowIfCancellationRequested();
                var face = $"chara/human/{code}/obj/face/f{i:D4}/model/{code}f{i:D4}_fac.mdl";
                if (!labeled.Contains(face) && game.FileExists(face)) options.Add(new("fac", face, $"脸型 {i:D4}"));
                var path = $"chara/human/{code}/obj/hair/h{i:D4}/model/{code}h{i:D4}_hir.mdl";
                if (!labeled.Contains(path) && game.FileExists(path)) options.Add(new("hir", path, $"发型 {i:D4}"));
            }
            return partOptions[code] = options.OrderBy(x => x.Slot, StringComparer.Ordinal)
                .ThenBy(x => x.Label, StringComparer.CurrentCulture).ToArray();
        }
    }

    public (int Material, int? Mask) ResolveItemPartVariant(GameCharacterPartOption option, CancellationToken token = default)
        => CharacterPartVariant(option, token);

    public IReadOnlyList<GameCharacterPartOption> ListStandaloneItemParts(CancellationToken token)
    {
        lock (gate)
        {
            var options = new List<GameCharacterPartOption>();
            AddItemParts(null, options, token);
            return options;
        }
    }

    private void AddItemParts(string? code, List<GameCharacterPartOption> options, CancellationToken token)
    {
        var paths = new Dictionary<(string Slot, int Id, int Body), string?>();
        foreach (var row in ReadVerifiedAnimationSheet("Item", "e9a33c9d", token))
        {
            token.ThrowIfCancellationRequested();
            var slot = (byte)row.ReadColumn(17) switch
            {
                1 or 13 or 14 => "weapon", 2 => "offhand", 3 => "met",
                4 or 15 or 16 or 19 or 20 or 21 or 22 or 23 => "top",
                5 => "glv", 7 or 18 => "dwn", 8 => "sho",
                9 => "ear", 10 => "nek", 11 => "wrs", 12 => "rir", _ => null
            };
            if (slot == null) continue;
            if (ReadAnimationText(row, 9) is not { } name) continue;
            AddItemPart(slot, Convert.ToUInt64(row.ReadColumn(47)));
            if (code != null && slot == "rir") AddItemPart("ril", Convert.ToUInt64(row.ReadColumn(47)));
            if (slot == "weapon") AddItemPart("offhand", Convert.ToUInt64(row.ReadColumn(48)));

            void AddItemPart(string target, ulong packed)
            {
                var id = (ushort)packed;
                var second = (ushort)(packed >> 16);
                var weapon = target is "weapon" or "offhand";
                if (id is 0 or > 9999 || weapon && (second is 0 or > 9999)) return;
                var key = (target, id, weapon ? second : 0);
                if (!paths.TryGetValue(key, out var path))
                {
                    if (weapon)
                    {
                        var candidate = $"chara/weapon/w{id:D4}/obj/body/b{second:D4}/model/w{id:D4}b{second:D4}.mdl";
                        path = game.FileExists(candidate) ? candidate : null;
                    }
                    else
                    {
                        IEnumerable<string> races = code == null ? CharacterPreviewCodes : [code];
                        foreach (var race in races)
                        {
                            var candidate = code == null && !AccessorySlots.Contains(target)
                                ? $"chara/equipment/e{id:D4}/model/{race}e{id:D4}_{target}.mdl"
                                : ResolveWearableModelPath(race, id, target);
                            if (candidate == null || !game.FileExists(candidate)) continue;
                            path = candidate;
                            break;
                        }
                    }
                    paths.Add(key, path);
                }
                if (path == null) return;
                options.Add(new(target, path, $"{name} · #{row.RowId}")
                {
                    ImcSubset = weapon ? (ushort)(packed >> 32) : second,
                    DyeCount = (byte)row.ReadColumn(28), RowId = row.RowId, IconId = Convert.ToUInt32(row.ReadColumn(10))
                });
            }
        }
    }

    private static string? CharacterPartSlot(string code, string path)
    {
        var accessory = AccessoryPartPath.Match(path);
        if (accessory.Success && accessory.Groups[2].Value == code) return accessory.Groups[3].Value;
        var equip = EquipmentPartPath.Match(path);
        if (equip.Success && equip.Groups[2].Value == code) return equip.Groups[3].Value;
        var face = FacePartPath.Match(path);
        if (face.Success && face.Groups[1].Value == code) return "fac";
        var hair = HairPartPath.Match(path);
        if (hair.Success && hair.Groups[1].Value == code) return "hir";
        return WeaponPartPath.IsMatch(path) ? "weapon" : null;
    }

    private int CharacterPartMaterial(GameCharacterPartOption option, CancellationToken token)
    {
        if (option.ImcSubset is not { } subset) return option.MaterialVariant;
        return ResolveModelMaterialSubset(option.Path, subset, option.Slot is not ("weapon" or "offhand"), token);
    }

    private Matrix4x4 ReadWeaponGrip(string code, string attachment, string? decoder, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(decoder)) throw new NotSupportedException("预览手持武器需要设置 Godbert 安装目录。");
        var key = (code, Path.GetFullPath(decoder));
        Dictionary<string, Matrix4x4>? grips;
        lock (gate) weaponGrips.TryGetValue(key, out grips);
        if (grips == null)
        {
            var pose = Task.Run(() => BakeModelAnimationCoreAsync("character:" + code, "", 0, decoder, token, 1, referenceOnly: true), token)
                .GetAwaiter().GetResult();
            grips = pose.BoneNames.Select((name, i) => (name, i)).ToDictionary(b => b.name, b => pose.Frames[0][b.i], StringComparer.Ordinal);
            lock (gate) weaponGrips[key] = grips;
        }
        return grips.TryGetValue(attachment, out var transform) ? transform
            : throw new NotSupportedException($"当前体型的基础骨架缺少挂点 {attachment}。");
    }
}
