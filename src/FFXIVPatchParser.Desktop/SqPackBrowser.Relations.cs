using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace FFXIVPatchParser.Desktop;

public partial class SqPackBrowser
{
    public SaintCoinachDefinitions Definitions { get; } = new();

    private readonly Dictionary<string, string?> definitionDownloads = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<SheetColumn>> tableColumns = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim tableRelationGate = new(1);
    private RelationResolver? tableRelations;
    private bool showRelations = true;
    private CancellationTokenSource? tablePresentationCancellation;
    private Task tablePresentationTask = Task.CompletedTask;
    private (SheetData Raw, RelatedSheetPage View)? pendingTableView;
    private CancellationTokenSource? cellRelationRequest;
    private RelatedCell? cellRelation;

    private void ResetTableRelations()
    {
        tableColumns.Clear();
        tableRelations = game is { } source ? new RelationResolver(Definitions, source.AsTableSource()) : null;
    }

    private RelatedSheetPage RawTableView(string sheet, SheetData raw) => new(
        tableColumns.TryGetValue(sheet, out var columns) ? raw with { Columns = columns } : raw,
        new Dictionary<(RowKey, int), RelatedCell>());

    private async Task<(RelatedSheetPage View, string? Status)> GetTableView(string sheet, SheetData raw, bool related, CancellationToken token)
    {
        var rawView = RawTableView(sheet, raw);
        if (tableRelations is not { } resolver) return (rawView, null);
        await tableRelationGate.WaitAsync(token);
        try
        {
            string? status = null;
            if (related && !definitionDownloads.TryGetValue(sheet, out status))
            {
                using var download = CancellationTokenSource.CreateLinkedTokenSource(token);
                download.CancelAfter(TimeSpan.FromSeconds(8));
                try
                {
                    await Definitions.DownloadAsync([sheet], download.Token);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    status = "获取 SaintCoinach 定义超时";
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    status = "获取 SaintCoinach 定义失败：" + ex.Message;
                }
                token.ThrowIfCancellationRequested();
                definitionDownloads[sheet] = status;
            }
            var view = await Task.Run(() => resolver.EnrichWithDetailsAsync(sheet, raw, related, token), token);
            if (resolver.LastWarning is { } warning)
                status = status == null ? warning : $"{warning} · {status}";
            if (related && view.Data.Columns.SequenceEqual(raw.Columns) && view.Cells.Count == 0)
                status ??= "此表没有已验证的字段定义，正在显示 Raw";
            return (view, status);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (rawView, "关联解析失败，正在显示 Raw：" + ex.Message);
        }
        finally
        {
            tableRelationGate.Release();
        }
    }

    private void QueueTablePresentation()
    {
        tablePresentationCancellation?.Cancel();
        pendingTableView = null;
        if (closing || tablePage is not { } page || selectedTable is not { } sheet || tableRelations == null) return;
        if (!showRelations && tableColumns.ContainsKey(sheet)) return;
        var cancellation = new CancellationTokenSource();
        tablePresentationCancellation = cancellation;
        tablePresentationTask = LoadTablePresentation(sheet, page.Raw, showRelations, cancellation);
    }

