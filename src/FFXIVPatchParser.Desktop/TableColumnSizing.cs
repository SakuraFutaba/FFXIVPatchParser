using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FFXIVPatchParser.Desktop;

internal static class TableColumnSizing
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DataGridColumn, WidthState> widths = new();
    private sealed record WidthState(double Value);

    // 重建关联显示时保留滚动位置与手调列宽；自动列宽继续跟随新加载的数据。
    public static Action Capture(DataGrid grid, bool preserve)
    {
        if (!preserve) return () => { };
        var manual = grid.Columns.Select((column, index) => (column, key: ColumnKey(column, index)))
            .Where(pair => widths.TryGetValue(pair.column, out var state) && pair.column.Width.IsAbsolute && Math.Abs(pair.column.Width.Value - state.Value) > 1)
            .ToDictionary(pair => pair.key, pair => pair.column.Width.Value);
        var viewer = FindScrollViewer(grid);
        var horizontal = viewer?.HorizontalOffset ?? 0;
        var vertical = viewer?.VerticalOffset ?? 0;
        return () =>
        {
            for (var index = 0; index < grid.Columns.Count; index++)
                if (manual.TryGetValue(ColumnKey(grid.Columns[index], index), out var width)) grid.Columns[index].Width = width;
            grid.UpdateLayout();
            viewer?.ScrollToHorizontalOffset(horizontal);
            viewer?.ScrollToVerticalOffset(vertical);
        };
    }

    private static (int Position, string? Id) ColumnKey(DataGridColumn column, int index)
        => string.IsNullOrEmpty(column.SortMemberPath) ? (index, null) : (-1, column.SortMemberPath);

    public static void ShiftRows(DataGrid grid, int rows)
    {
        if (FindScrollViewer(grid) is { } viewer) viewer.ScrollToVerticalOffset(viewer.VerticalOffset + rows);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer) return viewer;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } child) return child;
        return null;
    }

    public static void Apply<TRow>(DataGrid grid, IReadOnlyList<TRow> rows,
        Func<TRow, int, (string Text, double IconWidth, bool Emphasized)> cell)
    {
        var typeface = new Typeface(grid.FontFamily, grid.FontStyle, grid.FontWeight, grid.FontStretch);
        var emphasized = new Typeface(grid.FontFamily, grid.FontStyle, FontWeights.SemiBold, grid.FontStretch);
        var dpi = VisualTreeHelper.GetDpi(grid).PixelsPerDip;
        for (var index = 0; index < grid.Columns.Count; index++)
        {
            var column = grid.Columns[index];
            var width = grid.MinColumnWidth;
            var measured = new HashSet<(string Text, double IconWidth, bool Emphasized)>();
            foreach (var row in rows)
            {
                var value = cell(row, index);
                if (!measured.Add(value)) continue;
                var textWidth = value.Text.Length == 0 ? 0 : new FormattedText(value.Text, CultureInfo.CurrentCulture,
                    grid.FlowDirection, value.Emphasized ? emphasized : typeface, grid.FontSize, Brushes.Black, dpi).WidthIncludingTrailingWhitespace;
                width = Math.Max(width, Math.Ceiling(textWidth + value.IconWidth) + 22);
            }
            if (column.Header is FrameworkElement header)
            {
                header.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                width = Math.Max(width, Math.Ceiling(header.DesiredSize.Width) + 22);
            }
            column.MinWidth = grid.MinColumnWidth;
            column.Width = width;
            widths.Remove(column);
            widths.Add(column, new WidthState(width));
        }
    }
}
