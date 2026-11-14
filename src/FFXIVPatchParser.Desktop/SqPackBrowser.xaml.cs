using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FFXIVPatchParser.Desktop;

public enum SqPackPage { Tables, Images, Models, Audio, PatchImages, PatchModels, PatchAudio }

public partial class SqPackBrowser : UserControl
{
    private const int RowsPerPage = 100, IconsPerPage = 24;
    private static string PathSetting => Path.Combine(Workspace.Temp, "ui", "sqpack-path.txt");

    private LocalGameSource? game;
    private CancellationTokenSource? operation;
    private SqPackPage? workPage;
    private (string Message, bool Error) connectionStatus;
    private readonly Dictionary<SqPackPage, (string Message, bool Error)> pageStatuses = new();
    private Task? autoConnectTask, runningWork;
    private string? autoConnectFailure;
    private bool patchWork, initialized, closing;
    private SqPackPage currentPage = SqPackPage.Tables;
    private string? selectedTable;
    private TablePage? tablePage;
    private int firstTableOffset, editingColumn;
    private bool loadingTableRows;
    private readonly Dictionary<int, string> columnFilters = new();
    private SheetCell? selectedCell;
    private IReadOnlyList<string>? texturePaths;
    private CancellationTokenSource? textureSearchCancellation;
    private PathTreeNode? galleryDirectory;
    private string[] galleryPaths = [];
    private int iconPage;
    private CancellationTokenSource? thumbnailCancellation;
    private Task thumbnailTask = Task.CompletedTask;
    private LoadedPatch? patch;
    private string[] patchArchives = [];
    private IReadOnlyList<PatchImageEntry> patchImages = [];

    public SqPackBrowser()
    {
        InitializeComponent();
        initialized = true;
        ResetPatchMedia();
        UpdateControls();
        SetConnectionStatus("请在设置中连接游戏目录。");
    }

    public event Action<SqPackPage>? PageRequested;
    public event Action? MediaControlsChanged;
    public event Action? GameVersionChanged;

    public static string? RememberedGamePath()
    {
        var configured = Environment.GetEnvironmentVariable("FFXIV_GAME_PATH");
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        return File.Exists(PathSetting) ? File.ReadAllText(PathSetting).Trim() : null;
    }

    public string? ConnectedGamePath => game?.SqPackPath;
    public string GameVersionLabel { get; private set; } = "未连接";
    public string ConnectionStatus => connectionStatus.Message;
    public bool HasConnectionError => connectionStatus.Error;

    public void ShowPage(SqPackPage page)
    {
        CloseTablePopups();
        currentPage = page;
        TablesPanel.Visibility = Visible(SqPackPage.Tables);
        LocalImagesPanel.Visibility = Visible(SqPackPage.Images);
        LocalModelsPanel.Visibility = Visible(SqPackPage.Models);
        LocalAudioPanel.Visibility = Visible(SqPackPage.Audio);
        PatchImagesPanel.Visibility = Visible(SqPackPage.PatchImages);
        PatchModelsPanel.Visibility = Visible(SqPackPage.PatchModels);
        PatchAudioPanel.Visibility = Visible(SqPackPage.PatchAudio);
        DisplayBrowserStatus();
        MediaControlsChanged?.Invoke();

        Visibility Visible(SqPackPage target) => page == target ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetPatch(LoadedPatch? loaded, string[] archivePaths)
    {
        if (patchWork) operation?.Cancel();
        patchCatalogueCancellation?.Cancel();
        patch = loaded;
        patchArchives = archivePaths.ToArray();
        pageStatuses.Remove(SqPackPage.PatchImages);
        pageStatuses.Remove(SqPackPage.PatchModels);
        pageStatuses.Remove(SqPackPage.PatchAudio);
        ResetPatchImages();
        ResetPatchMedia();
        StartPatchCatalogue();
    }

    private async void BrowserLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= BrowserLoaded;
        if (Window.GetWindow(this) is { } window)
        {
            window.Closed += BrowserClosed;
            window.PreviewMouseDown += DismissTableCellOnOutsideClick;
            window.PreviewKeyDown += CellPopupKeyDown;
            window.Deactivated += (_, _) => CloseTableCell();
            window.LocationChanged += (_, _) => CloseTableCell();
            window.SizeChanged += (_, _) => CloseTableCell();
        }
        await AutoConnect();
    }

