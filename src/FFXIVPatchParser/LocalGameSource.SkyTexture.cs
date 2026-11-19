using System.Buffers.Binary;
using System.Numerics;
using Lumina.Data;

namespace FFXIVPatchParser;

/// <summary>客户端原始天空体积纹理。纹理内容已验证，方向、时间和亮度映射仍是实验性近似。</summary>
public sealed class GameSkyTexture
{
    private readonly byte[] pixels;
    public ReadOnlyMemory<byte> BgraPixels => pixels;
    public string Path { get; }
    public const int Width = 8;
    public const int Height = 32;
    public const int Depth = 24;

    internal GameSkyTexture(string path, byte[] pixels) { Path = path; this.pixels = pixels; }

    /// <summary>
    /// 按方向余弦、高度和一天秒数进行三线性采样。24层对应24小时、纵轴翻转和横轴缩放均为推断，
    /// 未恢复游戏 cSkyParam 的太阳方向、地平线修正、亮度及天气过渡；不要把此结果作为正式天气还原。
    /// </summary>
    public Vector3 SampleExperimental(float horizontalCosine, float elevation, float seconds)
    {
        if (!float.IsFinite(horizontalCosine) || !float.IsFinite(elevation) || !float.IsFinite(seconds))
            throw new ArgumentOutOfRangeException(nameof(seconds), "天空采样参数必须为有限值。");
        var x = (Math.Clamp(horizontalCosine, -1, 1) + 1) * .5f * (Width - 1);
        var y = (1 - Math.Clamp(MathF.Abs(elevation), 0, 1)) * (Height - 1);
        var z = ((seconds % 86400 + 86400) % 86400) / 3600;
        var x0 = (int)x; var x1 = Math.Min(x0 + 1, Width - 1);
        var y0 = (int)y; var y1 = Math.Min(y0 + 1, Height - 1);
        var z0 = Math.Min((int)z, Depth - 1); var z1 = (z0 + 1) % Depth;
        Vector3 Pixel(int ix, int iy, int iz)
        {
            var p = ((iz * Height + iy) * Width + ix) * 4;
            return new Vector3(pixels[p + 2], pixels[p + 1], pixels[p]) / 255;
        }
        Vector3 Slice(int iz) => Vector3.Lerp(
            Vector3.Lerp(Pixel(x0, y0, iz), Pixel(x1, y0, iz), x - x0),
            Vector3.Lerp(Pixel(x0, y1, iz), Pixel(x1, y1, iz), x - x0), y - y0);
        return Vector3.Lerp(Slice(z0), Slice(z1), z - z0);
    }
}

public sealed partial class LocalGameSource
{
    private byte[]? nativeSkyProgram;

    /// <summary>取得已核对资源绑定的 Sky 像素程序。包更新时须重新核对输入布局。</summary>
    public ReadOnlyMemory<byte> ReadNativeSkyProgram()
    {
        lock (gate)
        {
            if (nativeSkyProgram != null) return nativeSkyProgram;
            var data = game.GetFile<FileResource>("shader/sm5/posteffect/Sky.shcd")?.Data;
            nativeSkyProgram = data != null && Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)) ==
                "3A6150C72BB530C28255B1632E81C5767801CD9EB939E633E2E41D8DBE14CBDA"
                ? data.AsSpan(124, 1856).ToArray() : [];
            return nativeSkyProgram;
        }
    }

    private readonly Dictionary<uint, GameSkyTexture?> experimentalSkyTextures = [];

    /// <summary>只接受本地实测的8×32×24 BGRA体积纹理；缺失或格式不符返回null，调用方保留原预览。</summary>
    public GameSkyTexture? ReadExperimentalSkyTexture(uint candidateId, CancellationToken token = default)
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            if (candidateId is 0 or > 999) return null;
            if (experimentalSkyTextures.TryGetValue(candidateId, out var cached)) return cached;
            var path = $"bgcommon/nature/sky/texture/sky_{candidateId:D3}.tex";
            var data = game.GetFile<FileResource>(path)?.Data;
            GameSkyTexture? result = null;
            if (data is { Length: >= 80 })
            {
                var bytes = data.AsSpan();
                var type = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
                var format = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
                var width = BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]);
                var height = BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..]);
                var depth = BinaryPrimitives.ReadUInt16LittleEndian(bytes[12..]);
                var offset = BinaryPrimitives.ReadUInt32LittleEndian(bytes[28..]);
                const int length = GameSkyTexture.Width * GameSkyTexture.Height * GameSkyTexture.Depth * 4;
                // 0x01000000=TextureType3D，0x1450=A8R8G8B8；按实际小端BGRA顺序读取mip0。
                if (type == 0x01000000 && format == 0x1450 && width == GameSkyTexture.Width
                    && height == GameSkyTexture.Height && depth == GameSkyTexture.Depth
                    && offset >= 80 && offset <= data.Length - length)
                    result = new(path, bytes.Slice((int)offset, length).ToArray());
            }
            // 单份仅24KiB，缓存上限64份，避免扫描全目录时无限积累。
            if (experimentalSkyTextures.Count >= 64) experimentalSkyTextures.Clear();
            experimentalSkyTextures[candidateId] = result;
            return result;
        }
    }
}
