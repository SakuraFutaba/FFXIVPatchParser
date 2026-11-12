using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FFXIVPatchParser.Desktop;

public partial class MainWindow
{
    private string? relationSheet;
    private SheetData? rawBefore, rawAfter;
    private RelationResolver? beforeRelations, afterRelations;
    private CancellationTokenSource? relationOperation;
    private CancellationTokenSource? relationNavigation;
    private CancellationTokenSource? diffCellRelationRequest;
    private Task diffRelationPresentationTask = Task.CompletedTask;
    private bool diffPresentationPending;
    private readonly Dictionary<(bool Added, RowKey Row, string Column), RelatedCell> relationValues = new();
    private readonly Dictionary<(bool Added, string Column), SheetColumn> relationColumns = new();

    private void SetDiffRelationSources(string sheet, SheetData? oldData, SheetData? newData)
    {
        relationSheet = sheet; rawBefore = oldData; rawAfter = newData;
        CreateDiffResolvers();
    }

    private void CreateDiffResolvers()
    {
        beforeRelations = before == null ? null : new(SqPackView.Definitions, before);
        afterRelations = after == null ? null : new(SqPackView.Definitions, after);
    }

    private void ResetDiffRelations()
    {
        ClearDiffRelationPresentation();
        relationColumns.Clear();
        relationNavigation?.Cancel();
        relationSheet = null; rawBefore = rawAfter = null;
        beforeRelations = afterRelations = null;
    }

    private void ClearDiffRelationPresentation()
    {
        relationOperation?.Cancel();
        diffPresentationPending = false;
        relationValues.Clear();
    }

    private void ToggleDiffRelations(object sender, RoutedEventArgs e)
    {
        if (DiffLinked.IsChecked == true) CreateDiffResolvers();
        else if (diff != null) SetStatus("Raw 显示 · 字段名来自对应版本的已验证定义。");
        RenderDiff();
    }

    private ComparedColumn PresentedColumn(ComparedColumn column) => new(column.Id,
        relationColumns.TryGetValue((false, column.Id), out var oldColumn) ? oldColumn : column.Before,
        relationColumns.TryGetValue((true, column.Id), out var newColumn) ? newColumn : column.After);

    private SheetCell? PresentedValue(ChangedRow row, ComparedColumn column, int index, bool added, bool detailed = false)
    {
        var original = added ? row.After?[index] : row.Before?[index];
        if (original == null || DiffLinked.IsChecked != true ||
            !relationValues.TryGetValue((added, row.Key, column.Id), out var relation)) return original;
        var text = detailed || relation.Reference == null ? relation.Display : relation.Label ?? relation.Reference;
        return original with { Display = text };
    }

    private async Task LoadDiffRelationPresentation()
    {
        if (diff == null || relationSheet == null) return;
        using var pending = new CancellationTokenSource();
        relationOperation = pending;
        var capturedDiff = diff;
        var sheet = relationSheet;
        var related = DiffLinked.IsChecked == true;
        var keys = filteredRows.Skip(rowPage * RowsPerPage).Take(RowsPerPage).Select(row => row.Key).ToArray();
        var oldResolver = beforeRelations; var newResolver = afterRelations;
        SheetData? Page(SheetData? source)
        {
            if (!related || source == null) return source;
            var rows = new Dictionary<RowKey, SheetCell[]>();
            foreach (var key in keys)
                if (source.Rows.TryGetValue(key, out var values)) rows.Add(key, values);
            return new(source.Columns, rows);
        }
        var oldPage = Page(rawBefore); var newPage = Page(rawAfter);
        try
        {
            if (related)
            {
                SetStatus("正在解析当前页的表格关联…");
                using var download = CancellationTokenSource.CreateLinkedTokenSource(pending.Token);
                download.CancelAfter(TimeSpan.FromSeconds(8));
                try { await SqPackView.Definitions.DownloadAsync([sheet], download.Token); }
                catch (Exception) when (!pending.IsCancellationRequested) { /* Versioned CSV headers remain usable offline. */ }
            }
            pending.Token.ThrowIfCancellationRequested();
            async Task<RelatedSheetPage?> Read(RelationResolver? resolver, SheetData? data)
                => resolver == null || data == null ? null : await Task.Run(() => resolver.EnrichWithDetailsAsync(sheet, data, related, pending.Token), pending.Token);
            var results = await Task.WhenAll(Read(oldResolver, oldPage), Read(newResolver, newPage));
            pending.Token.ThrowIfCancellationRequested();
            if (diff != capturedDiff || relationSheet != sheet) return;
            var columnsChanged = false;
            for (var side = 0; side < results.Length; side++)
            {
                if (results[side] is not { } page) continue;
                var rawColumns = (side == 1 ? newPage : oldPage)!.Columns;
                for (var i = 0; i < page.Data.Columns.Count; i++)
                {
                    var column = page.Data.Columns[i];
                    var key = (side == 1, column.Id);
                    if (column == rawColumns[i] && relationColumns.ContainsKey(key)) continue;
                    if (column != relationColumns.GetValueOrDefault(key, rawColumns[i])) columnsChanged = true;
                    relationColumns[key] = column;
                }
                foreach (var (key, relation) in page.Cells)
                    relationValues[(side == 1, key.Row, page.Data.Columns[key.Column].Id)] = relation;
            }
            diffPresentationPending = columnsChanged || relationValues.Count > 0;
            ApplyDiffRelationPresentation();
            var warning = new[] { oldResolver?.LastWarning, newResolver?.LastWarning }.FirstOrDefault(message => message != null);
            if (related) SetStatus(warning ?? "关联显示 · 引用分别取自旧、新数据源；差异按原始值判断。右键绿色行可定位本地单元格或打开关联。");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (relationOperation == pending) SetStatus("关联解析失败，保留原始值：" + ex.Message, true); }
        finally { if (relationOperation == pending) relationOperation = null; }
    }

