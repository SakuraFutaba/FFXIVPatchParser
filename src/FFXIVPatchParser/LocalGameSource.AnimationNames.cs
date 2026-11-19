using System.Globalization;
using System.Text.RegularExpressions;
using Lumina.Data.Structs.Excel;
using Lumina.Excel;
using Lumina.Text.ReadOnly;

namespace FFXIVPatchParser;

public sealed record GameAnimationReference(string Sheet, uint RowId, string Role, string? Name = null, string? TimelineSlot = null)
{
    public string Label => $"{Sheet} #{RowId} · {Role}" + (string.IsNullOrEmpty(Name) ? "" : $" · {Name}") +
        (TimelineSlot == null ? "" : TimelineSlot.EndsWith(".pap", StringComparison.Ordinal)
            ? $" · TMB 资源 {TimelineSlot}" : $" · TMB 同名槽 {TimelineSlot}");
}

public sealed record GameAnimationDescription(string Path, string? Body, string? Bank, string? AnimationSet,
    string? Key, IReadOnlyList<GameAnimationReference> References)
{
    public string? WeaponVariantName => AnimationSet switch
    {
        "bt_2ax_emp" => "双手斧（斧术师／战士）",
        "bt_2bk_emp" => "魔导书（秘术师／召唤师／学者）",
        "bt_2bw_emp" => "弓（弓箭手／吟游诗人）",
        "bt_2gl_emp" => "天球仪（占星术士）",
        "bt_swd_sld" => "剑盾（剑术师／骑士）",
        "bt_clw_clw" => "格斗武器（格斗家／武僧）",
        "bt_2sp_emp" => "长枪（枪术师／龙骑士）",
        "bt_dgr_dgr" => "双刀（双剑师／忍者）",
        "bt_2gn_emp" => "火枪（机工士）",
        "bt_2sw_emp" => "巨剑（暗黑骑士）",
        "bt_2kt_emp" => "刀（武士）",
        "bt_2rp_emp" => "刺剑（赤魔法师）",
        "bt_chk_chk" => "投掷武器（舞者）",
        "bt_2gb_emp" => "枪刃（绝枪战士）",
        "bt_2km_emp" => "战镰（钐镰客）",
        "bt_bld_bld" => "双牙剑（蝰蛇剑士）",
        "bt_brs_plt" => "画笔（绘灵法师）",
        "bt_axe_sld" => "斧盾（驯兽师）",
        "bt_2ff_emp" => "贤具（贤者）",
        "bt_jst_sld" => "单手咒杖与盾（咒术师）",
        "bt_stf_sld" => "单手幻杖与盾（幻术师）",
        "bt_2js_emp" => "双手咒杖（咒术师／黑魔法师）",
        "bt_2st_emp" => "双手幻杖（幻术师／白魔法师）",
        "bt_rod_emp" => "青魔杖（青魔法师）",
        _ => null
    };

    public string? PoseName
    {
        get
        {
            if (AnimationSet == "bt_common")
            {
                var ground = References.FirstOrDefault(r => r.Sheet == "Emote" &&
                    r.TimelineSlot == null && r.RowId is 97 or 98 or 117);
                var pose = ground?.RowId switch { 97 => 1, 98 => 2, 117 => 3, _ => 0 };
                if (pose != 0 && Key == $"emote/j_pose0{pose}_loop") return $"地面坐姿 {pose}（循环）";
                if (pose != 0 && Key == $"emote/j_pose0{pose}_start") return $"地面坐姿 {pose}（起始）";
            }
            if (AnimationSet?.StartsWith("bt_", StringComparison.Ordinal) != true || AnimationSet == "bt_common") return null;
            return Key switch
            {
                "emote/b_pose01_loop" => "持武器待机姿势（循环）",
                "emote/b_pose01_start" => "持武器待机姿势（起始）",
                "emote/battle07" or "emote/battle08" or "emote/battle09" => "持武器动作变体",
                _ => null
            };
        }
    }

    public string NamingEvidence => PoseName != null ? "姿势或武器变体" :
        LocalizedNames(false).Length > 0 ? "表内名称" :
        LocalizedNames(true).Length > 0 ? "TMB 槽关联名称" :
        References.Any(r => r.Sheet is not ("Race" or "ResidentMotionType")) ? "有表行或槽名" : "仅资源标识";

    private string[] LocalizedNames(bool fromTimeline) => References
        .Where(r => (r.Sheet is "Action" or "Emote" or "Mount") && r.Name != null && (r.TimelineSlot != null) == fromTimeline)
        .Select(r => r.Name!).Distinct(StringComparer.Ordinal).ToArray();

    public string Label
    {
        get
        {
            var file = Path[(Path.LastIndexOf('/') + 1)..];
            var names = LocalizedNames(false);
            var variant = WeaponVariantName ?? (AnimationSet == "bt_common" ? null : AnimationSet);
            if (PoseName is { } pose)
                return variant == null ? $"{pose} · {file}" : $"{variant} · {pose} · {file}";
            var face = Bank?.StartsWith('f') == true;
            if (face) variant = $"面部 {Bank}";
            if (names.Length == 0 && LocalizedNames(true) is { Length: > 0 } linked)
                return $"{(variant == null ? "" : variant + " · ")}{linked[0]}{(linked.Length > 1 ? $" 等 {linked.Length} 个" : "")}（槽关联） · {file}";
            if (variant != null && names.Length == 1 && Key is "emote/battle01" or "emote/battle02" or "emote/battle03")
                return $"{variant} · {names[0]} · {file}";
            if (names.Length == 0 && variant != null) return $"{variant} · {file}";
            return names.Length switch
            {
                0 => file,
                1 => $"{names[0]} · {file}",
                _ => $"{string.Join(" / ", names.Take(2))}{(names.Length > 2 ? $" 等 {names.Length} 个名称" : "")} · {file}"
            };
        }
    }
}

