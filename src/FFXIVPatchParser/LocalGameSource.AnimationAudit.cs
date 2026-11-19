namespace FFXIVPatchParser;

public sealed record GameAnimationAuditEntry(GameAnimationDescription Description,
    IReadOnlyList<GameAnimationSlot> Slots, string? Error);

public sealed partial class LocalGameSource
{
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyList<string>, List<GameAnimationAuditEntry>> auditedAnimations = new();

    /// <summary>逐个核对所选模型的 PAP 槽与表格关系，仅在无法定位索引条目时读取整个 PAP。</summary>
    public IReadOnlyList<GameAnimationAuditEntry> AuditModelAnimations(string modelPath, CancellationToken token = default,
        IProgress<(int Read, int Total)>? progress = null)
    {
        var paths = ListModelAnimationPaths(modelPath, token);
        if (auditedAnimations.TryGetValue(paths, out var cached)) return cached;
        Dictionary<string, SqPackFileSystemEntry> index;
        lock (gate) index = indexedAnimationEntries;
        var names = GetAnimationNames(token);
        var links = paths.Count == 0 ? null : GetPapTimelineReferences(names, token);
        var result = new List<GameAnimationAuditEntry>(paths.Count);
        for (var i = 0; i < paths.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var path = paths[i];
            try
            {
                var metadata = index.TryGetValue(path, out var entry)
                    ? ReadFileSystemAnimationMetadata(entry, ReadFileSystemFile(entry, 384, token), token)
                    : ReadAnimationMetadata(path, token);
                result.Add(new(DescribeAnimation(path, names, metadata.Slots, links), metadata.Slots, null));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or ArgumentException)
            {
                result.Add(new(DescribeAnimation(path, names, null, null), [], ex.Message));
            }
            if ((i & 63) == 63 || i + 1 == paths.Count) progress?.Report((i + 1, paths.Count));
        }
        token.ThrowIfCancellationRequested();
        return auditedAnimations.GetValue(paths, _ => result);
    }
}
