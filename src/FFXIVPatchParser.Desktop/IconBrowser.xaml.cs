using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FFXIVPatchParser.Desktop;

public partial class IconBrowser : UserControl
{
    private const int IconIdLimit = 300_000;
    private const int ThumbnailCacheLimit = 256;
    private const double TileGap = 4;
    private const double ScrollbarSpace = 12;
    private readonly Dictionary<int, IconTile> tiles = [];
    private readonly Dictionary<ThumbnailKey, LinkedListNode<(ThumbnailKey Key, ThumbnailResult Value)>> thumbnails = [];
    private readonly LinkedList<(ThumbnailKey Key, ThumbnailResult Value)> thumbnailOrder = [];
    private readonly HashSet<Task> activeWork = [];
    private LocalGameSource? game;
    private CancellationTokenSource? sourceCancellation;
    private CancellationTokenSource? catalogCancellation;
    private CancellationTokenSource? hoverCancellation;
    private IconEntry[]? icons;
    private IconTile? hoveredTile;
    private int rangeStart;
    private int rangeEnd = IconIdLimit;
    private int firstMatch;
    private int matchCount;
    private int imageWidth = 64;
    private int imageHeight = 64;
    private int columns = 1;
    private double rowHeight;
    private bool gridUpdatePending;
    private bool loadingThumbnails;
    private bool catalogFailed;
    private Point? dragStart;
    private double dragOffset;

    internal event Func<string, Task>? TextureRequested;

    public IconBrowser()
    {
        InitializeComponent();
        IsVisibleChanged += BrowserVisibilityChanged;
    }

    internal void SetSource(LocalGameSource? source)
    {
        if (ReferenceEquals(source, game)) return;
        catalogCancellation?.Cancel();
        hoverCancellation?.Cancel();
        sourceCancellation?.Cancel();
        sourceCancellation?.Dispose();
        sourceCancellation = source is null ? null : new CancellationTokenSource();
        game = source;
        icons = null;
        catalogFailed = false;
        thumbnails.Clear();
        thumbnailOrder.Clear();
        Filters.IsEnabled = source is not null;
        UpdateRange();
        StartCatalog();
    }

    internal Task StopAsync()
    {
        SetSource(null);
        return Task.WhenAll(activeWork);
    }

    private void Track(Task work)
    {
        if (work.IsCompleted) return;
        activeWork.Add(work);
        _ = work.ContinueWith(task => Dispatcher.InvokeAsync(() => activeWork.Remove(task)),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void BrowserVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            StartCatalog();
            ScheduleGridUpdate();
            return;
        }

        catalogCancellation?.Cancel();
        hoverCancellation?.Cancel();
        ClearTiles();
    }

    private void StartCatalog()
    {
        if (!IsVisible || game is not { } source || icons is not null || catalogFailed ||
            catalogCancellation is { IsCancellationRequested: false }) return;
        Track(LoadCatalog(source));
    }

