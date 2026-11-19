using System.Text;
using Lumina.Data;

namespace FFXIVPatchParser;

public sealed record GameTmbAvfxCue(int Frame, string Path);

public sealed partial class LocalGameSource
{
    /// <summary>读取动作时间线中无条件轨道的 C012 特效条目，帧号为条目在轨道上的触发帧。</summary>
    public IReadOnlyList<GameTmbAvfxCue> ReadTmbAvfxCues(string path, CancellationToken token = default)
    {
        var normalized = NormalizeFileSystemPath(path);
        if (normalized == null || !normalized.EndsWith(".tmb", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请选择 SqPack 中的 TMB 文件。", nameof(path));
        byte[] bytes;
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            bytes = game.GetFile<FileResource>(normalized)?.Data ?? throw new FileNotFoundException("缺少 TMB：" + normalized);
        }
        var cues = ParseTmbAvfxCues(bytes, token);
        lock (gate)
        {
            var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var asset in cues.Select(c => c.Path).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                if (game.FileExists(asset)) available.Add(asset);
            }
            return cues.Where(c => available.Contains(c.Path)).OrderBy(c => c.Frame).ToArray();
        }
    }

    private static IReadOnlyList<GameTmbAvfxCue> ParseTmbAvfxCues(byte[] data, CancellationToken token)
    {
        int I(int offset) => BitConverter.ToInt32(data, offset);
        short S(int offset) => BitConverter.ToInt16(data, offset);
        if (data.Length < 12 || !data.AsSpan(0, 4).SequenceEqual("TMLB"u8) || I(4) != data.Length)
            throw new InvalidDataException("TMB 文件头或文件长度无效。");
        var count = I(8);
        if (count < 0 || count > (data.Length - 12) / 8) throw new InvalidDataException("TMB 块数量越界。");

        var actors = new Dictionary<short, int>();
        var tracks = new Dictionary<short, int>();
        var effects = new Dictionary<short, int>();
        var lists = new List<int>();
        int cursor = 12;
        for (var i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            if (cursor > data.Length - 8) throw new InvalidDataException("TMB 块头越界。");
            int size = I(cursor + 4);
            if (size < 8 || size > data.Length - cursor) throw new InvalidDataException("TMB 块长度越界。");
            var tag = data.AsSpan(cursor, 4);
            if (tag.SequenceEqual("TMAL"u8) && size >= 16) lists.Add(cursor);
            else if (tag.SequenceEqual("TMAC"u8) && size >= 28) actors.Add(S(cursor + 8), cursor);
            else if (tag.SequenceEqual("TMTR"u8) && size >= 24) tracks.Add(S(cursor + 8), cursor);
            else if (tag.SequenceEqual("C012"u8) && size >= 24) effects.Add(S(cursor + 8), cursor);
            cursor += size;
        }

        IEnumerable<short> Children(int block, int field)
        {
            int number = I(block + field + 4);
            if (number == 0) yield break;
            long absolute = (long)block + 8 + I(block + field);
            if (number < 0 || absolute < cursor || absolute + number * 2L > data.Length)
                throw new InvalidDataException("TMB 子条目索引越界。");
            for (int n = 0; n < number; n++)
            {
                token.ThrowIfCancellationRequested();
                yield return S((int)absolute + n * 2);
            }
        }

        var result = new List<GameTmbAvfxCue>();
        foreach (var actorId in lists.SelectMany(list => Children(list, 8)).Distinct())
        {
            if (!actors.TryGetValue(actorId, out var actor)) continue;
            foreach (var trackId in Children(actor, 20).Distinct())
            {
                if (!tracks.TryGetValue(trackId, out var track) || I(track + 20) != 0 || S(track + 10) != 0) continue;
                foreach (var entryId in Children(track, 12))
                {
                    if (!effects.TryGetValue(entryId, out var entry) || S(entry + 10) < 0) continue;
                    long address = (long)entry + 8 + I(entry + 20);
                    if (address < cursor || address >= data.Length) continue;
                    var start = (int)address;
                    var end = Array.IndexOf(data, (byte)0, start, Math.Min(1024, data.Length - start));
                    if (end <= start) continue;
                    var asset = NormalizeFileSystemPath(Encoding.UTF8.GetString(data, start, end - start));
                    if (asset != null && asset.EndsWith(".avfx", StringComparison.OrdinalIgnoreCase))
                        result.Add(new(S(entry + 10), asset));
                }
            }
        }
        return result;
    }
}
