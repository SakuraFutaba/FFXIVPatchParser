using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace FFXIVPatchParser;

public sealed record AvfxPreview(IReadOnlyList<AvfxTrack> Tracks, int FrameCount, int SkippedTracks);
public sealed record AvfxTrack(GameTexture Texture, int Start, int Stop, int Interval, int Lifetime,
    AvfxVectorCurve EmitterPosition, AvfxVectorCurve Position, AvfxVectorCurve Scale,
    AvfxCurve Angle, AvfxCurve Alpha, AvfxCurve Color, int DrawMode)
{
    public GameModelMesh? Model { get; init; }
    public Matrix4x4 Anchor { get; init; } = Matrix4x4.Identity;
    public AvfxVectorCurve EmitterScale { get; init; }
    public AvfxVectorCurve Rotation { get; init; }
    public bool SingleBirth { get; init; }
    public bool ColorToAlpha { get; init; }
}

public readonly record struct AvfxVectorCurve(AvfxCurve X, AvfxCurve Y, AvfxCurve Z)
{
    public Vector3 Sample(double frame) => new(X.Sample(frame), Y.Sample(frame), Z.Sample(frame));
}

public sealed class AvfxCurve(float fallback, IReadOnlyList<(short Frame, short Type, Vector3 Value)> keys)
{
    public float Sample(double frame)
        => SampleVector(frame).Z;

    public Vector3 SampleVector(double frame)
    {
        if (keys.Count == 0) return new(fallback);
        for (var i = 1; i < keys.Count; i++)
        {
            var right = keys[i];
            if (frame >= right.Frame) continue;
            var left = keys[i - 1];
            if (frame <= left.Frame || left.Type == 2) return left.Value;
            return Vector3.Lerp(left.Value, right.Value, (float)((frame - left.Frame) / (right.Frame - left.Frame)));
        }
        return keys[^1].Value;
    }
}

public sealed partial class LocalGameSource
{
    private const int MaxAvfxFrames = 1800;
    private readonly record struct AvfxChunk(string Tag, ReadOnlyMemory<byte> Data);

    public AvfxPreview ReadTmbAvfxPreview(string path, CancellationToken token = default)
    {
        var cues = ReadTmbAvfxCues(path, token);
        var effects = new Dictionary<string, AvfxPreview>(StringComparer.Ordinal);
        var tracks = new List<AvfxTrack>();
        var skipped = 0;
        foreach (var cue in cues)
        {
            token.ThrowIfCancellationRequested();
            if (cue.Frame is < 0 or >= MaxAvfxFrames || tracks.Count >= 256) { skipped++; continue; }
            if (!effects.TryGetValue(cue.Path, out var effect))
            {
                if (effects.Count >= 16) { skipped++; continue; }
                try { effect = ReadAvfxPreview(cue.Path, token); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or NotSupportedException)
                {
                    effect = new([], 1, 1);
                }
                effects.Add(cue.Path, effect);
            }
            skipped += effect.SkippedTracks;
            foreach (var track in effect.Tracks.Take(256 - tracks.Count))
            {
                var start = cue.Frame + track.Start;
                if (start >= MaxAvfxFrames) break;
                tracks.Add(track with { Start = start, Stop = Math.Min(cue.Frame + track.Stop, MaxAvfxFrames) });
            }
        }
        var duration = tracks.Count == 0 ? 1 : Math.Clamp(tracks.Max(track => track.Stop + track.Lifetime), 1, MaxAvfxFrames);
        return new(tracks, duration, skipped);
    }

    public AvfxPreview ReadAvfxPreview(string path, CancellationToken token = default)
        => ReadAvfxPreview(path, null, token);

