using System.Numerics;
using Lumina.Data;
using Lumina.Data.Parsing;
using Lumina.Models.Models;

namespace FFXIVPatchParser;

public sealed partial class LocalGameSource
{
    private static int[][] ReadBonePalettes(LuminaBinaryReader reader, uint version, int count, int boneCount)
    {
        var result = new int[count][];
        var start = reader.BaseStream.Position;
        if ((version & 0xff) >= 6)
        {
            var offsets = new ushort[count];
            var sizes = new ushort[count];
            for (var i = 0; i < count; i++) { offsets[i] = reader.ReadUInt16(); sizes[i] = reader.ReadUInt16(); }
            for (var i = 0; i < count; i++)
            {
                if (sizes[i] > 256) throw new InvalidDataException("模型骨骼调色板过大。");
                reader.BaseStream.Position = start + i * 4L + offsets[i] * 4L;
                result[i] = Enumerable.Range(0, sizes[i]).Select(_ => (int)reader.ReadUInt16()).ToArray();
            }
        }
        else
        {
            for (var i = 0; i < count; i++)
            {
                var bones = Enumerable.Range(0, 64).Select(_ => (int)reader.ReadUInt16()).ToArray();
                var size = reader.ReadInt32();
                if (size is < 0 or > 64) throw new InvalidDataException("模型骨骼调色板范围无效。");
                result[i] = bones[..size];
            }
        }
        if (result.Any(p => p.Any(i => i >= boneCount))) throw new InvalidDataException("模型骨骼调色板引用越界。");
        return result;
    }

    private static (int[][] Indices, float[][] Weights) ReadBlendData(ReadOnlySpan<byte> buffer,
        MdlStructs.MeshStruct mesh, MdlStructs.VertexElement[] elements, int[][] palettes)
    {
        var wi = Array.FindIndex(elements, e => e.Usage == (byte)Vertex.VertexUsage.BlendWeights && e.UsageIndex == 0);
        var bi = Array.FindIndex(elements, e => e.Usage == (byte)Vertex.VertexUsage.BlendIndices && e.UsageIndex == 0);
        if (wi < 0 || bi < 0 || mesh.BoneTableIndex >= palettes.Length) return ([], []);
        var w = elements[wi]; var b = elements[bi];
        // 面部网格可以使用八个字节分别编码八项权重和索引。
        var components = (w.Type, b.Type) switch { (5 or 8, 5) => 4, (17, 17) => 8, _ => 0 };
        if (components == 0) return ([], []);
        var indices = new int[mesh.VertexCount][]; var weights = new float[mesh.VertexCount][];
        for (var v = 0; v < mesh.VertexCount; v++)
        {
            indices[v] = new int[components]; weights[v] = new float[components];
            for (var j = 0; j < components; j++)
            {
                var weight = ReadBlendByte(buffer, mesh, w, v, j) / 255f;
                var local = ReadBlendByte(buffer, mesh, b, v, j);
                if (weight > 0 && local >= palettes[mesh.BoneTableIndex].Length)
                    throw new InvalidDataException("顶点骨骼引用越界。");
                weights[v][j] = weight;
                indices[v][j] = weight > 0 ? palettes[mesh.BoneTableIndex][local] : 0;
            }
        }
        return (indices, weights);
    }

    private static byte ReadBlendByte(ReadOnlySpan<byte> data, MdlStructs.MeshStruct mesh, MdlStructs.VertexElement element, int vertex, int component)
    {
        if (element.Stream >= mesh.VertexStreamCount || element.Offset + component + 1 > mesh.VertexBufferStride[element.Stream])
            throw new InvalidDataException("蒙皮顶点声明越界。");
        var offset = (long)mesh.VertexBufferOffset[element.Stream] + vertex * mesh.VertexBufferStride[element.Stream] + element.Offset + component;
        if (offset < 0 || offset >= data.Length) throw new InvalidDataException("蒙皮顶点缓冲区越界。");
        return data[(int)offset];
    }

    /// <summary>预先解析骨骼映射和归一化权重，播放期间复用绑定。</summary>
    public static ModelSkinningBinding CreateModelSkinningBinding(GameModelPreview preview, ModelAnimationClip clip)
        => new(preview, clip);

