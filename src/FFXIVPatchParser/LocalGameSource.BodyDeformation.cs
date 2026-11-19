using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace FFXIVPatchParser;

public sealed partial class LocalGameSource
{
    private sealed record BodyDeformer(int? Parent, Dictionary<string, Matrix4x4> Bones);
    private Dictionary<int, BodyDeformer>? bodyDeformers;

    private int? GetBodyDeformerParent(int body)
    {
        lock (gate) return ReadBodyDeformers().GetValueOrDefault(body)?.Parent;
    }

    private Dictionary<int, BodyDeformer> ReadBodyDeformers()
    {
        if (bodyDeformers != null) return bodyDeformers;
        var result = new Dictionary<int, BodyDeformer>();
        var data = game.GetFile("chara/xls/bonedeformer/human.pbd")?.Data;
        if (data is not { Length: >= 4 }) return bodyDeformers = result;
        int I32(int offset) => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
        ushort U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
        var count = I32(0);
        if (count is < 1 or > 512 || 4 + 20 * count > data.Length) return bodyDeformers = result;
        var links = 4 + 12 * count;
        for (var i = 0; i < count; i++)
        {
            var header = 4 + 12 * i;
            var link = U16(header + 2);
            if (link >= count) continue;
            var parentLink = U16(links + 8 * link);
            var parentEntry = parentLink < count ? U16(links + 8 * parentLink + 6) : ushort.MaxValue;
            int? parent = parentEntry < count ? U16(4 + 12 * parentEntry) : null;
            var offset = I32(header + 4);
            var bones = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal);
            if (offset >= 4 + 20 * count && offset <= data.Length - 4)
            {
                var bonesCount = I32(offset);
                if (bonesCount is > 0 and <= 2048)
                {
                    var matricesOffset = offset + 4L + ((bonesCount * 2 + 3) & ~3);
                    if (matricesOffset + 48L * bonesCount <= data.Length)
                        for (var bone = 0; bone < bonesCount; bone++)
                        {
                            var matrices = (int)matricesOffset;
                            var nameOffset = offset + U16(offset + 4 + 2 * bone);
                            if (nameOffset < matrices + 48 * bonesCount || nameOffset >= data.Length) continue;
                            var length = data.AsSpan(nameOffset, Math.Min(96, data.Length - nameOffset)).IndexOf((byte)0);
                            if (length <= 0) continue;
                            var at = matrices + bone * 48;
                            float F(int word) => BitConverter.Int32BitsToSingle(I32(at + word * 4));
                            // SqPack 的 3×4 行为列向量变换，转换成 Numerics 的行向量排列。
                            var matrix = new Matrix4x4(F(0), F(4), F(8), 0, F(1), F(5), F(9), 0,
                                F(2), F(6), F(10), 0, F(3), F(7), F(11), 1);
                            var determinant = matrix.GetDeterminant();
                            if (float.IsFinite(determinant) && MathF.Abs(determinant) > 1e-8f)
                                bones.TryAdd(Encoding.ASCII.GetString(data, nameOffset, length), matrix);
                        }
                }
            }
            result.TryAdd(U16(header), new(parent, bones));
        }
        return bodyDeformers = result;
    }

    private GameModelPreview DeformWearable(GameModelPreview model, int from, int to, CancellationToken token)
    {
        var edges = new List<BodyDeformer>();
        lock (gate)
        {
            var deformers = ReadBodyDeformers();
            var visited = new HashSet<int>();
            for (var body = to; body != from;)
            {
                if (!visited.Add(body) || !deformers.TryGetValue(body, out var edge) || edge.Parent is not { } parent || edge.Bones.Count == 0)
                    throw new InvalidDataException($"无法从体型 {from:D4} 变形到 {to:D4}。");
                edges.Add(edge);
                body = parent;
            }
        }
        edges.Reverse();
        var transforms = new (Matrix4x4 Position, Matrix4x4 Normal)[model.BoneNames.Count];
        for (var i = 0; i < transforms.Length; i++)
        {
            var matrix = Matrix4x4.Identity;
            foreach (var edge in edges)
                if (edge.Bones.TryGetValue(model.BoneNames[i], out var step)) matrix *= step;
            var normal = Matrix4x4.Invert(matrix, out var inverse) ? Matrix4x4.Transpose(inverse) : matrix;
            transforms[i] = (matrix, normal);
        }
        var meshes = new List<GameModelMesh>(model.Meshes.Count);
        foreach (var mesh in model.Meshes)
        {
            token.ThrowIfCancellationRequested();
            if (mesh.BoneIndices.Length != mesh.Positions.Length || mesh.BoneWeights.Length != mesh.Positions.Length)
            {
                meshes.Add(mesh);
                continue;
            }
            var positions = new Vector3[mesh.Positions.Length];
            var normals = mesh.Normals.Length == positions.Length ? new Vector3[positions.Length] : mesh.Normals;
            for (var i = 0; i < positions.Length; i++)
            {
                var pos = Vector3.Zero;
                var normal = Vector3.Zero;
                var weightSum = 0f;
                for (var j = 0; j < mesh.BoneWeights[i].Length; j++)
                {
                    var weight = mesh.BoneWeights[i][j];
                    if (weight <= 0) continue;
                    var transform = transforms[mesh.BoneIndices[i][j]];
                    pos += Vector3.Transform(mesh.Positions[i], transform.Position) * weight;
                    if (normals.Length == positions.Length) normal += Vector3.TransformNormal(mesh.Normals[i], transform.Normal) * weight;
                    weightSum += weight;
                }
                positions[i] = weightSum > 0 ? pos / weightSum : mesh.Positions[i];
                if (normals.Length == positions.Length)
                    normals[i] = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : mesh.Normals[i];
            }
            meshes.Add(mesh with { Positions = positions, Normals = normals });
        }
        return model with { Meshes = meshes };
    }
}
