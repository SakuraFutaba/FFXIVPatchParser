using System.IO.Compression;
using System.Security.Cryptography;

namespace FFXIVPatchParser;

public sealed record PatchImageEntry(string Path, string Container, long Offset, string Evidence);
public sealed record PatchImageScanResult(IReadOnlyList<PatchImageEntry> Images, string Summary);
public enum PatchMediaKind { Model, Audio }
public sealed record PatchMediaEntry(string Path, string Container, long Offset);
public sealed record PatchMediaScanResult(IReadOnlyList<PatchMediaEntry> Entries, string Summary);

public static class PatchImageScanner
{
    /// <summary>自动候选仅验证索引和差分；数据容器尚未完整校验。</summary>
    public static PatchImageScanResult ScanKnownPaths(LocalGameSource source, LoadedPatch patch, IEnumerable<string> archivePaths,
        CancellationToken token = default, IProgress<string>? progress = null)
    {
        var skipped = new List<string>();
        var ranges = PatchMediaScanner.ReadRanges(source, patch, archivePaths, ["ui"], token, progress, verifyContainers: false, skippedContainers: skipped);
        if (ranges.Count == 0 && skipped.Count == 0) return new([], "本补丁没有 UI 数据容器更改。");
        var entries = source.ListKnownMediaLocations("image", ranges, token)
            .Select(item => new PatchImageEntry(item.Path, item.Container, item.Offset, PatchMediaScanner.KnownPathEvidence)).ToArray();
        return new(entries, $"{entries.Length:N0} 张已知路径图片写入候选。" +
            PatchMediaScanner.KnownPathSummary(ranges.Count, skipped));
    }

    public static PatchImageScanResult Scan(LocalGameSource source, LoadedPatch patch, IEnumerable<string> archivePaths,
        CancellationToken token = default, IProgress<string>? progress = null)
    {
        var ranges = PatchMediaScanner.ReadRanges(source, patch, archivePaths, ["ui"], token, progress);
        if (ranges.Count == 0) return new([], "本补丁没有 UI 数据容器更改。");
        var textures = source.EnumerateUiTextures(token, ranges.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase),
            (path, start, end) => PatchMediaScanner.Overlaps(ranges, path, start, end));
        const string evidence = "目标容器与差分 MD5 已验证。纹理存储区间（含对齐填充）与 VCDIFF 写入区间相交，可能包含容器重排。";
        var images = textures.Where(texture => PatchMediaScanner.Overlaps(ranges, texture.Container, texture.Offset, texture.End))
            .Select(texture => new PatchImageEntry(texture.Path, texture.Container, texture.Offset, evidence)).ToArray();
        return new(images, $"{images.Length:N0} 张写入纹理候选。预览来自通过校验的本地目标版本，写入区间可能包含容器重排。");
    }
}

public static class PatchMediaScanner
{
    internal const string KnownPathEvidence = "已校验目标索引和差分。数据容器留待完整扫描校验。写入候选可能包含容器重排。";

    /// <summary>自动候选仅验证索引和差分；未知路径由完整扫描补充。</summary>
    public static PatchMediaScanResult ScanKnownPaths(LocalGameSource source, LoadedPatch patch, IEnumerable<string> archivePaths,
        PatchMediaKind kind, CancellationToken token = default, IProgress<string>? progress = null, string? container = null)
    {
        var label = kind == PatchMediaKind.Model ? "模型" : "音频";
        var skipped = new List<string>();
        var ranges = ReadRanges(source, patch, archivePaths, Categories(kind), token, progress, container, verifyContainers: false, skippedContainers: skipped);
        if (ranges.Count == 0 && skipped.Count == 0) return new([], $"本补丁没有{label}相关容器更改。");
        var entries = source.ListKnownMediaLocations(kind == PatchMediaKind.Model ? "model" : "audio", ranges, token)
            .Select(item => new PatchMediaEntry(item.Path, item.Container, item.Offset)).ToArray();
        return new(entries, $"{entries.Length:N0} 个已知路径{label}写入候选。" + KnownPathSummary(ranges.Count, skipped));
    }

