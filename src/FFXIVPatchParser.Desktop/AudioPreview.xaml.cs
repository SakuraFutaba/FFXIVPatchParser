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
    private readonly DispatcherTimer transportSync = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private AudioPreview? mirroredTransport;
    private LocalGameSource? source;
    private CancellationTokenSource? request;
    private bool playAfterRead;
    private CancellationTokenSource? listRequest;
    private CancellationTokenSource? conversion;
    private GameAudio? audio;
    private MediaPlayer? player;
    private PcmLoopPlayer? pcmPlayer;
    private string? temporaryFile;
    private string? selectedPath;
    private string? pendingReveal;
    private bool updatingControls;
    private bool playerConverted;
    private IReadOnlyList<string>? catalogPaths;
    private IReadOnlyDictionary<string, MusicTrackText>? catalogTitles;
    private bool catalogScanned;
    private int catalogRevision = -1;
    private CancellationTokenSource? catalogFilter;
    private int playbackVersion;
    private bool playbackEnded;
    internal bool KeepPlaybackAlive { get; set; }
    private bool loopTrack;
    internal bool LoopTrack
    {
        get => loopTrack;
        set { loopTrack = value; pcmPlayer?.SetLoop(value); }
    }
    internal event EventHandler? PlaybackCompleted;
    internal bool IsPlaying => PlayButton.IsPlaying;
    internal bool CanPlay => PlayButton.IsEnabled;
    internal bool CanSeek => DurationSeconds > 0 && PositionSlider.IsEnabled;
    internal double PositionSeconds => pcmPlayer?.Position ?? player?.Position.TotalSeconds ?? 0;
    internal double DurationSeconds => pcmPlayer?.Duration ?? (player is { NaturalDuration: { HasTimeSpan: true } duration }
        ? duration.TimeSpan.TotalSeconds : 0);
    internal string PlaybackStatus => Status.Text;
    internal (double Start, double End)? RecordedLoop => pcmPlayer?.RecordedLoop;
    internal GameAudio? SelectedAudio => audio;
    internal double PlaybackVolume { get; set; } = 0.6;
    internal void SetVolume(double value)
    {
        PlaybackVolume = value;
        if (player != null) player.Volume = value;
        pcmPlayer?.SetVolume(value);
    }
    internal void ShutdownPlayback() => ReleasePlayer();
    internal bool MatchesSelection(LocalGameSource game, GameAudio selected) =>
        ReferenceEquals(source, game) && audio?.Path.Equals(selected.Path, StringComparison.OrdinalIgnoreCase) == true &&
        audio.TrackIndex == selected.TrackIndex;

    // 来源预览只持有控制端点；选择变化或页面隐藏不改变全局播放生命周期。
    private AudioPreview? GlobalTransport => !KeepPlaybackAlive && source is { } game && audio is { } selected
        ? GlobalMusicPlayer.Current?.TransportFor(game, selected) : null;

    private void SyncGlobalTransport()
    {
        if (KeepPlaybackAlive) return;
        var transport = GlobalTransport;
        if (transport is null)
        {
            if (mirroredTransport is null) return;
            mirroredTransport = null;
            PlayButton.IsPlaying = false;
            PlayButton.IsEnabled = audio is not null;
            StopButton.IsEnabled = PositionSlider.IsEnabled = false;
            UpdatePosition();
            if (audio is not null) Status.Text = "";
            return;
        }
        mirroredTransport = transport;
        PlayButton.IsPlaying = transport.IsPlaying;
        PlayButton.IsEnabled = transport.CanPlay;
        StopButton.IsEnabled = transport.StopButton.IsEnabled;
        PositionSlider.IsEnabled = transport.CanSeek;
        updatingControls = true;
        try
        {
            PositionSlider.Maximum = transport.PositionSlider.Maximum;
            if (!PositionSlider.IsMouseCaptureWithin)
                PositionSlider.Value = transport.PositionSeconds;
        }
        finally { updatingControls = false; }
        TimeLabel.Text = transport.TimeLabel.Text;
        Status.Text = transport.PlaybackStatus;
        Status.ToolTip = transport.Status.ToolTip;
    }

    internal async Task PlaySelectionAsync(LocalGameSource game, GameAudio selected)
    {
        if (MatchesSelection(game, selected))
        {
            await StartPlaybackAsync();
            return;
        }
        request?.Cancel();
        ReleasePlayer();
        source = game;
        audio = selected;
        selectedPath = selected.Path;
        AudioDetails.Text = selected.Path;
        SetAudioControls();
        await StartPlaybackAsync();
    }

    public AudioPreview()
    {
        InitializeComponent();
        transportSync.Tick += (_, _) => SyncGlobalTransport();
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
            if (value && (IsVisible || KeepPlaybackAlive)) _ = LoadNamedPathsAsync();
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
        pendingReveal = null;
        audio = null;
        ReleasePlayer();
        SetAudioControls();
        AudioDetails.Text = "选择 .scd 文件以预览";
        catalogFilter?.Cancel();
        catalogPaths = null;
        catalogTitles = null;
        AudioSearch.Text = "";
        PathTreeView.Reset(AudioTree);
        CatalogStatus.Text = "";
        catalogScanned = false;
        AudioPath.Text = "";
        Status.Text = "";
        if (IsVisible && ShowBrowser) _ = LoadNamedPathsAsync();
    }

    public Task OpenAsync(string path)
    {
        AudioPath.Text = path;
        return LoadAudioAsync(path);
    }

    internal async Task RevealAsync(GameAudio selected)
    {
        pendingReveal = selected.Path;
        if (AudioSearch.Text.Length != 0) AudioSearch.Clear();
        _ = LoadNamedPathsAsync();
        RevealCatalogEntry();
        if (audio is not { } local || !string.Equals(local.Path, selected.Path, StringComparison.OrdinalIgnoreCase) ||
            local.TrackIndex != selected.TrackIndex)
        {
            AudioPath.Text = selected.Path;
            await LoadAudioAsync(selected.Path, selected.TrackIndex);
        }
        SyncGlobalTransport();
    }

    private void RevealCatalogEntry()
    {
        if (pendingReveal is not { } path || !IsVisible) return;
        var indexes = new List<int>();
        if (AudioTree.ItemsSource is IReadOnlyList<PathTreeNode> roots && FindPath(roots) &&
            PathTreeView.SelectItem(AudioTree, indexes, notify: false)) pendingReveal = null;

        bool FindPath(IReadOnlyList<PathTreeNode> nodes)
        {
            for (var i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                indexes.Add(i);
                if (node.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase) ||
                    node.IsDirectory && path.StartsWith(node.FullPath + "/", StringComparison.OrdinalIgnoreCase) && FindPath(node.Children))
                    return true;
                indexes.RemoveAt(indexes.Count - 1);
            }
            return false;
        }
    }

    internal void CancelFileOpen() => request?.Cancel();
    internal bool IsPreviewingPath(string path) => audio != null && selectedPath == path;

    internal Task OpenAndPlayAsync(string path)
    {
        AudioPath.Text = path;
        if (string.Equals(selectedPath, path, StringComparison.OrdinalIgnoreCase))
        {
            if (audio is not null) return PlayCurrentAsync(toggle: false);
            if (request is { IsCancellationRequested: false })
            {
                playAfterRead = true;
                return Task.CompletedTask;
            }
        }
        return LoadAudioAsync(path, playWhenLoaded: true);
    }

    private async Task LoadAudioAsync(string path, int? trackIndex = null, bool playWhenLoaded = false)
    {
        if (source is not { } game || string.IsNullOrWhiteSpace(path)) return;
        request?.Cancel();
        using var current = new CancellationTokenSource();
        request = current;
        playAfterRead = playWhenLoaded;
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
        var shouldPlay = false;
        try
        {
            var (result, title) = await Task.Run(() =>
            {
                var selected = game.ReadAudio(path, trackIndex, current.Token);
                var titles = MusicMetadata.ReadCatalogOrEmpty(game);
                return (selected, MusicMetadata.DescribePath(titles, selected.Path).Title);
            }, current.Token);
            if (current.IsCancellationRequested) return;
            audio = result;
            AudioDetails.ToolTip = result.Path;
            AudioDetails.Text = $"{title}\n{result.Codec} · {result.Channels} 声道 · {result.SampleRate:N0} Hz · {Workspace.Size(result.Data.LongLength)}";
            SetAudioControls();
            Status.Text = "";
            SyncGlobalTransport();
            shouldPlay = playAfterRead;
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
        if (shouldPlay) await PlayCurrentAsync(toggle: false);
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

    private Task LoadNamedPathsAsync() => (catalogPaths is null || catalogRevision != source?.MediaCatalogRevision) && listRequest is not { IsCancellationRequested: false }
        ? LoadCatalogAsync(false) : Task.CompletedTask;

    public Task ScanUnknownAsync(CancellationToken token = default) => LoadCatalogAsync(true, token);

    private async Task LoadCatalogAsync(bool scanUnknown, CancellationToken token = default)
    {
        if (source is not { } game) return;
        listRequest?.Cancel();
        using var current = CancellationTokenSource.CreateLinkedTokenSource(token);
        listRequest = current;
        var revision = game.MediaCatalogRevision;
        CatalogStatus.Text = scanUnknown ? "正在扫描音频索引…" : "正在读取已知音频目录…";
        var progress = new Progress<string>(message =>
        {
            if (ReferenceEquals(listRequest, current) && !current.IsCancellationRequested) CatalogStatus.Text = message;
        });
        try
        {
            var result = await Task.Run(() =>
            {
                var named = game.ListKnownLocalMediaLocations("audio", current.Token).Select(entry => entry.Path).ToArray();
                var paths = scanUnknown
                    ? named.Concat(game.ListAudioLocations(current.Token, progress: progress).Select(entry => entry.Path))
                        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray()
                    : named;
                IReadOnlyDictionary<string, MusicTrackText>? titles = null;
                try { titles = MusicMetadata.ReadCatalog(game); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException
                    or KeyNotFoundException or System.Text.Json.JsonException)
                {
                    // 曲名资料缺失时保留可打开的 SCD 文件目录。
                }
                return (paths, titles);
            }, current.Token);
            token.ThrowIfCancellationRequested();
            if (current.IsCancellationRequested || !ReferenceEquals(source, game)) return;
            catalogPaths = result.paths;
            catalogTitles = result.titles;
            catalogScanned = scanUnknown;
            catalogRevision = revision;
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
        var titles = catalogTitles;
        var search = AudioSearch.Text;
        try
        {
            var tree = await Task.Run(() => PathTree.Build(paths, path => path, search,
                path => titles?.GetValueOrDefault(path)?.SearchText ?? "", current.Token,
                leafLabelSelector: path => MusicMetadata.DirectoryLabel(path, titles?.GetValueOrDefault(path))), current.Token);
            if (current.IsCancellationRequested) return;
            PathTreeView.SetItems(AudioTree, tree, search, path => path == "music");
            RevealCatalogEntry();
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
        if (PathTreeView.IsRestoringSelection(AudioTree) ||
            e.NewValue is not PathTreeNode { IsDirectory: false } node) return;
        pendingReveal = null;
        await OpenAsync(node.FullPath);
    }

    internal static PathTreeNode? GetDoubleClickedFile(TreeView tree, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return null;
        for (var element = e.OriginalSource as Visual; element is not null && element != tree;
            element = VisualTreeHelper.GetParent(element) as Visual)
        {
            if (element is TreeViewItem item)
                return item.Header is PathTreeNode { IsDirectory: false } node && ReferenceEquals(tree.SelectedItem, node)
                    ? node : null;
        }
        return null;
    }

    private async void PlayAudioFile(object sender, MouseButtonEventArgs e)
    {
        if (GetDoubleClickedFile(AudioTree, e) is not { } node) return;
        e.Handled = true;
        await OpenAndPlayAsync(node.FullPath);
    }

    private async void PreviewLoaded(object sender, RoutedEventArgs e)
    {
        if (!KeepPlaybackAlive && IsVisible) { transportSync.Start(); SyncGlobalTransport(); }
        if (ShowBrowser && (IsVisible || KeepPlaybackAlive)) await LoadNamedPathsAsync();
    }

    private void PreviewUnloaded(object sender, RoutedEventArgs e)
    {
        transportSync.Stop();
        if (!KeepPlaybackAlive) ReleasePlayer();
    }

    private void PreviewVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsLoaded) return;
        if (IsVisible)
        {
            if (!KeepPlaybackAlive) { transportSync.Start(); SyncGlobalTransport(); }
            if (ShowBrowser) _ = LoadNamedPathsAsync();
        }
        else if (!KeepPlaybackAlive)
        {
            listRequest?.Cancel();
            transportSync.Stop();
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

    private async void PlayAudio(object sender, RoutedEventArgs e) => await PlayCurrentAsync(toggle: true);

    private async Task PlayCurrentAsync(bool toggle)
    {
        if (!KeepPlaybackAlive && source is { } game && audio is { } selection && GlobalMusicPlayer.Current is { } global)
        {
            if (global.TransportFor(game, selection) is { } transport)
            {
                if (toggle) await transport.TogglePlaybackAsync();
                else await transport.StartPlaybackAsync();
            }
            else await global.PlayAsync(game, selection);
            SyncGlobalTransport();
            return;
        }
        if (toggle) await TogglePlaybackAsync();
        else await StartPlaybackAsync();
    }

    internal Task TogglePlaybackAsync()
    {
        if (!CanPlay) return Task.CompletedTask;
        if (IsPlaying)
        {
            player?.Pause();
            pcmPlayer?.Pause();
            timer.Stop();
            PlayButton.IsPlaying = false;
            UpdatePosition();
            return Task.CompletedTask;
        }
        return StartPlaybackAsync();
    }

    private async Task StartPlaybackAsync()
    {
        if (!CanPlay || IsPlaying || audio is not { } selected) return;
        if (player is not null || pcmPlayer is not null)
        {
            ResumePlayback();
            return;
        }
        if ((selected.Codec == "HCA" || selected.Loop is not null && selected.Codec != "PCM 16-bit") && FindFfmpeg() is { } ffmpeg)
        {
            await PlayConvertedAsync(ffmpeg, selected);
            return;
        }
        if (selected.Codec == "HCA")
        {
            Status.Text = "播放 HCA 需要 FFmpeg。请将 ffmpeg.exe 加入 PATH，或导出 HCA 文件。";
            return;
        }
        PlayButton.IsEnabled = false;
        var version = playbackVersion;
        var file = Path.Combine(Workspace.Temp, "audio-preview", Guid.NewGuid().ToString("N") + selected.Extension);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllBytesAsync(file, selected.Data);
            if (version != playbackVersion || !ReferenceEquals(audio, selected) || (!IsVisible && !KeepPlaybackAlive)) return;
            Status.Text = "正在打开音轨…";
            if (selected.Codec == "PCM 16-bit" && selected.Loop is not null)
                OpenPcmPlayer(file, selected, converted: false);
            else OpenPlayer(file, converted: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            if (version == playbackVersion && ReferenceEquals(audio, selected))
            {
                ReleasePlayer();
                Status.Text = $"无法播放：{ex.Message}";
            }
        }
        finally
        {
            if (temporaryFile != file) DeleteTemporaryFile(file);
            if (version == playbackVersion && conversion is null) PlayButton.IsEnabled = true;
        }
    }

    private void OpenPcmPlayer(string file, GameAudio selected, bool converted)
    {
        var pcm = new PcmLoopPlayer(file, selected.Loop, selected.SampleRate);
        pcmPlayer = pcm;
        temporaryFile = file;
        playbackEnded = false;
        pcm.SetVolume(PlaybackVolume);
        pcm.SetLoop(LoopTrack);
        pcm.Ended += OnPlaybackEnded;
        pcm.Failed += ex => { ReleasePlayer(); ShowPlaybackFailure(ex.Message); };
        Status.Text = converted ? "正在播放 FFmpeg 转码预览" : "";
        updatingControls = true;
        try { PositionSlider.Maximum = pcm.Duration; }
        finally { updatingControls = false; }
        PositionSlider.IsEnabled = StopButton.IsEnabled = true;
        ResumePlayback();
        UpdatePosition();
    }

    private void OpenPlayer(string file, bool converted)
    {
        var media = new MediaPlayer { Volume = PlaybackVolume };
        media.MediaOpened += MediaOpened;
        media.MediaEnded += MediaEnded;
        media.MediaFailed += MediaFailed;
        temporaryFile = file;
        player = media;
        playbackEnded = false;
        playerConverted = converted;
        media.Open(new Uri(file, UriKind.Absolute));
        if (!ReferenceEquals(media, player)) return;
        ResumePlayback();
        StopButton.IsEnabled = true;
    }

    private void MediaOpened(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, player)) return;
        Status.Text = playerConverted ? "正在播放 FFmpeg 转码预览" : "";
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
        if (!wasConverted && (IsVisible || KeepPlaybackAlive) && audio is { } selected && FindFfmpeg() is { } executable)
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
        conversion = current;
        PlayButton.IsEnabled = false;
        Status.Text = "正在使用本机 FFmpeg 转码…";
        var prefix = Path.Combine(Workspace.Temp, "audio-preview", Guid.NewGuid().ToString("N"));
        var input = prefix + selected.Extension;
        var output = prefix + "-preview.wav";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(input)!);
            await File.WriteAllBytesAsync(input, selected.Data, current.Token);
            await ConvertToWaveAsync(executable, input, output, current.Token);
            if (!current.IsCancellationRequested && ReferenceEquals(audio, selected) && (IsVisible || KeepPlaybackAlive))
            {
                if (selected.Codec == "HCA" || selected.Loop is not null) OpenPcmPlayer(output, selected, converted: true);
                else OpenPlayer(output, converted: true);
            }
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested) { }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        {
            if (ReferenceEquals(conversion, current) && ReferenceEquals(audio, selected) && (IsVisible || KeepPlaybackAlive))
            {
                ReleasePlayer();
                ShowPlaybackFailure(ex.Message);
            }
        }
        finally
        {
            DeleteTemporaryFile(input);
            if (temporaryFile != output) DeleteTemporaryFile(output);
            if (ReferenceEquals(conversion, current))
            {
                conversion = null;
                PlayButton.IsEnabled = true;
            }
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
            "-f", Path.GetExtension(input)[1..], "-i", input, "-map", "0:a:0",
            "-ac", "2", "-c:a", "pcm_s16le", "-f", "wav", output
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
        if (!ReferenceEquals(sender, player)) return;
        OnPlaybackEnded();
    }

    private void OnPlaybackEnded()
    {
        playbackEnded = true;
        timer.Stop();
        PlayButton.IsPlaying = false;
        UpdatePosition();
        if (LoopTrack) ResumePlayback();
        else PlaybackCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void ResumePlayback()
    {
        if (pcmPlayer is { } pcm)
        {
            if (playbackEnded) pcm.Seek(0);
            playbackEnded = false;
            timer.Start();
            PlayButton.IsPlaying = true;
            pcm.Play();
            return;
        }
        if (playbackEnded && temporaryFile is { } file)
        {
            // 自然结束后的解码器可能停在 EOF；重新打开同一缓存，保留暂停时的寻址行为。
            var converted = playerConverted;
            var previous = player;
            player = null;
            previous?.Close();
            OpenPlayer(file, converted);
            return;
        }
        if (player is null) return;
        player.Play();
        timer.Start();
        PlayButton.IsPlaying = true;
    }

    private void StopAudio(object sender, RoutedEventArgs e)
    {
        (GlobalTransport ?? this).ResetPosition();
        SyncGlobalTransport();
    }

    private void ResetPosition()
    {
        player?.Pause();
        if (pcmPlayer is { } pcm) { pcm.Pause(); pcm.Seek(0); }
        if (player is not null) player.Position = TimeSpan.Zero;
        playbackEnded = false;
        timer.Stop();
        PlayButton.IsPlaying = false;
        UpdatePosition();
    }

    private void UpdatePosition()
    {
        var elapsed = TimeSpan.FromSeconds(PositionSeconds);
        updatingControls = true;
        try { PositionSlider.Value = elapsed.TotalSeconds; }
        finally { updatingControls = false; }
        UpdateTimeLabel(elapsed);
    }

    private void UpdateTimeLabel(TimeSpan elapsed)
    {
        TimeLabel.Text = DurationSeconds > 0
            ? $"{FormatTime(elapsed)} / {FormatTime(TimeSpan.FromSeconds(DurationSeconds))}" : FormatTime(elapsed);
    }

    private static string FormatTime(TimeSpan value) => $"{(int)value.TotalMinutes}:{value.Seconds:D2}";

    private void SeekAudio(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (updatingControls || !PositionSlider.IsEnabled) return;
        (GlobalTransport ?? this).SeekPlayback(e.NewValue);
        SyncGlobalTransport();
    }

    internal void SeekPlayback(double seconds)
    {
        if (!CanSeek || !double.IsFinite(seconds)) return;
        playbackEnded = false;
        if (pcmPlayer is { } pcm) pcm.Seek(seconds);
        else if (player is { } media) media.Position = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, DurationSeconds));
        UpdatePosition();
    }

    private void ReleasePlayer()
    {
        playbackVersion++;
        mirroredTransport = null;
        conversion?.Cancel();
        conversion = null;
        timer.Stop();
        var previous = player;
        player = null;
        previous?.Close();
        pcmPlayer?.Dispose();
        pcmPlayer = null;
        if (temporaryFile is { } file)
        {
            temporaryFile = null;
            DeleteTemporaryFile(file);
        }
        PlayButton.IsPlaying = false;
        PlayButton.IsEnabled = audio is not null;
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
