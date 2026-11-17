using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace FFXIVPatchParser.Desktop;

public partial class SqPackBrowser
{
    private const int FileHexPageSize = 4096;
    private sealed record FileTarget(SqPackPage Page, string Path, NamedMapEntry? Map = null, bool MapImage = false);

    private GridLength fileTreeWidth = new(295);
    private SqPackFileSystemEntry? fileEntry;
    private SqPackFileSystemPreview? fileBytePage;
    private FileTarget? fileTarget;
    private bool showFileHex, filePreviewSuspended;
    private bool copyingFilePath;
    private CancellationTokenSource? fileRequest;
    private Task filePreviewTask = Task.CompletedTask;

    private void UpdateFileLayout(bool show)
    {
        if (show == (FileTree.Visibility == Visibility.Visible)) return;
        if (!show) fileTreeWidth = FileTreeColumn.Width;
        FileTree.Visibility = FileSplitter.Visibility = FileDetails.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        FileDetailsRow.SharedSizeGroup = show ? "FileHeader" : null;
        FileTreeColumn.MinWidth = show ? 180 : 0;
        FileTreeColumn.MaxWidth = show ? 470 : double.PositiveInfinity;
        FileTreeColumn.Width = show ? fileTreeWidth : new(0);
        FileDividerColumn.Width = new(show ? 8 : 0);
        TableTreeColumn.Width = new(show ? 0 : 196);
        ImageTreeColumn.Width = new(show ? 0 : 310);
        TableTreeDivider.Width = ImageTreeDivider.Width = new(show ? 0 : 8);
        TableTreePanel.Visibility = LocalImageTreePanel.Visibility = LocalImagePathPanel.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        (show ? FooterTableToolbar : InlineTableToolbar).Content = null;
        TableToolbar.Margin = show ? new(0, 4, 0, 6) : new(0, 6, 0, 0);
        (show ? InlineTableToolbar : FooterTableToolbar).Content = TableToolbar;
        LocalModelsPanel.ShowBrowser = LocalAudioPanel.ShowBrowser = !show;
        LocalMapsPanel.ShowMapBrowser(!show);
        if (show && fileEntry == null) ResetFilePreview();
    }

