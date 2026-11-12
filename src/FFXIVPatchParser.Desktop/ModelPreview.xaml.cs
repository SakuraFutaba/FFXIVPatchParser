using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace FFXIVPatchParser.Desktop;

public partial class ModelPreview : UserControl
{
    private static readonly DiffuseMaterial GreyMaterial = CreateGreyMaterial();
    private LocalGameSource? source;
    private IReadOnlyList<SqPackModelLocation>? models;
    private IReadOnlyList<NamedModelEntry>? namedModels;
    private CancellationTokenSource? scanCancellation, filterCancellation, loadCancellation, textureCancellation;
    private CancellationTokenSource? actionDiscoveryCancellation, actionReadCancellation;
    private ResourceChoiceItem[]? actions;
    private PreviewScene? scene;
    private Point? dragPosition;
    private MouseButton dragButton;
    private Vector3D pan;
    private double yaw, pitch, zoom;

    public ModelPreview()
    {
        InitializeComponent();
        playbackTimer.Tick += PlaybackTick;
        SetSourceStatus();
        ResetCamera();
    }

    public bool ShowBrowser
    {
        get => Browser.Visibility == Visibility.Visible;
        set
        {
            Browser.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            ModelPath.IsReadOnly = !value;
            ModelPath.Tag = value ? "输入 SqPack 内的 .mdl 路径…" : "选择补丁模型候选…";
            ModelPath.ToolTip = value ? "例如 chara/human/c0101/obj/hair/h0001/model/c0101h0001_hir.mdl" : null;
            LoadButton.Content = value ? "读取模型" : "重新读取";
            UpdateLoadButton();
            if (scene is null && loadCancellation is null) EmptyViewport.Text = EmptyMessage;
        }
    }

    public void SetSource(LocalGameSource? game)
    {
        if (ReferenceEquals(source, game)) return;
        CancelWork();
        source = game;
        models = null;
        namedModels = null;
        ModelTree.ItemsSource = null;
        ModelSearch.Clear();
        ModelSearch.IsEnabled = false;
        ModelPath.Clear();
        ClearScene();
        SetSourceStatus();
        StartCatalogueIfVisible();
    }

    private void SetSourceStatus()
    {
        ModelPath.IsEnabled = MaterialVariant.IsEnabled = ScanButton.IsEnabled = ScanAllModels.IsEnabled = source is not null;
        UpdateLoadButton();
        NpcNamesButton.IsEnabled = source is not null;
        ScanButton.Content = ModelMode.SelectedIndex == 2 ? "扫描模型" : "读取目录";
        BrowserStatus.Text = source is null ? "请选择游戏目录" : "尚未读取";
        EmptyViewport.Text = EmptyMessage;
        SetModelStatus("");
    }

    private string EmptyMessage => source is null ? "请选择游戏目录" : ShowBrowser ? "选择或输入模型路径" : "选择补丁模型候选";

    private void UpdateLoadButton() => LoadButton.IsEnabled = source is not null && (ShowBrowser || ModelPath.Text.Length != 0);

    private void CancelWork()
    {
        scanCancellation?.Cancel();
        filterCancellation?.Cancel();
        loadCancellation?.Cancel();
        textureCancellation?.Cancel();
        actionDiscoveryCancellation?.Cancel();
        actionReadCancellation?.Cancel();
        namesDownload?.Cancel();
        namedSelection?.Cancel();
        StopPlayback(restoreScene: false);
    }

    private void PreviewUnloaded(object sender, RoutedEventArgs e) => CancelWork();

