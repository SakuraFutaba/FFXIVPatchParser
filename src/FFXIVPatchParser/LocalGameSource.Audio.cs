using System.Buffers.Binary;
using System.IO.Compression;
using System.Globalization;
using System.Runtime.InteropServices;
using Lumina;
using Lumina.Data;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Scd;
using Lumina.Data.Structs;
using Lumina.Data.Structs.Excel;
using Lumina.Excel;
using Lumina.Text.ReadOnly;

namespace FFXIVPatchParser;

public sealed record SqPackAudioLocation(string Path, string Container, long Offset, long End);
public sealed record AudioLoop(long StartSample, long EndSample);
public sealed record GameAudio(string Path, int TrackIndex, int TrackCount, string Codec, uint Channels, uint SampleRate,
    string Extension, byte[] Data, AudioLoop? Loop = null);

public sealed partial class LocalGameSource
{
    private const AudioFormat Pcm16LittleEndian = (AudioFormat)1;
    private const string SampleAudioPath = "music/ex2/bgm_ex2_system_title.scd";
    private IReadOnlyList<(uint Id, string Path)>? bgmPaths;
    private Dictionary<ulong, string>? musicNames;
    private readonly Dictionary<string, IReadOnlyList<SqPackAudioLocation>> audioLocations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (SqPack Pack, long Offset)> audioAddresses = new(StringComparer.OrdinalIgnoreCase);

    public static bool BgmPathMatches(string bgmPath, string resourcePath)
    {
        if (bgmPath.Equals(resourcePath, StringComparison.OrdinalIgnoreCase)) return true;
        if (!resourcePath.StartsWith("sqpack:", StringComparison.OrdinalIgnoreCase) || !resourcePath.EndsWith(".scd", StringComparison.OrdinalIgnoreCase)) return false;
        var hashStart = resourcePath.LastIndexOf('#');
        if (hashStart < 0 || !ulong.TryParse(resourcePath.AsSpan(hashStart + 1, resourcePath.Length - hashStart - 5),
            NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hash)) return false;
        if (GameData.ParseFilePath(bgmPath) is not { } parsed || parsed.IndexHash != hash) return false;
        var category = Array.IndexOf(FileSystemCategories, parsed.Category);
        return category >= 0 && resourcePath.StartsWith($"sqpack:{parsed.Repository}/{category:x2}", StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<(uint Id, string Path)> ListBgmPaths(CancellationToken token = default)
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            if (bgmPaths is not null) return bgmPaths;
            var result = new List<(uint, string)>();
            if (!SheetNames.Contains("BGM")) return result;
            var (header, language) = ReadSheetHeader("BGM");
            foreach (var row in game.Excel.GetSheet<RawRow>(language, "BGM"))
            {
                token.ThrowIfCancellationRequested();
                for (var column = 0; column < header.ColumnDefinitions.Length; column++)
                {
                    if (row.ReadColumn(column) is not ReadOnlySeString text) continue;
                    if (NormalizeFileSystemPath(text.ExtractText()) is { } path &&
                        path.EndsWith(".scd", StringComparison.Ordinal) && game.FileExists(path))
                        result.Add((row.RowId, path));
                }
            }
            return bgmPaths = result.AsReadOnly();
        }
    }