    private async Task LoadTablePresentation(string sheet, SheetData raw, bool related, CancellationTokenSource cancellation)
    {
        try
        {
            var (view, status) = await GetTableView(sheet, raw, related, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (tablePage?.Raw != raw || !string.Equals(selectedTable, sheet, StringComparison.Ordinal)) return;
            if (!view.Data.Columns.SequenceEqual(raw.Columns)) tableColumns[sheet] = view.Data.Columns;
            else if (tableColumns.TryGetValue(sheet, out var names)) view = view with { Data = view.Data with { Columns = names } };
            if (status != null && operation == null && TablesPanel.IsVisible) SetStatus(status);
            if (view.Cells.Count == 0 && view.Data.Columns.SequenceEqual(tablePage.Columns)) return;
            pendingTableView = (raw, view);
            ApplyTablePresentation();
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (tablePresentationCancellation == cancellation) tablePresentationCancellation = null;
            cancellation.Dispose();
        }
    }

    private void ApplyTablePresentation()
    {
        if (closing || CellPopup.IsOpen || TableGrid.IsMouseCaptureWithin || pendingTableView is not { } pending) return;
        pendingTableView = null;
        if (tablePage is { } page && ReferenceEquals(page.Raw, pending.Raw)) UpdateTableView(page, pending.View);
    }

    private async Task StopTablePresentation()
    {
        CloseTableCell();
        tablePresentationCancellation?.Cancel();
        pendingTableView = null;
        await tablePresentationTask;
        await tableRelationGate.WaitAsync();
        tableRelationGate.Release();
    }

    private void ToggleTableMode(object sender, RoutedEventArgs e)
    {
        var related = !showRelations;
        if (related)
        {
            foreach (var sheetWithError in definitionDownloads.Where(pair => pair.Value != null).Select(pair => pair.Key).ToArray())
                definitionDownloads.Remove(sheetWithError);
            tableRelations = game is { } source ? new RelationResolver(Definitions, source.AsTableSource()) : null;
        }
        SetTableMode(related);
        if (tablePage is not { } page || selectedTable is not { } sheet) return;
        UpdateTableView(page, RawTableView(sheet, page.Raw));
        QueueTablePresentation();
        SetStatus(related ? "已切换至关联视图" : "已切换至 Raw");
    }

    private void SetTableMode(bool related)
    {
        showRelations = related;
        TableModeButton.Content = related ? "切换至 Raw" : "切换至关联";
    }

    private async void ImportTableDefinitions(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择 SaintCoinach 定义目录" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        var folder = dialog.FolderName;

        await Work("正在导入 SaintCoinach 定义…", async token =>
        {
            await StopTablePresentation();
            await Task.Run(() => Definitions.ImportDirectory(folder), token);
            definitionDownloads.Clear();
            ResetTableRelations();
            token.ThrowIfCancellationRequested();
            if (tablePage is { } page && selectedTable is { } sheet)
            {
                UpdateTableView(page, RawTableView(sheet, page.Raw));
                QueueTablePresentation();
            }
            return "已导入 SaintCoinach 定义";
        });
    }

    private void UpdateTableView(TablePage page, RelatedSheetPage view)
    {
        var current = TableGrid.CurrentCell;
        var index = current.Column?.DisplayIndex ?? -1;
        var focusGrid = TableGrid.IsKeyboardFocusWithin;
        tablePage = page with
        {
            Columns = view.Data.Columns,
            Rows = BuildTableRows(view)
        };
        RenderTable(scrollToFirst: false);
        if (current.Item is TableRow previous && index >= 0 && index < TableGrid.Columns.Count &&
            tablePage.Rows.FirstOrDefault(row => row.Id == previous.Id) is { } row)
            SelectTableCell(row, index, focusGrid);
    }

    private void ClearCellRelation()
    {
        cellRelationRequest?.Cancel();
        cellRelation = null;
        SetTargetIcon(null);
        CellRelationButton.Visibility = Visibility.Collapsed;
        CellRelationStatus.Visibility = Visibility.Collapsed;
    }

    private void CloseTableCell()
    {
        if (CellPopup?.PlacementTarget == null) return;
        CellPopup.IsOpen = false;
        CellPopup.PlacementTarget = null;
        selectedCell = null;
        ClearCellRelation();
        if (pendingTableView != null) Dispatcher.BeginInvoke(DispatcherPriority.Background, ApplyTablePresentation);
    }

    private async Task ShowCellRelation(RowKey key, int columnPosition, SheetData raw, RelatedCell? resolved)
    {
        if (!showRelations || tableRelations is not { } resolver || selectedTable is not { } sheet) return;
        using var request = new CancellationTokenSource();
        cellRelationRequest = request;
        try
        {
            string? warning = null;
            if (resolved == null)
            {
                await tableRelationGate.WaitAsync(request.Token);
                try
                {
                    resolved = await Task.Run(() => resolver.ResolveAsync(sheet, raw, key, columnPosition, request.Token), request.Token);
                    warning = resolver.LastWarning;
                }
                finally
                {
                    tableRelationGate.Release();
                }
            }
            if (request.IsCancellationRequested || !CellPopup.IsOpen) return;
            PresentCellRelation(resolved, warning);
            if ((resolved.TargetIconPath ?? resolved.IconPath) is { } path)
            {
                var bitmap = await ReadRelationIconAsync(path, request.Token);
                if (!request.IsCancellationRequested && CellPopup.IsOpen)
                    SetTargetIcon(bitmap, path);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (request.IsCancellationRequested || !CellPopup.IsOpen) return;
            CellRelationStatus.Text = "解析关联失败：" + ex.Message;
            CellRelationStatus.Visibility = Visibility.Visible;
        }
        finally
        {
            if (cellRelationRequest == request) cellRelationRequest = null;
        }
    }

    private void PresentCellRelation(RelatedCell result, string? warning)
    {
        cellRelation = result;
        if (selectedCell is { } cell && result.TargetKey != null && result.Display != cell.Display)
        {
            selectedCell = cell with { Display = result.Display };
            RawCellToggle.Visibility = result.Display == cell.Canonical ? Visibility.Collapsed : Visibility.Visible;
            if (RawCellToggle.IsChecked != true) CellText.Text = result.Display;
        }
        var status = warning ?? (result.Status switch
        {
            "missing-definition" => "此表没有可用的关联定义。",
            "unverified-schema" => "此表的定义未通过当前版本验证。",
            "missing-target" => "找不到关联的目标行。",
            "ambiguous-subrow-or-key" => "关联匹配多行，无法确定目标。",
            "cycle" => "关联存在循环。",
            "temporarily-unavailable" => "关联暂不可用。",
            _ => null
        });
        if (status != null)
        {
            CellRelationStatus.Text = status;
            CellRelationStatus.Visibility = Visibility.Visible;
        }
        if (result.IconPath is { Length: > 0 })
        {
            CellRelationButton.Content = "打开本地图标";
            CellRelationButton.Visibility = Visibility.Visible;
        }
        else if (result.Reference is { } reference)
        {
            CellRelationButton.Content = $"跳转至 {reference}";
            CellRelationButton.Visibility = Visibility.Visible;
        }
    }

    private async void OpenCellIcon(object sender, RoutedEventArgs e)
    {
        if (CellTargetIconButton.Tag is not string path) return;
        CloseTableCell();
        await ShowTextureAsync(path);
    }

    private async void FollowCellRelation(object sender, RoutedEventArgs e)
    {
        if (cellRelation is not { } relation) return;
        CloseTableCell();
        if (relation.IconPath is { Length: > 0 } path) await ShowTextureAsync(path);
        else if (relation.TargetSheet is { Length: > 0 } target && relation.TargetKey is { } key)
            await JumpToCellAsync(target, key, -1);
    }

    public Task JumpToCellAsync(string sheet, uint rowId, ushort? subrowId, int columnPosition)
        => JumpToCellAsync(sheet, new RowKey(rowId, subrowId), columnPosition);

    public async Task JumpToCellAsync(string sheet, RowKey key, int columnPosition)
    {
        PageRequested?.Invoke(SqPackPage.Tables);
        if (!await EnsureGameForNavigation() || game is not { } source) return;
        var name = source.SheetNames.FirstOrDefault(name => name.Equals(sheet, StringComparison.OrdinalIgnoreCase));
        if (name == null)
        {
            SetStatus($"本地 SqPack 中找不到表 {sheet}。", true);
            return;
        }

        TableRow? targetRow = null;
        await Work($"正在定位 {name} · Row {key}…", async token =>
        {
            tablePresentationCancellation?.Cancel();
            pendingTableView = null;
            var rowOffset = await Task.Run(() => source.FindSheetRowOffset(name, key, token), token)
                ?? throw new KeyNotFoundException($"本地 {name} 中找不到行 {key}。");
            var pageIndex = rowOffset / RowsPerPage;
            var page = await ReadTablePage(source, name, pageIndex * RowsPerPage, [], token);
            token.ThrowIfCancellationRequested();
            if (columnPosition < -1 || columnPosition >= page.Columns.Count)
                throw new ArgumentOutOfRangeException(nameof(columnPosition), $"本地 {name} 中找不到列 {columnPosition}。");
            targetRow = page.Rows.FirstOrDefault(row => row.Id == key)
                ?? throw new KeyNotFoundException($"本地 {name} 中找不到行 {key}。");

            selectedTable = name;
            columnFilters.Clear();
            firstTableOffset = pageIndex * RowsPerPage;
            tablePage = page;
            RenderTable();
            return $"已定位 {name} · Row {key}";
        });
        if (targetRow == null) return;

        await Dispatcher.InvokeAsync(() =>
        {
            var visibleColumn = columnPosition < 0 ? 0 : columnPosition + 1;
            if (!TableGrid.Items.Contains(targetRow) || visibleColumn >= TableGrid.Columns.Count) return;
            SelectTableTreeItem(name);
            SelectTableCell(targetRow, visibleColumn, true);
        }, DispatcherPriority.Loaded);
        QueueTablePresentation();
    }

    private void SelectTableCell(TableRow row, int columnIndex, bool focus)
    {
        var column = TableGrid.Columns[columnIndex];
        var cell = new DataGridCellInfo(row, column);
        if (focus)
        {
            TableGrid.ScrollIntoView(row, column);
            TableGrid.Focus();
        }
        TableGrid.CurrentCell = cell;
        TableGrid.SelectedCells.Clear();
        TableGrid.SelectedCells.Add(cell);
    }

    private void SelectTableTreeItem(string name)
    {
        var path = new List<int>();
        if (TablesList.ItemsSource is not IReadOnlyList<PathTreeNode> nodes || !FindTablePath(nodes))
        {
            TableSearch.Clear();
            if (TablesList.ItemsSource is not IReadOnlyList<PathTreeNode> allNodes || !FindTablePath(allNodes)) return;
        }

        ItemsControl parent = TablesList;
        for (var level = 0; level < path.Count; level++)
        {
            var index = path[level];
            parent.UpdateLayout();
            if (parent.ItemContainerGenerator.ContainerFromIndex(index) == null)
                FindVirtualizingItemsHost(parent)?.BringIndexIntoViewPublic(index);
            if (parent.ItemContainerGenerator.ContainerFromIndex(index) is not TreeViewItem item) return;
            if (level == path.Count - 1)
            {
                item.IsSelected = true;
                item.BringIntoView();
            }
            else
            {
                item.SetCurrentValue(TreeViewItem.IsExpandedProperty, true);
                parent = item;
            }
        }

        bool FindTablePath(IReadOnlyList<PathTreeNode> candidates)
        {
            for (var i = 0; i < candidates.Count; i++)
            {
                var node = candidates[i];
                path.Add(i);
                if (Equals(node.Payload, name) || FindTablePath(node.Children)) return true;
                path.RemoveAt(path.Count - 1);
            }
            return false;
        }
    }

    private static VirtualizingPanel? FindVirtualizingItemsHost(DependencyObject root)
    {
        if (root is VirtualizingPanel { IsItemsHost: true } panel) return panel;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindVirtualizingItemsHost(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }
}
