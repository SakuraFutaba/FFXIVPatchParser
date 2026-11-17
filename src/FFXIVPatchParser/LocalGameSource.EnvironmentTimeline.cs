using System.Numerics;
using Lumina.Data;

namespace FFXIVPatchParser;

public sealed record GameEnvironmentKeyframe(float Time, Vector3 Color, float Intensity);
public sealed record GameEnvironmentWeather(uint Id, IReadOnlyList<GameEnvironmentKeyframe> Sunlight,
    IReadOnlyList<GameEnvironmentKeyframe> Fog, IReadOnlyList<GameEnvironmentKeyframe> Cloud,
    uint? CandidateSkyTextureId = null);
public sealed record GameEnvironmentTimeline(string Path, IReadOnlyList<GameEnvironmentWeather> Weathers);

public sealed partial class LocalGameSource
{
    /// <summary>读取 ENVS v6 的日光、雾与云颜色轨道。颜色保留为线性运算前的原始 RGB/255 与独立强度。</summary>
    public GameEnvironmentTimeline ReadEnvironmentTimeline(string path, CancellationToken token = default)
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            var bytes = game.GetFile<FileResource>(path)?.Data ?? throw new FileNotFoundException("缺少环境文件：" + path);
            void Check(int p, int size)
            {
                if (p < 0 || size < 0 || p > bytes.Length - size) throw new InvalidDataException("ENVB 轨道越界。");
            }
            int I(int p) { Check(p, 4); return BitConverter.ToInt32(bytes, p); }
            float F(int p)
            {
                Check(p, 4); var v = BitConverter.ToSingle(bytes, p);
                if (!float.IsFinite(v)) throw new InvalidDataException("ENVB 参数不是有限值。");
                return v;
            }
            int Relative(int p) => checked(p + I(p));
            Check(0, 40);
            if (!bytes.AsSpan(0, 4).SequenceEqual("ENVB"u8) || I(4) != bytes.Length || I(8) != 1
                || !bytes.AsSpan(12, 4).SequenceEqual("ENVS"u8) || I(20) != 6)
                throw new NotSupportedException("仅支持已验证的单段 ENVS v6 环境文件。");
            var count = I(28);
            if (count is < 0 or > 256) throw new InvalidDataException("ENVB 天气数量无效。");
            var sections = checked(20 + I(24)); Check(sections, count * 16);
            var result = new List<GameEnvironmentWeather>();
            for (var i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                var section = sections + i * 16;
                var trackCount = I(section + 4);
                if (trackCount is < 0 or > 128) throw new InvalidDataException("ENVB 轨道数量无效。");
                var tracks = Relative(section); Check(tracks, trackCount * 12);
                var channels = new Dictionary<int, IReadOnlyList<GameEnvironmentKeyframe>>();
                for (var j = 0; j < trackCount; j++)
                {
                    var track = tracks + j * 12; var kind = I(track + 8);
                    if (kind is not (0 or 2 or 13)) continue;
                    var keys = I(track + 4);
                    if (keys is < 0 or > 4096) throw new InvalidDataException("ENVB 关键帧数量无效。");
                    var offsets = Relative(track); Check(offsets, keys * 4);
                    var frames = new List<GameEnvironmentKeyframe>();
                    for (var k = 0; k < keys; k++)
                    {
                        var frame = checked(offsets + I(offsets + k * 4));
                        var time = F(frame);
                        if (time is < 0 or > 86400) throw new InvalidDataException("ENVB 昼夜时间超出一天。");
                        var color = checked(frame + I(frame + (kind == 2 ? 20 : 4)));
                        Check(color, 8);
                        var intensity = F(color + 4);
                        if (intensity < 0) throw new InvalidDataException("ENVB 颜色强度无效。");
                        frames.Add(new(time, new Vector3(bytes[color], bytes[color + 1], bytes[color + 2]) / 255, intensity));
                    }
                    channels[kind] = frames.OrderBy(f => f.Time).ToArray();
                }
                // ENVS v6 footer+4 的布局已实测；它对应 sky_NNN 的含义仍是待客户端核对的推断。
                var footer = checked(section + I(section + 12));
                uint? candidateSky = null;
                if (footer >= 0 && footer <= bytes.Length - 20)
                {
                    var rawSky = unchecked((uint)I(footer + 4));
                    if (rawSky is > 0 and <= 999) candidateSky = rawSky;
                }
                result.Add(new(unchecked((uint)I(section + 8)), channels.GetValueOrDefault(0) ?? [],
                    channels.GetValueOrDefault(13) ?? [], channels.GetValueOrDefault(2) ?? [], candidateSky));
            }
            return new(path, result);
        }
    }

    public static Vector3 SampleEnvironmentColor(IReadOnlyList<GameEnvironmentKeyframe> frames, float seconds)
    {
        if (frames.Count == 0 || !float.IsFinite(seconds)) return Vector3.Zero;
        seconds = (seconds % 86400 + 86400) % 86400;
        var next = 0;
        while (next < frames.Count && frames[next].Time <= seconds) next++;
        var a = frames[(next + frames.Count - 1) % frames.Count];
        var b = frames[next % frames.Count];
        var start = a.Time; var end = b.Time;
        if (next == 0) start -= 86400;
        if (next == frames.Count) end += 86400;
        var blend = end > start ? Math.Clamp((seconds - start) / (end - start), 0, 1) : 0;
        return Vector3.Lerp(a.Color * a.Intensity, b.Color * b.Intensity, blend);
    }
}
