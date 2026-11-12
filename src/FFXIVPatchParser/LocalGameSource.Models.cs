using System.Buffers.Binary;
using System.Numerics;
using Lumina.Data;
using Lumina.Data.Parsing;
using Lumina.Data.Structs;
using Lumina.Extensions;
using Lumina.Models.Models;

namespace FFXIVPatchParser;

public sealed record GameModelMesh(Vector3[] Positions, int[] TriangleIndices)
{
    public Vector3[] Normals { get; init; } = [];
    public Vector2[] TextureCoordinates { get; init; } = [];
    public int MaterialIndex { get; init; }
    public int[][] BoneIndices { get; init; } = [];
    public float[][] BoneWeights { get; init; } = [];
}

public sealed record GameModelPreview(string Path, IReadOnlyList<GameModelMesh> Meshes,
    IReadOnlyList<GameModelMaterial> Materials, IReadOnlyList<string> BoneNames, IReadOnlyList<string> Warnings);

public sealed partial class LocalGameSource
{
    public IReadOnlyList<GameModelMesh> ReadModelGeometry(string path, CancellationToken token = default)
        => ReadModelPreview(path, 1, token).Meshes;

    public GameModelPreview ReadModelPreview(string path, int materialVariant = 1, CancellationToken token = default)
    {
        if (materialVariant is < 1 or > 9999) throw new ArgumentOutOfRangeException(nameof(materialVariant));
        path = path.Trim().Replace('\\', '/').TrimStart('/');
        var model = ReadModelFile(path, token);
        if (model.FileInfo.Type != FileType.Model) throw new InvalidDataException("此路径不是 SqPack 模型。");
        using var reader = model.Reader;
        var header = MdlStructs.ModelFileHeader.Read(reader);
        if (header.LodCount == 0 || header.EnableEdgeGeometry)
            throw new NotSupportedException("此模型的顶点缓冲区格式不受支持。");

        // The geometry tables precede the bone and shape data, whose layouts vary by client version.
        var declarations = new MdlStructs.VertexDeclarationStruct[header.VertexDeclarationCount];
        for (var i = 0; i < declarations.Length; i++) declarations[i] = MdlStructs.VertexDeclarationStruct.Read(reader);
        reader.ReadUInt32(); // String count and padding.
        var stringTableSize = reader.ReadUInt32();
        if (stringTableSize > 16 * 1024 * 1024) throw new InvalidDataException("模型字符串表过大。");
        var strings = reader.ReadBytes((int)stringTableSize);
        var modelHeader = reader.ReadStructure<MdlStructs.ModelHeader>();
        for (var i = 0; i < modelHeader.ElementIdCount; i++) MdlStructs.ElementIdStruct.Read(reader);
        var lod = reader.ReadStructuresAsArray<MdlStructs.LodStruct>(3)[0];
        if (modelHeader.ExtraLodEnabled) reader.ReadStructuresAsArray<MdlStructs.ExtraLodStruct>(3);

        var meshEnd = lod.MeshIndex + lod.MeshCount;
        if (meshEnd > modelHeader.MeshCount || meshEnd > declarations.Length)
            throw new InvalidDataException("模型网格范围越界。");
        var meshTable = new MdlStructs.MeshStruct[modelHeader.MeshCount];
        for (var i = 0; i < meshTable.Length; i++) meshTable[i] = MdlStructs.MeshStruct.Read(reader);
        reader.ReadUInt32Array(modelHeader.AttributeCount);
        reader.ReadStructuresAsArray<MdlStructs.TerrainShadowMeshStruct>(modelHeader.TerrainShadowMeshCount);
        reader.ReadStructuresAsArray<MdlStructs.SubmeshStruct>(modelHeader.SubmeshCount);
        reader.ReadStructuresAsArray<MdlStructs.TerrainShadowSubmeshStruct>(modelHeader.TerrainShadowSubmeshCount);
        var materialNames = reader.ReadUInt32Array(modelHeader.MaterialCount).Select(offset => ModelString(strings, offset)).ToArray();
        var boneNames = reader.ReadUInt32Array(modelHeader.BoneCount).Select(offset => ModelString(strings, offset)).ToArray();
        var bonePalettes = ReadBonePalettes(reader, header.Version, modelHeader.BoneTableCount, boneNames.Length);

        var vertexBuffer = ModelBuffer(model.Data, header.VertexOffset[0], header.VertexBufferSize[0]);
        var indexBuffer = ModelBuffer(model.Data, header.IndexOffset[0], header.IndexBufferSize[0]);
        var meshes = new List<GameModelMesh>(lod.MeshCount);
        var vertexCount = 0;
        var indexCount = 0L;
        for (var meshIndex = (int)lod.MeshIndex; meshIndex < meshEnd; meshIndex++)
        {
            token.ThrowIfCancellationRequested();
            var mesh = meshTable[meshIndex];
            if (mesh.IndexCount == 0) continue;
            vertexCount += mesh.VertexCount;
            indexCount += mesh.IndexCount;
            if (vertexCount > 1_000_000 || indexCount > 6_000_000)
                throw new NotSupportedException("模型超过预览上限（100 万个顶点或 200 万个三角面）。");

            var elements = declarations[meshIndex].VertexElements;
            var positionIndex = Array.FindIndex(elements, element => element.Usage == (byte)Vertex.VertexUsage.Position);
            if (positionIndex < 0) throw new InvalidDataException("模型网格缺少顶点坐标声明。");
            var element = elements[positionIndex];
            var type = (Vertex.VertexType)element.Type;
            var positionSize = type switch
            {
                Vertex.VertexType.Single3 => 12,
                Vertex.VertexType.Single4 => 16,
                Vertex.VertexType.Half4 => 8,
                _ => throw new NotSupportedException($"不支持的模型顶点坐标格式（{element.Type}）。")
            };
            if (element.Stream >= mesh.VertexStreamCount || element.Stream >= mesh.VertexBufferStride.Length)
                throw new InvalidDataException("模型顶点流编号越界。");

            var stride = mesh.VertexBufferStride[element.Stream];
            var firstVertex = (long)mesh.VertexBufferOffset[element.Stream] + element.Offset;
            var vertexEnd = firstVertex + (mesh.VertexCount - 1L) * stride + positionSize;
            var firstIndex = 2L * mesh.StartIndex;
            var indexEnd = firstIndex + 2L * mesh.IndexCount;
            if (mesh.VertexCount == 0 || element.Offset + positionSize > stride || vertexEnd > vertexBuffer.Length ||
                mesh.IndexCount % 3 != 0 || indexEnd > indexBuffer.Length)
                throw new InvalidDataException("模型顶点或三角面范围无效。");

            var positions = new Vector3[mesh.VertexCount];
            for (var i = 0; i < positions.Length; i++)
            {
                var bytes = vertexBuffer[(int)(firstVertex + i * stride)..];
                var point = type == Vertex.VertexType.Half4
                    ? new Vector3((float)BinaryPrimitives.ReadHalfLittleEndian(bytes),
                        (float)BinaryPrimitives.ReadHalfLittleEndian(bytes[2..]), (float)BinaryPrimitives.ReadHalfLittleEndian(bytes[4..]))
                    : new Vector3(BinaryPrimitives.ReadSingleLittleEndian(bytes), BinaryPrimitives.ReadSingleLittleEndian(bytes[4..]),
                        BinaryPrimitives.ReadSingleLittleEndian(bytes[8..]));
                if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z))
                    throw new InvalidDataException("模型含有无效顶点坐标。");
                positions[i] = point;
            }

