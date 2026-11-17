using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Lumina.Data.Structs;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FFXIVPatchParser;

public sealed partial class LocalGameSource
{
    // 只读取 MDL 的运行时元数据，其中开头是字符串表。顶点与索引无需解包。
    private static byte[] ReadModelPathMetadata(BinaryReader reader, long offset, int limit)
    {
        var stream = reader.BaseStream;
        stream.Position = offset;
        var size = Unsafe.SizeOf<ModelBlock>();
        var headerBytes = reader.ReadBytes(size);
        if (headerBytes.Length != size) return [];
        var model = MemoryMarshal.Read<ModelBlock>(headerBytes);
        var runtimeBlockEnd = (int)model.StackBlockNum + model.RuntimeBlockNum;
        if (model.Size < size + runtimeBlockEnd * 2L || model.Size > stream.Length - offset ||
            model.RuntimeBlockNum == 0 || model.RuntimeSize == 0) return [];
        stream.Position = offset + size + model.StackBlockNum * 2L;
        var lengths = new ushort[model.RuntimeBlockNum];
        for (var i = 0; i < lengths.Length; i++) lengths[i] = reader.ReadUInt16();
        var address = offset + model.Size + model.RuntimeOffset;
        var data = new byte[(int)Math.Min(limit, model.RuntimeSize)];
        var used = 0;
        foreach (var length in lengths)
        {
            if (used == data.Length) break;
            if (length < 16 || address > stream.Length - length) throw new InvalidDataException("模型元数据块越界。");
            stream.Position = address;
            var header = reader.ReadUInt32();
            reader.ReadUInt32();
            var compressed = reader.ReadUInt32();
            var raw = reader.ReadUInt32();
            if (header < 16 || header > length || raw is 0 or > 65536 ||
                (compressed == 32000 ? raw : compressed) > length - header)
                throw new InvalidDataException("模型元数据块长度异常。");
            stream.Position = address + header;
            var count = Math.Min(data.Length - used, (int)raw);
            if (compressed == 32000) stream.ReadExactly(data.AsSpan(used, count));
            else
            {
                using var payload = new MemoryStream(reader.ReadBytes((int)compressed), writable: false);
                using var inflate = new DeflateStream(payload, CompressionMode.Decompress);
                inflate.ReadExactly(data.AsSpan(used, count));
            }
            used += count;
            address += length;
        }
        return data.AsSpan(0, used).ToArray();
    }

    private static IEnumerable<string> ExtractModelMaterialPaths(byte[] runtime, string? modelPath)
    {
        if (runtime.Length < 8) yield break;
        var size = BinaryPrimitives.ReadUInt32LittleEndian(runtime.AsSpan(4));
        var end = (int)Math.Min(runtime.Length, 8L + size);
        var directory = modelPath is not null ? modelPath[..modelPath.LastIndexOf('/')] : null;
        var terrain = directory?.StartsWith("bg/", StringComparison.Ordinal) == true ||
            directory?.StartsWith("bgcommon/", StringComparison.Ordinal) == true;
        for (var offset = 8; offset < end;)
        {
            var separator = Array.IndexOf(runtime, (byte)0, offset, end - offset);
            if (separator < 0) yield break;
            var value = Encoding.UTF8.GetString(runtime, offset, separator - offset).TrimStart('/');
            offset = separator + 1;
            if (!value.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase)) continue;
            if (value.Contains('/'))
            {
                if (NormalizeFileSystemPath(value) is { } fullPath) yield return fullPath;
            }
            else if (terrain && directory is not null)
            {
                yield return directory + "/" + value;
                yield return directory[..directory.LastIndexOf('/')] + "/material/" + value;
            }
        }
    }

    private static IEnumerable<string> ExtractMaterialShaderPath(byte[] data)
    {
        if (data.Length < 16 || BinaryPrimitives.ReadUInt32LittleEndian(data) != 0x01030000) yield break;
        var stringStart = 16 + 4 * (data[12] + data[13] + data[14]);
        var stringLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(8));
        var shaderOffset = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(10));
        if (shaderOffset >= stringLength || stringStart + stringLength > data.Length) yield break;
        var start = stringStart + shaderOffset;
        var end = Array.IndexOf(data, (byte)0, start, stringLength - shaderOffset);
        if (end <= start) yield break;
        var shader = Encoding.ASCII.GetString(data, start, end - start);
        if (shader.EndsWith(".shpk", StringComparison.OrdinalIgnoreCase) &&
            shader.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'))
            yield return "shader/sm5/shpk/" + shader;
    }
}
