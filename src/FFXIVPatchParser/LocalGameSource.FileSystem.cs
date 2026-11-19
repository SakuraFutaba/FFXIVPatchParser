using System.IO.Compression;
using Lumina;
using Lumina.Data;
using Lumina.Data.Files.Excel;

namespace FFXIVPatchParser;

public sealed record SqPackFileSystemContainer(string Id, string Repository, string Category, string IndexName, int EntryCount);
public readonly record struct SqPackFileSystemEntry(string Container, long Offset, ulong Hash, bool IsIndex2, string? KnownPath)
{
    public string Path => KnownPath ?? Locator;
    public string Locator => $"sqpack:{Container}@{Offset:X}#{Hash:X16}";
    public uint? FolderHash => IsIndex2 ? null : (uint)(Hash >> 32);
    public uint FileHash => (uint)Hash;
    public uint? FullPathHash { get; init; }
}
public sealed record SqPackFileSystemPreview(string Path, string FileType, long Size, byte[] Bytes, bool Truncated, long Offset);

public sealed partial class LocalGameSource
{
    private static readonly string[] FileSystemCategories = ["common", "bgcommon", "bg", "cut", "chara", "shader", "ui", "sound", "vfx", "ui_script", "exd", "game_script", "music"];
    private sealed record FileSystemPack(SqPack Pack, Category Category, byte Dat, SqPackFileSystemContainer Info);
    private sealed class FileSystemNameTable
    {
        public readonly Dictionary<ulong, (string? Path, uint FullHash)> Primary = [];
        public readonly Dictionary<uint, (string? Path, ulong SplitHash)> Secondary = [];
    }
    private Dictionary<string, FileSystemPack>? fileSystemPacks;
    private readonly Dictionary<string, IReadOnlyList<SqPackFileSystemEntry>> fileSystemEntries = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<(string Repository, string Category), FileSystemNameTable>? fileSystemNames;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> fileSystemModelContexts = new(StringComparer.OrdinalIgnoreCase);
    private static string FileSystemPathCatalog => System.IO.Path.Combine(Workspace.Temp, "file-paths", "catalog.txt");

    /// <summary>只统计索引中指向实际 dat 的地址；不读取所有资源的文件头。</summary>
    public IReadOnlyList<SqPackFileSystemContainer> ListFileSystemContainers(CancellationToken token = default)
    {
        lock (gate)
        {
            if (fileSystemPacks is null)
            {
                var packs = new Dictionary<string, FileSystemPack>(StringComparer.OrdinalIgnoreCase);
                foreach (var (repositoryName, repository) in game.Repositories)
                foreach (var (categoryId, categories) in repository.Categories)
                foreach (var category in categories)
                {
                    token.ThrowIfCancellationRequested();
                    var counts = new Dictionary<byte, int>();
                    var lengths = category.DatFiles.ToDictionary(pair => pair.Key, pair => new FileInfo(pair.Value.FullName).Length);
                    foreach (var row in FileSystemMergedIndexEntries(category, token))
                    {
                        var data = row.Data;
                        if ((data & 1) != 0) continue; // synonym 的 data 是碰撞表索引，不能当作 dat 地址。
                        var dat = (byte)((data & 14) >> 1);
                        var address = (long)(data & ~15u) * 8;
                        if (!lengths.TryGetValue(dat, out var length) || address < 0x800 || address > length - 24) continue;
                        counts[dat] = counts.GetValueOrDefault(dat) + 1;
                    }
                    foreach (var (dat, count) in counts)
                    {
                        if (category.GetDat(dat) is not { } pack) continue;
                        var id = System.IO.Path.GetRelativePath(SqPackPath, pack.FullName).Replace('\\', '/');
                        var name = categoryId < FileSystemCategories.Length ? FileSystemCategories[categoryId] : $"0x{categoryId:x2}";
                        packs.TryAdd(id, new(pack, category, dat, new(id, repositoryName, name, category.Index.Name, count)));
                    }
                }
                fileSystemPacks = packs;
            }
            return fileSystemPacks.Values.Select(p => p.Info).OrderBy(p => p.Repository, StringComparer.Ordinal).ThenBy(p => p.Id, StringComparer.Ordinal).ToArray();
        }
    }

