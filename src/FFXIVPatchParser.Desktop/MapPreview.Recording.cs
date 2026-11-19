using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media.Media3D;
using System.Runtime.CompilerServices;

namespace FFXIVPatchParser.Desktop;

public partial class MapPreview
{
    private CancellationTokenSource? recordingRead;
    private readonly DispatcherTimer recordingTimer = new() { Interval = TimeSpan.FromMilliseconds(25) };
    private readonly System.Diagnostics.Stopwatch recordingClock = new();
    private DateTimeOffset recordingStart;
    private readonly Dictionary<uint, bool> phaseVisibility = [];
    private readonly ConditionalWeakTable<Model3DGroup, PhaseMesh> phaseGroups = new();
    private sealed record PhaseMesh(uint LayoutId, Model3DGroup Original);

    private Model3DGroup MakePhaseGroup(Model3DGroup geometry, uint layoutId)
    {
        if (layoutId == 0) return geometry;
        var wrapper = new Model3DGroup();
        phaseGroups.Add(wrapper, new(layoutId, geometry));
        if (phaseVisibility.GetValueOrDefault(layoutId, true)) wrapper.Children.Add(geometry);
        return wrapper;
    }

    private void UpdatePhaseVisibility()
    {
        foreach (var wrapper in visibleScene.Children.OfType<Model3DGroup>())
        {
            if (!phaseGroups.TryGetValue(wrapper, out var mesh)) continue;
            var visible = phaseVisibility.GetValueOrDefault(mesh.LayoutId, true);
            if (visible && wrapper.Children.Count == 0) wrapper.Children.Add(mesh.Original);
            else if (!visible && wrapper.Children.Count != 0) wrapper.Children.Clear();
        }
    }

    private void PreviewPhaseTimeline(object sender, SelectionChangedEventArgs e)
    {
        if (PhaseTimeline.SelectedItem is not GameSharedGroupTimeline slot || MapEffectChoice.SelectedItem is not MapEffectOption option) return;
        phaseVisibility[option.Binding.LayoutId] = slot.Name == "bg_on";
        UpdatePhaseVisibility();
        RecordingStatus.Text = $"共享组显隐预览：{slot.Name}（整组近似）";
    }

    private int appliedEvent = -1;
    private void ApplyRecordedPhases(int selected)
    {
        if (!advancingRecording || selected < appliedEvent)
        {
            phaseVisibility.Clear();
            appliedEvent = -1;
        }
        var options = MapEffectChoice.Items.OfType<MapEffectOption>().SelectMany(option =>
            option.Binding.SourceRows.Select(row => row.SubrowId).Distinct().Select(id => (id, option))).ToLookup(pair => pair.id, pair => pair.option);
        for (var i = appliedEvent + 1; i <= selected; i++)
        {
            var evt = (GameMapEffectEvent)RecordingEvents.Items[i];
            var matches = options[evt.Index].ToArray();
            if (matches.Length != 1 || evt.TimelineIndex == 0) continue;
            var requested = matches[0].Binding.Timelines.Where(t => (evt.TimelineIndex & t.Mask) != 0).ToArray();
            if (requested.Length == 1 && requested[0].Name is "bg_on" or "bg_off")
                phaseVisibility[matches[0].Binding.LayoutId] = requested[0].Name == "bg_on";
        }
        appliedEvent = selected;
        UpdatePhaseVisibility();
    }

    private void ClearRecording()
    {
        recordingTimer.Stop();
        recordingClock.Reset();
        recordingRead?.Cancel();
        phaseVisibility.Clear();
        appliedEvent = -1;
        UpdatePhaseVisibility();
        RecordingEvents.ItemsSource = null;
        RecordingStatus.Text = "";
        RecordingPlay.IsEnabled = false;
        RecordingPlay.Content = "播放事件";
    }

