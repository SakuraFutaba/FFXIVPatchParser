using System.Diagnostics;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace FFXIVPatchParser.Desktop;

public partial class ModelPreview
{
    private readonly Stopwatch itemEffectClock = new();
    private CancellationTokenSource? itemEffectRequest;
    private ItemEffectLayer[] itemEffectLayers = [];
    private int itemEffectFrameCount;
    private int lastItemEffectFrame = -1;

    private sealed record ItemEffectLayer(AvfxTrack Track, GeometryModel3D Model, Brush Brush, MatrixTransform3D Transform);

    private async void BeginItemEffects(LocalGameSource game, GameCharacterPartOption option)
    {
        ResetItemEffects();
        if (scene is not { } shown) return;
        using var request = new CancellationTokenSource();
        itemEffectRequest = request;
        var token = request.Token;
        try
        {
            var effect = await Task.Run(() => game.ReadItemEffect(option, token), token);
            token.ThrowIfCancellationRequested();
            if (effect == null || !ReferenceEquals(scene, shown)) return;
            var tracks = effect.Preview.Tracks.Take(48).ToArray();
            itemEffectLayers = tracks.Select(CreateItemEffectLayer).ToArray();
            var group = new Model3DGroup();
            foreach (var layer in itemEffectLayers) group.Children.Add(layer.Model);
            ItemEffectVisual.Content = group;
            itemEffectFrameCount = effect.Preview.FrameCount;
            ModelInfo.ToolTip = $"{ModelInfo.ToolTip}\n{effect.Path}\n光效预览 · {tracks.Length} 条轨道 · {effect.Preview.SkippedTracks + effect.Preview.Tracks.Count - tracks.Length} 条未显示";
            UpdateItemEffects(this, EventArgs.Empty);
            SetItemEffectVisibility();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested) ModelInfo.ToolTip = $"{ModelInfo.ToolTip}\n无法预览装备光效 · {ex.Message}";
        }
        finally { if (ReferenceEquals(itemEffectRequest, request)) itemEffectRequest = null; }
    }

    private static ItemEffectLayer CreateItemEffectLayer(AvfxTrack track)
    {
        var geometry = new MeshGeometry3D();
        if (track.Model is { } model)
        {
            geometry.Positions = new(model.Positions.Select(p => new Point3D(p.X, p.Y, p.Z)));
            geometry.TextureCoordinates = new(model.TextureCoordinates.Select(uv => new Point(uv.X, uv.Y)));
            geometry.TriangleIndices = new(model.TriangleIndices);
        }
        else
        {
            geometry.Positions = new([new(-.5, .5, 0), new(.5, .5, 0), new(.5, -.5, 0), new(-.5, -.5, 0)]);
            geometry.TextureCoordinates = new([new(0, 0), new(1, 0), new(1, 1), new(0, 1)]);
            geometry.TriangleIndices = new([0, 1, 2, 0, 2, 3]);
        }
        geometry.Freeze();
        var pixels = track.Texture.Bgra32.ToArray();
        var tint = Vector3.Clamp(track.Color.SampleVector(20), Vector3.Zero, Vector3.One);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var intensity = Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2]));
            if (track.ColorToAlpha || track.DrawMode is 2 or 4)
                pixels[i + 3] = (byte)(pixels[i + 3] * intensity / 255);
            pixels[i] = (byte)(pixels[i] * tint.Z);
            pixels[i + 1] = (byte)(pixels[i + 1] * tint.Y);
            pixels[i + 2] = (byte)(pixels[i + 2] * tint.X);
        }
        var bitmap = CreateBitmap(track.Texture with { Bgra32 = pixels }, PixelFormats.Bgra32);
        var brush = new ImageBrush(bitmap) { Opacity = 0, Stretch = Stretch.Fill };
        var surface = new EmissiveMaterial(brush);
        var transform = new MatrixTransform3D();
        return new(track, new GeometryModel3D(geometry, surface) { BackMaterial = surface, Transform = transform }, brush, transform);
    }

    private void SetItemEffectVisibility()
    {
        CompositionTarget.Rendering -= UpdateItemEffects;
        itemEffectClock.Stop();
        if (!IsVisible || itemEffectLayers.Length == 0) return;
        itemEffectClock.Start();
        CompositionTarget.Rendering += UpdateItemEffects;
    }

    private void ResetItemEffects()
    {
        itemEffectRequest?.Cancel();
        CompositionTarget.Rendering -= UpdateItemEffects;
        itemEffectClock.Reset();
        lastItemEffectFrame = -1;
        itemEffectLayers = [];
        if (ItemEffectVisual != null) ItemEffectVisual.Content = null;
    }

    private void UpdateItemEffects(object? sender, EventArgs e)
    {
        if (scene is not { } preview || itemEffectLayers.Length == 0) return;
        var frame = (int)(itemEffectClock.Elapsed.TotalSeconds * 30) * 2 % itemEffectFrameCount;
        if (frame == lastItemEffectFrame) return;
        lastItemEffectFrame = frame;
        var normalization = Matrix4x4.CreateTranslation(-(float)preview.Center.X, -(float)preview.Center.Y, -(float)preview.Center.Z)
            * Matrix4x4.CreateScale((float)preview.Scale);
        foreach (var layer in itemEffectLayers)
        {
            var track = layer.Track;
            var latest = Math.Min(frame, track.Stop - 1);
            if (latest < track.Start) { layer.Brush.Opacity = 0; continue; }
            var birth = track.Start + (track.SingleBirth ? 0 : (latest - track.Start) / track.Interval * track.Interval);
            var age = frame - birth;
            if (age >= track.Lifetime) { layer.Brush.Opacity = 0; continue; }
            var opacity = track.Alpha.Sample(age);
            var scale = track.EmitterScale.Sample(birth - track.Start) * track.Scale.Sample(age);
            var translation = track.EmitterPosition.Sample(birth - track.Start) + track.Position.Sample(age);
            var rotation = track.Rotation.Sample(age);
            var transform = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateRotationX(rotation.X)
                * Matrix4x4.CreateRotationY(rotation.Y) * Matrix4x4.CreateRotationZ(rotation.Z);
            if (track.Model == null)
            {
                var direction = ViewCamera.LookDirection;
                var up = ViewCamera.UpDirection;
                var facing = new Vector3((float)-direction.X, (float)-direction.Y, (float)-direction.Z);
                transform *= Matrix4x4.CreateWorld(Vector3.Zero, -Vector3.Normalize(facing), new((float)up.X, (float)up.Y, (float)up.Z));
                transform *= Matrix4x4.CreateTranslation(Vector3.Transform(translation, track.Anchor));
            }
            else transform *= Matrix4x4.CreateTranslation(translation) * track.Anchor;
            var m = transform * normalization;
            layer.Transform.Matrix = new(m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
                m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44);
            layer.Brush.Opacity = Math.Clamp(opacity, 0, 1);
        }
    }
}