    private void UpdateVisiblePanels()
    {
        var files = currentPage == SqPackPage.Files;
        SqPackPage? previewPage = currentPage;
        if (files) previewPage = showFileHex ? null : fileTarget?.Page;
        TablesPanel.Visibility = Visible(SqPackPage.Tables);
        var inTable = previewPage == SqPackPage.Tables;
        var statusHost = inTable ? TableStatusHost : PageStatusHost;
        if (statusHost.Content == null)
        {
            (inTable ? PageStatusHost : TableStatusHost).Content = null;
            statusHost.Content = BrowserStatusBar;
        }
        var browseImages = currentPage == SqPackPage.Images;
        LocalImagesPanel.Visibility = files || imagePage == ImagePage.Textures ? Visible(SqPackPage.Images) : Visibility.Collapsed;
        LocalIconBrowser.Visibility = browseImages && imagePage == ImagePage.Icons ? Visibility.Visible : Visibility.Collapsed;
        LocalUldBrowser.Visibility = browseImages && imagePage == ImagePage.Uld ? Visibility.Visible : Visibility.Collapsed;
        LocalModelsPanel.Visibility = Visible(SqPackPage.Models);
        LocalMapsPanel.Visibility = Visible(SqPackPage.Maps);
        LocalAudioPanel.Visibility = Visible(SqPackPage.Audio);
        PatchImagesPanel.Visibility = Visible(SqPackPage.PatchImages);
        PatchModelsPanel.Visibility = Visible(SqPackPage.PatchModels);
        PatchAudioPanel.Visibility = Visible(SqPackPage.PatchAudio);
        FileHexPanel.Visibility = files && previewPage == null ? Visibility.Visible : Visibility.Collapsed;

        Visibility Visible(SqPackPage target) => previewPage == target ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void SuspendFilePreview()
    {
        if (currentPage != SqPackPage.Files) return;
        filePreviewSuspended = true;
        CancelFilePreview();
    }

    private void ResumeFilePreview()
    {
        filePreviewSuspended = false;
        if (fileEntry != null) BeginFileRead(fileBytePage?.Offset ?? 0, reuse: true);
    }

    private void CancelFilePreview()
    {
        if (fileRequest == null) return;
        fileRequest.Cancel();
        if (workPage == SqPackPage.Files) operation?.Cancel();
        if (fileTarget?.Page == SqPackPage.Models) LocalModelsPanel.CancelFileOpen();
        if (fileTarget?.Page == SqPackPage.Audio) LocalAudioPanel.CancelFileOpen();
        if (fileTarget?.Page == SqPackPage.Maps) _ = LocalMapsPanel.StopLoadingAsync();
    }

    private async Task StopFileWorkAsync()
    {
        CancelFilePreview();
        await Task.WhenAll(filePreviewTask, FileTree.StopLoadingAsync());
    }

    private void ResetFilePreview()
    {
        fileEntry = null;
        fileBytePage = null;
        fileTarget = null;
        showFileHex = false;
        FileInfo.Text = "";
        SetFilePreviewIssue(null);
        FileSourcePath.Text = "选择文件查看内容";
        FileHexText.Clear();
        UpdateFileButtons();
        UpdateVisiblePanels();
    }

    private void OpenFileFromTree(SqPackFileSystemEntry entry)
    {
        if (currentPage != SqPackPage.Files || filePreviewSuspended || game == null) return;
        CancelFilePreview();
        fileEntry = entry;
        fileTarget = null;
        fileBytePage = null;
        showFileHex = false;
        FileSourcePath.Text = entry.Path;
        FileInfo.Text = FileAddressLabel(entry);
        SetFilePreviewIssue(null);
        FileHexText.Text = "正在读取文件…";
        UpdateFileButtons();
        UpdateVisiblePanels();
        BeginFileRead(0);
    }

    private void BeginFileRead(long offset, bool reuse = false)
    {
        if (game is not { } source || fileEntry is not { } entry) return;
        CancelFilePreview();
        var pending = new CancellationTokenSource();
        fileRequest = pending;
        filePreviewTask = ReadFilePreviewAsync(source, entry, offset, filePreviewTask, pending, reuse);
        UpdateFileButtons();
    }

    private async Task ReadFilePreviewAsync(LocalGameSource source, SqPackFileSystemEntry entry, long offset,
        Task previous, CancellationTokenSource pending, bool reuse)
    {
        var token = pending.Token;
        var bytesReady = false;
        try
        {
            await previous;
            // 导航先显示宿主，再启动需要可见视口的预览。
            await Dispatcher.Yield(DispatcherPriority.Background);
            token.ThrowIfCancellationRequested();
            var first = fileBytePage == null;
            var page = reuse && fileBytePage is { } cached ? cached : await Task.Run(
                () => source.ReadFileSystemFile(entry, FileHexPageSize, token, offset), token);
            token.ThrowIfCancellationRequested();
            fileBytePage = page;
            FileInfo.Text = $"{Workspace.Size(page.Size)} · {FileAddressLabel(entry)}";
            RenderFileHex(page);
            bytesReady = true;
            var target = first ? await FindFileTarget(source, entry, page, token) : fileTarget;
            token.ThrowIfCancellationRequested();
            fileTarget = target;
            if (target is null) SetFilePreviewIssue(null);
            UpdateVisiblePanels();
            UpdateFileButtons();
            if (!showFileHex && target != null)
            {
                var opened = await OpenFileTarget(target);
                token.ThrowIfCancellationRequested();
                if (!opened) ShowFilePreviewFailure($"此文件暂时无法显示{FilePreviewName(target.Page)}预览。");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested) return;
            ShowFilePreviewFailure((bytesReady ? "无法打开专用预览 · " : "无法读取文件字节 · ") + ex.Message);
        }
        finally
        {
            if (fileRequest == pending)
            {
                fileRequest = null;
                UpdateFileButtons();
            }
            pending.Dispose();
        }
    }

    private void SetFilePreviewIssue(string? message)
    {
        FilePreviewIssue.Text = message ?? "";
        FilePreviewIssue.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowFilePreviewFailure(string message)
    {
        showFileHex = true;
        if (fileBytePage is null) FileHexText.Clear();
        SetFilePreviewIssue(message);
        UpdateVisiblePanels();
    }

    private static string FileAddressLabel(SqPackFileSystemEntry entry)
    {
        var hashes = entry.IsIndex2 ? $"index2 #{entry.Hash:X8}" : $"index #{entry.Hash:X16}";
        if (!entry.IsIndex2 && entry.FullPathHash is { } full) hashes += $" · index2 #{full:X8}";
        return $"{entry.Container} · 0x{entry.Offset:X} · {hashes}";
    }

    private static async Task<FileTarget?> FindFileTarget(LocalGameSource source, SqPackFileSystemEntry entry,
        SqPackFileSystemPreview page, CancellationToken token)
    {
        var path = entry.KnownPath ?? "";
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var scene = extension == ".lvb" && path.StartsWith("bg/", StringComparison.OrdinalIgnoreCase) &&
            path.Contains("/level/", StringComparison.OrdinalIgnoreCase);
        var mapTexture = extension == ".tex" && path.StartsWith("ui/map/", StringComparison.OrdinalIgnoreCase);
        if (scene || mapTexture)
        {
            IReadOnlyList<NamedMapEntry> maps;
            try { maps = await source.ListNamedMapsAsync(token); }
            catch (Exception ex) when (ex is IOException or ArgumentException or InvalidDataException or NotSupportedException)
            { maps = []; }
            var map = maps.FirstOrDefault(candidate => string.Equals(scene ? candidate.ScenePath : candidate.MapTexturePath,
                path, StringComparison.OrdinalIgnoreCase));
            if (map != null) return new(SqPackPage.Maps, path, map, mapTexture);
            if (scene)
            {
                var layout = new NamedMapEntry(0, 0, Path.GetFileNameWithoutExtension(path), "", "", "", 0,
                    "", "", path[3..^4], path, "", null, 0, 0, 0, false);
                return new(SqPackPage.Maps, path, layout);
            }
        }
        if (page.FileType == "Model") return new(SqPackPage.Models, page.Path);
        if (page.FileType == "Texture") return new(SqPackPage.Images, page.Path);
        if (page.Bytes.AsSpan().StartsWith("SEDBSSCF"u8)) return new(SqPackPage.Audio, page.Path);
        if (FindSheetName(source.SheetNames, path, extension) is { } sheet) return new(SqPackPage.Tables, sheet);
        return null;
    }

    private static string? FindSheetName(IReadOnlyList<string> sheets, string path, string extension)
    {
        if (extension is not (".exh" or ".exd") || !path.StartsWith("exd/", StringComparison.OrdinalIgnoreCase)) return null;
        var name = path[4..^4];
        if (extension == ".exd")
        {
            var separator = name.LastIndexOf('_');
            if (separator <= 0) return null;
            if (!uint.TryParse(name.AsSpan(separator + 1), out _))
            {
                var languageSeparator = separator;
                separator = name.LastIndexOf('_', languageSeparator - 1);
                if (separator < 0 || !uint.TryParse(name.AsSpan(separator + 1, languageSeparator - separator - 1), out _)) return null;
            }
            name = name[..separator];
        }
        return sheets.FirstOrDefault(sheet => string.Equals(sheet, name, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<bool> OpenFileTarget(FileTarget target)
    {
        switch (target.Page)
        {
            case SqPackPage.Tables:
                await OpenLocalTableAsync(target.Path);
                return CurrentTableName == target.Path && tablePage != null;
            case SqPackPage.Images:
                if (CurrentTexturePath != target.Path) await ShowLocalTexture(target.Path, remember: false);
                return CurrentTexturePath == target.Path;
            case SqPackPage.Audio:
                if (!LocalAudioPanel.IsPreviewingPath(target.Path)) await LocalAudioPanel.OpenAsync(target.Path);
                return LocalAudioPanel.IsPreviewingPath(target.Path);
            case SqPackPage.Models:
                if (!LocalModelsPanel.IsPreviewingPath(target.Path)) await LocalModelsPanel.OpenIndexedFileAsync(target.Path);
                return LocalModelsPanel.IsPreviewingPath(target.Path);
            case SqPackPage.Maps:
                await LocalMapsPanel.OpenMapFileAsync(target.Map!, target.MapImage);
                return LocalMapsPanel.HasFilePreview;
            default:
                return false;
        }
    }

    private static string FilePreviewName(SqPackPage page) => page switch
    {
        SqPackPage.Tables => "表格", SqPackPage.Images => "图片", SqPackPage.Models => "模型",
        SqPackPage.Maps => "地图", SqPackPage.Audio => "音频", _ => "文件"
    };

    private void UpdateFileButtons()
    {
        var ready = fileRequest == null && fileBytePage != null;
        CopyFilePathButton.IsEnabled = fileEntry != null && !copyingFilePath;
        SwitchFilePreviewButton.Visibility = JumpToFilePreviewButton.Visibility = fileTarget != null ? Visibility.Visible : Visibility.Collapsed;
        SwitchFilePreviewButton.IsEnabled = ready;
        JumpToFilePreviewButton.IsEnabled = ready;
        SwitchFilePreviewButton.Content = showFileHex ? "查看预览" : "查看 Hex";
        JumpToFilePreviewButton.Content = fileTarget is { } target ? $"打开{FilePreviewName(target.Page)}页" : "打开预览页";
        FileHexNavigation.Visibility = FileHexPanel.Visibility == Visibility.Visible && fileBytePage is { Size: > 0 }
            ? Visibility.Visible : Visibility.Collapsed;
        FileHexNavigation.IsEnabled = ready;
        PreviousFileHexButton.IsEnabled = fileBytePage is { Offset: > 0 };
        NextFileHexButton.IsEnabled = fileBytePage is { Truncated: true };
    }

    private void SwitchFilePreview(object sender, RoutedEventArgs e)
    {
        if (fileTarget == null) return;
        showFileHex = !showFileHex;
        UpdateVisiblePanels();
        if (!showFileHex)
        {
            SetFilePreviewIssue(null);
            ResumeFilePreview();
        }
        UpdateFileButtons();
    }

    private async void JumpToFilePreview(object sender, RoutedEventArgs e)
    {
        if (fileTarget is not { } target) return;
        try
        {
            PageRequested?.Invoke(target.Page);
            if (!await OpenFileTarget(target)) SetStatus($"此文件暂时无法显示{FilePreviewName(target.Page)}预览。", true);
        }
        catch (Exception ex) { SetStatus("无法打开预览 · " + ex.Message, true); }
    }

    private async void CopyFilePath(object sender, RoutedEventArgs e)
    {
        if (copyingFilePath || fileEntry is not { } entry || PresentationSource.FromVisual(this) is not HwndSource source) return;
        var path = entry.KnownPath ?? fileBytePage?.Path ?? entry.Locator;
        copyingFilePath = true;
        CopyFilePathButton.IsEnabled = false;
        try
        {
            MainWindow.Notify(this, await ClipboardText.TryCopyAsync(source.Handle, path)
                ? "已复制路径" : "剪贴板正被占用，请稍后重试。");
        }
        catch (Win32Exception ex) { MainWindow.Notify(this, "无法复制路径 · " + ex.Message); }
        finally
        {
            copyingFilePath = false;
            CopyFilePathButton.IsEnabled = fileEntry != null;
        }
    }

    private void PreviousFileHex(object sender, RoutedEventArgs e)
    {
        if (fileBytePage is { } page) BeginFileRead(Math.Max(0, page.Offset - FileHexPageSize));
    }

    private void NextFileHex(object sender, RoutedEventArgs e)
    {
        if (fileBytePage is { Truncated: true } page) BeginFileRead(page.Offset + page.Bytes.Length);
    }

    private void GoToFileOffset(object sender, RoutedEventArgs e) => ReadFileOffset();

    private void FileOffsetKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        ReadFileOffset();
    }

    private void ReadFileOffset()
    {
        if (fileBytePage is not { Size: > 0 } page) return;
        var text = FileOffsetInput.Text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        if (long.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var offset) && offset >= 0)
            BeginFileRead(Math.Min(offset, page.Size - 1));
        else FileOffsetInput.SelectAll();
    }

    private void RenderFileHex(SqPackFileSystemPreview page)
    {
        FileOffsetInput.Text = page.Offset.ToString("X", CultureInfo.InvariantCulture);
        if (page.Bytes.Length == 0)
        {
            FileHexText.Text = "此文件没有可显示的字节。";
            return;
        }
        var builder = new StringBuilder(page.Bytes.Length * 5);
        for (var row = 0; row < page.Bytes.Length; row += 16)
        {
            var length = Math.Min(16, page.Bytes.Length - row);
            builder.Append($"{page.Offset + row:X12}  ");
            for (var column = 0; column < 16; column++)
            {
                if (column == 8) builder.Append(' ');
                if (column < length) builder.Append($"{page.Bytes[row + column]:X2} ");
                else builder.Append("   ");
            }
            builder.Append("  ");
            foreach (var value in page.Bytes.AsSpan(row, length)) builder.Append(value is >= 32 and <= 126 ? (char)value : '·');
            builder.AppendLine();
        }
        FileHexText.Text = builder.ToString();
        FileHexText.ScrollToHome();
    }
}
