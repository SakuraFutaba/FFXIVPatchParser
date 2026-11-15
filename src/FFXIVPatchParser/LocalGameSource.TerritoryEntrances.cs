using System.Numerics;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;

namespace FFXIVPatchParser;

public sealed partial class LocalGameSource
{
    /// <summary>返回绑定入口标记的事件对象位置；多个结果表示入口存在歧义，由调用方决定是否采用。</summary>
    public IReadOnlyList<Vector3> ReadTerritoryEntrancePositions(string bgPath, CancellationToken token = default)
    {
        bgPath = bgPath.Trim().Replace('\\', '/').TrimStart('/');
        if (!bgPath.StartsWith("bg/", StringComparison.OrdinalIgnoreCase)) bgPath = "bg/" + bgPath;
        var level = bgPath.IndexOf("/level/", StringComparison.OrdinalIgnoreCase);
        if (level < 0) throw new ArgumentException("场景路径需要包含 /level/。", nameof(bgPath));
        LgbFile? file;
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            file = game.GetFile<LgbFile>(bgPath[..(level + 7)] + "planmap.lgb");
        }
        if (file == null) return [];
        const string marker = "bgcommon/world/lvd/shared/for_vfx/sgvf_w_lvd_b0095.sgb";
        var objects = file.Layers.SelectMany(layer => layer.InstanceObjects).ToArray();
        var markerIds = new HashSet<uint>();
        foreach (var item in objects)
        {
            token.ThrowIfCancellationRequested();
            if (item.Object is LayerCommon.SharedGroupInstanceObject shared &&
                string.Equals(shared.AssetPath, marker, StringComparison.OrdinalIgnoreCase))
                markerIds.Add(item.InstanceId);
        }
        var positions = new HashSet<Vector3>();
        foreach (var item in objects)
        {
            token.ThrowIfCancellationRequested();
            if (item.Object is not LayerCommon.EventInstanceObject eobj || !markerIds.Contains(eobj.BoundInstanceId)) continue;
            var p = item.Transform.Translation;
            var position = new Vector3(p.X, p.Y, p.Z);
            if (float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z)) positions.Add(position);
        }
        return positions.OrderBy(p => p.X).ThenBy(p => p.Y).ThenBy(p => p.Z).ToArray();
    }
}