public sealed partial class LocalGameSource
{
    private sealed record AnimationNames(
        ILookup<string, uint> ActionKeys,
        ILookup<string, GameAnimationReference> OtherKeys,
        ILookup<uint, GameAnimationReference> TimelineUsers,
        ILookup<string, GameAnimationReference> AnimationSets,
        Dictionary<(uint Race, int Gender), string> RaceNames);

    private AnimationNames? animationNames;
    private static readonly Regex CharacterPapPath = new(
        @"^chara/(?<kind>human|demihuman|monster|weapon)/(?<body>[cdmw]\d{4})/animation/(?<bank>[afs]\d{4})/(?<rest>.+)\.pap$",
        RegexOptions.CultureInvariant);

    // The two Hyur body pairs share Race #1. Hrothgar and Viera have a different row order from their body codes.
    private static uint BodyRace(int pair) => pair switch
    {
        1 or 2 => 1, 3 => 2, 4 => 4, 5 => 5, 6 => 3, 7 => 6, 8 => 7, 9 => 8, _ => 0
    };

    private static bool TryDescribeHumanBody(string code, out uint race, out int gender, out int pair)
    {
        race = 0;
        gender = pair = 0;
        if (code.Length != 5 || code[0] != 'c' || !int.TryParse(code.AsSpan(1, 2), NumberStyles.None,
            CultureInfo.InvariantCulture, out var prefix)) return false;
        pair = (prefix + 1) / 2;
        gender = (prefix + 1) % 2;
        race = BodyRace(pair);
        return race != 0;
    }

    public IReadOnlyList<GameAnimationDescription> ListModelAnimationDescriptions(string modelPath, CancellationToken token = default)
    {
        var paths = ListModelAnimationPaths(modelPath, token);
        var index = GetAnimationNames(token);
        return paths.Select(path =>
        {
            token.ThrowIfCancellationRequested();
            if (path.EndsWith("/bt_common/resident/action.pap", StringComparison.Ordinal))
            {
                try
                {
                    var slots = ReadAnimationMetadata(path, token).Slots;
                    return DescribeAnimation(path, index, slots, GetPapTimelineReferences(index, token));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException) { }
            }
            return DescribeAnimation(path, index, null, null);
        }).ToArray();
    }