    public sealed class ModelSkinningBinding
    {
        private readonly ModelAnimationClip clip;
        private readonly Vector3[][] positions;
        private readonly (int Bone, float Weight)[][][] influences;

        internal ModelSkinningBinding(GameModelPreview preview, ModelAnimationClip clip)
        {
            if (preview.Path != clip.ModelPath) throw new ArgumentException("动作与预览模型不匹配。");
            this.clip = clip;
            var names = clip.BoneNames.Select((name, index) => (name, index))
                .ToDictionary(p => p.name, p => p.index, StringComparer.Ordinal);
            var assembly = preview.Path.StartsWith("character:", StringComparison.Ordinal);
            var map = preview.BoneNames.Select(name =>
            {
                var faceBone = name.StartsWith("face/", StringComparison.Ordinal);
                if (clip.IsFaceAnimation && assembly && !faceBone) return -2;
                if (assembly && (name.StartsWith("met/", StringComparison.Ordinal) || name.StartsWith("top/", StringComparison.Ordinal)))
                {
                    // 附加骨保持局部参考姿势时，其蒙皮矩阵等于最近一个有动作的祖先。
                    foreach (var ancestor in name[4..].Split('/'))
                        if (names.TryGetValue(ancestor, out var attached)) return attached;
                    if (name.StartsWith("met/", StringComparison.Ordinal)) name = "j_kao";
                    else if (name.StartsWith("top/j_ex_top", StringComparison.Ordinal)) return -2;
                }
                if (faceBone && names.TryGetValue(name, out var layered)) return layered;
                if (faceBone)
                {
                    name = name[5..];
                    // 脸模下缘也使用身体骨骼。只有脸部专用骨在缺少表情轨时随头部运动。
                    if (!clip.IsFaceAnimation && !names.ContainsKey(name)) name = "j_kao";
                }
                return names.TryGetValue(name, out var index) ? index : clip.IsFaceAnimation ? -2 : -1;
            }).ToArray();
            positions = preview.Meshes.Select(mesh => mesh.Positions.ToArray()).ToArray();
            influences = new (int Bone, float Weight)[positions.Length][][];
            for (var m = 0; m < positions.Length; m++)
            {
                var mesh = preview.Meshes[m];
                if (mesh.BoneIndices.Length != positions[m].Length || mesh.BoneWeights.Length != positions[m].Length)
                    throw new NotSupportedException("此模型的顶点蒙皮格式暂不支持动作播放。");
                influences[m] = new (int Bone, float Weight)[positions[m].Length][];
                for (var v = 0; v < positions[m].Length; v++)
                {
                    var weights = mesh.BoneWeights[v];
                    var indices = mesh.BoneIndices[v];
                    var total = weights.Where(w => w > 0).Sum();
                    if (!float.IsFinite(total) || total <= 0 || indices.Length != weights.Length)
                        throw new NotSupportedException("模型有无效的顶点绑定，无法可靠播放。");
                    var binding = new List<(int Bone, float Weight)>(weights.Length);
                    for (var j = 0; j < weights.Length; j++)
                    {
                        if (weights[j] <= 0) continue;
                        var bone = indices[j];
                        if ((uint)bone >= map.Length || map[bone] == -1)
                            throw new NotSupportedException("模型与动作骨架不兼容：找不到顶点引用的骨骼。");
                        binding.Add((map[bone], weights[j] / total));
                    }
                    influences[m][v] = binding.ToArray();
                }
            }
        }

        public Vector3[][] SkinFrame(int frame)
        {
            if ((uint)frame >= clip.Frames.Length) throw new ArgumentOutOfRangeException(nameof(frame));
            var matrices = clip.Frames[frame];
            var result = new Vector3[positions.Length][];
            for (var m = 0; m < result.Length; m++)
            {
                var output = new Vector3[positions[m].Length];
                for (var v = 0; v < output.Length; v++)
                {
                    var value = Vector3.Zero;
                    foreach (var influence in influences[m][v])
                        value += (influence.Bone == -2 ? positions[m][v] : Vector3.Transform(positions[m][v], matrices[influence.Bone])) * influence.Weight;
                    output[v] = value;
                }
                result[m] = output;
            }
            return result;
        }
    }

    public static Vector3[][] SkinModelFrame(GameModelPreview preview, ModelAnimationClip clip, int frame)
        => CreateModelSkinningBinding(preview, clip).SkinFrame(frame);
}
