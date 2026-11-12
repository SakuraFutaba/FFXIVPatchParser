using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Microsoft.Win32;

namespace FFXIVPatchParser.Desktop;

public partial class ModelPreview
{
    private static string GodbertSetting => Path.Combine(Workspace.Temp, "ui", "godbert-path.txt");
    private readonly DispatcherTimer playbackTimer = new() { Interval = TimeSpan.FromMilliseconds(1000.0 / 30) };
    private readonly Stopwatch playbackTime = new();
    private CancellationTokenSource? playbackBake;
    private GameAnimationMetadata? loadedAction;
    private ModelAnimationClip? playbackClip;
    private MeshGeometry3D[]? animatedGeometry;
    private int lastFrame = -1;

    private void PlaybackSlotChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PlaybackStart == null) return;
        StopPlayback();
        PlaybackStatus.Text = "";
    }

    private void UpdatePlaybackControls()
    {
        PlaybackStart.IsEnabled = scene != null && loadedAction != null && PlaybackSlot.SelectedItem is GameAnimationSlot && playbackBake == null;
        PlaybackStop.IsEnabled = playbackBake != null || playbackClip != null;
    }

    private static string? SavedGodbertPath() => File.Exists(GodbertSetting) ? File.ReadAllText(GodbertSetting).Trim() : null;

    private void ChooseGodbert(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new OpenFolderDialog { Title = "选择已安装 Godbert 的目录" };
            if (SavedGodbertPath() is { } remembered && Directory.Exists(remembered)) dialog.InitialDirectory = remembered;
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            if (!File.Exists(Path.Combine(dialog.FolderName, "hkAnimationInterop.dll")))
            {
                PlaybackStatus.Text = "该目录中没有 hkAnimationInterop.dll。";
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(GodbertSetting)!);
            File.WriteAllText(GodbertSetting, dialog.FolderName);
            StopPlayback();
            PlaybackStatus.Text = "Godbert 目录已保存。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PlaybackStatus.Text = "无法保存 Godbert 目录 · " + ex.Message;
        }
    }

    private async void PlayAnimation(object sender, RoutedEventArgs e)
    {
        if (source is not { } game || scene is not { } preview || loadedAction is not { } action ||
            PlaybackSlot.SelectedItem is not GameAnimationSlot slot) return;
        StopPlayback();
        using var pending = new CancellationTokenSource();
        playbackBake = pending;
        UpdatePlaybackControls();
        try
        {
            var directory = SavedGodbertPath();
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                PlaybackStatus.Text = "先用「Godbert…」选择已安装的目录。";
                return;
            }
            if (preview.Vertices > 100_000)
            {
                PlaybackStatus.Text = "模型超过实时蒙皮的 10 万顶点预览上限。";
                return;
            }
            PlaybackStatus.Text = "正在用 Godbert 采样动作…";
            var clip = await game.BakeModelAnimationAsync(preview.Path, action.Path, slot.Index, directory, pending.Token);
            pending.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(scene, preview) || !ReferenceEquals(loadedAction, action)) return;
            // Check the skeleton binding before switching the viewport to mutable meshes.
            var first = await Task.Run(() => LocalGameSource.SkinModelFrame(preview.SourceModel, clip, 0), pending.Token);
            pending.Token.ThrowIfCancellationRequested();
            animatedGeometry = preview.Meshes.Select(mesh =>
            {
                var geometry = mesh.Geometry.CloneCurrentValue();
                geometry.Normals = new Vector3DCollection();
                return geometry;
            }).ToArray();
            playbackClip = clip;
            ApplyPose(preview, first);
            RenderScene();
            lastFrame = 0;
            playbackTime.Restart();
            playbackTimer.Start();
            PlaybackStatus.Text = $"{slot.Name} · {clip.Duration:0.00} 秒 · 循环播放";
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!pending.IsCancellationRequested)
            {
                StopPlayback();
                PlaybackStatus.Text = "无法播放动作 · " + ex.Message;
            }
        }
        finally
        {
            if (playbackBake == pending) playbackBake = null;
            UpdatePlaybackControls();
        }
    }

    private void PlaybackTick(object? sender, EventArgs e)
    {
        if (scene is not { } preview || playbackClip is not { } clip) return;
        var elapsed = playbackTime.Elapsed.TotalSeconds % clip.Duration;
        var frame = Math.Clamp((int)(elapsed * clip.FramesPerSecond), 0, clip.Frames.Length - 1);
        if (frame == lastFrame) return;
        try
        {
            ApplyPose(preview, LocalGameSource.SkinModelFrame(preview.SourceModel, clip, frame));
            lastFrame = frame;
        }
        catch (Exception ex)
        {
            StopPlayback();
            PlaybackStatus.Text = "蒙皮失败 · " + ex.Message;
        }
    }

    private void ApplyPose(PreviewScene preview, System.Numerics.Vector3[][] meshPositions)
    {
        if (animatedGeometry is not { } geometries) return;
        for (var mesh = 0; mesh < geometries.Length; mesh++)
        {
            var positions = new Point3DCollection(meshPositions[mesh].Length);
            foreach (var vertex in meshPositions[mesh])
                positions.Add(new Point3D() + (new Point3D(vertex.X, vertex.Y, vertex.Z) - preview.Center) * preview.Scale);
            positions.Freeze();
            geometries[mesh].Positions = positions;
        }
    }

    private void StopAnimation(object sender, RoutedEventArgs e)
    {
        StopPlayback();
        PlaybackStatus.Text = "已停止";
    }

    private void StopPlayback(bool restoreScene = true)
    {
        playbackBake?.Cancel();
        playbackBake = null;
        playbackTimer.Stop();
        playbackTime.Reset();
        playbackClip = null;
        var wasAnimated = animatedGeometry != null;
        animatedGeometry = null;
        if (restoreScene && wasAnimated) RenderScene();
        if (PlaybackStart != null) UpdatePlaybackControls();
    }
}
