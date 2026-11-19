namespace FFXIVPatchParser;

public sealed partial class LocalGameSource
{
    private readonly Dictionary<(int Body, bool Accessory), byte[]?> wearableDeformerTables = new();

    private string? ResolveWearableModelPath(string code, int model, string slot)
    {
        var accessory = AccessorySlots.Contains(slot);
        var index = Array.IndexOf(accessory ? AccessorySlots : EquipmentSlots, slot);
        if (index < 0 || !int.TryParse(code.AsSpan(1), out var race)) return null;
        lock (gate)
        {
            var visited = new HashSet<int>();
            for (int? body = race; body.HasValue && visited.Add(body.Value); body = GetBodyDeformerParent(body.Value))
            {
                if (!wearableDeformerTables.TryGetValue((body.Value, accessory), out var data))
                {
                    var table = accessory ? "accessorydeformerparameter" : "equipmentdeformerparameter";
                    data = game.GetFile($"chara/xls/charadb/{table}/c{body.Value:D4}.eqdp")?.Data;
                    wearableDeformerTables[(body.Value, accessory)] = data;
                }
                if (data == null || data.Length < 6) continue;
                var blockSize = BitConverter.ToUInt16(data, 2);
                var blockCount = BitConverter.ToUInt16(data, 4);
                if (blockSize == 0 || model / blockSize >= blockCount || 6 + 2 * blockCount > data.Length) continue;
                var block = BitConverter.ToUInt16(data, 6 + 2 * (model / blockSize));
                if (block == ushort.MaxValue) continue;
                var offset = 6 + 2 * (blockCount + block + model % blockSize);
                if (offset + 2 > data.Length) continue;
                if ((BitConverter.ToUInt16(data, offset) & (2 << (index * 2))) != 0)
                    return accessory ? $"chara/accessory/a{model:D4}/model/c{body.Value:D4}a{model:D4}_{slot}.mdl"
                        : $"chara/equipment/e{model:D4}/model/c{body.Value:D4}e{model:D4}_{slot}.mdl";
            }
        }
        return null;
    }
}
