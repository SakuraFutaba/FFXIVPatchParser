namespace FFXIVPatchParser.Desktop;

internal sealed class SqPackFileTreeIndex
{
    internal readonly record struct File(IReadOnlyList<SqPackFileSystemEntry> Entries, int Index)
    {
        public SqPackFileSystemEntry Entry => Entries[Index];
    }

    internal sealed class Folder(string name, string path)
    {
        public string Name { get; } = name;
        public string Path { get; } = path;
        public List<Folder> Folders { get; } = [];
        public List<File> Files { get; } = [];
        public int FileCount { get; set; }
    }

    public Folder Root { get; } = new("", "");
    public int NamedCount { get; private set; }
    public (SqPackFileSystemEntry Entry, string Folder)? Selection { get; private set; }

    public static SqPackFileTreeIndex Build(LocalGameSource source, CancellationToken token, SqPackFileSystemEntry? selection = null)
    {
        var index = new SqPackFileTreeIndex();
        var folders = new Dictionary<string, Folder>(StringComparer.OrdinalIgnoreCase) { [""] = index.Root };
        var names = new Dictionary<(string Category, uint Hash), Folder?>();
        var lists = source.ListFileSystemContainers(token)
            .Select(container => (container.Category, Entries: source.EnumerateFileSystemEntries(container.Id, token))).ToArray();
        foreach (var (category, entries) in lists)
        {
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                if (entry.KnownPath is not { } path || entry.FolderHash is not { } hash || path.LastIndexOf('/') is not (>= 0 and var slash)) continue;
                var folder = FindFolder(path[..slash]);
                var key = (category, hash);
                if (!names.TryGetValue(key, out var previous)) names.Add(key, folder);
                else if (previous != folder) names[key] = null;
            }
        }

        var unresolvedFolders = new Dictionary<(string Category, uint Bucket, bool Index2), Folder>();
        foreach (var (category, entries) in lists)
        {
            for (var i = 0; i < entries.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var entry = entries[i];
                Folder folder;
                if (entry.KnownPath is { } path)
                {
                    folder = FindFolder(path[..Math.Max(0, path.LastIndexOf('/'))]);
                    index.NamedCount++;
                }
                else if (entry.FolderHash is { } hash && names.TryGetValue((category, hash), out var known) && known != null)
                    folder = known;
                else
                {
                    var key = (category, entry.FolderHash ?? (entry.FileHash >> 24), entry.IsIndex2);
                    if (!unresolvedFolders.TryGetValue(key, out folder!))
                    {
                        folder = entry.IsIndex2 ? FindFolder($"{category}/~完整路径哈希/{key.Item2:X2}")
                            : FindFolder($"{category}/~{key.Item2:X8}");
                        unresolvedFolders.Add(key, folder);
                    }
                }
                folder.Files.Add(new(entries, i));
                if (selection is { } selected && entry.Offset == selected.Offset && entry.Hash == selected.Hash &&
                    entry.IsIndex2 == selected.IsIndex2 && entry.Container == selected.Container)
                    index.Selection = (entry, folder.Path);
            }
        }

        Complete(index.Root, token);
        return index;

        Folder FindFolder(string path)
        {
            if (folders.TryGetValue(path, out var folder)) return folder;
            var slash = path.LastIndexOf('/');
            var parent = FindFolder(slash < 0 ? "" : path[..slash]);
            folder = new(path[(slash + 1)..], path);
            parent.Folders.Add(folder);
            folders.Add(path, folder);
            return folder;
        }
    }

    private static void Complete(Folder folder, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        folder.Folders.Sort(static (left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name));
        folder.Files.Sort(static (left, right) => Compare(left.Entry, right.Entry));
        folder.FileCount = folder.Files.Count;
        foreach (var child in folder.Folders)
        {
            Complete(child, token);
            folder.FileCount += child.FileCount;
        }
    }

    private static int Compare(SqPackFileSystemEntry left, SqPackFileSystemEntry right)
    {
        if (left.KnownPath is null && right.KnownPath != null) return 1;
        if (left.KnownPath != null && right.KnownPath is null) return -1;
        var name = StringComparer.OrdinalIgnoreCase.Compare(left.KnownPath, right.KnownPath);
        if (name != 0) return name;
        var hash = left.FileHash.CompareTo(right.FileHash);
        if (hash != 0) return hash;
        var container = StringComparer.OrdinalIgnoreCase.Compare(left.Container, right.Container);
        return container != 0 ? container : left.Offset.CompareTo(right.Offset);
    }

    public Folder Filter(string text, CancellationToken token)
    {
        var query = text.Trim().Replace('\\', '/');
        if (query.Length == 0) return Root;
        var hashQuery = query.TrimStart('~', '#');
        var findHash = hashQuery.Length is > 0 and <= 16 && hashQuery.All(char.IsAsciiHexDigit);
        var locatorQuery = query.StartsWith("sqpack:", StringComparison.OrdinalIgnoreCase);
        var offsetMarker = query.IndexOf('@');
        var locatorContainer = locatorQuery ? query[7..(offsetMarker < 0 ? query.Length : offsetMarker)] : "";
        return FindMatches(Root) ?? new("", "");

        Folder? FindMatches(Folder folder)
        {
            token.ThrowIfCancellationRequested();
            if (folder.Path.Contains(query, StringComparison.OrdinalIgnoreCase)) return folder;
            Folder? result = null;
            foreach (var child in folder.Folders)
            {
                if (FindMatches(child) is not { } matching) continue;
                result ??= new(folder.Name, folder.Path);
                result.Folders.Add(matching);
                result.FileCount += matching.FileCount;
            }
            foreach (var file in folder.Files)
            {
                token.ThrowIfCancellationRequested();
                var entry = file.Entry;
                if (entry.KnownPath?.Contains(query, StringComparison.OrdinalIgnoreCase) != true &&
                    !entry.Container.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !(findHash && (ContainsHash(entry.Hash, hashQuery, entry.IsIndex2 ? "X8" : "X16") ||
                        entry.FullPathHash is { } pathHash && ContainsHash(pathHash, hashQuery, "X8"))) &&
                    !(locatorQuery && entry.Container.StartsWith(locatorContainer, StringComparison.OrdinalIgnoreCase) &&
                        entry.Locator.StartsWith(query, StringComparison.OrdinalIgnoreCase))) continue;
                result ??= new(folder.Name, folder.Path);
                result.Files.Add(file);
                result.FileCount++;
            }
            return result;
        }
    }

    private static bool ContainsHash(ulong hash, string query, string format)
    {
        Span<char> text = stackalloc char[16];
        hash.TryFormat(text, out var length, format);
        return text[..length].Contains(query.AsSpan(), StringComparison.OrdinalIgnoreCase);
    }
}
