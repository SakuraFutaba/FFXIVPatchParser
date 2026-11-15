using System.Buffers.Binary;
using System.Text;

namespace FFXIVPatchParser;

public sealed record GameShaderResource(uint Id, string Name, ushort Slot, ushort Size);
public sealed record GameShaderKey(uint Id, uint DefaultValue);
public sealed record GameShaderParameter(uint Id, ushort ByteOffset, ushort ByteSize);

public sealed record GameShaderPackage(string Path, uint Version, int DirectXVersion,
    uint VertexShaderCount, uint PixelShaderCount, uint HullShaderCount, uint DomainShaderCount,
    uint GeometryShaderCount, uint NodeCount, uint MaterialParameterSize, ReadOnlyMemory<byte> MaterialParameterDefaults,
    IReadOnlyList<GameShaderParameter> MaterialParameters, IReadOnlyList<GameShaderResource> Constants,
    IReadOnlyList<GameShaderResource> Samplers, IReadOnlyList<GameShaderResource> Textures,
    IReadOnlyList<GameShaderResource> UnorderedAccessViews, IReadOnlyList<GameShaderKey> SystemKeys,
    IReadOnlyList<GameShaderKey> SceneKeys, IReadOnlyList<GameShaderKey> MaterialKeys);

public sealed partial class LocalGameSource
{
    private readonly Dictionary<string, GameShaderPackage> shaderPackages = new(StringComparer.Ordinal);

