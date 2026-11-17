using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Automation;

namespace FFXIVPatchParser.Desktop;

public partial class GlobalMusicPlayer : UserControl, IDisposable
{
    internal static GlobalMusicPlayer? Current { get; private set; }
    internal event Func<GameAudio?, Task>? AudioPageRequested;
    private async void OpenAudioPage(object sender, RoutedEventArgs e)
    {
        if (AudioPageRequested is { } open) await open(Playback.SelectedAudio);
    }
    private readonly List<MusicEntry> queue = [];
    private readonly MusicPlaybackOrder order = new();
    private readonly DispatcherTimer transportTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly DispatcherTimer favoriteFeedbackTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private bool syncingPosition;
    private double audibleVolume = 0.6;
    private const double WorkspaceRightInset = 8;
    private Window? queueHost;
    private readonly HashSet<string> favorites = new(StringComparer.OrdinalIgnoreCase);
    private LocalGameSource? source;
    private int currentIndex = -1;
    private int selectionVersion;
    private IReadOnlyList<MusicEntry>? library;
    private bool loadingLibrary;
    private int libraryVersion;
    private bool ShowingLibrary => LibraryMode?.SelectedIndex == 1;
    private string FavoritesPath => Path.Combine(Workspace.Temp, "music", "favorites.json");
    private sealed record MusicEntry(string Path, int Track, string Title, string Subtitle = "", string SearchText = "")
    {
        public string Key => Path + "#track=" + Track;
        public string Label => string.IsNullOrWhiteSpace(Title) || Title == System.IO.Path.GetFileName(Path) ? Path : Title;
    }

