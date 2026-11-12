using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FFXIVPatchParser.Desktop;

internal static class TableColumnSizing
{
    public static void Apply<TRow>(DataGrid grid, IReadOnlyList<TRow> rows, bool fitContents,
        Func<TRow, int, (string Text, double IconWidth, bool Emphasized)> cell)
    {
        var typeface = new Typeface(grid.FontFamily, grid.FontStyle, grid.FontWeight, grid.FontStretch);
        var emphasized = new Typeface(grid.FontFamily, grid.FontStyle, FontWeights.SemiBold, grid.FontStretch);
        var dpi = VisualTreeHelper.GetDpi(grid).PixelsPerDip;
        foreach (var column in grid.Columns)
        {
            var width = grid.MinColumnWidth;
            if (fitContents)
            {
                foreach (var row in rows)
                {
                    var value = cell(row, column.DisplayIndex);
                    var textWidth = value.Text.Length == 0 ? 0 : new FormattedText(value.Text, CultureInfo.CurrentCulture,
                        grid.FlowDirection, value.Emphasized ? emphasized : typeface, grid.FontSize, Brushes.Black, dpi).WidthIncludingTrailingWhitespace;
                    width = Math.Max(width, Math.Ceiling(textWidth + value.IconWidth) + 22);
                }
            }
            column.MinWidth = width;
            column.Width = DataGridLength.SizeToHeader;
        }
    }
}
