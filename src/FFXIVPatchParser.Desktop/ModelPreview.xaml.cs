using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    private bool suppressInitialAction;
    private bool usePlayerPose;
    private AnimationChoiceItem[]? actions;
    private string? requestedActionPath;
    private PreviewScene? scene;
    private readonly PreviewCameraController camera;

    public ModelPreview()
    {
        InitializeComponent();
        InitializeCharacterLayout();
        SetSourceStatus();
        camera = new(ModelSurface, ModelViewport, ViewCamera, 0.4, 0.2, modelProjection: true);
    }

    public bool ShowBrowser
    {
        get => showModelBrowser;
        set
        {
            if (!value) CloseCatalogueOptions();
            showModelBrowser = value;
            UpdateCharacterLayout();
            ModelPath.IsReadOnly = !value;
            ModelPath.Tag = value ? "输入 SqPack 内的 .mdl 路径…" : "选择模型文件…";
            ModelPath.ToolTip = value ? "例如 chara/human/c0101/obj/hair/h0001/model/c0101h0001_hir.mdl" : null;
            LoadButton.Content = value ? "读取模型" : "重新读取";
            UpdateLoadButton();
            if (scene is null && loadCancellation is null) EmptyViewport.Text = EmptyMessage;
            if (value) StartCatalogueIfVisible();
        }
    }

    public void SetSource(LocalGameSource? game)
    {
        if (ReferenceEquals(source, game)) return;
        CancelWork();
        source = game;
        ResetNpcPicker();
        catalogueTrees.Clear();
        restoreCataloguePreview = false;
        displayedCataloguePayload = null;
        models = null;
        itemOptions = [];
        assetNames = new Dictionary<string, string>();
        namedModels = null;
        catalogueMode = ModelCatalogueMode.ResourceType;
        CatalogueModeChanged?.Invoke(catalogueMode);
        NpcNamesButton.Visibility = Visibility.Visible;
        PathTreeView.Reset(ModelTree);
        ModelSearch.Clear();
        ModelSearch.IsEnabled = false;
        ModelPath.Clear();
        ClearScene();
        SetSourceStatus();
        StartCatalogueIfVisible();
    }

    private void SetSourceStatus()
    {
        UpdateCharacterLayout();
        ModelPath.IsEnabled = MaterialVariant.IsEnabled = ScanAllModels.IsEnabled = source is not null;
        UpdateLoadButton();
        NpcNamesButton.IsEnabled = source is not null;
        BrowserStatus.Text = source is null ? "请选择游戏目录" : "尚未读取";
        EmptyViewport.Text = EmptyMessage;
        SetModelStatus("");
    }

    private string EmptyMessage => source is null ? "请选择游戏目录" : CharacterMode
        ? "选择种族或查找 NPC" : ShowBrowser ? "选择或输入模型路径" : "选择模型文件";

    private void UpdateLoadButton() => LoadButton.IsEnabled = source is not null && (ShowBrowser || ModelPath.Text.Length != 0);

    private void CancelWork()
    {
        ResetItemEffects();
        CloseCatalogueOptions();
        npcListRequest?.Cancel();
        shaderRequest?.Cancel();
        scanCancellation?.Cancel();
        filterCancellation?.Cancel();
        loadCancellation?.Cancel();
        textureCancellation?.Cancel();
        actionDiscoveryCancellation?.Cancel();
        actionReadCancellation?.Cancel();
        recolorCancellation?.Cancel();
        partOptionsCancellation?.Cancel();
        partPreviewCancellation?.Cancel();
        namesDownload?.Cancel();
        namedSelection?.Cancel();
        StopPlayback(restoreScene: false);
    }

    private void PreviewUnloaded(object sender, RoutedEventArgs e) => CancelWork();

    private void PreviewVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        SetItemEffectVisibility();
        if (!IsVisible)
        {
            CloseCatalogueOptions();
            scanCancellation?.Cancel();
            if (PlaybackStart != null) StopPlayback();
        }
        else StartCatalogueIfVisible();
    }

    internal void CancelFileOpen() => loadCancellation?.Cancel();
    internal bool IsPreviewingPath(string path) => scene?.Path == path;

    internal async Task OpenIndexedFileAsync(string path)
    {
        if (catalogueMode == ModelCatalogueMode.Character) await SelectCatalogueMode(ModelCatalogueMode.ResourceType);
        MaterialVariant.Text = "1";
        await OpenAsync(path);
    }

    public Task OpenAsync(string path) => OpenAsync(path, startIdle: true, imcMask: null);

    private async Task OpenAsync(string path, bool startIdle, int? imcMask, NamedModelEntry? demiHuman = null)
    {
        loadCancellation?.Cancel();
        loadCancellation = null;
        ModelPath.Text = path.Trim().Replace('\\', '/').TrimStart('/');
        UpdateLoadButton();
        if (source is not { } game) return;
        var characterCode = ModelPath.Text.StartsWith("character:", StringComparison.Ordinal) ? ModelPath.Text[10..] : null;
        var keepAppearanceControls = CharacterMode && characterCode != null;
        ClearScene(keepAppearanceControls);
        suppressInitialAction = !startIdle;
        usePlayerPose = characterCode != null;
        if (!int.TryParse(MaterialVariant.Text, out var variant) || variant is < 1 or > 9999)
        {
            if (keepAppearanceControls) ClearScene();
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

        using var cancellation = new CancellationTokenSource();
        loadCancellation = cancellation;
        var token = cancellation.Token;
        var modelPath = ModelPath.Text;
        SetModelStatus("正在读取模型…");
        EmptyViewport.Text = "正在读取模型…";
        try
        {
            var (preview, palette) = await Task.Run(() =>
            {
                if (!modelPath.StartsWith("character:", StringComparison.Ordinal))
                {
                    var modelPreview = demiHuman == null ? game.ReadModelPreview(modelPath, variant, token, imcMask)
                        : game.ReadDemiHumanPreview(demiHuman, token);
                    return (BuildScene(modelPreview, token), (GameCharacterPalette?)null);
                }
                var code = modelPath[10..];
                var colors = game.ReadCharacterPalette(code, token);
                var model = game.ReadCharacterPreview(code, variant, token);
                return (BuildScene(model, token), colors);
            }, token);
            token.ThrowIfCancellationRequested();
            ShowScene(preview, startIdle);
            ShowCharacterAppearance(palette);
            ShowCharacterParts(characterCode);
            SynchronizeCharacterRace(characterCode);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (loadCancellation == cancellation && ReferenceEquals(source, game))
            {
                if (keepAppearanceControls) ClearScene();
                SetModelStatus("");
                EmptyViewport.Text = EmptyMessage;
            }
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                if (keepAppearanceControls) ClearScene();
                SetModelStatus($"无法预览模型 · {ex.Message}", error: true);
                EmptyViewport.Text = "模型读取失败";
            }
        }
        finally
        {
            if (loadCancellation == cancellation) loadCancellation = null;
        }
    }

    private async void LoadModel(object sender, RoutedEventArgs e) => await ReloadModelPath();

    private async void ModelPathKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await ReloadModelPath();
    }

    private Task ReloadModelPath() =>
        selectedNamedModel is { ModelType: 2 } entry &&
        string.Equals(ModelPath.Text.Trim(), entry.Path, StringComparison.OrdinalIgnoreCase) &&
        ModelTree.SelectedItem is PathTreeNode selected && Equals(selected.Payload, entry)
            ? OpenNamedModel(entry, selected) : OpenAsync(ModelPath.Text);

    private async void ModelSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (!PathTreeView.IsRestoringSelection(ModelTree) && e.NewValue is PathTreeNode node)
            await OpenCatalogueNode(node);
    }

    private object? displayedCataloguePayload;
    private WeakReference<PreviewScene>? displayedCatalogueScene;
    private bool restoreCataloguePreview;

    private async Task OpenCatalogueNode(PathTreeNode node)
    {
        if (node.Payload == null || scene != null && Equals(displayedCataloguePayload, node.Payload) &&
            displayedCatalogueScene?.TryGetTarget(out var previous) == true && ReferenceEquals(previous, scene)) return;
        switch (node.Payload)
        {
            case SqPackModelLocation model:
                await OpenAsync(model.Path, startIdle: catalogueMode != ModelCatalogueMode.ResourceType || GearFamily(model.Path) == null, imcMask: null);
                break;
            case GameCharacterPartOption option: await OpenItemModel(option, node); break;
            case NamedModelEntry named: await OpenNamedModel(named, node); break;
        }
        if (scene != null && PathTreeView.IsCurrentSelection(ModelTree, node))
        {
            displayedCataloguePayload = node.Payload;
            displayedCatalogueScene = new(scene);
        }
    }

    private async Task ReadCatalogue(bool refresh = false)
    {
        if (scanCancellation is { IsCancellationRequested: false }) return;
        if (source is not { } game) return;
        using var cancellation = new CancellationTokenSource();
        scanCancellation = cancellation;
        var token = cancellation.Token;
        BrowserStatus.Text = "正在读取模型关联表…";
        var progress = new Progress<string>(message =>
        {
            if (scanCancellation == cancellation && !token.IsCancellationRequested) BrowserStatus.Text = message;
        });
        try
        {
            var catalog = namedModels != null && !refresh ? namedModels : await ReadAssemblyCatalogue(game, token, progress, refresh);
            token.ThrowIfCancellationRequested();
            if (catalogueMode != ModelCatalogueMode.Character && (models == null || modelCatalogRevision != game.MediaCatalogRevision))
            {
                var snapshot = await Task.Run(() => LoadModelAssets(game, token), token);
                token.ThrowIfCancellationRequested();
                models = snapshot.Models;
                itemOptions = snapshot.Items;
                assetNames = snapshot.Labels;
                modelCatalogRevision = snapshot.Revision;
            }
            token.ThrowIfCancellationRequested();
            namedModels = catalog;
            if (catalogueMode != ModelCatalogueMode.Character)
            {
                ModelSearch.IsEnabled = true;
                await ApplyModelFilter();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (scanCancellation == cancellation && ReferenceEquals(source, game) && catalogueMode != ModelCatalogueMode.Character)
                BrowserStatus.Text = "已取消读取";
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested && catalogueMode != ModelCatalogueMode.Character) BrowserStatus.Text = $"读取模型目录失败 · {ex.Message}";
        }
        finally
        {
            if (scanCancellation == cancellation) scanCancellation = null;
        }
    }

    private async void FilterModels(object sender, TextChangedEventArgs e) => await ApplyModelFilter(debounce: true);

    private sealed record CatalogueTree(IReadOnlyList<SqPackModelLocation> Raw, IReadOnlyList<NamedModelEntry> Named,
        IReadOnlyList<GameCharacterPartOption> Gear, string Query, IReadOnlyList<PathTreeNode> Nodes);
    private readonly Dictionary<ModelCatalogueMode, CatalogueTree> catalogueTrees = new();

    private async Task ApplyModelFilter(bool debounce = false)
    {
        var mode = catalogueMode;
        var rawCatalog = models;
        var namedCatalog = namedModels;
        var gearOptions = itemOptions;
        var labels = assetNames;
        if (mode == ModelCatalogueMode.Character || source == null || rawCatalog == null || namedCatalog == null) return;
        filterCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        filterCancellation = cancellation;
        var token = cancellation.Token;
        var query = ModelSearch.Text;
        try
        {
            if (debounce) await Task.Delay(180, token);
            if (!catalogueTrees.TryGetValue(mode, out var cached) || cached.Query != query ||
                !ReferenceEquals(cached.Raw, rawCatalog) || !ReferenceEquals(cached.Gear, gearOptions) ||
                !ReferenceEquals(cached.Named, namedCatalog))
            {
                var nodes = await Task.Run(() => mode == ModelCatalogueMode.Number
                    ? BuildNumberTree(namedCatalog, rawCatalog, gearOptions, query, token)
                    : BuildResourceTree(rawCatalog, gearOptions, namedCatalog, labels, query, token), token);
                token.ThrowIfCancellationRequested();
                cached = new(rawCatalog, namedCatalog, gearOptions, query, nodes);
                catalogueTrees[mode] = cached;
            }
            var tree = cached.Nodes;
            PathTreeView.SetItems(ModelTree, tree, query, compactDirectories: mode == ModelCatalogueMode.ResourceType);
            var count = tree.Sum(node => node.LeafCount);
            BrowserStatus.Text = string.IsNullOrWhiteSpace(query) ? $"共 {count:N0} 个模型条目" : $"匹配 {count:N0} 个模型条目";
            if (restoreCataloguePreview)
            {
                restoreCataloguePreview = false;
                if (PathTreeView.SelectedNode(ModelTree) is { } node) await OpenCatalogueNode(node);
            }
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

    private void ClearScene(bool keepAppearanceControls = false)
    {
        ResetItemEffects();
        StopPlayback(restoreScene: false);
        textureCancellation?.Cancel();
        actionDiscoveryCancellation?.Cancel();
        actionReadCancellation?.Cancel();
        actions = null;
        suppressInitialAction = false;
        usePlayerPose = false;
        requestedActionPath = null;
        scene = null;
        MaterialVariant.IsEnabled = source != null;
        MaterialVariant.IsReadOnly = false;
        if (keepAppearanceControls)
        {
            partOptionsCancellation?.Cancel();
            partPreviewCancellation?.Cancel();
            recolorCancellation?.Cancel();
            AppearanceContent.IsEnabled = false;
        }
        else
        {
            ShowCharacterParts(null);
            ShowCharacterAppearance(null);
            SynchronizeCharacterRace(null);
        }
        selectedNamedModel = null;
        itemDyeCancellation?.Cancel();
        ItemDyePanel.Visibility = Visibility.Collapsed;
        ItemDyeControls.Children.Clear();
        ItemHandButton.Visibility = Visibility.Collapsed;
        ModelEntryInfo.Text = "";
        ModelTableLinks.Visibility = Visibility.Collapsed;
        camera.SetBounds(Rect3D.Empty, Rect3D.Empty);
        ModelSurface.Cursor = Cursors.Arrow;
        ModelVisual.Content = null;
        ResetButton.IsEnabled = UseTextures.IsEnabled = false;
        Inspector.Visibility = keepAppearanceControls ? Inspector.Visibility : Visibility.Collapsed;
        CameraHelp.Visibility = Visibility.Collapsed;
        EmptyViewport.Visibility = Visibility.Visible;
        MaterialChoice.ItemsSource = null;
        ClearMaterialShader();
        TextureChoice.ItemsSource = null;
        TextureImage.Source = null;
        BoneNames.Clear();
        ActionList.IsExpanded = true;
        ActionChoice.ItemsSource = null;
        PreviousAction.IsEnabled = NextAction.IsEnabled = false;
        ActionChoice.IsEnabled = ActionFilter.IsEnabled = false;
        ActionFilter.Clear();
        ActionPath.Clear();
        ActionDiscoveryStatus.Text = "";
        ClearActionMetadata();
    }

    private void ShowScene(PreviewScene preview, bool startIdle = true)
    {
        scene = preview;
        var choices = new List<MaterialChoiceItem> { new(-1, "全部材质") };
        var usedMaterials = preview.Meshes.Select(mesh => mesh.MaterialIndex).ToHashSet();
        for (var index = 0; index < preview.Materials.Length; index++)
        {
            if (!usedMaterials.Contains(index)) continue;
            var material = preview.Materials[index].Info;
            var name = Path.GetFileName(material.Path ?? material.Reference);
            choices.Add(new(index, string.IsNullOrEmpty(name) ? $"材质 {index + 1}" : $"{index + 1} · {name}"));
        }

        UseTextures.IsEnabled = preview.Meshes.Any(mesh => mesh.Geometry.TextureCoordinates.Count != 0
            && mesh.MaterialIndex >= 0 && mesh.MaterialIndex < preview.Materials.Length
            && preview.Materials[mesh.MaterialIndex].Surface is not null);
        MaterialChoice.ItemsSource = choices;
        MaterialChoice.SelectedIndex = 0;
        MaterialChoice.IsEnabled = choices.Count > 1;
        BoneList.IsExpanded = false;
        BoneList.Visibility = preview.BoneNames.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        BoneNames.Text = string.Join(Environment.NewLine, preview.BoneNames);
        AnimationStatus.Text = $"{preview.BoneNames.Count:N0} 个模型骨骼 · 可选择与当前基础骨架匹配的 PAP 动作";
        SetModelStatus($"LOD 0 · {preview.Meshes.Count:N0} 个网格 · {preview.Vertices:N0} 个顶点 · {preview.Triangles:N0} 个三角面");
        if (preview.Warnings.Count != 0)
            ModelInfo.ToolTip = ModelInfo.Text + Environment.NewLine + string.Join(Environment.NewLine, preview.Warnings);

        var bounds = Rect3D.Empty;
        foreach (var mesh in preview.Meshes) bounds.Union(mesh.Geometry.Bounds);
        camera.SetBounds(bounds, bounds);
        // 两套装配都选中「全部材质」时，WPF 可能不触发 SelectionChanged。
        RenderScene();
        Inspector.Visibility = CameraHelp.Visibility = Visibility.Visible;
        EmptyViewport.Visibility = Visibility.Collapsed;
        ResetButton.IsEnabled = true;
        ModelSurface.Cursor = Cursors.SizeAll;
        if (startIdle) _ = LoadDefaultAnimationAsync(preview);
        else ActionListExpanded(ActionList, new RoutedEventArgs());
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
        var masked = new List<GeometryModel3D>();
        var blended = new List<GeometryModel3D>();
        for (var i = 0; i < preview.Meshes.Count; i++)
        {
            var mesh = preview.Meshes[i];
            if (materialIndex >= 0 && materialIndex != mesh.MaterialIndex) continue;
            var material = GreyMaterial;
            var alphaMode = GameMaterialAlphaMode.Opaque;
            if (UseTextures.IsChecked == true && mesh.Geometry.TextureCoordinates.Count != 0
                && mesh.MaterialIndex >= 0 && mesh.MaterialIndex < preview.Materials.Length
                && preview.Materials[mesh.MaterialIndex] is { Surface: not null } textured)
            {
                material = textured.Surface;
                alphaMode = textured.Info.AlphaMode;
            }
            var geometry = animatedGeometry?[i] ?? mesh.Geometry;
            var hideBackfaces = mesh.MaterialIndex >= 0 && mesh.MaterialIndex < preview.Materials.Length
                && (preview.Materials[mesh.MaterialIndex].Info.ShaderFlags & 1u) != 0;
            var model = new GeometryModel3D(geometry, material) { BackMaterial = hideBackfaces ? null : material };
            if (alphaMode == GameMaterialAlphaMode.Blend) blended.Add(model);
            else if (alphaMode == GameMaterialAlphaMode.Mask) masked.Add(model);
            else group.Children.Add(model);
        }
        // 先画皮肤和镂空服装，再让半透明布料与已经绘制的底色混合。
        foreach (var model in masked) group.Children.Add(model);
        foreach (var model in blended) group.Children.Add(model);
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
        ClearMaterialShader();
        if (choice.Index < 0)
        {
            var loaded = preview.Materials.Count(material => material.Surface is not null);
            MaterialDetails.Text = $"{preview.Materials.Length:N0} 个材质槽 · {loaded:N0} 个可预览基色";
            return;
        }

        var material = preview.Materials[choice.Index].Info;
        if (material.ShaderPackage is { Length: > 0 }) ShaderInspector.Visibility = Visibility.Visible;
        MaterialDetails.Text = string.Join(Environment.NewLine,
            new[] { material.Path ?? material.Reference, material.ShaderPackage, material.Warning }.Where(value => !string.IsNullOrEmpty(value)));
        var surface = material.SurfaceTexture ?? material.DiffuseTexture;
        var textures = material.TexturePaths.Select(path => new ResourceChoiceItem(path, Path.GetFileName(path))).ToArray();
        var surfaceIndex = Array.FindIndex(textures, texture => texture.Path == surface?.Path);
        if (surface != null && surfaceIndex < 0) textures = [new ResourceChoiceItem("", "模型预览用基色"), .. textures];
        if (textures.Length == 0) return;
        TextureChoice.ItemsSource = textures;
        TextureChoice.Visibility = Visibility.Visible;
        TextureChoice.SelectedIndex = Math.Max(0, surfaceIndex);
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
            var material = preview.Materials[slot.Index].Info;
            var bitmap = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var data = texture.Path.Length == 0 ? (material.SurfaceTexture ?? material.DiffuseTexture)! : game.ReadTexture(texture.Path);
                return CreateBitmap(data, PixelFormats.Bgra32);
            }, token);
            token.ThrowIfCancellationRequested();
            TextureImage.Source = bitmap;
            TextureImage.Visibility = Visibility.Visible;
            TextureStatus.Text = $"{bitmap.PixelWidth:N0} × {bitmap.PixelHeight:N0} · {(texture.Path.Length == 0 ? texture.Label : texture.Path)}";
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
            var playerPose = usePlayerPose;
            var defaultPath = await Task.Run(() => game.FindDefaultModelAnimation(preview.Path, token, playerPose), token);
            var choices = await Task.Run(() => game.ListModelAnimationDescriptions(preview.Path, token)
                .Select(item => new AnimationChoiceItem(item, item.Label)).OrderByDescending(item => item.Path == defaultPath).ToArray(), token);
            token.ThrowIfCancellationRequested();
            actions = choices;
            ActionFilter.IsEnabled = actions.Length > 0;
            ApplyActionFilter();
            if (!suppressInitialAction && requestedActionPath is null && string.IsNullOrWhiteSpace(ActionPath.Text) && actions.Length > 0)
                ActionChoice.SelectedItem = actions.FirstOrDefault(item => item.Path == defaultPath);
            if (actions.Length > 0)
            {
                var verified = await Task.Run(() => game.AuditModelAnimations(preview.Path, token)
                    .Select(item => new AnimationChoiceItem(item.Description, item.Description.Label, item.Slots)).OrderByDescending(item => item.Path == defaultPath).ToArray(), token);
                token.ThrowIfCancellationRequested();
                actions = verified;
                ApplyActionFilter();
            }
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
        var filtered = query.Length == 0 ? actions : actions.Where(item => item.Matches(query)).ToArray();
        ActionChoice.ItemsSource = filtered;
        SyncActionChoice();
        ActionChoice.IsEnabled = filtered.Length > 0;
        PreviousAction.IsEnabled = NextAction.IsEnabled = filtered.Length > 1;
        ActionDiscoveryStatus.Text = actions.Length == 0 ? "未找到本地动作候选，可输入路径。" : $"{filtered.Length:N0} / {actions.Length:N0} 个本地候选";
    }

    private void SyncActionChoice() => ActionChoice.SelectedItem = requestedActionPath is null ? null
        : ActionChoice.Items.Cast<AnimationChoiceItem>().FirstOrDefault(item =>
            string.Equals(item.Path, requestedActionPath, StringComparison.OrdinalIgnoreCase));

    private async void ActionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ActionChoice.SelectedItem is AnimationChoiceItem action
            && !string.Equals(action.Path, requestedActionPath, StringComparison.OrdinalIgnoreCase))
            await OpenAnimation(action.Path);
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
        requestedActionPath = actionPath;
        SyncActionChoice();
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
            settingPlaybackSlots = true;
            try
            {
                PlaybackSlot.ItemsSource = action.Slots;
                PlaybackSlotPicker.Visibility = action.Slots.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
                PreviousPlaybackSlot.IsEnabled = NextPlaybackSlot.IsEnabled = action.Slots.Count > 1;
                PlaybackSlot.SelectedItem = action.Slots.FirstOrDefault(slot => slot.Name == "cbnm_id0")
                    ?? action.Slots.FirstOrDefault(slot => slot.Name == "cbbm_id0") ?? action.Slots.FirstOrDefault();
            }
            finally { settingPlaybackSlots = false; }
            UpdatePlaybackControls();
            await StartPlaybackAsync();
            var description = await Task.Run(() => game.DescribeAnimation(action.Path, action.Slots, token), token);
            token.ThrowIfCancellationRequested();
            ActionRelations.Show(description);
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

    private void SelectPreviousAction(object sender, RoutedEventArgs e) => SelectAdjacent(ActionChoice, -1);

    private void SelectNextAction(object sender, RoutedEventArgs e) => SelectAdjacent(ActionChoice, 1);

    private void SelectPreviousPlaybackSlot(object sender, RoutedEventArgs e) => SelectAdjacent(PlaybackSlot, -1);

    private void SelectNextPlaybackSlot(object sender, RoutedEventArgs e) => SelectAdjacent(PlaybackSlot, 1);

    private void OpenActionTable(object? sender, ModelTableLinkEventArgs e) => RelatedTableRequested?.Invoke(this, e);

    private static void SelectAdjacent(ComboBox picker, int direction)
    {
        var count = picker.Items.Count;
        if (count < 2) return;
        var index = picker.SelectedIndex;
        if (index < 0) index = direction < 0 ? 0 : count - 1;
        picker.SelectedIndex = (index + direction + count) % count;
    }

    private void ClearActionMetadata()
    {
        loadedAction = null;
        StopPlayback();
        PlaybackSlot.ItemsSource = null;
        PlaybackSlotPicker.Visibility = Visibility.Collapsed;
        PlaybackStatus.Text = "";
        ActionMetadataStatus.Text = "";
        ActionRelations.Clear();
    }

    private static PreviewScene BuildScene(GameModelPreview preview, CancellationToken token, PreviewScene? frameOf = null)
    {
        var center = frameOf?.Center ?? default;
        var scale = frameOf?.Scale ?? 1;
        if (frameOf == null)
        {
            var bounds = Rect3D.Empty;
            foreach (var mesh in preview.Meshes)
            {
                token.ThrowIfCancellationRequested();
                foreach (var position in mesh.Positions) bounds.Union(new Point3D(position.X, position.Y, position.Z));
            }
            var size = new Vector3D(bounds.SizeX, bounds.SizeY, bounds.SizeZ);
            if (bounds.IsEmpty || size.Length <= 0) throw new InvalidDataException("模型网格没有可显示的范围。");
            center = new Point3D(bounds.X, bounds.Y, bounds.Z) + size / 2;
            scale = 2 / size.Length;
        }
        var meshes = new List<PreviewMesh>(preview.Meshes.Count);
        foreach (var sourceMesh in preview.Meshes)
        {
            token.ThrowIfCancellationRequested();
            if (frameOf != null && ReusePreviewMesh(sourceMesh, preview, frameOf) is { } cachedMesh)
            {
                meshes.Add(cachedMesh);
                continue;
            }
            var mesh = sourceMesh;
            PreviewAlphaGeometry.VertexSource[]? sources = null;
            if (GetClippingMaterial(mesh, preview) is { SurfaceTexture: { } surface } material)
            {
                (mesh, sources) = PreviewAlphaGeometry.ClipAnimated(mesh, surface, PreviewSurfaceMaterial.CutoffByte(material), token);
            }
            var positions = new Point3DCollection(mesh.Positions.Length);
            foreach (var position in mesh.Positions)
                positions.Add(new Point3D() + (new Point3D(position.X, position.Y, position.Z) - center) * scale);

            var geometry = new MeshGeometry3D { Positions = positions, TriangleIndices = new Int32Collection(mesh.TriangleIndices) };
            if (mesh.Normals.Length == mesh.Positions.Length)
                geometry.Normals = new Vector3DCollection(mesh.Normals.Select(normal => new Vector3D(normal.X, normal.Y, normal.Z)));
            if (mesh.TextureCoordinates.Length == mesh.Positions.Length)
                geometry.TextureCoordinates = new PointCollection(mesh.TextureCoordinates.Select(uv => new Point(uv.X, uv.Y)));
            geometry.Freeze();
            meshes.Add(new(geometry, mesh.MaterialIndex, sources));
        }

        var materials = new PreviewMaterial[preview.Materials.Count];
        var materialCache = new Dictionary<GameModelMaterial, PreviewMaterial>(ReferenceEqualityComparer.Instance);
        if (frameOf != null)
            foreach (var material in frameOf.Materials) materialCache.TryAdd(material.Info, material);
        for (var i = 0; i < materials.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var material = preview.Materials[i];
            if (!materialCache.TryGetValue(material, out var cached)) materialCache.Add(material, cached = BuildPreviewMaterial(material));
            materials[i] = cached;
        }

        return new(preview.Path, meshes, materials, preview.BoneNames, preview.Warnings,
            preview.Meshes.Sum(mesh => mesh.Positions.Length), preview.Meshes.Sum(mesh => mesh.TriangleIndices.Length) / 3,
            preview, center, scale);
    }

    private static PreviewMesh? ReusePreviewMesh(GameModelMesh mesh, GameModelPreview preview, PreviewScene previous)
    {
        var material = GetClippingMaterial(mesh, preview);
        for (var i = 0; i < previous.SourceModel.Meshes.Count; i++)
        {
            var old = previous.SourceModel.Meshes[i];
            if (!ReferenceEquals(mesh.Positions, old.Positions) || !ReferenceEquals(mesh.Normals, old.Normals) ||
                !ReferenceEquals(mesh.TextureCoordinates, old.TextureCoordinates) || !ReferenceEquals(mesh.TriangleIndices, old.TriangleIndices)) continue;
            var oldMaterial = GetClippingMaterial(old, previous.SourceModel);
            if (ReferenceEquals(material, oldMaterial) || material != null && oldMaterial != null &&
                ReferenceEquals(material.SurfaceTexture, oldMaterial.SurfaceTexture) &&
                PreviewSurfaceMaterial.CutoffByte(material) == PreviewSurfaceMaterial.CutoffByte(oldMaterial))
                return previous.Meshes[i] with { MaterialIndex = mesh.MaterialIndex };
        }
        return null;
    }

    private static GameModelMaterial? GetClippingMaterial(GameModelMesh mesh, GameModelPreview preview)
    {
        if (mesh.TextureCoordinates.Length == mesh.Positions.Length &&
            (uint)mesh.MaterialIndex < preview.Materials.Count &&
            preview.Materials[mesh.MaterialIndex] is { AlphaMode: not GameMaterialAlphaMode.Opaque, SurfaceTexture: not null } material)
            return material;
        return null;
    }

    private static PreviewMaterial BuildPreviewMaterial(GameModelMaterial material) =>
        new(material, (material.SurfaceTexture ?? material.DiffuseTexture) is { } diffuse
            ? PreviewSurfaceMaterial.Create(diffuse, material.AlphaMode, PreviewSurfaceMaterial.CutoffByte(material)) : null);

    private static DiffuseMaterial CreateGreyMaterial()
    {
        var material = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(178, 187, 201)));
        material.Freeze();
        return material;
    }

    private static BitmapSource CreateBitmap(GameTexture texture, PixelFormat format)
    {
        var bitmap = BitmapSource.Create(texture.Width, texture.Height, 96, 96, format, null, texture.Bgra32, checked(texture.Width * 4));
        bitmap.Freeze();
        return bitmap;
    }

    private void ResetView(object sender, RoutedEventArgs e) => camera.Reset();

    private sealed record PreviewMesh(MeshGeometry3D Geometry, int MaterialIndex, PreviewAlphaGeometry.VertexSource[]? Sources);
    private sealed record PreviewMaterial(GameModelMaterial Info, DiffuseMaterial? Surface);
    private sealed record PreviewScene(string Path, IReadOnlyList<PreviewMesh> Meshes, PreviewMaterial[] Materials,
        IReadOnlyList<string> BoneNames, IReadOnlyList<string> Warnings, int Vertices, int Triangles,
        GameModelPreview SourceModel, Point3D Center, double Scale);
    private sealed record MaterialChoiceItem(int Index, string Label);
    private sealed record ResourceChoiceItem(string Path, string Label);
    private sealed record AnimationChoiceItem(GameAnimationDescription Info, string Label, IReadOnlyList<GameAnimationSlot>? Slots = null)
    {
        public string Path => Info.Path;
        public bool Matches(string query) => Path.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            Label.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            Slots?.Any(slot => slot.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) == true ||
            Info.References.Any(r => r.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) == true ||
                r.Label.Contains(query, StringComparison.OrdinalIgnoreCase));
    }
}