    private AvfxPreview ReadAvfxPreview(string path, IReadOnlyDictionary<int, Matrix4x4>? anchors, CancellationToken token)
    {
        if (NormalizeFileSystemPath(path) is not { } name || !name.EndsWith(".avfx", StringComparison.Ordinal))
            throw new InvalidDataException("请输入 SqPack 中的 AVFX 路径。");
        byte[] bytes;
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            bytes = game.GetFile(name)?.Data ?? throw new FileNotFoundException("找不到特效文件。", name);
        }
        var roots = ReadAvfxChunks(bytes);
        if (roots.Length != 1 || roots[0].Tag != "AVFX") throw new InvalidDataException("AVFX 文件头无效。");
        var root = ReadAvfxChunks(roots[0].Data);
        var timelines = root.Where(chunk => chunk.Tag == "TmLn").Select(chunk => ReadAvfxChunks(chunk.Data)).ToArray();
        var emitters = root.Where(chunk => chunk.Tag == "Emit").Select(chunk => ReadAvfxChunks(chunk.Data)).ToArray();
        var particles = root.Where(chunk => chunk.Tag == "Ptcl").Select(chunk => ReadAvfxChunks(chunk.Data)).ToArray();
        var models = anchors == null ? [] : root.Where(chunk => chunk.Tag == "Modl").Select(chunk => ReadEmbeddedAvfxModel(chunk.Data)).ToArray();
        var binders = anchors == null ? [] : root.Where(chunk => chunk.Tag == "Bind").Select(chunk => ReadAvfxChunks(chunk.Data)).ToArray();
        var textures = root.Where(chunk => chunk.Tag == "Tex")
            .Select(chunk => NormalizeFileSystemPath(Encoding.UTF8.GetString(chunk.Data.Span).TrimEnd('\0'))).ToArray();
        var images = new Dictionary<int, GameTexture?>();
        var bakedModels = new Dictionary<(int Model, int Texture), (GameModelMesh Mesh, GameTexture Texture)>();
        var tracks = new List<AvfxTrack>();
        var skipped = 0;
        foreach (var scheduler in root.Where(chunk => chunk.Tag == "Schd"))
        foreach (var schedule in AvfxItems(ReadAvfxChunks(scheduler.Data), "Item", "bEna"))
        {
            token.ThrowIfCancellationRequested();
            if (AvfxInt(schedule, "bEna", 0) == 0) continue;
            var timelineIndex = AvfxInt(schedule, "TlNo", -1);
            if ((uint)timelineIndex >= timelines.Length) { skipped++; continue; }
            var timelineStart = Math.Clamp(AvfxInt(schedule, "StTm", 0), 0, MaxAvfxFrames);
            foreach (var item in AvfxItems(timelines[timelineIndex], "Item", "bEna"))
            {
                if (AvfxInt(item, "bEna", 0) == 0) continue;
                var emitterIndex = AvfxInt(item, "EmNo", -1);
                if ((uint)emitterIndex >= emitters.Length) { skipped++; continue; }
                var start = timelineStart + Math.Clamp(AvfxInt(item, "StTm", 0), 0, MaxAvfxFrames);
                var relativeEnd = AvfxInt(item, "EdTm", MaxAvfxFrames);
                var end = timelineStart + Math.Clamp(relativeEnd == -1 ? MaxAvfxFrames : relativeEnd, 0, MaxAvfxFrames);
                var anchor = Matrix4x4.Identity;
                var binderIndex = AvfxInt(item, "BdNo", AvfxInt(timelines[timelineIndex], "BnNo", -1));
                if (anchors != null && binderIndex >= 0 &&
                    !TryAvfxItemAnchor(binders, binderIndex, anchors, out anchor)) { skipped++; continue; }
                AddEmitter(emitters[emitterIndex], start, end, anchor);
            }
        }
        var frameCount = tracks.Count == 0 ? 1 : Math.Clamp(tracks.Max(track => track.Stop + track.Lifetime), 1, MaxAvfxFrames);
        return new(tracks, frameCount, skipped);

