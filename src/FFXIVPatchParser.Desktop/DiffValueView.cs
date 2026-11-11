using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace FFXIVPatchParser.Desktop;

public sealed class DiffValueView : RichTextBox
{
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
}