    public GameAnimationDescription DescribeAnimation(string path, IReadOnlyList<GameAnimationSlot>? slots = null, CancellationToken token = default)
    {
        var index = GetAnimationNames(token);
        var slotsFromTimelines = slots is { Count: > 0 } && path.StartsWith("chara/", StringComparison.OrdinalIgnoreCase)
            ? GetPapTimelineReferences(index, token) : null;
        return DescribeAnimation(path, index, slots, slotsFromTimelines);
    }

    private static GameAnimationDescription DescribeAnimation(string path, AnimationNames index, IReadOnlyList<GameAnimationSlot>? slots,
        ILookup<string, PapTimelineReference>? slotsFromTimelines)
    {
        var normalized = path.Trim().Replace('\\', '/').TrimStart('/').ToLowerInvariant();
        var references = new List<GameAnimationReference>();
        string? body = null, bank = null, set = null, key = null;
        var match = CharacterPapPath.Match(normalized);
        var isWeapon = false;
        if (match.Success)
        {
            var code = match.Groups["body"].Value;
            body = code;
            bank = match.Groups["bank"].Value;
            var kind = match.Groups["kind"].Value;
            isWeapon = kind == "weapon";
            if (kind == "human" && TryDescribeHumanBody(code, out var race, out var gender, out var pair))
            {
                if (index.RaceNames.TryGetValue((race, gender), out var name))
                {
                    body = $"{name}{(pair == 1 ? "中原体型" : pair == 2 ? "高地体型" : "")} · {(gender == 0 ? "男" : "女")} ({code})";
                    references.Add(new("Race", race, "人形体型", name));
                }
            }
            var relative = match.Groups["rest"].Value;
            var slash = relative.IndexOf('/');
            if (slash > 0 && relative[..slash] is { } prefix &&
                (prefix.StartsWith("bt_", StringComparison.Ordinal) || prefix.StartsWith("wp_", StringComparison.Ordinal) || Regex.IsMatch(prefix, @"^mt_m\d{4}$")))
            {
                set = prefix;
                relative = relative[(slash + 1)..];
                references.AddRange(index.AnimationSets[set]);
            }
            key = relative;
            var isFace = kind == "human" && bank.StartsWith('f');
            if (!isWeapon && !isFace)
                foreach (var timeline in index.ActionKeys[key])
                {
                    references.Add(new("ActionTimeline", timeline, "动作键", key));
                    var weaponVariant = kind == "human" && set is not (null or "bt_common") &&
                        key is "emote/battle07" or "emote/battle08" or "emote/battle09";
                    references.AddRange(weaponVariant
                        ? index.TimelineUsers[timeline].Select(r => r.Sheet == "Emote" ? r with { Role = "同键记录（武器变体）" } : r)
                        : index.TimelineUsers[timeline]);
                }
            references.AddRange(index.OtherKeys[(isWeapon ? "weapon:" : "actor:") + key]);
            if (slots != null && slotsFromTimelines != null)
            {
                // TMB can name a reusable internal slot without naming the physical PAP. Preserve that distinction.
                foreach (var slot in slots)
                    foreach (var reference in slotsFromTimelines["slot:" + slot.Name.ToLowerInvariant()])
                        if (reference.Matches(isFace ? "facial" : kind, code, key, set) &&
                            (isFace || isWeapon || !index.ActionKeys[key].Contains(reference.Timeline)))
                            references.AddRange(reference.References);
                foreach (var reference in slotsFromTimelines["path:" + normalized])
                    references.AddRange(reference.References);
            }
        }
        else if (normalized.StartsWith("cut/", StringComparison.Ordinal) &&
                 normalized.IndexOf("/animation/", StringComparison.Ordinal) is var cutEnd && cutEnd > 4)
        {
            // Cutscene.Path ends with the cut's own filename. A PAP under its animation folder
            // belongs to the cut, but that link does not identify the name of an individual clip.
            var root = normalized[..cutEnd];
            var scene = root[(root.LastIndexOf('/') + 1)..];
            references.AddRange(index.OtherKeys["cut:" + root + "/" + scene]);
        }
        if (slots != null)
            foreach (var slot in slots)
                references.AddRange(index.OtherKeys["slot:" + slot.Name.ToLowerInvariant()]);

        return new(path, body, bank, set, key, references.Distinct()
            .OrderBy(r => r.Sheet switch { "Emote" => 0, "Action" => 1, "ActionTimeline" => 2, _ => 3 })
            .ThenBy(r => r.Sheet, StringComparer.Ordinal).ThenBy(r => r.RowId).ToArray());
    }

