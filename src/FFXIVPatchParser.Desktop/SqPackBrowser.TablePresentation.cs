using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FFXIVPatchParser.Desktop;

public partial class SqPackBrowser
{
    private const int TableIconSize = 18;
    private const int TableIconSpacing = 5;
    private const int TableIconCacheSize = 384;
    private static readonly Brush LinkForeground = (Brush)new SolidColorBrush(Color.FromRgb(23, 92, 211)).GetAsFrozen();

    private readonly Dictionary<string, BitmapSource?> tableIconCache = new(StringComparer.Ordinal);
    private CancellationTokenSource? tableIconCancellation;
    private Task tableIconTask = Task.CompletedTask;

    internal async Task<BitmapSource?> ReadRelationIconAsync(string path, CancellationToken token)
    {
        try
        {
            await AutoConnect().WaitAsync(token);
            if (closing || game is not { } source) return null;
            var bitmap = await Task.Run(() => CreateBitmap(source.ReadTexture(path)), token);
            return ReferenceEquals(game, source) ? bitmap : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private void SetTargetIcon(BitmapSource? bitmap, string? path = null)
    {
        CellTargetIcon.Source = bitmap;
        CellTargetIconButton.Tag = path;
        CellTargetIconButton.ToolTip = bitmap == null ? null : $"在本地图片中打开 {path}";
        CellTargetIconViewport.ScrollToHome();
        CellTargetIconViewport.Visibility = bitmap == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static TableRow[] BuildTableRows(RelatedSheetPage page)
    {
        var relatedRows = page.Cells.ToLookup(pair => pair.Key.Row);
        return page.Data.Rows.Select(pair =>
        {
            var cells = new TableRelatedCell?[pair.Value.Length];
            foreach (var relation in relatedRows[pair.Key])
                cells[relation.Key.Column] = new(relation.Value);
            return new TableRow(pair.Key, pair.Value, cells);
        }).ToArray();
    }

    private bool IconOnlyColumn(TablePage page, int index) => showRelations &&
        (page.Columns[index].Name == "Icon" || page.Rows.Any(row => row.Related[index]?.Relation.IconPath != null));

    private void ResetTableColumnSizing(object sender, RoutedEventArgs e)
    {
        if (tablePage is not { } page) return;

        ApplyTableColumnSizing(page);
    }

    private void ApplyTableColumnSizing(TablePage page)
    {
        var iconOnly = Enumerable.Range(0, TableGrid.Columns.Count - 1).Select(index => IconOnlyColumn(page, index)).ToArray();
        TableColumnSizing.Apply(TableGrid, page.Rows, (row, displayIndex) =>
        {
            if (displayIndex == 0) return (row.Key, 0, false);
            var index = displayIndex - 1;
            var related = row.Related[index];
            var showText = !iconOnly[displayIndex - 1];
            var iconWidth = related?.PreviewIconPath == null ? 0 : TableIconSize + (showText ? TableIconSpacing : 0);
            return (showText ? row.GridValues[index] : "", iconWidth, related?.IsLink == true);
        });
    }

    private static Style TableValueStyle(int index, bool hasLinks)
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        if (hasLinks)
        {
            var linked = new DataTrigger { Binding = new Binding($"Related[{index}].IsLink"), Value = true };
            linked.Setters.Add(new Setter(TextBlock.ForegroundProperty, LinkForeground));
            linked.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold));
            linked.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, "点击单元格查看并打开关联行"));
            style.Triggers.Add(linked);
        }
        return style;
    }

    private static DataTemplate TableIconTemplate(int index, string binding, bool showText, bool hasLinks)
    {
        var panel = new FrameworkElementFactory(typeof(DockPanel));
        panel.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        var icon = new FrameworkElementFactory(typeof(Image));
        icon.SetBinding(Image.SourceProperty, new Binding($"Related[{index}].Thumbnail"));
        icon.SetBinding(FrameworkElement.ToolTipProperty, new Binding($"Related[{index}].PreviewIconPath"));
        icon.SetValue(FrameworkElement.WidthProperty, (double)TableIconSize);
        icon.SetValue(FrameworkElement.HeightProperty, (double)TableIconSize);
        icon.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        icon.SetValue(DockPanel.DockProperty, Dock.Left);
        var iconStyle = new Style(typeof(Image));
        var missingIcon = new DataTrigger { Binding = new Binding($"Related[{index}].PreviewIconPath") { FallbackValue = null }, Value = null };
        missingIcon.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed));
        iconStyle.Triggers.Add(missingIcon);
        icon.SetValue(FrameworkElement.StyleProperty, iconStyle);
        panel.AppendChild(icon);
        if (showText)
        {
            icon.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, TableIconSpacing, 0));
            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetBinding(TextBlock.TextProperty, new Binding(binding));
            text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            text.SetValue(FrameworkElement.StyleProperty, TableValueStyle(index, hasLinks));
            panel.AppendChild(text);
        }
        return new DataTemplate { VisualTree = panel };
    }

    private void TableGridVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (initialized) QueueTableIconPreviews();
    }

    private void QueueTableIconPreviews()
    {
        tableIconCancellation?.Cancel();
        if (!showRelations || !TableGrid.IsVisible || closing || tablePage is not { } page || game is not { } source) return;
        var groups = page.Rows.SelectMany(row => row.Related)
            .OfType<TableRelatedCell>().Where(cell => cell.PreviewIconPath != null)
            .GroupBy(cell => cell.PreviewIconPath!, StringComparer.Ordinal).ToArray();
        if (groups.Length == 0) return;
        var cancellation = new CancellationTokenSource();
        tableIconCancellation = cancellation;
        tableIconTask = LoadTableIconPreviews(tableIconTask, source, groups, cancellation);
    }

    private async Task LoadTableIconPreviews(Task previous, LocalGameSource source,
        IGrouping<string, TableRelatedCell>[] groups, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        try
        {
            await previous;
            foreach (var group in groups)
            {
                token.ThrowIfCancellationRequested();
                if (!tableIconCache.TryGetValue(group.Key, out var thumbnail))
                {
                    try
                    {
                        thumbnail = await Task.Run(() => CreateThumbnail(source.ReadTexture(group.Key), TableIconSize), token);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        thumbnail = null;
                    }
                    token.ThrowIfCancellationRequested();
                    if (tableIconCache.Count >= TableIconCacheSize) tableIconCache.Remove(tableIconCache.Keys.First());
                    tableIconCache[group.Key] = thumbnail;
                }
                foreach (var cell in group) cell.SetThumbnail(thumbnail);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (tableIconCancellation == cancellation) tableIconCancellation = null;
            cancellation.Dispose();
        }
    }

    private sealed class TableRelatedCell(RelatedCell relation) : INotifyPropertyChanged
    {
        public RelatedCell Relation { get; } = relation;
        public bool IsLink => Relation.TargetKey != null;
        public string? PreviewIconPath => Relation.IconPath ?? Relation.TargetIconPath;
        public BitmapSource? Thumbnail { get; private set; }
        public event PropertyChangedEventHandler? PropertyChanged;

        public void SetThumbnail(BitmapSource? thumbnail)
        {
            if (Thumbnail == thumbnail) return;
            Thumbnail = thumbnail;
            PropertyChanged?.Invoke(this, new(nameof(Thumbnail)));
        }
    }
}
