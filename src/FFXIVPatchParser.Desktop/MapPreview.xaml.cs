using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace FFXIVPatchParser.Desktop;

public partial class MapPreview : UserControl
{
    private LocalGameSource? source;
    private IReadOnlyList<NamedMapEntry>? maps;
    private NamedMapEntry? selectedMap;
    private CancellationTokenSource? catalogueRequest, previewRequest;
    private Task catalogueLoad = Task.CompletedTask, previewLoad = Task.CompletedTask;
    private bool updatingList;
    private bool previewComplete;
    private GridLength mapBrowserWidth = new(284);
    private string? displayedPath;
    private readonly LinkedList<CachedScene> scenes = new();
    private readonly Model3DGroup visibleScene = new();
    private int opaqueSceneGroups;
    private readonly PreviewCameraController camera;
    private PreviewCameraController.ViewState? suspendedCamera;
    private Rect3D sceneBounds = Rect3D.Empty;
    private Rect3D terrainBounds = Rect3D.Empty;

    public MapPreview()
    {
        InitializeComponent();
        camera = new(MapSurface, MapViewport, MapCamera, Math.PI * 0.14, Math.PI * 0.22);
        MapVisual.Content = visibleScene;
    }

    public event EventHandler<ModelTableLinkEventArgs>? RelatedTableRequested;

    internal void ShowMapBrowser(bool visible)
    {
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (MapBrowserPanel.Visibility == visibility) return;
        if (!visible) mapBrowserWidth = MapBrowserColumn.Width;
        MapBrowserPanel.Visibility = MapBrowserDivider.Visibility = visibility;
        MapBrowserColumn.MinWidth = visible ? 215 : 0;
        MapBrowserColumn.Width = visible ? mapBrowserWidth : new(0);
        MapDividerColumn.Width = new(visible ? 8 : 0);
        if (visible) StartCatalogue();
    }

    internal Task OpenMapFileAsync(NamedMapEntry entry, bool mapImage)
    {
        var changed = selectedMap != entry;
        if (changed)
        {
            selectedMap = entry;
            suspendedCamera = null;
            ShowMapInfo(entry);
        }
        updatingList = true;
        try { MapList.SelectedItem = maps?.FirstOrDefault(map => map.TerritoryId == entry.TerritoryId); }
        finally { updatingList = false; }

        ImageMode.IsEnabled = entry.MapTexturePath != null;
        var mode = mapImage && ImageMode.IsEnabled ? ImageMode : SceneMode;
        if (mode.IsChecked != true) mode.IsChecked = true;
        else if (changed || !previewComplete && previewRequest is null) StartPreview();
        return previewLoad;
    }

    internal void SetSource(LocalGameSource? game)
    {
        if (source == game) return;
        catalogueRequest?.Cancel();
        previewRequest?.Cancel();
        source = game;
        maps = null;
        selectedMap = null;
        suspendedCamera = null;
        scenes.Clear();
        MapList.ItemsSource = null;
        MapCount.Text = "";
        MapName.Text = "区域信息";
        MapFields.ItemsSource = null;
        MapLinks.Children.Clear();
        StartPositionControls.Visibility = Visibility.Collapsed;
        ImageMode.IsEnabled = false;
        SceneMode.IsChecked = true;
        ClearPreview();
        StartCatalogue();
    }

    internal async Task StopLoadingAsync()
    {
        catalogueRequest?.Cancel();
        previewRequest?.Cancel();
        await Task.WhenAll(catalogueLoad, previewLoad);
    }

