using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Vector3 = System.Numerics.Vector3;
using Matrix4x4 = System.Numerics.Matrix4x4;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace FFXIVPatchParser.Desktop;

public partial class MapPreview
{
    private static readonly DiffuseMaterial TerrainMaterial = CreateMaterial(150, 162, 133);
    private static readonly DiffuseMaterial SceneryMaterial = CreateMaterial(180, 190, 199);
    private static readonly DiffuseMaterial FoliageMaterial = CreateMaterial(117, 154, 120);
    private sealed class ScenePickTarget(GameTerritoryInstance instance)
    {
        public GameTerritoryInstance Instance { get; } = instance;
        public Rect3D Bounds = Rect3D.Empty;
    }

    private sealed record VertexRange(int End, ScenePickTarget Target);
    private static readonly ConditionalWeakTable<MeshGeometry3D, VertexRange[]> pickRanges = new();

    private void FocusSceneObject(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ClickCount != 2 || sceneBounds.IsEmpty) return;
        if (!TryFocusSceneObject(e.GetPosition(MapViewport))) return;
        MapSurface.Focus();
        e.Handled = true;
    }

    private bool TryFocusSceneObject(Point position)
    {
        ScenePickTarget? target = null;
        var nearest = double.PositiveInfinity;
        VisualTreeHelper.HitTest(MapViewport, null, result =>
        {
            if (result is RayMeshGeometry3DHitTestResult hit && hit.DistanceToRayOrigin < nearest &&
                pickRanges.TryGetValue(hit.MeshHit, out var ranges))
            {
                var range = Array.Find(ranges, range => hit.VertexIndex1 < range.End);
                if (range != null)
                {
                    nearest = hit.DistanceToRayOrigin;
                    target = range.Target;
                }
            }
            return HitTestResultBehavior.Continue;
        }, new PointHitTestParameters(position));
        if (target == null) return false;
        camera.FocusBounds(target.Bounds);
        FocusedObject.Text = $"已聚焦 {Path.GetFileName(target.Instance.ModelPath)}";
        FocusedObject.ToolTip = target.Instance.Layer + "\n" + target.Instance.ModelPath;
        return true;
    }

    private void ResetView(object sender, RoutedEventArgs e)
    {
        camera.Reset();
        FocusedObject.Text = "";
        FocusedObject.ToolTip = null;
        MapSurface.Focus();
    }

    private void ResetNearView(object sender, RoutedEventArgs e)
    {
        camera.ResetAtDistance(20);
        MapSurface.Focus();
    }

    private async Task LoadSceneAsync(LocalGameSource game, NamedMapEntry entry, CancellationToken token)
    {
        displayedPath = entry.ScenePath;
        await ShowEnvironmentAsync(game, entry, token);
        await ShowMapEffectsAsync(game, entry, token);
        if (scenes.FirstOrDefault(cached => cached.Path == entry.ScenePath) is { } cached)
        {
            scenes.Remove(cached);
            scenes.AddFirst(cached);
            foreach (var group in cached.Groups) visibleScene.Children.Add(group);
            sceneBounds = cached.Bounds;
            terrainBounds = cached.TerrainBounds;
            SetSceneEntrance(cached.Entrance);
            camera.SetBounds(sceneBounds, CameraFramingBounds());
            ShowCameraControls();
            MapStatus.Text = cached.Summary + " · 已缓存";
            MapStatus.ToolTip = cached.Warning;
            previewComplete = true;
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        var managedGroupIds = MapEffectChoice.Items.OfType<MapEffectOption>()
            .Where(option => option.Binding.Timelines.Any(t => t.Name is "bg_on" or "bg_off"))
            .Select(option => option.Binding.LayoutId).ToHashSet();
        long drawTriangles = 0;
        var result = await Task.Run(() =>
        {
            var layout = game.ReadTerritoryLayout(entry.ScenePath, token);
            var entrance = FindSceneEntrance(game, entry.ScenePath, token);
            var models = new Dictionary<string, IReadOnlyList<GameModelMesh>>(StringComparer.OrdinalIgnoreCase);
            var materials = new Dictionary<string, IReadOnlyList<GameModelMaterial>>(StringComparer.OrdinalIgnoreCase);
            var surfaces = new Dictionary<(string Path, GameMaterialAlphaMode Alpha, byte Cutoff), DiffuseMaterial>();
            var clippedMeshes = new Dictionary<(GameModelMesh Mesh, GameModelMaterial Material), GameModelMesh>();
            var progress = new SceneProgress(batch =>
            {
                token.ThrowIfCancellationRequested();
                foreach (var model in batch.Models) models.Add(model.Key, model.Value);
                foreach (var material in batch.Materials) materials.Add(material.Key, material.Value);
                var geometry = batch.Instances.GroupBy(instance => managedGroupIds.Contains(instance.RootSharedGroupId)
                        ? instance.RootSharedGroupId : 0)
                    .Select(group => (LayoutId: group.Key, Mesh: BuildSceneBatch(group.ToArray(), models, materials, surfaces, clippedMeshes, token)))
                    .ToArray();
                drawTriangles += geometry.Sum(group => group.Mesh.TriangleCount);
                Dispatcher.Invoke(() =>
                {
                    if (token.IsCancellationRequested || displayedPath != entry.ScenePath) return;
                    if (sceneBounds.IsEmpty) SetSceneEntrance(entrance);
                    foreach (var (layoutId, mesh) in geometry) ShowSceneGroup(mesh.Opaque, mesh.Transparent, mesh.Terrain, layoutId);
                    MapStatus.Text = $"已加载 {batch.LoadedInstances:N0} / {batch.TotalInstances:N0} 个场景实例";
                }, DispatcherPriority.Background, token);
            });
            var compactScene = layout.Instances.Count <= 768;
            return game.LoadTerritoryGeometry(layout, new()
            {
                BatchSize = 384,
                MaxTriangles = compactScene ? 2_000_000 : 1_000_000,
                TextureMaxDimension = compactScene ? 512 : 256,
                PreviewLod = compactScene ? 0 : layout.Instances.Count > 3000 ? 2 : 1
            }, progress, token);
        }, token);
        token.ThrowIfCancellationRequested();
        if (sceneBounds.IsEmpty)
        {
            EmptyScene.Text = "此区域没有可显示的静态场景";
            MapStatus.Text = result.Warnings.FirstOrDefault() ?? "可切换到游戏地图查看平面图。";
            previewComplete = true;
            return;
        }
        var summary = $"{result.Instances.Count:N0} 个实例 · {drawTriangles:N0} 个三角面"
            + (result.IsTruncated ? " · 已达到预览上限" : "");
        MapStatus.Text = $"{summary} · {stopwatch.Elapsed.TotalSeconds:0.0} 秒";
        var warning = result.Warnings.Count == 0 ? null : string.Join("\n", result.Warnings.Take(12));
        MapStatus.ToolTip = warning;
        previewComplete = true;
        scenes.AddFirst(new CachedScene(entry.ScenePath, visibleScene.Children.ToArray(), sceneBounds, terrainBounds,
            sceneEntrance, summary, warning));
        if (scenes.Count > 2) scenes.RemoveLast();
    }

    private async Task ShowEnvironmentAsync(LocalGameSource game, NamedMapEntry entry, CancellationToken token)
    {
        try
        {
            var (environment, timeline, textures, icons) = await Task.Run(() =>
            {
                var metadata = game.ReadTerritoryEnvironment(entry.ScenePath, token);
                GameEnvironmentTimeline? lighting = null;
                if (metadata.EnvironmentPaths.FirstOrDefault() is { } path)
                {
                    try { lighting = game.ReadEnvironmentTimeline(path, token); }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or OverflowException) { }
                }
                var sky = lighting == null ? [] : ReadSkyTextures(game, lighting, token);
                return (metadata, lighting, sky, ReadWeatherIcons(game, metadata, token));
            }, token);
            token.ThrowIfCancellationRequested();
            if (displayedPath != entry.ScenePath) return;
            SetEnvironment(environment, timeline, textures, icons);
            var fields = (MapFields.ItemsSource as IEnumerable<MapField> ?? []).Where(field =>
                field.Label != "环境资源").ToList();
            foreach (var path in environment.EnvironmentPaths) fields.Add(new("环境资源", path));
            MapFields.ItemsSource = fields;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException
            or NotSupportedException or IndexOutOfRangeException or OverflowException)
        {
            // 环境解析尚未覆盖所有版本，静态场景仍可独立预览。
        }
    }

    private void ShowSceneGroup(Model3DGroup opaque, Model3DGroup transparent, Rect3D terrain, uint layoutId)
    {
        var bounds = opaque.Bounds;
        bounds.Union(transparent.Bounds);
        if (bounds.IsEmpty) return;
        if (opaque.Children.Count > 0) visibleScene.Children.Insert(opaqueSceneGroups++, MakePhaseGroup(opaque, layoutId));
        if (transparent.Children.Count > 0) visibleScene.Children.Add(MakePhaseGroup(transparent, layoutId));
        var first = sceneBounds.IsEmpty;
        sceneBounds.Union(bounds);
        terrainBounds.Union(terrain);
        camera.SetBounds(sceneBounds, CameraFramingBounds());
        if (first) ShowCameraControls();
    }

    private void ShowCameraControls()
    {
        EmptyScene.Visibility = Visibility.Collapsed;
        CameraHelp.Visibility = Visibility.Visible;
        ResetCameraButton.IsEnabled = ResetNearCameraButton.IsEnabled = true;
        CopyCurrentPositionButton.IsEnabled = true;
        if (selectedMap is { } entry)
        {
            displayedCameraMap = CameraMapKey(entry);
            if (mapCameraViews.TryGetValue(displayedCameraMap.Value, out var view)) camera.RestoreView(view);
            else ApplyStartPosition();
        }
        else ApplyStartPosition();
        UpdateStartButton();
        if (MapList.IsKeyboardFocusWithin || SceneMode.IsKeyboardFocused) MapSurface.Focus();
    }

    private Rect3D CameraFramingBounds()
    {
        // 有些副本只在地下保留几块很小的占位地形，取景时用出生圈周边或场景范围。
        if (terrainBounds.SizeX >= 1 && terrainBounds.SizeZ >= 1) return terrainBounds;
        return sceneEntrance is { } point
            ? new Rect3D(point.X - 24, point.Y - 4, point.Z - 24, 48, 12, 48)
            : Rect3D.Empty;
    }

    private static Point3D? FindSceneEntrance(LocalGameSource game, string path, CancellationToken token)
    {
        try
        {
            var entrances = game.ReadTerritoryEntrancePositions(path, token);
            return entrances.Count == 1 ? new Point3D(entrances[0].X, entrances[0].Y, entrances[0].Z) : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or NotSupportedException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    private static (Model3DGroup Opaque, Model3DGroup Transparent, Rect3D Terrain, long TriangleCount) BuildSceneBatch(
        IReadOnlyList<GameTerritoryInstance> instances,
        Dictionary<string, IReadOnlyList<GameModelMesh>> models,
        Dictionary<string, IReadOnlyList<GameModelMaterial>> materials,
        Dictionary<(string Path, GameMaterialAlphaMode Alpha, byte Cutoff), DiffuseMaterial> surfaces,
        Dictionary<(GameModelMesh Mesh, GameModelMaterial Material), GameModelMesh> clippedMeshes, CancellationToken token)
    {
        // 同一贴图的网格合并后提交，避免每个实例单独绘制。
        var opaque = new Model3DGroup();
        var transparent = new Model3DGroup();
        var terrainBounds = Rect3D.Empty;
        long triangleCount = 0;
        var batches = new Dictionary<(DiffuseMaterial Material, bool TwoSided, bool Terrain, bool UsesAlpha, bool HasNormals), SceneMesh>();
        foreach (var instance in instances)
        {
            token.ThrowIfCancellationRequested();
            var target = new ScenePickTarget(instance);
            materials.TryGetValue(instance.ModelPath, out var modelMaterials);
            var foliage = IsFoliage(instance.ModelPath);
            var fallback = instance.IsTerrain ? TerrainMaterial : foliage ? FoliageMaterial : SceneryMaterial;
            var reverseWinding = instance.Transform.GetDeterminant() < 0;
            var invertible = Matrix4x4.Invert(instance.Transform, out var inverse);
            var normalMatrix = Matrix4x4.Transpose(inverse);
            foreach (var sourceMesh in models[instance.ModelPath])
            {
                var mesh = sourceMesh;
                var info = modelMaterials != null && mesh.TextureCoordinates.Length == mesh.Positions.Length &&
                    (uint)mesh.MaterialIndex < modelMaterials.Count ? modelMaterials[mesh.MaterialIndex] : null;
                var texture = info?.DiffuseTexture;
                var clipped = texture != null && info!.AlphaMode == GameMaterialAlphaMode.Mask;
                if (clipped)
                {
                    var clipKey = (sourceMesh, info!);
                    if (!clippedMeshes.TryGetValue(clipKey, out mesh))
                    {
                        mesh = PreviewAlphaGeometry.Clip(sourceMesh, texture!, PreviewSurfaceMaterial.CutoffByte(info!), token);
                        clippedMeshes.Add(clipKey, mesh);
                    }
                }
                var indices = mesh.TriangleIndices;
                if (indices.Length == 0) continue;
                triangleCount += indices.Length / 3;
                // 孔洞已从几何中删除，剩余面按不透明材质写深度，交叉叶片无需排序。
                var alpha = clipped ? GameMaterialAlphaMode.Opaque : info?.AlphaMode ?? GameMaterialAlphaMode.Opaque;
                var usesAlpha = texture != null && alpha != GameMaterialAlphaMode.Opaque;
                var material = texture == null ? fallback : GetSurface(texture, alpha, PreviewSurfaceMaterial.CutoffByte(info!), surfaces);
                var key = (Material: material, TwoSided: texture == null || foliage || usesAlpha || clipped, Terrain: instance.IsTerrain,
                    UsesAlpha: usesAlpha, HasNormals: invertible && mesh.Normals.Length == mesh.Positions.Length);
                if (!batches.TryGetValue(key, out var batch))
                    batches.Add(key, batch = new SceneMesh());
                if (batch.Indices.Count != 0 &&
                    (batch.Points.Count + mesh.Positions.Length > 20_000 || batch.Indices.Count + mesh.TriangleIndices.Length > 60_000))
                {
                    var bounds = AddSurface(usesAlpha ? transparent : opaque, batch, material, key.TwoSided);
                    if (instance.IsTerrain) terrainBounds.Union(bounds);
                    batches[key] = batch = new SceneMesh();
                }
                var start = batch.Points.Count;
                for (var vertex = 0; vertex < mesh.Positions.Length; vertex++)
                {
                    var transformed = Vector3.Transform(mesh.Positions[vertex], instance.Transform);
                    var point = new Point3D(transformed.X, transformed.Y, transformed.Z);
                    batch.Points.Add(point);
                    target.Bounds.Union(point);
                    if (key.HasNormals)
                    {
                        var normal = Vector3.TransformNormal(mesh.Normals[vertex], normalMatrix);
                        var length = normal.Length();
                        normal = float.IsFinite(length) && length > 0 ? normal / length : Vector3.Zero;
                        batch.Normals.Add(new Vector3D(normal.X, normal.Y, normal.Z));
                    }
                    if (texture != null)
                    {
                        var uv = mesh.TextureCoordinates[vertex];
                        batch.Uv.Add(new Point(float.IsFinite(uv.X) ? uv.X : 0, float.IsFinite(uv.Y) ? uv.Y : 0));
                    }
                }
                batch.Ranges.Add(new(batch.Points.Count, target));
                for (var i = 0; i < indices.Length; i += 3)
                {
                    batch.Indices.Add(start + indices[i]);
                    batch.Indices.Add(start + indices[i + (reverseWinding ? 2 : 1)]);
                    batch.Indices.Add(start + indices[i + (reverseWinding ? 1 : 2)]);
                }
            }
        }
        foreach (var (key, batch) in batches)
        {
            var bounds = AddSurface(key.UsesAlpha ? transparent : opaque, batch, key.Material, key.TwoSided);
            if (key.Terrain) terrainBounds.Union(bounds);
        }
        opaque.Freeze();
        transparent.Freeze();
        return (opaque, transparent, terrainBounds, triangleCount);
    }

    private static bool IsFoliage(string path) => path.Contains("tree", StringComparison.OrdinalIgnoreCase)
        || path.Contains("grass", StringComparison.OrdinalIgnoreCase) || path.Contains("plant", StringComparison.OrdinalIgnoreCase);

    private static Rect3D AddSurface(Model3DGroup group, SceneMesh mesh, DiffuseMaterial material, bool twoSided)
    {
        if (mesh.Indices.Count == 0) return Rect3D.Empty;
        var geometry = new MeshGeometry3D { Positions = new Point3DCollection(mesh.Points), TriangleIndices = new Int32Collection(mesh.Indices) };
        pickRanges.Add(geometry, mesh.Ranges.ToArray());
        if (mesh.Uv.Count != 0) geometry.TextureCoordinates = new PointCollection(mesh.Uv);
        if (mesh.Normals.Count != 0) geometry.Normals = new Vector3DCollection(mesh.Normals);
        group.Children.Add(new GeometryModel3D(geometry, material) { BackMaterial = twoSided ? material : null });
        return geometry.Bounds;
    }

    private static DiffuseMaterial GetSurface(GameTexture texture, GameMaterialAlphaMode alpha, byte cutoff,
        Dictionary<(string Path, GameMaterialAlphaMode Alpha, byte Cutoff), DiffuseMaterial> surfaces)
    {
        var key = (texture.Path, alpha, alpha == GameMaterialAlphaMode.Opaque ? (byte)0 : cutoff);
        if (surfaces.TryGetValue(key, out var material)) return material;
        material = PreviewSurfaceMaterial.Create(texture, alpha, key.Item3);
        surfaces.Add(key, material);
        return material;
    }

    private static DiffuseMaterial CreateMaterial(byte red, byte green, byte blue)
    {
        var material = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(red, green, blue)));
        material.Freeze();
        return material;
    }

    private sealed class SceneProgress(Action<GameTerritoryGeometryBatch> publish) : IProgress<GameTerritoryGeometryBatch>
    {
        public void Report(GameTerritoryGeometryBatch value) => publish(value);
    }

    private sealed class SceneMesh
    {
        public List<VertexRange> Ranges { get; } = new();
        public List<Point3D> Points { get; } = new();
        public List<int> Indices { get; } = new();
        public List<Point> Uv { get; } = new();
        public List<Vector3D> Normals { get; } = new();
    }
}
