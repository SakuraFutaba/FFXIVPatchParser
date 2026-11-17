using System.Windows;
using System.Windows.Controls;

namespace FFXIVPatchParser.Desktop;

public partial class MapPreview
{
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
        if (MapEffectDetail == null || MapEffectChoice.SelectedItem is not MapEffectOption option) return;
        var binding = option.Binding;
        MapEffectDetail.Text = $"LayoutId：{binding.LayoutId}\n管理表：{string.Join("、", binding.SourceRows)}\n布局：{binding.Layer}\nSGB：{binding.SharedGroupPath}"
            + (binding.SourceRows.Select(row => row.RowId).Distinct().Count() > 1
                ? "\n多个管理表行引用同一实例；尚未确定当前 Director 使用哪一行。" : "")
            + "\n仅查看资源绑定；尚未执行状态或时间线。";
    }
}
