namespace FFXIVPatchParser;

public sealed partial class LocalGameSource
{
    private int mediaCatalogRevision;
    public int MediaCatalogRevision => Volatile.Read(ref mediaCatalogRevision);

    /// <summary>按已验证路径的扩展名建立目录；不读取未知资源的 DAT 文件头。</summary>
    public IReadOnlyList<SqPackModelLocation> ListKnownLocalMediaLocations(string kind, CancellationToken token = default)
    {
        var extensions = kind switch
        {
            "image" => new[] { ".tex", ".atex" },
            "model" => new[] { ".mdl" },
            "audio" => new[] { ".scd" },
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var result = new List<SqPackModelLocation>();
        // 容器之间释放源锁，预览读取可以穿插进行；索引与名称复用文件系统缓存。
        foreach (var container in ListFileSystemContainers(token))
        {
            token.ThrowIfCancellationRequested();
            var entries = EnumerateFileSystemEntries(container.Id, token);
            var end = new FileInfo(Path.Combine(SqPackPath, container.Id)).Length;
            var previousOffset = end;
            for (var index = entries.Count - 1; index >= 0; index--)
            {
                token.ThrowIfCancellationRequested();
                var entry = entries[index];
                if (entry.Offset != previousOffset) { end = previousOffset; previousOffset = entry.Offset; }
                if (entry.KnownPath is { } path && extensions.Any(extension => path.EndsWith(extension, StringComparison.Ordinal)))
                    result.Add(new(path, entry.Container, entry.Offset, end));
            }
        }
        // 同一路径若仍对应多个实际地址，留在文件系统中按地址浏览。
        return result.GroupBy(entry => entry.Path, StringComparer.Ordinal)
            .Where(group => group.Select(entry => (entry.Container, entry.Offset)).Distinct().Take(2).Count() == 1)
            .Select(group => group.First()).OrderBy(entry => entry.Path, StringComparer.Ordinal).ToArray();
    }
}