    public GameShaderPackage ReadShaderPackage(string nameOrPath, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nameOrPath);
        var path = nameOrPath.Trim().Replace('\\', '/').TrimStart('/').ToLowerInvariant();
        if (!path.Contains('/')) path = "shader/sm5/shpk/" + path;
        if (!path.StartsWith("shader/", StringComparison.Ordinal) || !path.EndsWith(".shpk", StringComparison.Ordinal) ||
            path.Split('/').Any(part => part is "" or "." or ".."))
            throw new ArgumentException("请输入 SHPK 文件名或 shader/ 下的资源路径。", nameof(nameOrPath));

        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            if (shaderPackages.TryGetValue(path, out var cached)) return cached;
            var data = game.GetFile(path)?.Data ?? throw new FileNotFoundException("未找到着色器包 " + path, path);
            token.ThrowIfCancellationRequested();
            var package = ParseShaderPackage(path, data, token);
            shaderPackages.Add(path, package);
            return package;
        }
    }

    // SHPK 的包级声明位于已编译程序和字符串区之前。格式参考 Penumbra.GameData 的 ShpkFile。
    internal static GameShaderPackage ParseShaderPackage(string path, ReadOnlySpan<byte> data, CancellationToken token)
    {
        const int headerSize = 72;
        if (data.Length < headerSize || !data[..4].SequenceEqual("ShPk"u8) || ShaderUInt32(data, 12) != data.Length)
            throw new InvalidDataException("着色器包文件头无效。");

        var version = ShaderUInt32(data, 4);
        var directX = ShaderUInt32(data, 8) switch
        {
            0x00395844 => 9,
            0x31315844 => 11,
            _ => throw new InvalidDataException("着色器包的 DirectX 标识无法识别。")
        };
        var programStart = ShaderUInt32(data, 16);
        var stringStart = ShaderUInt32(data, 20);
        var vertexCount = ShaderUInt32(data, 24);
        var pixelCount = ShaderUInt32(data, 28);
        var paramSize = ShaderUInt32(data, 32);
        var paramCount = ShaderUInt16(data, 36);
        var hasDefaults = ShaderUInt16(data, 38) != 0;
        var constantCount = ShaderUInt32(data, 40);
        var samplerCount = ShaderUInt16(data, 44);
        var textureCount = ShaderUInt16(data, 46);
        var uavCount = ShaderUInt32(data, 48);
        var systemKeyCount = ShaderUInt32(data, 52);
        var sceneKeyCount = ShaderUInt32(data, 56);
        var materialKeyCount = ShaderUInt32(data, 60);
        var nodeCount = ShaderUInt32(data, 64);

        var hasAdditionalStages = version >= 0x0D01;
        var metadataStart = headerSize + (hasAdditionalStages ? 12 : 0) + (version >= 0x0E01 ? 4 : 0);
        if (programStart < metadataStart || programStart > stringStart || stringStart > data.Length)
            throw new InvalidDataException("着色器包数据区偏移越界。");
        var hullCount = hasAdditionalStages ? ShaderUInt32(data, 72) : 0;
        var domainCount = hasAdditionalStages ? ShaderUInt32(data, 76) : 0;
        var geometryCount = hasAdditionalStages ? ShaderUInt32(data, 80) : 0;
        var metadata = data[metadataStart..(int)programStart];
        var strings = data[(int)stringStart..];
        var programCount = (long)vertexCount + pixelCount + hullCount + domainCount + geometryCount;
        var programHeaderSize = hasAdditionalStages ? 20 : 16;
        if (programCount * programHeaderSize > metadata.Length)
            throw new InvalidDataException("着色器程序数量超出数据区。");

        for (long i = 0; i < programCount; i++)
        {
            token.ThrowIfCancellationRequested();
            var program = TakeShaderData(ref metadata, programHeaderSize);
            var offset = ShaderUInt32(program, 0);
            var size = ShaderUInt32(program, 4);
            var vertexPrefixSize = i < vertexCount ? (directX == 11 ? 8 : 4) : 0;
            if (size < vertexPrefixSize || offset + (long)size > stringStart - programStart)
                throw new InvalidDataException("着色器程序偏移越界。");
            var resourceCount = ShaderUInt16(program, 8) + ShaderUInt16(program, 10) +
                ShaderUInt16(program, 12) + ShaderUInt16(program, 14);
            TakeShaderData(ref metadata, resourceCount * 16L);
        }

        var parameterData = TakeShaderData(ref metadata, paramCount * 8L);
        var parameters = new GameShaderParameter[paramCount];
        for (var index = 0; index < parameters.Length; index++)
        {
            var entry = parameterData[(index * 8)..];
            parameters[index] = new(ShaderUInt32(entry, 0), ShaderUInt16(entry, 4), ShaderUInt16(entry, 6));
        }
        ReadOnlyMemory<byte> defaults = hasDefaults ? TakeShaderData(ref metadata, paramSize).ToArray() : default;
        var constants = ReadShaderResources(ref metadata, strings, constantCount);
        var samplers = ReadShaderResources(ref metadata, strings, samplerCount);
        var textures = ReadShaderResources(ref metadata, strings, textureCount);
        var uavs = ReadShaderResources(ref metadata, strings, uavCount);
        var systemKeys = ReadShaderKeys(ref metadata, systemKeyCount);
        var sceneKeys = ReadShaderKeys(ref metadata, sceneKeyCount);
        var materialKeys = ReadShaderKeys(ref metadata, materialKeyCount);
        TakeShaderData(ref metadata, 8); // 两个子视图键的默认值。

        return new(path, version, directX, vertexCount, pixelCount, hullCount, domainCount, geometryCount,
            nodeCount, paramSize, defaults, Array.AsReadOnly(parameters), Array.AsReadOnly(constants),
            Array.AsReadOnly(samplers), Array.AsReadOnly(textures), Array.AsReadOnly(uavs),
            Array.AsReadOnly(systemKeys), Array.AsReadOnly(sceneKeys), Array.AsReadOnly(materialKeys));
    }

    private static GameShaderResource[] ReadShaderResources(ref ReadOnlySpan<byte> metadata,
        ReadOnlySpan<byte> strings, uint count)
    {
        var data = TakeShaderData(ref metadata, count * 16L);
        var resources = new GameShaderResource[count];
        for (var i = 0; i < resources.Length; i++)
        {
            var entry = data[(i * 16)..];
            var offset = ShaderUInt32(entry, 4);
            var length = ShaderUInt16(entry, 8);
            if (offset + (long)length > strings.Length) throw new InvalidDataException("着色器包资源名越界。");
            var name = Encoding.UTF8.GetString(strings.Slice((int)offset, length));
            resources[i] = new(ShaderUInt32(entry, 0), name, ShaderUInt16(entry, 12), ShaderUInt16(entry, 14));
        }
        return resources;
    }

    private static GameShaderKey[] ReadShaderKeys(ref ReadOnlySpan<byte> metadata, uint count)
    {
        var data = TakeShaderData(ref metadata, count * 8L);
        var keys = new GameShaderKey[count];
        for (var i = 0; i < keys.Length; i++)
        {
            var entry = data[(i * 8)..];
            keys[i] = new(ShaderUInt32(entry, 0), ShaderUInt32(entry, 4));
        }
        return keys;
    }

    private static ReadOnlySpan<byte> TakeShaderData(ref ReadOnlySpan<byte> data, long size)
    {
        if (size > data.Length) throw new InvalidDataException("着色器包声明表越界。");
        var result = data[..(int)size];
        data = data[(int)size..];
        return result;
    }

    private static uint ShaderUInt32(ReadOnlySpan<byte> data, int offset)
        => BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);

    private static ushort ShaderUInt16(ReadOnlySpan<byte> data, int offset)
        => BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
}
