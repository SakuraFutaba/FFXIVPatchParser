using System.Buffers.Binary;
using System.Numerics;
using Lumina.Data.Parsing;

namespace FFXIVPatchParser;

public sealed partial class LocalGameSource
{
    private const uint CharacterIndexSampler = 0x565F8FD8;
    private const uint SkinTypeKey = 0x380CAED0, FaceSkin = 0xF5673524;
    private const uint HairTypeKey = 0x24826489, FaceHair = 0x6E5B8F10;

    internal sealed record CharacterSurface(string Shader, bool Face, GameTexture? Diffuse, GameTexture? Normal, GameTexture? Mask);

    /// <summary>Rebuilds only tintable surfaces. The same masks and geometry are used for every palette.</summary>
    public static IReadOnlyList<GameModelMaterial> RecolorCharacterMaterials(GameModelPreview model, GameCharacterColors colors,
        CancellationToken token = default)
    {
        var results = new Dictionary<GameModelMaterial, GameModelMaterial>(ReferenceEqualityComparer.Instance);
        return model.Materials.Select(material =>
        {
            token.ThrowIfCancellationRequested();
            if (material.CharacterTint is not { } tint) return material;
            if (!results.TryGetValue(material, out var result))
                results.Add(material, result = RenderCharacterTint(material, tint, colors, token));
            return result;
        }).ToArray();
    }

