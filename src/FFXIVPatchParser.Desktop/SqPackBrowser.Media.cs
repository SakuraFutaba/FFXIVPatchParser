using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FFXIVPatchParser.Desktop;

public partial class SqPackBrowser
{
    private CancellationTokenSource? patchCatalogueCancellation;
    private Task patchCatalogueTask = Task.CompletedTask;
    private bool patchCatalogueNeeded;
    private IReadOnlyList<PatchMediaEntry> patchModels = [], patchAudio = [];
    private IReadOnlyDictionary<string, MusicTrackText> patchSongTitles = new Dictionary<string, MusicTrackText>();

    public bool IsPatchMediaPage => currentPage is SqPackPage.PatchImages or SqPackPage.PatchModels or SqPackPage.PatchAudio;
    public bool CanScanMedia => operation == null && game != null &&
        IsPatchMediaPage && patch != null && patchArchives.Length > 0;

    public async Task ScanCurrentMediaAsync()
    {
        switch (currentPage)
        {
            case SqPackPage.PatchImages: await ScanPatchImages(); break;
            case SqPackPage.PatchModels: await ScanMedia(PatchMediaKind.Model); break;
            case SqPackPage.PatchAudio: await ScanMedia(PatchMediaKind.Audio); break;
        }
    }

    private async void OpenModelSheet(object? sender, ModelTableLinkEventArgs e) =>
        await JumpToCellAsync(e.Sheet, new RowKey(e.RowId, null), -1);

    private void SetMediaSource(LocalGameSource? source)
    {
        LocalIconBrowser.SetSource(source);
        LocalUldBrowser.SetSource(source);
        LocalModelsPanel.SetSource(source);
        LocalMapsPanel.SetSource(source);
        LocalAudioPanel.SetSource(source);
        PatchModelPreview.SetSource(source);
        PatchAudioPreview.SetSource(source);
    }

    private void ResetPatchMedia()
    {
        patchCatalogueNeeded = patch != null && patchArchives.Length > 0;
        patchModels = patchAudio = [];
        patchSongTitles = new Dictionary<string, MusicTrackText>();
        PathTreeView.Reset(PatchModelsList);
        PathTreeView.Reset(PatchAudioList);
        PatchModelSearch.Clear();
        PatchAudioSearch.Clear();
        PatchModelPreview.SetSource(null);
        PatchAudioPreview.SetSource(null);
        PatchModelPreview.SetSource(game);
        PatchAudioPreview.SetSource(game);
        PatchModelsSummary.Text = PatchAudioSummary.Text = PatchCatalogueMessage;
    }

    private string PatchCatalogueMessage => patch == null ? "请在「补丁目录」打开补丁 ZIP。"
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
                    () => (Scan: PatchMediaScanner.ScanKnownPaths(source, loaded, archives, PatchMediaKind.Audio, token),
                           Titles: MusicMetadata.ReadCatalogOrEmpty(source)),
                    result =>
                    {
                        patchSongTitles = result.Titles;
                        ShowPatchMedia(PatchMediaKind.Audio, result.Scan);
                    }, token);
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

    private void ApplyPatchModelFilter()
    {
        var query = PatchModelSearch.Text;
        var tree = PathTree.Build(patchModels, entry => entry.Path, query, entry => entry.Container,
            displayPathSelector: CompactModelPath);
        PathTreeView.SetItems(PatchModelsList, tree, query);
    }

    private void ApplyPatchAudioFilter()
    {
        var query = PatchAudioSearch.Text;
        var tree = PathTree.Build(patchAudio, entry => entry.Path, query,
            entry => entry.Container + " " + patchSongTitles.GetValueOrDefault(entry.Path)?.SearchText,
            leafLabelSelector: entry => MusicMetadata.DirectoryLabel(entry.Path, patchSongTitles.GetValueOrDefault(entry.Path)));
        PathTreeView.SetItems(PatchAudioList, tree, query);
    }

    internal static string CompactModelPath(string path)
    {
        if (!path.StartsWith("chara/", StringComparison.OrdinalIgnoreCase)) return path;
        var parts = path.Split('/');
        return string.Join('/', parts.Where((part, index) => index == parts.Length - 1
            || part is not ("obj" or "body" or "model")));
    }

    private async void PatchModelSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (PathTreeView.IsRestoringSelection(PatchModelsList)) return;
        if (e.NewValue is PathTreeNode { Payload: PatchMediaEntry entry }) await PatchModelPreview.OpenAsync(entry.Path);
    }

    private async void PatchAudioSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (PathTreeView.IsRestoringSelection(PatchAudioList)) return;
        if (e.NewValue is PathTreeNode { Payload: PatchMediaEntry entry }) await PatchAudioPreview.OpenAsync(entry.Path);
    }

    private async void PlayPatchAudioFile(object sender, MouseButtonEventArgs e)
    {
        if (AudioPreview.GetDoubleClickedFile(PatchAudioList, e)?.Payload is not PatchMediaEntry entry) return;
        e.Handled = true;
        await PatchAudioPreview.OpenAndPlayAsync(entry.Path);
    }
}
