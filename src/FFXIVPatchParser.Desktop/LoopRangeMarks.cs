using System.Windows;
using System.Windows.Media;

namespace FFXIVPatchParser.Desktop;

public sealed class LoopRangeMarks : FrameworkElement
{
    private (double Start, double End)? fractions;

    internal void SetRange((double Start, double End)? loop, double duration)
    {
        (double, double)? next = loop is { } range && duration > 0
            ? (Math.Clamp(range.Start / duration, 0, 1), Math.Clamp(range.End / duration, 0, 1))
            : null;
        if (fractions == next) return;
        fractions = next;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (fractions is not { } loop || ActualWidth <= 0) return;
        // 滑块的 10 DIP 手柄中心在轨道两端各留 5 DIP。
        var left = ActualWidth * loop.Start;
        var right = ActualWidth * loop.End;
        var middle = ActualHeight / 2;
        var fill = new SolidColorBrush(Color.FromArgb(85, 55, 108, 195));
        var mark = new Pen((Brush)FindResource("ShellAccent"), 1.6);
        drawingContext.DrawRoundedRectangle(fill, null, new Rect(left, middle - 2, right - left, 4), 2, 2);
        drawingContext.DrawLine(mark, new Point(left, middle - 6), new Point(left, middle + 6));
        drawingContext.DrawLine(mark, new Point(right, middle - 6), new Point(right, middle + 6));
    }
}