    private async void BrowserVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!initialized) return;
        if (IsVisible) await AutoConnect();
        else CloseTablePopups();
    }

    private void CloseTablePopups()
    {
        TableFilterPopup.IsOpen = false;
        CloseTableCell();
    }

    private Task AutoConnect()
    {
        if (closing || game != null) return Task.CompletedTask;
        return autoConnectTask ??= ConnectRememberedGame();
    }

    private async Task ConnectRememberedGame()
    {
        string? path;
        try
        {
            path = RememberedGamePath();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            autoConnectFailure = "读取上次目录失败：" + ex.Message;
            SetConnectionStatus(autoConnectFailure, true);
            return;
        }
        if (runningWork is { } pending) await pending;
        if (!string.IsNullOrWhiteSpace(path) && game == null && !closing)
        {
            await ConnectGame(path, remember: false);
            if (game == null) autoConnectFailure = ConnectionStatus;
        }
    }

    private async Task<bool> EnsureGameForNavigation()
    {
        await AutoConnect();
        if (runningWork is { } pending) await pending;
        if (closing) return false;
        if (game != null) return true;
        SetStatus(autoConnectFailure ?? "请先选择包含 sqpack 的本地游戏目录。", true);
        return false;
    }

    private async void BrowserClosed(object? sender, EventArgs e)
    {
        closing = true;
        operation?.Cancel();
        patchCatalogueCancellation?.Cancel();
        imageCatalogCancellation?.Cancel();
        textureSearchCancellation?.Cancel();
        await StopTablePresentation();
        await StopThumbnails();
        await patchCatalogueTask;
        if (operation == null) DisposeGame();
    }

    private void DisposeGame()
    {
        var source = game;
        game = null;
        SetMediaSource(null);
        source?.Dispose();
    }

    public async Task ConnectGame(string path, bool remember = true)
    {
        if (runningWork is { } pending) await pending;
        await Work("正在读取 SqPack 目录…", async token =>
        {
            imageCatalogCancellation?.Cancel();
            await StopTablePresentation();
            await StopThumbnails();
            var source = await Task.Run(() => new LocalGameSource(path), token);
            patchCatalogueCancellation?.Cancel();
            await patchCatalogueTask;
            if (token.IsCancellationRequested)
            {
                source.Dispose();
                token.ThrowIfCancellationRequested();
            }
            DisposeGame();
            game = source;
            pageStatuses.Clear();
            SetMediaSource(source);
            tableIconCache.Clear();
            ResetTableRelations();
            GameVersionLabel = GetGameVersionLabel(source);
            GameVersionChanged?.Invoke();
            selectedTable = null;
            ResetTable();
            ApplyTableSearch();
            ResetLocalImages();
            ResetPatchImages();
            ResetPatchMedia();
            if (remember)
            {
                try
                {
                    await SavePath(source.SqPackPath, token);
                }
                catch (IOException ex) { return "已连接；保存目录失败：" + ex.Message; }
                catch (UnauthorizedAccessException ex) { return "已连接；保存目录失败：" + ex.Message; }
            }
            return $"已连接 SqPack · {source.SheetNames.Count:N0} 张表";
        }, connectionWork: true);
        StartPatchCatalogue();
    }

    private static async Task SavePath(string sqPackPath, CancellationToken token)
    {
        var setting = PathSetting;
        Directory.CreateDirectory(Path.GetDirectoryName(setting)!);
        var partial = setting + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            await File.WriteAllTextAsync(partial, sqPackPath, token);
            token.ThrowIfCancellationRequested();
            File.Move(partial, setting, overwrite: true);
        }
        finally { File.Delete(partial); }
    }

    private static string GetGameVersionLabel(LocalGameSource source)
    {
        try
        {
            if (source.GameVersion is { Length: > 0 } version) return version;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return "版本未知";
    }

    private Task Work(string message, Func<CancellationToken, Task<string>> action, bool forPatch = false, bool connectionWork = false)
    {
        if (operation != null || closing) return Task.CompletedTask;
        return runningWork = RunWork(message, action, forPatch, connectionWork ? null : currentPage);
    }

    private async Task RunWork(string message, Func<CancellationToken, Task<string>> action, bool forPatch, SqPackPage? page)
    {
        using var current = new CancellationTokenSource();
        operation = current;
        workPage = page;
        patchWork = forPatch;
        CloseTablePopups();
        UpdateControls();
        SetWorkStatus(page, message);
        try
        {
            var result = await action(current.Token);
            current.Token.ThrowIfCancellationRequested();
            SetWorkStatus(page, result);
        }
        catch (OperationCanceledException) { if (!closing) SetWorkStatus(page, "已取消"); }
        catch (Exception ex) { if (!closing) SetWorkStatus(page, "读取失败：" + ex.Message, true); }
        finally
        {
            operation = null;
            workPage = null;
            patchWork = false;
            if (closing)
            {
                await StopTablePresentation();
                await StopThumbnails();
                await patchCatalogueTask;
                DisposeGame();
            }
            else
            {
                UpdateControls();
                StartPatchCatalogue();
            }
        }
    }

    private void UpdateControls()
    {
        var idle = operation == null;
        TablesPanel.IsEnabled = idle && game != null;
        LocalImagesPanel.IsEnabled = idle && game != null;
        LocalModelsPanel.IsEnabled = idle && game != null;
        LocalAudioPanel.IsEnabled = idle && game != null;
        PatchImagesPanel.IsEnabled = PatchModelsPanel.IsEnabled = PatchAudioPanel.IsEnabled = idle;
        DisplayBrowserStatus();
        MediaControlsChanged?.Invoke();
    }

    private IProgress<string> WorkProgress(CancellationToken token)
    {
        var page = workPage;
        return new Progress<string>(message =>
        {
            if (operation?.Token == token && !token.IsCancellationRequested) SetWorkStatus(page, message);
        });
    }

    private void SetWorkStatus(SqPackPage? page, string message, bool error = false)
    {
        if (page is { } selected) pageStatuses[selected] = (message, error);
        else connectionStatus = (message, error);
        DisplayBrowserStatus();
    }

    private void SetStatus(string message, bool error = false) => SetWorkStatus(currentPage, message, error);
    private void SetConnectionStatus(string message, bool error = false) => SetWorkStatus(null, message, error);

    private void DisplayBrowserStatus()
    {
        var busy = operation != null && (workPage == null || workPage == currentPage);
        var fallback = game == null || (busy && workPage == null) ? connectionStatus : ("", false);
        var (message, error) = busy && workPage == null ? fallback : pageStatuses.GetValueOrDefault(currentPage, fallback);
        BrowserStatus.Text = message;
        BrowserStatus.Foreground = error ? Brushes.Firebrick : (Brush)FindResource("Muted");
        CancelBrowserButton.Visibility = BrowserProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BrowserStatusBar.Visibility = busy || !string.IsNullOrEmpty(message) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CancelBrowser(object sender, RoutedEventArgs e) => operation?.Cancel();

    private void FilterTables(object sender, TextChangedEventArgs e)
    {
        if (initialized) ApplyTableSearch();
    }

    private void ApplyTableSearch()
    {
        var names = game?.SheetNames ?? [];
        var tree = PathTree.Build(names, name => name, TableSearch.Text);
        TablesList.ItemsSource = tree;
        TableCount.Text = $"{tree.Sum(node => node.LeafCount):N0} / {names.Count:N0} 张表";
    }

    private async void TableSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not PathTreeNode { Payload: string name } || name == selectedTable) return;
        selectedTable = name;
        ResetTable();
        await ReadRows();
    }

    private void ResetTable()
    {
        columnFilters.Clear();
        ResetRows();
    }

    private void ResetRows()
    {
        tablePresentationCancellation?.Cancel();
        pendingTableView = null;
        CloseTablePopups();
        tablePage = null;
        firstTableOffset = 0;
        RenderTable();
    }

    private async Task ReadRows()
    {
        if (game is not { } source || selectedTable is not { } name) return;
        var filters = columnFilters.ToArray();
        await Work($"正在读取 {name}…", async token =>
        {
            tablePresentationCancellation?.Cancel();
            pendingTableView = null;
            var result = await ReadTablePage(source, name, 0, filters, token);
            token.ThrowIfCancellationRequested();
            firstTableOffset = 0;
            tablePage = result;
            RenderTable();
            QueueTablePresentation();
            return result.Rows.Count == 0 ? (filters.Length == 0 ? "表中没有数据行" : "整张表中没有匹配行") : $"已读取 {name}";
        });
    }

    private async Task<TablePage> ReadTablePage(LocalGameSource source, string name, int offset,
        KeyValuePair<int, string>[] filters, CancellationToken token)
    {
        bool Matches(RowKey key, SheetCell[] cells) => filters.All(filter =>
            (filter.Key < 0 ? key.ToString() : cells[filter.Key].Display).Contains(filter.Value, StringComparison.OrdinalIgnoreCase));
        var page = await Task.Run(() => source.ReadSheetPage(name, offset, RowsPerPage, filters.Length == 0 ? null : Matches, token), token);
        var raw = new SheetData(page.Columns, page.Rows);
        var view = RawTableView(name, raw);
        return new(view.Data.Columns, BuildTableRows(view), page.Language, page.HasMore ? page.NextOffset : null, raw);
    }

    private void RenderTable(bool scrollToFirst = true)
    {
        var restore = TableColumnSizing.Capture(TableGrid, !scrollToFirst);
        tableIconCancellation?.Cancel();
        CloseTableCell();
        TableGrid.ItemsSource = null;
        TableGrid.FrozenColumnCount = 0;
        TableGrid.Columns.Clear();
        ReloadRowsButton.IsEnabled = selectedTable != null;
        FitTableColumnsButton.IsEnabled = tablePage?.Rows.Count > 0;
        ClearTableFiltersButton.IsEnabled = columnFilters.Count != 0;
        TableInfo.Text = selectedTable ?? "选择一张表查看完整内容";
        if (tablePage is not { } page) return;
        var language = page.Language == "ChineseSimplified" ? "简体中文" : "无语言";
        TableInfo.Text = $"{selectedTable} · {language} · {(showRelations ? "关联" : "Raw")}" +
            (columnFilters.Count == 0 ? "" : $" · {columnFilters.Count} 个筛选");
        AddTableColumn(-1, "Row ID", "行号或 行号.子行号", "Key");
        for (var i = 0; i < page.Columns.Count; i++)
        {
            var column = page.Columns[i];
            var rawName = page.Raw.Columns[i].Name;
            var detail = column.Name == rawName ? rawName : $"{column.Name} · {rawName}";
            var iconOnly = IconOnlyColumn(page, i);
            var hasIcons = iconOnly || page.Rows.Any(row => row.Related[i]?.PreviewIconPath != null);
            var hasLinks = page.Rows.Any(row => row.Related[i]?.IsLink == true);
            AddTableColumn(i, column.Name, $"{detail} · {column.Type} · 偏移 {column.Offset}", $"GridValues[{i}]",
                hasIcons, iconOnly, hasLinks);
        }
        TableGrid.FrozenColumnCount = 1;
        TableGrid.ItemsSource = page.Rows;
        ApplyTableColumnSizing(page);
        restore();
        if (scrollToFirst && page.Rows.Count > 0) TableGrid.ScrollIntoView(page.Rows[0]);
        QueueTableIconPreviews();
    }

    private void AddTableColumn(int index, string name, string detail, string binding, bool hasIcons = false, bool iconOnly = false, bool hasLinks = false)
    {
        var active = columnFilters.TryGetValue(index, out var term);
        var header = new Button
        {
            Content = new TextBlock { Text = name + (active ? " ●" : " ▾"), TextTrimming = TextTrimming.CharacterEllipsis },
            ToolTip = detail + (active ? $" · 筛选：{term}" : " · 点击筛选"),
            Padding = new Thickness(4, 2, 4, 2),
            Margin = new Thickness(0),
            Background = active ? Brushes.AliceBlue : Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
        };
        header.Click += (_, _) => OpenTableFilter(index, name, header);
        var column = hasIcons
            ? (DataGridColumn)new DataGridTemplateColumn { CellTemplate = TableIconTemplate(index, binding, !iconOnly, hasLinks) }
            : new DataGridTextColumn { Binding = new Binding(binding), ElementStyle = TableValueStyle(index, hasLinks) };
        column.Header = header;
        column.Width = DataGridLength.SizeToHeader;
        TableGrid.Columns.Add(column);
    }

    private async void ReloadRows(object sender, RoutedEventArgs e)
    {
        ResetRows();
        await ReadRows();
    }

    private async Task LoadMoreTableRows(bool prepend = false)
    {
        if (loadingTableRows || operation != null || tablePage is not { } previous
            || game is not { } source || selectedTable is not { } name) return;
        if (prepend ? firstTableOffset == 0 : previous.NextOffset == null) return;
        var offset = prepend ? Math.Max(0, firstTableOffset - RowsPerPage) : previous.NextOffset!.Value;
        loadingTableRows = true;
        try
        {
            var filters = columnFilters.ToArray();
            await Work($"正在读取 {name}…", async token =>
            {
                var next = await ReadTablePage(source, name, offset, filters, token);
                token.ThrowIfCancellationRequested();
                if (tablePage?.Raw != previous.Raw || selectedTable != name) return "";
                tablePresentationCancellation?.Cancel();
                pendingTableView = null;
                var rawRows = (prepend ? next.Raw.Rows.Concat(previous.Raw.Rows) : previous.Raw.Rows.Concat(next.Raw.Rows))
                    .ToDictionary(pair => pair.Key, pair => pair.Value);
                tablePage = previous with { Rows = (prepend ? next.Rows.Concat(previous.Rows) : previous.Rows.Concat(next.Rows)).ToArray(),
                    NextOffset = prepend ? previous.NextOffset : next.NextOffset, Raw = new SheetData(previous.Raw.Columns, rawRows) };
                if (prepend) firstTableOffset = offset;
                RenderTable(scrollToFirst: false);
                if (prepend) TableColumnSizing.ShiftRows(TableGrid, next.Rows.Count);
                QueueTablePresentation();
                return $"已加载 {name} · {tablePage.Rows.Count:N0} 行";
            });
        }
        finally { loadingTableRows = false; }
    }

    private void OpenTableFilter(int index, string name, Button anchor)
    {
        CloseTableCell();
        editingColumn = index;
        TableFilterTitle.Text = $"{name} · 筛选";
        TableFilterInput.Text = columnFilters.GetValueOrDefault(index, "");
        TableFilterPopup.PlacementTarget = anchor;
        TableFilterPopup.IsOpen = true;
        TableFilterInput.Focus();
        TableFilterInput.SelectAll();
    }

    private async void TableFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Escape or Key.Enter)) return;
        e.Handled = true;
        if (e.Key == Key.Escape) TableFilterPopup.IsOpen = false;
        else await SetTableFilter(TableFilterInput.Text);
    }
    private async void ApplyTableFilter(object sender, RoutedEventArgs e) => await SetTableFilter(TableFilterInput.Text);
    private async void ClearTableFilter(object sender, RoutedEventArgs e) => await SetTableFilter("");
    private async void ClearTableFilters(object sender, RoutedEventArgs e)
    {
        columnFilters.Clear();
        ResetRows();
        await ReadRows();
    }

    private async Task SetTableFilter(string text)
    {
        var term = text.Trim();
        if (term.Length == 0) columnFilters.Remove(editingColumn);
        else columnFilters[editingColumn] = term;
        ResetRows();
        await ReadRows();
    }

    private async void TableCellClicked(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject) is { } cell) await OpenCell(cell);
    }

    private async void TableGridKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject) is { } cell)
        {
            e.Handled = true;
            await OpenCell(cell, focusValue: true);
        }
        else if (CellPopup.IsOpen) CloseTableCell();
    }

    internal static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T result) return result;
            current = current is FrameworkContentElement content ? content.Parent : VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private async Task OpenCell(DataGridCell cell, bool focusValue = false)
    {
        if (CellPopup.IsOpen && ReferenceEquals(CellPopup.PlacementTarget, cell))
        {
            if (focusValue) CellText.Focus();
            else CloseTableCell();
            return;
        }
        CloseTableCell();
        if (cell.DataContext is not TableRow row || tablePage is not { } page) return;
        var index = cell.Column.DisplayIndex == 0 ? -1 : cell.Column.DisplayIndex - 1;
        selectedCell = index < 0 ? new SheetCell(row.Key, row.Key) : row.Cells[index];
        CellTitle.Text = index < 0 ? "Row ID" : $"{page.Columns[index].Name} · Row {row.Key}";
        RawCellToggle.Visibility = selectedCell.Display == selectedCell.Canonical ? Visibility.Collapsed : Visibility.Visible;
        RawCellToggle.IsChecked = false;
        CellText.Text = selectedCell.Display;
        CellText.ScrollToHome();
        CellPopup.PlacementTarget = cell;
        CellPopup.IsOpen = true;
        if (focusValue) CellText.Focus();
        if (index >= 0) await ShowCellRelation(row.Id, index, page.Raw, row.Related[index]?.Relation);
    }

    private void ToggleRawCell(object sender, RoutedEventArgs e)
    {
        if (selectedCell != null) CellText.Text = RawCellToggle.IsChecked == true ? selectedCell.Canonical : selectedCell.Display;
    }
    private void CellPopupKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !CellPopup.IsOpen) return;
        var restoreFocus = TableCellContent.IsKeyboardFocusWithin;
        var cell = CellPopup.PlacementTarget;
        CloseTableCell();
        if (restoreFocus) cell?.Focus();
        e.Handled = true;
    }

    private void DismissTableCellOnOutsideClick(object sender, MouseButtonEventArgs e)
    {
        if (!CellPopup.IsOpen || TableCellContent.IsMouseOver) return;
        if (e.ChangedButton == MouseButton.Left &&
            FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject)?.DataContext is TableRow) return;
        CloseTableCell();
    }

    private async void TableGridScrolled(object sender, ScrollChangedEventArgs e)
    {
        if (e.HorizontalChange != 0 || e.VerticalChange != 0) CloseTableCell();
        if (e.VerticalChange > 0 && e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 10)
            await LoadMoreTableRows();
        else if (e.VerticalChange < 0 && e.VerticalOffset <= 10) await LoadMoreTableRows(prepend: true);
    }

    private void TableGridLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (pendingTableView != null) Dispatcher.BeginInvoke(DispatcherPriority.Background, ApplyTablePresentation);
    }
    private void CopyCell(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetDataObject(CellText.Text, true);
            SetStatus("已复制单元格内容");
        }
        catch (ExternalException ex) { SetStatus("复制失败：" + ex.Message, true); }
    }

    private void ResetLocalImages()
    {
        imageCatalogCancellation?.Cancel();
        textureSearchCancellation?.Cancel();
        texturePaths = null;
        imageIndexScanned = false;
        IconSearch.Clear();
        ShowTextureTree([]);
        TexturePath.Clear();
        LocalTexturePreview.SetImage(null);
        StartImageCatalogIfVisible();
    }

    private async Task ScanIcons()
    {
        if (game is not { } source) return;
        imageCatalogCancellation?.Cancel();
        textureSearchCancellation?.Cancel();
        var query = IconSearch.Text;
        await Work("正在扫描 UI 容器中的纹理…", async token =>
        {
            var (paths, tree) = await Task.Run(() =>
            {
                IReadOnlyList<string> result = source.ListIcons(token).Concat(source.ListUiTextures(token))
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                return (result, PathTree.Build(result, path => path, query, token: token));
            }, token);
            token.ThrowIfCancellationRequested();
            texturePaths = paths;
            imageIndexScanned = true;
            ShowTextureTree(tree);
            return $"已扫描 {paths.Count:N0} 张 UI 纹理，含 {paths.Count(path => path.StartsWith("sqpack:", StringComparison.Ordinal)):N0} 张路径未识别纹理";
        });
    }

    private async void FilterIcons(object sender, TextChangedEventArgs e)
    {
        if (!initialized || closing || texturePaths is not { } paths) return;
        textureSearchCancellation?.Cancel();
        using var current = new CancellationTokenSource();
        textureSearchCancellation = current;
        var token = current.Token;
        var query = IconSearch.Text;
        try
        {
            await Task.Delay(180, token);
            var tree = await Task.Run(() => PathTree.Build(paths, path => path, query, token: token), token);
            token.ThrowIfCancellationRequested();
            ShowTextureTree(tree);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!closing) SetStatus("筛选图片失败：" + ex.Message, true); }
        finally
        {
            if (textureSearchCancellation == current) textureSearchCancellation = null;
        }
    }

    private void ShowTextureTree(IReadOnlyList<PathTreeNode> tree)
    {
        TextureTree.ItemsSource = tree;
        TextureTreeCount.Text = texturePaths == null ? "尚未读取目录"
            : $"{tree.Sum(node => node.LeafCount):N0} / {texturePaths.Count:N0} 张{(imageIndexScanned ? " UI 纹理（含未识别路径）" : "已知图标")}";
        galleryDirectory = null;
        LoadGalleryDirectory();
    }

    private async void TextureTreeSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not PathTreeNode node) return;
        if (node.IsDirectory)
        {
            galleryDirectory = node;
            if (IconGallery.IsExpanded) LoadGalleryDirectory();
        }
        else if (node.Payload is string path)
        {
            TexturePath.Text = path;
            await ShowLocalTexture(path);
        }
    }

    private void IconGalleryExpanded(object sender, RoutedEventArgs e)
    {
        if (initialized) LoadGalleryDirectory();
    }

    private void IconGalleryCollapsed(object sender, RoutedEventArgs e)
    {
        if (!initialized) return;
        thumbnailCancellation?.Cancel();
        IconsList.ItemsSource = null;
        galleryPaths = [];
    }

    private void LoadGalleryDirectory()
    {
        if (!IconGallery.IsExpanded) return;
        galleryPaths = galleryDirectory?.Children.Where(child => !child.IsDirectory).Select(child => child.FullPath).ToArray() ?? [];
        iconPage = 0;
        RenderIconPage();
    }

    private void RenderIconPage()
    {
        var tiles = galleryPaths.Skip(iconPage * IconsPerPage).Take(IconsPerPage).Select(path => new TextureTile(path)).ToArray();
        IconsList.ItemsSource = tiles;
        PreviousIconsButton.IsEnabled = iconPage > 0;
        NextIconsButton.IsEnabled = (iconPage + 1) * IconsPerPage < galleryPaths.Length;
        var pages = (galleryPaths.Length + IconsPerPage - 1) / IconsPerPage;
        IconPageLabel.Text = galleryDirectory == null ? "选择一个目录"
            : pages == 0 ? "此层没有图片" : $"{iconPage + 1:N0} / {pages:N0} 页 · {galleryPaths.Length:N0} 张";
        thumbnailCancellation?.Cancel();
        if (game is not { } source || tiles.Length == 0) return;
        var cancellation = new CancellationTokenSource();
        thumbnailCancellation = cancellation;
        thumbnailTask = LoadThumbnails(thumbnailTask, source, tiles, cancellation);
    }

    private async Task LoadThumbnails(Task previous, LocalGameSource source, TextureTile[] tiles, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        try
        {
            await previous;
            foreach (var tile in tiles)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var thumbnail = await Task.Run(() => CreateThumbnail(source.ReadTexture(tile.Path)), token);
                    token.ThrowIfCancellationRequested();
                    tile.SetThumbnail(thumbnail);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    tile.SetFailure(ex.Message);
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (thumbnailCancellation == cancellation) thumbnailCancellation = null;
            cancellation.Dispose();
        }
    }

    private async Task StopThumbnails()
    {
        thumbnailCancellation?.Cancel();
        tableIconCancellation?.Cancel();
        await Task.WhenAll(thumbnailTask, tableIconTask);
    }

    private static BitmapSource CreateBitmap(GameTexture texture)
    {
        var bitmap = BitmapSource.Create(texture.Width, texture.Height, 96, 96, PixelFormats.Bgra32, null,
            texture.Bgra32, checked(texture.Width * 4));
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource CreateThumbnail(GameTexture texture, int size = 56)
    {
        var bitmap = CreateBitmap(texture);
        var scale = Math.Min(1.0, (double)size / Math.Max(texture.Width, texture.Height));
        if (scale == 1.0) return bitmap;
        var resized = new TransformedBitmap(bitmap, new ScaleTransform(Math.Max(1.0 / texture.Width, scale),
            Math.Max(1.0 / texture.Height, scale)));
        var stride = resized.PixelWidth * 4;
        var pixels = new byte[stride * resized.PixelHeight];
        resized.CopyPixels(pixels, stride, 0);
        var thumbnail = BitmapSource.Create(resized.PixelWidth, resized.PixelHeight, 96, 96, resized.Format, null, pixels, stride);
        thumbnail.Freeze();
        return thumbnail;
    }

    private void PreviousIcons(object sender, RoutedEventArgs e) { iconPage--; RenderIconPage(); }
    private void NextIcons(object sender, RoutedEventArgs e) { iconPage++; RenderIconPage(); }

    private async void IconSelected(object sender, SelectionChangedEventArgs e)
    {
        if (IconsList.SelectedItem is not TextureTile tile) return;
        TexturePath.Text = tile.Path;
        await ShowLocalTexture(tile.Path);
    }

    private async void OpenTexture(object sender, RoutedEventArgs e) => await OpenTexturePath();
    private async void TexturePathKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await OpenTexturePath();
    }

    private async Task OpenTexturePath()
    {
        await ShowLocalTexture(TexturePath.Text);
    }

    public async Task ShowTextureAsync(string virtualPath)
    {
        PageRequested?.Invoke(SqPackPage.Images);
        if (await EnsureGameForNavigation()) await ShowLocalTexture(virtualPath);
    }

    private async Task ShowLocalTexture(string virtualPath)
    {
        var path = virtualPath.Trim().Replace('\\', '/').TrimStart('/');
        if (!path.EndsWith(".tex", StringComparison.OrdinalIgnoreCase) && !path.StartsWith("sqpack:", StringComparison.Ordinal))
        {
            SetStatus("请输入 SqPack 内的 .tex 路径，例如 ui/icon/000000/000001.tex。", true);
            return;
        }
        if (game is not { } source) return;
        TexturePath.Text = path;
        await Work("正在解码纹理…", async token =>
        {
            LocalTexturePreview.SetImage(null);
            var bitmap = await Task.Run(() => CreateBitmap(source.ReadTexture(path)), token);
            token.ThrowIfCancellationRequested();
            LocalTexturePreview.SetImage(bitmap, path);
            return "已读取 " + path;
        });
    }

    private void ResetPatchImages()
    {
        patchImages = [];
        PatchSummary.Text = PatchCatalogueMessage;
        PatchImageSearch.Clear();
        ApplyPatchImageFilter();
        PatchImageCount.Text = "";
        PatchImagePath.Clear();
        PatchTexturePreview.SetImage(null);
        UpdateControls();
    }

    private async Task ScanPatchImages()
    {
        if (game is not { } source || patch is not { } loaded) return;
        var archives = patchArchives;
        await Work("准备完整扫描…", async token =>
        {
            await patchCatalogueTask.WaitAsync(token);
            var progress = WorkProgress(token);
            var result = await Task.Run(() => PatchImageScanner.Scan(source, loaded, archives, token, progress), token);
            token.ThrowIfCancellationRequested();
            ShowPatchImages(result);
            return "";
        }, forPatch: true);
    }

    private void ShowPatchImages(PatchImageScanResult result)
    {
        patchImages = result.Images;
        PatchSummary.Text = result.Summary;
        ApplyPatchImageFilter();
    }

    private void FilterPatchImages(object sender, TextChangedEventArgs e)
    {
        if (initialized) ApplyPatchImageFilter();
    }

    private void ApplyPatchImageFilter()
    {
        var tree = PathTree.Build(patchImages, image => image.Path, PatchImageSearch.Text, image => image.Evidence);
        PatchImagesList.ItemsSource = tree;
        PatchImageCount.Text = $"{tree.Sum(node => node.LeafCount):N0} / {patchImages.Count:N0} 张写入纹理候选";
    }

    private async void PatchImageSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (game is not { } source || e.NewValue is not PathTreeNode { Payload: PatchImageEntry entry }) return;
        await Work("正在读取补丁目标纹理…", async token =>
        {
            PatchTexturePreview.SetImage(null);
            PatchImagePath.Text = entry.Path;
            var bitmap = await Task.Run(() => CreateBitmap(source.ReadTexture(entry.Path)), token);
            token.ThrowIfCancellationRequested();
            PatchTexturePreview.SetImage(bitmap, entry.Path, "本地 SqPack · 写入候选");
            return "已读取补丁目标纹理";
        }, forPatch: true);
    }

    private sealed class TextureTile(string path) : INotifyPropertyChanged
    {
        public string Path { get; } = path;
        public string Name { get; } = path.StartsWith("sqpack:", StringComparison.Ordinal)
            ? path[(path.LastIndexOf('#') + 1)..] : System.IO.Path.GetFileNameWithoutExtension(path);
        public string Description { get; private set; } = path;
        public BitmapSource? Thumbnail { get; private set; }
        public event PropertyChangedEventHandler? PropertyChanged;

        public void SetThumbnail(BitmapSource thumbnail)
        {
            Thumbnail = thumbnail;
            PropertyChanged?.Invoke(this, new(nameof(Thumbnail)));
        }

        public void SetFailure(string error)
        {
            Description = $"{Path}\n{error}";
            PropertyChanged?.Invoke(this, new(nameof(Description)));
        }
    }

    private sealed record TableRow(RowKey Id, SheetCell[] Cells, TableRelatedCell?[] Related)
    {
        public string Key => Id.ToString();
        public string[] GridValues { get; } = Cells.Select((cell, index) =>
            Related[index]?.Relation is { Reference: { } reference } relation ? relation.Label ?? reference : cell.Display).ToArray();
    }
    private sealed record TablePage(IReadOnlyList<SheetColumn> Columns, IReadOnlyList<TableRow> Rows, string Language,
        int? NextOffset, SheetData Raw);
}
