using System.Windows;
using System.Windows.Controls;

namespace FFXIVPatchParser.Desktop;

public partial class MapPreview
{
    private CancellationTokenSource? battleLogRequest;
    private Task battleLogLoad = Task.CompletedTask;

    private async void ReadBattleLog(object sender, RoutedEventArgs e)
    {
        if (source is not { } game || selectedMap is not { } entry) return;
        var picker = new Microsoft.Win32.OpenFileDialog { Filter = "资源路径日志|*.txt;*.log|所有文件|*.*" };
        if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
        battleLogRequest?.Cancel();
        using var request = new CancellationTokenSource();
        battleLogRequest = request;
        BattleResources.ItemsSource = null;
        BattleResourceDetails.Text = "正在核对场地资源…";
        ReadBattleLogButton.IsEnabled = false;
        try
        {
            var task = Task.Run(() => game.ReadBattleLogResources(entry.ScenePath, picker.FileName, request.Token), request.Token);
            battleLogLoad = task;
            var rows = await task;
            request.Token.ThrowIfCancellationRequested();
            BattleResources.ItemsSource = rows;
            if (rows.Count > 0) BattleResources.SelectedIndex = 0;
            else BattleResourceDetails.Text = "日志中未找到当前区域入场后带场地代号的 TMB、SGB 或 AVFX。";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!request.IsCancellationRequested) BattleResourceDetails.Text = ex.Message; }
        finally
        {
            if (battleLogRequest == request)
            {
                battleLogRequest = null;
                ReadBattleLogButton.IsEnabled = true;
                battleLogLoad = Task.CompletedTask;
            }
        }
    }

    private void BattleResourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BattleResources.SelectedItem is not GameBattleResource resource) return;
        BattleResourceDetails.Text = $"日志首次读取：第 {resource.FirstLogLine:N0} 行\n{resource.Path}\n\n文件内静态引用（{resource.References.Count}）\n"
            + string.Join("\n", resource.References);
    }

    private sealed record MapEffectOption(GameMapEffectBinding Binding)
    {
        public string Label => $"索引 {string.Join("/", Binding.SourceRows.Select(r => r.SubrowId).Distinct())} · 实例 {Binding.LayoutId}";
    }

    private async Task ShowMapEffectsAsync(LocalGameSource game, NamedMapEntry entry, CancellationToken token)
    {
        MapEffectControls.Visibility = Visibility.Visible;
        MapEffectChoice.ItemsSource = null;
        MapEffectDetail.Text = "正在匹配场景资源…";
        try
        {
            var bindings = await Task.Run(() => game.ReadMapEffectBindings(entry.ScenePath, token), token);
            token.ThrowIfCancellationRequested();
            MapEffectChoice.ItemsSource = bindings.Select(binding => new MapEffectOption(binding)).ToArray();
            MapEffectChoice.IsEnabled = bindings.Count > 0;
            if (bindings.Count > 0) MapEffectChoice.SelectedIndex = 0;
            else MapEffectDetail.Text = "当前布局与管理表没有已验证的绑定，暂不可选择 MapEffect 目标。";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            token.ThrowIfCancellationRequested();
            MapEffectChoice.IsEnabled = false;
            MapEffectDetail.Text = "绑定读取失败：" + ex.Message;
        }
    }

    private void MapEffectSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PhaseTimeline is not null)
        {
            var phaseSlots = (MapEffectChoice.SelectedItem as MapEffectOption)?.Binding.Timelines
                .Where(t => t.Name is "bg_on" or "bg_off").ToArray();
            PhaseTimeline.ItemsSource = phaseSlots;
            PhaseTimeline.Visibility = phaseSlots is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
            PhaseTimeline.ToolTip = "按选定轨道近似预览该共享组的整体显隐";
        }
        if (MapEffectDetail == null || MapEffectChoice.SelectedItem is not MapEffectOption option) return;
        var binding = option.Binding;
        MapEffectDetail.Text = $"LayoutId：{binding.LayoutId}\n管理表：{string.Join("、", binding.SourceRows)}\n布局：{binding.Layer}\nSGB：{binding.SharedGroupPath}"
            + (binding.SourceRows.Select(row => row.RowId).Distinct().Count() > 1
                ? "\n多个管理表行引用同一实例；尚未确定当前 Director 使用哪一行。" : "")
            + (binding.Timelines.Count == 0 ? "\n没有可解析的时间线槽。"
                : "\n时间线槽\n" + string.Join("\n", binding.Timelines.Select(t => t.Label)));
    }
}
