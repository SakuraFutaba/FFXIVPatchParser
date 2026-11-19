namespace FFXIVPatchParser;

public sealed partial class LocalGameSource
{
    private readonly record struct AppearancePartKey(string Slot, string Folder, int? Subset);
    private ILookup<AppearancePartKey, GameCharacterPartOption>? npcItemNames;

    public IReadOnlyList<GameCharacterPartOption> DescribeCharacterNpcEquipment(GameCharacterNpcPreset preset, CancellationToken token = default)
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            npcItemNames ??= ListStandaloneItemParts(token).ToLookup(NpcPartKey);
            return preset.Parts.Select(part =>
            {
                if (part.ImcSubset == null) return part;
                var key = NpcPartKey(part);
                var matches = npcItemNames[key].DistinctBy(candidate => candidate.RowId).Take(2).ToArray();
                if (matches is [var item])
                    return part with { Label = item.Label, RowId = item.RowId, IconId = item.IconId };

                var pieces = key.Folder.Split('/');
                var model = key.Slot is "weapon" or "offhand" ? pieces[2] + pieces[^1] : pieces[^1];
                return part with { Label = $"{model} · IMC {key.Subset}" };
            }).ToArray();
        }
    }

    private static AppearancePartKey NpcPartKey(GameCharacterPartOption part)
    {
        var modelIndex = part.Path.LastIndexOf("/model/", StringComparison.Ordinal);
        var folder = modelIndex < 0 ? part.Path : part.Path[..modelIndex];
        return new(part.Slot == "ril" ? "rir" : part.Slot, folder, part.ImcSubset);
    }
}
