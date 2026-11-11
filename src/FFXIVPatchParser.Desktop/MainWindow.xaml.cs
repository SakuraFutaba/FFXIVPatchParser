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
    private int rowPage, columnPage;
    private readonly Dictionary<int, string> columnFilters = new();
    private int editingFilterColumn;
    private const int RowsPerPage = 100, ColumnsPerPage = 40;

    public MainWindow()
    {
        InitializeComponent();
        ready = true;
        Loaded += async (_, _) => await Work("正在读取本地工作空间…", async token =>
        {
            catalog = await Task.Run(PatchCatalog.Cached, token);
            UpdateCatalog();
            var root = Path.Combine(Workspace.Temp, "patches");
            if (Directory.Exists(root))
            {
                var latest = Directory.EnumerateFiles(root, "*.zip", SearchOption.AllDirectories)
                    .OrderByDescending(path => Version.TryParse(PatchVersions(path).LastOrDefault(), out var version) ? version : new Version())
                    .ThenByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                if (latest != null)
                {
                    var versions = PatchVersions(latest);
                    var parts = versions.Length == 2 ? Directory.GetFiles(Path.GetDirectoryName(latest)!, "*.zip")
                        .Where(path => PatchVersions(path).SequenceEqual(versions)).ToArray() : [latest];
                    await LoadPatchFiles(parts, token);
                }
            }
        });
        Closed += (_, _) => operation?.Cancel();
    }

    private static string[] PatchVersions(string path)
    {
        var match = Regex.Match(Path.GetFileName(path), @"_(\d+(?:\.\d+){3})-(\d+(?:\.\d+){3})(?:\.|_|$)");
        return match.Success ? [match.Groups[1].Value, match.Groups[2].Value] : [];
    }

    private void Navigate(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        ResourceView.Visibility = ResourceNav.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        DiffView.Visibility = DiffNav.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        CatalogView.Visibility = CatalogNav.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = ResourceNav.IsChecked == true ? "变化资源" : DiffNav.IsChecked == true ? "表格 Diff" : "补丁目录";
        PageSubtitle.Text = ResourceNav.IsChecked == true ? "打开补丁，检查它改变了哪些文件。"
            : DiffNav.IsChecked == true ? "逐行比较旧值与新值，定位每个变化的单元格。"
            : "浏览官方可下载的全部更新，并校验下载结果。";
    }

    private void SetBusy(bool busy)
    {
        foreach (var button in new[] { OpenPatchButton, RemoteButton, OldButton, NewButton, RefreshButton, DownloadButton })
            button.IsEnabled = !busy;
        SheetsList.IsEnabled = !busy;
        SheetSearch.IsEnabled = !busy;
        CancelButton.Visibility = Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }
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
        if (dialog.ShowDialog(this) == true)
            await Work("正在读取补丁目录…", token => LoadPatchFiles(dialog.FileNames, token));
    }
    private async Task LoadPatchFiles(string[] files, CancellationToken token)
    {
        var loaded = await Task.Run(() => PatchArchive.Load(files), token);
        token.ThrowIfCancellationRequested();
        patch = loaded;
        PatchTitle.Text = patch.From != null ? patch.From + " → " + patch.To : patch.Name;
        PatchTitle.ToolTip = patch.Name;
        var missing = patch.Resources.Count(r => !r.Available);
        PatchInfo.Text = $"{patch.ArchiveCount} 个 ZIP · {patch.Resources.Count:N0} 个变化文件 · {patch.Resources.Select(r => r.Category).Distinct().Count()} 个类别"
            + (missing > 0 ? $" · {missing} 个文件缺少分卷" : "");
        CategoryFilter.ItemsSource = new[] { "全部类别" }.Concat(patch.Resources.Select(r => r.Category).Distinct().Order()).ToArray();
        CategoryFilter.SelectedIndex = 0; ResourceSearch.Clear();
        ApplyResourceFilter();
        before = after = null; ResetComparison();
        SourceLabel.Text = "当前补丁：" + PatchTitle.Text;
        await MatchPatch(token);
    }
    private void FilterResources(object sender, RoutedEventArgs e) { if (ready) ApplyResourceFilter(); }
    private void ApplyResourceFilter()
    {
        var text = ResourceSearch.Text.Trim();
        var category = CategoryFilter.SelectedItem as string;
        ResourcesGrid.ItemsSource = patch?.Resources.Where(r => (category is null or "全部类别" || r.Category == category)
            && (r.Path.Contains(text, StringComparison.OrdinalIgnoreCase) || r.Category.Contains(text, StringComparison.OrdinalIgnoreCase))).ToArray();
    }
    private void ResourceSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ResourcesGrid.SelectedItem is PatchResource item)
            ResourceDetail.Text = $"{item.Path}\nZIP 条目：{item.Entry}\n旧 MD5：{item.OriginMd5}    新 MD5：{item.ResultMd5}\n差分 MD5：{item.DeltaMd5} · {item.Status}";
    }
    private void ShowDiff(object sender, RoutedEventArgs e) => DiffNav.IsChecked = true;

    private void OpenSourceSettings(object sender, RoutedEventArgs e)
    {
        SourceSettings.HorizontalOffset = SourceSettingsButton.ActualWidth - 580;
        SourceSettings.IsOpen = true;
    }
    private async void LoadRemote(object sender, RoutedEventArgs e)
    {
        SourceSettings.IsOpen = false;
        await Work("正在匹配历史表格…", MatchPatch);
    }
    private async Task MatchPatch(CancellationToken token)
    {
        SourceNote.Text = "正在自动匹配 CHS 历史版本…";
        SourceSettingsButton.Content = "数据源 · 匹配中";
        try
        {
            if (patch?.From == null || patch.To == null)
                throw new InvalidOperationException("补丁文件名中没有可识别的版本，请在数据源设置中选择 Raw 快照。");
            var result = await DataminingSource.ForPatch(patch.From, patch.To, Reporter(), token);
            token.ThrowIfCancellationRequested();
            before = result.Before; after = result.After;
            BindSources();
            SourceNote.Text = "来源：InfSein/ffxiv-datamining-mixed 的 CHS CSV；准确匹配构建号。此数据来自历史仓库，未从 ZIP 还原。";
            SourceSettingsButton.Content = "数据源 / 设置";
        }
        catch (OperationCanceledException)
        {
            SourceNote.Text = "匹配已取消，可点击重新匹配，或手动选择 Raw 快照。";
            SourceSettingsButton.Content = "数据源 · 已取消";
            throw;
        }
        catch (Exception ex)
        {
            SourceNote.Text = "自动匹配失败：" + ex.Message;
            SourceSettingsButton.Content = "数据源 · 匹配失败";
            throw;
        }
    }
    private async void SelectOld(object sender, RoutedEventArgs e) => await SelectSnapshot(true);
    private async void SelectNew(object sender, RoutedEventArgs e) => await SelectSnapshot(false);
    private async Task SelectSnapshot(bool old)
    {
        SourceSettings.IsOpen = false;
        var dialog = new OpenFileDialog { Title = old ? "选择旧版 Raw 快照的 manifest.json" : "选择新版 Raw 快照的 manifest.json", Filter = "快照清单 (manifest.json)|manifest.json|JSON (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        await Work("正在读取快照索引…", async token =>
        {
            var source = await Task.Run(() => new RawSnapshotSource(dialog.FileName), token);
            token.ThrowIfCancellationRequested();
            if (before?.Format != source.Format) before = null;
            if (after?.Format != source.Format) after = null;
            if (old) before = source; else after = source;
            BindSources();
            SourceSettingsButton.Content = "数据源 / 设置";
            SourceNote.Text = "来源：本地 Raw Excel 快照。按 RowId / SubrowId 与原始列编号比较，SeString 和浮点数使用原始值。";
        });
    }
    private void ResetComparison()
    {
        sheets = []; SheetsList.ItemsSource = null;
        diff = null; filteredRows = []; rowPage = columnPage = 0;
        DiffGrid.ItemsSource = null; DiffGrid.Columns.Clear();
        DiffSummary.Text = "尚未比较";
        columnFilters.Clear(); ColumnFilterPopup.IsOpen = false; ClearFiltersButton.Visibility = Visibility.Collapsed;
        OldCell.Text = "− 旧值 · 点击单元格查看完整内容"; NewCell.Text = "+ 新值 · 点击单元格查看完整内容";
        RowPageLabel.Text = ColumnPageLabel.Text = "";
    }
    private void BindSources()
    {
        ResetComparison();
        SourceLabel.Text = (before?.Label ?? "请选择旧快照") + "   →   " + (after?.Label ?? "请选择新快照");
        if (before == null || after == null) return;
        sheets = SheetComparison.List(before, after);
        DiffSummary.Text = $"{sheets.Count:N0} 张表文件发生变化 · 旧 {before.Sheets.Count:N0} / 新 {after.Sheets.Count:N0}";
        ApplySheetFilter();
    }
    private void FilterSheets(object sender, TextChangedEventArgs e) { if (ready) ApplySheetFilter(); }
    private void ApplySheetFilter() => SheetsList.ItemsSource = sheets.Where(s => s.Name.Contains(SheetSearch.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
    private async void SheetSelected(object sender, SelectionChangedEventArgs e)
    {
        if (SheetsList.SelectedItem is not SheetChange sheet || before == null || after == null || operation != null) return;
        await Work("正在读取并比较 " + sheet.Name + "…", async token =>
        {
            diff = null; filteredRows = [];
            DiffGrid.ItemsSource = null; DiffGrid.Columns.Clear();
            DiffSummary.Text = sheet.Name + " · 加载中…";
            columnFilters.Clear(); ColumnFilterPopup.IsOpen = false; ClearFiltersButton.Visibility = Visibility.Collapsed;
            OldCell.Clear(); NewCell.Clear();
            var data = await Task.WhenAll(before.Read(sheet.Name, token), after.Read(sheet.Name, token));
            var result = await Task.Run(() => TableDiff.Compare(data[0], data[1], token), token);
            token.ThrowIfCancellationRequested();
            diff = result; rowPage = columnPage = 0;
            DiffSummary.Text = $"{sheets.Count:N0} 张变化表 · {sheet.Name}：+{diff.Added:N0} / −{diff.Removed:N0} / ~{diff.Modified:N0} 行 · {diff.UnchangedRows:N0} 行不变";
            OldCell.Text = "− 旧值 · 点击单元格查看完整内容"; NewCell.Text = "+ 新值 · 点击单元格查看完整内容";
            ApplyRowFilter();
        });
    }
    private void ApplyRowFilter()
    {
        filteredRows = diff?.Rows.Where(r => CellPresentation.Matches(r, columnFilters)).ToArray() ?? [];
        ClearFiltersButton.Content = $"清除筛选 ({columnFilters.Count})";
        ClearFiltersButton.Visibility = columnFilters.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RenderDiff();
    }
    private void ClearFilters(object sender, RoutedEventArgs e)
    {
        columnFilters.Clear(); ColumnFilterPopup.IsOpen = false; rowPage = 0; ApplyRowFilter();
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
        ColumnFilterPopup.IsOpen = false; rowPage = 0; ApplyRowFilter();
    }
    private void ClearColumnFilter(object sender, RoutedEventArgs e)
    {
        columnFilters.Remove(editingFilterColumn);
        ColumnFilterPopup.IsOpen = false; rowPage = 0; ApplyRowFilter();
    }
    private void ColumnsChanged(object sender, RoutedEventArgs e) { columnPage = 0; RenderDiff(); }
    private void PreviousColumns(object sender, RoutedEventArgs e) { columnPage = Math.Max(0, columnPage - 1); RenderDiff(); }
    private void NextColumns(object sender, RoutedEventArgs e) { columnPage++; RenderDiff(); }
    private void PreviousRows(object sender, RoutedEventArgs e) { rowPage = Math.Max(0, rowPage - 1); RenderDiff(); }
    private void NextRows(object sender, RoutedEventArgs e) { rowPage++; RenderDiff(); }

    private void RenderDiff()
    {
        if (diff == null) return;
        var columnIds = Enumerable.Range(0, diff.Columns.Count).Where(i => ChangedColumnsOnly.IsChecked != true
            || diff.Columns[i].SchemaChanged || diff.Rows.Any(r => r.Changed[i])).ToArray();
        var rowPages = Math.Max(1, (filteredRows.Length + RowsPerPage - 1) / RowsPerPage);
        var columnPages = Math.Max(1, (columnIds.Length + ColumnsPerPage - 1) / ColumnsPerPage);
        rowPage = Math.Clamp(rowPage, 0, rowPages - 1); columnPage = Math.Clamp(columnPage, 0, columnPages - 1);
        var visibleColumns = columnIds.Skip(columnPage * ColumnsPerPage).Take(ColumnsPerPage).ToArray();
        DiffGrid.ItemsSource = null; DiffGrid.Columns.Clear();
        DiffGrid.Columns.Add(new DataGridTemplateColumn { Header = FilterHeader(-1, "Row / Subrow", "按行号筛选"), Width = 120, CellTemplate = CellTemplate("Label", "Color", "Background"), CellStyle = DiffCellStyle() });
        foreach (var (index, local) in visibleColumns.Select((n, i) => (n, i)))
        {
            var column = diff.Columns[index];
            var header = FilterHeader(index, $"[{column.Id}] {column.Header}", column.Detail, column.SchemaChanged);
            DiffGrid.Columns.Add(new DataGridTemplateColumn
            {
                Header = header, Width = visibleColumns.Length <= 4 ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(205), MinWidth = 160, SortMemberPath = index.ToString(),
                CellTemplate = CellTemplate($"Cells[{local}].Text", "Color", $"Cells[{local}].Background", $"Cells[{local}]"), CellStyle = DiffCellStyle()
            });
        }
        var lines = new List<DiffLine>();
        foreach (var row in filteredRows.Skip(rowPage * RowsPerPage).Take(RowsPerPage))
        {
            if (row.Before != null) lines.Add(MakeLine(row, false, visibleColumns));
            if (row.After != null) lines.Add(MakeLine(row, true, visibleColumns));
        }
        DiffGrid.ItemsSource = lines;
        RowPageLabel.Text = $"{rowPage + 1}/{rowPages} · {filteredRows.Length:N0} 行";
        ColumnPageLabel.Text = $"{columnPage + 1}/{columnPages} · {columnIds.Length:N0} 列";
        if (lines.Count == 0)
        {
            OldCell.Text = diff.Rows.Count == 0 ? "没有行值差异；文件可能仅调整结构或导出格式。列结构变化以黄色表头标记。" : "没有符合搜索条件的行。";
            NewCell.Text = diff.Rows.Count == 0 ? $"表结构：{diff.Columns.Count(c => c.SchemaChanged)} 列变化。" : "";
        }
    }
    private DiffLine MakeLine(ChangedRow row, bool added, int[] columns)
    {
        var cells = added ? row.After! : row.Before!;
        var background = added ? "#F0FFF4" : "#FFF5F5";
        return new DiffLine(row, (added ? "+  " : "−  ") + row.Key, Brush(added ? "#116329" : "#82071E"), Brush(background),
            columns.Select(i => PresentCell(row, diff!.Columns[i], i, added)).ToArray());
    }
    private static DisplayCell PresentCell(ChangedRow row, ComparedColumn column, int index, bool added)
    {
        var oldCell = row.Before?[index]; var newCell = row.After?[index];
        var cell = added ? newCell : oldCell;
        var text = cell?.Display ?? "∅";
        var difference = CellPresentation.Compare(column, oldCell, newCell);
        var spans = cell == null ? new[] { new TextSpan(text, false) } : added ? difference.After : difference.Before;
        var isString = CellPresentation.IsString(added ? column.After : column.Before);
        var background = !row.Changed[index] ? added ? "#F0FFF4" : "#FFF5F5"
            : isString ? added ? "#DAFBE1" : "#FFEBE9" : added ? "#ACEEBB" : "#FFCECB";
        return new DisplayCell(text, Brush(background), Brush(added ? "#7EE29A" : "#FF9B95"), spans);
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
    private void CellSelected(object sender, SelectedCellsChangedEventArgs e)
    {
        if (diff == null || DiffGrid.SelectedCells.Count == 0) return;
        var selected = DiffGrid.SelectedCells[0];
        if (selected.Item is not DiffLine line || !int.TryParse(selected.Column.SortMemberPath, out var column)) return;
        var row = line.Row; var definition = diff.Columns[column];
        var oldValue = row.Before?[column]; var newValue = row.After?[column];
        var rawDifference = oldValue?.Display == newValue?.Display && oldValue?.Canonical != newValue?.Canonical;
        OldCell.Show($"− {row.Key} · [{definition.Id}] {definition.Before?.Name ?? "无此列"}",
            PresentCell(row, definition, column, false), rawDifference ? "原始值：" + oldValue?.Canonical : "");
        NewCell.Show($"+ {row.Key} · [{definition.Id}] {definition.After?.Name ?? "无此列"}",
            PresentCell(row, definition, column, true), rawDifference ? "原始值：" + newValue?.Canonical : "");
    }

    private void UpdateCatalog()
    {
        CatalogTitle.Text = catalog == null ? "官方国服 V3 补丁" : "最新目标 · " + catalog.Target;
        CatalogInfo.Text = catalog == null ? "点击刷新，获取官方可下载列表。" : $"{catalog.Packages.Count} 条更新 · {catalog.Packages.Sum(p => p.Files.Count)} 个 ZIP · 查询于 {catalog.CheckedAt:yyyy-MM-dd HH:mm}";
        ApplyCatalogFilter();
    }
    private void FilterCatalog(object sender, TextChangedEventArgs e) { if (ready) ApplyCatalogFilter(); }
    private void ApplyCatalogFilter()
    {
        var search = CatalogSearch.Text.Trim();
        CatalogGrid.ItemsSource = catalog?.Packages.Where(p => (p.From + " " + p.To + " " + p.VersionView).Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => Version.TryParse(p.To, out var version) ? version : new Version()).ToArray();
    }
    private void CatalogSelected(object sender, SelectionChangedEventArgs e)
    {
        if (CatalogGrid.SelectedItem is not CatalogPackage package) return;
        CatalogDetail.Text = string.Join("\n", package.Files.Select(f => $"{f.Name} · {f.Size / 1048576d:N2} MiB · MD5 {f.Md5}"))
            + (package.Error == null ? "\n下载到 tmp/patches，大小及 MD5 校验通过后可查看；不执行安装。" : "\n查询错误：" + package.Error);
    }
    private async void RefreshCatalog(object sender, RoutedEventArgs e) => await Work("正在查询官方补丁目录…", async token =>
    {
        catalog = await PatchCatalog.Refresh(Reporter(), token); UpdateCatalog();
    });
    private async void DownloadPatch(object sender, RoutedEventArgs e)
    {
        if (CatalogGrid.SelectedItem is not CatalogPackage package) { SetStatus("请先选择需要下载的补丁。", true); return; }
        await Work("正在检查已下载的文件…", async token =>
        {
            var paths = await PatchCatalog.Download(package, Reporter(), token);
            await LoadPatchFiles(paths, token);
            ResourceNav.IsChecked = true;
        });
    }
    private static SolidColorBrush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); return brush;
    }
    private sealed record DiffLine(ChangedRow Row, string Label, SolidColorBrush Color, SolidColorBrush Background, DisplayCell[] Cells);
}
