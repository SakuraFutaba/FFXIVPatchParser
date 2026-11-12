using System.Windows;
using System.Windows.Controls;

namespace FFXIVPatchParser.Desktop;

public partial class SqPackBrowser
{
    private async void OpenModelSheet(object? sender, ModelTableLinkEventArgs e) =>
        await JumpToCellAsync(e.Sheet, new RowKey(e.RowId, null), -1);

    private void SetMediaSource(LocalGameSource? source)
    {
        LocalModelsPanel.SetSource(source);
        LocalAudioPanel.SetSource(source);
        PatchModelPreview.SetSource(source);
        PatchAudioPreview.SetSource(source);
    }

    private void ResetPatchMedia()
    {
        PatchModelsList.ItemsSource = null;
        PatchAudioList.ItemsSource = null;
        PatchModelPreview.SetSource(null);
        PatchAudioPreview.SetSource(null);
        PatchModelPreview.SetSource(game);
        PatchAudioPreview.SetSource(game);
        var summary = patch == null ? "在补丁工作区打开补丁 ZIP。" : "使用通过校验的本地目标版本筛选写入候选。";
        PatchModelsSummary.Text = PatchAudioSummary.Text = summary;
        ResetContainers(PatchModelContainer, PatchMediaKind.Model);
        ResetContainers(PatchAudioContainer, PatchMediaKind.Audio);
    }

    private void ResetContainers(ComboBox picker, PatchMediaKind kind)
    {
        picker.ItemsSource = new[] { "全部相关容器" }.Concat(patch == null ? [] : PatchMediaScanner.Containers(patch, kind)).ToArray();
        picker.SelectedIndex = 0;
    }

    private async void ScanPatchModels(object sender, RoutedEventArgs e) => await ScanMedia(PatchMediaKind.Model, PatchModelsList, PatchModelsSummary, PatchModelContainer);
    private async void ScanPatchAudio(object sender, RoutedEventArgs e) => await ScanMedia(PatchMediaKind.Audio, PatchAudioList, PatchAudioSummary, PatchAudioContainer);

    private async Task ScanMedia(PatchMediaKind kind, ListBox list, TextBlock summary, ComboBox picker)
    {
        if (game is not { } source || patch is not { } loaded) return;
        var archives = patchArchives;
        var container = picker.SelectedIndex > 0 ? picker.SelectedItem as string : null;
        await Work("正在验证补丁与本地目标版本…", async token =>
        {
            var progress = new Progress<string>(message => { if (!token.IsCancellationRequested) SetStatus(message); });
            var result = await Task.Run(() => PatchMediaScanner.Scan(source, loaded, archives, kind, token, progress, container), token);
            token.ThrowIfCancellationRequested();
            list.ItemsSource = result.Entries;
            summary.Text = result.Summary;
            return result.Summary;
        }, forPatch: true);
    }

    private async void PatchModelSelected(object sender, SelectionChangedEventArgs e)
    {
        if (PatchModelsList.SelectedItem is PatchMediaEntry entry) await PatchModelPreview.OpenAsync(entry.Path);
    }

    private async void PatchAudioSelected(object sender, SelectionChangedEventArgs e)
    {
        if (PatchAudioList.SelectedItem is PatchMediaEntry entry) await PatchAudioPreview.OpenAsync(entry.Path);
    }
}
