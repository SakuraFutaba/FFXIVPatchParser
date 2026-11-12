using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace FFXIVPatchParser.Desktop;

public sealed record DisplayCell(string Text, SolidColorBrush Background, SolidColorBrush Accent, IReadOnlyList<TextSpan> Spans, bool IsReference = false);

public static class InlineHighlight
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.RegisterAttached(
        "Value", typeof(DisplayCell), typeof(InlineHighlight), new PropertyMetadata(null, Changed));
    public static void SetValue(DependencyObject target, DisplayCell? value) => target.SetValue(ValueProperty, value);
    public static DisplayCell? GetValue(DependencyObject target) => (DisplayCell?)target.GetValue(ValueProperty);
    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not TextBlock block) return;
        block.Inlines.Clear();
        if (e.NewValue is not DisplayCell cell) return;
        foreach (var run in Runs(cell)) block.Inlines.Add(run);
    }
    public static IEnumerable<Run> Runs(DisplayCell cell)
    {
        foreach (var part in cell.Spans)
        {
            var run = new Run(part.Text);
            if (cell.IsReference) { run.Foreground = Brushes.RoyalBlue; run.FontWeight = FontWeights.SemiBold; }
            if (part.Changed) { run.Background = cell.Accent; run.FontWeight = FontWeights.SemiBold; }
            yield return run;
        }
    }
}
