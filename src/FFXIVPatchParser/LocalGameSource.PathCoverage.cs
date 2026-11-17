using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lumina;
using Lumina.Data.Structs.Excel;
using Lumina.Excel;
using Lumina.Text.ReadOnly;

namespace FFXIVPatchParser;

public sealed record SqPackPathCoverageRow(string Repository, string Category, string Container, int Total, int Named)
{
    public int Unknown => Total - Named;
}
public sealed record SqPackPathCoverage(string Build, string IndexFingerprint, IReadOnlyList<SqPackPathCoverageRow> Containers)
{
    public int Total => Containers.Sum(r => r.Total);
    public int Named => Containers.Sum(r => r.Named);
    public int Unknown => Total - Named;
}
public sealed record SqPackPathImportResult(int Candidates, int Matched, int Added, int Ambiguous, string SourceSha256);
public sealed record SqPackPathReferenceScan(int Examined, int Read, int Failed, long Bytes, string? LastLocator, bool Complete, SqPackPathImportResult Import);
public sealed record SqPackPathEvidence(string Path, string Source, string SourceSha256, string Locator, string IndexFingerprint);

public sealed partial class LocalGameSource
{
    private string? pathIndexFingerprint;
    private string PathCoverageDirectory => Path.Combine(Workspace.Temp, "file-paths", "verified", GetPathIndexFingerprint());
    private string VerifiedPathCatalog => Path.Combine(PathCoverageDirectory, "catalog.tsv");

    // 仅由显式媒体扫描调用；名称仍须对应当前容器的同一偏移和索引哈希。
    private Dictionary<(long Offset, ulong Hash), string> GetFileSystemMediaPaths(string container, string extension, CancellationToken token,
        string? alternateExtension = null)
    {
        var result = new Dictionary<(long, ulong), string>();
        var conflicts = new HashSet<(long, ulong)>();
        foreach (var entry in EnumerateFileSystemEntries(container, token))
        {
            token.ThrowIfCancellationRequested();
            if (entry.IsIndex2 || entry.KnownPath is not { } path ||
                (!path.EndsWith(extension, StringComparison.Ordinal) &&
                (alternateExtension is null || !path.EndsWith(alternateExtension, StringComparison.Ordinal)))) continue;
            var key = (entry.Offset, entry.Hash);
            if (conflicts.Contains(key)) continue;
            if (result.TryGetValue(key, out var previous) && previous != path) { result.Remove(key); conflicts.Add(key); }
            else result[key] = path;
        }
        return result;
    }

