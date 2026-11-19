using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace FFXIVPatchParser.Desktop;

public partial class SqPackFileTree : UserControl
{
    private readonly HashSet<string> expanded = new(StringComparer.OrdinalIgnoreCase);
    private LocalGameSource? source;
    private SqPackFileTreeIndex? index;
    private SqPackFileSystemEntry? selected;
    private string selectedFolder = "";
    private CancellationTokenSource? building, filtering;
    private Task buildTask = Task.CompletedTask;
    private PathCoveragePanel? coveragePanel;
    private Task coverageCloseTask = Task.CompletedTask;
    private bool pathCoverageAvailable = true, coverageClosing;

    public SqPackFileTree() => InitializeComponent();

    internal event Action<SqPackFileSystemEntry>? FileSelected;

    internal void SetSource(LocalGameSource? game)
    {
        if (ReferenceEquals(source, game)) return;
        building?.Cancel();
        filtering?.Cancel();
        source = game;
        CloseCoverage();
        index = null;
        selected = null;
        selectedFolder = "";
        expanded.Clear();
        DirectoryTree.ItemsSource = null;
        FileSearch.Clear();
        FileSearch.IsEnabled = false;
        PathCoverageButton.IsEnabled = false;
        FileCount.Text = game == null ? "请在设置中连接游戏目录" : "等待读取索引";
        StartIndex();
    }

    internal async Task StopLoadingAsync()
    {
        CloseCoverage();
        await coverageCloseTask;
        building?.Cancel();
        filtering?.Cancel();
        await buildTask;
    }

    internal void SetPathCoverageAvailable(bool available)
    {
        pathCoverageAvailable = available;
        UpdatePathCoverageButton();
        if (available) StartIndex();
        else CloseCoverage();
    }

