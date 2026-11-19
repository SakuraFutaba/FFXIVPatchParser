using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;

namespace FFXIVPatchParser;

public sealed partial class LocalGameSource
{
    public IReadOnlyDictionary<string, string> ListCatalogPartNames(IEnumerable<string> paths, CancellationToken token = default)
    {
        lock (gate)
        {
            var requested = paths.Where(p => p.StartsWith("bgcommon/hou/", StringComparison.Ordinal)
                || p.Contains("/obj/hair/", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (requested.Count == 0) return result;
            var items = GetVerifiedRawSheet("Item", "e9a33c9d");
            if (items == null) return result;

            var books = new Dictionary<uint, string>();
            foreach (var row in ReadVerifiedAnimationSheet("CharaMakeCustomize", "c30e9b73", token))
            {
                if (!(bool)row.ReadColumn(3) || !items.TryGetRow((uint)row.ReadColumn(5), out var item)) continue;
                var name = Cell(item.ReadColumn(9)).Display;
                if (name.StartsWith("发型样式", StringComparison.Ordinal)) books[(uint)row.ReadColumn(1)] = name;
            }
            foreach (var path in requested)
            {
                token.ThrowIfCancellationRequested();
                var hair = HairPartPath.Match(path);
                if (hair.Success && books.TryGetValue(FindHairstyleIcon(hair.Groups[1].Value, path, null, token), out var book))
                    result[path] = book;
            }
            var folders = requested.Where(p => p.StartsWith("bgcommon/hou/", StringComparison.Ordinal))
                .Select(p => p.Split('/')).Where(parts => parts.Length == 7 && parts[3] == "general")
                .Select(parts => string.Join("/", parts.Take(5))).ToHashSet(StringComparer.Ordinal);
            foreach (var (sheet, hash, folder, prefix, column) in new[] {
                ("HousingFurniture", "ccfbe5ff", "indoor", "fun", 7), ("HousingYardObject", "a0acf7ca", "outdoor", "gar", 6) })
            {
                foreach (var row in ReadVerifiedAnimationSheet(sheet, hash, token))
                {
                    var id = (ushort)row.ReadColumn(0);
                    var root = $"bgcommon/hou/{folder}/general/{id:D4}";
                    if (!folders.Contains(root) || !items.TryGetRow((uint)row.ReadColumn(column), out var item)) continue;
                    var name = Cell(item.ReadColumn(9)).Display;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    try
                    {
                        var sgb = game.GetFile<SgbFile>($"{root}/asset/{prefix}_b0_m{id:D4}.sgb");
                        if (sgb == null) continue;
                        foreach (var layer in sgb.LayerGroups.SelectMany(group => group.Layers))
                        foreach (var obj in layer.InstanceObjects)
                            if (obj.Object is LayerCommon.BGInstanceObject bg && requested.Contains(bg.AssetPath)
                                && bg.AssetPath.StartsWith(root + "/", StringComparison.Ordinal))
                                result[bg.AssetPath] = name;
                    }
                    catch (Exception ex) when (IsTerritoryReadFailure(ex)) { continue; }
                }
            }
            return result;
        }
    }
}