    private static void CheckPathCacheBudget(long reservedBytes)
    {
        var root = Path.Combine(Workspace.Temp, "file-paths");
        var used = Directory.Exists(root) ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length) : 0;
        if (used + reservedBytes > 2L * 1024 * 1024 * 1024)
            throw new IOException("路径缓存已接近 2 GiB 上限，请先清理 tmp/file-paths 中不再使用的下载或旧构建缓存。");
    }

    private string GetPathIndexFingerprint()
    {
        if (pathIndexFingerprint is not null) return pathIndexFingerprint;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Directory.EnumerateFiles(SqPackPath, "*.index*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(SqPackPath, file).Replace('\\', '/')));
            using var input = File.OpenRead(file);
            hash.AppendData(SHA256.HashData(input));
        }
        return pathIndexFingerprint = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public SqPackPathCoverage GetFileSystemPathCoverage(CancellationToken token = default)
    {
        lock (gate)
        {
            var rows = new List<SqPackPathCoverageRow>();
            foreach (var pack in ListFileSystemContainers(token))
            {
                token.ThrowIfCancellationRequested();
                var entries = EnumerateFileSystemEntries(pack.Id, token);
                rows.Add(new(pack.Repository, pack.Category, pack.Id, entries.Count, entries.Count(e => e.KnownPath is not null)));
            }
            var versionFile = Path.Combine(Path.GetDirectoryName(SqPackPath)!, "ffxivgame.ver");
            var build = File.Exists(versionFile) ? File.ReadAllText(versionFile).Trim() : "未知构建";
            return new(build, GetPathIndexFingerprint(), rows);
        }
    }

    public SqPackPathEvidence? GetFileSystemPathEvidence(string path, CancellationToken token = default)
    {
        lock (gate)
        {
            if (NormalizeFileSystemPath(path) is not { } normalized || !File.Exists(VerifiedPathCatalog)) return null;
            foreach (var line in ReadBoundedPathLines(VerifiedPathCatalog, token))
            {
                var fields = line.Split('\t');
                if (fields.Length != 3 || fields[0] != normalized) continue;
                if (fields[1].Length != 64 || !fields[1].All(Uri.IsHexDigit)) return null;
                var manifest = Path.Combine(PathCoverageDirectory, fields[1] + ".json");
                if (!File.Exists(manifest)) return null;
                using var json = JsonDocument.Parse(File.ReadAllText(manifest));
                return new(normalized, json.RootElement.GetProperty("source").GetString() ?? "未知来源", fields[1], fields[2], GetPathIndexFingerprint());
            }
            return null;
        }
    }

    /// <summary>只接受与当前合并索引及实际 dat 地址匹配的完整路径。单文件最多 512 MiB，最多四百万行。</summary>
    public SqPackPathImportResult ImportFileSystemPaths(string file, string source, CancellationToken token = default, IProgress<string>? progress = null)
    {
        if (new FileInfo(file).Length > 512L * 1024 * 1024) throw new InvalidDataException("路径清单超过 512 MiB 限制。");
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            CheckPathCacheBudget(512L * 1024 * 1024);
            using var sourceStream = File.OpenRead(file);
            var digest = Convert.ToHexString(SHA256.HashData(sourceStream)).ToLowerInvariant();
            // 来源标签不能把本机绝对路径写进证据。
            var label = Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme == "https" ? source : Path.GetFileName(source);
            if (label.Length > 2048 || label.Any(char.IsControl)) throw new ArgumentException("来源标签无效。", nameof(source));
            var lookup = new Dictionary<(string Repository, string Category, ulong Hash, bool Index2), List<SqPackFileSystemEntry>>();
            foreach (var pack in ListFileSystemContainers(token))
            foreach (var entry in EnumerateFileSystemEntries(pack.Id, token))
            {
                token.ThrowIfCancellationRequested();
                var key = (pack.Repository, pack.Category, entry.Hash, entry.IsIndex2);
                if (!lookup.TryGetValue(key, out var values)) lookup.Add(key, values = []);
                values.Add(entry);
            }
            var records = new Dictionary<string, string>(StringComparer.Ordinal);
            if (File.Exists(VerifiedPathCatalog))
                foreach (var line in ReadBoundedPathLines(VerifiedPathCatalog, token))
                {
                    var split = line.IndexOf('\t');
                    if (split > 0) records.TryAdd(line[..split], line);
                }
            var candidates = 0; var matched = 0; var added = 0; var ambiguous = 0;
            foreach (var line in ReadBoundedPathLines(file, token))
            {
                if (++candidates % 100000 == 0) progress?.Report($"已验证 {candidates:N0} 行，匹配 {matched:N0}，新增 {added:N0}");
                var path = NormalizeFileSystemPath(line);
                if (path is null || GameData.ParseFilePath(path) is not { } parsed) continue;
                lookup.TryGetValue((parsed.Repository, parsed.Category, parsed.IndexHash, false), out var primary);
                lookup.TryGetValue((parsed.Repository, parsed.Category, parsed.Index2Hash, true), out var secondary);
                var matches = (primary ?? []).Concat(secondary ?? []).Where(e =>
                    (e.FullPathHash is not uint full || full == parsed.Index2Hash) &&
                    (e.KnownPath is null || e.KnownPath == path)).ToArray();
                if (matches.Length == 0) continue;
                // 同一候选指向多个不同物理地址时保留为待解析，不能任意选名。
                if (matches.Select(e => (e.Container, e.Offset)).Distinct().Take(2).Count() != 1) { ambiguous++; continue; }
                matched++;
                if (records.TryAdd(path, $"{path}\t{digest}\t{matches[0].Locator}")) added++;
                if (records.Count > 4000000) throw new InvalidDataException("已验证路径缓存超过四百万条限制。");
            }
            if (added == 0) return new(candidates, matched, added, ambiguous, digest);
            Directory.CreateDirectory(PathCoverageDirectory);
            var stage = VerifiedPathCatalog + ".partial";
            try
            {
                using (var writer = new StreamWriter(stage, false, new UTF8Encoding(false)))
                    foreach (var record in records.OrderBy(p => p.Key, StringComparer.Ordinal))
                    {
                        token.ThrowIfCancellationRequested();
                        writer.WriteLine(record.Value);
                        if (writer.BaseStream.Position > 512L * 1024 * 1024) throw new InvalidDataException("已验证路径缓存超过 512 MiB 限制。");
                    }
                token.ThrowIfCancellationRequested();
                File.WriteAllText(Path.Combine(PathCoverageDirectory, digest + ".json"), JsonSerializer.Serialize(new { source = label, sha256 = digest, importedUtc = DateTimeOffset.UtcNow, candidates, matched, added, ambiguous }, Workspace.Json));
                File.Move(stage, VerifiedPathCatalog, true);
            }
            finally { if (File.Exists(stage)) File.Delete(stage); }
            Interlocked.Increment(ref mediaCatalogRevision);
            fileSystemNames = null;
            fileSystemEntries.Clear();
            catalogModelPaths = null;
            modelLocations.Clear();
            imageTextures = null;
            audioLocations.Clear();
            return new(candidates, matched, added, ambiguous, digest);
        }
    }

    private static IEnumerable<string> ReadBoundedPathLines(string file, CancellationToken token)
    {
        if (new FileInfo(file).Length > 512L * 1024 * 1024) throw new InvalidDataException("路径清单超过 512 MiB 限制。");
        using var reader = new StreamReader(file);
        var line = new StringBuilder();
        var lines = 0;
        while (reader.Read() is var value && value >= 0)
        {
            if (value != '\n')
            {
                if (line.Length >= 8192) throw new InvalidDataException("路径清单单行超过 8192 字符限制。");
                line.Append((char)value);
                continue;
            }
            token.ThrowIfCancellationRequested();
            if (++lines > 4000000) throw new InvalidDataException("路径清单超过四百万行限制。");
            yield return line.ToString().TrimEnd('\r');
            line.Clear();
        }
        token.ThrowIfCancellationRequested();
        if (line.Length > 0)
        {
            if (++lines > 4000000) throw new InvalidDataException("路径清单超过四百万行限制。");
            yield return line.ToString();
        }
    }

    private void WriteSheetPathCandidates(TextWriter writer, CancellationToken token)
    {
        (string Sheet, int Column, int Offset, string Directory, string Extension)[] rules =
        [
            ("Omen", 0, 0, "vfx/omen/eff", "avfx"),
            ("Channeling", 0, 0, "vfx/channeling/eff", "avfx"),
            ("Lockon", 0, 0, "vfx/lockon/eff", "avfx"),
            ("Mount", 18, 16, "sound/battle/etc", "scd")
        ];
        foreach (var rule in rules)
        {
            if (!SheetNames.Contains(rule.Sheet)) continue;
            try
            {
                var (header, language) = ReadSheetHeader(rule.Sheet);
                if (header.Header.Variant != ExcelVariant.Default || header.ColumnDefinitions.Length <= rule.Column ||
                    header.ColumnDefinitions[rule.Column].Type != ExcelColumnDataType.String ||
                    header.ColumnDefinitions[rule.Column].Offset != rule.Offset) continue;
                foreach (var row in game.Excel.GetSheet<RawRow>(language, rule.Sheet))
                {
                    token.ThrowIfCancellationRequested();
                    if (row.ReadColumn(rule.Column) is not ReadOnlySeString text) continue;
                    var name = text.ExtractText();
                    if (name.Length is 0 or > 256 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')) continue;
                    writer.WriteLine($"exd/{rule.Sheet.ToLowerInvariant()}.exh#{row.RowId},{rule.Directory}/{name.ToLowerInvariant()}.{rule.Extension}");
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or NotSupportedException)
            {
                // 缺失表、数据页或当前构建不支持的语言不影响其他候选。
            }
        }
    }

    /// <summary>首批提取已知表的短资源名，再按容器顺序读取标准资源前 1 MiB 的完整 ASCII 路径。只保存引用与来源定位符。</summary>
    public SqPackPathReferenceScan ScanFileSystemPathReferences(int maxFiles = 20000, long maxTotalBytes = 256L * 1024 * 1024,
        string? afterLocator = null, CancellationToken token = default, IProgress<string>? progress = null)
    {
        if (maxFiles is < 1 or > 2000000 || maxTotalBytes is < 1 or > 4L * 1024 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maxFiles));
        lock (gate)
        {
            CheckPathCacheBudget(640L * 1024 * 1024);
            Directory.CreateDirectory(PathCoverageDirectory);
            var candidatesFile = Path.Combine(PathCoverageDirectory, "references.csv");
            var stage = candidatesFile + ".partial";
            var examined = 0; var read = 0; var failed = 0; long bytes = 0;
            var reached = afterLocator is null; var complete = true; string? last = afterLocator;
            try
            {
                using (var writer = new StreamWriter(stage, false, new UTF8Encoding(false)))
                {
                    if (afterLocator is null) WriteSheetPathCandidates(writer, token);
                    foreach (var container in ListFileSystemContainers(token))
                    {
                        using var dat = File.OpenRead(fileSystemPacks![container.Id].Pack.FullName);
                        using var headerReader = new BinaryReader(dat);
                        foreach (var entry in EnumerateFileSystemEntries(container.Id, token))
                        {
                            token.ThrowIfCancellationRequested();
                            if (!reached) { if (entry.Locator == afterLocator) reached = true; continue; }
                            if (examined >= maxFiles || bytes >= maxTotalBytes) { complete = false; break; }
                            examined++; last = entry.Locator;
                            try
                            {
                                dat.Position = entry.Offset + 4;
                                var type = headerReader.ReadUInt32();
                                if (type is not (2 or 3)) continue;
                                var limit = (int)Math.Min(1048576, maxTotalBytes - bytes);
                                var content = type == 3 ? ReadModelPathMetadata(headerReader, entry.Offset, limit) :
                                    ReadFileSystemFile(entry, limit, token).Bytes;
                                bytes += content.Length; read++;
                                var paths = type == 3 ? ExtractModelMaterialPaths(content, entry.KnownPath) :
                                    ExtractFileSystemReferences(content).Concat(ExtractMaterialShaderPath(content));
                                foreach (var path in paths) writer.WriteLine($"{entry.Locator},{path}");
                                if (writer.BaseStream.Position > 128L * 1024 * 1024) { complete = false; break; }
                            }
                            catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException) { failed++; }
                            if (examined % 1000 == 0) progress?.Report($"已检查 {examined:N0} 条，读取 {read:N0} 个资源（{Workspace.Size(bytes)}）");
                        }
                        if (!complete) break;
                    }
                }
                if (!reached) throw new ArgumentException("扫描游标不属于当前索引。", nameof(afterLocator));
                token.ThrowIfCancellationRequested();
                // 用内容摘要归档引用证据，后续批次不会覆盖来源文件。
                using var evidence = File.OpenRead(stage);
                var evidenceHash = Convert.ToHexString(SHA256.HashData(evidence)).ToLowerInvariant();
                evidence.Close();
                candidatesFile = Path.Combine(PathCoverageDirectory, "references-" + evidenceHash + ".csv");
                File.Move(stage, candidatesFile, true);
                var imported = ImportFileSystemPaths(candidatesFile, "游戏资源直接引用", token, progress);
                var position = Path.Combine(PathCoverageDirectory, "scan-position.json");
                var positionStage = position + ".partial";
                try
                {
                    File.WriteAllText(positionStage, JsonSerializer.Serialize(new { lastLocator = last, complete }, Workspace.Json));
                    File.Move(positionStage, position, true);
                }
                finally { if (File.Exists(positionStage)) File.Delete(positionStage); }
                return new(examined, read, failed, bytes, last, complete, imported);
            }
            finally { if (File.Exists(stage)) File.Delete(stage); }
        }
    }

    private static IEnumerable<string> ExtractFileSystemReferences(byte[] bytes)
    {
        var start = 0;
        for (var end = 0; end < bytes.Length; end++)
        {
            if (bytes[end] is >= 32 and <= 126) continue;
            if (end - start is > 5 and <= 1024)
            {
                var text = Encoding.ASCII.GetString(bytes, start, end - start);
                foreach (var category in FileSystemCategories)
                {
                    var prefix = category + "/";
                    var index = text.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
                    if (index < 0) continue;
                    var path = NormalizeFileSystemPath(text[index..]);
                    if (path is not null && !path.Contains(' ') && Path.HasExtension(path)) yield return path;
                }
            }
            start = end + 1;
        }
    }
}
