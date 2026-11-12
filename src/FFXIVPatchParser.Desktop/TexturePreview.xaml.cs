using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace FFXIVPatchParser.Desktop;

public partial class TexturePreview : UserControl
{
    private const double MaxZoom = 32;
    private BitmapSource? bitmap;
    private string? virtualPath;
    private string? details;
    private string? exportName;
    private bool fit;
    private double zoom = 1;
    private Point origin;
    private Point? dragPosition;

    public TexturePreview() => InitializeComponent();

    public void SetImage(BitmapSource? bitmap, string? virtualPath = null, string? details = null, string? exportName = null)
    {
        if (Viewport.IsMouseCaptured) Viewport.ReleaseMouseCapture();
        this.bitmap = bitmap;
        this.virtualPath = virtualPath;
        this.details = details;
        this.exportName = exportName;
        PreviewImage.Source = bitmap;
        PreviewImage.Width = bitmap?.PixelWidth ?? 0;
        PreviewImage.Height = bitmap?.PixelHeight ?? 0;
        Actions.IsEnabled = bitmap is not null;
        CopyPathButton.IsEnabled = !string.IsNullOrWhiteSpace(virtualPath);
        Viewport.Cursor = bitmap is null ? Cursors.Arrow : Cursors.SizeAll;
        SetStatus(null);
        ResetView(fitToWindow: false);
    }

    private void FitImage(object sender, RoutedEventArgs e) => ResetView(fitToWindow: true);

    private void ShowActualSize(object sender, RoutedEventArgs e) => ResetView(fitToWindow: false);

    private double FitZoom(DpiScale dpi)
    {
        if (bitmap is null || Viewport.ActualWidth <= 0 || Viewport.ActualHeight <= 0) return 1;
        return Math.Min(MaxZoom, Math.Min(Viewport.ActualWidth * dpi.DpiScaleX / bitmap.PixelWidth,
            Viewport.ActualHeight * dpi.DpiScaleY / bitmap.PixelHeight));
    }

    private void ResetView(bool fitToWindow)
    {
        fit = fitToWindow;
        var dpi = VisualTreeHelper.GetDpi(Viewport);
        zoom = fit ? FitZoom(dpi) : 1;
        origin = new Point(
            (Viewport.ActualWidth - PreviewImage.Width * zoom / dpi.DpiScaleX) / 2,
            (Viewport.ActualHeight - PreviewImage.Height * zoom / dpi.DpiScaleY) / 2);
        UpdateView(dpi);
    }

    private void ResizeViewport(object sender, SizeChangedEventArgs e)
    {
        if (bitmap is null) return;
        if (fit)
        {
            ResetView(fitToWindow: true);
            return;
        }
        origin.Offset((e.NewSize.Width - e.PreviousSize.Width) / 2,
            (e.NewSize.Height - e.PreviousSize.Height) / 2);
        UpdateView(VisualTreeHelper.GetDpi(Viewport));
    }

    private void ZoomWithWheel(object sender, MouseWheelEventArgs e)
    {
        if (bitmap is null || e.Delta == 0) return;
        var dpi = VisualTreeHelper.GetDpi(Viewport);
        var minZoom = Math.Min(0.05, FitZoom(dpi) / 2);
        var nextZoom = Math.Clamp(zoom * Math.Pow(1.25, e.Delta / 120.0), minZoom, MaxZoom);
        var pointer = e.GetPosition(Viewport);
        origin = pointer + (origin - pointer) * (nextZoom / zoom);
        fit = false;
        zoom = nextZoom;
        UpdateView(dpi);
        e.Handled = true;
    }

    private void StartPan(object sender, MouseButtonEventArgs e)
    {
        if (bitmap is null || !Viewport.CaptureMouse()) return;
        dragPosition = e.GetPosition(Viewport);
        e.Handled = true;
    }

    private void PanImage(object sender, MouseEventArgs e)
    {
        if (dragPosition is not { } previous || e.LeftButton != MouseButtonState.Pressed) return;
        var current = e.GetPosition(Viewport);
        if (current == previous) return;
        dragPosition = current;
        fit = false;
        origin += current - previous;
        UpdateView(VisualTreeHelper.GetDpi(Viewport));
    }

