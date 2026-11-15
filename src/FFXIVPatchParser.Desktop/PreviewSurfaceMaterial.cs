using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace FFXIVPatchParser.Desktop;

internal static class PreviewSurfaceMaterial
{
    public static byte CutoffByte(GameModelMaterial material) => material.AlphaMode == GameMaterialAlphaMode.Opaque
        ? (byte)0 : (byte)Math.Ceiling(Math.Clamp(material.AlphaCutoff, 0, 1) * 255);

    public static DiffuseMaterial Create(GameTexture texture, GameMaterialAlphaMode alpha, byte cutoff)
    {
        var pixels = texture.Bgra32;
        var format = PixelFormats.Bgr32;
        if (alpha != GameMaterialAlphaMode.Opaque)
        {
            format = PixelFormats.Bgra32;
            if (alpha == GameMaterialAlphaMode.Mask || cutoff != 0)
            {
                // WPF 没有片元裁剪，在贴图副本上应用阈值。原始纹理仍可查看和导出。
                pixels = (byte[])pixels.Clone();
                for (var offset = 0; offset < pixels.Length; offset += 4)
                {
                    var opacity = pixels[offset + 3];
                    if (opacity == 0 || opacity < cutoff) pixels.AsSpan(offset, 4).Clear();
                    else if (alpha == GameMaterialAlphaMode.Mask) pixels[offset + 3] = 255;
                }
            }
        }
        var image = BitmapSource.Create(texture.Width, texture.Height, 96, 96, format, null,
            pixels, checked(texture.Width * 4));
        var brush = new ImageBrush(image)
        {
            Stretch = Stretch.Fill,
            TileMode = TileMode.Tile,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, 1, 1)
        };
        var material = new DiffuseMaterial(brush);
        material.Freeze();
        return material;
    }
}