        void AddEmitter(AvfxChunk[] emitter, int start, int end, Matrix4x4 anchor)
        {
            if (AvfxInt(emitter, "EVT", -1) != 0) { skipped++; return; }
            var lifetime = AvfxLife(emitter, 60, anchors != null);
            var interval = (int)Math.Clamp(AvfxReadCurve(emitter, "CrI", 1).Sample(0), 1, MaxAvfxFrames);
            var emitterPosition = AvfxVector(emitter, "Pos", 0);
            var emitterScale = AvfxVector(emitter, "Scl", 1);
            skipped += AvfxItems(emitter, "ItEm", "bEnb").Count(item => AvfxInt(item, "bEnb", 0) != 0);
            foreach (var item in AvfxItems(emitter, "ItPr", "bEnb").Take(256))
            {
                token.ThrowIfCancellationRequested();
                if (AvfxInt(item, "bEnb", 0) == 0 || AvfxInt(item, "CrPr", 100) <= 0) continue;
                var particleIndex = AvfxInt(item, "TgtB", -1);
                if ((uint)particleIndex >= particles.Length || tracks.Count >= 256) { skipped++; continue; }
                var particle = particles[particleIndex];
                var type = AvfxInt(particle, "PrVT", -1);
                var modelIndex = -1;
                AvfxEmbeddedModel? model = null;
                if (type == 5 && anchors != null)
                {
                    modelIndex = AvfxInt(ReadAvfxChunks(AvfxData(particle, "Data")), "MdNo", -1);
                    if ((uint)modelIndex >= models.Length || (model = models[modelIndex]) == null) { skipped++; continue; }
                }
                else if (type is not (1 or 8)) { skipped++; continue; }
                var binding = ReadAvfxChunks(AvfxData(particle, "TC1"));
                var textureIndex = AvfxInt(binding, "TxNo", AvfxInt(binding, "TLst", -1));
                var textureEnabled = AvfxInt(binding, "bEna", 0) != 0;
                GameTexture? image = !textureEnabled && model != null ? SolidAvfxTexture : null;
                if (textureEnabled && (uint)textureIndex < textures.Length && textures[textureIndex] is { } texturePath &&
                    !images.TryGetValue(textureIndex, out image))
                {
                    try { image = ReadTerritoryTexture(texturePath, 256, token); }
                    catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException) { image = null; }
                    images[textureIndex] = image;
                }
                if (image == null) { skipped++; continue; }
                var mesh = model?.Mesh;
                if (model is { } embedded && embedded.Alpha.Any(alpha => alpha < 255) && embedded.Mesh.TriangleIndices.Length <= 12288)
                {
                    var key = (modelIndex, textureEnabled ? textureIndex : -1);
                    if (!bakedModels.TryGetValue(key, out var baked))
                    {
                        baked = BakeAvfxVertexAlpha(embedded, image, token);
                        bakedModels.Add(key, baked);
                    }
                    (mesh, image) = baked;
                }
                var first = start + Math.Clamp(AvfxInt(item, "GenD", 0), 0, MaxAvfxFrames);
                var stop = Math.Min(Math.Min(end, start + lifetime), MaxAvfxFrames);
                if (first >= stop) continue;
                var life = AvfxInt(item, "bOvr", 0) != 0 ? Math.Clamp(AvfxInt(item, "OvrV", 60), 1, MaxAvfxFrames) : AvfxLife(particle, 60, anchors != null);
                var color = ReadAvfxChunks(AvfxData(particle, "Col"));
                tracks.Add(new(image, first, stop, interval, life, emitterPosition, AvfxVector(particle, "Pos", 0),
                    AvfxVector(particle, "Scl", 1), AvfxReadCurve(ReadAvfxChunks(AvfxData(particle, "Rot")), "Z", 0),
                    AvfxReadCurve(color, "A", 1), AvfxReadCurve(color, "RGB", 1), AvfxInt(particle, "RMT", 0))
                {
                    Model = mesh, Anchor = anchor, EmitterScale = emitterScale, Rotation = AvfxVector(particle, "Rot", 0),
                    SingleBirth = life == MaxAvfxFrames || AvfxInt(item, "CrTm", 1) == 1,
                    ColorToAlpha = AvfxInt(binding, "bC2A", 0) != 0
                });
            }
        }
    }

    private static AvfxChunk[] ReadAvfxChunks(ReadOnlyMemory<byte> data)
    {
        var chunks = new List<AvfxChunk>();
        for (var offset = 0; offset < data.Length;)
        {
            if (data.Length - offset < 8 || chunks.Count == 32768) throw new InvalidDataException("AVFX 区块头无效。");
            var header = data.Span.Slice(offset, 8);
            var size = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
            var aligned = (size + 3L) & ~3L;
            offset += 8;
            if (size < 0 || aligned > data.Length - offset) throw new InvalidDataException("AVFX 区块范围无效。");
            var tag = new string([(char)header[3], (char)header[2], (char)header[1], (char)header[0]]).Trim('\0');
            chunks.Add(new(tag, data.Slice(offset, size)));
            offset += (int)aligned;
        }
        return chunks.ToArray();
    }

    private static IEnumerable<AvfxChunk[]> AvfxItems(AvfxChunk[] parent, string tag, string startTag)
    {
        var last = parent.LastOrDefault(chunk => chunk.Tag == tag);
        var fields = ReadAvfxChunks(last.Data);
        var start = Array.FindIndex(fields, field => field.Tag == startTag);
        while (start >= 0)
        {
            var next = start + 1;
            while (next < fields.Length && fields[next].Tag != startTag) next++;
            yield return fields[start..next];
            start = next < fields.Length ? next : -1;
        }
    }

    private static ReadOnlyMemory<byte> AvfxData(AvfxChunk[] chunks, string tag) =>
        chunks.FirstOrDefault(chunk => chunk.Tag == tag).Data;

    private static int AvfxInt(AvfxChunk[] chunks, string tag, int fallback) => AvfxData(chunks, tag).Span switch
    {
        { Length: >= 4 } bytes => BinaryPrimitives.ReadInt32LittleEndian(bytes),
        { Length: 1 } bytes => bytes[0],
        _ => fallback
    };

    private static int AvfxLife(AvfxChunk[] fields, int fallback, bool infinite = false)
    {
        var life = BitConverter.Int32BitsToSingle(AvfxInt(ReadAvfxChunks(AvfxData(fields, "Life")), "Val", 0));
        if (infinite && life == -1) return MaxAvfxFrames;
        return float.IsFinite(life) && life > 0 ? (int)Math.Clamp(life, 1, MaxAvfxFrames) : fallback;
    }

    private static AvfxVectorCurve AvfxVector(AvfxChunk[] parent, string name, float fallback)
    {
        var data = ReadAvfxChunks(AvfxData(parent, name));
        return new(AvfxReadCurve(data, "X", fallback), AvfxReadCurve(data, "Y", fallback), AvfxReadCurve(data, "Z", fallback));
    }

    private static AvfxCurve AvfxReadCurve(AvfxChunk[] parent, string tag, float fallback)
    {
        var fields = ReadAvfxChunks(AvfxData(parent, tag));
        var data = AvfxData(fields, "Keys").Span;
        var keys = new List<(short Frame, short Type, Vector3 Value)>();
        for (var offset = 0; offset + 16 <= data.Length; offset += 16)
        {
            var key = data[offset..];
            var value = new Vector3(BinaryPrimitives.ReadSingleLittleEndian(key[4..]),
                BinaryPrimitives.ReadSingleLittleEndian(key[8..]), BinaryPrimitives.ReadSingleLittleEndian(key[12..]));
            if (float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z))
                keys.Add((BinaryPrimitives.ReadInt16LittleEndian(key), BinaryPrimitives.ReadInt16LittleEndian(key[2..]), value));
        }
        return new(fallback, keys.OrderBy(key => key.Frame).ToArray());
    }
}
