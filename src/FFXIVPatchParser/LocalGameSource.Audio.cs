using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Lumina;
using Lumina.Data;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Scd;
using Lumina.Data.Structs;
using Lumina.Excel;
using Lumina.Text.ReadOnly;

namespace FFXIVPatchParser;

public sealed record SqPackAudioLocation(string Path, string Container, long Offset, long End);
public sealed record GameAudio(string Path, int TrackIndex, int TrackCount, string Codec, uint Channels, uint SampleRate,
    string Extension, byte[] Data);

public sealed partial class LocalGameSource
{
    private const AudioFormat Pcm16LittleEndian = (AudioFormat)1;
    private const string SampleAudioPath = "music/ex2/bgm_ex2_system_title.scd";
    private Dictionary<ulong, string>? musicNames;
    private readonly Dictionary<string, IReadOnlyList<SqPackAudioLocation>> audioLocations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (SqPack Pack, long Offset)> audioAddresses = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> ListNamedAudioPaths(CancellationToken token = default)
    {
        lock (gate)
            return GetMusicNames(token).Values.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public IReadOnlyList<SqPackAudioLocation> ListAudioLocations(CancellationToken token = default, IReadOnlySet<string>? containers = null)
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
                                var ordered = group.OrderBy(entry => entry.Key.Offset).ToArray();
                                entries = ScanAudioContainer(pack, container, ordered, GetMusicNames(token), token);
                                audioLocations.Add(container, entries);
                            }
                            result.AddRange(entries);
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
                    var separator = path.IndexOf('@');
                    if (!audioAddresses.TryGetValue(path, out var address) && separator > 7)
                    {
                        ListAudioLocations(token, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { path[7..separator] });
                        audioAddresses.TryGetValue(path, out address);
                    }
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
                        return new(path, index, count, "Ogg Vorbis", description.Channel, description.Rate, ".ogg", audio.AudioData);
                    case AudioFormat.Mp3:
                        return new(path, index, count, "MP3", description.Channel, description.Rate, ".mp3", audio.AudioData);
                    case AudioFormat.MsAdpcm when audio.AudioDataHeader is AdpcmWaveFormat wave:
                        return new(path, index, count, "MS ADPCM", description.Channel, description.Rate, ".wav", WrapAdpcm(wave, audio.AudioData));
                    case Pcm16LittleEndian:
                        return new(path, index, count, "PCM 16-bit", description.Channel, description.Rate, ".wav", WrapPcm(description, audio.AudioData));
                }
                if (trackIndex is not null) throw new NotSupportedException($"无法预览此音轨的编码（{description.Format}）。");
            }
            throw new NotSupportedException("此 SCD 没有可预览的音轨。");
        }
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
        if (SheetNames.Contains("BGM"))
        {
            try
            {
                var (header, language) = ReadSheetHeader("BGM");
                foreach (var row in game.Excel.GetSheet<RawRow>(language, "BGM"))
                {
                    token.ThrowIfCancellationRequested();
                    for (var column = 0; column < header.ColumnDefinitions.Length; column++)
                        if (row.ReadColumn(column) is ReadOnlySeString text) Add(text.ExtractText());
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or KeyNotFoundException)
            {
                // The SCD scan also works when the game installation has no complete BGM sheet.
            }
        }
        musicNames = result;
        return result;
    }

    private List<SqPackAudioLocation> ScanAudioContainer(SqPack pack, string container,
        KeyValuePair<(byte Dat, long Offset), ulong>[] ordered, Dictionary<ulong, string> knownNames, CancellationToken token)
    {
        using var stream = new FileStream(pack.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
        using var reader = new BinaryReader(stream);
        var entries = new List<SqPackAudioLocation>();
        var addresses = new List<(string Path, long Offset)>();
        for (var i = 0; i < ordered.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var offset = ordered[i].Key.Offset;
            var end = i + 1 < ordered.Length ? ordered[i + 1].Key.Offset : stream.Length;
            if (!HasScdMagic(reader, offset, end)) continue;
            var hash = ordered[i].Value;
            var path = knownNames.GetValueOrDefault(hash) ?? $"sqpack:{container}@{offset:x}#{hash:x16}.scd";
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
