using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace FFXIVPatchParser.Desktop;

public partial class MainWindow : Window
{
    private bool ready;
    private CancellationTokenSource? operation;
    private LoadedPatch? patch;
    private CatalogDocument? catalog;
    private ITableSource? before, after;
    private IReadOnlyList<SheetChange> sheets = [];
    private DiffResult? diff;
    private ChangedRow[] filteredRows = [];
    private int loadedDiffRows = 100;
    private bool loadingDiffRows;
    private readonly Dictionary<int, string> columnFilters = new();
    private int editingFilterColumn;
    private const int RowsPerPage = 100;

    public MainWindow()
    {
        InitializeComponent();
        ready = true;
        SqPackView.PageRequested += page =>
        {
            if (page == SqPackPage.Tables) SqPackNav.IsChecked = true;
            if (page == SqPackPage.Images) LocalImagesNav.IsChecked = true;
        };
        SqPackView.MediaControlsChanged += UpdateMediaControls;
        SqPackView.GameVersionChanged += () =>
        {
            SqPackVersion.Text = SqPackView.GameVersionLabel;
            ResourceHexView.RefreshLocalFiles();
        };
        ResourceHexView.GetTargetDirectory = () => SqPackView.ConnectedGamePath ?? SqPackBrowser.RememberedGamePath();
        PreviewMouseDown += DismissCellDetailsOnOutsideClick;
        PreviewKeyDown += DismissCellDetailsOnEscape;
        Deactivated += (_, _) => CloseCellDetails();
        LocationChanged += (_, _) => CloseCellDetails();
        SizeChanged += (_, _) => CloseCellDetails();
        Loaded += async (_, _) => await Work("正在读取本地工作空间…", async token =>
        {
            catalog = await Task.Run(PatchCatalog.Cached, token);
            ReadPathSettings();
            UpdateCatalog();
            await OpenLatestPatch(token);
        });
        Closed += (_, _) =>
        {
            ResourceHexView.Dispose();
            operation?.Cancel();
            relationOperation?.Cancel();
            relationNavigation?.Cancel();
            diffCellRelationRequest?.Cancel();
        };
    }

    private static string[] PatchVersions(string path)
    {
        var match = Regex.Match(Path.GetFileName(path), @"_(\d+(?:\.\d+){3})-(\d+(?:\.\d+){3})(?:\.|_|$)");
        return match.Success ? [match.Groups[1].Value, match.Groups[2].Value] : [];
    }

