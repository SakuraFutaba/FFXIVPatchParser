using System.Buffers.Binary;
using System.Numerics;
using Lumina.Data.Parsing;
using Lumina.Data.Structs;

namespace FFXIVPatchParser;

public sealed record GameItemEffect(string Path, AvfxPreview Preview);

public sealed partial class LocalGameSource
{
    private static readonly GameTexture SolidAvfxTexture = new("", 1, 1, [255, 255, 255, 255]);
    private sealed record AvfxEmbeddedModel(GameModelMesh Mesh, byte[] Alpha);

    public GameItemEffect? ReadItemEffect(GameCharacterPartOption option, CancellationToken token = default)
    {
        if (option.ImcSubset is not { } subset) return null;
        var weapon = option.Path.StartsWith("chara/weapon/", StringComparison.Ordinal);
        var accessory = option.Path.StartsWith("chara/accessory/", StringComparison.Ordinal);
        if (!weapon && !accessory && !option.Path.StartsWith("chara/equipment/", StringComparison.Ordinal)) return null;
        var variant = ReadModelImcChange(option.Path, subset, !weapon, token);
        if (variant is not { VfxId: > 0 } change) return null;
        var id = change.VfxId;
        var root = option.Path[..option.Path.LastIndexOf("/model/", StringComparison.Ordinal)];
        var path = weapon
            ? id < 100 ? $"{root}/vfx/eff/vw{id:D4}.avfx" : $"vfx/weapon/eff/vw{id:D4}.avfx"
            : !accessory && id >= 100 ? $"vfx/equipment/eff/ve{id:D4}.avfx" : $"{root}/vfx/eff/v{(accessory ? 'a' : 'e')}{id:D4}.avfx";
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            if (!game.FileExists(path)) return null;
        }
        var anchors = ReadItemAnchors(option.Path, token);
        return new(path, ReadAvfxPreview(path, anchors, token));
    }

    private Dictionary<int, Matrix4x4> ReadItemAnchors(string path, CancellationToken token)
    {
        var model = ReadModelFile(path, token);
        using var reader = model.Reader;
        var header = MdlStructs.ModelFileHeader.Read(reader);
        for (var i = 0; i < header.VertexDeclarationCount; i++) MdlStructs.VertexDeclarationStruct.Read(reader);
        reader.ReadUInt32();
        var stringsLength = reader.ReadUInt32();
        if (stringsLength > 16 * 1024 * 1024) throw new InvalidDataException("模型字符串表过大。");
        var strings = reader.ReadBytes((int)stringsLength);
        var modelHeader = reader.ReadStructure<MdlStructs.ModelHeader>();
        var anchors = new Dictionary<int, Matrix4x4>();
        for (var i = 0; i < modelHeader.ElementIdCount; i++)
        {
            token.ThrowIfCancellationRequested();
            var id = reader.ReadInt32();
            var bone = reader.ReadUInt32();
            var position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            var rotation = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()) * (MathF.PI / 180);
            if (bone >= strings.Length || !Finite(position) || !Finite(rotation)) continue;
            // 独立武器的 n_hara 处于模型原点。其他骨骼需取得对应的武器姿势。
            if (!strings.AsSpan((int)bone).StartsWith("n_hara\0"u8) &&
                !strings.AsSpan((int)bone).StartsWith("n_root\0"u8)) continue;
            anchors.TryAdd(id, RotationMatrix(rotation) * Matrix4x4.CreateTranslation(position));
        }
        return anchors;
    }

    private static bool TryAvfxItemAnchor(AvfxChunk[][] binders, int index, IReadOnlyDictionary<int, Matrix4x4> anchors, out Matrix4x4 result)
    {
        result = Matrix4x4.Identity;
        if ((uint)index >= binders.Length || AvfxInt(binders[index], "BnVr", -1) != 0) return false;
        var properties = ReadAvfxChunks(AvfxData(binders[index], "PrpS"));
        var type = AvfxInt(properties, "BPTP", -1);
        if (type == 3 && AvfxInt(properties, "BPT", -1) == 0)
            return anchors.TryGetValue(AvfxInt(properties, "BPID", -1), out result);
        return false;
    }

    private static AvfxEmbeddedModel? ReadEmbeddedAvfxModel(ReadOnlyMemory<byte> data)
    {
        const int stride = 36;
        var chunks = ReadAvfxChunks(data);
        var vertices = AvfxData(chunks, "VDrw").Span;
        var indices = AvfxData(chunks, "VIdx").Span;
        if (vertices.Length % stride != 0 || indices.Length % 6 != 0 ||
            vertices.Length / stride is < 3 or > 32768 || indices.Length is < 6 or > 393216) return null;
        var positions = new Vector3[vertices.Length / stride];
        var texcoords = new Vector2[positions.Length];
        var alpha = new byte[positions.Length];
        for (var i = 0; i < positions.Length; i++)
        {
            var vertex = vertices.Slice(i * stride, stride);
            var position = new Vector3(AvfxHalf(vertex), AvfxHalf(vertex[2..]), AvfxHalf(vertex[4..]));
            var texcoord = new Vector2(AvfxHalf(vertex[20..]), AvfxHalf(vertex[22..]));
            if (!Finite(position) || !float.IsFinite(texcoord.X) || !float.IsFinite(texcoord.Y)) return null;
            positions[i] = position;
            texcoords[i] = texcoord + new Vector2(.5f);
            alpha[i] = vertex[19];
        }
        var triangles = new int[indices.Length / 2];
        for (var i = 0; i < triangles.Length; i++)
        {
            var index = BinaryPrimitives.ReadUInt16LittleEndian(indices[(i * 2)..]);
            if (index >= positions.Length) return null;
            triangles[i] = index;
        }
        return new(new GameModelMesh(positions, triangles) { TextureCoordinates = texcoords }, alpha);
    }

    // WPF 网格不能直接传顶点 Alpha。为三角形分配独立贴图区域，保留插值后的透明度。
    private static (GameModelMesh Mesh, GameTexture Texture) BakeAvfxVertexAlpha(
        AvfxEmbeddedModel model, GameTexture texture, CancellationToken token)
    {
        const int tile = 10;
        const int inner = 5;
        var source = model.Mesh;
        var count = source.TriangleIndices.Length / 3;
        var columns = (int)Math.Ceiling(Math.Sqrt(count));
        var width = columns * tile;
        var height = (count + columns - 1) / columns * tile;
        var pixels = new byte[width * height * 4];
        var positions = new Vector3[count * 3];
        var uv = new Vector2[positions.Length];
        var indices = new int[positions.Length];
        for (var triangle = 0; triangle < count; triangle++)
        {
            token.ThrowIfCancellationRequested();
            var first = triangle * 3;
            var a = source.TriangleIndices[first];
            var b = source.TriangleIndices[first + 1];
            var c = source.TriangleIndices[first + 2];
            var x0 = triangle % columns * tile;
            var y0 = triangle / columns * tile;
            positions[first] = source.Positions[a];
            positions[first + 1] = source.Positions[b];
            positions[first + 2] = source.Positions[c];
            uv[first] = new((x0 + 2.5f) / width, (y0 + 2.5f) / height);
            uv[first + 1] = uv[first] + new Vector2((float)inner / width, 0);
            uv[first + 2] = uv[first] + new Vector2(0, (float)inner / height);
            indices[first] = first;
            indices[first + 1] = first + 1;
            indices[first + 2] = first + 2;
            for (var y = 0; y < tile; y++)
            for (var x = 0; x < tile; x++)
            {
                var wb = Math.Clamp((x - 2f) / inner, 0, 1);
                var wc = Math.Clamp((y - 2f) / inner, 0, 1);
                var sum = Math.Max(1, wb + wc);
                wb /= sum;
                wc /= sum;
                var wa = Math.Max(0, 1 - wb - wc);
                var sample = wa * source.TextureCoordinates[a] + wb * source.TextureCoordinates[b] + wc * source.TextureCoordinates[c];
                var tx = (int)(Math.Clamp(sample.X, 0, 1) * (texture.Width - 1) + .5f);
                var ty = (int)(Math.Clamp(sample.Y, 0, 1) * (texture.Height - 1) + .5f);
                var offset = (ty * texture.Width + tx) * 4;
                var pixel = ((y0 + y) * width + x0 + x) * 4;
                texture.Bgra32.AsSpan(offset, 3).CopyTo(pixels.AsSpan(pixel, 3));
                var alpha = wa * model.Alpha[a] + wb * model.Alpha[b] + wc * model.Alpha[c];
                pixels[pixel + 3] = (byte)Math.Clamp(MathF.Round(texture.Bgra32[offset + 3] * alpha / 255), 0, 255);
            }
        }
        return (new(positions, indices) { TextureCoordinates = uv }, new(texture.Path, width, height, pixels));
    }

    private static float AvfxHalf(ReadOnlySpan<byte> data) =>
        (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(data));

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static Matrix4x4 RotationMatrix(Vector3 radians) =>
        Matrix4x4.CreateRotationX(radians.X) * Matrix4x4.CreateRotationY(radians.Y) * Matrix4x4.CreateRotationZ(radians.Z);
}