    public GlobalMusicPlayer()
    {
        InitializeComponent();
        Current = this;
        Playback.KeepPlaybackAlive = true;
        // 队列按路径和音轨区分，来源预览可以切换同一文件的音轨。
        Playback.PlaybackCompleted += async (_, _) => await SelectAsync(order.Next(currentIndex, queue.Count, naturalEnd: true));
        transportTimer.Tick += (_, _) => SyncTransport();
        transportTimer.Start();
        favoriteFeedbackTimer.Tick += (_, _) =>
        {
            favoriteFeedbackTimer.Stop();
            FavoriteFeedback.Text = "";
        };
        QueuePopup.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
            [new CustomPopupPlacement(new Point(targetSize.Width - popupSize.Width - WorkspaceRightInset, -popupSize.Height), PopupPrimaryAxis.None)];
        SizeChanged += (_, _) => ResizeQueue();
        Loaded += AttachQueueHost;
        Unloaded += DetachQueueHost;
        try
        {
            if (File.Exists(FavoritesPath))
                foreach (var item in JsonSerializer.Deserialize<List<MusicEntry>>(File.ReadAllText(FavoritesPath)) ?? [])
                    if (item is { Track: >= 0 } && !string.IsNullOrWhiteSpace(item.Path) && favorites.Add(item.Key))
                        queue.Add(item);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { QueueStatus.Text = "收藏读取失败：" + ex.Message; }
        Refresh();
    }

    internal AudioPreview? TransportFor(LocalGameSource game, GameAudio selected) =>
        Playback.MatchesSelection(game, selected) ? Playback : null;

    public void SetSource(LocalGameSource? game)
    {
        if (ReferenceEquals(source, game)) return;
        ++selectionVersion;
        source = game;
        ++libraryVersion;
        loadingLibrary = false;
        library = null;
        Refresh();
        if (game != null && queue.Count > 0) _ = RefreshQueueTitlesAsync(game);
        if (ShowingLibrary) _ = LoadLibraryAsync();
    }

    private async Task RefreshQueueTitlesAsync(LocalGameSource game)
    {
        var titles = await Task.Run(() => MusicMetadata.ReadCatalogOrEmpty(game));
        if (!ReferenceEquals(source, game) || !ReferenceEquals(Current, this)) return;
        for (var i = 0; i < queue.Count; i++)
        {
            var entry = queue[i];
            var text = MusicMetadata.DescribePath(titles, entry.Path);
            queue[i] = entry with { Title = text.Title, Subtitle = text.Subtitle, SearchText = text.SearchText };
        }
        if (currentIndex >= 0) UpdateTitle();
        else Refresh();
    }

    private async Task LoadLibraryAsync()
    {
        if (!ShowingLibrary || library is not null || loadingLibrary) return;
        if (source is not { } game) { QueueStatus.Text = "请先打开本地游戏数据源"; return; }
        var version = ++libraryVersion;
        loadingLibrary = true;
        Refresh();
        try
        {
            var rows = await Task.Run(() => MusicMetadata.ReadCatalog(game));
            if (version != libraryVersion || !ReferenceEquals(source, game)) return;
            library = rows.OrderBy(row => row.Value.Title, StringComparer.CurrentCultureIgnoreCase)
                .Select(row => new MusicEntry(row.Key, -1, row.Value.Title, row.Value.Subtitle, row.Value.SearchText)).ToArray();
            loadingLibrary = false;
            Refresh();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException
            or NotSupportedException or ArgumentException or JsonException or KeyNotFoundException)
        {
            if (version != libraryVersion) return;
            loadingLibrary = false;
            QueueStatus.Text = "读取本地曲库失败：" + ex.Message;
        }
    }

    public Task PlayAsync(LocalGameSource game, GameAudio audio)
    {
        order.Reset();
        return PlayCoreAsync(game, audio);
    }

    private async Task PlayCoreAsync(LocalGameSource game, GameAudio audio)
    {
        SetSource(game);
        var index = Enqueue(new(audio.Path, audio.TrackIndex, audio.Path));
        if (queue[index].Track < 0) queue[index] = queue[index] with { Track = audio.TrackIndex };
        currentIndex = index;
        var version = ++selectionVersion;
        UpdateTitle();
        await Playback.PlaySelectionAsync(game, audio);
        try
        {
            var title = await Task.Run(() => MusicMetadata.DescribeDetailed(game, audio.Path));
            if (version != selectionVersion) return;
            queue[index] = queue[index] with { Title = title.Title, Subtitle = title.Subtitle, SearchText = title.SearchText };
            UpdateTitle();
            if (favorites.Contains(queue[index].Key)) SaveFavorites();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException
            or NotSupportedException or ArgumentException or JsonException)
        { if (version == selectionVersion) QueueStatus.Text = "音频已加载，曲名读取失败。"; }
    }

    private async Task SelectAsync(int index)
    {
        if (source is not { } game || index < 0 || index >= queue.Count) return;
        var item = queue[index];
        var version = ++selectionVersion;
        QueueStatus.Text = "正在读取音轨…";
        try
        {
            var audio = await Task.Run(() => game.ReadAudio(item.Path, item.Track < 0 ? null : item.Track));
            if (version != selectionVersion) return;
            if (item.Track < 0) queue[index] = item with { Track = audio.TrackIndex };
            await PlayCoreAsync(game, audio);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException
            or NotSupportedException or ArgumentException or OverflowException or IndexOutOfRangeException)
        { if (version == selectionVersion) QueueStatus.Text = "无法读取音轨 · " + ex.Message; }
    }

    private void UpdateTitle()
    {
        if (currentIndex < 0) return;
        var current = queue[currentIndex];
        TrackTitle.Text = current.Label;
        TrackSubtitle.Text = current.Subtitle;
        OpenAudioPageButton.ToolTip = $"在 SqPack 音频中定位\n{current.Label}\n{current.Path}";
        FavoriteButton.IsEnabled = true;
        FavoriteButton.IsChecked = favorites.Contains(current.Key);
        FavoriteButton.ToolTip = FavoriteButton.IsChecked == true ? "取消收藏" : "收藏";
        AutomationProperties.SetName(FavoriteButton, FavoriteButton.ToolTip.ToString());
        Refresh();
    }
    private void Refresh()
    {
        if (QueueList == null || Search == null) return;
        var selected = QueueList.SelectedItem;
        var entries = ShowingLibrary ? library ?? [] : queue;
        var query = Search.Text.Trim();
        var visible = entries.Where(x => (LibraryMode.SelectedIndex != 2 || favorites.Contains(x.Key)) &&
            (x.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             x.Subtitle.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             x.SearchText?.Contains(query, StringComparison.OrdinalIgnoreCase) == true ||
             x.Path.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        QueueList.ItemsSource = visible;
        if (selected is MusicEntry item && visible.Contains(item)) QueueList.SelectedItem = item;
        AddToQueueButton.Visibility = ShowingLibrary ? Visibility.Visible : Visibility.Collapsed;
        QueueStatus.Text = ShowingLibrary
            ? loadingLibrary ? "正在读取本地曲库…" : $"本地曲库 {visible.Length}/{entries.Count} 首 · 双击播放"
            : $"{queue.Count} 首 · {favorites.Count} 首收藏 · 双击播放";
    }

    private int Enqueue(MusicEntry item)
    {
        var index = queue.FindIndex(entry => entry.Path.Equals(item.Path, StringComparison.OrdinalIgnoreCase) &&
            (entry.Track == item.Track || entry.Track < 0 || item.Track < 0));
        if (index >= 0) return index;
        queue.Add(item);
        order.Reset();
        return queue.Count - 1;
    }
    private void AddToQueue(object sender, RoutedEventArgs e)
    {
        if (QueueList.SelectedItem is not MusicEntry item) return;
        Enqueue(item);
        Refresh();
        QueueStatus.Text = $"已加入队列 · 共 {queue.Count} 首";
    }
    private async void LibraryModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (QueueList is null) return;
        Refresh();
        await LoadLibraryAsync();
    }

    private void SaveFavorites()
    {
        var path = FavoritesPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".partial", JsonSerializer.Serialize(queue.Where(x => favorites.Contains(x.Key))));
        File.Move(path + ".partial", path, true);
    }
    private void Favorite(object sender, RoutedEventArgs e)
    {
        if (currentIndex < 0) return;
        var key = queue[currentIndex].Key;
        var added = favorites.Add(key);
        if (!added) favorites.Remove(key);
        try
        {
            SaveFavorites();
            FavoriteFeedback.Text = added ? "收藏成功" : "已取消收藏";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (added) favorites.Remove(key);
            else favorites.Add(key);
            FavoriteFeedback.Text = "收藏保存失败";
        }
        UpdateTitle();
        favoriteFeedbackTimer.Stop();
        favoriteFeedbackTimer.Start();
    }
    private async void Previous(object sender, RoutedEventArgs e) => await SelectAsync(order.Previous(currentIndex, queue.Count));
    private async void Next(object sender, RoutedEventArgs e) => await SelectAsync(order.Next(currentIndex, queue.Count, naturalEnd: false));
    private async void Choose(object sender, MouseButtonEventArgs e) { if (QueueList.SelectedItem is MusicEntry item) { order.Reset(); await SelectAsync(Enqueue(item)); } }
    private async void ChooseKey(object sender, KeyEventArgs e) { if (e.Key == Key.Enter && QueueList.SelectedItem is MusicEntry item) { e.Handled = true; order.Reset(); await SelectAsync(Enqueue(item)); } }
    private void Filter(object sender, RoutedEventArgs e) => Refresh();
    private void RepeatChanged(object sender, RoutedEventArgs e)
    {
        order.Repeat = (MusicRepeatMode)(((int)order.Repeat + 1) % 3);
        Playback.LoopTrack = order.Repeat == MusicRepeatMode.One;
        Repeat.SetResourceReference(ForegroundProperty, order.Repeat == MusicRepeatMode.Off ? "ShellMuted" : "ShellAccent");
        RepeatGlyph.Data = (Geometry)FindResource(order.Repeat == MusicRepeatMode.One ? "MusicRepeatOne" : "MusicRepeat");
        Repeat.ToolTip = "循环：" + (order.Repeat == MusicRepeatMode.All ? "列表循环" : order.Repeat == MusicRepeatMode.One ? "单曲循环" : "关闭");
        AutomationProperties.SetName(Repeat, Repeat.ToolTip.ToString());
        SyncTransport();
    }
    private void ShuffleChanged(object sender, RoutedEventArgs e)
    {
        order.Shuffle = Shuffle.IsChecked == true;
        order.Reset();
        var label = order.Shuffle ? "随机播放已开启" : "随机播放已关闭";
        Shuffle.ToolTip = label;
        AutomationProperties.SetName(Shuffle, label);
    }
    private async void PlayPause(object sender, RoutedEventArgs e) { await Playback.TogglePlaybackAsync(); SyncTransport(); }
    private void ToggleQueue(object sender, RoutedEventArgs e)
    {
        ResizeQueue();
        QueuePopup.IsOpen = QueueToggle.IsChecked == true;
    }
    private void ResizeQueue() => QueuePanel.Width = Math.Min(760, Math.Max(320, ActualWidth - 2 * WorkspaceRightInset));
    private void QueueOpened(object sender, EventArgs e) => Search.Focus();
    private void QueueClosed(object sender, EventArgs e)
    {
        if (!QueuePopup.IsOpen) QueueToggle.IsChecked = false;
    }

    private void AttachQueueHost(object sender, RoutedEventArgs e)
    {
        if (queueHost != null || Window.GetWindow(this) is not { } owner) return;
        queueHost = owner;
        owner.PreviewMouseDown += DismissQueueOnOutsideClick;
        owner.Deactivated += CloseQueue;
        owner.LocationChanged += CloseQueue;
    }

    private void DetachQueueHost(object sender, RoutedEventArgs e)
    {
        QueuePopup.IsOpen = false;
        if (queueHost is not { } owner) return;
        owner.PreviewMouseDown -= DismissQueueOnOutsideClick;
        owner.Deactivated -= CloseQueue;
        owner.LocationChanged -= CloseQueue;
        queueHost = null;
    }

    private void DismissQueueOnOutsideClick(object sender, MouseButtonEventArgs e)
    {
        if (QueuePopup.IsOpen && !QueueToggle.IsMouseOver && !QueuePanel.IsMouseOver)
            QueuePopup.IsOpen = false;
    }

    private void CloseQueue(object? sender, EventArgs e) => QueuePopup.IsOpen = false;
    private void SyncTransport()
    {
        if (currentIndex >= 0)
        {
            var current = queue[currentIndex];
            var info = current.Subtitle;
            var status = Playback.PlaybackStatus;
            TrackSubtitle.Text = !Playback.CanSeek && !string.IsNullOrWhiteSpace(status) ? status : info;
            TrackSubtitle.ToolTip = string.IsNullOrWhiteSpace(status) ? info : $"{info}\n{status}";
        }
        MainPlay.IsPlaying = Playback.IsPlaying;
        MainPlay.IsEnabled = Playback.CanPlay;
        Position.IsEnabled = Playback.CanSeek;
        syncingPosition = true;
        try
        {
            Position.Maximum = Playback.DurationSeconds > 0 ? Playback.DurationSeconds : 1;
            if (!Position.IsMouseCaptureWithin) Position.Value = Playback.PositionSeconds;
        }
        finally { syncingPosition = false; }
        Elapsed.Text = FormatTime(Playback.PositionSeconds);
        Duration.Text = FormatTime(Playback.DurationSeconds);
        var loop = order.Repeat == MusicRepeatMode.One ? Playback.RecordedLoop : null;
        LoopMarkers.SetRange(loop, Playback.DurationSeconds);
        Position.ToolTip = loop is { } range ? $"循环 {FormatTime(range.Start)} – {FormatTime(range.End)}" : null;
        MainPlay.ToolTip = string.IsNullOrWhiteSpace(Playback.PlaybackStatus) ? (Playback.IsPlaying ? "暂停" : "播放") : Playback.PlaybackStatus;
    }
    private static string FormatTime(double seconds) => $"{(int)seconds / 60}:{(int)seconds % 60:D2}";
    private void Seek(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (syncingPosition || Playback == null || !Playback.CanSeek) return;
        Playback.SeekPlayback(e.NewValue);
        Elapsed.Text = FormatTime(e.NewValue);
    }
    private void ToggleMute(object sender, RoutedEventArgs e) =>
        VolumeSlider.Value = VolumeSlider.Value == 0 ? audibleVolume : 0;

    private void VolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var muted = e.NewValue == 0;
        if (!muted) audibleVolume = e.NewValue;
        if (Playback == null) return;
        Playback.SetVolume(e.NewValue);
        VolumeGlyph.Data = (Geometry)FindResource(muted ? "MusicMuted" : "MusicVolume");
        var label = muted ? "取消静音" : "静音";
        MuteButton.ToolTip = label;
        AutomationProperties.SetName(MuteButton, label);
    }
    private void PlayerKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !QueuePopup.IsOpen) return;
        QueuePopup.IsOpen = false;
        QueueToggle.Focus();
        e.Handled = true;
    }
    public void Dispose() { ++selectionVersion; ++libraryVersion; QueuePopup.IsOpen = false; transportTimer.Stop(); favoriteFeedbackTimer.Stop(); Playback.ShutdownPlayback(); if (ReferenceEquals(Current, this)) Current = null; }
}
