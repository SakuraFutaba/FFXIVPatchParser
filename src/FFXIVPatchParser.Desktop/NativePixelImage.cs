using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FFXIVPatchParser.Desktop;

public sealed class NativePixelImage : Image
{
    public NativePixelImage()
    {
        Stretch = Stretch.Fill;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Source is not BitmapSource bitmap) return base.MeasureOverride(availableSize);
        var dpi = VisualTreeHelper.GetDpi(this);
        return new Size(bitmap.PixelWidth / dpi.DpiScaleX, bitmap.PixelHeight / dpi.DpiScaleY);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidateMeasure();
    }
}
