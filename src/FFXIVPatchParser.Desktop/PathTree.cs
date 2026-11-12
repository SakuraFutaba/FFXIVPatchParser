using System.ComponentModel;

namespace FFXIVPatchParser.Desktop;

/// <summary>A data-only tree node. FullPath and Payload retain the selected original leaf identity.</summary>
public sealed class PathTreeNode : INotifyPropertyChanged
{
    public string FullPath { get; }
    public string Label { get; }
    public bool IsDirectory { get; }
    public object? Payload { get; }
    public IReadOnlyList<PathTreeNode> Children { get; }
    public int LeafCount { get; }
    private bool expanded;
    public bool IsExpanded
    {
        get => expanded;
        set
        {
            if (expanded == value) return;
            expanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    internal PathTreeNode(string fullPath, string label, object? payload, IReadOnlyList<PathTreeNode> children, bool directory, bool expand)
    {
        FullPath = fullPath;
        Label = label;
        Payload = payload;
        Children = children;
        IsDirectory = directory;
        expanded = expand;
        LeafCount = directory ? children.Sum(child => child.LeafCount) : 1;
    }
}

public static class PathTree
{
    /// <summary>Builds an immutable hierarchy without allocating TreeViewItems. Filtered leaves retain and expand their ancestors.</summary>
    public static IReadOnlyList<PathTreeNode> Build<T>(IEnumerable<T> items, Func<T, string> pathSelector,
        string? filter = null, Func<T, string>? searchTextSelector = null, CancellationToken token = default,
        IComparer<string>? labelComparer = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(pathSelector);
        var query = filter?.Trim() ?? "";
        var root = new Branch("", "");
        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested();
            var original = pathSelector(item) ?? throw new ArgumentException("Path cannot be null.", nameof(pathSelector));
            var segments = Segments(original);
            if (query.Length != 0 && !original.Contains(query, StringComparison.OrdinalIgnoreCase)
                && !string.Join('/', segments).Contains(query.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)
                && !(searchTextSelector?.Invoke(item)?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)) continue;
            var branch = root;
            for (var i = 0; i < segments.Length - 1; i++)
            {
                var segment = segments[i];
                branch.Directories ??= new(StringComparer.Ordinal);
                if (!branch.Directories.TryGetValue(segment, out var next))
                {
                    var directoryPath = branch.Path.Length == 0 ? segment : branch.Path + "/" + segment;
                    next = new Branch(directoryPath, segment);
                    branch.Directories.Add(segment, next);
                }
                branch = next;
            }
            branch.Leaves ??= [];
            branch.Leaves.Add(new(original, segments[^1], item, [], false, false));
        }
        return Freeze(root, query.Length != 0, token, new NodeComparer(labelComparer ?? StringComparer.OrdinalIgnoreCase));
    }

    private static string[] Segments(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith("sqpack:", StringComparison.OrdinalIgnoreCase))
        {
            // Synthetic addresses keep their exact original identity, but group by repository and data container.
            var body = normalized[7..];
            var at = body.LastIndexOf('@');
            normalized = "sqpack/" + (at >= 0 ? body[..at] + "/" + body[at..] : body);
        }
        var result = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return result.Length == 0 ? [path.Length == 0 ? "(空路径)" : path] : result;
    }

    private static IReadOnlyList<PathTreeNode> Freeze(Branch branch, bool expand, CancellationToken token, NodeComparer comparer)
    {
        token.ThrowIfCancellationRequested();
        var result = new List<PathTreeNode>((branch.Directories?.Count ?? 0) + (branch.Leaves?.Count ?? 0));
        if (branch.Directories != null)
            foreach (var child in branch.Directories.Values)
                result.Add(new(child.Path, child.Label, null, Freeze(child, expand, token, comparer), true, expand));
        if (branch.Leaves != null) result.AddRange(branch.Leaves);
        result.Sort(comparer);
        return result.ToArray();
    }

    private sealed class Branch(string path, string label)
    {
        public string Path { get; } = path;
        public string Label { get; } = label;
        public Dictionary<string, Branch>? Directories { get; set; }
        public List<PathTreeNode>? Leaves { get; set; }
    }
    private sealed class NodeComparer(IComparer<string> labels) : IComparer<PathTreeNode>
    {
        public int Compare(PathTreeNode? x, PathTreeNode? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x == null) return -1;
            if (y == null) return 1;
            var kind = y.IsDirectory.CompareTo(x.IsDirectory);
            if (kind != 0) return kind;
            var label = labels.Compare(x.Label, y.Label);
            if (label != 0) return label;
            label = StringComparer.Ordinal.Compare(x.Label, y.Label);
            return label != 0 ? label : StringComparer.Ordinal.Compare(x.FullPath, y.FullPath);
        }
    }
}