    private void Navigate(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        CloseCellDetails();
        relationNavigation?.Cancel();
        PatchWorkspace.Visibility = Visible(PatchNav);
        CatalogView.Visibility = Visible(CatalogNav);
        if (sender == PatchNav || sender == CatalogNav) PatchNavigation.IsExpanded = true;
        DiffView.Visibility = Visible(DiffNav);
        SettingsView.Visibility = Visible(SettingsNav);
        if (sender == SettingsNav) RefreshPathSettings();

        (SqPackPage? page, string title) = sender switch
        {
            _ when sender == SqPackNav => (SqPackPage.Tables, "SqPack · 表格"),
            _ when sender == LocalImagesNav => (SqPackPage.Images, "SqPack · 图片"),
            _ when sender == LocalModelsNav => (SqPackPage.Models, "SqPack · 模型"),
            _ when sender == LocalAudioNav => (SqPackPage.Audio, "SqPack · 音频"),
            _ when sender == PatchImagesNav => (SqPackPage.PatchImages, "图片 Diff"),
            _ when sender == PatchModelsNav => (SqPackPage.PatchModels, "模型 Diff"),
            _ when sender == PatchAudioNav => (SqPackPage.PatchAudio, "音频 Diff"),
            _ => ((SqPackPage?)null, ((RadioButton)sender).Content?.ToString() ?? "")
        };
        if (page is { } selected)
        {
            if (selected is SqPackPage.PatchImages or SqPackPage.PatchModels or SqPackPage.PatchAudio) DiffNavigation.IsExpanded = true;
            else LocalNavigation.IsExpanded = true;
            SqPackView.ShowPage(selected);
        }
        if (sender == DiffNav)
        {
            DiffNavigation.IsExpanded = true;
            title = "表格 Diff";
        }
        SqPackView.Visibility = page != null ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = title;
        UpdateMediaControls();
        UpdateMainStatusVisibility();

        static Visibility Visible(RadioButton item) => item.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateMediaControls()
    {
        var onBrowser = SqPackView.Visibility == Visibility.Visible;
        ScanMediaButton.Visibility = onBrowser && SqPackView.IsMediaPage ? Visibility.Visible : Visibility.Collapsed;
        ScanMediaButton.IsEnabled = SqPackView.CanScanMedia;
        ScanMediaButton.Content = SqPackView.IsPatchMediaPage ? "完整扫描" : "扫描资源索引";
        ScanMediaButton.ToolTip = SqPackView.IsPatchMediaPage
            ? "校验本地目标容器，补查原始路径未识别的资源。"
            : "扫描 SqPack 索引，查找已知目录之外的资源。";
    }

    private async void ScanCurrentMedia(object sender, RoutedEventArgs e) => await SqPackView.ScanCurrentMediaAsync();

    private void SetBusy(bool busy)
    {
        foreach (var button in new[] { OpenPatchButton, RetryMatchButton, RefreshButton, ChoosePatchDirectoryButton, ResetPatchDirectoryButton, LoadLatestPatchButton })
            button.IsEnabled = !busy;
        UpdateCatalogSelection();
        SheetsList.IsEnabled = !busy;
        SheetSearch.IsEnabled = !busy;
        CancelButton.Visibility = Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateMainStatusVisibility();
    }

    private void UpdateMainStatusVisibility() => MainStatusBar.Visibility =
        SqPackView.Visibility != Visibility.Visible || operation != null ? Visibility.Visible : Visibility.Collapsed;
    private async Task Work(string message, Func<CancellationToken, Task> action)
    {
        if (operation != null) return;
        using var current = new CancellationTokenSource();
        operation = current; SetBusy(true); SetStatus(message);
        try
        {
            await action(current.Token);
            current.Token.ThrowIfCancellationRequested();
            SetStatus("就绪 · 所有缓存与下载保存在项目 tmp");
        }
        catch (OperationCanceledException) { SetStatus("已取消；可以重新加载或下载。"); }
        catch (Exception ex) { SetStatus("操作失败：" + ex.Message, true); }
        finally { operation = null; SetBusy(false); }
    }
    private IProgress<string> Reporter() => new Progress<string>(message => SetStatus(message));
    private void SetStatus(string message, bool error = false)
    {
        Status.Text = message;
        Status.Foreground = Brush(error ? "#CF222E" : "#667085");
    }
    private void Cancel(object sender, RoutedEventArgs e) => operation?.Cancel();

    private async void OpenPatch(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择同一次更新的补丁 ZIP 分卷", Filter = "补丁包 (*.zip)|*.zip", Multiselect = true };
        if (Directory.Exists(PatchDirectory)) dialog.InitialDirectory = PatchDirectory;
        if (dialog.ShowDialog(this) == true)
            await Work("正在读取补丁目录…", token => LoadPatchFiles(dialog.FileNames, token));
    }
    private async Task LoadPatchFiles(string[] files, CancellationToken token)
    {
        var loaded = await Task.Run(() => PatchArchive.Load(files), token);
        token.ThrowIfCancellationRequested();
        patch = loaded;
        before = after = null;
        UpdateDiffVersion();
        SqPackView.SetPatch(loaded, files);
        ResourceHexView.SetPatch(files);
        var resources = PathTree.Build(patch.Resources, resource => resource.Path);
        if (resources.FirstOrDefault(node => node.FullPath == "game" && node.IsDirectory) is { } gameNode)
        {
            gameNode.IsExpanded = true;
            if (gameNode.Children.FirstOrDefault(node => node.FullPath == "game/sqpack" && node.IsDirectory) is { } sqpackNode)
                sqpackNode.IsExpanded = true;
        }
        ResourcesTree.ItemsSource = resources;
        PatchNav.IsChecked = true;
        UpdateCatalogSelection();
        ResetComparison();
        await MatchPatch(token);
    }
    private void ResourceSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (!ready) return;
        ResourceHexView.SelectResource((e.NewValue as PathTreeNode)?.Payload as PatchResource);
    }

    private async void RetryMatch(object sender, RoutedEventArgs e) => await Work("正在匹配历史表格…", MatchPatch);

