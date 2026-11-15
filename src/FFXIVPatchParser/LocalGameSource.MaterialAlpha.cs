using System.Buffers.Binary;

namespace FFXIVPatchParser;

public sealed partial class LocalGameSource
{
    private const uint AlphaTestKey = 0xA9A3EE25;
    private const uint AlphaTestOn = 0x72AAA9AE;
    private const uint AlphaThreshold = 0x29AC0223;
    private sealed record ShaderAlphaDefaults(IReadOnlyDictionary<uint, uint> Keys, float? Cutoff);
    private readonly Dictionary<string, ShaderAlphaDefaults> shaderAlphaDefaults = new(StringComparer.Ordinal);

    private GameModelMaterial ReadMaterialAlpha(GameModelMaterial material, byte[] data, int stringEnd, CancellationToken token, Func<string, GameTexture?>? textureReader = null)
    {
        // MTRL 布局依据 Penumbra.GameData/Files/Materials/MtrlFile.cs；保留原始标志与覆盖键。
        var p = stringEnd + data[15] + BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6));
        if (p + 12 > data.Length) return material;
        int U16(int at) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at, 2));
        uint U32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at, 4));
        var valueSize = U16(p);
        var keys = U16(p + 2);
        var constants = U16(p + 4);
        var samplers = U16(p + 6);
        var values = p + 12 + 8 * (keys + constants) + 12 * samplers;
        if ((long)values + valueSize > data.Length) return material;
        var overrides = new Dictionary<uint, uint>();
        for (var i = 0; i < keys; i++) overrides[U32(p + 12 + 8 * i)] = U32(p + 16 + 8 * i);
        material = material with { ShaderFlags = U32(p + 8), ShaderKeys = overrides };
        if (material.ShaderPackage is "character.shpk" or "characterlegacy.shpk" && material.DiffuseTexture is { } diffuse)
        {
            // 角色包的遮罩来自法线蓝色通道，不能将任意漫反射或法线 Alpha 当作透明度。
            // Penumbra MaterialExporter.ProcessCharacterNormalOperation 使用同一通道。
            var samplerStart = p + 12 + 8 * (keys + constants);
            for (var i = 0; i < samplers; i++)
            {
                var at = samplerStart + i * 12;
                var index = data[at + 8];
                if (U32(at) != (uint)Lumina.Data.Parsing.TextureUsage.SamplerNormal || index >= material.TexturePaths.Count) continue;
                var normalPath = material.TexturePaths[index];
                GameTexture? normal;
                try { normal = textureReader != null ? textureReader(normalPath) : ReadTexture(normalPath); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
                {
                    return material with { Warning = material.Warning + " 无法读取法线遮罩，暂按不透明基色预览。" };
                }
                if (normal is null) return material;
                var pixels = (byte[])diffuse.Bgra32.Clone();
                var hasMask = false;
                for (var y = 0; y < diffuse.Height; y++)
                {
                    token.ThrowIfCancellationRequested();
                    for (var x = 0; x < diffuse.Width; x++)
                    {
                        var opacity = normal.Bgra32[((y * normal.Height / diffuse.Height) * normal.Width
                            + x * normal.Width / diffuse.Width) * 4];
                        pixels[(y * diffuse.Width + x) * 4 + 3] = opacity;
                        hasMask |= opacity < 255;
                    }
                }
                if (!hasMask) return material;
                var characterCutoff = ReadShaderAlphaDefaults(material.ShaderPackage, token).Cutoff ?? 0;
                for (var c = 0; c < constants; c++)
                {
                    var atConstant = p + 12 + 8 * (keys + c);
                    var offset = U16(atConstant + 4);
                    if (U32(atConstant) == AlphaThreshold && U16(atConstant + 6) >= 4 && offset + 4 <= valueSize)
                        characterCutoff = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(values + offset, 4));
                }
                return material with
                {
                    SurfaceTexture = new(diffuse.Path, diffuse.Width, diffuse.Height, pixels),
                    AlphaMode = (material.ShaderFlags & 0x10) != 0 ? GameMaterialAlphaMode.Blend : GameMaterialAlphaMode.Mask,
                    AlphaCutoff = float.IsFinite(characterCutoff) ? Math.Clamp(characterCutoff, 0, 1) : 0,
                    Warning = material.Warning + " 透明遮罩取自法线蓝色通道；预览保留原始纹理。"
                };
            }
            return material;
        }
        // 背景包使用已验证的漫反射 Alpha；其他包保持原来的不透明回退。
        if (material.ShaderPackage is not ("bg.shpk" or "bgprop.shpk" or "bgcolorchange.shpk"
            or "bgcrestchange.shpk" or "bguvscroll.shpk") || material.DiffuseTexture is null)
            return material;
        var hasAlphaKey = overrides.TryGetValue(AlphaTestKey, out var key);
        if (hasAlphaKey && key != AlphaTestOn) return material;
        float? cutoff = null;
        for (var i = 0; i < constants; i++)
        {
            var at = p + 12 + 8 * (keys + i);
            var offset = U16(at + 4);
            if (U32(at) == AlphaThreshold && U16(at + 6) >= 4 && offset + 4 <= valueSize)
                cutoff = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(values + offset, 4));
        }
        if (!hasAlphaKey || cutoff is not { } explicitCutoff || !float.IsFinite(explicitCutoff))
        {
            var defaults = ReadShaderAlphaDefaults(material.ShaderPackage, token);
            if (!hasAlphaKey && defaults.Keys.GetValueOrDefault(AlphaTestKey) != AlphaTestOn) return material;
            if (cutoff is not { } value || !float.IsFinite(value)) cutoff = defaults.Cutoff;
        }
        var threshold = cutoff is { } candidate && float.IsFinite(candidate) ? candidate : 0;
        // TexTools XivMtrl.EMaterialFlags1: EnableTranslucency=0x10。
        return material with
        {
            AlphaMode = (material.ShaderFlags & 0x10) != 0 ? GameMaterialAlphaMode.Blend : GameMaterialAlphaMode.Mask,
            AlphaCutoff = Math.Clamp(threshold, 0, 1)
        };
    }

    private ShaderAlphaDefaults ReadShaderAlphaDefaults(string shader, CancellationToken token)
    {
        lock (gate)
        {
            if (shaderAlphaDefaults.TryGetValue(shader, out var cached)) return cached;
            var result = new ShaderAlphaDefaults(new Dictionary<uint, uint>(), null);
            try
            {
                var package = ReadShaderPackage(shader, token);
                var keys = package.MaterialKeys.ToDictionary(k => k.Id, k => k.DefaultValue);
                var parameter = package.MaterialParameters.FirstOrDefault(p => p.Id == AlphaThreshold);
                float? cutoff = null;
                if (parameter is not null && parameter.ByteSize >= 4 &&
                    parameter.ByteOffset + 4 <= package.MaterialParameterDefaults.Length)
                    cutoff = BinaryPrimitives.ReadSingleLittleEndian(package.MaterialParameterDefaults.Span.Slice(parameter.ByteOffset, 4));
                result = new(keys, cutoff);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or ArgumentException
                or OverflowException or IndexOutOfRangeException)
            {
                // 无法识别的包只使用 MTRL 的显式键，不据纹理内容推测透明模式。
            }
            shaderAlphaDefaults[shader] = result;
            return result;
        }
    }

}

