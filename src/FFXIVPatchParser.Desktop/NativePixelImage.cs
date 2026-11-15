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
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Source is not BitmapSource bitmap) return base.MeasureOverride(availableSize);
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = bitmap.PixelWidth / dpi.DpiScaleX;
        var height = bitmap.PixelHeight / dpi.DpiScaleY;
        var scale = Math.Min(1, Math.Min(availableSize.Width / width, availableSize.Height / height));
        return new Size(width * scale, height * scale);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidateMeasure();
    }
}
