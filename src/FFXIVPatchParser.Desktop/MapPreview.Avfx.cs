using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FFXIVPatchParser.Desktop;

public partial class MapPreview
{
    private void OpenBattleEffect(object sender, RoutedEventArgs e)
    {
        if (source is not { } game || BattleResources.SelectedItem is not GameBattleResource resource) return;
        var paths = resource.References.Where(path => path.EndsWith(".avfx", StringComparison.OrdinalIgnoreCase));
        if (resource.Path.EndsWith(".avfx", StringComparison.OrdinalIgnoreCase) || resource.Path.EndsWith(".tmb", StringComparison.OrdinalIgnoreCase))
            paths = paths.Prepend(resource.Path);
        var choices = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (choices.Length == 0)
        {
            MainWindow.Notify(this, "所选资源没有直接引用的 AVFX。");
            return;
        }
        new AvfxPreviewWindow(game, choices) { Owner = Window.GetWindow(this) }.Show();
    }
}

internal sealed class AvfxPreviewWindow : Window
{
    private readonly LocalGameSource source;
    private readonly AvfxSurface surface = new();
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Slider seek = new() { Minimum = 0, IsMoveToPointEnabled = true, IsEnabled = false };
    private readonly ToggleButton play = new CheckBox { Content = "播放", IsEnabled = false, Margin = new Thickness(0, 0, 12, 0) };
    private readonly Stopwatch clock = new();
    private CancellationTokenSource? loadRequest;
    private double offset;
    private bool advancing;

    public AvfxPreviewWindow(LocalGameSource source, IReadOnlyList<string> paths)
    {
        this.source = source;
        Title = "AVFX 粒子预览";
        Width = 740;
        Height = 510;
        MinWidth = 360;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel { Margin = new Thickness(12) };
        var choose = new ComboBox { ItemsSource = paths, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(choose, Dock.Top);
        panel.Children.Add(choose);
        var controls = new DockPanel();
        DockPanel.SetDock(controls, Dock.Bottom);
        DockPanel.SetDock(status, Dock.Bottom);
        panel.Children.Add(controls);
        panel.Children.Add(status);
        controls.Children.Add(play);
        controls.Children.Add(seek);
        panel.Children.Add(surface);
        Content = panel;

        choose.SelectionChanged += async (_, _) =>
        {
            if (choose.SelectedItem is string path) await LoadEffect(path);
        };
        play.Checked += (_, _) => { clock.Restart(); CompositionTarget.Rendering += Advance; };
        play.Unchecked += (_, _) => { offset = seek.Value; CompositionTarget.Rendering -= Advance; };
        seek.ValueChanged += (_, _) =>
        {
            if (!advancing) { offset = seek.Value; clock.Restart(); }
            surface.Frame = seek.Value;
            surface.InvalidateVisual();
        };
        Closed += (_, _) =>
        {
            play.IsChecked = false;
            loadRequest?.Cancel();
        };
        choose.SelectedIndex = 0;
    }

    private async Task LoadEffect(string path)
    {
        loadRequest?.Cancel();
        using var request = new CancellationTokenSource();
        loadRequest = request;
        play.IsChecked = false;
        play.IsEnabled = seek.IsEnabled = false;
        surface.SetEffect(null);
        status.Text = "正在读取调度器、发射器和贴图…";
        try
        {
            var timeline = path.EndsWith(".tmb", StringComparison.OrdinalIgnoreCase);
            var result = await Task.Run(() => timeline ? source.ReadTmbAvfxPreview(path, request.Token)
                : source.ReadAvfxPreview(path, request.Token), request.Token);
            if (request.IsCancellationRequested) return;
            surface.SetEffect(result);
            seek.Maximum = result.FrameCount;
            seek.Value = offset = 0;
            play.IsEnabled = seek.IsEnabled = result.Tracks.Count > 0;
            play.IsChecked = play.IsEnabled;
            status.Text = result.Tracks.Count == 0 ? "没有支持的点发射器纹理粒子。"
                : $"已载入 {result.Tracks.Count} 条纹理粒子轨道，跳过 {result.SkippedTracks} 条不支持的引用。使用近似混合和 60 帧／秒的预览时钟。"
                + (timeline ? "按 TMB 触发帧合成，采用同一局部坐标原点。" : "");
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!request.IsCancellationRequested) status.Text = "无法预览特效 · " + ex.Message;
        }
        finally { if (loadRequest == request) loadRequest = null; }
    }

    private void Advance(object? sender, EventArgs args)
    {
        var frame = (int)(offset + clock.Elapsed.TotalSeconds * 60) % (int)seek.Maximum;
        if (frame == seek.Value) return;
        advancing = true;
        seek.Value = frame;
        advancing = false;
    }

    private sealed class AvfxSurface : FrameworkElement
    {
        private static readonly Brush Background = new SolidColorBrush(Color.FromRgb(24, 31, 43));
        private WriteableBitmap? bitmap;
        private byte[] pixels = [];
        private AvfxPreview? effect;
        public double Frame { get; set; }

