using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using Lumina.Data.Parsing;
using Lumina.Models.Models;

namespace FFXIVPatchParser;

public sealed record GameModelMaterial(string Reference, string? Path, string? ShaderPackage,
    IReadOnlyList<string> TexturePaths, GameTexture? DiffuseTexture, string? Warning);

public sealed partial class LocalGameSource
{
    private static string ModelString(byte[] strings, uint offset)
    {
        if (offset >= strings.Length) throw new InvalidDataException("模型字符串偏移越界。");
        var end = Array.IndexOf(strings, (byte)0, (int)offset);
        if (end < 0) throw new InvalidDataException("模型字符串没有终止符。");
        return Encoding.UTF8.GetString(strings, (int)offset, end - (int)offset);
    }

    private static Vector4[] ReadModelAttribute(ReadOnlySpan<byte> buffer, MdlStructs.MeshStruct mesh,
        MdlStructs.VertexElement[] elements, Vertex.VertexUsage usage, int minimumComponents)
    {
        var index = Array.FindIndex(elements, e => e.Usage == (byte)usage && e.UsageIndex == 0);
        if (index < 0) return [];
        var element = elements[index];
        var type = (Vertex.VertexType)element.Type;
        var (components, half) = type switch
        {
            (Vertex.VertexType)1 => (2, false),
            Vertex.VertexType.Single3 => (3, false),
            Vertex.VertexType.Single4 => (4, false),
            Vertex.VertexType.Half2 => (2, true),
            Vertex.VertexType.Half4 => (4, true),
            _ => (0, false)
        };
        if (components < minimumComponents) return [];
        if (element.Stream >= mesh.VertexStreamCount || element.Stream >= mesh.VertexBufferStride.Length)
            throw new InvalidDataException("模型属性顶点流越界。");
        var stride = mesh.VertexBufferStride[element.Stream];
        var size = components * (half ? 2 : 4);
        var start = (long)mesh.VertexBufferOffset[element.Stream] + element.Offset;
        if (element.Offset + size > stride || start + (mesh.VertexCount - 1L) * stride + size > buffer.Length)
            throw new InvalidDataException("模型属性缓冲区越界。");
        var result = new Vector4[mesh.VertexCount];
        for (var i = 0; i < result.Length; i++)
        {
            var data = buffer[(int)(start + i * stride)..];
            var v = Vector4.Zero;
            for (var c = 0; c < components; c++)
            {
                var value = half ? (float)BinaryPrimitives.ReadHalfLittleEndian(data[(c * 2)..])
                    : BinaryPrimitives.ReadSingleLittleEndian(data[(c * 4)..]);
                if (!float.IsFinite(value)) throw new InvalidDataException("模型属性包含非有限值。");
                v[c] = value;
            }
            result[i] = v;
        }
        return result;
    }

    private GameModelMaterial ReadModelMaterial(string modelPath, string reference, int variant, CancellationToken token)
    {
        string? path = null;
        string? shader = null;
        var textures = new List<string>();
        try
        {
            token.ThrowIfCancellationRequested();
            var name = reference.TrimStart('/');
            var candidates = new List<string>();
            if (name.Contains('/')) candidates.Add(name);
            var modelFolder = modelPath.LastIndexOf("/model/", StringComparison.Ordinal);
            if (modelFolder >= 0)
            {
                var folder = modelPath[..modelFolder];
                candidates.Add($"{folder}/material/v{variant:D4}/{name}");
                candidates.Add($"{folder}/material/{name}");
            }
            // Some equipment meshes reference the shared human skin material rather than
            // the equipment directory. The material identifier also recovers common roots
            // when a SqPack address has no known model filename.
            var human = Regex.Match(name, @"^mt_(c\d{4})([bfhtz])(\d{4})(?:_|\.)", RegexOptions.CultureInvariant);
            if (human.Success)
            {
                var part = human.Groups[2].Value switch { "b" => "body", "f" => "face", "h" => "hair", "t" => "tail", _ => "zear" };
                var root = $"chara/human/{human.Groups[1].Value}/obj/{part}/{human.Groups[2].Value}{human.Groups[3].Value}/material";
                candidates.Add($"{root}/v{variant:D4}/{name}");
                if (part == "body") candidates.Add($"{root}/v0001/{name}");
                candidates.Add($"{root}/{name}");
            }
            var equipment = Regex.Match(name, @"^mt_c\d{4}([ea]\d{4})_", RegexOptions.CultureInvariant);
            if (equipment.Success)
            {
                var part = equipment.Groups[1].Value.StartsWith('e') ? "equipment" : "accessory";
                candidates.Add($"chara/{part}/{equipment.Groups[1].Value}/material/v{variant:D4}/{name}");
            }
            var creature = Regex.Match(name, @"^mt_([mdw]\d{4})(b\d{4})_", RegexOptions.CultureInvariant);
            if (creature.Success)
            {
                var part = creature.Groups[1].Value[0] switch { 'm' => "monster", 'd' => "demihuman", _ => "weapon" };
                candidates.Add($"chara/{part}/{creature.Groups[1].Value}/obj/body/{creature.Groups[2].Value}/material/v{variant:D4}/{name}");
            }
            byte[] data;
            lock (gate)
            {
                path = candidates.FirstOrDefault(candidate => game.FileExists(candidate));
                if (path == null) return new(reference, null, null, [], null, $"找不到材质 {reference}（变体 {variant}）。");
                data = game.GetFile(path)!.Data;
            }
            // Read only the stable MTRL header and string table. Dawntrail colorset/shader payloads
            // differ from older clients and are deliberately not interpreted as diffuse colors.
            if (data.Length < 16) throw new InvalidDataException("材质头不完整。");
            var stringSize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(8));
            var shaderOffset = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(10));
            var textureCount = data[12];
            var uvCount = data[13];
            var colorCount = data[14];
            var stringStart = 16 + 4 * (textureCount + uvCount + colorCount);
            if ((long)stringStart + stringSize > data.Length) throw new InvalidDataException("材质字符串表越界。");
            var strings = data.AsSpan(stringStart, stringSize).ToArray();
            shader = ModelString(strings, shaderOffset);
            for (var i = 0; i < textureCount; i++)
            {
                var offset = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(16 + 4 * i));
                textures.Add(ModelString(strings, offset));
            }
            // A normal/mask/specular map must never be presented as a base-color map.
            var diffuse = textures.FirstOrDefault(t => t.EndsWith("_d.tex", StringComparison.OrdinalIgnoreCase)
                || t.EndsWith("_base.tex", StringComparison.OrdinalIgnoreCase));
            token.ThrowIfCancellationRequested();
            var image = diffuse == null ? null : ReadTexture(diffuse);
            return new(reference, path, shader, textures, image, image == null
                ? $"{reference}：无独立漫反射纹理，使用灰模；未模拟游戏材质着色器。"
                : $"{reference}：使用原始漫反射近似；未模拟染色、法线和游戏材质着色器。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException)
        {
            return new(reference, path, shader, textures, null, $"{reference}：材质预览不可用（{ex.Message}）。");
        }
    }
}