    private static IEnumerable<(ulong Hash, uint Data, string? Path)> FileSystemIndexEntries(Category category)
    {
        if (category.Index.IsIndex2)
        {
            foreach (var entry in category.Index2HashTableEntries?.Values.AsEnumerable() ?? []) yield return (entry.hash, entry.data, null);
        }
        else
            foreach (var entry in category.IndexHashTableEntries?.Values.AsEnumerable() ?? []) yield return (entry.hash, entry.data, null);

        // 碰撞表中的文本地址由索引提供；校验哈希后才采用，哨兵不能当成资源。
        var header = category.Index.IndexHeader;
        if (header.synonym_data_size == 0) yield break;
        using var stream = File.OpenRead(category.Index.FullName);
        if (header.synonym_data_size % 256 != 0 || (long)header.synonym_data_offset + header.synonym_data_size > stream.Length)
            throw new InvalidDataException("SqPack 碰撞表越界。");
        using var reader = new BinaryReader(stream);
        stream.Position = header.synonym_data_offset;
        for (var index = 0u; index < header.synonym_data_size / 256; index++)
        {
            var hash = reader.ReadUInt64();
            var data = reader.ReadUInt32();
            var conflictIndex = reader.ReadUInt32();
            var bytes = reader.ReadBytes(240);
            if (conflictIndex == uint.MaxValue || (data & 1) != 0) continue;
            var end = Array.IndexOf(bytes, (byte)0);
            var path = NormalizeFileSystemPath(System.Text.Encoding.UTF8.GetString(bytes, 0, end < 0 ? bytes.Length : end));
            if (path is null || GameData.ParseFilePath(path) is not { } parsed) continue;
            if (category.Index.IsIndex2) hash = (uint)hash;
            if (hash != (category.Index.IsIndex2 ? parsed.Index2Hash : parsed.IndexHash)) continue;
            var hasPointer = category.Index.IsIndex2
                ? category.Index2HashTableEntries?.TryGetValue((uint)hash, out var pointer2) == true && pointer2.IsSynonym
                : category.IndexHashTableEntries?.TryGetValue(hash, out var pointer1) == true && pointer1.IsSynonym;
            if (!hasPointer) continue;
            yield return (hash, data, path);
        }
    }

    private readonly record struct FileSystemIndexRow(ulong Hash, uint Data, string? Path, bool IsIndex2, uint? FullPathHash);

    private static IEnumerable<FileSystemIndexRow> FileSystemMergedIndexEntries(Category category, CancellationToken token)
    {
        var primary = FileSystemIndexEntries(category).Where(e => (e.Data & 1) == 0)
            .Select(e => new FileSystemIndexRow(e.Hash, e.Data, e.Path, category.Index.IsIndex2, category.Index.IsIndex2 ? (uint)e.Hash : null));
        var companion = System.IO.Path.ChangeExtension(category.Index.FullName, ".index2");
        if (category.Index.IsIndex2 || !File.Exists(companion))
        {
            foreach (var row in primary) { token.ThrowIfCancellationRequested(); yield return row; }
            yield break;
        }
        var first = primary.OrderBy(e => e.Data).ToArray();
        var second = ReadFileSystemIndex2(companion, token).OrderBy(e => e.Data).ToArray();
        var a = 0; var b = 0;
        while (a < first.Length || b < second.Length)
        {
            token.ThrowIfCancellationRequested();
            if (b == second.Length || (a < first.Length && first[a].Data < second[b].Data)) { yield return first[a++]; continue; }
            if (a == first.Length || second[b].Data < first[a].Data) { yield return second[b++]; continue; }
            var aEnd = a + 1; var bEnd = b + 1;
            while (aEnd < first.Length && first[aEnd].Data == first[a].Data) aEnd++;
            while (bEnd < second.Length && second[bEnd].Data == second[b].Data) bEnd++;
            // 一个地址只有两侧各一个别名时才能唯一配对；多别名全部保留，不任意选名。
            if (aEnd == a + 1 && bEnd == b + 1 &&
                (second[b].Path is not { } secondPath || GameData.ParseFilePath(secondPath)!.IndexHash == first[a].Hash) &&
                (first[a].Path is not { } firstPath || GameData.ParseFilePath(firstPath)!.Index2Hash == second[b].Hash))
                yield return first[a] with { FullPathHash = (uint)second[b].Hash, Path = first[a].Path ?? second[b].Path };
            else
            {
                for (var i = a; i < aEnd; i++) yield return first[i];
                for (var i = b; i < bEnd; i++) yield return second[i];
            }
            a = aEnd; b = bEnd;
        }
    }