    private void ShowDiffCellValues(ChangedRow row, ComparedColumn column, int index)
    {
        var definition = PresentedColumn(column);
        var oldValue = row.Before?[index];
        var newValue = row.After?[index];
        var rawDifference = oldValue?.Display == newValue?.Display && oldValue?.Canonical != newValue?.Canonical;
        string RawSuffix(bool added, SheetCell? original)
        {
            if (original == null) return "";
            var target = DiffLinked.IsChecked == true
                ? relationValues.GetValueOrDefault((added, row.Key, column.Id))?.TargetKey : null;
            return rawDifference || (target is { } key && original.Display != key.ToString())
                ? "原始值：" + original.Canonical : "";
        }
        OldCell.Show($"− {row.Key} · [{definition.Id}] {definition.Before?.Name ?? "无此列"}",
            PresentCell(row, definition, index, false, detailed: true), RawSuffix(false, oldValue));
        NewCell.Show($"+ {row.Key} · [{definition.Id}] {definition.After?.Name ?? "无此列"}",
            PresentCell(row, definition, index, true, detailed: true), RawSuffix(true, newValue));
    }

    private async Task LoadDiffCellRelations(ChangedRow row, ComparedColumn column, int index)
    {
        if (DiffLinked.IsChecked != true || relationSheet is not { } sheet) return;
        using var request = new CancellationTokenSource();
        diffCellRelationRequest = request;
        var token = request.Token;
        var oldKey = (false, row.Key, column.Id);
        var newKey = (true, row.Key, column.Id);
        var initialOld = relationValues.GetValueOrDefault(oldKey);
        var initialNew = relationValues.GetValueOrDefault(newKey);
        var oldResolver = beforeRelations;
        var newResolver = afterRelations;
        var oldData = rawBefore;
        var newData = rawAfter;

        async Task<RelatedCell?> Read(bool added, SheetData? raw, RelationResolver? resolver)
        {
            if (relationValues.TryGetValue((added, row.Key, column.Id), out var existing)) return existing;
            if (raw == null || resolver == null || !raw.Rows.ContainsKey(row.Key)) return null;
            var position = Enumerable.Range(0, raw.Columns.Count).FirstOrDefault(i => raw.Columns[i].Id == column.Id, -1);
            if (position < 0) return null;
            try
            {
                return await Task.Run(() => resolver.ResolveAsync(sheet, raw, row.Key, position, token), token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return null;
            }
        }

        async Task ShowIcon(DiffValueView view, RelatedCell? relation)
        {
            if ((relation?.TargetIconPath ?? relation?.IconPath) is not { } path) return;
            var bitmap = await SqPackView.ReadRelationIconAsync(path, token);
            token.ThrowIfCancellationRequested();
            if (bitmap == null) return;
            view.ShowTargetIcon(bitmap, path, async (_, _) =>
            {
                CloseCellDetails();
                await SqPackView.ShowTextureAsync(path);
            });
        }

        try
        {
            if ((initialOld == null && row.Before?[index] != null) || (initialNew == null && row.After?[index] != null))
                await diffRelationPresentationTask.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            var results = await Task.WhenAll(Read(false, oldData, oldResolver), Read(true, newData, newResolver));
            token.ThrowIfCancellationRequested();
            var oldRelation = results[0] is { Reference: not null } or { IconPath: not null } ? results[0] : null;
            var newRelation = results[1] is { Reference: not null } or { IconPath: not null } ? results[1] : null;
            if (oldRelation != initialOld || newRelation != initialNew)
            {
                if (oldRelation != null) relationValues[oldKey] = oldRelation;
                if (newRelation != null) relationValues[newKey] = newRelation;
                diffPresentationPending = true;
                ShowDiffCellValues(row, column, index);
            }
            await Task.WhenAll(ShowIcon(OldCell, oldRelation), ShowIcon(NewCell, newRelation));
        }
        catch (OperationCanceledException) { }
        finally { if (diffCellRelationRequest == request) diffCellRelationRequest = null; }
    }

    private void ApplyDiffRelationPresentation()
    {
        if (!diffPresentationPending || CellDetailsPopup.IsOpen || DiffGrid.IsMouseCaptureWithin) return;
        var current = DiffGrid.CurrentCell;
        var index = current.Column?.DisplayIndex ?? -1;
        var focused = DiffGrid.IsKeyboardFocusWithin;
        RenderDiff(updateRelations: false);
        if (current.Item is not DiffLine previous || index < 0 || index >= DiffGrid.Columns.Count) return;
        var line = DiffGrid.Items.OfType<DiffLine>().FirstOrDefault(item => item.Label == previous.Label);
        if (line == null) return;
        var column = DiffGrid.Columns[index];
        var cell = new DataGridCellInfo(line, column);
        if (focused)
        {
            DiffGrid.ScrollIntoView(line, column);
            DiffGrid.Focus();
        }
        DiffGrid.CurrentCell = cell;
        DiffGrid.SelectedCells.Clear();
        DiffGrid.SelectedCells.Add(cell);
    }

    private void OpenDiffCellActions(object sender, MouseButtonEventArgs e)
    {
        var hit = VisualTreeHelper.HitTest(DiffGrid, e.GetPosition(DiffGrid))?.VisualHit;
        for (var element = hit; element != null && element != DiffGrid; element = VisualTreeHelper.GetParent(element))
        {
            if (element is not DataGridCell cell) continue;
            if (cell.DataContext is not DiffLine line || !line.Label.StartsWith('+') || relationSheet == null || rawAfter == null) return;
            var position = -1;
            if (int.TryParse(cell.Column.SortMemberPath, out var aligned) && diff != null)
            {
                position = Enumerable.Range(0, rawAfter.Columns.Count).FirstOrDefault(i => rawAfter.Columns[i].Id == diff.Columns[aligned].Id, -1);
                if (position < 0) return;
            }
            var sheet = relationSheet; var key = line.Row.Key; var raw = rawAfter; var resolver = afterRelations;
            var menu = new ContextMenu();
            var locate = new MenuItem { Header = position < 0 ? "在本地 SqPack 定位此行" : "在本地 SqPack 定位此单元格" };
            locate.Click += async (_, _) =>
            {
                CloseCellDetails();
                await SqPackView.JumpToCellAsync(sheet, key, position);
            };
            menu.Items.Add(locate);
            if (position >= 0 && resolver != null)
            {
                var follow = new MenuItem { Header = "打开关联记录 / Icon 图片" };
                follow.Click += async (_, _) => await OpenRelatedDiffValue(sheet, raw, key, position, resolver);
                menu.Items.Add(follow);
            }
            CloseCellDetails(); menu.PlacementTarget = cell; menu.IsOpen = true;
            e.Handled = true; return;
        }
    }

    private async Task OpenRelatedDiffValue(string sheet, SheetData raw, RowKey key, int position, RelationResolver resolver)
    {
        relationNavigation?.Cancel();
        using var pending = new CancellationTokenSource();
        relationNavigation = pending;
        var capturedDiff = diff;
        try
        {
            var related = await Task.Run(() => resolver.ResolveAsync(sheet, raw, key, position, pending.Token), pending.Token);
            pending.Token.ThrowIfCancellationRequested();
            if (diff != capturedDiff || relationSheet != sheet || DiffNav.IsChecked != true) return;
            if (related.IconPath is { } path)
            {
                await SqPackView.ShowTextureAsync(path);
            }
            else if (related.TargetSheet is { } target && related.TargetKey is { } targetKey)
            {
                await SqPackView.JumpToCellAsync(target, targetKey, -1);
            }
            else SetStatus(resolver.LastWarning ?? "此单元格没有可打开的关联：" + related.Status);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (relationNavigation == pending) SetStatus("无法打开关联：" + ex.Message, true); }
        finally { if (relationNavigation == pending) relationNavigation = null; }
    }
}
