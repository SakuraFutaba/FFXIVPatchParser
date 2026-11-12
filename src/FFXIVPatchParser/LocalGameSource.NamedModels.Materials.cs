using Lumina.Data.Files;

namespace FFXIVPatchParser;

public sealed partial class LocalGameSource
{
    /// <summary>Resolve the ModelChara IMC subset to its material folder on selection.
    /// If no IMC exists, preserve the existing preview convention: v{Variant}, at least v0001.
    /// Missing/disabled parts and invalid subset indices are errors, not guessed material folders.</summary>
    public int ResolveNamedModelMaterialVariant(NamedModelEntry entry, CancellationToken token = default)
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            var modelDirectory = entry.Path.LastIndexOf("/model/", StringComparison.Ordinal);
            if (modelDirectory < 0) throw new ArgumentException("模型路径没有 model 目录。", nameof(entry));
            var root = entry.Path[..modelDirectory];
            var stem = root[(root.LastIndexOf('/') + 1)..];
            var imcPath = root + "/" + stem + ".imc";
            var imc = game.GetFile<ImcFile>(imcPath);
            if (imc is null) return Math.Max(1, entry.Variant);
            var slot = 0;
            if (entry.ModelType == 2)
            {
                var suffix = Path.GetFileNameWithoutExtension(entry.Path).Split('_').Last();
                slot = Array.IndexOf(EquipmentSlots, suffix);
                if (slot < 0) throw new InvalidDataException("无法识别亚人模型的装备部位。");
            }
            if ((imc.PartMask & (1 << slot)) == 0) throw new InvalidDataException("IMC 不含所选模型部位。");
            var part = System.Numerics.BitOperations.PopCount((uint)(imc.PartMask & ((1 << slot) - 1)));
            if (entry.Variant < 0 || entry.Variant > imc.Count) throw new InvalidDataException("ModelChara 变体超过 IMC 范围。");
            var change = entry.Variant == 0 ? imc.GetDefaultVariant(part) : imc.GetVariant(part, entry.Variant - 1);
            if (change.MaterialId == 0) throw new NotSupportedException("IMC 指定隐藏部位（材质编号为0）。");
            return change.MaterialId;
        }
    }
}