    private static IEnumerable<FileSystemIndexRow> ReadFileSystemIndex2(string path, CancellationToken token)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (stream.Length < 24 || reader.ReadUInt64() != 0x00006b6361507153UL) throw new InvalidDataException("无效的 SqPack index2。");
        if (reader.ReadByte() != 0) throw new NotSupportedException("文件系统副索引仅支持本地 Win32 格式。");
        stream.Position = 12;
        var header = reader.ReadUInt32();
        if (header > stream.Length - 0x5c) throw new InvalidDataException("SqPack index2 文件头越界。");
        stream.Position = header + 8;
        var tableOffset = reader.ReadUInt32(); var tableSize = reader.ReadUInt32();
        stream.Position = header + 0x54;
        var synonymsOffset = reader.ReadUInt32(); var synonymsSize = reader.ReadUInt32();
        if (tableSize % 8 != 0 || (long)tableOffset + tableSize > stream.Length || synonymsSize % 256 != 0 || (long)synonymsOffset + synonymsSize > stream.Length)
            throw new InvalidDataException("SqPack index2 索引表越界。");
        var pointers = new HashSet<uint>();
        stream.Position = tableOffset;
        for (var i = 0u; i < tableSize / 8; i++)
        {
            token.ThrowIfCancellationRequested();
            var hash = reader.ReadUInt32(); var data = reader.ReadUInt32();
            if ((data & 1) != 0) pointers.Add(hash);
            else yield return new(hash, data, null, true, hash);
        }
        stream.Position = synonymsOffset;
        for (var i = 0u; i < synonymsSize / 256; i++)
        {
            token.ThrowIfCancellationRequested();
            var hash = reader.ReadUInt32(); reader.ReadUInt32();
            var data = reader.ReadUInt32(); var conflictIndex = reader.ReadUInt32();
            var bytes = reader.ReadBytes(240);
            if (conflictIndex == uint.MaxValue || (data & 1) != 0 || !pointers.Contains(hash)) continue;
            var end = Array.IndexOf(bytes, (byte)0);
            var known = NormalizeFileSystemPath(System.Text.Encoding.UTF8.GetString(bytes, 0, end < 0 ? bytes.Length : end));
            if (known is null || GameData.ParseFilePath(known)!.Index2Hash != hash) continue;
            yield return new(hash, data, known, true, hash);
        }
    }

    /// <summary>每个容器只建立一次轻量索引。未知名称始终保留哈希，不推测扩展名。</summary>
    public IReadOnlyList<SqPackFileSystemEntry> EnumerateFileSystemEntries(string containerId, CancellationToken token = default)
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            ListFileSystemContainers(token);
            if (fileSystemEntries.TryGetValue(containerId, out var cached)) return cached;
            if (!fileSystemPacks!.TryGetValue(containerId, out var pack)) throw new FileNotFoundException("未知 SqPack 容器。");
            EnsureFileSystemNames(token);
            fileSystemNames!.TryGetValue((pack.Info.Repository, pack.Info.Category), out var names);
            var siblings = fileSystemPacks.Values.Where(p => ReferenceEquals(p.Category, pack.Category)).ToDictionary(p => p.Dat);
            var lengths = siblings.ToDictionary(p => p.Key, p => new FileInfo(p.Value.Pack.FullName).Length);
            var results = siblings.ToDictionary(p => p.Key, _ => new List<SqPackFileSystemEntry>());
            foreach (var row in FileSystemMergedIndexEntries(pack.Category, token))
            {
                token.ThrowIfCancellationRequested();
                var data = row.Data;
                var dat = (byte)((data & 14) >> 1);
                if (!siblings.TryGetValue(dat, out var sibling)) continue;
                var offset = (long)(data & ~15u) * 8;
                if (offset < 0x800 || offset > lengths[dat] - 24) continue;
                var known = row.Path;
                if (known is null && names is not null)
                {
                    if (row.IsIndex2) known = names.Secondary.GetValueOrDefault((uint)row.Hash).Path;
                    else
                    {
                        var primary = names.Primary.GetValueOrDefault(row.Hash);
                        if (row.FullPathHash is not uint fullHash || primary.FullHash == fullHash) known = primary.Path;
                        if (known is null && row.FullPathHash is uint alternateHash &&
                            names.Secondary.TryGetValue(alternateHash, out var alternate) && alternate.SplitHash == row.Hash)
                            known = alternate.Path;
                    }
                }
                results[dat].Add(new(sibling.Info.Id, offset, row.Hash, row.IsIndex2, known) { FullPathHash = row.FullPathHash });
            }
            foreach (var (dat, result) in results)
                fileSystemEntries[siblings[dat].Info.Id] = Array.AsReadOnly(result.OrderBy(e => e.Offset).ThenBy(e => e.Hash).ThenBy(e => e.IsIndex2).ToArray());
            return fileSystemEntries[containerId];
        }
    }

    private void EnsureFileSystemNames(CancellationToken token)
    {
        if (fileSystemNames is not null) return;
        var names = new Dictionary<(string, string), FileSystemNameTable>();
        void Add(string value)
        {
            token.ThrowIfCancellationRequested();
            var path = NormalizeFileSystemPath(value);
            if (path is null || GameData.ParseFilePath(path) is not { } parsed) return;
            var key = (parsed.Repository, parsed.Category);
            if (!names.TryGetValue(key, out var table)) names.Add(key, table = new());
            if (!table.Primary.TryAdd(parsed.IndexHash, (path, parsed.Index2Hash)) && table.Primary[parsed.IndexHash].Path != path)
                table.Primary[parsed.IndexHash] = default;
            if (!table.Secondary.TryAdd(parsed.Index2Hash, (path, parsed.IndexHash)) && table.Secondary[parsed.Index2Hash].Path != path)
                table.Secondary[parsed.Index2Hash] = default;
        }
        Add("exd/root.exl");
        foreach (var name in SheetNames)
        {
            Add($"exd/{name}.exh");
            var header = game.GetFile<ExcelHeaderFile>($"exd/{name}.exh");
            if (header is null) continue;
            foreach (var page in header.DataPages)
            foreach (var language in header.Languages)
            {
                if (!LanguageUtil.LanguageMap.TryGetValue(language, out var code)) continue;
                Add($"exd/{name}_{page.StartId}{(code.Length == 0 ? "" : "_" + code)}.exd");
            }
        }
        try
        {
            foreach (var map in ListNamedMaps(token))
            {
                Add(map.ScenePath);
                if (map.MapTexturePath is { } texture) Add(texture);
            }
        }
        catch (Exception ex) when (ex is NotSupportedException or FileNotFoundException) { }
        foreach (var path in ListIcons(token)) Add(path);
        foreach (var file in new[] { ModelPathCatalog, FileSystemPathCatalog })
            if (File.Exists(file)) foreach (var line in File.ReadLines(file)) Add(line);
        if (File.Exists(VerifiedPathCatalog))
            foreach (var line in ReadBoundedPathLines(VerifiedPathCatalog, token))
            {
                var end = line.IndexOf('\t');
                if (end > 0) Add(line[..end]);
            }
        if (namedModels is not null) foreach (var entry in namedModels) Add(entry.Path);
        foreach (var path in GetMusicNames(token).Values) Add(path);
        knownModelNames ??= BuildKnownModelNames(token);
        foreach (var path in knownModelNames.Values) Add(path);
        fileSystemNames = names;
    }

    private static string? NormalizeFileSystemPath(string value)
    {
        // 也接受路径位于最后一列的 CSV 和 TSV。
        var separator = Math.Max(value.LastIndexOf(','), value.LastIndexOf('\t'));
        var path = value[(separator + 1)..].Trim().Trim('"').Replace('\\', '/').ToLowerInvariant();
        if (path.Length is 0 or > 1024 || !path.Contains('/') || path.StartsWith('/') || path.Contains(':')) return null;
        var parts = path.Split('/');
        if (parts.Any(p => p is "" or "." or "..")) return null;
        // Lumina 7.7.2 检查扩展包时直接读取第二段的前三个字符；截断的 e/ex 会越界。
        if (parts[1] is "e" or "ex") return null;
        return GameData.ParseFilePath(path) is null ? null : path;
    }

    /// <summary>按准确索引地址读取所选文件。标准文件只解压与请求窗口相交的块。</summary>
    public SqPackFileSystemPreview ReadFileSystemFile(SqPackFileSystemEntry entry, int maxBytes = 4096, CancellationToken token = default, long offset = 0)
    {
        if (maxBytes is < 1 or > 1048576 || offset < 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            var pack = ValidateFileEntry(entry, token);
            using var stream = File.OpenRead(pack.Pack.FullName);
            using var reader = new BinaryReader(stream);
            stream.Position = entry.Offset;
            var header = reader.ReadUInt32();
            var type = reader.ReadUInt32();
            var size = reader.ReadUInt32();
            if (header < 24 || header > stream.Length - entry.Offset) throw new InvalidDataException("SqPack 文件头越界。");
            if (type == 1) return new(entry.Locator, "Empty", 0, [], false, offset);
            if (type is < 2 or > 4) throw new InvalidDataException("未知 SqPack 文件类型。");
            var count = (int)Math.Min(maxBytes, Math.Max(0L, size - offset));
            byte[] bytes;
            if (type == 2)
            {
                using var page = new MemoryStream(count);
                CopyFileSystemStandard(reader, entry.Offset, header, size, offset, count, page, token);
                bytes = page.ToArray();
            }
            else
            {
                if (size > 256 * 1024 * 1024) throw new NotSupportedException("模型或纹理超过 256 MiB 单文件预览限制。");
                var file = pack.Pack.ReadFile<FileResource>(entry.Offset);
                using (file.Reader) bytes = file.Data.AsSpan((int)Math.Min(offset, file.Data.Length), count).ToArray();
            }
            var locator = entry.Locator;
            if (type == 3)
            {
                modelAddresses[locator] = (pack.Pack, entry.Offset);
                if (entry.KnownPath is { } context) fileSystemModelContexts[locator] = context;
            }
            var isTexture = IsImageTexture(type, entry.KnownPath);
            if (isTexture) textureAddresses[locator] = (pack.Pack, entry.Offset);
            if (entry.KnownPath?.EndsWith(".scd", StringComparison.OrdinalIgnoreCase) == true || audioAddresses.ContainsKey(locator + ".scd") ||
                (offset == 0 && bytes.AsSpan().StartsWith("SEDBSSCF"u8)))
            {
                locator = locator.ToLowerInvariant() + ".scd";
                audioAddresses[locator] = (pack.Pack, entry.Offset);
            }
            return new(locator, isTexture ? "Texture" : type == 3 ? "Model" : "Standard", size, bytes, offset + bytes.Length < size, offset);
        }
    }

    private FileSystemPack ValidateFileEntry(SqPackFileSystemEntry entry, CancellationToken token)
    {
        ListFileSystemContainers(token);
        if (!fileSystemPacks!.TryGetValue(entry.Container, out var pack)) throw new FileNotFoundException("未知 SqPack 容器。");
        // 校验选择属于此索引，避免让自造 locator 访问任意偏移。
        var entries = EnumerateFileSystemEntries(entry.Container, token);
        var lo = 0; var hi = entries.Count - 1; var found = false;
        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            var candidate = entries[mid];
            var comparison = candidate.Offset.CompareTo(entry.Offset);
            if (comparison == 0) comparison = candidate.Hash.CompareTo(entry.Hash);
            if (comparison == 0) comparison = candidate.IsIndex2.CompareTo(entry.IsIndex2);
            if (comparison == 0) { found = candidate.IsIndex2 == entry.IsIndex2; break; }
            if (comparison < 0) lo = mid + 1; else hi = mid - 1;
        }
        if (!found) throw new InvalidDataException("该地址不属于当前索引。");
        return pack;
    }

    public long ExportFileSystemFile(SqPackFileSystemEntry entry, string destination, bool overwrite, CancellationToken token = default)
    {
        destination = Path.GetFullPath(destination);
        FileSystemPack pack;
        lock (gate) pack = ValidateFileEntry(entry, token);
        if (string.Equals(destination, Path.GetFullPath(pack.Pack.FullName),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("不能把导出目标设成源 SqPack 容器。");
        token.ThrowIfCancellationRequested();
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, "." + Guid.NewGuid().ToString("N") + ".partial");
        using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        try
        {
            using var stream = File.OpenRead(pack.Pack.FullName);
            using var reader = new BinaryReader(stream);
            stream.Position = entry.Offset;
            var header = reader.ReadUInt32();
            var type = reader.ReadUInt32();
            var size = reader.ReadUInt32();
            if (header < 24 || header > stream.Length - entry.Offset) throw new InvalidDataException("SqPack 文件头越界。");
            if (type == 2) CopyFileSystemStandard(reader, entry.Offset, header, size, 0, size, output, token);
            else if (type is 3 or 4)
            {
                if (size > 256 * 1024 * 1024) throw new NotSupportedException("模型或纹理超过 256 MiB 单文件导出限制。");
                byte[] bytes;
                lock (gate)
                {
                    token.ThrowIfCancellationRequested();
                    var resource = pack.Pack.ReadFile<FileResource>(entry.Offset);
                    using (resource.Reader) bytes = resource.Data;
                }
                if (bytes.LongLength != size) throw new InvalidDataException("解包后的长度与文件头不符。");
                for (var i = 0; i < bytes.Length; i += 65536)
                {
                    token.ThrowIfCancellationRequested();
                    output.Write(bytes, i, Math.Min(65536, bytes.Length - i));
                }
            }
            else if (type != 1) throw new InvalidDataException("未知 SqPack 文件类型。");
            var length = output.Length;
            output.Dispose();
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite);
            return length;
        }
        finally
        {
            output.Dispose();
            File.Delete(temporary);
        }
    }

    private static void CopyFileSystemStandard(BinaryReader reader, long start, uint header, uint size, long requestedOffset, long count, Stream destination, CancellationToken token)
    {
        var stream = reader.BaseStream;
        stream.Position = start + 20;
        var blocks = reader.ReadUInt32();
        if (24L + blocks * 8L > header) throw new InvalidDataException("SqPack 块表越界。");
        long decoded = 0;
        long copied = 0;
        for (var i = 0u; i < blocks && copied < count; i++)
        {
            token.ThrowIfCancellationRequested();
            stream.Position = start + 24 + i * 8L;
            var blockOffset = reader.ReadUInt32();
            reader.ReadUInt16();
            var declaredSize = reader.ReadUInt16();
            if (decoded + declaredSize <= requestedOffset) { decoded += declaredSize; continue; }
            var address = start + header + blockOffset;
            if (address > stream.Length - 16) throw new InvalidDataException("SqPack 块偏移越界。");
            stream.Position = address;
            var blockHeader = reader.ReadUInt32(); reader.ReadUInt32();
            var compressed = reader.ReadUInt32();
            var raw = reader.ReadUInt32();
            if (blockHeader < 16 || raw != declaredSize || raw > 65536 || decoded + raw > size || address + blockHeader > stream.Length) throw new InvalidDataException("SqPack 块长度异常。");
            stream.Position = address + blockHeader;
            var block = new byte[raw];
            if (compressed == 32000) stream.ReadExactly(block);
            else
            {
                if (compressed == 0 || compressed > 65536 || compressed > stream.Length - stream.Position) throw new InvalidDataException("SqPack 压缩块越界。");
                using var compressedStream = new MemoryStream(reader.ReadBytes((int)compressed), writable: false);
                using var inflate = new DeflateStream(compressedStream, CompressionMode.Decompress);
                inflate.ReadExactly(block);
            }
            var skip = (int)Math.Max(0, requestedOffset - decoded);
            var take = (int)Math.Min(count - copied, block.Length - skip);
            destination.Write(block, skip, take);
            copied += take; decoded += raw;
        }
        if (copied != count) throw new InvalidDataException("SqPack 文件块不足。");
    }
}
