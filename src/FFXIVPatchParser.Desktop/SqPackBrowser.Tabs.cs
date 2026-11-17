using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FFXIVPatchParser.Desktop;

public partial class SqPackBrowser
{
    internal void SetTableTabHeader(ContentControl header, bool useHeader)
    {
        var target = useHeader ? header : InlineTableTabs;
        if (target.Content == TableTabsScroll) return;
        (useHeader ? InlineTableTabs : header).Content = null;
        target.Content = TableTabsScroll;
    }

    private sealed record LocalTableState(TablePage? Page, int Offset, KeyValuePair<int, string>[] Filters, bool Related, Action Restore);
    private readonly TableTabCollection<LocalTableState> localTableTabs = new();
    public string? CurrentTableName => selectedTable;

    private void SaveLocalTableTab()
    {
        if (selectedTable is { } name && localTableTabs.Contains(name))
            localTableTabs[name] = new(tablePage, firstTableOffset, columnFilters.ToArray(), showRelations, TableTabs.Capture(TableGrid));
    }

    private void RenderLocalTableTabs() => TableTabs.Render(LocalTableTabStrip, localTableTabs.Names, selectedTable, localTableTabs.Preview,
        async name => await OpenLocalTableAsync(name), PinLocalTable, async name => await CloseLocalTableTab(name));

    private void PinLocalTable(string name)
    {
        if (localTableTabs.Pin(name)) RenderLocalTableTabs();
    }

    public Task OpenLocalTableAsync(string name) => ShowLocalTableAsync(name, pin: false);

    private async Task ShowLocalTableAsync(string name, bool pin)
    {
        if (pin) PinLocalTable(name);
        if (operation != null || closing || game == null) return;
        if (name == selectedTable && tablePage != null) return;
        if (currentPage != SqPackPage.Files) NavigationStarting?.Invoke();
        SaveLocalTableTab();
        tableIconCancellation?.Cancel();
        selectedTable = name;
        ResetTable();
        localTableTabs.Open(name);
        if (pin) localTableTabs.Pin(name);
        var state = localTableTabs.State(name);
        if (state?.Page != null)
        {
            tablePage = state.Page;
            firstTableOffset = state.Offset;
            foreach (var pair in state.Filters) columnFilters[pair.Key] = pair.Value;
            showRelations = state.Related;
            TableModeButton.Content = showRelations ? "切换至 Raw" : "切换至关联";
            RenderTable();
            state.Restore();
            QueueTablePresentation();
        }
        else
        {
            RenderLocalTableTabs();
            await ReadRows();
        }
        RenderLocalTableTabs();
    }

    private async Task CloseLocalTableTab(string name)
    {
        if (operation != null) return;
        var index = localTableTabs.Close(name);
        if (index < 0) return;
        if (selectedTable == name)
        {
            NavigationStarting?.Invoke();
            tableIconCancellation?.Cancel();
            selectedTable = null;
            ResetTable();
            if (localTableTabs.Count > 0) await OpenLocalTableAsync(localTableTabs.Names.ElementAt(Math.Min(index, localTableTabs.Count - 1)));
        }
        RenderLocalTableTabs();
    }

    private void CopyLocalTableName(object sender, MouseButtonEventArgs e) => TableTabs.CopyName(sender, e);

    private async void ClickLocalTable(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<System.Windows.Controls.TreeViewItem>(e.OriginalSource as DependencyObject)?.DataContext
            is PathTreeNode { IsDirectory: false, Payload: string name })
            await localTableTabs.OpenFromTreeAsync(name, e.ClickCount == 2, ShowLocalTableAsync);
    }
}
