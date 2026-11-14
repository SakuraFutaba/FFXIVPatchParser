using System.Windows;
using System.Windows.Controls;

namespace FFXIVPatchParser.Desktop;

public partial class SqPackBrowser
{
    private CancellationTokenSource? patchCatalogueCancellation;
    private Task patchCatalogueTask = Task.CompletedTask;
    private bool patchCatalogueNeeded;
    private IReadOnlyList<PatchMediaEntry> patchModels = [], patchAudio = [];

    public bool IsPatchMediaPage => currentPage is SqPackPage.PatchImages or SqPackPage.PatchModels or SqPackPage.PatchAudio;
    public bool IsLocalMediaPage => currentPage is SqPackPage.Images or SqPackPage.Models or SqPackPage.Audio;
    public bool IsMediaPage => IsPatchMediaPage || IsLocalMediaPage;
    public bool CanScanMedia => operation == null && game != null &&
        (IsLocalMediaPage || IsPatchMediaPage && patch != null && patchArchives.Length > 0);

    public async Task ScanCurrentMediaAsync()
    {
        switch (currentPage)
        {
            case SqPackPage.Images: await ScanIcons(); break;
            case SqPackPage.Models:
                await Work("正在扫描模型索引…", async token =>
                {
                    var progress = WorkProgress(token);
                    return await LocalModelsPanel.ScanUnknownAsync(token, progress);
                });
                break;
            case SqPackPage.Audio:
                await Work("正在扫描音频索引…", async token =>
                {
                    await LocalAudioPanel.ScanUnknownAsync(token);
                    return "音频扫描完成";
                });
                break;
            case SqPackPage.PatchImages: await ScanPatchImages(); break;
            case SqPackPage.PatchModels: await ScanMedia(PatchMediaKind.Model); break;
            case SqPackPage.PatchAudio: await ScanMedia(PatchMediaKind.Audio); break;
        }
    }

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
        patchCatalogueNeeded = patch != null && patchArchives.Length > 0;
        patchModels = patchAudio = [];
        PatchModelsList.ItemsSource = null;
        PatchAudioList.ItemsSource = null;
        PatchModelSearch.Clear();
        PatchAudioSearch.Clear();
        PatchModelPreview.SetSource(null);
        PatchAudioPreview.SetSource(null);
        PatchModelPreview.SetSource(game);
        PatchAudioPreview.SetSource(game);
        PatchModelsSummary.Text = PatchAudioSummary.Text = PatchCatalogueMessage;
    }

    private string PatchCatalogueMessage => patch == null ? "请在补丁工作区打开补丁 ZIP。"
        : game == null ? "请在设置中连接补丁对应的游戏版本。" : "等待查找已知路径…";

    private void StartPatchCatalogue()
    {
        if (!patchCatalogueNeeded || closing || operation != null || game is not { } source || patch is not { } loaded) return;
        patchCatalogueNeeded = false;
        var request = new CancellationTokenSource();
        patchCatalogueCancellation = request;
        patchCatalogueTask = ReadPatchCatalogue(source, loaded, patchArchives, patchCatalogueTask, request);
    }

    private async Task ReadPatchCatalogue(LocalGameSource source, LoadedPatch loaded, string[] archives, Task previous,
        CancellationTokenSource request)
    {
        using (request)
        {
            var token = request.Token;
            try
            {
                await previous;
                token.ThrowIfCancellationRequested();
                await ReadKnownPatchEntries(PatchSummary,
                    () => PatchImageScanner.ScanKnownPaths(source, loaded, archives, token), ShowPatchImages, token);
                await ReadKnownPatchEntries(PatchModelsSummary,
                    () => PatchMediaScanner.ScanKnownPaths(source, loaded, archives, PatchMediaKind.Model, token),
                    result => ShowPatchMedia(PatchMediaKind.Model, result), token);
                await ReadKnownPatchEntries(PatchAudioSummary,
                    () => PatchMediaScanner.ScanKnownPaths(source, loaded, archives, PatchMediaKind.Audio, token),
                    result => ShowPatchMedia(PatchMediaKind.Audio, result), token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            finally
            {
                if (patchCatalogueCancellation == request) patchCatalogueCancellation = null;
            }
        }
    }

    private static async Task ReadKnownPatchEntries<T>(TextBlock summary, Func<T> scan, Action<T> display, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        summary.Text = "正在查找已知路径…";
        try
        {
            var result = await Task.Run(scan, token);
            token.ThrowIfCancellationRequested();
            display(result);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            token.ThrowIfCancellationRequested();
            summary.Text = "查找已知路径失败：" + ex.Message;
        }
    }

    private async Task ScanMedia(PatchMediaKind kind)
    {
        if (game is not { } source || patch is not { } loaded) return;
        var archives = patchArchives;
        await Work("准备完整扫描…", async token =>
        {
            await patchCatalogueTask.WaitAsync(token);
            var progress = WorkProgress(token);
            var result = await Task.Run(() => PatchMediaScanner.Scan(source, loaded, archives, kind, token, progress), token);
            token.ThrowIfCancellationRequested();
            ShowPatchMedia(kind, result);
            return "";
        }, forPatch: true);
    }

    private void ShowPatchMedia(PatchMediaKind kind, PatchMediaScanResult result)
    {
        if (kind == PatchMediaKind.Model)
        {
            patchModels = result.Entries;
            PatchModelsSummary.Text = result.Summary;
            ApplyPatchModelFilter();
        }
        else
        {
            patchAudio = result.Entries;
            PatchAudioSummary.Text = result.Summary;
            ApplyPatchAudioFilter();
        }
    }

    private void FilterPatchModels(object sender, TextChangedEventArgs e)
    {
        if (initialized) ApplyPatchModelFilter();
    }

    private void FilterPatchAudio(object sender, TextChangedEventArgs e)
    {
        if (initialized) ApplyPatchAudioFilter();
    }

    private void ApplyPatchModelFilter() => PatchModelsList.ItemsSource = PathTree.Build(patchModels, entry => entry.Path,
        PatchModelSearch.Text, entry => entry.Container, displayPathSelector: CompactModelPath);

    private void ApplyPatchAudioFilter() => PatchAudioList.ItemsSource = PathTree.Build(patchAudio, entry => entry.Path,
        PatchAudioSearch.Text, entry => entry.Container);

    internal static string CompactModelPath(string path)
    {
        if (!path.StartsWith("chara/", StringComparison.OrdinalIgnoreCase)) return path;
        var parts = path.Split('/');
        return string.Join('/', parts.Where((part, index) => index == parts.Length - 1
            || part is not ("obj" or "body" or "model")));
    }

    private async void PatchModelSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is PathTreeNode { Payload: PatchMediaEntry entry }) await PatchModelPreview.OpenAsync(entry.Path);
    }

    private async void PatchAudioSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is PathTreeNode { Payload: PatchMediaEntry entry }) await PatchAudioPreview.OpenAsync(entry.Path);
    }
}
