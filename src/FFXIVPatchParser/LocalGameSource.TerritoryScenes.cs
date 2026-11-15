using System.Numerics;
using Lumina.Data.Files;
using Lumina.Data.Parsing;
using Lumina.Data.Parsing.Layer;

namespace FFXIVPatchParser;

public sealed record GameTerritoryInstance(string ModelPath, string Layer, Matrix4x4 Transform, bool IsTerrain);
public sealed record GameTerritoryLayout(string BasePath, IReadOnlyList<GameTerritoryInstance> Instances, IReadOnlyList<string> Warnings);
public sealed record GameTerritoryLoadOptions
{
    public int MaxUniqueModels { get; init; } = 1024;
    public int MaxInstances { get; init; } = 6000;
    public long MaxTriangles { get; init; } = 2_000_000;
    public int BatchSize { get; init; } = 32;
    public bool TerrainOnly { get; init; }
    public bool IncludeTextures { get; init; } = true;
    public int TextureMaxDimension { get; init; } = 256;
    public int PreviewLod { get; init; } = 1;
}
public sealed record GameTerritoryGeometryBatch(
    IReadOnlyDictionary<string, IReadOnlyList<GameModelMesh>> Models,
    IReadOnlyList<GameTerritoryInstance> Instances, int LoadedInstances, int TotalInstances)
{
    public IReadOnlyDictionary<string, IReadOnlyList<GameModelMaterial>> Materials { get; init; }
        = new Dictionary<string, IReadOnlyList<GameModelMaterial>>();
}
public sealed record GameTerritoryGeometry(
    IReadOnlyDictionary<string, IReadOnlyList<GameModelMesh>> Models,
    IReadOnlyList<GameTerritoryInstance> Instances, IReadOnlyList<string> Warnings,
    long TriangleCount, bool IsTruncated)
{
    public IReadOnlyDictionary<string, IReadOnlyList<GameModelMaterial>> Materials { get; init; }
        = new Dictionary<string, IReadOnlyList<GameModelMaterial>>();
}