            var indices = new int[(int)mesh.IndexCount];
            for (var i = 0; i < indices.Length; i++)
            {
                var index = BinaryPrimitives.ReadUInt16LittleEndian(indexBuffer[(int)(firstIndex + i * 2)..]);
                if (index >= positions.Length) throw new InvalidDataException("模型三角面引用了越界顶点。");
                indices[i] = index;
            }
            var normals = ReadModelAttribute(vertexBuffer, mesh, elements, Vertex.VertexUsage.Normal, 3)
                .Select(v => new Vector3(v.X, v.Y, v.Z)).ToArray();
            var uv = ReadModelAttribute(vertexBuffer, mesh, elements, Vertex.VertexUsage.UV, 2)
                .Select(v => new Vector2(v.X, v.Y)).ToArray();
            var (blendIndices, blendWeights) = ReadBlendData(vertexBuffer, mesh, elements, bonePalettes);
            meshes.Add(new(positions, indices) { Normals = normals, TextureCoordinates = uv, MaterialIndex = mesh.MaterialIndex,
                BoneIndices = blendIndices, BoneWeights = blendWeights });
        }
        if (meshes.Count == 0) throw new InvalidDataException("模型的 LOD 0 没有可显示的主网格。");
        var warnings = new List<string>();
        var materials = materialNames.Select(name => ReadModelMaterial(path, name, materialVariant, token)).ToArray();
        foreach (var material in materials)
            if (material.Warning != null) warnings.Add(material.Warning);
        if (boneNames.Length > 0) warnings.Add("默认显示绑定姿势；怪物可使用本机 Godbert 解码器播放已验证的 PAP 动作。");
        return new(path, meshes, materials, boneNames, warnings);
    }

    private static ReadOnlySpan<byte> ModelBuffer(byte[] data, uint offset, uint length)
    {
        if ((long)offset + length > data.Length) throw new InvalidDataException("模型缓冲区范围越界。");
        return data.AsSpan((int)offset, (int)length);
    }
}
