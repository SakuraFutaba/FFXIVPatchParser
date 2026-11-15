using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
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
    private CancellationTokenSource? fileRequest;
    private Task filePreviewTask = Task.CompletedTask;

    private void UpdateFileLayout(bool show)
    {
        if (show == (FileTree.Visibility == Visibility.Visible)) return;
        if (!show) fileTreeWidth = FileTreeColumn.Width;
        FileTree.Visibility = FileSplitter.Visibility = FileDetails.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        FileTreeColumn.MinWidth = show ? 180 : 0;
        FileTreeColumn.MaxWidth = show ? 470 : double.PositiveInfinity;
        FileTreeColumn.Width = show ? fileTreeWidth : new(0);
        FileDividerColumn.Width = new(show ? 8 : 0);
        TableTreeColumn.Width = new(show ? 0 : 196);
        ImageTreeColumn.Width = new(show ? 0 : 310);
        TableTreeDivider.Width = ImageTreeDivider.Width = new(show ? 0 : 8);
        TableTreePanel.Visibility = LocalImageTreePanel.Visibility = LocalImagePathPanel.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
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
        LocalImagesPanel.Visibility = Visible(SqPackPage.Images);
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
            var target = first ? await FindFileTarget(source, entry, page, token) : fileTarget;
            token.ThrowIfCancellationRequested();
            fileTarget = target;
            fileBytePage = page;
            FileInfo.Text = $"{Workspace.Size(page.Size)} · {FileAddressLabel(entry)}";
            RenderFileHex(page);
            UpdateVisiblePanels();
            UpdateFileButtons();
            if (!showFileHex && target != null) await OpenFileTarget(target);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested) return;
            showFileHex = true;
            FileHexText.Text = "无法读取文件 · " + ex.Message;
            UpdateVisiblePanels();
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

    private Task OpenFileTarget(FileTarget target) => target.Page switch
    {
        SqPackPage.Tables => OpenLocalTableAsync(target.Path),
        SqPackPage.Images => CurrentTexturePath == target.Path ? Task.CompletedTask : ShowLocalTexture(target.Path, remember: false),
        SqPackPage.Audio => LocalAudioPanel.IsPreviewingPath(target.Path) ? Task.CompletedTask : LocalAudioPanel.OpenAsync(target.Path),
        SqPackPage.Models => LocalModelsPanel.IsPreviewingPath(target.Path) ? Task.CompletedTask : LocalModelsPanel.OpenIndexedFileAsync(target.Path),
        SqPackPage.Maps => LocalMapsPanel.OpenMapFileAsync(target.Map!, target.MapImage),
        _ => Task.CompletedTask
    };

    private void UpdateFileButtons()
    {
        var ready = fileRequest == null && fileBytePage != null;
        CopyFilePathButton.IsEnabled = fileEntry != null;
        SwitchFilePreviewButton.Visibility = JumpToFilePreviewButton.Visibility = fileTarget != null ? Visibility.Visible : Visibility.Collapsed;
        SwitchFilePreviewButton.IsEnabled = ready;
        JumpToFilePreviewButton.IsEnabled = ready;
        SwitchFilePreviewButton.Content = showFileHex ? "查看预览" : "查看 Hex";
        JumpToFilePreviewButton.Content = fileTarget?.Page switch
        {
            SqPackPage.Tables => "打开表格页", SqPackPage.Images => "打开图片页", SqPackPage.Models => "打开模型页",
            SqPackPage.Maps => "打开地图页", SqPackPage.Audio => "打开音频页", _ => "打开预览页"
        };
        FileHexNavigation.Visibility = fileBytePage is { Size: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        FileHexNavigation.IsEnabled = ready;
        PreviousFileHexButton.IsEnabled = fileBytePage is { Offset: > 0 };
        NextFileHexButton.IsEnabled = fileBytePage is { Truncated: true };
    }

    private void SwitchFilePreview(object sender, RoutedEventArgs e)
    {
        if (fileTarget == null) return;
        showFileHex = !showFileHex;
        UpdateVisiblePanels();
        if (!showFileHex) ResumeFilePreview();
        UpdateFileButtons();
    }

    private async void JumpToFilePreview(object sender, RoutedEventArgs e)
    {
        if (fileTarget is not { } target) return;
        PageRequested?.Invoke(target.Page);
        await OpenFileTarget(target);
    }

    private void CopyFilePath(object sender, RoutedEventArgs e)
    {
        if (fileEntry is not { } entry) return;
        try { Clipboard.SetText(entry.KnownPath ?? fileBytePage?.Path ?? entry.Locator); }
        catch (System.Runtime.InteropServices.ExternalException ex) { SetStatus("无法复制路径 · " + ex.Message, true); }
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
