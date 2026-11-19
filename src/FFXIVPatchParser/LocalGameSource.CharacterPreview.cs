namespace FFXIVPatchParser;

public sealed partial class LocalGameSource
{
    private readonly record struct CharacterPartCacheKey(string Code, string Path, int Variant, int? Mask, GameCharacterColors Colors, byte First, byte Second);
    private Dictionary<CharacterPartCacheKey, GameModelPreview> previousCharacterParts = new();
    private sealed record CharacterParts(string Equipment, string? Gloves = null, string Face = "f0001", string Hair = "h0001", string? Extra = null);
    private static readonly Dictionary<string, CharacterParts> CharacterSamples = new(StringComparer.Ordinal)
    {
        ["c0101"] = new("e0001", "e0000"),
        ["c0201"] = new("e0001", "e0000"),
        ["c0301"] = new("e9017", Face: "f0101"),
        ["c0401"] = new("e0001", Face: "f0101"),
        ["c0501"] = new("e0086"),
        ["c0601"] = new("e0087"),
        ["c0701"] = new("e0088", Extra: "tail"),
        ["c0801"] = new("e0089", Extra: "tail"),
        ["c0901"] = new("e0002"),
        ["c1001"] = new("e0091"),
        ["c1101"] = new("e0002"),
        ["c1201"] = new("e0093"),
        ["c1301"] = new("e0257", Extra: "tail"),
        ["c1401"] = new("e0258", Extra: "tail"),
        ["c1501"] = new("e0597", Face: "f0005", Extra: "tail"),
        ["c1601"] = new("e0829", Face: "f0005", Hair: "h0009", Extra: "tail"),
        ["c1701"] = new("e0744", Extra: "zear"),
        ["c1801"] = new("e0581", Extra: "zear")
    };
    public static IReadOnlyList<string> CharacterPreviewCodes { get; } = Array.AsReadOnly(CharacterSamples.Keys.Order().ToArray());

    private List<string> DefaultCharacterPaths(string code, CancellationToken token)
    {
        if (!CharacterSamples.TryGetValue(code, out var sample)) throw new NotSupportedException("当前没有该体型的角色组合。");
        var human = $"chara/human/{code}/obj";
        string Equipment(string id, string slot) => ResolveWearableModelPath(code, int.Parse(id.AsSpan(1)), slot) ?? $"chara/equipment/{id}/model/{code}{id}_{slot}.mdl";
        string BodyPart(string type, string id, string slot) => $"{human}/{type}/{id}/model/{code}{id}_{slot}.mdl";
        var parts = new List<string>
        {
            Equipment(sample.Equipment, "top"), Equipment(sample.Equipment, "dwn"),
            Equipment(sample.Gloves ?? sample.Equipment, "glv"), Equipment(sample.Equipment, "sho"),
            BodyPart("face", sample.Face, "fac"), BodyPart("hair", sample.Hair, "hir")
        };
        if (sample.Extra == "tail") parts.Add(BodyPart("tail", "t0001", "til"));
        if (sample.Extra == "zear") parts.Add(BodyPart("zear", "z0001", "zer"));
        foreach (var item in ReadStartingEquipment(code, token).Values)
        {
            var index = parts.FindIndex(path => path.EndsWith($"_{item.Slot}.mdl", StringComparison.Ordinal));
            if (index >= 0) parts[index] = item.Path;
        }
        return parts;
    }

    public IReadOnlyList<GameCharacterPartOption> GetDefaultCharacterPartOptions(string code, CancellationToken token = default)
    {
        var options = ListCharacterPartOptions(code, token).ToLookup(o => o.Path);
        var startingItems = ReadStartingEquipment(code, token);
        return DefaultCharacterPaths(code, token).Select(path =>
        {
            var slot = CharacterPartSlot(code, path) ?? Path.GetFileNameWithoutExtension(path).Split('_').Last();
            if (startingItems.TryGetValue(path, out var starting)) return starting;
            foreach (var option in options[path].OrderBy(o => o.ImcSubset is null or 0 or 1 ? 0 : 1).ThenBy(o => o.RowId))
            {
                try
                {
                    if (CharacterPartMaterial(option, token) == 1) return option;
                }
                catch (Exception ex) when (ex is InvalidDataException or NotSupportedException) { }
            }
            return new GameCharacterPartOption(slot, path, Path.GetFileNameWithoutExtension(path));
        }).ToArray();
    }