    private AnimationNames GetAnimationNames(CancellationToken token)
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            if (animationNames != null) return animationNames;
            var keys = new List<(string Key, uint Id)>();
            var other = new List<(string Key, GameAnimationReference Reference)>();
            var sets = new List<(string Key, GameAnimationReference Reference)>();
            var users = new List<(uint Timeline, GameAnimationReference Reference)>();
            var races = new Dictionary<(uint, int), string>();
            var castTimelines = new Dictionary<uint, uint>();

            foreach (var row in ReadVerifiedAnimationSheet("ActionTimeline", "d803699f", token))
                if (ReadAnimationKey(row, 6) is { } key) keys.Add((key, row.RowId));
            foreach (var row in ReadVerifiedAnimationSheet("ActionCastTimeline", "2020acf6", token))
                if (row.RowId != 0) castTimelines[row.RowId] = (ushort)row.ReadColumn(0);
            foreach (var row in ReadVerifiedAnimationSheet("Action", "c5217018", token))
            {
                var name = ReadAnimationText(row, 0);
                if (name == null) continue;
                var cast = castTimelines.GetValueOrDefault((byte)row.ReadColumn(5));
                var end = (short)row.ReadColumn(7);
                var hit = (ushort)row.ReadColumn(8);
                if (cast != 0) users.Add((cast, new("Action", row.RowId, "咏唱开始", name)));
                if (end > 0) users.Add(((uint)end, new("Action", row.RowId, "施放动作", name)));
                if (hit != 0) users.Add((hit, new("Action", row.RowId, "命中反应", name)));
            }
            foreach (var row in ReadVerifiedAnimationSheet("Emote", "aff5d14e", token))
            {
                var name = ReadAnimationText(row, 0);
                for (var col = 1; col <= 7; col++)
                    if ((ushort)row.ReadColumn(col) is var id && id != 0)
                        users.Add((id, new("Emote", row.RowId, "情感动作", name)));
            }
            string[] moves = ["待机", "前移", "后移", "左移", "右移", "上移", "下移", "左转", "右转", "额外动作"];
            foreach (var row in ReadVerifiedAnimationSheet("MoveTimeline", "f057da9c", token))
                for (var col = 0; col < moves.Length; col++)
                    if ((ushort)row.ReadColumn(col) is var id && id != 0)
                        users.Add((id, new("MoveTimeline", row.RowId, moves[col])));
            foreach (var (sheet, role) in new[] { ("EventItemTimeline", "事件道具动作"), ("EventItemCastTimeline", "事件道具读条") })
                foreach (var row in ReadVerifiedAnimationSheet(sheet, "dbf43666", token))
                    if ((uint)row.ReadColumn(0) is var id && id != 0)
                        users.Add((id, new(sheet, row.RowId, role)));
            foreach (var row in ReadVerifiedAnimationSheet("ResidentMotionType", "ac18b425", token))
                for (var col = 0; col < 8; col++)
                    if (ReadAnimationKey(row, col) is { } set && set.StartsWith("bt_", StringComparison.Ordinal))
                        sets.Add((set, new("ResidentMotionType", row.RowId, $"动作集列 {col}", set)));