    internal static string KnownPathSummary(int verifiedContainers, IReadOnlyList<string> skipped) =>
        (verifiedContainers == 0 ? (skipped.Count == 0 ? "本补丁没有相关数据容器更改。" : "没有可确认目标索引的更改容器。") : KnownPathEvidence) +
        (skipped.Count == 0 ? "" : $"另有 {skipped.Count:N0} 个容器缺少目标 index 摘要，已跳过，需手动完整扫描。");

    private const string SqPackPrefix = "game/sqpack/";

    public static IReadOnlyList<string> Containers(LoadedPatch patch, PatchMediaKind kind)
    {
        var categories = Categories(kind);
        return patch.Resources.Where(resource => categories.Contains(resource.Category) && IsContainer(resource))
            .Select(resource => resource.Path[SqPackPrefix.Length..]).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static PatchMediaScanResult Scan(LocalGameSource source, LoadedPatch patch, IEnumerable<string> archivePaths, PatchMediaKind kind,
        CancellationToken token = default, IProgress<string>? progress = null, string? container = null)
    {
        var model = kind == PatchMediaKind.Model;
        var label = model ? "模型" : "音频";
        var ranges = ReadRanges(source, patch, archivePaths, Categories(kind), token, progress, container);
        if (ranges.Count == 0) return new([], $"本补丁没有{label}相关容器更改。");
        var containers = ranges.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        progress?.Report("正在筛选索引中的" + label + "资源…");
        var locations = model
            ? source.ListModelLocations(token, containers, progress, includeRange: (path, start, end) => Overlaps(ranges, path, start, end)).Select(item => (item.Path, item.Container, item.Offset, item.End))
            : source.ListAudioLocations(token, containers, progress, includeRange: (path, start, end) => Overlaps(ranges, path, start, end)).Select(item => (item.Path, item.Container, item.Offset, item.End));
        var entries = locations.Where(item => Overlaps(ranges, item.Container, item.Offset, item.End))
            .OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .Select(item => new PatchMediaEntry(item.Path, item.Container, item.Offset)).ToArray();
        return new(entries, $"{entries.Length:N0} 个{label}写入候选。预览来自通过校验的本地目标版本，写入区间可能包含容器重排。");
    }

    internal static Dictionary<string, IReadOnlyList<VcdiffRanges.Range>> ReadRanges(LocalGameSource source, LoadedPatch patch,
        IEnumerable<string> archivePaths, string[] categories, CancellationToken token, IProgress<string>? progress, string? selectedContainer = null, bool verifyContainers = true, List<string>? skippedContainers = null)
    {
        token.ThrowIfCancellationRequested();
        var resources = patch.Resources.Where(resource => categories.Contains(resource.Category) && resource.Path.StartsWith(SqPackPrefix, StringComparison.OrdinalIgnoreCase)).ToArray();
        var containers = resources.Where(resource => IsContainer(resource) && (selectedContainer == null ||
            resource.Path[SqPackPrefix.Length..].Equals(selectedContainer, StringComparison.OrdinalIgnoreCase))).ToArray();
        var ranges = new Dictionary<string, IReadOnlyList<VcdiffRanges.Range>>(StringComparer.OrdinalIgnoreCase);
        if (containers.Length == 0) return ranges;
        // 快速路径依赖 index 的目录哈希；缺少对应目标摘要的容器交给手动完整扫描。
        if (!verifyContainers)
        {
            containers = containers.Where(resource =>
            {
                var index = resource.Path[..resource.Path.LastIndexOf(".dat", StringComparison.OrdinalIgnoreCase)] + ".index";
                if (resources.Any(candidate => candidate.Path.Equals(index, StringComparison.OrdinalIgnoreCase))) return true;
                skippedContainers?.Add(resource.Path[SqPackPrefix.Length..]);
                return false;
            }).ToArray();
            if (containers.Length == 0) return ranges;
        }

        var indexNames = containers.SelectMany(resource =>
        {
            var baseName = resource.Path[..resource.Path.LastIndexOf(".dat", StringComparison.OrdinalIgnoreCase)];
            return new[] { baseName + ".index", baseName + ".index2" };
        }).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var archives = new List<ZipArchive>();
        try
        {
            foreach (var archive in archivePaths) archives.Add(ZipFile.OpenRead(archive));
            foreach (var resource in resources.Where(resource => indexNames.Contains(resource.Path)))
                VerifyTarget(source, resource, resource.Path[SqPackPrefix.Length..], token, progress);

            foreach (var resource in containers)
            {
                token.ThrowIfCancellationRequested();
                if (!resource.Available) throw new InvalidDataException("补丁缺少分卷：" + resource.Entry);
                var relative = resource.Path[SqPackPrefix.Length..];
                var length = verifyContainers ? VerifyTarget(source, resource, relative, token, progress)
                    : new FileInfo(Workspace.Within(source.SqPackPath, relative)).Length;
                var entry = archives.SelectMany(archive => archive.Entries).FirstOrDefault(candidate =>
                    candidate.FullName.Replace('\\', '/').Equals(resource.Entry, StringComparison.OrdinalIgnoreCase))
                    ?? throw new FileNotFoundException("找不到差分：" + resource.Entry);
                progress?.Report("分析写入区间：" + relative);
                using var delta = ReadDelta(entry, resource.DeltaMd5, token);
                ranges.Add(relative, VcdiffRanges.Read(delta, token, length));
            }
        }
        finally { foreach (var archive in archives) archive.Dispose(); }
        return ranges;
    }

    private static string[] Categories(PatchMediaKind kind) => kind == PatchMediaKind.Model
        ? ["chara", "bgcommon", "bg", "cut", "common", "vfx"] : ["music", "sound", "cut"];

    private static bool IsContainer(PatchResource resource) => resource.Path.StartsWith(SqPackPrefix, StringComparison.OrdinalIgnoreCase)
        && resource.Path.Contains(".win32.dat", StringComparison.OrdinalIgnoreCase);

    private static long VerifyTarget(LocalGameSource source, PatchResource resource, string relative, CancellationToken token, IProgress<string>? progress)
    {
        progress?.Report("校验本地目标文件：" + relative);
        using var input = File.OpenRead(Workspace.Within(source.SqPackPath, relative));
        if (!Hash(input, token).Equals(resource.ResultMd5, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("本地文件与补丁目标版本不匹配：" + relative);
        return input.Length;
    }

    private static Stream ReadDelta(ZipArchiveEntry entry, string expectedMd5, CancellationToken token)
    {
        const int limit = 256 * 1024 * 1024;
        if (entry.Length > limit) throw new NotSupportedException("单个差分超过 256 MiB 分析限制。");
        Stream delta;
        if (entry.Length <= 16 * 1024 * 1024) delta = new MemoryStream((int)entry.Length);
        else
        {
            var directory = Path.Combine(Workspace.Temp, "media");
            Directory.CreateDirectory(directory);
            delta = new FileStream(Path.Combine(directory, Path.GetRandomFileName()), FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 64 * 1024, FileOptions.DeleteOnClose);
        }
        try
        {
            using var input = entry.Open();
            using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            var buffer = new byte[65536];
            int count;
            while ((count = input.Read(buffer)) != 0)
            {
                token.ThrowIfCancellationRequested();
                if (delta.Length + count > limit) throw new InvalidDataException("差分超过分析限制。");
                delta.Write(buffer, 0, count);
                md5.AppendData(buffer, 0, count);
            }
            if (!Convert.ToHexString(md5.GetHashAndReset()).Equals(expectedMd5, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("差分 MD5 不匹配：" + entry.FullName);
            delta.Position = 0;
            return delta;
        }
        catch
        {
            delta.Dispose();
            throw;
        }
    }

    internal static bool Overlaps(Dictionary<string, IReadOnlyList<VcdiffRanges.Range>> byContainer, string container, long offset, long end)
    {
        if (!byContainer.TryGetValue(container, out var ranges)) return false;
        var first = 0;
        var last = ranges.Count;
        while (first < last)
        {
            var middle = first + (last - first) / 2;
            if (ranges[middle].End <= offset) first = middle + 1;
            else last = middle;
        }
        return first < ranges.Count && ranges[first].Start < end;
    }

    private static string Hash(Stream stream, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var buffer = new byte[1024 * 1024];
        int count;
        while ((count = stream.Read(buffer)) != 0)
        {
            token.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, count);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