    private readonly Dictionary<string, Dictionary<string, GameCharacterPartOption>> startingEquipment = new(StringComparer.Ordinal);

    private Dictionary<string, GameCharacterPartOption> ReadStartingEquipment(string code, CancellationToken token)
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            if (startingEquipment.TryGetValue(code, out var cached)) return cached;
            var equipment = new Dictionary<string, GameCharacterPartOption>(StringComparer.Ordinal);
            if (TryDescribeHumanBody(code, out var race, out var gender, out _) &&
                GetVerifiedRawSheet("Race", "3403807a") is { } races && races.TryGetRow(race, out var preset) &&
                GetVerifiedRawSheet("Item", "e9a33c9d") is { } items)
            {
                string[] slots = ["top", "glv", "dwn", "sho"];
                for (var i = 0; i < slots.Length; i++)
                {
                    var id = Convert.ToUInt32(preset.ReadColumn(2 + gender * 4 + i));
                    if (!items.TryGetRow(id, out var item) || ReadAnimationText(item, 9) is not { } name) continue;
                    var packed = Convert.ToUInt64(item.ReadColumn(47));
                    var path = ResolveWearableModelPath(code, (ushort)packed, slots[i]);
                    if (path != null && game.FileExists(path))
                        equipment[path] = new(slots[i], path, $"{name} · #{id}")
                        {
                            DyeCount = (byte)item.ReadColumn(28), RowId = id, IconId = Convert.ToUInt32(item.ReadColumn(10)), ImcSubset = (ushort)(packed >> 16)
                        };
                }
            }
            return startingEquipment[code] = equipment;
        }
    }

    public GameModelPreview ReadCharacterPreview(string code, int materialVariant = 1, CancellationToken token = default)
        => ReadCharacterPreview(code, ReadCharacterPalette(code, token)?.DefaultColors ?? GameCharacterColors.Neutral, materialVariant, token);

    public GameModelPreview ReadCharacterPreview(string code, GameCharacterColors colors, int materialVariant = 1,
        CancellationToken token = default)
        => ReadCharacterPreview(code, colors, [], materialVariant, token);

    public GameModelPreview ReadCharacterPreview(string code, GameCharacterColors colors,
        IReadOnlyList<GameCharacterPartOption> overrides, int materialVariant = 1, CancellationToken token = default,
        string? godbertDirectory = null, bool sheathed = false, bool useStartingEquipment = true)
    {
        var parts = !useStartingEquipment ? [] : DefaultCharacterPaths(code, token).Select(path => (Slot: CharacterPartSlot(code, path) ?? Path.GetFileNameWithoutExtension(path).Split('_').Last(), Path: path)).ToList();
        var variants = new Dictionary<string, (int Material, int? Mask)>(StringComparer.Ordinal);

        if (useStartingEquipment && materialVariant == 1)
        {
            var starting = ReadStartingEquipment(code, token);
            foreach (var (slot, path) in parts)
                if (starting.TryGetValue(path, out var option)) variants[slot] = CharacterPartVariant(option, token);
        }

        var stains = new Dictionary<string, (byte First, byte Second)>();
        foreach (var option in overrides)
        {
            token.ThrowIfCancellationRequested();
            if (!IsCharacterPartForBody(code, option))
                throw new ArgumentException("部件路径与体型或装备栏不匹配。", nameof(overrides));
            var index = parts.FindIndex(p => p.Slot == option.Slot);
            if (index >= 0) parts[index] = (option.Slot, option.Path);
            else parts.Add((option.Slot, option.Path));
            stains[option.Slot] = (option.DyeCount >= 1 ? option.Stain0Id : (byte)0, option.DyeCount >= 2 ? option.Stain1Id : (byte)0);
            variants[option.Slot] = CharacterPartVariant(option, token);
        }
        var materials = new List<GameModelMaterial>();
        var previousParts = Volatile.Read(ref previousCharacterParts);
        var currentParts = new Dictionary<CharacterPartCacheKey, GameModelPreview>();
        var meshes = new List<GameModelMesh>();
        var bones = new List<string>();
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        var textures = new Dictionary<string, GameTexture?>(StringComparer.Ordinal);
        var sharedMaterials = new Dictionary<(string Path, string Reference), GameModelMaterial>();
        GameTexture? Texture(string path)
        {
            if (!textures.TryGetValue(path, out var image))
            {
                try { image = ReadTexture(path); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or ArgumentException) { }
                textures.Add(path, image);
            }
            return image;
        }
        int Bone(string name)
        {
            var i = bones.IndexOf(name);
            if (i >= 0) return i;
            bones.Add(name);
            return bones.Count - 1;
        }

        foreach (var (slot, path) in parts)
        {
            token.ThrowIfCancellationRequested();
            var (variant, imcMask) = variants.GetValueOrDefault(slot, (path.StartsWith("chara/human/") ? 1 : materialVariant, null));
            var dye = stains.GetValueOrDefault(slot);
            GameModelMaterial Material(string reference)
                => ReadModelMaterial(path, reference, variant, token, Texture, colors, dye == default ? sharedMaterials : null, dye);
            var key = new CharacterPartCacheKey(code, path, variant, imcMask, colors, dye.First, dye.Second);
            if (!currentParts.TryGetValue(key, out var part) && !previousParts.TryGetValue(key, out part))
            {
                part = ReadModelPreviewCore(path, variant, true, token, materialReader: Material, imcMask: imcMask, hideOptionalFaceFeatures: path.Contains("/face/", StringComparison.Ordinal));
                if (AccessorySlots.Contains(slot) || EquipmentSlots.Contains(slot))
                {
                    var wearable = (AccessorySlots.Contains(slot) ? AccessoryPartPath : EquipmentPartPath).Match(path);
                    var sourceCode = wearable.Groups[2].ValueSpan;
                    if (!sourceCode.SequenceEqual(code))
                        part = DeformWearable(part, int.Parse(sourceCode[1..]), int.Parse(code.AsSpan(1)), token);
                }
            }
            currentParts[key] = part;
            if (slot is "weapon" or "offhand")
                part = ApplyWeaponIdlePose(part, godbertDirectory, sheathed, token, warnings);
            var face = path.Contains("/face/", StringComparison.Ordinal);
            var partBones = slot is "met" or "top"
                ? ResolveEquipmentBoneNames(code, slot, path, part.BoneNames, godbertDirectory, token, warnings)
                : part.BoneNames.Select(name => face ? "face/" + name : name);
            var map = partBones.Select(Bone).ToArray();
            var offset = materials.Count;
            materials.AddRange(part.Materials);
            warnings.UnionWith(part.Warnings);
            var headAttachment = path.Contains("/hair/") || path.Contains("/zear/");
            var attachment = slot switch { "weapon" => sheathed ? "j_kosi" : "n_buki_r", "offhand" => sheathed ? "j_sebo_b" : "n_buki_l", _ => null };
            var offsetTransform = System.Numerics.Matrix4x4.Identity;
            if (attachment != null)
            {
                if (ReadWeaponAttachment(code, path, sheathed) is { } placement)
                    (attachment, offsetTransform) = placement;
                else
                    warnings.Add($"找不到武器挂点偏移，使用参考骨 {attachment}。");
            }
            var weaponGrip = attachment == null ? (System.Numerics.Matrix4x4?)null
                : offsetTransform * ReadWeaponGrip(code, attachment, godbertDirectory, token);
            foreach (var mesh in part.Meshes)
            {
                int[][] indices;
                var weights = mesh.BoneWeights;
                var positions = mesh.Positions;
                var normals = mesh.Normals;
                if (path.StartsWith("chara/weapon/", StringComparison.Ordinal) && weaponGrip is { } grip)
                {
                    var bone = new[] { Bone(attachment!) };
                    indices = Enumerable.Repeat(bone, positions.Length).ToArray();
                    weights = Enumerable.Repeat(new[] { 1f }, positions.Length).ToArray();
                    positions = positions.Select(p => System.Numerics.Vector3.Transform(p, grip)).ToArray();
                    normals = normals.Select(n => n.LengthSquared() > 0 ? System.Numerics.Vector3.Normalize(System.Numerics.Vector3.TransformNormal(n, grip)) : n).ToArray();
                }
                else if (headAttachment || face && mesh.BoneWeights.Length != mesh.Positions.Length)
                {
                    // 脸部和头发有独立的附加骨架。基础动作预览将整个部件挂在头骨上。
                    var head = new[] { Bone("j_kao") };
                    indices = Enumerable.Repeat(head, mesh.Positions.Length).ToArray();
                    weights = Enumerable.Repeat(new[] { 1f }, mesh.Positions.Length).ToArray();
                }
                else
                    indices = mesh.BoneIndices.Select(vertex => vertex.Select(index => map[index]).ToArray()).ToArray();
                meshes.Add(mesh with { Positions = positions, Normals = normals, MaterialIndex = mesh.MaterialIndex + offset, BoneIndices = indices, BoneWeights = weights });
            }
        }
        warnings.Add("同体型组合；头发与独立耳部件随头骨运动。");
        token.ThrowIfCancellationRequested();
        Volatile.Write(ref previousCharacterParts, currentParts);
        return new("character:" + code, meshes, materials, bones, warnings.ToArray()) { FaceModelPath = parts.First(p => p.Slot == "fac").Path };
    }

    private bool IsCharacterPartForBody(string code, GameCharacterPartOption option)
    {
        if (CharacterPartSlot(code, option.Path) == (option.Slot == "offhand" ? "weapon" : option.Slot)) return true;
        if (option.Slot is "zer" or "til")
        {
            var ear = option.Slot == "zer";
            var file = Path.GetFileNameWithoutExtension(option.Path);
            if (file.Length != 14 || !int.TryParse(file.AsSpan(6, 4), out var id)) return false;
            var part = $"{(ear ? 'z' : 't')}{id:D4}";
            return option.Path == $"chara/human/{code}/obj/{(ear ? "zear" : "tail")}/{part}/model/{code}{part}_{option.Slot}.mdl";
        }
        var wearable = (AccessorySlots.Contains(option.Slot) ? AccessoryPartPath : EquipmentPartPath).Match(option.Path);
        return wearable.Success && wearable.Groups[3].Value == option.Slot &&
            int.TryParse(wearable.Groups[1].ValueSpan[1..], out var model) &&
            string.Equals(ResolveWearableModelPath(code, model, option.Slot), option.Path, StringComparison.Ordinal);
    }

    private GameModelPreview ApplyWeaponIdlePose(GameModelPreview part, string? decoder, bool sheathed,
        CancellationToken token, HashSet<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(decoder)) return part;
        var owner = AnimationOwner(part.Path);
        var pap = owner + "/animation/a0001/wp_common/resident/weapon.pap";
        lock (gate) if (!game.FileExists(pap)) return part;
        try
        {
            var slots = ReadAnimationMetadata(pap, token).Slots;
            var pair = slots.Where(s => s.Name.EndsWith("bw_id0", StringComparison.Ordinal))
                .Select(battle => (Battle: battle, Normal: slots.FirstOrDefault(s => s.Name == battle.Name[..^6] + "nw_id0")))
                .FirstOrDefault(p => p.Normal != null);
            if (pair.Normal == null) return part;
            var slot = sheathed ? pair.Normal : pair.Battle;
            var clip = BakeModelAnimationAsync(part.Path, pap, slot.Index, decoder, token, samplingRate: 60)
                .GetAwaiter().GetResult();
            var positions = SkinModelFrame(part, clip, 0);
            var boneMap = part.BoneNames.Select(name => Array.IndexOf(clip.BoneNames, name)).ToArray();
            var meshes = part.Meshes.Select((mesh, i) =>
            {
                var normals = mesh.Normals.ToArray();
                for (var v = 0; v < normals.Length; v++)
                {
                    var transformed = System.Numerics.Vector3.Zero;
                    for (var j = 0; j < mesh.BoneWeights[v].Length; j++)
                    {
                        var weight = mesh.BoneWeights[v][j];
                        if (weight <= 0) continue;
                        var matrix = clip.Frames[0][boneMap[mesh.BoneIndices[v][j]]];
                        transformed += System.Numerics.Vector3.TransformNormal(normals[v], matrix) * weight;
                    }
                    if (transformed.LengthSquared() > 0) normals[v] = System.Numerics.Vector3.Normalize(transformed);
                }
                return mesh with { Positions = positions[i], Normals = normals };
            }).ToArray();
            return part with { Meshes = meshes };
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or InvalidDataException or InvalidOperationException or TimeoutException)
        {
            warnings.Add($"武器形态使用模型绑定姿势：{ex.Message}");
            return part;
        }
    }

}
