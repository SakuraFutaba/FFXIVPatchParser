using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;

namespace FFXIVPatchParser.Desktop;

public partial class MainWindow
{
    private const int HistoryLimit = 80;
    private readonly List<NavigationLocation> backHistory = [], forwardHistory = [];
    private RadioButton? activeNavigation;
    private bool restoringNavigation;

    private sealed record NavigationLocation(RadioButton Page, string? Table = null, RowKey? Row = null,
        int Column = -1, string? ImagePath = null, bool NewSide = false);

    private NavigationLocation CaptureNavigation()
    {
        var page = activeNavigation ?? PatchNav;
        if (page == SqPackNav)
        {
            var (row, column) = SqPackView.CurrentTableCell();
            return new(page, SqPackView.CurrentTableName, row, column);
        }
        if (page == DiffNav && DiffGrid.CurrentCell.Item is DiffLine line)
        {
            var column = int.TryParse(DiffGrid.CurrentCell.Column?.SortMemberPath, out var position) ? position : -1;
            return new(page, CurrentDiffTableName, line.Row.Key, column, NewSide: line.Label.StartsWith('+'));
        }
        if (page == DiffNav) return new(page, CurrentDiffTableName);
        if (page == LocalImagesNav) return new(page, ImagePath: SqPackView.CurrentTexturePath);
        return new(page);
    }

    private void RememberNavigation()
    {
        if (restoringNavigation) return;
        PushLocation(backHistory, CaptureNavigation());
        forwardHistory.Clear();
        UpdateHistoryButtons();
    }

    private static void PushLocation(List<NavigationLocation> history, NavigationLocation location)
    {
        if (history.Count > 0 && history[^1] == location) return;
        if (history.Count == HistoryLimit) history.RemoveAt(0);
        history.Add(location);
    }

    private void UpdateHistoryButtons()
    {
        BackButton.IsEnabled = !restoringNavigation && operation == null && backHistory.Count > 0;
        ForwardButton.IsEnabled = !restoringNavigation && operation == null && forwardHistory.Count > 0;
    }

    private async void NavigateBack(object sender, RoutedEventArgs e) => await MoveInHistory(backHistory, forwardHistory);
    private async void NavigateForward(object sender, RoutedEventArgs e) => await MoveInHistory(forwardHistory, backHistory);

    private async void NavigationKeys(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Alt || e.SystemKey is not (Key.Left or Key.Right)) return;
        e.Handled = true;
        if (e.SystemKey == Key.Left) await MoveInHistory(backHistory, forwardHistory);
        else await MoveInHistory(forwardHistory, backHistory);
    }

    private async Task MoveInHistory(List<NavigationLocation> source, List<NavigationLocation> opposite)
    {
        if (restoringNavigation || operation != null) return;
        var current = CaptureNavigation();
        while (source.Count > 0 && source[^1] == current) source.RemoveAt(source.Count - 1);
        if (source.Count == 0)
        {
            UpdateHistoryButtons();
            return;
        }
        var destination = source[^1];
        source.RemoveAt(source.Count - 1);
        PushLocation(opposite, current);
        restoringNavigation = true;
        UpdateHistoryButtons();
        try
        {
            CloseCellDetails();
            await SqPackView.WaitForCurrentWorkAsync();
            destination.Page.IsChecked = true;
            if (destination.Page == SqPackNav)
                await SqPackView.RestoreTableLocationAsync(destination.Table, destination.Row, destination.Column);
            else if (destination.Page == DiffNav)
            {
                if (destination.Table is { } compared)
                {
                    await OpenDiffTableAsync(compared);
                    if (CurrentDiffTableName == compared && destination.Row is { } row)
                        RestoreDiffCell(row, destination.Column, destination.NewSide);
                }
                else
                {
                    SaveDiffTableTab();
                    ClearActiveDiffTable();
                    RenderDiffTableTabs();
                }
            }
            else if (destination.Page == LocalImagesNav)
                await SqPackView.RestoreTextureLocationAsync(destination.ImagePath);
        }
        catch (Exception ex) { SetStatus("无法恢复位置：" + ex.Message, true); }
        finally
        {
            restoringNavigation = false;
            UpdateHistoryButtons();
        }
    }

    private void RestoreDiffCell(RowKey row, int column, bool newSide)
    {
        var line = DiffGrid.Items.OfType<DiffLine>().FirstOrDefault(item => item.Row.Key == row && item.Label.StartsWith('+') == newSide);
        if (line == null || DiffGrid.Columns.Count == 0) return;
        var gridColumn = DiffGrid.Columns.FirstOrDefault(item => item.SortMemberPath == column.ToString()) ?? DiffGrid.Columns[0];
        var cell = new DataGridCellInfo(line, gridColumn);
        DiffGrid.ScrollIntoView(line, gridColumn);
        DiffGrid.CurrentCell = cell;
        DiffGrid.SelectedCells.Clear();
        DiffGrid.SelectedCells.Add(cell);
    }

    private void MinimizeWindow(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void CloseWindow(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);
    private void MaximizeWindow(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (MaximizeButton == null) return;
        var maximized = WindowState == WindowState.Maximized;
        var frame = SystemParameters.WindowResizeBorderThickness;
        WindowContent.Margin = maximized ? new Thickness(Math.Max(8, frame.Left), Math.Max(8, frame.Top),
            Math.Max(8, frame.Right), Math.Max(8, frame.Bottom)) : default;
        WindowChrome.GetWindowChrome(this).CaptionHeight = 38 + WindowContent.Margin.Top;
        MaximizeGlyph.Data = Geometry.Parse(maximized ? "M 4,4 H 11 V 11 H 4 Z M 1,8 V 1 H 8 V 4" : "M 1,1 H 11 V 11 H 1 Z");
        MaximizeButton.ToolTip = maximized ? "还原窗口" : "最大化";
        System.Windows.Automation.AutomationProperties.SetName(MaximizeButton, (string)MaximizeButton.ToolTip);
    }
}
