using Lumina.Data.Files;

namespace FFXIVPatchParser;

public sealed partial class LocalGameSource
{
    /// <summary>Resolve the ModelChara IMC subset to its material folder on selection.
    /// If no IMC exists, preserve the existing preview convention: v{Variant}, at least v0001.
    /// Missing/disabled parts and invalid subset indices are errors, not guessed material folders.</summary>
    public int ResolveNamedModelMaterialVariant(NamedModelEntry entry, CancellationToken token = default)
        => ResolveModelMaterialSubset(entry.Path, entry.Variant, entry.ModelType == 2, token);

    private (int Material, int? Mask) CharacterPartVariant(GameCharacterPartOption option, CancellationToken token)
        => option.ImcSubset is { } subset
            ? ResolveModelSubset(option.Path, subset, option.Slot is not ("weapon" or "offhand"), token)
            : (option.MaterialVariant, null);

    private int ResolveModelMaterialSubset(string path, int subset, bool equipment, CancellationToken token)
        => ResolveModelSubset(path, subset, equipment, token).Material;

    private (int Material, int? Mask) ResolveModelSubset(string path, int subset, bool equipment, CancellationToken token)
    {
        var change = ReadModelImcChange(path, subset, equipment, token);
        if (change is null) return (Math.Max(1, subset), null);
        if (change.Value.MaterialId == 0) throw new NotSupportedException("IMC 指定隐藏部位（材质编号为0）。");
        return (change.Value.MaterialId, change.Value.AttributeMask & 0x3ff);
    }

    private ImcFile.ImageChangeData? ReadModelImcChange(string path, int subset, bool equipment, CancellationToken token, bool skipMissingSlot = false)
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            var modelDirectory = path.LastIndexOf("/model/", StringComparison.Ordinal);
            if (modelDirectory < 0) throw new ArgumentException("模型路径没有 model 目录。", nameof(path));
            var root = path[..modelDirectory];
            var stem = root[(root.LastIndexOf('/') + 1)..];
            var imcPath = root + "/" + stem + ".imc";
            var imc = game.GetFile<ImcFile>(imcPath);
            if (imc is null) return null;
            var slot = 0;
            if (equipment)
            {
                var suffix = Path.GetFileNameWithoutExtension(path).Split('_').Last();
                slot = Array.IndexOf(path.StartsWith("chara/accessory/") ? AccessorySlots : EquipmentSlots, suffix);
                if (slot < 0) throw new InvalidDataException("无法识别模型的装备部位。");
            }
            if ((imc.PartMask & (1 << slot)) == 0)
            {
                if (skipMissingSlot) return new ImcFile.ImageChangeData();
                throw new InvalidDataException("IMC 不含所选模型部位。");
            }
            var part = System.Numerics.BitOperations.PopCount((uint)(imc.PartMask & ((1 << slot) - 1)));
            if (subset < 0 || subset > imc.Count) throw new InvalidDataException("模型变体超过 IMC 范围。");
            return subset == 0 ? imc.GetDefaultVariant(part) : imc.GetVariant(part, subset - 1);
        }
    }
}