    private GameModelMaterial ReadCharacterSurface(GameModelMaterial material, byte[] data, int stringEnd,
        GameCharacterColors? colors, CancellationToken token, Func<string, GameTexture?>? textureReader, (byte First, byte Second) dye)
    {
        var shader = material.ShaderPackage;
        if (shader is not ("hair.shpk" or "skin.shpk" or "iris.shpk" or "charactertattoo.shpk" or
            "character.shpk" or "characterlegacy.shpk" or "characterglass.shpk" or
            "charactertransparency.shpk" or "characterocclusion.shpk")) return material;
        var tail = stringEnd + data[15] + BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6));
        if (tail > data.Length - 12) return material;
        int U16(int at) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at, 2));
        var start = tail + 12 + (U16(tail + 2) + U16(tail + 4)) * 8;
        var end = start + U16(tail + 6) * 12;
        if (end > data.Length) return material;
        GameTexture? Sampler(uint id)
        {
            for (var at = start; at < end; at += 12)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at, 4)) != id) continue;
                var index = data[at + 8];
                if (index >= material.TexturePaths.Count) continue;
                var path = material.TexturePaths[index];
                token.ThrowIfCancellationRequested();
                try { return textureReader == null ? ReadTexture(path) : textureReader(path); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or ArgumentException)
                { return null; }
            }
            return null;
        }

        if (shader is "character.shpk" or "characterlegacy.shpk" or "characterglass.shpk" or "charactertransparency.shpk")
        {
            if (ReadCharacterColorTable(data, stringEnd) is not { } table || Sampler(CharacterIndexSampler) is not { } indices)
                return material;
            if (dye != default) ApplyEquipmentStains(data, stringEnd, table, dye);
            var normal = Sampler((uint)TextureUsage.SamplerNormal);
            // 色表预览输出基色。遮蔽属于间接光照，不能烘入所有光照共用的基色。
            var diffuse = material.DiffuseTexture;
            var surface = RenderColorTable(material.Path!, table, indices, normal, diffuse, token, out var translucent);
            var alpha = shader is "characterglass.shpk" or "charactertransparency.shpk" || (material.ShaderFlags & 0x10) != 0
                ? GameMaterialAlphaMode.Blend : translucent ? GameMaterialAlphaMode.Mask : GameMaterialAlphaMode.Opaque;
            var cutoff = material.AlphaMode == GameMaterialAlphaMode.Opaque && alpha == GameMaterialAlphaMode.Mask
                ? ReadShaderAlphaDefaults(shader, token).Cutoff ?? 0.5f : material.AlphaCutoff;
            return material with { SurfaceTexture = surface, AlphaMode = alpha, AlphaCutoff = cutoff,
                Warning = "基色由 MTRL 色表与索引贴图生成；未计算游戏光照。" };
        }

        var baseImage = material.DiffuseTexture;
        var normalImage = shader == "iris.shpk" ? null : Sampler((uint)TextureUsage.SamplerNormal);
        var maskImage = shader is "hair.shpk" or "iris.shpk" ? Sampler((uint)TextureUsage.SamplerMask) : null;
        if (shader == "characterocclusion.shpk" && normalImage?.Path == "chara/common/texture/eye/eyelids_shadow.tex")
        {
            // The shared eyelid texture is a grayscale shadow mask, not a white base layer.
            var shadow = new byte[normalImage.Bgra32.Length];
            for (var offset = 3; offset < shadow.Length; offset += 4)
                shadow[offset] = Mul(normalImage.Bgra32[offset], 96);
            return material with { SurfaceTexture = new(normalImage.Path, normalImage.Width, normalImage.Height, shadow),
                AlphaMode = GameMaterialAlphaMode.Blend, Warning = "用公共遮罩近似眼睑阴影。" };
        }
        if (shader == "characterocclusion.shpk" || (shader switch
            {
                "hair.shpk" => normalImage == null || maskImage == null,
                "skin.shpk" => baseImage == null || normalImage == null,
                "iris.shpk" => baseImage == null || maskImage == null,
                _ => normalImage == null
            })) return material;
        var face = shader switch
        {
            "skin.shpk" => material.ShaderKeys.GetValueOrDefault(SkinTypeKey, FaceSkin) == FaceSkin,
            "hair.shpk" => material.ShaderKeys.GetValueOrDefault(HairTypeKey) == FaceHair,
            _ => false
        };
        var source = new CharacterSurface(shader, face, baseImage, normalImage, maskImage);
        var tinted = RenderCharacterTint(material, source, colors ?? GameCharacterColors.Neutral, token);
        return colors.HasValue && shader != "charactertattoo.shpk" ? tinted with { CharacterTint = source } : tinted;
    }

    private static GameModelMaterial RenderCharacterTint(GameModelMaterial material, CharacterSurface source,
        GameCharacterColors colors, CancellationToken token)
    {
        var basis = source.Diffuse ?? source.Normal!;
        var w = basis.Width;
        var h = basis.Height;
        var output = new byte[checked(w * h * 4)];
        var mode = source.Shader switch
        {
            "hair.shpk" when source.Face => GameMaterialAlphaMode.Blend,
            "skin.shpk" when source.Face => GameMaterialAlphaMode.Mask,
            "hair.shpk" => GameMaterialAlphaMode.Mask,
            "charactertattoo.shpk" => GameMaterialAlphaMode.Blend,
            _ => GameMaterialAlphaMode.Opaque
        };
        var shadeSkin = new GameRgb(Encode(colors.Skin.R / 255f), Encode(colors.Skin.G / 255f), Encode(colors.Skin.B / 255f));
        for (var y = 0; y < h; y++)
        {
            token.ThrowIfCancellationRequested();
            for (var x = 0; x < w; x++)
            {
                var at = (y * w + x) * 4;
                var n = SurfacePixel(source.Normal, x, y, w, h);
                var m = SurfacePixel(source.Mask, x, y, w, h);
                var p = basis.Bgra32.AsSpan(at, 4);
                var dst = output.AsSpan(at, 4);
                switch (source.Shader)
                {
                    case "hair.shpk":
                        // 法线蓝色混合发色与挑染，Alpha 决定发片覆盖；遮罩 Alpha 保存基色明暗。
                        var highlight = colors.Highlights ?? colors.Hair;
                        dst[0] = Mul(Lerp(colors.Hair.B, highlight.B, n[0]), m[3]);
                        dst[1] = Mul(Lerp(colors.Hair.G, highlight.G, n[0]), m[3]);
                        dst[2] = Mul(Lerp(colors.Hair.R, highlight.R, n[0]), m[3]);
                        dst[3] = n[3];
                        break;
                    case "charactertattoo.shpk":
                        dst[0] = 61;
                        dst[1] = 53;
                        dst[2] = 49;
                        dst[3] = n[3];
                        break;
                    default:
                        var skin = source.Shader == "skin.shpk";
                        var influence = skin ? n[0] : m[0];
                        var tint = skin ? shadeSkin : colors.Eyes;
                        dst[0] = Mul(p[0], Lerp(255, tint.B, influence));
                        dst[1] = Mul(p[1], Lerp(255, tint.G, influence));
                        dst[2] = Mul(p[2], Lerp(255, tint.R, influence));
                        dst[3] = mode == GameMaterialAlphaMode.Opaque ? (byte)255 : p[3];
                        break;
                }
            }
        }
        var warning = source.Shader switch
        {
            "skin.shpk" => "皮肤基色按肤色与染色遮罩合成。",
            "iris.shpk" => "虹膜基色按瞳色与虹膜遮罩合成。",
            "hair.shpk" => "发色、挑染与透明边缘取自角色颜色和贴图遮罩。",
            _ => "面部标记按遮罩近似预览。"
        };
        return material with
        {
            SurfaceTexture = new(basis.Path, w, h, output),
            AlphaMode = mode, AlphaCutoff = mode == GameMaterialAlphaMode.Mask ? 0.5f : 0,
            Warning = warning
        };
    }

    private static Vector3[]? ReadCharacterColorTable(byte[] data, int stringEnd)
    {
        var size = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6));
        var extra = data[15];
        if (extra < 2 || (long)stringEnd + extra + size > data.Length) return null;
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(stringEnd, 2));
        // Dawntrail 色表：每行 64 字节，前 3 个 Half 为线性 RGB。
        if ((flags & 4) == 0 || ((flags >> 4) & 0xFF) != 0x53 || size < 32 * 64) return null;
        var rows = new Vector3[32];
        for (var i = 0; i < rows.Length; i++)
        {
            var bytes = data.AsSpan(stringEnd + extra + 64 * i, 6);
            var rgb = new Vector3((float)BinaryPrimitives.ReadHalfLittleEndian(bytes),
                (float)BinaryPrimitives.ReadHalfLittleEndian(bytes[2..]), (float)BinaryPrimitives.ReadHalfLittleEndian(bytes[4..]));
            if (!float.IsFinite(rgb.X) || !float.IsFinite(rgb.Y) || !float.IsFinite(rgb.Z)) return null;
            rows[i] = rgb;
        }
        return rows;
    }

    private static GameTexture RenderColorTable(string path, Vector3[] table, GameTexture indices, GameTexture? normal,
        GameTexture? diffuse, CancellationToken token, out bool translucent)
    {
        var w = Math.Min(2048, Math.Max(indices.Width, Math.Max(normal?.Width ?? 0, diffuse?.Width ?? 0)));
        var h = Math.Min(2048, Math.Max(indices.Height, Math.Max(normal?.Height ?? 0, diffuse?.Height ?? 0)));
        var output = new byte[checked(w * h * 4)];
        // 保留线性插值再转预览色空间；同组的 256 个混合权重只计算一次。
        var palette = new (byte B, byte G, byte R)[16 * 256];
        for (var pair = 0; pair < 16; pair++)
        for (var weight = 0; weight < 256; weight++)
        {
            var c = Vector3.Lerp(table[2 * pair], table[2 * pair + 1], 1 - weight / 255f);
            palette[(pair << 8) + weight] = (Encode(c.Z), Encode(c.Y), Encode(c.X));
        }

        translucent = false;
        for (var y = 0; y < h; y++)
        {
            token.ThrowIfCancellationRequested();
            for (var x = 0; x < w; x++)
            {
                var id = SurfacePixel(indices, x, y, w, h);
                var color = palette[(((id[2] + 8) / 17) << 8) + id[1]];
                var n = SurfacePixel(normal, x, y, w, h);
                var d = SurfacePixel(diffuse, x, y, w, h);
                var at = (y * w + x) * 4;
                output[at] = Mul(color.B, d.IsEmpty ? (byte)255 : d[0]);
                output[at + 1] = Mul(color.G, d.IsEmpty ? (byte)255 : d[1]);
                output[at + 2] = Mul(color.R, d.IsEmpty ? (byte)255 : d[2]);
                var opacity = n.IsEmpty ? (d.IsEmpty ? (byte)255 : d[3]) : n[0];
                output[at + 3] = opacity;
                translucent |= opacity < 255;
            }
        }
        return diffuse is { } original && original.Width == w && original.Height == h &&
            original.Bgra32.AsSpan().SequenceEqual(output) ? original : new(path, w, h, output);
    }

    private static ReadOnlySpan<byte> SurfacePixel(GameTexture? texture, int x, int y, int w, int h) => texture is null ? []
        : texture.Bgra32.AsSpan(((y * texture.Height / h) * texture.Width + x * texture.Width / w) * 4, 4);

    private static byte Encode(float linear) => (byte)MathF.Round(255 * MathF.Sqrt(Math.Clamp(linear, 0, 1)));
    private static byte Lerp(byte a, byte b, byte weight) => (byte)((a * (255 - weight) + b * weight + 127) / 255);
    private static byte Mul(byte a, byte b) => (byte)((a * b + 127) / 255);
}