    public IReadOnlyList<string> ListNamedAudioPaths(CancellationToken token = default)
    {
        lock (gate)
            return GetMusicNames(token).Values.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public IReadOnlyList<(uint Id, string Path, string Title)> ListOrchestrionTitles(CancellationToken token = default)
    {
        lock (gate)
        {
            if (!SheetNames.Contains("Orchestrion") || !SheetNames.Contains("OrchestrionPath")) return [];
            var (titleHeader, titleLanguage) = ReadSheetHeader("Orchestrion");
            var (pathHeader, pathLanguage) = ReadSheetHeader("OrchestrionPath");
            if (titleHeader.Header.Variant != ExcelVariant.Default ||
                !titleHeader.ColumnDefinitions.Select(c => (c.Type, c.Offset)).SequenceEqual(
                    [(ExcelColumnDataType.String, (ushort)0), (ExcelColumnDataType.String, (ushort)4)]) ||
                pathHeader.Header.Variant != ExcelVariant.Default ||
                !pathHeader.ColumnDefinitions.Select(c => (c.Type, c.Offset)).SequenceEqual(
                    [(ExcelColumnDataType.String, (ushort)0)])) return [];
            var titles = game.Excel.GetSheet<RawRow>(titleLanguage, "Orchestrion");
            var result = new List<(uint, string, string)>();
            foreach (var row in game.Excel.GetSheet<RawRow>(pathLanguage, "OrchestrionPath"))
            {
                token.ThrowIfCancellationRequested();
                var path = NormalizeFileSystemPath(((ReadOnlySeString)row.ReadColumn(0)).ExtractText());
                if (path?.EndsWith(".scd", StringComparison.Ordinal) != true ||
                    !titles.TryGetRow(row.RowId, out var named) || !game.FileExists(path)) continue;
                var title = ((ReadOnlySeString)named.ReadColumn(0)).ExtractText().Trim();
                if (title.Length > 0) result.Add((row.RowId, path, title));
            }
            return result;
        }
    }

    public IReadOnlyList<SqPackAudioLocation> ListAudioLocations(CancellationToken token = default, IReadOnlySet<string>? containers = null,
        IProgress<string>? progress = null, Func<string, long, long, bool>? includeRange = null)
    {
        lock (gate)
        {
            var wanted = containers?.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (wanted?.Count == 0) return [];
            var result = new List<SqPackAudioLocation>();
            foreach (var repo in game.Repositories.Values)
            {
                foreach (var categoryId in new byte[] { 3, 7, 12 }) // cut, sound, music
                {
                    if (!repo.Categories.TryGetValue(categoryId, out var categories)) continue;
                    foreach (var category in categories)
                    {
                        token.ThrowIfCancellationRequested();
                        var offsets = new Dictionary<(byte Dat, long Offset), ulong>();
                        foreach (var hash in category.IndexHashTableEntries?.Keys.AsEnumerable() ?? [])
                        {
                            token.ThrowIfCancellationRequested();
                            if (category.TryGetFileDatOffset(hash, out var dat, out var offset))
                                offsets.TryAdd((dat, offset), hash);
                        }
                        foreach (var group in offsets.GroupBy(entry => entry.Key.Dat))
                        {
                            if (category.GetDat(group.Key) is not { } pack) continue;
                            var container = Path.GetRelativePath(SqPackPath, pack.FullName).Replace('\\', '/');
                            if (wanted is not null && !wanted.Contains(container)) continue;
                            if (!audioLocations.TryGetValue(container, out var entries))
                            {
                                progress?.Report("正在扫描音频容器 · " + container);
                                var ordered = group.OrderBy(entry => entry.Key.Offset).ToArray();
                                entries = ScanAudioContainer(pack, container, ordered, GetMusicNames(token), token, includeRange);
                                if (includeRange is null) audioLocations.Add(container, entries);
                            }
                            result.AddRange(includeRange is null ? entries : entries.Where(item => includeRange(item.Container, item.Offset, item.End)));
                        }
                    }
                }
            }
            return result;
        }
    }

    public GameAudio ReadAudio(string path, int? trackIndex = null, CancellationToken token = default)
    {
        path = path.Trim().Replace('\\', '/').TrimStart('/').ToLowerInvariant();
        if (!path.EndsWith(".scd", StringComparison.Ordinal) || !path.Contains('/'))
            throw new ArgumentException("请输入 SqPack 内的 .scd 路径。", nameof(path));

        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            ScdFile? file;
            try
            {
                if (path.StartsWith("sqpack:", StringComparison.Ordinal))
                {
                    if (!audioAddresses.TryGetValue(path, out var address))
                        address = ResolveAudioAddress(path, token);
                    file = address.Pack?.ReadFile<ScdFile>(address.Offset);
                }
                else file = game.GetFile<ScdFile>(path);
            }
            catch (SqPackInflateException ex) { throw new InvalidDataException("无法解压 SqPack 音频。", ex); }

            if (file is null) throw new FileNotFoundException("未找到音频：" + path);
            using var reader = file.Reader;
            if (!file.Data.AsSpan().StartsWith("SEDBSSCF"u8)) throw new InvalidDataException("无效的 SCD 文件头。");
            var count = file.AudioDataCount;
            if (trackIndex is int requested && (requested < 0 || requested >= count))
                throw new ArgumentOutOfRangeException(nameof(trackIndex), "音轨编号越界。");
            for (var index = trackIndex ?? 0; index < (trackIndex + 1 ?? count); index++)
            {
                token.ThrowIfCancellationRequested();
                var audio = file.GetAudio(index);
                var description = audio.AudioBasicDesc;
                if (audio.AudioData.Length == 0)
                {
                    if (trackIndex is not null) throw new NotSupportedException("此音轨没有音频数据。");
                    continue;
                }
                switch (description.Format)
                {
                    case AudioFormat.OggVorbis when audio.AudioData.AsSpan().StartsWith("OggS"u8):
                        return new(path, index, count, "Ogg Vorbis", description.Channel, description.Rate, ".ogg", audio.AudioData, ReadVorbisLoop(audio.AudioData));
                    case AudioFormat.Mp3:
                        return new(path, index, count, "MP3", description.Channel, description.Rate, ".mp3", audio.AudioData);
                    case AudioFormat.MsAdpcm when audio.AudioDataHeader is AdpcmWaveFormat wave:
                        var adpcm = WrapAdpcm(wave, audio.AudioData);
                        long AdpcmSample(long bytes)
                        {
                            var remainder = bytes % wave.BlockAlign;
                            return bytes / wave.BlockAlign * wave.SamplesPerBlock +
                                (remainder >= 7 * wave.Channels ? 2 + (remainder - 7 * wave.Channels) * 2 / wave.Channels : 0);
                        }
                        return new(path, index, count, "MS ADPCM", description.Channel, description.Rate, ".wav", adpcm,
                            description.LoopEnd <= audio.AudioData.Length ? ValidLoop(AdpcmSample(description.LoopStart), AdpcmSample(description.LoopEnd)) : null);
                    case (AudioFormat)0x1A:
                        var end = checked((int)reader.Position);
                        var start = end - audio.AudioData.Length;
                        var extra = checked((int)description.SubInfoSize);
                        var markerSize = checked((int)(audio.MarkerChunkHeader?.Header.Size ?? 0));
                        if (audio.AudioData.Length != description.Size || extra > start || markerSize > extra)
                            throw new InvalidDataException("SCD 的 HCA 音轨长度无效。");
                        var hca = UnwrapHca(file.Data.AsSpan(start - extra + markerSize, extra - markerSize), audio.AudioData);
                        return new(path, index, count, "HCA", description.Channel, description.Rate, ".hca", hca);
                    case Pcm16LittleEndian:
                        var pcm = WrapPcm(description, audio.AudioData);
                        var align = 2 * description.Channel;
                        return new(path, index, count, "PCM 16-bit", description.Channel, description.Rate, ".wav", pcm,
                            description.LoopEnd <= audio.AudioData.Length ? ValidLoop(description.LoopStart / align, description.LoopEnd / align) : null);
                }
                if (trackIndex is not null) throw new NotSupportedException($"无法预览此音轨的编码（{description.Format}）。");
            }
            throw new NotSupportedException("此 SCD 没有可预览的音轨。");
        }
    }

