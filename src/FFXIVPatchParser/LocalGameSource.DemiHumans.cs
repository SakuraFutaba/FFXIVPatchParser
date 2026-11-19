namespace FFXIVPatchParser;

public sealed partial class LocalGameSource
{
    public GameModelPreview ReadDemiHumanPreview(NamedModelEntry entry, CancellationToken token = default)
    {
        if (entry.ModelType != 2) throw new ArgumentException("所选条目不是亚人。", nameof(entry));
        var meshes = new List<GameModelMesh>();
        var materials = new List<GameModelMaterial>();
        var bones = new List<string>();
        var boneIds = new Dictionary<string, int>(StringComparer.Ordinal);
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        foreach (var slot in EquipmentSlots)
        {
            token.ThrowIfCancellationRequested();
            var stem = $"d{entry.ModelId:D4}";
            var equipment = $"e{entry.BodyId:D4}";
            var path = $"chara/demihuman/{stem}/obj/equipment/{equipment}/model/{stem}{equipment}_{slot}.mdl";
            if (!game.FileExists(path)) continue;
            var change = ReadModelImcChange(path, entry.Variant, true, token, skipMissingSlot: true);
            if (change?.MaterialId == 0) continue;
            var part = ReadModelPreview(path, change?.MaterialId ?? Math.Max(1, entry.Variant), token,
                change is { } imc ? imc.AttributeMask & 0x3ff : null);
            var map = new int[part.BoneNames.Count];
            for (int i = 0; i < map.Length; i++)
            {
                var name = part.BoneNames[i];
                if (!boneIds.TryGetValue(name, out var id))
                {
                    id = bones.Count;
                    bones.Add(name);
                    boneIds.Add(name, id);
                }
                map[i] = id;
            }
            foreach (var mesh in part.Meshes)
                meshes.Add(mesh with { MaterialIndex = mesh.MaterialIndex + materials.Count,
                    BoneIndices = mesh.BoneIndices.Select(vertex => vertex.Select(index => map[index]).ToArray()).ToArray() });
            materials.AddRange(part.Materials);
            warnings.UnionWith(part.Warnings);
        }
        return new(entry.Path, meshes, materials, bones, warnings.ToArray());
    }
}