    private void EndPan(object sender, MouseButtonEventArgs e)
    {
        if (!Viewport.IsMouseCaptured) return;
        Viewport.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void StopPan(object sender, MouseEventArgs e) => dragPosition = null;

    private void UpdateView(DpiScale dpi)
    {
        if (bitmap is null)
        {
            ImageInfo.Text = "选择一张图片以预览";
            return;
        }

        var scaleX = zoom / dpi.DpiScaleX;
        var scaleY = zoom / dpi.DpiScaleY;
        var width = bitmap.PixelWidth * scaleX;
        var height = bitmap.PixelHeight * scaleY;
        var visibleWidth = Math.Min(20, Math.Min(width, Viewport.ActualWidth));
        var visibleHeight = Math.Min(20, Math.Min(height, Viewport.ActualHeight));
        origin.X = Math.Clamp(origin.X, visibleWidth - width, Viewport.ActualWidth - visibleWidth);
        origin.Y = Math.Clamp(origin.Y, visibleHeight - height, Viewport.ActualHeight - visibleHeight);
        var offsetX = Math.Round(origin.X * dpi.DpiScaleX) / dpi.DpiScaleX;
        var offsetY = Math.Round(origin.Y * dpi.DpiScaleY) / dpi.DpiScaleY;
        ImageTransform.Matrix = new Matrix(scaleX, 0, 0, scaleY, offsetX, offsetY);
        RenderOptions.SetBitmapScalingMode(PreviewImage,
            zoom >= 1 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);

        var dimensions = $"{bitmap.PixelWidth:N0} × {bitmap.PixelHeight:N0} 像素 · {zoom * 100:0.##}%";
        ImageInfo.Text = string.IsNullOrWhiteSpace(details) ? dimensions : $"{dimensions} · {details}";
    }

    private void ChangeBackground(object sender, SelectionChangedEventArgs e)
    {
        if (Viewport is null) return;
        Viewport.Background = BackgroundChoice.SelectedIndex switch
        {
            1 => new SolidColorBrush(Color.FromRgb(28, 35, 47)),
            2 => Brushes.White,
            _ => (Brush)FindResource("Checkerboard")
        };
    }

    private void CopyPath(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(virtualPath)) return;
        try
        {
            Clipboard.SetText(virtualPath);
            SetStatus("已复制资源路径", virtualPath);
        }
        catch (ExternalException ex)
        {
            SetStatus("无法复制资源路径", ex.Message, error: true);
        }
    }

    private async void SavePng(object sender, RoutedEventArgs e)
    {
        if (bitmap is not { } image) return;
        SavePngButton.IsEnabled = false;
        SetStatus(null);
        try
        {
            var exportDirectory = Path.Combine(Workspace.Temp, "exports");
            var dialog = new SaveFileDialog
            {
                Title = "保存图片为 PNG",
                Filter = "PNG 图片 (*.png)|*.png",
                DefaultExt = ".png",
                AddExtension = true,
                FileName = SuggestedFileName(),
                InitialDirectory = exportDirectory
            };
            try
            {
                Directory.CreateDirectory(exportDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                dialog.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            }

            var owner = Window.GetWindow(this);
            if ((owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner)) != true) return;

            var pngSource = !image.IsFrozen && image.CanFreeze ? (BitmapSource)image.GetAsFrozen() : image;
            var path = dialog.FileName;
            if (pngSource.IsFrozen)
                await Task.Run(() => WritePng(pngSource, path));
            else
                WritePng(pngSource, path);

            if (ReferenceEquals(image, bitmap)) SetStatus($"已保存 {Path.GetFileName(path)}", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or NotSupportedException or ExternalException)
        {
            if (ReferenceEquals(image, bitmap)) SetStatus("无法保存 PNG", ex.Message, error: true);
        }
        finally
        {
            SavePngButton.IsEnabled = true;
        }
    }

    private string SuggestedFileName()
    {
        var path = exportName ?? virtualPath ?? "texture";
        var separator = path.LastIndexOfAny(['/', '\\', '#']);
        var fileName = Path.GetFileNameWithoutExtension(path[(separator + 1)..]);
        var invalid = Path.GetInvalidFileNameChars();
        fileName = string.Concat(fileName.Select(character => invalid.Contains(character) ? '_' : character)).Trim();
        return (string.IsNullOrEmpty(fileName) ? "texture" : fileName) + ".png";
    }

    private static void WritePng(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        var temporaryPath = Path.Combine(Path.GetDirectoryName(path)!, Path.GetRandomFileName() + ".partial");
        var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write);
        try
        {
            using (file) encoder.Save(file);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private void SetStatus(string? text, string? description = null, bool error = false)
    {
        ActionStatus.Text = text ?? "";
        ActionStatus.ToolTip = description;
        ActionStatus.Foreground = error ? Brushes.Firebrick : (Brush)FindResource("Muted");
        ActionStatus.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
