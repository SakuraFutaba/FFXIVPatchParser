namespace FFXIVPatchParser;

public sealed partial class LocalGameSource
{
    private readonly Dictionary<string, Dictionary<(int Race, int Model), ushort>> equipmentSkeletonTables = new();
    private readonly Dictionary<(string Path, string Decoder), Dictionary<string, string>> equipmentSkeletonChains = new();

    private IEnumerable<string> ResolveEquipmentBoneNames(string code, string slot, string modelPath,
        IReadOnlyList<string> bones, string? decoder, CancellationToken token, ISet<string> warnings)
    {
        var fallback = slot == "met" ? "独立头饰骨随头部运动" : "未能绑定的独立上衣骨保持参考姿势";
        var hasExtra = bones.Any(name => name.StartsWith($"j_ex_{slot}", StringComparison.Ordinal));
        Dictionary<string, string>? chains = null;
        try
        {
            chains = ReadEquipmentBoneChains(code, slot, modelPath, decoder, token);
            if (hasExtra && chains == null)
                warnings.Add($"{Path.GetFileName(modelPath)} 缺少可用的附加骨架或解码器，{fallback}。");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException
            or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception or BadImageFormatException
            or ArgumentException or OverflowException)
        {
            token.ThrowIfCancellationRequested();
            var reason = ex switch
            {
                TimeoutException => "解码超时",
                FileNotFoundException => "缺少资源",
                UnauthorizedAccessException => "没有读取权限",
                InvalidDataException or ArgumentException or OverflowException => "数据无效",
                IOException => "读取失败",
                _ => "解码失败"
            };
            warnings.Add($"{Path.GetFileName(modelPath)} 附加骨架{reason}，{fallback}。");
        }
        return bones.Select(name => chains != null && chains.TryGetValue(name, out var chain)
            ? slot + "/" + chain : slot == "met" || name.StartsWith("j_ex_top", StringComparison.Ordinal) ? slot + "/" + name : name);
    }

    private Dictionary<string, string>? ReadEquipmentBoneChains(string code, string slot, string modelPath,
        string? decoder, CancellationToken token)
    {
        Dictionary<string, string>? chains = null;
        var match = EquipmentPartPath.Match(modelPath);
        if (!string.IsNullOrWhiteSpace(decoder) && File.Exists(Path.Combine(decoder, "hkAnimationInterop.dll")) && match.Success &&
            int.TryParse(match.Groups[1].Value.AsSpan(1), out var model) && int.TryParse(code.AsSpan(1), out var race))
        {
            var path = FindEquipmentSkeleton(slot, race, model);
            if (path != null)
            {
                var key = (path, Path.GetFullPath(decoder));
                lock (gate) equipmentSkeletonChains.TryGetValue(key, out chains);
                if (chains == null)
                {
                    var clip = Task.Run(() => BakeModelAnimationCoreAsync("character:" + code, "", 0, decoder, token, 1,
                        referenceOnly: true, referenceSkeletonPath: path), token).GetAwaiter().GetResult();
                    chains = new(StringComparer.Ordinal);
                    for (var i = 0; i < clip.BoneNames.Length; i++)
                    {
                        var ancestors = new List<string>();
                        for (var b = i; b >= 0; b = clip.ReferenceParents![b]) ancestors.Add(clip.BoneNames[b]);
                        chains.Add(clip.BoneNames[i], string.Join('/', ancestors));
                    }
                    lock (gate) equipmentSkeletonChains[key] = chains;
                }
            }
        }
        return chains;
    }

    private string? FindEquipmentSkeleton(string slot, int race, int model)
    {
        lock (gate)
        {
            if (!equipmentSkeletonTables.TryGetValue(slot, out var table))
            {
                table = new();
                var raw = game.GetFile($"chara/xls/charadb/extra_{slot}.est")?.Data;
                if (raw != null)
                {
                    if (raw.Length < 4 || 4L + BitConverter.ToUInt32(raw) * 6L != raw.Length)
                        throw new InvalidDataException("装备附加骨架 EST 表长度无效。");
                    var count = (raw.Length - 4) / 6;
                    for (var i = 0; i < count; i++)
                        table.Add((BitConverter.ToUInt16(raw, 6 + 4 * i), BitConverter.ToUInt16(raw, 4 + 4 * i)),
                            BitConverter.ToUInt16(raw, 4 + count * 4 + 2 * i));
                }
                equipmentSkeletonTables.Add(slot, table);
            }
            var visited = new HashSet<int>();
            for (int? current = race; current.HasValue && visited.Add(current.Value); current = GetBodyDeformerParent(current.Value))
            {
                var skeleton = table.GetValueOrDefault((current.Value, model));
                if (skeleton == 0) continue;
                var id = $"c{current.Value:D4}";
                var part = $"{slot[0]}{skeleton:D4}";
                return $"chara/human/{id}/skeleton/{slot}/{part}/skl_{id}{part}.sklb";
            }
            return null;
        }
    }
}