    private void PageVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (MapList == null) return;
        if (IsVisible)
        {
            StartCatalogue();
            if (selectedMap != null && !previewComplete) StartPreview();
        }
        else
        {
            catalogueRequest?.Cancel();
            previewRequest?.Cancel();
        }
    }

    private void StartCatalogue()
    {
        if (!IsVisible || MapList == null || MapBrowserPanel.Visibility != Visibility.Visible) return;
        if (source == null)
        {
            MapCount.Text = "请在设置中连接游戏目录";
            return;
        }
        if (maps != null || catalogueRequest is { IsCancellationRequested: false }) return;
        var request = new CancellationTokenSource();
        catalogueRequest = request;
        catalogueLoad = ReadCatalogueAsync(source, catalogueLoad, request);
    }

    private async Task ReadCatalogueAsync(LocalGameSource game, Task previous, CancellationTokenSource request)
    {
        MapCount.Text = "正在读取区域目录…";
        RetryButton.Visibility = Visibility.Collapsed;
        try
        {
            await previous;
            request.Token.ThrowIfCancellationRequested();
            var result = await game.ListNamedMapsAsync(request.Token);
            request.Token.ThrowIfCancellationRequested();
            maps = result;
            ApplyMapFilter();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (request.IsCancellationRequested) return;
            MapCount.Text = "读取区域目录失败";
            MapStatus.Text = ex.Message;
            RetryButton.Visibility = Visibility.Visible;
        }
        finally
        {
            if (catalogueRequest == request) catalogueRequest = null;
            request.Dispose();
        }
    }

    private void FilterMaps(object sender, TextChangedEventArgs e) => ApplyMapFilter();

    private void ApplyMapFilter()
    {
        if (MapList == null || maps == null) return;
        var query = MapSearch.Text.Trim();
        var rows = query.Length == 0 ? maps : maps.Where(map => Matches(map, query)).ToArray();
        updatingList = true;
        try
        {
            MapList.ItemsSource = rows;
            if (selectedMap != null) MapList.SelectedItem = rows.FirstOrDefault(map => map.TerritoryId == selectedMap.TerritoryId);
        }
        finally { updatingList = false; }
        MapCount.Text = $"{rows.Count:N0} / {maps.Count:N0} 个区域";
    }

    private static bool Matches(NamedMapEntry entry, string query) => entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || entry.Region.Contains(query, StringComparison.OrdinalIgnoreCase) || entry.Bg.Contains(query, StringComparison.OrdinalIgnoreCase)
        || entry.ContentName.Contains(query, StringComparison.OrdinalIgnoreCase) || entry.Zone.Contains(query, StringComparison.OrdinalIgnoreCase)
        || entry.MapCode.Contains(query, StringComparison.OrdinalIgnoreCase) || entry.Category.Contains(query, StringComparison.OrdinalIgnoreCase)
        || entry.IntendedUse.Contains(query, StringComparison.OrdinalIgnoreCase)
        || (uint.TryParse(query, out var id) && (entry.TerritoryId == id || entry.MapId == id));

    private void SelectMap(object sender, SelectionChangedEventArgs e)
    {
        if (updatingList || MapList.SelectedItem is not NamedMapEntry entry || selectedMap == entry) return;
        selectedMap = entry;
        suspendedCamera = null;
        ShowMapInfo(entry);
        ImageMode.IsEnabled = entry.MapTexturePath != null;
        if (!ImageMode.IsEnabled && ImageMode.IsChecked == true) SceneMode.IsChecked = true;
        else StartPreview();
    }

    private void ShowMapInfo(NamedMapEntry entry)
    {
        MapName.Text = entry.Name;
        var fields = new List<MapField>();
        if (entry.TerritoryId != 0) fields.Add(new("区域 ID", entry.TerritoryId.ToString()));
        if (entry.MapId > 0) fields.Add(new("地图 ID", entry.MapId.ToString()));
        if (entry.Zone.Length > 0 && entry.Zone != entry.Name) fields.Add(new("区域", entry.Zone));
        if (entry.Region.Length > 0 && entry.Region != entry.Zone && entry.Region != entry.Name) fields.Add(new("地域", entry.Region));
        if (entry.ContentName.Length > 0) fields.Add(new("副本／任务", entry.ContentName));
        if (entry.IntendedUse.Length > 0) fields.Add(new("区域用途", $"{entry.IntendedUse} · {entry.IntendedUseId}"));
        if (entry.MapCode.Length > 0) fields.Add(new("地图代号", entry.MapCode));
        fields.Add(new("场景路径", entry.ScenePath));
        if (entry.MapTexturePath is { } texture) fields.Add(new("地图纹理", texture));
        MapFields.ItemsSource = fields;
        MapLinks.Children.Clear();
        if (entry.TerritoryId != 0) AddMapLink("TerritoryType", entry.TerritoryId);
        if (entry.MapId > 0) AddMapLink("Map", entry.MapId);
        if (entry.PlaceNameId > 0) AddMapLink("PlaceName", entry.PlaceNameId);
        ShowStartPosition(entry.TerritoryId);
    }

    private void AddMapLink(string sheet, uint id)
    {
        var button = new Button { Content = $"{sheet} #{id}", Margin = new Thickness(0, 0, 5, 5), Padding = new Thickness(6, 3, 6, 3) };
        button.Click += (_, _) => RelatedTableRequested?.Invoke(this, new ModelTableLinkEventArgs(sheet, id));
        MapLinks.Children.Add(button);
    }

    private void ChangeMode(object sender, RoutedEventArgs e)
    {
        if (MapSurface == null || MapImage == null) return;
        if (ImageMode.IsChecked == true && MapSurface.Visibility == Visibility.Visible && !sceneBounds.IsEmpty)
            suspendedCamera = camera.CaptureView();
        StartPreview();
    }

    private void ClearPreview()
    {
        previewComplete = false;
        displayedPath = null;
        visibleScene.Children.Clear();
        opaqueSceneGroups = 0;
        sceneBounds = terrainBounds = Rect3D.Empty;
        SetSceneEntrance(null);
        camera.SetBounds(sceneBounds, terrainBounds);
        MapImage.SetImage(null);
        EmptyScene.Visibility = Visibility.Visible;
        EmptyScene.Text = "选择一个区域查看 3D 场景";
        CameraHelp.Visibility = Visibility.Collapsed;
        ResetCameraButton.IsEnabled = ResetNearCameraButton.IsEnabled = false;
        CopyCurrentPositionButton.IsEnabled = false;
        MapStatus.Text = "";
        MapStatus.ToolTip = null;
        CancelButton.Visibility = RetryButton.Visibility = Visibility.Collapsed;
    }

    private void StartPreview()
    {
        previewRequest?.Cancel();
        ClearPreview();
        var showImage = ImageMode.IsChecked == true;
        MapSurface.Visibility = showImage ? Visibility.Collapsed : Visibility.Visible;
        MapImage.Visibility = showImage ? Visibility.Visible : Visibility.Collapsed;
        if (!IsVisible || source is not { } game || selectedMap is not { } entry) return;

        var request = new CancellationTokenSource();
        previewRequest = request;
        CancelButton.Visibility = Visibility.Visible;
        EmptyScene.Text = "正在读取场景数据…";
        MapStatus.Text = showImage ? "正在读取地图纹理…" : "正在读取场景布局…";
        previewLoad = LoadPreviewAsync(game, entry, showImage, previewLoad, request);
    }

    private async Task LoadPreviewAsync(LocalGameSource game, NamedMapEntry entry, bool showImage, Task previous,
        CancellationTokenSource request)
    {
        var token = request.Token;
        try
        {
            await previous;
            token.ThrowIfCancellationRequested();
            if (showImage && entry.MapTexturePath is { } texturePath)
            {
                var image = await Task.Run(() => ReadMapImage(game, texturePath, token), token);
                token.ThrowIfCancellationRequested();
                MapImage.SetImage(image, texturePath, entry.Name);
                MapImage.FitToWindow();
                previewComplete = true;
                MapStatus.Text = "";
            }
            else await LoadSceneAsync(game, entry, token);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            if (previewRequest == request)
            {
                EmptyScene.Text = "已取消预览";
                MapStatus.Text = sceneBounds.IsEmpty ? "已取消加载" : "已取消加载 · 可继续查看已加载的部分";
                RetryButton.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            if (previewRequest != request) return;
            EmptyScene.Text = "此区域的场景暂时无法预览";
            MapStatus.Text = $"读取失败 · {ex.Message}";
            RetryButton.Visibility = Visibility.Visible;
        }
        finally
        {
            if (previewRequest == request)
            {
                previewRequest = null;
                CancelButton.Visibility = Visibility.Collapsed;
            }
            request.Dispose();
        }
    }

    private static BitmapSource ReadMapImage(LocalGameSource game, string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var texture = game.ReadTexture(path);
        var image = BitmapSource.Create(texture.Width, texture.Height, 96, 96, PixelFormats.Bgra32, null, texture.Bgra32, texture.Width * 4);
        image.Freeze();
        return image;
    }

    private void CancelPreview(object sender, RoutedEventArgs e) => previewRequest?.Cancel();

    private void RetryPreview(object sender, RoutedEventArgs e)
    {
        if (maps == null) StartCatalogue();
        else StartPreview();
    }

    private sealed record MapField(string Label, string Value);
    private sealed record CachedScene(string Path, Model3D[] Groups, Rect3D Bounds, Rect3D TerrainBounds,
        Point3D? Entrance, string Summary, string? Warning);
}
