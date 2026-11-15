using System.Windows;
using System.Windows.Input;

namespace FFXIVPatchParser.Desktop;

public partial class MainWindow
{
    private sealed record DiffTableState(DiffResult Result, SheetData? Before, SheetData? After,
        KeyValuePair<int, string>[] Filters, int LoadedRows, bool Linked, bool ChangedOnly, Action Restore);
    private readonly TableTabCollection<DiffTableState> diffTableTabs = new();
    internal string? CurrentDiffTableName => relationSheet;

    private void SaveDiffTableTab()
    {
        if (relationSheet is { } name && diff != null && diffTableTabs.Contains(name))
            diffTableTabs[name] = new(diff, rawBefore, rawAfter, columnFilters.ToArray(), loadedDiffRows,
                DiffLinked.IsChecked == true, ChangedColumnsOnly.IsChecked == true, TableTabs.Capture(DiffGrid));
    }

    private void RenderDiffTableTabs() => TableTabs.Render(DiffTableTabStrip, diffTableTabs.Names, diffTableTabs.Opening ?? relationSheet, diffTableTabs.Preview,
        async name => await OpenDiffTableAsync(name), PinDiffTable, async name => await CloseDiffTableTab(name));

    private void PinDiffTable(string name)
    {
        if (diffTableTabs.Pin(name)) RenderDiffTableTabs();
    }

    internal Task OpenDiffTableAsync(string name) => ShowDiffTableAsync(name, pin: false);

    private async Task ShowDiffTableAsync(string name, bool pin)
    {
        if (pin) PinDiffTable(name);
        if (operation != null || before == null || after == null) return;
        if (name == relationSheet && diff != null) return;
        var sheet = sheets.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (sheet == null) return;
        RememberNavigation();
        SaveDiffTableTab();
        CloseCellDetails();
        ResetDiffRelations();
        diff = null; filteredRows = [];
        DiffGrid.ItemsSource = null; DiffGrid.Columns.Clear();
        FitDiffColumnsButton.IsEnabled = false;
        columnFilters.Clear(); ColumnFilterPopup.IsOpen = false;
        ClearFiltersButton.Visibility = Visibility.Collapsed;
        DiffEmptyMessage.Visibility = Visibility.Collapsed;
        diffTableTabs.Open(name);
        if (pin) diffTableTabs.Pin(name);
        if (diffTableTabs.State(name) is { } state)
        {
            diff = state.Result;
            SetDiffRelationSources(name, state.Before, state.After);
            foreach (var pair in state.Filters) columnFilters[pair.Key] = pair.Value;
            DiffLinked.IsChecked = state.Linked;
            ChangedColumnsOnly.IsChecked = state.ChangedOnly;
            loadedDiffRows = state.LoadedRows;
            filteredRows = diff.Rows.Where(row => CellPresentation.Matches(row, columnFilters)).ToArray();
            ClearFiltersButton.Content = $"清除筛选 ({columnFilters.Count})";
            ClearFiltersButton.Visibility = columnFilters.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            RenderDiff();
            state.Restore();
        }
        else
        {
            RenderDiffTableTabs();
            await Work("正在读取并比较 " + name + "…", async token =>
            {
                DiffSummary.Text = name + " · 加载中…";
                var data = await Task.WhenAll(before.Read(name, token), after.Read(name, token));
                var result = await Task.Run(() => TableDiff.Compare(data[0], data[1], token), token);
                token.ThrowIfCancellationRequested();
                diff = result;
                SetDiffRelationSources(name, data[0], data[1]);
                ApplyRowFilter();
            });
        }
        if (diff != null) DiffSummary.Text = $"{sheets.Count:N0} 张变化表 · {name}：+{diff.Added:N0} / −{diff.Removed:N0} / ~{diff.Modified:N0} 行 · {diff.UnchangedRows:N0} 行不变";
        RenderDiffTableTabs();
    }

    private async Task CloseDiffTableTab(string name)
    {
        if (operation != null) return;
        var index = diffTableTabs.Close(name);
        if (index < 0) return;
        if (relationSheet == name)
        {
            RememberNavigation();
            ClearActiveDiffTable();
            if (diffTableTabs.Count > 0) await OpenDiffTableAsync(diffTableTabs.Names.ElementAt(Math.Min(index, diffTableTabs.Count - 1)));
        }
        RenderDiffTableTabs();
    }

    private void ClearActiveDiffTable()
    {
        ResetDiffRelations(); CloseCellDetails();
        diff = null; filteredRows = [];
        DiffGrid.ItemsSource = null; DiffGrid.Columns.Clear();
        FitDiffColumnsButton.IsEnabled = false;
        columnFilters.Clear(); ColumnFilterPopup.IsOpen = false;
        ClearFiltersButton.Visibility = Visibility.Collapsed;
        DiffEmptyMessage.Visibility = Visibility.Collapsed;
        DiffSummary.Text = "选择一张变化表查看差异";
    }

    private void CopyDiffTableName(object sender, MouseButtonEventArgs e) => TableTabs.CopyName(sender, e);

    private async void ClickDiffTable(object sender, MouseButtonEventArgs e)
    {
        if (SqPackBrowser.FindAncestor<System.Windows.Controls.TreeViewItem>(e.OriginalSource as DependencyObject)?.DataContext
            is PathTreeNode { IsDirectory: false, Payload: SheetChange sheet })
            await diffTableTabs.OpenFromTreeAsync(sheet.Name, e.ClickCount == 2, ShowDiffTableAsync);
    }
}