    private async Task LoadCatalog(LocalGameSource source)
    {
        using var cancellation = new CancellationTokenSource();
        catalogCancellation = cancellation;
        var token = cancellation.Token;
        ShowEmpty("正在读取图标目录…");
        try
        {
            var entries = await Task.Run(() => BuildCatalog(source, token), token);
            token.ThrowIfCancellationRequested();
            icons = entries;
            UpdateRange();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested) return;
            catalogFailed = true;
            ShowEmpty("读取图标目录失败。", ex.Message, retry: true);
        }
        finally
        {
            if (catalogCancellation == cancellation) catalogCancellation = null;
        }
    }

    private static IconEntry[] BuildCatalog(LocalGameSource source, CancellationToken token)
    {
        var entries = new Dictionary<int, IconEntry>();
        foreach (var path in source.ListIcons(token))
        {
            token.ThrowIfCancellationRequested();
            var name = path.AsSpan(path.LastIndexOf('/') + 1);
            if (!int.TryParse(name[..6], NumberStyles.None, CultureInfo.InvariantCulture, out var id)) continue;
            var highResolution = name.EndsWith("_hr1.tex", StringComparison.Ordinal);
            var localized = path.Contains("/chs/", StringComparison.Ordinal);
            // 优先读取无语言的高清图标，其次是简体中文的高清和普通图标，最后是无语言的普通图标。
            var priority = highResolution && !localized ? 0 : localized ? highResolution ? 1 : 2 : 3;
            if (!entries.TryGetValue(id, out var existing) || priority < existing.Priority)
                entries[id] = new(id, path, priority);
        }
        return entries.Values.OrderBy(entry => entry.Id).ToArray();
    }

    private void RetryCatalog(object sender, RoutedEventArgs e)
    {
        catalogFailed = false;
        StartCatalog();
    }

    private void NumberKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox field) return;
        ApplyNumber(field);
        field.SelectAll();
        e.Handled = true;
    }

    private void NumberFocusLost(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox field) ApplyNumber(field);
    }

    private void ApplyNumber(TextBox field)
    {
        if (field == StartRange || field == EndRange)
        {
            var start = ReadNumber(StartRange, rangeStart, 0, IconIdLimit);
            var end = ReadNumber(EndRange, rangeEnd, 0, IconIdLimit);
            if (start == rangeStart && end == rangeEnd) return;
            rangeStart = start;
            rangeEnd = end;
            UpdateRange();
            return;
        }

        var width = ReadNumber(IconWidth, imageWidth, 24, 160);
        var height = ReadNumber(IconHeight, imageHeight, 24, 160);
        if (width == imageWidth && height == imageHeight) return;
        imageWidth = width;
        imageHeight = height;
        ClearTiles();
        ScheduleGridUpdate();
    }

    private static int ReadNumber(TextBox field, int current, int min, int max)
    {
        var value = int.TryParse(field.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? Math.Clamp(number, min, max) : current;
        field.Text = value.ToString(CultureInfo.InvariantCulture);
        return value;
    }

    private void UpdateRange()
    {
        firstMatch = FindFirstIcon(rangeStart);
        matchCount = Math.Max(0, FindFirstIcon(rangeEnd) - firstMatch);
        IconScroll.ScrollToTop();
        ClearTiles();
        ScheduleGridUpdate();
        if (icons is not null)
        {
            ShowEmpty(rangeStart >= rangeEnd ? "结束 ID 必须大于起始 ID。" : "此编号范围内没有图标。");
            EmptyPanel.Visibility = matchCount == 0 ? Visibility.Visible : Visibility.Collapsed;
            CountLabel.Text = $"显示 {matchCount:N0} / {icons.Length:N0} 个图标";
        }
        else
        {
            if (!catalogFailed)
                ShowEmpty(game is null ? "连接本地游戏后即可浏览图标" : "正在读取图标目录…");
            CountLabel.Text = "";
        }
    }

    private int FindFirstIcon(int id)
    {
        if (icons is not { } entries) return 0;
        var left = 0;
        var right = entries.Length;
        while (left < right)
        {
            var middle = left + (right - left) / 2;
            if (entries[middle].Id < id) left = middle + 1;
            else right = middle;
        }
        return left;
    }

    private void ShowEmpty(string message, string? details = null, bool retry = false)
    {
        EmptyMessage.Text = message;
        EmptyMessage.ToolTip = details;
        RetryCatalogButton.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
        EmptyPanel.Visibility = Visibility.Visible;
    }

    private void IconScrollChanged(object sender, ScrollChangedEventArgs e) => ScheduleGridUpdate();

    private void ScheduleGridUpdate()
    {
        if (gridUpdatePending || !IsVisible) return;
        gridUpdatePending = true;
        _ = Dispatcher.InvokeAsync(UpdateGrid, DispatcherPriority.Background);
    }

    private void UpdateGrid()
    {
        gridUpdatePending = false;
        if (!IsVisible) return;
        if (icons is not { } entries || matchCount == 0)
        {
            IconSurface.Height = 0;
            return;
        }
        if (IconScroll.ViewportWidth <= 0 || IconScroll.ViewportHeight <= 0) return;

        var columnWidth = imageWidth + TileGap;
        var newColumns = Math.Max(1, (int)((IconScroll.ViewportWidth - ScrollbarSpace) / columnWidth));
        var newHeight = imageHeight + TileGap;
        var rowCount = (matchCount + newColumns - 1) / newColumns;
        var extent = rowCount * newHeight + TileGap;
        var offset = Math.Min(IconScroll.VerticalOffset, Math.Max(0, extent - IconScroll.ViewportHeight));
        if (rowHeight > 0 && (columns != newColumns || rowHeight != newHeight))
        {
            var anchor = (int)(IconScroll.VerticalOffset / rowHeight) * columns;
            offset = Math.Min(anchor / newColumns * newHeight, Math.Max(0, extent - IconScroll.ViewportHeight));
            IconScroll.ScrollToVerticalOffset(offset);
        }
        columns = newColumns;
        rowHeight = newHeight;
        IconSurface.Height = extent;

        // 只保留视口内的格子和前后各一行。滚动范围由全部匹配项的行高决定。
        var start = Math.Max(0, (int)(offset / rowHeight) - 1) * columns;
        var end = Math.Min(matchCount, ((int)Math.Ceiling((offset + IconScroll.ViewportHeight) / rowHeight) + 1) * columns);
        foreach (var index in tiles.Keys.Where(index => index < start || index >= end).ToArray())
        {
            var tile = tiles[index];
            CloseTileTooltip(tile);
            IconSurface.Children.Remove(tile.Button);
            tiles.Remove(index);
        }
        var dpi = VisualTreeHelper.GetDpi(IconSurface);
        var thumbnailSize = Math.Min(256, (int)Math.Ceiling(Math.Max(imageWidth * dpi.DpiScaleX, imageHeight * dpi.DpiScaleY)));
        for (var index = start; index < end; index++)
        {
            if (!tiles.TryGetValue(index, out var tile))
            {
                tile = AddTile(entries[firstMatch + index]);
                tiles.Add(index, tile);
            }
            tile.Button.Width = imageWidth;
            tile.Button.Height = imageHeight;
            Canvas.SetLeft(tile.Button, TileGap + index % columns * columnWidth);
            Canvas.SetTop(tile.Button, TileGap + index / columns * rowHeight);
            var key = new ThumbnailKey(tile.Icon.Path, thumbnailSize);
            if (tile.Key == key) continue;
            tile.Key = key;
            tile.IsResolved = false;
            UpdateTile(tile, GetCachedThumbnail(key));
        }
        if (!loadingThumbnails) Track(LoadVisibleThumbnails());
    }

    private IconTile AddTile(IconEntry icon)
    {
        var button = new Button
        {
            Style = (Style)FindResource("IconTileButton"),
            ContentTemplate = (DataTemplate)FindResource("IconTileTemplate"),
            ToolTip = new ToolTip { Content = $"图标 {icon.Id:D6}", MaxWidth = SystemParameters.WorkArea.Width / 2 }
        };
        var tile = new IconTile(icon, button);
        button.Tag = tile;
        System.Windows.Automation.AutomationProperties.SetName(button, $"图标 {icon.Id:D6}");
        var save = new MenuItem
        {
            Header = "导出为 PNG",
            ToolTip = "导出为 PNG",
            Icon = new System.Windows.Shapes.Path
            {
                Width = 14, Height = 14, Stretch = Stretch.Uniform,
                Data = (Geometry)FindResource("ExportGlyph"), Fill = (Brush)FindResource("Ink")
            }
        };
        save.Click += SaveIcon;
        button.ContextMenu = new ContextMenu { PlacementTarget = button, Items = { save } };
        IconSurface.Children.Add(button);
        return tile;
    }

    private void UpdateTile(IconTile tile, ThumbnailResult? value)
    {
        tile.IsResolved = value is not null;
        tile.Error = value?.Error;
        tile.Button.Content = new TileContent(value?.Image,
            value is null ? "…" : value.Error is null ? "" : "!");
    }

    private void ClearTiles()
    {
        if (hoveredTile is { } tile) CloseTileTooltip(tile);
        IconSurface.Children.Clear();
        tiles.Clear();
    }

    private async Task LoadVisibleThumbnails()
    {
        loadingThumbnails = true;
        try
        {
            while (IsVisible && game is { } source && sourceCancellation is { } cancellation &&
                   tiles.Values.FirstOrDefault(tile => !tile.IsResolved) is { } pending)
            {
                var key = pending.Key;
                var cached = GetCachedThumbnail(key);
                if (cached is not null)
                {
                    UpdateTile(pending, cached);
                    continue;
                }
                var token = cancellation.Token;
                try
                {
                    var value = await Task.Run(() => ReadThumbnail(source, key), token);
                    token.ThrowIfCancellationRequested();
                    StoreThumbnail(key, value);
                    if (pending.Key == key && IconSurface.Children.Contains(pending.Button)) UpdateTile(pending, value);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            }
        }
        finally
        {
            loadingThumbnails = false;
        }
    }

    private static ThumbnailResult ReadThumbnail(LocalGameSource source, ThumbnailKey key)
    {
        try
        {
            return new(SqPackBrowser.CreateThumbnail(source.ReadTexture(key.Path), key.Size), null);
        }
        catch (Exception ex)
        {
            return new(null, ex.Message);
        }
    }

    private ThumbnailResult? GetCachedThumbnail(ThumbnailKey key)
    {
        if (!thumbnails.TryGetValue(key, out var node)) return null;
        thumbnailOrder.Remove(node);
        thumbnailOrder.AddFirst(node);
        return node.Value.Value;
    }

    private void StoreThumbnail(ThumbnailKey key, ThumbnailResult value)
    {
        thumbnails.Add(key, thumbnailOrder.AddFirst((key, value)));
        if (thumbnailOrder.Count <= ThumbnailCacheLimit || thumbnailOrder.Last is not { } oldest) return;
        thumbnailOrder.RemoveLast();
        thumbnails.Remove(oldest.Value.Key);
    }

    private async void OpenIcon(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: IconTile tile } && TextureRequested is { } open)
            await open(tile.Icon.Path);
    }

    private void SaveIcon(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem item &&
            ItemsControl.ItemsControlFromItemContainer(item) is ContextMenu { PlacementTarget: Button { Tag: IconTile tile } } &&
            game is { } source && sourceCancellation is { } lifetime)
            Track(ExportIcon(source, tile.Icon, lifetime.Token));
    }

    private async Task ExportIcon(LocalGameSource source, IconEntry icon, CancellationToken token)
    {
        try
        {
            var image = await DecodeOriginal(source, icon.Path, token);
            token.ThrowIfCancellationRequested();
            var saved = await TexturePreview.ExportPngAsync(image, icon.Path, Window.GetWindow(this));
            if (!token.IsCancellationRequested && saved is not null) CountLabel.Text = $"已保存 {saved}";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested) CountLabel.Text = $"无法保存图标 {icon.Id:D6}：{ex.Message}";
        }
    }

    private static Task<BitmapSource> DecodeOriginal(LocalGameSource source, string path, CancellationToken token) =>
        Task.Run(() => SqPackBrowser.CreateBitmap(source.ReadTexture(path)), token);

    private void OpenHoverPreview(object sender, ToolTipEventArgs e)
    {
        if (sender is not Button { Tag: IconTile tile, ToolTip: ToolTip tooltip }) return;
        hoverCancellation?.Cancel();
        hoveredTile = tile;
        var label = $"图标 {tile.Icon.Id:D6}";
        tooltip.Content = tile.Error is { } error ? $"{label}\n{error}" : label;
        if (game is { } source && tile.Error is null)
            Track(LoadHoverPreview(source, tile, tooltip, label));
    }

    private async Task LoadHoverPreview(LocalGameSource source, IconTile tile, ToolTip tooltip, string label)
    {
        using var cancellation = new CancellationTokenSource();
        hoverCancellation = cancellation;
        var token = cancellation.Token;
        try
        {
            var image = await DecodeOriginal(source, tile.Icon.Path, token);
            token.ThrowIfCancellationRequested();
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = label, HorizontalAlignment = HorizontalAlignment.Center, Margin = new(0, 0, 0, 5) });
            panel.Children.Add(new Image
            {
                Source = image,
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                MaxWidth = SystemParameters.WorkArea.Width / 2,
                MaxHeight = SystemParameters.WorkArea.Height / 2
            });
            tooltip.Content = panel;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested) tooltip.Content = $"{label}\n{ex.Message}";
        }
        finally
        {
            if (hoverCancellation == cancellation) hoverCancellation = null;
        }
    }

    private void CloseHoverPreview(object sender, ToolTipEventArgs e)
    {
        if (sender is Button { Tag: IconTile tile }) ClearHoverPreview(tile);
    }

    private void CloseTileTooltip(IconTile tile)
    {
        ((ToolTip)tile.Button.ToolTip).IsOpen = false;
        ClearHoverPreview(tile);
    }

    private void ClearHoverPreview(IconTile tile)
    {
        if (!ReferenceEquals(hoveredTile, tile)) return;
        hoverCancellation?.Cancel();
        hoveredTile = null;
        ((ToolTip)tile.Button.ToolTip).Content = $"图标 {tile.Icon.Id:D6}";
    }

    private void StartGridDrag(object sender, MouseButtonEventArgs e)
    {
        if (!IconSurface.IsMouseOver || IconScroll.ScrollableHeight <= 0) return;
        dragStart = e.GetPosition(IconScroll);
        dragOffset = IconScroll.VerticalOffset;
    }

    private void DragGrid(object sender, MouseEventArgs e)
    {
        if (dragStart is not { } start) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            dragStart = null;
            return;
        }
        var shift = start.Y - e.GetPosition(IconScroll).Y;
        if (!IconScroll.IsMouseCaptured)
        {
            if (Math.Abs(shift) < SystemParameters.MinimumVerticalDragDistance || !IconScroll.CaptureMouse()) return;
        }
        IconScroll.ScrollToVerticalOffset(dragOffset + shift);
        e.Handled = true;
    }

    private void StopGridDrag(object sender, MouseButtonEventArgs e)
    {
        dragStart = null;
        if (!IconScroll.IsMouseCaptured) return;
        IconScroll.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void GridCaptureLost(object sender, MouseEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, IconScroll)) dragStart = null;
    }

    private sealed record IconEntry(int Id, string Path, int Priority);
    private readonly record struct ThumbnailKey(string Path, int Size);
    private sealed record ThumbnailResult(BitmapSource? Image, string? Error);
    private sealed record TileContent(BitmapSource? Image, string Indicator);
    private sealed class IconTile(IconEntry icon, Button button)
    {
        public IconEntry Icon { get; } = icon;
        public Button Button { get; } = button;
        public ThumbnailKey Key { get; set; }
        public bool IsResolved { get; set; }
        public string? Error { get; set; }
    }
}
