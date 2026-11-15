using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Media3D;
using System.Windows.Media;

namespace FFXIVPatchParser.Desktop;

public partial class ModelPreview
{
    private readonly Stopwatch playbackTime = new();
    private CancellationTokenSource? playbackBake;
    private GameAnimationMetadata? loadedAction;
    private ModelAnimationClip? playbackClip;
    private MeshGeometry3D[]? animatedGeometry;
    private int lastFrame = -1;
    private Point3DCollection[][]? poseCache;
    private bool framePending;
    private LocalGameSource.ModelSkinningBinding? skinningBinding;
    private readonly Dictionary<int, Point3DCollection[]> recentPoses = new();
    private readonly Queue<int> recentPoseOrder = new();
    private int recentPoseLimit;
    private double playbackOffset;
    private bool updatingPosition;
    private double lastTimeLabelPosition = double.NegativeInfinity;
    private int playbackGeneration;
    private bool settingPlaybackSlots;
    private double? renderClockOrigin;

    private async void PlaybackSlotChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PlaybackStart == null) return;
        StopPlayback();
        PlaybackStatus.Text = "";
        if (!settingPlaybackSlots && IsVisible && PlaybackSlot.SelectedItem is GameAnimationSlot)
            await StartPlaybackAsync();
    }

    private void UpdatePlaybackControls()
    {
        PlaybackStart.IsEnabled = scene != null && loadedAction != null && PlaybackSlot.SelectedItem is GameAnimationSlot && playbackBake == null;
        PlaybackStop.IsEnabled = playbackBake != null || playbackClip != null;
        PlaybackStart.IsPlaying = playbackClip != null && playbackTime.IsRunning;
    }

    private async void PlayAnimation(object sender, RoutedEventArgs e)
    {
        if (playbackClip != null)
        {
            if (playbackTime.IsRunning)
            {
                playbackTime.Stop();
                CompositionTarget.Rendering -= PlaybackTick;
                UpdatePlaybackPosition(PlaybackPosition.Value);
                UpdatePlaybackStatus("已暂停");
            }
            else
            {
                playbackOffset = PlaybackPosition.Value;
                renderClockOrigin = null;
                playbackTime.Restart();
                CompositionTarget.Rendering += PlaybackTick;
                UpdatePlaybackStatus("循环播放");
            }
            UpdatePlaybackControls();
            return;
        }
        await StartPlaybackAsync();
    }

    private async Task StartPlaybackAsync()
    {
        if (!IsVisible || source is not { } game || scene is not { } preview || loadedAction is not { } action ||
            PlaybackSlot.SelectedItem is not GameAnimationSlot slot) return;
        StopPlayback();
        using var pending = new CancellationTokenSource();
        playbackBake = pending;
        UpdatePlaybackControls();
        try
        {
            var directory = GodbertAnimationSettings.ReadDirectory();
            if (directory == null)
            {
                PlaybackStatus.Text = "请在设置中选择 Godbert 安装目录。";
                return;
            }
            if (preview.Vertices > 100_000)
            {
                PlaybackStatus.Text = "模型超过实时蒙皮的 10 万顶点预览上限。";
                return;
            }
            PlaybackStatus.Text = "正在用 Godbert 采样动作…";
            var clip = await game.BakeModelAnimationAsync(preview.Path, action.Path, slot.Index, directory, pending.Token, samplingRate: 120, previewVertexCount: preview.Vertices);
            pending.Token.ThrowIfCancellationRequested();
            if (!IsVisible || !ReferenceEquals(scene, preview) || !ReferenceEquals(loadedAction, action)) return;
            // 短片段在启动前准备所有帧，避免播放时后台任务切换占用每帧的时间预算。
            var cacheAllFrames = (preview.Meshes.Sum(mesh => (long)mesh.Geometry.Positions.Count) * 24L + preview.Meshes.Count * 128L) * clip.Frames.Length <= 64L * 1024 * 1024;
            PlaybackStatus.Text = "正在准备动作帧…";
            LocalGameSource.ModelSkinningBinding? binding = null;
            var prepared = await Task.Run(() =>
            {
                binding = LocalGameSource.CreateModelSkinningBinding(preview.SourceModel, clip);
                var frames = new Point3DCollection[cacheAllFrames ? clip.Frames.Length : 1][];
                for (var frame = 0; frame < frames.Length; frame++)
                {
                    pending.Token.ThrowIfCancellationRequested();
                    frames[frame] = PreparePose(preview, clip, frame, binding);
                }
                return frames;
            }, pending.Token);
            pending.Token.ThrowIfCancellationRequested();
            if (!IsVisible || !ReferenceEquals(scene, preview) || !ReferenceEquals(loadedAction, action)) return;
            animatedGeometry = preview.Meshes.Select(mesh =>
            {
                var geometry = mesh.Geometry.CloneCurrentValue();
                geometry.Normals = new Vector3DCollection();
                return geometry;
            }).ToArray();
            playbackClip = clip;
            skinningBinding = binding;
            recentPoseLimit = (int)Math.Max(1, 64L * 1024 * 1024 / (preview.Meshes.Sum(mesh => (long)mesh.Geometry.Positions.Count) * 24L + preview.Meshes.Count * 128L));
            PlaybackPosition.Maximum = clip.Duration;
            PlaybackPosition.IsEnabled = true;
            UpdatePlaybackPosition(0);
            // 仅为能完整容纳的片段缓存顶点，避免长动作无限占用内存。
            poseCache = cacheAllFrames ? prepared : null;
            ApplyPose(prepared[0]);
            RenderScene();
            lastFrame = 0;
            playbackTime.Restart();
            CompositionTarget.Rendering += PlaybackTick;
            UpdatePlaybackStatus("循环播放");
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

    private async void PlaybackTick(object? sender, EventArgs e)
    {
        if (!playbackTime.IsRunning || scene is not { } preview || playbackClip is not { } clip) return;
        var seconds = playbackOffset + playbackTime.Elapsed.TotalSeconds;
        if (e is RenderingEventArgs rendering)
        {
            // 使用预计呈现时间，避免 UI 回调排队抖动使相邻采样帧被重复或跳过。
            renderClockOrigin ??= rendering.RenderingTime.TotalSeconds - seconds;
            seconds = Math.Max(0, rendering.RenderingTime.TotalSeconds - renderClockOrigin.Value);
        }
        var elapsed = seconds % clip.Duration;
        if (!PlaybackPosition.IsMouseCaptureWithin) UpdatePlaybackPosition(elapsed, forceTimeLabel: false);
        if (framePending) return;
        var frame = Math.Clamp((int)(elapsed * clip.FramesPerSecond), 0, clip.Frames.Length - 1);
        if (frame == lastFrame) return;
        var generation = playbackGeneration;
        framePending = true;
        try
        {
            var positions = poseCache?[frame] ?? recentPoses.GetValueOrDefault(frame);
            if (positions == null)
            {
                var binding = skinningBinding;
                positions = await Task.Run(() => PreparePose(preview, clip, frame, binding));
            }
            if (generation != playbackGeneration || !playbackTime.IsRunning) return;
            RememberPose(frame, positions);
            ApplyPose(positions);
            lastFrame = frame;
        }
        catch (Exception ex)
        {
            if (generation == playbackGeneration)
            {
                StopPlayback();
                PlaybackStatus.Text = "蒙皮失败 · " + ex.Message;
            }
        }
        finally
        {
            if (generation == playbackGeneration) framePending = false;
        }
    }

    private static Point3DCollection[] PreparePose(PreviewScene preview, ModelAnimationClip clip, int frame, LocalGameSource.ModelSkinningBinding? binding = null)
    {
        var meshPositions = binding?.SkinFrame(frame) ?? LocalGameSource.SkinModelFrame(preview.SourceModel, clip, frame);
        var result = new Point3DCollection[meshPositions.Length];
        for (var mesh = 0; mesh < result.Length; mesh++)
        {
            var vertices = meshPositions[mesh];
            var sources = preview.Meshes[mesh].Sources;
            var points = new Point3D[sources?.Length ?? vertices.Length];
            for (var i = 0; i < points.Length; i++)
            {
                var vertex = sources == null ? vertices[i] : sources[i].Position(vertices);
                points[i] = new Point3D((vertex.X - preview.Center.X) * preview.Scale,
                    (vertex.Y - preview.Center.Y) * preview.Scale, (vertex.Z - preview.Center.Z) * preview.Scale);
            }
            var positions = new Point3DCollection(points);
            positions.Freeze();
            result[mesh] = positions;
        }
        return result;
    }

    private void RememberPose(int frame, Point3DCollection[] positions)
    {
        if (poseCache != null || recentPoses.ContainsKey(frame)) return;
        while (recentPoses.Count >= recentPoseLimit && recentPoseOrder.TryDequeue(out var oldest))
            recentPoses.Remove(oldest);
        recentPoses.Add(frame, positions);
        recentPoseOrder.Enqueue(frame);
    }

    private void UpdatePlaybackStatus(string state)
    {
        if (playbackClip is not { } clip) return;
        var name = (PlaybackSlot.SelectedItem as GameAnimationSlot)?.Name ?? "动作";
        PlaybackStatus.Text = $"{name} · {clip.Duration:0.00} 秒 · 采样 {clip.FramesPerSecond:0} fps · {state}";
        PlaybackStatus.ToolTip = "动作采样率与窗口刷新率分别计算；实际呈现受显示器、WPF 和模型开销影响。";
    }

    private void UpdatePlaybackPosition(double seconds, bool forceTimeLabel = true)
    {
        updatingPosition = true;
        try { PlaybackPosition.Value = seconds; }
        finally { updatingPosition = false; }
        // 滑块跟随渲染时钟，时间文字单独限频，减少文本布局。
        if (!forceTimeLabel && Math.Abs(seconds - lastTimeLabelPosition) < 0.1) return;
        lastTimeLabelPosition = seconds;
        PlaybackPositionText.Text = $"{seconds:0.00} / {playbackClip?.Duration ?? 0:0.00} 秒";
    }

    private void SeekAnimation(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (updatingPosition || playbackClip is not { } clip || scene is not { } preview) return;
        var running = playbackTime.IsRunning;
        playbackOffset = Math.Clamp(e.NewValue, 0, clip.Duration);
        playbackTime.Reset();
        if (running) playbackTime.Start();
        renderClockOrigin = null;
        // 使跳转前尚未完成的后台帧失效；保持原来的播放或暂停状态。
        playbackGeneration++;
        framePending = false;
        var frame = Math.Clamp((int)(playbackOffset * clip.FramesPerSecond), 0, clip.Frames.Length - 1);
        try
        {
            var positions = poseCache?[frame] ?? recentPoses.GetValueOrDefault(frame)
                ?? PreparePose(preview, clip, frame, skinningBinding);
            RememberPose(frame, positions);
            ApplyPose(positions);
            lastFrame = frame;
            UpdatePlaybackPosition(playbackOffset);
        }
        catch (Exception ex)
        {
            StopPlayback();
            PlaybackStatus.Text = "跳转失败 · " + ex.Message;
        }
    }

    private void ApplyPose(Point3DCollection[] positions)
    {
        if (animatedGeometry is not { } geometries) return;
        for (var mesh = 0; mesh < geometries.Length; mesh++) geometries[mesh].Positions = positions[mesh];
    }

    private async void StartDefaultIdle()
    {
        if (scene is not { } preview || !IsVisible) return;
        try
        {
            var match = System.Text.RegularExpressions.Regex.Match(preview.Path,
                @"^chara/monster/(m\d{4})/obj/body/b\d{4}/model/");
            if (!match.Success || GodbertAnimationSettings.ReadDirectory() == null) return;
            await OpenAnimation($"chara/monster/{match.Groups[1].Value}/animation/a0001/bt_common/resident/monster.pap", autoPlayIdle: true);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            PlaybackStatus.Text = "无法读取 Godbert 配置 · " + ex.Message;
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
        CompositionTarget.Rendering -= PlaybackTick;
        playbackGeneration++;
        framePending = false;
        poseCache = null;
        skinningBinding = null;
        recentPoses.Clear();
        recentPoseOrder.Clear();
        playbackOffset = 0;
        if (PlaybackPosition != null)
        {
            PlaybackPosition.IsEnabled = false;
            updatingPosition = true;
            try { PlaybackPosition.Value = 0; }
            finally { updatingPosition = false; }
            PlaybackPositionText.Text = "0.00 / 0.00 秒";
        }
        playbackTime.Reset();
        renderClockOrigin = null;
        playbackClip = null;
        var wasAnimated = animatedGeometry != null;
        animatedGeometry = null;
        if (restoreScene && wasAnimated) RenderScene();
        if (PlaybackStart != null) UpdatePlaybackControls();
    }
}