public sealed partial class LocalGameSource
{
    /// <summary>只读取布局与地形清单；模型和纹理在此阶段均不解码。</summary>
    public GameTerritoryLayout ReadTerritoryLayout(string bgPath, CancellationToken token = default)
    {
        bgPath = bgPath.Trim().Replace('\\', '/').TrimStart('/');
        if (!bgPath.StartsWith("bg/", StringComparison.OrdinalIgnoreCase)) bgPath = "bg/" + bgPath;
        var level = bgPath.IndexOf("/level/", StringComparison.OrdinalIgnoreCase);
        if (level < 0) throw new ArgumentException("场景路径需要包含 /level/。", nameof(bgPath));
        var basePath = bgPath[..(level + 1)];
        var instances = new List<GameTerritoryInstance>();
        var warnings = new List<string>();
        var shared = new Dictionary<string, LayerCommon.Layer[]>(StringComparer.OrdinalIgnoreCase);
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        const int maxLayoutInstances = 100_000;
        const int maxSharedFiles = 4096;
        void Warn(string text) { if (warnings.Count < 100 && !warnings.Contains(text)) warnings.Add(text); }

        try
        {
            TeraFile? terrain;
            lock (gate)
            {
                token.ThrowIfCancellationRequested();
                terrain = game.GetFile<TeraFile>(basePath + "bgplate/terrain.tera");
            }
            if (terrain != null)
            {
                if (terrain.PlateCount > 10000) throw new InvalidDataException("地形片数量超过预览上限。");
                for (var i = 0; i < terrain.PlateCount; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var pos = terrain.GetPlatePosition(i);
                    instances.Add(new(basePath + $"bgplate/{i:D4}.mdl", "地形", Matrix4x4.CreateTranslation(pos.X, 0, pos.Y), true));
                }
            }
        }
        catch (Exception ex) when (IsTerritoryReadFailure(ex)) { Warn("地形清单读取失败：" + ex.Message); }

        void AddLayers(IEnumerable<LayerCommon.Layer> layers, Matrix4x4 parent, string source, int depth)
        {
            foreach (var layer in layers)
            {
                // 节庆层依赖服务器状态，默认排除，避免多个节庆同时叠在一起。
                if (layer.FestivalID != 0) continue;
                foreach (var item in layer.InstanceObjects)
                {
                    token.ThrowIfCancellationRequested();
                    if (instances.Count >= maxLayoutInstances) { Warn("场景布局超过 10 万个实例，已截断。"); return; }
                    var transform = TerritoryTransform(item.Transform) * parent;
                    if (!IsFiniteTerritoryTransform(transform)) { Warn("跳过含无效坐标的场景实例。"); continue; }
                    // 国服 7.58 的有效背景模型也使用 IsVisible=0，不能据此剔除。
                    if (item.Object is LayerCommon.BGInstanceObject model &&
                        model.AssetPath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
                        instances.Add(new(model.AssetPath, source + "/" + layer.Name, transform, false));
                    else if (item.Object is LayerCommon.SharedGroupInstanceObject group &&
                             group.AssetPath.EndsWith(".sgb", StringComparison.OrdinalIgnoreCase))
                    {
                        if (depth >= 16 || !active.Add(group.AssetPath)) { Warn("跳过递归过深或循环引用的 SGB。"); continue; }
                        try
                        {
                            if (!shared.TryGetValue(group.AssetPath, out var children))
                            {
                                if (shared.Count >= maxSharedFiles) { Warn("共享场景文件超过预览上限。"); continue; }
                                SgbFile? sgb;
                                lock (gate)
                                {
                                    token.ThrowIfCancellationRequested();
                                    sgb = game.GetFile<SgbFile>(group.AssetPath);
                                }
                                children = sgb?.LayerGroups.SelectMany(g => g.Layers).ToArray() ?? [];
                                shared[group.AssetPath] = children;
                                if (sgb == null) Warn("缺少共享场景：" + group.AssetPath);
                            }
                            AddLayers(children, transform, source + "/" + layer.Name, depth + 1);
                        }
                        catch (Exception ex) when (IsTerritoryReadFailure(ex))
                        {
                            shared[group.AssetPath] = [];
                            Warn("共享场景读取失败：" + group.AssetPath + "：" + ex.Message);
                        }
                        finally { active.Remove(group.AssetPath); }
                    }
                }
            }
        }

        foreach (var fileName in new[] { "bg", "planmap", "planevent" })
        {
            try
            {
                LgbFile? file;
                lock (gate)
                {
                    token.ThrowIfCancellationRequested();
                    file = game.GetFile<LgbFile>(basePath + "level/" + fileName + ".lgb");
                }
                if (file != null) AddLayers(file.Layers, Matrix4x4.Identity, fileName, 0);
            }
            catch (Exception ex) when (IsTerritoryReadFailure(ex)) { Warn(fileName + ".lgb 读取失败：" + ex.Message); }
        }
        if (instances.Count == 0) Warn("此场景没有可预览的地形或背景模型。");
        Warn("显示静态场景；未应用任务阶段、服务器显隐、天气、灯光、特效及共享组动画。");
        return new(basePath, instances, warnings);
    }

    /// <summary>按模型路径去重，先加载地形；共享低分辨率纹理，每批只传递新增模型、材质与实例。</summary>
    public GameTerritoryGeometry LoadTerritoryGeometry(GameTerritoryLayout layout,
        GameTerritoryLoadOptions? options = null, IProgress<GameTerritoryGeometryBatch>? progress = null,
        CancellationToken token = default)
    {
        options ??= new();
        if (options.MaxUniqueModels is < 1 or > 10000 || options.MaxInstances is < 1 or > 100000 ||
            options.MaxTriangles is < 1 or > 50_000_000 || options.BatchSize is < 1 or > 1000 ||
            options.PreviewLod is < 0 or > 2 || options.TextureMaxDimension is < 32 or > 2048)
            throw new ArgumentOutOfRangeException(nameof(options));
        var models = new Dictionary<string, IReadOnlyList<GameModelMesh>>(StringComparer.OrdinalIgnoreCase);
        var materials = new Dictionary<string, IReadOnlyList<GameModelMaterial>>(StringComparer.OrdinalIgnoreCase);
        var materialCache = new Dictionary<string, GameModelMaterial>(StringComparer.OrdinalIgnoreCase);
        var textureCache = new Dictionary<string, GameTexture?>(StringComparer.OrdinalIgnoreCase);
        long textureBytes = 0;
        var textureBudgetReached = false;
        GameTexture? Texture(string path)
        {
            token.ThrowIfCancellationRequested();
            if (textureCache.TryGetValue(path, out var cached)) return cached;
            textureCache[path] = null; // 失败也缓存，避免重复读取损坏或缺失纹理。
            if (textureBytes >= 128L * 1024 * 1024) { textureBudgetReached = true; return null; }
            var image = ReadTerritoryTexture(path, options.TextureMaxDimension, token);
            if (textureBytes + image.Bgra32.LongLength > 128L * 1024 * 1024)
            { textureBudgetReached = true; return null; }
            textureBytes += image.Bgra32.LongLength;
            return textureCache[path] = image;
        }
        var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var loaded = new List<GameTerritoryInstance>();
        var warnings = layout.Warnings.ToList();
        var batchModels = new Dictionary<string, IReadOnlyList<GameModelMesh>>(StringComparer.OrdinalIgnoreCase);
        var batchInstances = new List<GameTerritoryInstance>();
        long triangles = 0;
        var truncated = false;
        var loadingTerrain = true;
        // 在各背景层之间交错取样，避免预算全部花在文件开头的装饰层。
        var candidates = layout.Instances.Where(i => i.IsTerrain)
            .Concat(options.TerrainOnly ? [] : layout.Instances.Where(i => !i.IsTerrain)
                .GroupBy(i => i.Layer).SelectMany(g => g.Select((item, index) => (item, index)))
                .OrderBy(x => x.index).Select(x => x.item)).ToArray();
        void Publish()
        {
            if (batchInstances.Count == 0) return;
            token.ThrowIfCancellationRequested();
            progress?.Report(new(batchModels, batchInstances.ToArray(), loaded.Count, candidates.Length)
            { Materials = batchModels.Keys.ToDictionary(p => p, p => materials[p], StringComparer.OrdinalIgnoreCase) });
            batchModels = new(StringComparer.OrdinalIgnoreCase);
            batchInstances.Clear();
        }
        foreach (var instance in candidates)
        {
            token.ThrowIfCancellationRequested();
            if (loadingTerrain && !instance.IsTerrain)
            {
                loadingTerrain = false;
                Publish();
            }
            if (loaded.Count >= options.MaxInstances) { truncated = true; break; }
            if (failed.Contains(instance.ModelPath)) continue;
            var isNew = !models.TryGetValue(instance.ModelPath, out var meshes);
            if (isNew)
            {
                if (models.Count >= options.MaxUniqueModels) { truncated = true; continue; }
                try
                {
                    GameModelMaterial Material(string reference)
                    {
                        var key = reference.TrimStart('/').StartsWith("bg", StringComparison.OrdinalIgnoreCase)
                            ? reference : instance.ModelPath + "|" + reference;
                        if (materialCache.TryGetValue(key, out var cached)) return cached;
                        var value = ReadModelMaterial(instance.ModelPath, reference, 1, token, Texture);
                        materialCache[key] = value;
                        return value;
                    }
                    var preview = ReadModelPreviewCore(instance.ModelPath, 1, options.IncludeTextures, token,
                        options.PreviewLod, Material);
                    meshes = preview.Meshes;
                    materials[instance.ModelPath] = preview.Materials;
                }
                catch (Exception ex) when (IsTerritoryReadFailure(ex))
                {
                    failed.Add(instance.ModelPath);
                    if (warnings.Count < 100) warnings.Add("模型读取失败：" + instance.ModelPath + "：" + ex.Message);
                    continue;
                }
            }
            var count = meshes!.Sum(m => (long)m.TriangleIndices.Length / 3);
            if (triangles + count > options.MaxTriangles)
            {
                truncated = true;
                if (isNew) { failed.Add(instance.ModelPath); materials.Remove(instance.ModelPath); }
                continue;
            }
            if (isNew) models.Add(instance.ModelPath, meshes!);
            triangles += count;
            loaded.Add(instance);
            if (progress == null) continue;
            if (isNew) batchModels.Add(instance.ModelPath, meshes!);
            batchInstances.Add(instance);
            if (loaded.Count == Math.Min(options.BatchSize, 8) || batchInstances.Count >= options.BatchSize)
                Publish();
        }
        Publish();
        if (options.IncludeTextures)
        {
            var usedMaterials = materials.Values.SelectMany(m => m).DistinctBy(m => m.Path ?? m.Reference).ToArray();
            var textured = usedMaterials.Count(m => m.DiffuseTexture != null);
            warnings.Add($"基色近似：{textured}/{usedMaterials.Length} 个独立材质有纹理；未模拟双层混合、法线、天气和水面着色器。");
            if (textureBudgetReached) warnings.Add("纹理缓存达到 128 MiB 上限，其余材质使用基础颜色。");
        }
        if (truncated) warnings.Add("场景超过预览上限，优先加载地形，其余预算分给各背景层。");
        return new(models, loaded, warnings, triangles, truncated) { Materials = materials };
    }

    private static bool IsTerritoryReadFailure(Exception ex)
        => ex is IOException or InvalidDataException or NotSupportedException or ArgumentException or IndexOutOfRangeException;

    private static Matrix4x4 TerritoryTransform(Common.Transformation value)
        => Matrix4x4.CreateScale(value.Scale.X, value.Scale.Y, value.Scale.Z)
           * Matrix4x4.CreateRotationX(value.Rotation.X) * Matrix4x4.CreateRotationY(value.Rotation.Y)
           * Matrix4x4.CreateRotationZ(value.Rotation.Z)
           * Matrix4x4.CreateTranslation(value.Translation.X, value.Translation.Y, value.Translation.Z);

    private static bool IsFiniteTerritoryTransform(Matrix4x4 m)
        => float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M13)
           && float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M23)
           && float.IsFinite(m.M31) && float.IsFinite(m.M32) && float.IsFinite(m.M33)
           && float.IsFinite(m.M41) && float.IsFinite(m.M42) && float.IsFinite(m.M43);
}
