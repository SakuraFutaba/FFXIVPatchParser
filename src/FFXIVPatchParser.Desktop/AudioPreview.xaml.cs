using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace FFXIVPatchParser.Desktop;

public partial class AudioPreview : UserControl
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private LocalGameSource? source;
    private CancellationTokenSource? request;
    private CancellationTokenSource? listRequest;
    private CancellationTokenSource? conversion;
    private GameAudio? audio;
    private MediaPlayer? player;
    private string? temporaryFile;
    private string? selectedPath;
    private bool updatingControls;
    private bool playerConverted;
    private IReadOnlyList<string>? catalogPaths;
    private bool catalogScanned;
    private CancellationTokenSource? catalogFilter;
    private int playbackVersion;

    public AudioPreview()
    {
        InitializeComponent();
        timer.Tick += (_, _) =>
        {
            if (!PositionSlider.IsMouseCaptureWithin) UpdatePosition();
        };
    }

    public bool ShowBrowser
    {
        get => Browser.Visibility == Visibility.Visible;
        set
        {
            Browser.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            PathInput.Visibility = Browser.Visibility;
            BrowserColumn.Width = new GridLength(value ? 318 : 0);
            if (value && IsVisible) _ = LoadNamedPathsAsync();
        }
    }

    public void SetSource(LocalGameSource? source)
    {
        if (ReferenceEquals(this.source, source)) return;
        request?.Cancel();
        listRequest?.Cancel();
        listRequest = null;
        this.source = source;
        selectedPath = null;
        audio = null;
        ReleasePlayer();
        SetAudioControls();
        AudioDetails.Text = "选择 .scd 文件以预览";
        catalogFilter?.Cancel();
        catalogPaths = null;
        AudioSearch.Text = "";
        AudioTree.ItemsSource = null;
        CatalogStatus.Text = "";
        catalogScanned = false;
        AudioPath.Text = "";
        Status.Text = "";
        if (IsVisible && ShowBrowser) _ = LoadNamedPathsAsync();
    }

    public Task OpenAsync(string path) => LoadAudioAsync(path);

    private async Task LoadAudioAsync(string path, int? trackIndex = null)
    {
        if (source is not { } game || string.IsNullOrWhiteSpace(path)) return;
        request?.Cancel();
        using var current = new CancellationTokenSource();
        request = current;
        ReleasePlayer();
        selectedPath = path;
        audio = null;
        if (trackIndex is null) SetAudioControls();
        else
        {
            PlayButton.IsEnabled = false;
            SaveButton.IsEnabled = false;
        }
        AudioDetails.Text = path;
        Status.ToolTip = null;
        Status.Text = "正在读取音轨…";
        try
        {
            var result = await Task.Run(() => game.ReadAudio(path, trackIndex, current.Token), current.Token);
            if (current.IsCancellationRequested) return;
            audio = result;
            AudioDetails.Text = $"{path}\n{result.Codec} · {result.Channels} 声道 · {result.SampleRate:N0} Hz · {Workspace.Size(result.Data.LongLength)}";
            SetAudioControls();
            Status.Text = "";
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or NotSupportedException or OverflowException or IndexOutOfRangeException)
        {
            if (!current.IsCancellationRequested) Status.Text = $"读取音轨失败：{ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(request, current)) request = null;
        }
    }

    private void SetAudioControls()
    {
        PlayButton.IsEnabled = SaveButton.IsEnabled = audio is not null;
        updatingControls = true;
        try
        {
            TrackSelector.ItemsSource = audio is null ? null : Enumerable.Range(1, audio.TrackCount).ToArray();
            TrackSelector.SelectedIndex = audio?.TrackIndex ?? -1;
            TrackSelector.IsEnabled = audio?.TrackCount > 1;
        }
        finally { updatingControls = false; }
    }

    private Task LoadNamedPathsAsync() => catalogPaths is null && listRequest is null
        ? LoadCatalogAsync(false) : Task.CompletedTask;

    public Task ScanUnknownAsync(CancellationToken token = default) => LoadCatalogAsync(true, token);

    private async Task LoadCatalogAsync(bool scanUnknown, CancellationToken token = default)
    {
        if (source is not { } game) return;
        listRequest?.Cancel();
        using var current = CancellationTokenSource.CreateLinkedTokenSource(token);
        listRequest = current;
        CatalogStatus.Text = scanUnknown ? "正在扫描音频索引…" : "正在读取已知音频目录…";
        var progress = new Progress<string>(message =>
        {
            if (ReferenceEquals(listRequest, current) && !current.IsCancellationRequested) CatalogStatus.Text = message;
        });
        try
        {
            var paths = await Task.Run(() =>
            {
                var named = game.ListNamedAudioPaths(current.Token);
                return scanUnknown
                    ? named.Concat(game.ListAudioLocations(current.Token, progress: progress).Select(entry => entry.Path))
                        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray()
                    : named;
            }, current.Token);
            token.ThrowIfCancellationRequested();
            if (current.IsCancellationRequested || !ReferenceEquals(source, game)) return;
            catalogPaths = paths;
            catalogScanned = scanUnknown;
            await RefreshCatalogAsync();
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested)
        {
            if (ReferenceEquals(listRequest, current)) await RefreshCatalogAsync();
            if (scanUnknown && token.IsCancellationRequested) throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
            or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            if (!current.IsCancellationRequested) CatalogStatus.Text = $"读取音频目录失败：{ex.Message}";
            if (scanUnknown) throw;
        }
        finally
        {
            if (ReferenceEquals(listRequest, current)) listRequest = null;
        }
    }

    private async Task RefreshCatalogAsync()
    {
        catalogFilter?.Cancel();
        using var current = new CancellationTokenSource();
        catalogFilter = current;
        var paths = catalogPaths ?? [];
        var search = AudioSearch.Text;
        try
        {
            var tree = await Task.Run(() => PathTree.Build(paths, path => path, search, token: current.Token), current.Token);
            if (current.IsCancellationRequested) return;
            AudioTree.ItemsSource = tree;
            CatalogStatus.Text = $"{tree.Sum(node => node.LeafCount):N0} / {paths.Count:N0} 个音频" +
                (catalogScanned ? "（含未识别路径）" : "（已知路径）");
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(catalogFilter, current)) catalogFilter = null;
        }
    }

    private async void FilterAudio(object sender, TextChangedEventArgs e)
    {
        if (AudioTree is not null && catalogPaths is not null) await RefreshCatalogAsync();
    }

    private async void SelectAudio(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not PathTreeNode { IsDirectory: false } node) return;
        AudioPath.Text = node.FullPath;
        await OpenAsync(node.FullPath);
    }

    private async void PreviewLoaded(object sender, RoutedEventArgs e)
    {
        if (ShowBrowser && IsVisible) await LoadNamedPathsAsync();
    }

    private void PreviewUnloaded(object sender, RoutedEventArgs e) => ReleasePlayer();

    private void PreviewVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsLoaded) return;
        if (IsVisible)
        {
            if (ShowBrowser) _ = LoadNamedPathsAsync();
        }
        else
        {
            if (player is not null || conversion is not null) Status.Text = "";
            ReleasePlayer();
        }
    }

    private async void OpenEnteredPath(object sender, RoutedEventArgs e) => await OpenAsync(AudioPath.Text);

    private async void EnterPath(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await OpenAsync(AudioPath.Text);
    }

    private async void SelectTrack(object sender, SelectionChangedEventArgs e)
    {
        if (!updatingControls && selectedPath is { } path && TrackSelector.SelectedIndex >= 0)
            await LoadAudioAsync(path, TrackSelector.SelectedIndex);
    }

    private async void PlayAudio(object sender, RoutedEventArgs e)
    {
        if (audio is not { } selected) return;
        if (player is not null)
        {
            if (PlayButton.IsPlaying)
            {
                player.Pause();
                timer.Stop();
                PlayButton.IsPlaying = false;
                UpdatePosition();
            }
            else ResumePlayback();
            return;
        }
        PlayButton.IsEnabled = false;
        var version = playbackVersion;
        var file = Path.Combine(Workspace.Temp, "audio-preview", Guid.NewGuid().ToString("N") + selected.Extension);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllBytesAsync(file, selected.Data);
            if (version != playbackVersion || !ReferenceEquals(audio, selected) || !IsVisible) return;
            Status.Text = "正在打开音轨…";
            OpenPlayer(file, converted: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            if (ReferenceEquals(audio, selected))
            {
                ReleasePlayer();
                Status.Text = $"无法播放：{ex.Message}";
            }
        }
        finally
        {
            if (temporaryFile != file) DeleteTemporaryFile(file);
            if (ReferenceEquals(audio, selected) && conversion is null) PlayButton.IsEnabled = true;
        }
    }

    private void OpenPlayer(string file, bool converted)
    {
        var media = new MediaPlayer { Volume = 0.6 };
        media.MediaOpened += MediaOpened;
        media.MediaEnded += MediaEnded;
        media.MediaFailed += MediaFailed;
        temporaryFile = file;
        player = media;
        playerConverted = converted;
        media.Open(new Uri(file, UriKind.Absolute));
        if (!ReferenceEquals(media, player)) return;
        ResumePlayback();
        StopButton.IsEnabled = true;
    }

    private void MediaOpened(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, player)) return;
        Status.Text = playerConverted ? "正在播放 FFmpeg 转码预览（最多 5 分钟）" : "";
        if (player!.NaturalDuration.HasTimeSpan && player.NaturalDuration.TimeSpan > TimeSpan.Zero)
        {
            PositionSlider.Maximum = player.NaturalDuration.TimeSpan.TotalSeconds;
            PositionSlider.IsEnabled = true;
        }
        UpdatePosition();
    }

    private async void MediaFailed(object? sender, ExceptionEventArgs e)
    {
        if (!ReferenceEquals(sender, player)) return;
        var wasConverted = playerConverted;
        ReleasePlayer();
        if (!wasConverted && IsVisible && audio is { } selected && FindFfmpeg() is { } executable)
            await PlayConvertedAsync(executable, selected);
        else ShowPlaybackFailure(e.ErrorException.Message);
    }

    private void ShowPlaybackFailure(string detail)
    {
        Status.Text = "无法播放此音轨。可导出后使用支持该编码的播放器。";
        Status.ToolTip = detail;
    }

    private async Task PlayConvertedAsync(string executable, GameAudio selected)
    {
        using var current = new CancellationTokenSource();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(current.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        conversion = current;
        PlayButton.IsEnabled = false;
        Status.Text = "正在使用本机 FFmpeg 转码…";
        var prefix = Path.Combine(Workspace.Temp, "audio-preview", Guid.NewGuid().ToString("N"));
        var input = prefix + selected.Extension;
        var output = prefix + "-preview.wav";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(input)!);
            await File.WriteAllBytesAsync(input, selected.Data, timeout.Token);
            await ConvertToWaveAsync(executable, input, output, timeout.Token);
            if (!current.IsCancellationRequested && ReferenceEquals(audio, selected) && IsVisible)
                OpenPlayer(output, converted: true);
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested) { }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        {
            if (ReferenceEquals(audio, selected) && IsVisible)
            {
                ReleasePlayer();
                ShowPlaybackFailure(ex is OperationCanceledException ? "FFmpeg 转码超时。" : ex.Message);
            }
        }
        finally
        {
            if (ReferenceEquals(conversion, current)) conversion = null;
            DeleteTemporaryFile(input);
            if (temporaryFile != output) DeleteTemporaryFile(output);
            if (ReferenceEquals(audio, selected) && conversion is null) PlayButton.IsEnabled = true;
        }
    }

    private static string? FindFfmpeg()
    {
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var directory = entry.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(directory)) continue;
            var executable = Path.Combine(directory, "ffmpeg.exe");
            if (File.Exists(executable)) return executable;
        }
        return null;
    }

    private static async Task ConvertToWaveAsync(string executable, string input, string output, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
        {
            "-nostdin", "-hide_banner", "-loglevel", "error", "-protocol_whitelist", "file",
            "-f", Path.GetExtension(input)[1..], "-i", input, "-map", "0:a:0", "-t", "300",
            "-ac", "2", "-ar", "48000", "-c:a", "pcm_s16le", "-f", "wav", output
        })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("无法启动 FFmpeg。");
        var stderr = process.StandardError.ReadToEndAsync(token);
        try
        {
            await process.WaitForExitAsync(token);
            var diagnostic = await stderr;
            if (process.ExitCode != 0 || !File.Exists(output))
                throw new IOException(string.IsNullOrWhiteSpace(diagnostic) ? "FFmpeg 未生成可播放的文件。" : diagnostic.Trim());
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync();
            throw;
        }
    }

    private void MediaEnded(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, player)) ResetPosition();
    }

    private void ResumePlayback()
    {
        player?.Play();
        timer.Start();
        PlayButton.IsPlaying = true;
    }

    private void StopAudio(object sender, RoutedEventArgs e) => ResetPosition();

    private void ResetPosition()
    {
        player?.Stop();
        timer.Stop();
        PlayButton.IsPlaying = false;
        UpdatePosition();
    }

    private void UpdatePosition()
    {
        var elapsed = player?.Position ?? TimeSpan.Zero;
        updatingControls = true;
        try { PositionSlider.Value = elapsed.TotalSeconds; }
        finally { updatingControls = false; }
        UpdateTimeLabel(elapsed);
    }

    private void UpdateTimeLabel(TimeSpan elapsed)
    {
        TimeLabel.Text = player?.NaturalDuration.HasTimeSpan == true
            ? $"{FormatTime(elapsed)} / {FormatTime(player.NaturalDuration.TimeSpan)}" : FormatTime(elapsed);
    }

    private static string FormatTime(TimeSpan value) => $"{(int)value.TotalMinutes}:{value.Seconds:D2}";

    private void SeekAudio(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!updatingControls && PositionSlider.IsEnabled && player is not null)
        {
            var elapsed = TimeSpan.FromSeconds(e.NewValue);
            player.Position = elapsed;
            UpdateTimeLabel(elapsed);
        }
    }

    private void ReleasePlayer()
    {
        playbackVersion++;
        conversion?.Cancel();
        conversion = null;
        timer.Stop();
        var previous = player;
        player = null;
        previous?.Close();
        if (temporaryFile is { } file)
        {
            temporaryFile = null;
            DeleteTemporaryFile(file);
        }
        PlayButton.IsPlaying = false;
        StopButton.IsEnabled = PositionSlider.IsEnabled = false;
        UpdatePosition();
    }

    private static void DeleteTemporaryFile(string file)
    {
        try { File.Delete(file); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private async void SaveAudio(object sender, RoutedEventArgs e)
    {
        if (audio is not { } selected) return;
        var fileName = selected.Path.StartsWith("sqpack:", StringComparison.OrdinalIgnoreCase)
            ? selected.Path[(selected.Path.LastIndexOf('#') + 1)..] : Path.GetFileName(selected.Path);
        var dialog = new SaveFileDialog
        {
            Title = "导出音轨",
            Filter = $"音频 (*{selected.Extension})|*{selected.Extension}",
            DefaultExt = selected.Extension,
            FileName = Path.GetFileNameWithoutExtension(fileName) + (selected.TrackCount > 1 ? $"-{selected.TrackIndex + 1}" : "")
        };
        var owner = Window.GetWindow(this);
        if ((owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner)) != true) return;
        SaveButton.IsEnabled = false;
        var temporaryPath = Path.Combine(Path.GetDirectoryName(dialog.FileName)!, Path.GetRandomFileName() + ".partial");
        try
        {
            await using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                await file.WriteAsync(selected.Data);
            File.Move(temporaryPath, dialog.FileName, overwrite: true);
            if (ReferenceEquals(audio, selected)) Status.Text = $"已导出 {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (ReferenceEquals(audio, selected)) Status.Text = $"导出失败：{ex.Message}";
        }
        finally
        {
            DeleteTemporaryFile(temporaryPath);
            if (ReferenceEquals(audio, selected)) SaveButton.IsEnabled = true;
        }
    }
}