    private async Task MatchPatch(CancellationToken token)
    {
        UpdateDiffVersion();
        DiffSummary.Text = "正在自动匹配 CHS 历史版本…";
        DiffMatchError.Visibility = RetryMatchButton.Visibility = Visibility.Collapsed;
        try
        {
            if (patch is not { From: not null, To: not null })
                throw new InvalidDataException("补丁 ZIP 文件名缺少数据版本，请打开带有原始文件名的 ZIP。");
            var result = await DataminingSource.ForPatch(patch.From, patch.To, Reporter(), token);
            token.ThrowIfCancellationRequested();
            before = result.Before; after = result.After;
            BindSources();
            UpdateDiffVersion();
        }
        catch (OperationCanceledException)
        {
            SetMatchError("自动匹配已取消。");
            throw;
        }
        catch (Exception ex)
        {
            SetMatchError("自动匹配失败：" + ex.Message);
            throw;
        }
    }
    private void SetMatchError(string message)
    {
        DiffSummary.Text = "等待 CHS 历史版本";
        DiffMatchError.Text = message;
        DiffMatchError.Visibility = Visibility.Visible;
        RetryMatchButton.Visibility = patch is { From: not null, To: not null } ? Visibility.Visible : Visibility.Collapsed;
    }
    private void UpdateDiffVersion()
    {
        DiffVersion.Text = patch is { From: not null, To: not null } ? $"{patch.From} → {patch.To}" : patch?.Name ?? "未选择";
        DiffVersion.ToolTip = patch?.Name;
        DiffGameVersions.Visibility = Visibility.Collapsed;
        if (patch is not { From: not null, To: not null }) return;

        var oldGameVersion = GameVersion(before, patch.From);
        var newGameVersion = GameVersion(after, patch.To);
        if (oldGameVersion != null && newGameVersion != null)
        {
            DiffGameVersions.Text = $"{oldGameVersion} → {newGameVersion}";
            DiffGameVersions.Visibility = Visibility.Visible;
        }
    }
    private string? GameVersion(ITableSource? source, string dataVersion)
    {
        var matched = Regex.Match(source?.Label ?? "", @"^CHS (\d+\.\d+(?:#hf\d+)?)(?=\s|$)");
        if (matched.Success) return matched.Groups[1].Value;

        var versionView = catalog?.Packages.FirstOrDefault(p => p.To == dataVersion)?.VersionView;
        var published = Regex.Match(versionView ?? "", @"^(?:\d{4}\.\d{2}\.\d{2}\.\d{4}\.\d{4}_)?(\d+\.\d+)$");
        return published.Success ? published.Groups[1].Value : null;
    }
    private void ResetComparison()
    {
        ResetDiffRelations();
        CloseCellDetails();
        DiffEmptyMessage.Visibility = Visibility.Collapsed;
        sheets = []; SheetsList.ItemsSource = null;
        diff = null; filteredRows = [];
        DiffGrid.ItemsSource = null; DiffGrid.Columns.Clear();
        FitDiffColumnsButton.IsEnabled = false;
        columnFilters.Clear(); ColumnFilterPopup.IsOpen = false; ClearFiltersButton.Visibility = Visibility.Collapsed;
    }
    private void BindSources()
    {
        ResetComparison();
        if (before == null || after == null) return;
        sheets = SheetComparison.List(before, after);
        DiffSummary.Text = $"{sheets.Count:N0} 张表文件发生变化 · 旧 {before.Sheets.Count:N0} / 新 {after.Sheets.Count:N0}";
        ApplySheetFilter();
    }
    private void FilterSheets(object sender, TextChangedEventArgs e) { if (ready) ApplySheetFilter(); }
    private void ApplySheetFilter() => SheetsList.ItemsSource = PathTree.Build(sheets, sheet => sheet.Name, SheetSearch.Text);
    private async void SheetSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        CloseCellDetails();
        if (e.NewValue is not PathTreeNode { Payload: SheetChange sheet } || before == null || after == null || operation != null) return;
        await Work("正在读取并比较 " + sheet.Name + "…", async token =>
        {
            ResetDiffRelations();
            diff = null; filteredRows = [];
            DiffGrid.ItemsSource = null; DiffGrid.Columns.Clear();
            FitDiffColumnsButton.IsEnabled = false;
            DiffSummary.Text = sheet.Name + " · 加载中…";
            columnFilters.Clear(); ColumnFilterPopup.IsOpen = false; ClearFiltersButton.Visibility = Visibility.Collapsed;
            DiffEmptyMessage.Visibility = Visibility.Collapsed;
            var data = await Task.WhenAll(before.Read(sheet.Name, token), after.Read(sheet.Name, token));
            var result = await Task.Run(() => TableDiff.Compare(data[0], data[1], token), token);
            token.ThrowIfCancellationRequested();
            diff = result;
            SetDiffRelationSources(sheet.Name, data[0], data[1]);
            DiffSummary.Text = $"{sheets.Count:N0} 张变化表 · {sheet.Name}：+{diff.Added:N0} / −{diff.Removed:N0} / ~{diff.Modified:N0} 行 · {diff.UnchangedRows:N0} 行不变";
            ApplyRowFilter();
        });
    }
    private void ApplyRowFilter()
    {
        loadedDiffRows = RowsPerPage;
        filteredRows = diff?.Rows.Where(r => CellPresentation.Matches(r, columnFilters)).ToArray() ?? [];
        ClearFiltersButton.Content = $"清除筛选 ({columnFilters.Count})";
        ClearFiltersButton.Visibility = columnFilters.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RenderDiff();
    }
    private void ClearFilters(object sender, RoutedEventArgs e)
    {
        columnFilters.Clear(); ColumnFilterPopup.IsOpen = false; ApplyRowFilter();
    }
    private FrameworkElement FilterHeader(int index, string title, string detail, bool schemaChanged = false)
    {
        var active = columnFilters.TryGetValue(index, out var keyword);
        var button = new Button
        {
            Content = title + (active ? "  ●" : "  ⌕"),
            Padding = new Thickness(2), Margin = new Thickness(0),
            Background = Brushes.Transparent, BorderBrush = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Foreground = Brush(active ? "#2563EB" : schemaChanged ? "#9A6700" : "#667085"),
            ToolTip = detail + (active ? "\n筛选：" + keyword : "\n点击输入本列筛选关键词")
        };
        button.Click += (_, _) =>
        {
            editingFilterColumn = index;
            ColumnFilterTitle.Text = title;
            ColumnFilterInput.Text = columnFilters.GetValueOrDefault(index, "");
            ColumnFilterPopup.PlacementTarget = button;
            ColumnFilterPopup.HorizontalOffset = Math.Min(0, DiffGrid.ActualWidth - button.TranslatePoint(new Point(), DiffGrid).X - 300);
            ColumnFilterPopup.IsOpen = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                ColumnFilterInput.Focus(); ColumnFilterInput.SelectAll();
            }));
        };
        return button;
    }
    private void ColumnFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { ApplyColumnFilter(sender, e); e.Handled = true; }
        else if (e.Key == Key.Escape) { ColumnFilterPopup.IsOpen = false; e.Handled = true; }
    }
    private void ApplyColumnFilter(object sender, RoutedEventArgs e)
    {
        var value = ColumnFilterInput.Text.Trim();
        if (value.Length == 0) columnFilters.Remove(editingFilterColumn);
        else columnFilters[editingFilterColumn] = value;
        ColumnFilterPopup.IsOpen = false; ApplyRowFilter();
    }
    private void ClearColumnFilter(object sender, RoutedEventArgs e)
    {
        columnFilters.Remove(editingFilterColumn);
        ColumnFilterPopup.IsOpen = false; ApplyRowFilter();
    }
    private void ColumnsChanged(object sender, RoutedEventArgs e) { RenderDiff(updateRelations: false); }
    private void ResetDiffColumnSizing(object sender, RoutedEventArgs e)
    {
        if (DiffGrid.ItemsSource is not IReadOnlyList<DiffLine> lines) return;

        ApplyDiffColumnSizing(lines);
    }

    private void ApplyDiffColumnSizing(IReadOnlyList<DiffLine> lines)
        => TableColumnSizing.Apply(DiffGrid, lines, (line, index) =>
        {
            if (index == 0) return (line.Label, 0, false);
            var cell = line.Cells[index - 1];
            return (cell.Text, 0, cell.IsReference || cell.Spans.Any(span => span.Changed));
        });

    private void RenderDiff(bool updateRelations = true)
    {
        diffPresentationPending = false;
        CloseCellDetails();
        if (diff == null) return;
        if (updateRelations) ClearDiffRelationPresentation();
        var visibleColumns = Enumerable.Range(0, diff.Columns.Count).Where(i => ChangedColumnsOnly.IsChecked != true
            || diff.Columns[i].SchemaChanged || diff.Rows.Any(r => r.Changed[i])).ToArray();
        var restore = TableColumnSizing.Capture(DiffGrid, !updateRelations);
        DiffGrid.ItemsSource = null; DiffGrid.Columns.Clear();
        DiffGrid.Columns.Add(new DataGridTemplateColumn { Header = FilterHeader(-1, "Row / Subrow", "按行号筛选"), Width = DataGridLength.SizeToHeader, CellTemplate = CellTemplate("Label", "Color", "Background"), CellStyle = DiffCellStyle() });
        foreach (var (index, local) in visibleColumns.Select((n, i) => (n, i)))
        {
            var column = PresentedColumn(diff.Columns[index]);
            var header = FilterHeader(index, $"[{column.Id}] {column.Header}", column.Detail, column.SchemaChanged);
            DiffGrid.Columns.Add(new DataGridTemplateColumn
            {
                Header = header, Width = DataGridLength.SizeToHeader, SortMemberPath = index.ToString(),
                CellTemplate = CellTemplate($"Cells[{local}].Text", "Color", $"Cells[{local}].Background", $"Cells[{local}]"), CellStyle = DiffCellStyle()
            });
        }
        var lines = new List<DiffLine>();
        foreach (var row in filteredRows.Take(loadedDiffRows))
        {
            if (row.Before != null) lines.Add(MakeLine(row, false, visibleColumns));
            if (row.After != null) lines.Add(MakeLine(row, true, visibleColumns));
        }
        DiffGrid.ItemsSource = lines;
        FitDiffColumnsButton.IsEnabled = lines.Count > 0;
        ApplyDiffColumnSizing(lines);
        restore();
        DiffEmptyMessage.Visibility = lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (lines.Count == 0)
        {
            DiffEmptyMessage.Text = diff.Rows.Count == 0
                ? $"没有行值差异。{diff.Columns.Count(c => c.SchemaChanged)} 列结构变化，以黄色表头标记。"
                : "没有符合筛选条件的行。";
        }
        if (updateRelations && (DiffLinked.IsChecked == true || relationColumns.Count == 0))
            diffRelationPresentationTask = LoadDiffRelationPresentation();
    }
    private DiffLine MakeLine(ChangedRow row, bool added, int[] columns)
    {
        var background = added ? "#F0FFF4" : "#FFF5F5";
        return new DiffLine(row, (added ? "+  " : "−  ") + row.Key, Brush(added ? "#116329" : "#82071E"), Brush(background),
            columns.Select(i => PresentCell(row, diff!.Columns[i], i, added)).ToArray());
    }
    private DisplayCell PresentCell(ChangedRow row, ComparedColumn column, int index, bool added, bool detailed = false)
    {
        var oldCell = PresentedValue(row, column, index, false, detailed);
        var newCell = PresentedValue(row, column, index, true, detailed);
        var cell = added ? newCell : oldCell;
        var text = cell?.Display ?? "∅";
        var difference = CellPresentation.Compare(column, oldCell, newCell);
        var spans = cell == null ? new[] { new TextSpan(text, false) } : added ? difference.After : difference.Before;
        var isString = CellPresentation.IsString(added ? column.After : column.Before);
        var background = !row.Changed[index] ? added ? "#F0FFF4" : "#FFF5F5"
            : isString ? added ? "#DAFBE1" : "#FFEBE9" : added ? "#ACEEBB" : "#FFCECB";
        var original = added ? row.After?[index] : row.Before?[index];
        var isReference = !isString && original != null
            && PresentedValue(row, column, index, added, detailed: true)?.Display != original.Display;
        return new DisplayCell(text, Brush(background), Brush(added ? "#7EE29A" : "#FF9B95"), spans, isReference);
    }
    private Style DiffCellStyle()
    {
        var style = new Style(typeof(DataGridCell), (Style)FindResource(typeof(DataGridCell)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Stretch));
        return style;
    }
    private static DataTemplate CellTemplate(string text, string color, string background, string? highlight = null)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetBinding(Border.BackgroundProperty, new Binding(background));
        border.SetValue(Border.HeightProperty, 32d);
        border.SetValue(Border.PaddingProperty, new Thickness(10, 0, 10, 0));
        var block = new FrameworkElementFactory(typeof(TextBlock));
        if (highlight == null) block.SetBinding(TextBlock.TextProperty, new Binding(text));
        else block.SetBinding(InlineHighlight.ValueProperty, new Binding(highlight));
        block.SetBinding(TextBlock.ForegroundProperty, new Binding(color));
        block.SetBinding(FrameworkElement.ToolTipProperty, new Binding(text));
        block.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        block.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(block);
        return new DataTemplate { VisualTree = border };
    }
    private void InspectCellClick(object sender, MouseButtonEventArgs e)
    {
        if (SqPackBrowser.FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject) is { } cell)
            ShowCellDetails(cell);
    }

    private void InspectCellKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SqPackBrowser.FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject) is { } cell)
            e.Handled = ShowCellDetails(cell, focusValue: true);
        else if (CellDetailsPopup.IsOpen)
            CloseCellDetails();
    }

    private bool ShowCellDetails(DataGridCell cell, bool focusValue = false)
    {
        if (diff == null || cell.DataContext is not DiffLine line || !int.TryParse(cell.Column.SortMemberPath, out var column))
        {
            CloseCellDetails();
            return false;
        }
        var value = line.Label.StartsWith('+') ? NewCell : OldCell;
        if (CellDetailsPopup.IsOpen && ReferenceEquals(CellDetailsPopup.PlacementTarget, cell))
        {
            if (focusValue) value.Focus();
            else CloseCellDetails();
            return true;
        }
        CloseCellDetails();
        var definition = diff.Columns[column];
        ShowDiffCellValues(line.Row, definition, column);
        CellDetailsPopup.PlacementTarget = cell;
        CellDetailsPopup.IsOpen = true;
        if (focusValue) value.Focus();
        _ = LoadDiffCellRelations(line.Row, definition, column);
        return true;
    }

    private void DismissCellDetailsOnOutsideClick(object sender, MouseButtonEventArgs e)
    {
        if (!CellDetailsPopup.IsOpen || CellDetailsContent.IsMouseOver) return;
        if (e.ChangedButton == MouseButton.Left &&
            SqPackBrowser.FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject)?.DataContext is DiffLine) return;
        CloseCellDetails();
    }

    private void DismissCellDetailsOnEscape(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !CellDetailsPopup.IsOpen) return;
        var restoreFocus = CellDetailsContent.IsKeyboardFocusWithin;
        var cell = CellDetailsPopup.PlacementTarget;
        CloseCellDetails();
        if (restoreFocus) cell?.Focus();
        e.Handled = true;
    }

    private void DiffGridScrolled(object sender, ScrollChangedEventArgs e)
    {
        if (e.HorizontalChange != 0 || e.VerticalChange != 0) CloseCellDetails();
        if (!loadingDiffRows && e.VerticalChange > 0 && e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 10
            && loadedDiffRows < filteredRows.Length)
        {
            loadingDiffRows = true;
            var rows = filteredRows;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                try
                {
                    if (!ReferenceEquals(rows, filteredRows)) return;
                    loadedDiffRows += RowsPerPage;
                    RenderDiff(updateRelations: false);
                    if (DiffLinked.IsChecked == true)
                    {
                        relationOperation?.Cancel();
                        diffRelationPresentationTask = LoadDiffRelationPresentation();
                    }
                }
                finally { loadingDiffRows = false; }
            }));
        }
    }

    private void DiffGridLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (diffPresentationPending) Dispatcher.BeginInvoke(DispatcherPriority.Background, ApplyDiffRelationPresentation);
    }

    private void CloseCellDetails()
    {
        diffCellRelationRequest?.Cancel();
        CellDetailsPopup.IsOpen = false;
        CellDetailsPopup.PlacementTarget = null;
        if (diffPresentationPending) Dispatcher.BeginInvoke(DispatcherPriority.Background, ApplyDiffRelationPresentation);
    }

    private void UpdateCatalog()
    {
        CatalogTitle.Text = catalog == null ? "官方国服 V3 补丁" : "最新目标 · " + catalog.Target;
        CatalogInfo.Text = catalog == null ? "点击刷新，获取官方可下载列表。" : $"{catalog.Packages.Count} 条更新 · {catalog.Packages.Sum(p => p.Files.Count)} 个 ZIP · 查询于 {catalog.CheckedAt:yyyy-MM-dd HH:mm}";
        ApplyCatalogFilter();
        UpdateDiffVersion();
    }
    private void FilterCatalog(object sender, TextChangedEventArgs e) { if (ready) ApplyCatalogFilter(); }
    private void ApplyCatalogFilter()
    {
        var search = CatalogSearch.Text.Trim();
        var selectedName = (CatalogGrid.SelectedItem as CatalogRow)?.Package.Name;
        var rows = catalog?.Packages.Where(p => (p.From + " " + p.To + " " + p.VersionView).Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => Version.TryParse(p.To, out var version) ? version : new Version())
            .Select(p => new CatalogRow(p, FindLocalPatch(p, PatchDirectory))).ToArray();
        CatalogGrid.ItemsSource = rows;
        if (selectedName != null)
            CatalogGrid.SelectedItem = rows?.FirstOrDefault(row => row.Package.Name == selectedName);
        UpdateCatalogSelection();
    }
    private static string[]? FindLocalPatch(CatalogPackage package, string preferredRoot)
    {
        if (package.Error != null || package.Files.Count == 0 || !Regex.IsMatch(package.Name, @"^patch-[0-9.]+-[0-9.]+$"))
            return null;
        foreach (var root in new[] { preferredRoot, Path.Combine(preferredRoot, "v3"), DefaultPatchDirectory }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var folder = Workspace.Within(root, package.Name);
            var paths = new List<string>(package.Files.Count);
            foreach (var file in package.Files)
            {
                if (string.IsNullOrEmpty(file.Name) || Path.GetFileName(file.Name) != file.Name
                    || !file.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return null;
                var info = new FileInfo(Workspace.Within(folder, file.Name));
                if (!info.Exists || info.Length != file.Size) break;
                paths.Add(info.FullName);
            }
            if (paths.Count == package.Files.Count) return paths.ToArray();
        }
        return null;
    }
    private bool IsCurrentPatch(CatalogPackage package) => patch != null && patch.From == package.From && patch.To == package.To;

    private void CatalogSelected(object sender, SelectionChangedEventArgs e) { if (ready) UpdateCatalogSelection(); }
    private void UpdateCatalogSelection()
    {
        var row = CatalogGrid.SelectedItem as CatalogRow;
        var canUseSelection = operation == null && row != null;
        var isCurrent = row != null && IsCurrentPatch(row.Package);
        ViewCatalogPatchButton.IsEnabled = canUseSelection && (row?.Paths != null || isCurrent);
        DownloadButton.IsEnabled = canUseSelection && row?.Package is { Error: null, Files.Count: > 0 };
        if (row == null)
        {
            CatalogDetail.Text = "选择一条更新查看 ZIP 分卷。";
            return;
        }
        var package = row.Package;
        var details = package.Files.Select(f => $"{f.Name} · {Workspace.Size(f.Size)} · MD5 {f.Md5}").ToList();
        if (isCurrent) details.Insert(0, "当前补丁");
        if (package.Error != null) details.Insert(0, "查询错误：" + package.Error);
        CatalogDetail.Text = string.Join("\n", details);
    }
    private async void RefreshCatalog(object sender, RoutedEventArgs e) => await Work("正在查询官方补丁目录…", async token =>
    {
        catalog = await PatchCatalog.Refresh(Reporter(), token); UpdateCatalog();
    });
    private async void DownloadPatch(object sender, RoutedEventArgs e)
    {
        if (CatalogGrid.SelectedItem is not CatalogRow { Package: var package }) return;
        await Work("正在检查已下载的文件…", async token =>
        {
            var paths = await PatchCatalog.Download(package, Reporter(), token);
            ApplyCatalogFilter();
            await LoadPatchFiles(paths, token);
        });
    }
    private async void OpenCatalogPatch(object sender, RoutedEventArgs e)
    {
        if (CatalogGrid.SelectedItem is not CatalogRow row) return;
        if (row.Paths == null)
        {
            if (IsCurrentPatch(row.Package)) PatchNav.IsChecked = true;
            return;
        }

        var paths = row.Paths;
        await Work("正在校验已下载的 ZIP 分卷…", async token =>
        {
            foreach (var (path, file) in paths.Zip(row.Package.Files))
                if (!await Workspace.Matches(path, file.Size, file.Md5, token))
                    throw new InvalidDataException($"本地分卷 {file.Name} 校验失败，请重新下载。");
            await LoadPatchFiles(paths, token);
        });
    }
    private static SolidColorBrush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); return brush;
    }
    private sealed record DiffLine(ChangedRow Row, string Label, SolidColorBrush Color, SolidColorBrush Background, DisplayCell[] Cells);
    private sealed record CatalogRow(CatalogPackage Package, string[]? Paths)
    {
        public string LocalStatus => Paths != null ? "已下载" : "—";
    }
}