    private static byte[] UnwrapHca(ReadOnlySpan<byte> extra, byte[] frames)
    {
        if (extra.Length < 4) throw new InvalidDataException("SCD 缺少 HCA 扩展头。");
        var offset = extra[1];
        var length = BinaryPrimitives.ReadUInt16LittleEndian(extra[2..]);
        if (offset < 4 || length < 8 || offset + length != extra.Length)
            throw new InvalidDataException("SCD 的 HCA 文件头长度无效。");
        var header = extra[offset..];
        if (!header.StartsWith("HCA\0"u8) || BinaryPrimitives.ReadUInt16BigEndian(header[6..]) != length)
            throw new InvalidDataException("无法识别 SCD 内的 HCA 文件头。");
        // SCD HCA 帧使用与 SCD Ogg V3 相同的查找表；表索引包含明文 HCA 头长度。
        ReadOnlySpan<byte> table = [
            0x3A, 0x32, 0x32, 0x32, 0x03, 0x7E, 0x12, 0xF7, 0xB2, 0xE2, 0xA2, 0x67, 0x32, 0x32, 0x22, 0x32,
            0x32, 0x52, 0x16, 0x1B, 0x3C, 0xA1, 0x54, 0x7B, 0x1B, 0x97, 0xA6, 0x93, 0x1A, 0x4B, 0xAA, 0xA6,
            0x7A, 0x7B, 0x1B, 0x97, 0xA6, 0xF7, 0x02, 0xBB, 0xAA, 0xA6, 0xBB, 0xF7, 0x2A, 0x51, 0xBE, 0x03,
            0xF4, 0x2A, 0x51, 0xBE, 0x03, 0xF4, 0x2A, 0x51, 0xBE, 0x12, 0x06, 0x56, 0x27, 0x32, 0x32, 0x36,
            0x32, 0xB2, 0x1A, 0x3B, 0xBC, 0x91, 0xD4, 0x7B, 0x58, 0xFC, 0x0B, 0x55, 0x2A, 0x15, 0xBC, 0x40,
            0x92, 0x0B, 0x5B, 0x7C, 0x0A, 0x95, 0x12, 0x35, 0xB8, 0x63, 0xD2, 0x0B, 0x3B, 0xF0, 0xC7, 0x14,
            0x51, 0x5C, 0x94, 0x86, 0x94, 0x59, 0x5C, 0xFC, 0x1B, 0x17, 0x3A, 0x3F, 0x6B, 0x37, 0x32, 0x32,
            0x30, 0x32, 0x72, 0x7A, 0x13, 0xB7, 0x26, 0x60, 0x7A, 0x13, 0xB7, 0x26, 0x50, 0xBA, 0x13, 0xB4,
            0x2A, 0x50, 0xBA, 0x13, 0xB5, 0x2E, 0x40, 0xFA, 0x13, 0x95, 0xAE, 0x40, 0x38, 0x18, 0x9A, 0x92,
            0xB0, 0x38, 0x00, 0xFA, 0x12, 0xB1, 0x7E, 0x00, 0xDB, 0x96, 0xA1, 0x7C, 0x08, 0xDB, 0x9A, 0x91,
            0xBC, 0x08, 0xD8, 0x1A, 0x86, 0xE2, 0x70, 0x39, 0x1F, 0x86, 0xE0, 0x78, 0x7E, 0x03, 0xE7, 0x64,
            0x51, 0x9C, 0x8F, 0x34, 0x6F, 0x4E, 0x41, 0xFC, 0x0B, 0xD5, 0xAE, 0x41, 0xFC, 0x0B, 0xD5, 0xAE,
            0x41, 0xFC, 0x3B, 0x70, 0x71, 0x64, 0x33, 0x32, 0x12, 0x32, 0x32, 0x36, 0x70, 0x34, 0x2B, 0x56,
            0x22, 0x70, 0x3A, 0x13, 0xB7, 0x26, 0x60, 0xBA, 0x1B, 0x94, 0xAA, 0x40, 0x38, 0x00, 0xFA, 0xB2,
            0xE2, 0xA2, 0x67, 0x32, 0x32, 0x12, 0x32, 0xB2, 0x32, 0x32, 0x32, 0x32, 0x75, 0xA3, 0x26, 0x7B,
            0x83, 0x26, 0xF9, 0x83, 0x2E, 0xFF, 0xE3, 0x16, 0x7D, 0xC0, 0x1E, 0x63, 0x21, 0x07, 0xE3, 0x01,
        ];
        var result = new byte[checked(length + frames.Length)];
        header.CopyTo(result);
        var mask = frames.Length & 0x7f;
        for (int i = 0; i < frames.Length; i++)
            result[length + i] = (byte)(frames[i] ^ mask ^ table[((frames.Length & 0x3f) + length + i) & 255]);
        return result;
    }

