using System.Windows;
using System.Windows.Controls.Primitives;

namespace FFXIVPatchParser.Desktop;

internal static class CellImageLayout
{
    public static void Anchor(Popup popup, FrameworkElement cell)
    {
        popup.PlacementTarget = cell;
        popup.Placement = PlacementMode.Custom;
        popup.CustomPopupPlacementCallback = (size, target, _) =>
            Place(size, target, popup.PlacementTarget as FrameworkElement);
    }

    private static CustomPopupPlacement[] Place(Size size, Size target, FrameworkElement? cell)
    {
        const double gap = 4;
        const double margin = 8;
        if (cell == null || Window.GetWindow(cell)?.Content is not FrameworkElement owner)
            return [new(new Point(0, target.Height + gap), PopupPrimaryAxis.None)];

        var origin = cell.TranslatePoint(new Point(), owner);
        var left = Math.Clamp(origin.X, margin, Math.Max(margin, owner.ActualWidth - size.Width - margin));
        var bottom = owner.ActualHeight - size.Height - margin;
        var below = origin.Y + target.Height + gap;
        var above = origin.Y - size.Height - gap;
        var top = below <= bottom ? below : above >= margin ? above : Math.Max(margin, bottom);
        return [new(new Point(left - origin.X, top - origin.Y), PopupPrimaryAxis.None)];
    }
}
