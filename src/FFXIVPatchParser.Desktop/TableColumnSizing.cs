using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace FFXIVPatchParser.Desktop;

internal static class TableColumnSizing
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DataGridColumn, WidthState> widths = new();
    private sealed record WidthState(double Value);

    public static Action Capture(DataGrid grid, bool preserveScroll, bool preserveWidths = true)
    {
        if (!preserveScroll) return () => { };
        var manual = !preserveWidths ? null : grid.Columns.Select((column, index) => (column, key: ColumnKey(column, index)))
            .Where(pair => widths.TryGetValue(pair.column, out var state) && pair.column.Width.IsAbsolute && Math.Abs(pair.column.Width.Value - state.Value) > 1)
            .ToDictionary(pair => pair.key, pair => pair.column.Width.Value);
        var viewer = FindScrollViewer(grid);
        var horizontal = viewer?.HorizontalOffset ?? 0;
        var vertical = viewer?.VerticalOffset ?? 0;
        return () =>
        {
            if (manual != null)
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

    private static IReadOnlyList<TRow> ViewportRows<TRow>(DataGrid grid, IReadOnlyList<TRow> fallback)
    {
        grid.UpdateLayout();
        var viewer = FindScrollViewer(grid);
        if (viewer == null) return fallback;
        var viewport = viewer.Template?.FindName("PART_ScrollContentPresenter", viewer) as ScrollContentPresenter
            ?? FindViewport(viewer);
        if (viewport == null || viewport.ActualHeight <= 0)
            return ScrollWindow();
        var visible = new List<TRow>();
        void Visit(DependencyObject element)
        {
            if (element is DataGridRow row)
            {
                // 虚拟化会在视口外保留一些行容器。宽度只由至少有一部分在视口内的行决定。
                if (row.Item is TRow item && row.Visibility == Visibility.Visible)
                {
                    var top = row.TranslatePoint(new Point(0, 0), viewport).Y;
                    if (top < viewport.ActualHeight && top + row.ActualHeight > 0) visible.Add(item);
                }
                return;
            }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++) Visit(VisualTreeHelper.GetChild(element, i));
        }
        Visit(viewport);
        return visible.Count != 0 ? visible : ScrollWindow();

        IReadOnlyList<TRow> ScrollWindow()
        {
            // 模板尚未挂载可视行时，DataGrid 的逻辑滚动量仍可定位当前窗口。
            if (!viewer.CanContentScroll || VirtualizingPanel.GetScrollUnit(grid) != ScrollUnit.Item ||
                viewer.ViewportHeight <= 0 || viewer.VerticalOffset >= fallback.Count) return fallback;
            return fallback.Skip((int)Math.Floor(viewer.VerticalOffset)).Take((int)Math.Ceiling(viewer.ViewportHeight)).ToArray();
        }

        static ScrollContentPresenter? FindViewport(DependencyObject node)
        {
            if (node is ScrollContentPresenter presenter) return presenter;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                if (FindViewport(VisualTreeHelper.GetChild(node, i)) is { } child) return child;
            return null;
        }
    }

    public static void Apply<TRow>(DataGrid grid, IReadOnlyList<TRow> rows,
        Func<TRow, int, (string Text, double IconWidth, bool Emphasized)> cell, bool viewportOnly = false)
    {
        var candidates = viewportOnly ? ViewportRows(grid, rows) : rows;
        var typeface = new Typeface(grid.FontFamily, grid.FontStyle, grid.FontWeight, grid.FontStretch);
        var emphasized = new Typeface(grid.FontFamily, grid.FontStyle, FontWeights.SemiBold, grid.FontStretch);
        var dpi = VisualTreeHelper.GetDpi(grid).PixelsPerDip;
        var textMode = TextOptions.GetTextFormattingMode(grid);
        double TextWidth(string text, Typeface font) => text.Length == 0 ? 0 :
            new FormattedText(text, CultureInfo.CurrentCulture, grid.FlowDirection, font, grid.FontSize, Brushes.Black, null, textMode, dpi)
                .WidthIncludingTrailingWhitespace;
        var defaultHeaderStyle = grid.ColumnHeaderStyle ?? grid.TryFindResource(typeof(DataGridColumnHeader)) as Style;
        var styles = new Dictionary<Style, DataGridColumnHeader>(ReferenceEqualityComparer.Instance);
        static double Horizontal(Thickness value) => value.Left + value.Right;
        static double UnconstrainedWidth(UIElement element)
        {
            element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return element.DesiredSize.Width;
        }

        double HeaderWidth(DataGridColumn column)
        {
            var style = column.HeaderStyle ?? defaultHeaderStyle;
            if (style == null) return 0;
            if (!styles.TryGetValue(style, out var metrics))
                styles.Add(style, metrics = new DataGridColumnHeader { Style = style });
            var titleTypeface = new Typeface(grid.FontFamily, grid.FontStyle, metrics.FontWeight, grid.FontStretch);
            var width = column.Header switch
            {
                // Button.Content can be a DockPanel with a separate filter chevron or dot.
                Button button => Math.Max(button.MinWidth, (button.Content switch
                {
                    DockPanel panel => panel.Children.Cast<UIElement>().Sum(child => child is TextBlock block
                        ? TextWidth(block.Text, titleTypeface) + Horizontal(block.Margin)
                        : UnconstrainedWidth(child)),
                    TextBlock text => TextWidth(text.Text, titleTypeface) + Horizontal(text.Margin),
                    string title => TextWidth(title, titleTypeface),
                    UIElement visual => UnconstrainedWidth(visual),
                    _ => 0
                }) + Horizontal(button.Padding) + Horizontal(button.BorderThickness)) + Horizontal(button.Margin),
                UIElement visual => UnconstrainedWidth(visual),
                string title => TextWidth(title, titleTypeface),
                _ => 0
            };
            return width + Horizontal(metrics.Padding) + Horizontal(metrics.BorderThickness) + 2;
        }

        for (var index = 0; index < grid.Columns.Count; index++)
        {
            var column = grid.Columns[index];
            var headerWidth = Math.Max(grid.MinColumnWidth, Math.Ceiling(HeaderWidth(column)));
            var width = headerWidth;
            var measured = new HashSet<(string Text, double IconWidth, bool Emphasized)>();
            foreach (var row in candidates)
            {
                var value = cell(row, index);
                if (!measured.Add(value)) continue;
                var textWidth = TextWidth(value.Text, value.Emphasized ? emphasized : typeface);
                width = Math.Max(width, Math.Ceiling(textWidth + value.IconWidth) + 22);
            }
            // 第一次渲染、手动调整和分页后的宽度恢复，都保留完整表头。
            column.MinWidth = headerWidth;
            column.Width = width;
            widths.Remove(column);
            widths.Add(column, new WidthState(width));
        }
    }
}