    private void PreviewVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible)
        {
            scanCancellation?.Cancel();
            if (PlaybackStart != null) StopPlayback();
        }
        else StartCatalogueIfVisible();
    }

    public async Task OpenAsync(string path)
    {
        loadCancellation?.Cancel();
        loadCancellation = null;
        ModelPath.Text = path.Trim().Replace('\\', '/').TrimStart('/');
        UpdateLoadButton();
        if (source is not { } game) return;
        ClearScene();
        if (!int.TryParse(MaterialVariant.Text, out var variant) || variant is < 1 or > 9999)
        {
            SetModelStatus("请输入 1 至 9999 的材质变体编号。", error: true);
            EmptyViewport.Text = EmptyMessage;
            return;
        }
        if (ModelPath.Text.Length == 0)
        {
            SetModelStatus("请输入 SqPack 内的 .mdl 路径。", error: true);
            EmptyViewport.Text = EmptyMessage;
            return;
        }

        scanCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        loadCancellation = cancellation;
        var token = cancellation.Token;
        var modelPath = ModelPath.Text;
        SetModelStatus("正在读取模型…");
        EmptyViewport.Text = "正在读取模型…";
        try
        {
            var preview = await Task.Run(() => BuildScene(game.ReadModelPreview(modelPath, variant, token), token), token);
            token.ThrowIfCancellationRequested();
            ShowScene(preview);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (loadCancellation == cancellation && ReferenceEquals(source, game))
            {
                SetModelStatus("");
                EmptyViewport.Text = EmptyMessage;
            }
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                SetModelStatus($"无法预览模型 · {ex.Message}", error: true);
                EmptyViewport.Text = "模型读取失败";
            }
        }
        finally
        {
            if (loadCancellation == cancellation) loadCancellation = null;
        }
    }

    private async void LoadModel(object sender, RoutedEventArgs e) => await OpenAsync(ModelPath.Text);

    private async void ModelPathKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await OpenAsync(ModelPath.Text);
    }

    private async void ModelSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is PathTreeNode { Payload: SqPackModelLocation model }) await OpenAsync(model.Path);
        else if (e.NewValue is PathTreeNode { Payload: NamedModelEntry named } node) await OpenNamedModel(named, node);
    }

    private async void ScanModels(object sender, RoutedEventArgs e) => await ReadCatalogue(refresh: true);

    private async Task ReadCatalogue(bool refresh = false)
    {
        if (scanCancellation is { IsCancellationRequested: false } scanning)
        {
            scanning.Cancel();
            return;
        }
        if (source is not { } game) return;
        using var cancellation = new CancellationTokenSource();
        scanCancellation = cancellation;
        var token = cancellation.Token;
        var rawIndex = ModelMode.SelectedIndex == 2;
        var charactersOnly = ScanAllModels.IsChecked != true;
        ScanAllModels.IsEnabled = false;
        ScanButton.Content = "取消读取";
        BrowserStatus.Text = rawIndex ? "正在扫描模型索引…" : "正在读取模型关联表…";
        var progress = new Progress<string>(message =>
        {
            if (scanCancellation == cancellation && !token.IsCancellationRequested) BrowserStatus.Text = message;
        });
        try
        {
            if (rawIndex)
            {
                var catalog = await Task.Run(() => game.ListModelLocations(token, progress: progress, charactersOnly: charactersOnly), token);
                token.ThrowIfCancellationRequested();
                models = catalog;
            }
            else
            {
                var catalog = await game.ListNamedModelsAsync(token, progress, refresh);
                token.ThrowIfCancellationRequested();
                namedModels = catalog;
            }
            ModelSearch.IsEnabled = true;
            await ApplyModelFilter();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (scanCancellation == cancellation && ReferenceEquals(source, game)) BrowserStatus.Text = "已取消扫描";
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested) BrowserStatus.Text = $"扫描模型失败 · {ex.Message}";
        }
        finally
        {
            if (scanCancellation == cancellation)
            {
                scanCancellation = null;
                ScanButton.Content = ModelMode.SelectedIndex == 2 ? "扫描模型" : "读取目录";
                ScanAllModels.IsEnabled = source is not null;
            }
        }
    }

    private async void FilterModels(object sender, TextChangedEventArgs e) => await ApplyModelFilter(debounce: true);

    private async Task ApplyModelFilter(bool debounce = false)
    {
        var mode = ModelMode.SelectedIndex;
        var rawCatalog = models;
        var namedCatalog = namedModels;
        if (mode == 2 ? rawCatalog == null : namedCatalog == null) return;
        filterCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        filterCancellation = cancellation;
        var token = cancellation.Token;
        var query = ModelSearch.Text;
        try
        {
            if (debounce) await Task.Delay(180, token);
            var tree = await Task.Run(() => mode == 2
                ? PathTree.Build(rawCatalog!, model => model.Path, query, model => model.Container, token)
                : BuildNamedTree(namedCatalog!, mode == 1, query, token), token);
            token.ThrowIfCancellationRequested();
            ModelTree.ItemsSource = tree;
            var count = mode == 2 ? rawCatalog!.Count : namedCatalog!.Count;
            BrowserStatus.Text = $"{tree.Sum(node => node.LeafCount):N0} / {count:N0} 个模型条目";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested) BrowserStatus.Text = $"筛选模型失败 · {ex.Message}";
        }
        finally
        {
            if (filterCancellation == cancellation) filterCancellation = null;
        }
    }

    private void ClearScene()
    {
        StopPlayback(restoreScene: false);
        textureCancellation?.Cancel();
        actionDiscoveryCancellation?.Cancel();
        actionReadCancellation?.Cancel();
        actions = null;
        scene = null;
        selectedNamedModel = null;
        ModelEntryInfo.Text = "";
        ModelTableLinks.Visibility = Visibility.Collapsed;
        if (ModelSurface.IsMouseCaptured) ModelSurface.ReleaseMouseCapture();
        ModelSurface.Cursor = Cursors.Arrow;
        ModelVisual.Content = null;
        ResetButton.IsEnabled = UseTextures.IsEnabled = false;
        Inspector.Visibility = CameraHelp.Visibility = Visibility.Collapsed;
        EmptyViewport.Visibility = Visibility.Visible;
        MaterialChoice.ItemsSource = null;
        TextureChoice.ItemsSource = null;
        TextureImage.Source = null;
        BoneNames.Clear();
        ActionList.IsExpanded = false;
        ActionChoice.ItemsSource = null;
        ActionChoice.IsEnabled = ActionFilter.IsEnabled = false;
        ActionFilter.Clear();
        ActionPath.Clear();
        ActionDiscoveryStatus.Text = "";
        ClearActionMetadata();
    }

    private void ShowScene(PreviewScene preview)
    {
        scene = preview;
        var choices = new List<MaterialChoiceItem> { new(-1, "全部材质") };
        var usedMaterials = preview.Meshes.Select(mesh => mesh.MaterialIndex).ToHashSet();
        for (var index = 0; index < preview.Materials.Count; index++)
        {
            if (!usedMaterials.Contains(index)) continue;
            var material = preview.Materials[index].Info;
            var name = Path.GetFileName(material.Path ?? material.Reference);
            choices.Add(new(index, string.IsNullOrEmpty(name) ? $"材质 {index + 1}" : $"{index + 1} · {name}"));
        }

        UseTextures.IsEnabled = preview.Meshes.Any(mesh => mesh.Geometry.TextureCoordinates.Count != 0
            && mesh.MaterialIndex >= 0 && mesh.MaterialIndex < preview.Materials.Count
            && preview.Materials[mesh.MaterialIndex].Surface is not null);
        MaterialChoice.ItemsSource = choices;
        MaterialChoice.SelectedIndex = 0;
        MaterialChoice.IsEnabled = choices.Count > 1;
        BoneList.IsExpanded = false;
        BoneList.Visibility = preview.BoneNames.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        BoneNames.Text = string.Join(Environment.NewLine, preview.BoneNames);
        AnimationStatus.Text = $"{preview.BoneNames.Count:N0} 个模型骨骼 · 已知怪物本体可用 Godbert 播放动作";
        SetModelStatus($"LOD 0 · {preview.Meshes.Count:N0} 个网格 · {preview.Vertices:N0} 个顶点 · {preview.Triangles:N0} 个三角面");
        if (preview.Warnings.Count != 0)
            ModelInfo.ToolTip = ModelInfo.Text + Environment.NewLine + string.Join(Environment.NewLine, preview.Warnings);

        ResetCamera();
        Inspector.Visibility = CameraHelp.Visibility = Visibility.Visible;
        EmptyViewport.Visibility = Visibility.Collapsed;
        ResetButton.IsEnabled = true;
        ModelSurface.Cursor = Cursors.SizeAll;
    }

    private void SetModelStatus(string message, bool error = false)
    {
        ModelInfo.Text = message;
        ModelInfo.ToolTip = message;
        ModelInfo.Foreground = error ? Brushes.Firebrick : (Brush)FindResource("Muted");
    }

    private void ChangeDisplay(object sender, RoutedEventArgs e) => RenderScene();

    private void RenderScene()
    {
        if (scene is not { } preview) return;
        var materialIndex = (MaterialChoice.SelectedItem as MaterialChoiceItem)?.Index ?? -1;
        var group = new Model3DGroup();
        for (var i = 0; i < preview.Meshes.Count; i++)
        {
            var mesh = preview.Meshes[i];
            if (materialIndex >= 0 && materialIndex != mesh.MaterialIndex) continue;
            var material = GreyMaterial;
            if (UseTextures.IsChecked == true && mesh.Geometry.TextureCoordinates.Count != 0
                && mesh.MaterialIndex >= 0 && mesh.MaterialIndex < preview.Materials.Count)
                material = preview.Materials[mesh.MaterialIndex].Surface ?? GreyMaterial;
            var geometry = animatedGeometry?[i] ?? mesh.Geometry;
            group.Children.Add(new GeometryModel3D(geometry, material) { BackMaterial = material });
        }
        if (animatedGeometry == null) group.Freeze();
        ModelVisual.Content = group;
    }

    private void MaterialSelected(object sender, SelectionChangedEventArgs e)
    {
        if (scene is not { } preview || MaterialChoice.SelectedItem is not MaterialChoiceItem choice) return;
        RenderScene();
        textureCancellation?.Cancel();
        TextureChoice.ItemsSource = null;
        TextureChoice.Visibility = TextureImage.Visibility = Visibility.Collapsed;
        TextureImage.Source = null;
        TextureStatus.Text = "";
        if (choice.Index < 0)
        {
            var loaded = preview.Materials.Count(material => material.Surface is not null);
            MaterialDetails.Text = $"{preview.Materials.Count:N0} 个材质槽 · {loaded:N0} 个基础色纹理";
            return;
        }

        var material = preview.Materials[choice.Index].Info;
        MaterialDetails.Text = string.Join(Environment.NewLine,
            new[] { material.Path ?? material.Reference, material.ShaderPackage, material.Warning }.Where(value => !string.IsNullOrEmpty(value)));
        if (material.TexturePaths.Count == 0) return;
        var textures = material.TexturePaths.Select(path => new ResourceChoiceItem(path, Path.GetFileName(path))).ToArray();
        TextureChoice.ItemsSource = textures;
        TextureChoice.Visibility = Visibility.Visible;
        var diffuse = Array.FindIndex(textures, texture => texture.Path == material.DiffuseTexture?.Path);
        TextureChoice.SelectedIndex = Math.Max(0, diffuse);
    }

    private async void TextureSelected(object sender, SelectionChangedEventArgs e)
    {
        if (source is not { } game || scene is not { } preview
            || MaterialChoice.SelectedItem is not MaterialChoiceItem { Index: >= 0 } slot
            || TextureChoice.SelectedItem is not ResourceChoiceItem texture) return;
        textureCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        textureCancellation = cancellation;
        var token = cancellation.Token;
        TextureImage.Source = null;
        TextureImage.Visibility = Visibility.Collapsed;
        TextureStatus.Text = "正在读取纹理…";
        try
        {
            var diffuse = preview.Materials[slot.Index].Info.DiffuseTexture;
            var bitmap = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var data = diffuse?.Path == texture.Path ? diffuse : game.ReadTexture(texture.Path);
                return CreateBitmap(data, PixelFormats.Bgra32);
            }, token);
            token.ThrowIfCancellationRequested();
            TextureImage.Source = bitmap;
            TextureImage.Visibility = Visibility.Visible;
            TextureStatus.Text = $"{bitmap.PixelWidth:N0} × {bitmap.PixelHeight:N0} · {texture.Path}";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested) TextureStatus.Text = $"无法读取纹理 · {ex.Message}";
        }
        finally
        {
            if (textureCancellation == cancellation) textureCancellation = null;
        }
    }

    private async void ActionListExpanded(object sender, RoutedEventArgs e)
    {
        if (source is not { } game || scene is not { } preview || actions is not null
            || actionDiscoveryCancellation is { IsCancellationRequested: false }) return;

        using var cancellation = new CancellationTokenSource();
        actionDiscoveryCancellation = cancellation;
        var token = cancellation.Token;
        ActionDiscoveryStatus.Text = "正在查询本地动作…";
        try
        {
            var paths = await Task.Run(() => game.ListModelAnimationPaths(preview.Path, token), token);
            token.ThrowIfCancellationRequested();
            actions = paths.Select(path => new ResourceChoiceItem(path, Path.GetFileName(path))).ToArray();
            ActionFilter.IsEnabled = actions.Length > 0;
            ApplyActionFilter();
            if (ActionPath.Text.Length == 0 && ActionFilter.Text.Length == 0)
                ActionChoice.SelectedItem = actions.FirstOrDefault(item => item.Path.EndsWith("/resident/monster.pap", StringComparison.Ordinal));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested) ActionDiscoveryStatus.Text = $"查询失败 · {ex.Message}";
        }
        finally
        {
            if (actionDiscoveryCancellation == cancellation) actionDiscoveryCancellation = null;
        }
    }

    private void FilterActions(object sender, TextChangedEventArgs e) => ApplyActionFilter();

    private void ApplyActionFilter()
    {
        if (actions is null) return;
        var query = ActionFilter.Text.Trim().Replace('\\', '/');
        var filtered = query.Length == 0 ? actions : actions.Where(item => item.Path.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        ActionChoice.ItemsSource = filtered;
        ActionChoice.IsEnabled = filtered.Length > 0;
        ActionDiscoveryStatus.Text = actions.Length == 0 ? "未找到本地动作候选，可输入路径。" : $"{filtered.Length:N0} / {actions.Length:N0} 个本地候选";
    }

    private async void ActionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ActionChoice.SelectedItem is ResourceChoiceItem action) await OpenAnimation(action.Path);
    }

    private async void LoadAction(object sender, RoutedEventArgs e) => await OpenAnimation(ActionPath.Text);

    private async void ActionPathKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await OpenAnimation(ActionPath.Text);
    }

    private async Task OpenAnimation(string path)
    {
        if (source is not { } game || scene is null) return;
        actionReadCancellation?.Cancel();
        ActionPath.Text = path.Trim().Replace('\\', '/').TrimStart('/');
        var actionPath = ActionPath.Text;
        if (ActionChoice.SelectedItem is ResourceChoiceItem selected && selected.Path != actionPath)
            ActionChoice.SelectedIndex = -1;
        ClearActionMetadata();
        if (!actionPath.EndsWith(".pap", StringComparison.OrdinalIgnoreCase))
        {
            ActionMetadataStatus.Text = "请输入 SqPack 内的 .pap 路径。";
            return;
        }

        using var cancellation = new CancellationTokenSource();
        actionReadCancellation = cancellation;
        var token = cancellation.Token;
        ActionMetadataStatus.Text = "正在读取动作元数据…";
        try
        {
            var action = await Task.Run(() => game.ReadAnimationMetadata(actionPath, token), token);
            token.ThrowIfCancellationRequested();
            ActionMetadataStatus.Text = $"{action.Slots.Count:N0} 个动作槽 · Havok 数据 {action.HavokDataBytes:N0} 字节";
            loadedAction = action;
            PlaybackSlot.ItemsSource = action.Slots;
            PlaybackSlot.Visibility = action.Slots.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            PlaybackSlot.SelectedItem = action.Slots.FirstOrDefault(slot => slot.Name == "cbnm_id0")
                ?? action.Slots.FirstOrDefault(slot => slot.Name == "cbbm_id0") ?? action.Slots.FirstOrDefault();
            UpdatePlaybackControls();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested) ActionMetadataStatus.Text = $"无法读取动作 · {ex.Message}";
        }
        finally
        {
            if (actionReadCancellation == cancellation) actionReadCancellation = null;
        }
    }

    private void ClearActionMetadata()
    {
        loadedAction = null;
        StopPlayback();
        PlaybackSlot.ItemsSource = null;
        PlaybackSlot.Visibility = Visibility.Collapsed;
        PlaybackStatus.Text = "";
        ActionMetadataStatus.Text = "";
    }

    private static PreviewScene BuildScene(GameModelPreview preview, CancellationToken token)
    {
        var bounds = Rect3D.Empty;
        foreach (var mesh in preview.Meshes)
        {
            token.ThrowIfCancellationRequested();
            foreach (var position in mesh.Positions) bounds.Union(new Point3D(position.X, position.Y, position.Z));
        }
        var size = new Vector3D(bounds.SizeX, bounds.SizeY, bounds.SizeZ);
        if (bounds.IsEmpty || size.Length <= 0) throw new InvalidDataException("模型网格没有可显示的范围。");
        var center = new Point3D(bounds.X, bounds.Y, bounds.Z) + size / 2;
        var scale = 2 / size.Length;
        var meshes = new List<PreviewMesh>(preview.Meshes.Count);
        foreach (var mesh in preview.Meshes)
        {
            token.ThrowIfCancellationRequested();
            var positions = new Point3DCollection(mesh.Positions.Length);
            foreach (var position in mesh.Positions)
                positions.Add(new Point3D() + (new Point3D(position.X, position.Y, position.Z) - center) * scale);

            var geometry = new MeshGeometry3D { Positions = positions, TriangleIndices = new Int32Collection(mesh.TriangleIndices) };
            if (mesh.Normals.Length == mesh.Positions.Length)
                geometry.Normals = new Vector3DCollection(mesh.Normals.Select(normal => new Vector3D(normal.X, normal.Y, normal.Z)));
            if (mesh.TextureCoordinates.Length == mesh.Positions.Length)
                geometry.TextureCoordinates = new PointCollection(mesh.TextureCoordinates.Select(uv => new Point(uv.X, uv.Y)));
            geometry.Freeze();
            meshes.Add(new(geometry, mesh.MaterialIndex));
        }

        var materials = new List<PreviewMaterial>(preview.Materials.Count);
        foreach (var material in preview.Materials)
        {
            token.ThrowIfCancellationRequested();
            materials.Add(new(material, material.DiffuseTexture is { } diffuse ? CreateSurfaceMaterial(diffuse) : null));
        }

        return new(preview.Path, meshes, materials, preview.BoneNames, preview.Warnings,
            preview.Meshes.Sum(mesh => mesh.Positions.Length), preview.Meshes.Sum(mesh => mesh.TriangleIndices.Length) / 3,
            preview, center, scale);
    }

    private static DiffuseMaterial CreateGreyMaterial()
    {
        var material = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(178, 187, 201)));
        material.Freeze();
        return material;
    }

    private static DiffuseMaterial CreateSurfaceMaterial(GameTexture texture)
    {
        // Model color textures may use alpha as a shader mask. Keep the approximate preview surface opaque.
        var image = CreateBitmap(texture, PixelFormats.Bgr32);
        var brush = new ImageBrush(image)
        {
            Stretch = Stretch.Fill,
            TileMode = TileMode.Tile,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, 1, 1)
        };
        var material = new DiffuseMaterial(brush);
        material.Freeze();
        return material;
    }

    private static BitmapSource CreateBitmap(GameTexture texture, PixelFormat format)
    {
        var bitmap = BitmapSource.Create(texture.Width, texture.Height, 96, 96, format, null, texture.Bgra32, checked(texture.Width * 4));
        bitmap.Freeze();
        return bitmap;
    }

    private void ResetView(object sender, RoutedEventArgs e) => ResetCamera();

    private void ResetCamera()
    {
        yaw = 0.4;
        pitch = 0.2;
        zoom = 1;
        pan = new Vector3D();
        UpdateCamera();
    }

    private void ResizeViewport(object sender, SizeChangedEventArgs e) => UpdateCamera();

    private Vector3D CameraDirection => new(Math.Sin(yaw) * Math.Cos(pitch), Math.Sin(pitch), Math.Cos(yaw) * Math.Cos(pitch));

    private void UpdateCamera()
    {
        if (ModelViewport is null || ViewCamera is null || ModelViewport.ActualWidth <= 0 || ModelViewport.ActualHeight <= 0) return;
        var direction = CameraDirection;
        ViewCamera.Position = new Point3D() + pan + direction * 4;
        ViewCamera.LookDirection = -direction;
        ViewCamera.Width = 2.2 * Math.Max(1, ModelViewport.ActualWidth / ModelViewport.ActualHeight) / zoom;
    }

    private void StartDrag(object sender, MouseButtonEventArgs e)
    {
        if (scene is null || dragPosition is not null || e.ChangedButton is not (MouseButton.Left or MouseButton.Middle or MouseButton.Right)
            || !ModelSurface.CaptureMouse()) return;
        dragPosition = e.GetPosition(ModelSurface);
        dragButton = e.ChangedButton;
        e.Handled = true;
    }

    private void DragModel(object sender, MouseEventArgs e)
    {
        if (dragPosition is not { } previous) return;
        var current = e.GetPosition(ModelSurface);
        var delta = current - previous;
        dragPosition = current;
        if (dragButton == MouseButton.Left)
        {
            yaw -= delta.X * 0.01;
            pitch = Math.Clamp(pitch + delta.Y * 0.01, -1.5, 1.5);
        }
        else if (ModelViewport.ActualWidth > 0)
        {
            var direction = CameraDirection;
            var right = Vector3D.CrossProduct(new Vector3D(0, 1, 0), direction);
            right.Normalize();
            var up = Vector3D.CrossProduct(direction, right);
            pan += (-delta.X * right + delta.Y * up) * (ViewCamera.Width / ModelViewport.ActualWidth);
        }
        UpdateCamera();
        e.Handled = true;
    }

    private void EndDrag(object sender, MouseButtonEventArgs e)
    {
        if (!ModelSurface.IsMouseCaptured || e.ChangedButton != dragButton) return;
        ModelSurface.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void StopDrag(object sender, MouseEventArgs e) => dragPosition = null;

    private void ZoomWithWheel(object sender, MouseWheelEventArgs e)
    {
        if (scene is null || e.Delta == 0) return;
        zoom = Math.Clamp(zoom * Math.Pow(1.2, e.Delta / 120.0), 0.15, 20);
        UpdateCamera();
        e.Handled = true;
    }

    private sealed record PreviewMesh(MeshGeometry3D Geometry, int MaterialIndex);
    private sealed record PreviewMaterial(GameModelMaterial Info, DiffuseMaterial? Surface);
    private sealed record PreviewScene(string Path, IReadOnlyList<PreviewMesh> Meshes, IReadOnlyList<PreviewMaterial> Materials,
        IReadOnlyList<string> BoneNames, IReadOnlyList<string> Warnings, int Vertices, int Triangles,
        GameModelPreview SourceModel, Point3D Center, double Scale);
    private sealed record MaterialChoiceItem(int Index, string Label);
    private sealed record ResourceChoiceItem(string Path, string Label);
}