        public void SetEffect(AvfxPreview? value)
        {
            effect = value;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawing)
        {
            var area = new Rect(RenderSize);
            drawing.DrawRoundedRectangle(Background, null, area, 6, 6);
            if (effect is null || area.Width < 1 || area.Height < 1) return;
            var ratio = Math.Min(1, 480 / Math.Max(area.Width, area.Height));
            var width = Math.Max(1, (int)(area.Width * ratio));
            var height = Math.Max(1, (int)(area.Height * ratio));
            if (bitmap?.PixelWidth != width || bitmap.PixelHeight != height)
            {
                bitmap = new(width, height, 96, 96, PixelFormats.Bgr32, null);
                pixels = new byte[width * height * 4];
            }
            for (var pixel = 0; pixel < pixels.Length; pixel += 4)
            {
                pixels[pixel] = 43;
                pixels[pixel + 1] = 31;
                pixels[pixel + 2] = 24;
            }
            var scale = Math.Min(width, height) / 12.0;
            var pixelBudget = 1_200_000;
            foreach (var track in effect.Tracks)
            {
                var last = Math.Min((int)Frame, track.Stop - 1);
                if (last < track.Start) continue;
                var latest = track.Start + (last - track.Start) / track.Interval * track.Interval;
                for (var birth = latest; birth >= track.Start && latest - birth < track.Interval * 24 && pixelBudget > 0; birth -= track.Interval)
                {
                    var age = Frame - birth;
                    if (age >= track.Lifetime) break;
                    var position = track.EmitterPosition.Sample(birth - track.Start) + track.Position.Sample(age);
                    var size = track.Scale.Sample(age);
                    var opacity = Math.Clamp(track.Alpha.Sample(age), 0, 1);
                    if (opacity == 0) continue;
                    var cx = width * .5 + Math.Clamp(position.X, -100, 100) * scale;
                    var cy = height * .5 - Math.Clamp(position.Y, -100, 100) * scale;
                    pixelBudget -= Composite(track, age, opacity, cx, cy, size.X * scale, size.Y * scale, width, height);
                }
            }
            bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
            drawing.PushClip(new RectangleGeometry(area, 6, 6));
            drawing.DrawImage(bitmap, area);
            drawing.Pop();
        }

        private int Composite(AvfxTrack track, double age, float alpha, double cx, double cy,
            double spriteWidth, double spriteHeight, int width, int height)
        {
            var w = Math.Clamp(spriteWidth, -2000, 2000);
            var h = Math.Clamp(spriteHeight, -2000, 2000);
            if (Math.Abs(w) < .5 || Math.Abs(h) < .5) return 0;
            var (sin, cos) = Math.SinCos(track.Angle.Sample(age));
            var extentX = (Math.Abs(w * cos) + Math.Abs(h * sin)) * .5;
            var extentY = (Math.Abs(w * sin) + Math.Abs(h * cos)) * .5;
            var left = Math.Max(0, (int)Math.Floor(cx - extentX));
            var right = Math.Min(width, (int)Math.Ceiling(cx + extentX));
            var top = Math.Max(0, (int)Math.Floor(cy - extentY));
            var bottom = Math.Min(height, (int)Math.Ceiling(cy + extentY));
            var color = System.Numerics.Vector3.Clamp(track.Color.SampleVector(age), new(0), new(1));
            var texture = track.Texture;
            for (var y = top; y < bottom; y++)
            for (var x = left; x < right; x++)
            {
                var dx = x + .5 - cx;
                var dy = y + .5 - cy;
                var u = (dx * cos - dy * sin) / w + .5;
                var v = (dx * sin + dy * cos) / h + .5;
                if (u < 0 || u >= 1 || v < 0 || v >= 1) continue;
                var source = ((int)(v * texture.Height) * texture.Width + (int)(u * texture.Width)) * 4;
                var mix = texture.Bgra32[source + 3] / 255f * alpha;
                var target = (y * width + x) * 4;
                pixels[target] = Blend(pixels[target], texture.Bgra32[source] * color.Z, mix, track.DrawMode);
                pixels[target + 1] = Blend(pixels[target + 1], texture.Bgra32[source + 1] * color.Y, mix, track.DrawMode);
                pixels[target + 2] = Blend(pixels[target + 2], texture.Bgra32[source + 2] * color.X, mix, track.DrawMode);
            }
            return Math.Max(0, right - left) * Math.Max(0, bottom - top);
        }

        private static byte Blend(byte background, float particle, float opacity, int mode) => (byte)Math.Clamp(mode switch
        {
            1 => background * (1 - opacity + particle * opacity / 255),
            2 => background + particle * opacity,
            4 => background + (255 - background) * particle * opacity / 255,
            _ => float.Lerp(background, particle, opacity)
        }, 0, 255);
    }
}