    private async void LoadRecording(object sender, RoutedEventArgs args)
    {
        if (selectedMap is not { } map) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "ACT 网络日志|*.log;*.txt|所有文件|*.*" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        ClearRecording();
        using var read = new CancellationTokenSource();
        recordingRead = read;
        RecordingStatus.Text = "正在读取事件…";
        try
        {
            var events = await Task.Run(() => LocalGameSource.ReadMapEffectRecording(dialog.FileName, map.TerritoryId, read.Token), read.Token);
            read.Token.ThrowIfCancellationRequested();
            RecordingEvents.ItemsSource = events;
            RecordingPlay.IsEnabled = events.Count > 1;
            if (events.Count > 0) RecordingEvents.SelectedIndex = 0;
            else RecordingStatus.Text = "未找到当前区域的事件。日志需包含 ACT 区域行（01）和 MapEffect 行（257）。";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!read.IsCancellationRequested) RecordingStatus.Text = ex.Message; }
        finally { if (recordingRead == read) recordingRead = null; }
    }

    private void SelectRecordedEvent(object sender, SelectionChangedEventArgs args)
    {
        if (RecordingEvents.SelectedItem is not GameMapEffectEvent evt) return;
        ApplyRecordedPhases(RecordingEvents.SelectedIndex);
        var matches = MapEffectChoice.Items.OfType<MapEffectOption>()
            .Where(option => option.Binding.SourceRows.Any(row => row.SubrowId == evt.Index)).ToArray();
        MapEffectChoice.SelectedItem = matches.Length == 1 ? matches[0] : null;
        if (matches.Length != 1) MapEffectDetail.Text = matches.Length == 0 ? "当前布局没有该索引的绑定。" : "该索引对应多个实例，无法唯一确定目标。";
        RecordingStatus.Text = $"Director 0x{evt.DirectorId:X8} · State 0x{evt.State:X4} · TimelineIndex 0x{evt.TimelineIndex:X4}";
        if (matches.Length == 1 && evt.TimelineIndex != 0)
        {
            var slots = matches[0].Binding.Timelines.Where(slot => (evt.TimelineIndex & slot.Mask) != 0);
            RecordingStatus.Text += "\n时间线请求：" + string.Join("、", slots.Select(slot => slot.Name).DefaultIfEmpty("没有匹配槽"));
            if (phaseVisibility.TryGetValue(matches[0].Binding.LayoutId, out var visible))
                RecordingStatus.Text += visible ? "\n整组显隐近似：显示" : "\n整组显隐近似：隐藏";
        }
        if (!advancingRecording)
        {
            recordingStart = evt.Time;
            recordingClock.Reset();
            if (recordingTimer.IsEnabled) recordingClock.Start();
        }
    }

    private bool advancingRecording;
    private void ToggleRecording(object sender, RoutedEventArgs args)
    {
        if (recordingTimer.IsEnabled)
        {
            recordingTimer.Stop();
            recordingClock.Stop();
            RecordingPlay.Content = "播放事件";
            return;
        }
        if (RecordingEvents.SelectedIndex == RecordingEvents.Items.Count - 1) RecordingEvents.SelectedIndex = 0;
        if (RecordingEvents.SelectedItem is not GameMapEffectEvent) return;
        recordingClock.Start();
        recordingTimer.Tick -= TickRecording;
        recordingTimer.Tick += TickRecording;
        recordingTimer.Start();
        RecordingPlay.Content = "暂停";
    }

    private void TickRecording(object? sender, EventArgs args)
    {
        var start = recordingStart;
        var elapsed = recordingClock.Elapsed;
        var i = RecordingEvents.SelectedIndex;
        while (i + 1 < RecordingEvents.Items.Count && ((GameMapEffectEvent)RecordingEvents.Items[i + 1]).Time - start <= elapsed) i++;
        advancingRecording = true;
        try { RecordingEvents.SelectedIndex = i; }
        finally { advancingRecording = false; }
        if (i + 1 >= RecordingEvents.Items.Count || !IsVisible)
        {
            recordingTimer.Stop();
            recordingClock.Stop();
            RecordingPlay.Content = "播放事件";
        }
    }
}