    private static AudioLoop? ValidLoop(long start, long end) => start >= 0 && end > start ? new(start, end) : null;

    private static AudioLoop? ReadVorbisLoop(ReadOnlySpan<byte> ogg)
    {
        // Vorbis 循环标签的单位是采样帧，SCD 头中的 Ogg 循环字段则是字节位置。
        using var packet = new MemoryStream();
        var packets = 0;
        while (ogg.Length >= 27 && ogg.StartsWith("OggS"u8) && packets < 2)
        {
            int count = ogg[26], offset = 27 + count;
            if (ogg.Length < offset) return null;
            foreach (var size in ogg.Slice(27, count))
            {
                if (size > ogg.Length - offset || packet.Length + size > 1024 * 1024) return null;
                packet.Write(ogg.Slice(offset, size));
                offset += size;
                if (size == 255) continue;
                if (++packets == 2) return ParseVorbisComments(packet.GetBuffer().AsSpan(0, (int)packet.Length));
                packet.SetLength(0);
            }
            ogg = ogg[offset..];
        }
        return null;
    }

    private static AudioLoop? ParseVorbisComments(ReadOnlySpan<byte> packet)
    {
        if (!packet.StartsWith("\x03vorbis"u8)) return null;
        packet = packet[7..];
        if (!ReadVorbisString(ref packet, out _) || packet.Length < 4) return null;
        var count = BinaryPrimitives.ReadUInt32LittleEndian(packet);
        packet = packet[4..];
        long? start = null, end = null, length = null;
        for (uint i = 0; i < count && ReadVorbisString(ref packet, out var entry); i++)
        {
            var comment = System.Text.Encoding.UTF8.GetString(entry);
            var eq = comment.IndexOf('=');
            if (eq < 0 || !long.TryParse(comment.AsSpan(eq + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var value)) continue;
            switch (comment[..eq].ToUpperInvariant())
            {
                case "LOOPSTART": start = value; break;
                case "LOOPEND": end = value; break;
                case "LOOPLENGTH": length = value; break;
            }
        }
        if (start is not long first) return null;
        if (end is null && length is long frames && frames <= long.MaxValue - first) end = first + frames;
        return end is long last ? ValidLoop(first, last) : null;
    }

    private static bool ReadVorbisString(ref ReadOnlySpan<byte> packet, out ReadOnlySpan<byte> value)
    {
        value = default;
        if (packet.Length < 4) return false;
        var length = BinaryPrimitives.ReadUInt32LittleEndian(packet);
        if (length > packet.Length - 4) return false;
        value = packet.Slice(4, (int)length);
        packet = packet[(4 + (int)length)..];
        return true;
    }

    private (SqPack Pack, long Offset) ResolveAudioAddress(string path, CancellationToken token)
    {
        var separator = path.IndexOf('@');
        var hashSeparator = path.IndexOf('#', Math.Max(separator, 0));
        if (separator <= 7 || hashSeparator <= separator + 1 || hashSeparator >= path.Length - 5 ||
            !long.TryParse(path.AsSpan(separator + 1, hashSeparator - separator - 1), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out var expectedOffset) || expectedOffset < 0 ||
            !ulong.TryParse(path.AsSpan(hashSeparator + 1, path.Length - hashSeparator - 5), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out var hash))
            throw new ArgumentException("无效的 SqPack 音频地址。", nameof(path));
        var container = path[7..separator];
        foreach (var repo in game.Repositories.Values)
        foreach (var categories in repo.Categories.Values)
        foreach (var category in categories)
        {
            token.ThrowIfCancellationRequested();
            if (!category.TryGetFileDatOffset(hash, out var dat, out var offset) || offset != expectedOffset ||
                category.GetDat(dat) is not { } pack) continue;
            var candidate = Path.GetRelativePath(SqPackPath, pack.FullName).Replace('\\', '/');
            if (!container.Equals(candidate, StringComparison.OrdinalIgnoreCase)) continue;
            var address = (pack, offset);
            audioAddresses[path] = address;
            return address;
        }
        throw new FileNotFoundException("当前 SqPack 索引中没有此音频地址：" + path);
    }

    private Dictionary<ulong, string> GetMusicNames(CancellationToken token)
    {
        if (musicNames is not null) return musicNames;
        var result = new Dictionary<ulong, string>();
        void Add(string value)
        {
            var path = value.Trim().Replace('\\', '/').ToLowerInvariant();
            if (path.EndsWith(".scd", StringComparison.Ordinal) && GameData.ParseFilePath(path) is { } parsed && game.FileExists(path))
                result.TryAdd(parsed.IndexHash, path);
        }
        Add(SampleAudioPath);
        try
        {
            foreach (var (_, path) in ListBgmPaths(token)) Add(path);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or KeyNotFoundException)
        {
            // 安装目录缺少完整 BGM 表时，仍可手动扫描 SCD。
        }
        musicNames = result;
        return result;
    }

    private List<SqPackAudioLocation> ScanAudioContainer(SqPack pack, string container,
        KeyValuePair<(byte Dat, long Offset), ulong>[] ordered, Dictionary<ulong, string> knownNames, CancellationToken token, Func<string, long, long, bool>? includeRange)
    {
        using var stream = new FileStream(pack.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
        using var reader = new BinaryReader(stream);
        var entries = new List<SqPackAudioLocation>();
        var addresses = new List<(string Path, long Offset)>();
        var verifiedNames = GetFileSystemMediaPaths(container, ".scd", token);
        for (var i = 0; i < ordered.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var offset = ordered[i].Key.Offset;
            var end = i + 1 < ordered.Length ? ordered[i + 1].Key.Offset : stream.Length;
            if (includeRange is not null && !includeRange(container, offset, end)) continue;
            if (!HasScdMagic(reader, offset, end)) continue;
            var hash = ordered[i].Value;
            var path = verifiedNames.GetValueOrDefault((offset, hash)) ?? knownNames.GetValueOrDefault(hash) ?? $"sqpack:{container}@{offset:x}#{hash:x16}.scd";
            addresses.Add((path, offset));
            entries.Add(new(path, container, offset, end));
        }
        foreach (var address in addresses) audioAddresses.TryAdd(address.Path, (pack, address.Offset));
        return entries;
    }

    private static bool HasScdMagic(BinaryReader reader, long offset, long end)
    {
        var stream = reader.BaseStream;
        if (offset < 0 || end > stream.Length || end - offset < 56) return false;
        stream.Position = offset;
        var headerSize = reader.ReadUInt32();
        if (reader.ReadUInt32() != (uint)FileType.Standard || reader.ReadUInt32() < 8 || headerSize < 32) return false;
        stream.Position = offset + 20;
        if (reader.ReadUInt32() == 0) return false;
        var blockOffset = offset + headerSize + reader.ReadUInt32();
        if (blockOffset > end - 16) return false;
        stream.Position = blockOffset;
        var blockHeaderSize = reader.ReadUInt32();
        reader.ReadUInt32();
        var compressedSize = reader.ReadUInt32();
        var decodedSize = reader.ReadUInt32();
        var payload = blockOffset + blockHeaderSize;
        if (blockHeaderSize < 16 || decodedSize < 8 || payload > end - 8) return false;
        stream.Position = payload;
        Span<byte> signature = stackalloc byte[8];
        if (compressedSize == 32000)
            stream.ReadExactly(signature);
        else
        {
            if (compressedSize == 0 || compressedSize > ushort.MaxValue || compressedSize > end - payload) return false;
            using var compressed = new MemoryStream(reader.ReadBytes((int)compressedSize), writable: false);
            using var deflate = new DeflateStream(compressed, CompressionMode.Decompress);
            try { deflate.ReadExactly(signature); }
            catch (Exception ex) when (ex is IOException or InvalidDataException) { return false; }
        }
        return signature.SequenceEqual("SEDBSSCF"u8);
    }

    private static byte[] WrapPcm(AudioBasicDesc description, byte[] data)
    {
        if (description.Channel is 0 or > 8 || description.Rate is 0 or > 384000 || data.Length % (2 * description.Channel) != 0)
            throw new InvalidDataException("无效的 PCM 格式。");
        var blockAlign = (ushort)(2 * description.Channel);
        Span<byte> format = stackalloc byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(format, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(format[2..], (ushort)description.Channel);
        BinaryPrimitives.WriteUInt32LittleEndian(format[4..], description.Rate);
        BinaryPrimitives.WriteUInt32LittleEndian(format[8..], description.Rate * blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(format[12..], blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(format[14..], 16);
        return WrapWave(format, data);
    }

    private static byte[] WrapAdpcm(AdpcmWaveFormat wave, byte[] data)
    {
        var format = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in wave, 1));
        if (wave.FormatTag != 2 || wave.Channels is < 1 or > 2 || wave.BlockAlign <= 0 || wave.SamplesPerBlock == 0 ||
            wave.Size < 4 || 18 + wave.Size > format.Length)
            throw new InvalidDataException("无效的 MS ADPCM 格式。");
        var remainder = data.Length % wave.BlockAlign;
        var samples = (long)(data.Length / wave.BlockAlign) * wave.SamplesPerBlock;
        if (remainder >= 7 * wave.Channels) samples += 2 + (remainder - 7 * wave.Channels) * 2 / wave.Channels;
        return WrapWave(format[..(18 + wave.Size)], data, checked((uint)samples));
    }

    private static byte[] WrapWave(ReadOnlySpan<byte> format, byte[] data, uint? samples = null)
    {
        var riffSize = 4 + 8 + format.Length + (format.Length & 1) + (samples is null ? 0 : 12) + 8 + data.Length + (data.Length & 1);
        using var stream = new MemoryStream(riffSize + 8);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(riffSize);
        writer.Write("WAVEfmt "u8);
        writer.Write(format.Length);
        writer.Write(format);
        if ((format.Length & 1) != 0) writer.Write((byte)0);
        if (samples is uint sampleCount)
        {
            writer.Write("fact"u8);
            writer.Write(4);
            writer.Write(sampleCount);
        }
        writer.Write("data"u8);
        writer.Write(data.Length);
        writer.Write(data);
        if ((data.Length & 1) != 0) writer.Write((byte)0);
        return stream.ToArray();
    }
}