            var mountModels = ReadVerifiedAnimationSheet("ModelChara", "c5bb1bc4", token)
                .Where(row => Convert.ToInt32(row.ReadColumn(0)) == 3)
                .ToDictionary(row => row.RowId, row => Convert.ToInt32(row.ReadColumn(1)));
            foreach (var row in ReadVerifiedAnimationSheet("Mount", "5b89058f", token))
            {
                var modelRow = Convert.ToUInt32(row.ReadColumn(8));
                if (mountModels.TryGetValue(modelRow, out var model))
                    sets.Add(($"mt_m{model:D4}", new("Mount", row.RowId, $"骑乘动作 · ModelChara #{modelRow}", ReadAnimationText(row, 0))));
            }
            void ReadKeys(string sheet, string signature, string prefix, string role, bool cut = false)
            {
                foreach (var row in ReadVerifiedAnimationSheet(sheet, signature, token))
                    if (ReadAnimationKey(row, 0) is { } key)
                        other.Add((prefix + (cut && !key.StartsWith("cut/", StringComparison.Ordinal) ? "cut/" : "") + key,
                            new(sheet, row.RowId, role, key)));
            }
            ReadKeys("CutActionTimeline", "a8b8495c", "actor:", "演出动作键");
            ReadKeys("WeaponTimeline", "7ba5390a", "weapon:", "武器时间线键");
            ReadKeys("MotionTimeline", "12260494", "slot:", "PAP 槽名");
            ReadKeys("Cutscene", "ae8b8521", "cut:", "所属过场", cut: true);
            foreach (var row in ReadVerifiedAnimationSheet("Race", "3403807a", token))
                for (var gender = 0; gender <= 1; gender++)
                    if (ReadAnimationText(row, gender) is { } name) races[(row.RowId, gender)] = name;

            return animationNames = new(
                keys.ToLookup(p => p.Key, p => p.Id, StringComparer.Ordinal),
                other.ToLookup(p => p.Key, p => p.Reference, StringComparer.Ordinal),
                users.Distinct().ToLookup(p => p.Timeline, p => p.Reference),
                sets.Distinct().ToLookup(p => p.Key, p => p.Reference, StringComparer.Ordinal), races);
        }
    }

    // EXH 列序可能随版本变化。只有通过了本地列签名验证，才按下标建立表关系。
    private IEnumerable<RawRow> ReadVerifiedAnimationSheet(string name, string signature, CancellationToken token)
    {
        var rows = GetVerifiedRawSheet(name, signature);
        if (rows == null) yield break;
        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested();
            yield return row;
        }
    }

    private ExcelSheet<RawRow>? GetVerifiedRawSheet(string name, string signature)
    {
        if (!SheetNames.Contains(name)) return null;
        ExcelSheet<RawRow>? rows = null;
        try
        {
            var (header, language) = ReadSheetHeader(name);
            if (header.Header.Variant == ExcelVariant.Default &&
                header.GetColumnsHashString().Equals(signature, StringComparison.OrdinalIgnoreCase))
                rows = game.Excel.GetSheet<RawRow>(language, name);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException) { }
        return rows;
    }

    private static string? ReadAnimationText(RawRow row, int column)
    {
        if (row.ReadColumn(column) is not ReadOnlySeString value) return null;
        var text = value.ExtractText().Trim();
        return text.Length == 0 ? null : text;
    }

    private static string? ReadAnimationKey(RawRow row, int column)
    {
        var key = ReadAnimationText(row, column)?.Replace('\\', '/').TrimStart('/').ToLowerInvariant();
        if (key is not { Length: > 0 and <= 256 } || key.Contains("..", StringComparison.Ordinal)) return null;
        if (key.StartsWith("chara/action/", StringComparison.Ordinal)) key = key[13..];
        return key.EndsWith(".pap", StringComparison.Ordinal) || key.EndsWith(".tmb", StringComparison.Ordinal) ? key[..^4] : key;
    }
}
