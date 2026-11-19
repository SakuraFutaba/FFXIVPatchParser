using System.Buffers.Binary;
using System.Text.RegularExpressions;

namespace FFXIVPatchParser;

public sealed partial class LocalGameSource
{
    private string? ResolvePreviewFaceSkeleton(string modelPath, string code)
    {
        var face = modelPath == "character:" + code && CharacterSamples.TryGetValue(code, out var sample)
            ? sample.Face
            : Regex.Match(modelPath, @"/obj/face/(f\d{4})/model/").Groups[1].Value;
        if (face.Length != 5 || !ushort.TryParse(face.AsSpan(1), out var faceId) ||
            !ushort.TryParse(code.AsSpan(1), out var raceId)) return null;
        var table = game.GetFile("chara/xls/charadb/faceSkeletonTemplate.est")?.Data;
        if (table == null || table.Length < 4) return null;
        var count = BinaryPrimitives.ReadUInt32LittleEndian(table);
        if (4 + 6L * count != table.Length) return null;
        for (var i = 0; i < count; i++)
        {
            if (BinaryPrimitives.ReadUInt16LittleEndian(table.AsSpan(4 + i * 4)) != faceId ||
                BinaryPrimitives.ReadUInt16LittleEndian(table.AsSpan(6 + i * 4)) != raceId) continue;
            var skeleton = BinaryPrimitives.ReadUInt16LittleEndian(table.AsSpan(4 + (int)count * 4 + i * 2));
            return $"f{skeleton:D4}";
        }
        return null;
    }

    private sealed record FaceTrack(string Path, int Slot, int Start, int Duration);

    private FaceTrack? FindEmoteFaceTrack(string modelPath, string papPath, int slot, CancellationToken token, string? faceModelPath)
    {
        if (!modelPath.StartsWith("character:", StringComparison.Ordinal) ||
            !papPath.Contains("/animation/a0001/", StringComparison.Ordinal) ||
            !papPath.Contains("/emote/", StringComparison.Ordinal)) return null;
        lock (gate)
        {
            var code = modelPath[10..];
            var skeleton = ResolvePreviewFaceSkeleton(faceModelPath ?? modelPath, code);
            if (skeleton == null || game.GetFile(papPath)?.Data is not { } pap) return null;
            var metadata = ParseAnimationMetadata(pap, papPath, token);
            // 每个槽的内嵌时间线独立。只接受此处已经核验的单槽、单表情轨。
            if (metadata.Slots.Count != 1 || metadata.Slots[0].Index != slot) return null;
            var data = pap.AsSpan(BinaryPrimitives.ReadInt32LittleEndian(pap.AsSpan(22)));
            if (data.Length < 12 || !data[..4].SequenceEqual("TMLB"u8)) return null;
            var length = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
            if (length < 12 || length > data.Length) return null;
            data = data[..length];
            var entries = BinaryPrimitives.ReadInt32LittleEndian(data[8..]);
            string? facialSlot = null;
            var start = 0;
            var duration = 0;
            var cursor = 12;
            var bodyMatched = false;
            for (var i = 0; i < entries; i++)
            {
                if (cursor > data.Length - 8) return null;
                var entry = data[cursor..];
                var size = BinaryPrimitives.ReadInt32LittleEndian(entry[4..]);
                if (size < 8 || size > entry.Length) return null;
                var body = entry[..4].SequenceEqual("C009"u8) && size == 24;
                var face = entry[..4].SequenceEqual("C010"u8) && size == 40;
                if (body || face)
                {
                    var offset = cursor + 8L + BinaryPrimitives.ReadInt32LittleEndian(entry[(body ? 20 : 32)..]);
                    if (offset < 0 || offset >= data.Length) return null;
                    var bytes = data[(int)offset..];
                    var end = bytes.IndexOf((byte)0);
                    if (end is < 1 or > 32) return null;
                    var name = System.Text.Encoding.ASCII.GetString(bytes[..end]);
                    if (body) bodyMatched |= name == metadata.Slots[0].Name;
                    if (face && name.StartsWith("cfxf_", StringComparison.Ordinal))
                    {
                        if (facialSlot != null || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')) return null;
                        facialSlot = name;
                        start = BinaryPrimitives.ReadUInt16LittleEndian(entry[10..]);
                        duration = BinaryPrimitives.ReadUInt16LittleEndian(entry[12..]);
                    }
                }
                cursor += size;
            }
            if (!bodyMatched || facialSlot == null || duration == 0) return null;
            var root = $"chara/human/{code}/animation/{skeleton}/";
            foreach (var path in new[] {root + "resident/face.pap", root + "nonresident/" + facialSlot[5..] + ".pap"})
            {
                if (game.GetFile(path)?.Data is not { } resource) continue;
                var match = ParseAnimationMetadata(resource, path, token).Slots.SingleOrDefault(x => x.Name == facialSlot);
                if (match != null) return new(path, match.Index, start, duration);
            }
            return null;
        }
    }

    private static ModelAnimationClip CombineFaceTrack(ModelAnimationClip body, ModelAnimationClip face, FaceTrack track,
        CancellationToken token)
    {
        var head = Array.IndexOf(body.BoneNames, "j_kao");
        if (head < 0) return body;
        var names = body.BoneNames.Concat(face.BoneNames.Select(name => "face/" + name)).ToArray();
        if ((long)names.Length * body.Frames.Length > 1_000_000)
            return body with { FaceLayerStatus = "仅身体动作（合成超过内存上限）" };
        var frames = new System.Numerics.Matrix4x4[body.Frames.Length][];
        for (var i = 0; i < frames.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var tick = i * 30f / body.FramesPerSecond;
            var active = tick >= track.Start && tick < track.Start + track.Duration;
            var index = Math.Clamp((int)((tick - track.Start) * face.FramesPerSecond / 30f), 0, face.Frames.Length - 1);
            var frame = frames[i] = new System.Numerics.Matrix4x4[names.Length];
            body.Frames[i].CopyTo(frame, 0);
            for (var b = 0; b < face.BoneNames.Length; b++)
                frame[body.BoneNames.Length + b] = active ? face.Frames[index][b] * frame[head] : frame[head];
        }
        return body with { BoneNames = names, Frames = frames, FaceLayerStatus = "含表情轨" };
    }
}
