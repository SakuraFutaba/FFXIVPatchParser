using System.Globalization;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;

namespace FFXIVPatchParser;

public sealed record GameMapEffectBinding(uint LayoutId, string SharedGroupPath, string Layer, IReadOnlyList<RowKey> SourceRows);

public sealed partial class LocalGameSource
{
    /// <summary>匹配管理表与当前布局的共享组；不推断状态或播放时间线。</summary>
    public IReadOnlyList<GameMapEffectBinding> ReadMapEffectBindings(string scenePath, CancellationToken token = default)
    {
        var managed = new Dictionary<uint, List<RowKey>>();
        var offset = 0;
        for (var pageIndex = 0; ; pageIndex++)
        {
            token.ThrowIfCancellationRequested();
            if (pageIndex >= 100) throw new InvalidDataException("MapEffect 管理表超过读取上限。");
            var page = ReadSheetPage("ContentDirectorManagedSG", offset, 1000, token);
            foreach (var (key, cells) in page.Rows)
            {
                if (cells.Length == 0 || !uint.TryParse(cells[0].Display, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id == 0) continue;
                if (!managed.TryGetValue(id, out var rows)) managed[id] = rows = [];
                rows.Add(key);
            }
            if (!page.HasMore) break;
            if (page.NextOffset <= offset) throw new InvalidDataException("MapEffect 管理表分页未前进。");
            offset = page.NextOffset;
        }
        scenePath = scenePath.Replace('\\', '/');
        if (!scenePath.StartsWith("bg/", StringComparison.OrdinalIgnoreCase)) scenePath = "bg/" + scenePath;
        var level = scenePath.IndexOf("/level/", StringComparison.OrdinalIgnoreCase);
        if (level < 0) throw new InvalidDataException("场景路径缺少 level 目录。");
        var root = scenePath[..(level + 7)];
        var result = new List<GameMapEffectBinding>();
        var count = 0;
        foreach (var name in new[] { "bg", "planmap", "planevent" })
        {
            LgbFile? file;
            lock (gate) { token.ThrowIfCancellationRequested(); file = game.GetFile<LgbFile>(root + name + ".lgb"); }
            if (file is null) continue;
            foreach (var layer in file.Layers)
            foreach (var item in layer.InstanceObjects)
            {
                token.ThrowIfCancellationRequested();
                if (++count > 100_000) throw new InvalidDataException("MapEffect 布局实例超过读取上限。");
                if (item.Object is LayerCommon.SharedGroupInstanceObject group && managed.TryGetValue(item.InstanceId, out var rows))
                    result.Add(new(item.InstanceId, group.AssetPath, name + "/" + layer.Name, rows.ToArray()));
            }
        }
        return result.OrderBy(item => item.SourceRows[0]).ThenBy(item => item.LayoutId).ToArray();
    }
}