    private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible) StartIndex();
        else CloseCoverage();
    }

    private void Retry(object sender, RoutedEventArgs e) => StartIndex();

    private void OpenPathCoverage(object sender, RoutedEventArgs e)
    {
        if (coveragePanel != null)
        {
            DismissCoverage();
            return;
        }
        if (!pathCoverageAvailable || coverageClosing || source is not { } game || index is null || Window.GetWindow(this) is not { } owner)
            return;
        coveragePanel = new PathCoveragePanel(game, owner);
        coveragePanel.CloseRequested += DismissCoverage;
        CoveragePopup.Child = coveragePanel;
        CoveragePopup.IsOpen = true;
    }

    private void CoverageOpened(object? sender, EventArgs e) => coveragePanel?.Open();

    private void CoverageClosed(object? sender, EventArgs e) => CloseCoverage();

    private void DismissCoverage()
    {
        CloseCoverage();
        if (PathCoverageButton.IsEnabled && IsVisible) PathCoverageButton.Focus();
    }

    private void CloseCoverage()
    {
        if (coveragePanel is not { } panel) return;
        coveragePanel = null;
        coverageClosing = true;
        panel.CloseRequested -= DismissCoverage;
        CoveragePopup.IsOpen = false;
        CoveragePopup.Child = null;
        UpdatePathCoverageButton();
        coverageCloseTask = FinishCoverageAsync(panel);
    }

    private async Task FinishCoverageAsync(PathCoveragePanel panel)
    {
        try
        {
            await panel.StopAsync();
            if (!panel.PathsChanged || source != panel.Source) return;
            filtering?.Cancel();
            index = null;
            FileSearch.IsEnabled = false;
            DirectoryTree.ItemsSource = null;
            await buildTask;
            if (source == panel.Source && pathCoverageAvailable) StartIndex();
        }
        finally
        {
            coverageClosing = false;
            UpdatePathCoverageButton();
        }
    }

    private void UpdatePathCoverageButton() => PathCoverageButton.IsEnabled = pathCoverageAvailable && !coverageClosing && index != null;

    private void StartIndex()
    {
        if (FileCount == null || !IsVisible || source is not { } game || index != null || building != null) return;
        buildTask = ReadIndexAsync(game);
    }

    private async Task ReadIndexAsync(LocalGameSource game)
    {
        using var pending = new CancellationTokenSource();
        building = pending;
        RetryButton.Visibility = Visibility.Collapsed;
        FileCount.Text = "正在读取 SqPack 索引…";
        try
        {
            var file = selected;
            var result = await Task.Run(() => SqPackFileTreeIndex.Build(game, pending.Token, file), pending.Token);
            if (pending.IsCancellationRequested || source != game) return;
            index = result;
            if (result.Selection is { } selection && selected == file)
            {
                selected = selection.Entry;
                selectedFolder = selection.Folder;
                ExpandAncestors(selectedFolder);
                if (file?.KnownPath != selection.Entry.KnownPath) FileSelected?.Invoke(selection.Entry);
            }
            FileSearch.IsEnabled = true;
            UpdatePathCoverageButton();
            await ApplySearch();
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (pending.IsCancellationRequested) return;
            FileCount.Text = "读取索引失败 · " + ex.Message;
            RetryButton.Visibility = Visibility.Visible;
        }
        finally { if (building == pending) building = null; }
    }

    private async void SearchChanged(object sender, TextChangedEventArgs e) => await ApplySearch();

    private async Task ApplySearch()
    {
        if (index is not { } current) return;
        filtering?.Cancel();
        using var pending = new CancellationTokenSource();
        filtering = pending;
        var query = FileSearch.Text.Trim();
        try
        {
            var folder = current.Root;
            if (query.Length != 0)
            {
                await Task.Delay(125, pending.Token);
                folder = await Task.Run(() => current.Filter(query, pending.Token), pending.Token);
            }
            pending.Token.ThrowIfCancellationRequested();
            var expandMatches = query.Length != 0 && folder.FileCount <= 150;
            var items = folder.Folders.Select(child => new Node(this, child, expandMatches)).ToArray();
            DirectoryTree.ItemsSource = items;
            FileCount.Text = query.Length == 0 ? $"{folder.FileCount:N0} 个文件 · {current.NamedCount:N0} 个已知路径"
                : $"{folder.FileCount:N0} / {current.Root.FileCount:N0} 个文件";
            if (query.Length == 0 && selected is { } file) RevealFile(folder, items, file);
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested) { }
        finally { if (filtering == pending) filtering = null; }
    }

    private void SelectFile(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not Node { Entry: { } entry } node || entry == selected) return;
        selected = entry;
        selectedFolder = node.ParentPath;
        ExpandAncestors(node.ParentPath);
        FileSelected?.Invoke(entry);
    }

    private void ExpandAncestors(string path)
    {
        for (var folder = path; folder.Length > 0; folder = folder[..Math.Max(0, folder.LastIndexOf('/'))])
            expanded.Add(folder);
    }

    private void RevealFile(SqPackFileTreeIndex.Folder folder, Node[] items, SqPackFileSystemEntry file)
    {
        var positions = new List<int>();
        while (!string.Equals(folder.Path, selectedFolder, StringComparison.OrdinalIgnoreCase))
        {
            var position = folder.Folders.FindIndex(child =>
                string.Equals(child.Path, selectedFolder, StringComparison.OrdinalIgnoreCase) ||
                selectedFolder.StartsWith(child.Path + "/", StringComparison.OrdinalIgnoreCase));
            if (position < 0) return;
            folder = CompactFolder(folder.Folders[position]);
            if (!expanded.Contains(folder.Path)) return;
            positions.Add(position);
        }
        var index = folder.Files.FindIndex(item => item.Entry == file);
        if (index < 0) return;
        positions.Add(folder.Folders.Count + index);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (IsVisible && DirectoryTree.ItemsSource == items && file == selected)
                PathTreeView.SelectItem(DirectoryTree, positions);
        });
    }

    private static SqPackFileTreeIndex.Folder CompactFolder(SqPackFileTreeIndex.Folder folder)
    {
        while (folder.Files.Count == 0 && folder.Folders.Count == 1) folder = folder.Folders[0];
        return folder;
    }
    private sealed class Node : INotifyPropertyChanged, IPathTreeRow
    {
        private static readonly object[] PendingChildren = [new object()];
        private readonly SqPackFileTree owner;
        private readonly SqPackFileTreeIndex.Folder? folder;
        private readonly bool expandMatches;
        private readonly string? folderDescription;
        private readonly string? collapsedLabel;
        private object[]? children;
        private bool expanded;

        public Node(SqPackFileTree owner, SqPackFileTreeIndex.Folder folder, bool expandMatches)
        {
            this.owner = owner;
            var first = folder;
            folder = CompactFolder(folder);
            this.folder = folder;
            this.expandMatches = expandMatches;
            Label = first.Name + folder.Path[first.Path.Length..];
            if (!ReferenceEquals(first, folder)) collapsedLabel = first.Name;
            folderDescription = $"{folder.Path} · {folder.FileCount:N0} 个文件";
            expanded = expandMatches || owner.expanded.Contains(folder.Path);
        }

        public Node(SqPackFileTree owner, SqPackFileSystemEntry entry, string parentPath, bool duplicate)
        {
            this.owner = owner;
            Entry = entry;
            ParentPath = parentPath;
            Label = entry.KnownPath is { } path ? path[(path.LastIndexOf('/') + 1)..] : $"~{entry.FileHash:X8}";
            if (duplicate) Label += $" · {entry.Container}@{entry.Offset:X}";
            IsSelected = entry == owner.selected;
        }

        public bool IsDirectory => folder != null;
        public string Label { get; }
        public string DisplayLabel => expanded ? Label : collapsedLabel ?? Label;
        public string Description => Entry is { } file
            ? file.KnownPath == null ? file.Locator : $"{file.KnownPath}\n{file.Locator}" : folderDescription ?? "";
        public string ParentPath { get; } = "";
        public SqPackFileSystemEntry? Entry { get; }
        public bool IsSelected { get; set; }

        public object[] Children => folder == null ? [] : children ?? (expanded ? children = MakeChildren(folder) : PendingChildren);

        public bool IsExpanded
        {
            get => expanded;
            set
            {
                if (folder == null || expanded == value) return;
                expanded = value;
                if (value) owner.expanded.Add(folder.Path);
                else owner.expanded.Remove(folder.Path);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Children)));
                if (collapsedLabel != null)
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayLabel)));
            }
        }

        private object[] MakeChildren(SqPackFileTreeIndex.Folder current)
        {
            var result = new Node[current.Folders.Count + current.Files.Count];
            for (var i = 0; i < current.Folders.Count; i++) result[i] = new(owner, current.Folders[i], expandMatches);
            for (var i = 0; i < current.Files.Count; i++)
            {
                var entry = current.Files[i].Entry;
                var duplicate = (i > 0 && SameLabel(entry, current.Files[i - 1].Entry)) ||
                    (i + 1 < current.Files.Count && SameLabel(entry, current.Files[i + 1].Entry));
                result[current.Folders.Count + i] = new(owner, entry, current.Path, duplicate);
            }
            return result;
        }

        private static bool SameLabel(SqPackFileSystemEntry left, SqPackFileSystemEntry right) => left.KnownPath != null
            ? left.KnownPath.Equals(right.KnownPath, StringComparison.OrdinalIgnoreCase)
            : right.KnownPath == null && left.FileHash == right.FileHash;

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
