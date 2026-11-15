using Lumina.Data.Files;
using System.Buffers.Binary;

namespace FFXIVPatchParser;

public sealed partial class LocalGameSource
{
    private static string? TerritoryDiffuseSampler(byte[] data, int stringEnd, IReadOnlyList<string> textures)
    {
        // Godbert BgMaterial 的 g_SamplerColorMap0；不把法线或遮罩当作基色。
        var metadata = stringEnd + data[15] + BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6));
        if (metadata + 12 > data.Length) return null;
        var keyCount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(metadata + 2));
        var constantCount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(metadata + 4));
        var samplerCount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(metadata + 6));
        var start = metadata + 12 + 8 * (keyCount + constantCount);
        if ((long)start + samplerCount * 12 > data.Length) return null;
        for (var i = 0; i < samplerCount; i++)
        {
            var offset = start + i * 12;
            var id = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
            var index = data[offset + 8];
            if (id is 0x1E6FEF9C or 0x115306BE && index < textures.Count) return textures[index];
        }
        return null;
    }

    private GameTexture ReadTerritoryTexture(string path, int maxDimension, CancellationToken token)
    {
        TexFile tex;
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            tex = game.GetFile<TexFile>(path) ?? throw new FileNotFoundException("未找到纹理：" + path);
        }
        if ((long)tex.Header.Width * tex.Header.Height > 32 * 1024 * 1024)
            throw new NotSupportedException("纹理超过3200万像素预览限制。");
        var mip = 0;
        while (mip + 1 < tex.Header.MipCount &&
            Math.Max(tex.Header.Width >> mip, tex.Header.Height >> mip) > maxDimension) mip++;
        token.ThrowIfCancellationRequested();
        // 先选择已有 mip，再解码压缩块，避免为每张地图纹理生成完整分辨率像素。
        var buffer = tex.TextureBuffer.Filter(mip, 0, TexFile.TextureFormat.B8G8R8A8);
        var width = buffer.Width;
        var height = buffer.Height;
        var pixels = buffer.RawData;
        if (pixels.Length != checked(width * height * 4)) throw new InvalidDataException("纹理解码尺寸异常。");
        if (Math.Max(width, height) <= maxDimension) return new(path, width, height, pixels);
        // 没有 mip 的纹理仍遵守场景缓存尺寸上限。
        var scale = (double)maxDimension / Math.Max(width, height);
        var w = Math.Max(1, (int)(width * scale));
        var h = Math.Max(1, (int)(height * scale));
        var small = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            token.ThrowIfCancellationRequested();
            for (var x = 0; x < w; x++)
                Buffer.BlockCopy(pixels, ((y * height / h) * width + x * width / w) * 4, small, (y * w + x) * 4, 4);
        }
        return new(path, w, h, small);
    }
}
