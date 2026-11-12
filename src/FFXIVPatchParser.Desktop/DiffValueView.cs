using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FFXIVPatchParser.Desktop;

public sealed class DiffValueView : RichTextBox
{
    public DiffValueView() => IsDocumentEnabled = true;

    public string Text
    {
        get => new TextRange(Document.ContentStart, Document.ContentEnd).Text;
        set => SetParagraph(new Paragraph(new Run(value)) { Margin = new Thickness(0) });
    }
    public void Clear() => Text = "";
    private void SetParagraph(Paragraph paragraph)
    {
        Document = new FlowDocument(paragraph) { PagePadding = new Thickness(0) };
    }
    public void Show(string heading, DisplayCell cell, string suffix)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        paragraph.Inlines.Add(new Run(heading) { FontSize = 11, Foreground = Brushes.DimGray });
        paragraph.Inlines.Add(new LineBreak());
        foreach (var run in InlineHighlight.Runs(cell)) paragraph.Inlines.Add(run);
        if (suffix.Length > 0) { paragraph.Inlines.Add(new LineBreak()); paragraph.Inlines.Add(new Run(suffix)); }
        SetParagraph(paragraph);
    }

    internal void ShowTargetIcon(BitmapSource bitmap, string path, RoutedEventHandler open)
    {
        if (Document.Blocks.LastBlock is not Paragraph paragraph) return;
        var button = new Button
        {
            Content = new NativePixelImage { Source = bitmap },
            Style = (Style)FindResource("CellIconLink"),
            ToolTip = $"在本地图片中打开 {path}"
        };
        button.Click += open;
        var viewport = new ScrollViewer
        {
            Content = button,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 150,
            Margin = new Thickness(0, 4, 0, 0)
        };
        if (ActualWidth > 0)
            viewport.MaxWidth = Math.Max(1, ActualWidth - Padding.Left - Padding.Right - SystemParameters.VerticalScrollBarWidth - 4);
        paragraph.Inlines.Add(new LineBreak());
        paragraph.Inlines.Add(new InlineUIContainer(viewport));
    }
}
