using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FFXIVPatchParser.Desktop;

/// <summary>用本地 Sky.shcd 绘制六个正方形视锥，再将颜色投影到 WPF 天空穹顶。</summary>
internal sealed unsafe class NativeSkyPass : IDisposable
{
    private const int FaceSize = 64;
    private const int AtlasWidth = FaceSize * 6;
    private static readonly (Vector3 Right, Vector3 Up, Vector3 Forward)[] Faces =
    [
        (-Vector3.UnitZ, Vector3.UnitY, Vector3.UnitX), (Vector3.UnitZ, Vector3.UnitY, -Vector3.UnitX),
        (Vector3.UnitX, -Vector3.UnitZ, Vector3.UnitY), (Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitY),
        (Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ), (-Vector3.UnitX, Vector3.UnitY, -Vector3.UnitZ)
    ];

    // 一个全屏三角形。游戏 PS 的 TEXCOORD0 输入使用裁剪空间的 x、y。
    private static readonly Lazy<byte[]> VertexProgram = new(CompileVertexProgram);
    private static readonly int[] Projection = CreateProjection();
    private const int Width = 256, Height = 128;

    // 本文件使用 Windows SDK d3d11.h 中的 COM 方法次序；参数中的资源描述结构均为连续的 32 位字段。
    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(nint adapter, uint driver, nint software, uint flags,
        uint* levels, uint count, uint sdkVersion, out nint device, out uint level, out nint context);

    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi)]
    private static extern int D3DCompile(byte[] source, nuint size, string? name, nint defines, nint include,
        string entry, string target, uint flags, uint effectFlags, out nint code, out nint errors);

    private static nint Method(nint obj, int index) => (*(nint**)obj)[index];

    private sealed class Resources : IDisposable
    {
        private readonly List<nint> handles = [];
        public nint Keep(int hr, nint resource)
        {
            if (resource != 0) handles.Add(resource);
            Marshal.ThrowExceptionForHR(hr);
            return resource;
        }
        public void Dispose()
        {
            for (var i = handles.Count - 1; i >= 0; i--) Marshal.Release(handles[i]);
            handles.Clear();
        }
    }

    private static byte[] CompileVertexProgram()
    {
        var source = Encoding.ASCII.GetBytes("""
            struct Output { float4 position : SV_Position; float2 xy : TEXCOORD0; };
            Output main(uint vertex : SV_VertexID) {
                Output o;
                o.xy = float2(vertex == 2 ? 3 : -1, vertex == 1 ? 3 : -1);
                o.position = float4(o.xy, 0, 1);
                return o;
            }
            """);
        var hr = D3DCompile(source, (nuint)source.Length, null, 0, 0, "main", "vs_5_0", 0, 0, out var code, out var errors);
        using var owned = new Resources();
        if (errors != 0) owned.Keep(0, errors);
        owned.Keep(hr, code);
        var ptr = ((delegate* unmanaged[Stdcall]<nint, byte*>)Method(code, 3))(code);
        var length = ((delegate* unmanaged[Stdcall]<nint, nuint>)Method(code, 4))(code);
        return new ReadOnlySpan<byte>(ptr, checked((int)length)).ToArray();
    }

    private static nint Shader(Resources owned, nint device, int slot, ReadOnlySpan<byte> code)
    {
        fixed (byte* ptr = code)
        {
            nint shader;
            var hr = ((delegate* unmanaged[Stdcall]<nint, byte*, nuint, nint, nint*, int>)Method(device, slot))
                (device, ptr, (nuint)code.Length, 0, &shader);
            return owned.Keep(hr, shader);
        }
    }

    private static nint Texture(Resources owned, nint device, int slot, ReadOnlySpan<uint> description, void* initial = null)
    {
        fixed (uint* desc = description)
        {
            nint resource;
            var hr = ((delegate* unmanaged[Stdcall]<nint, uint*, void*, nint*, int>)Method(device, slot))
                (device, desc, initial, &resource);
            return owned.Keep(hr, resource);
        }
    }

    private static nint View(Resources owned, nint device, int slot, nint resource)
    {
        nint view;
        var hr = ((delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)Method(device, slot))(device, resource, 0, &view);
        return owned.Keep(hr, view);
    }

    private static void Bind(nint context, int method, uint slot, nint resource) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)Method(context, method))(context, slot, 1, &resource);

    private static void Update(nint context, nint buffer, ReadOnlySpan<float> values)
    {
        fixed (float* data = values)
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, nint, float*, uint, uint, void>)Method(context, 48))
                (context, buffer, 0, 0, data, 0, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Subresource { public void* Data; public uint RowPitch, SlicePitch; }

    private readonly Resources owned = new();
    private nint graphicsContext, renderTarget, readback, viewBuffer, skyBuffer;
    public GameSkyTexture TextureSource { get; }

    public NativeSkyPass(ReadOnlySpan<byte> program, GameSkyTexture texture)
    {
        TextureSource = texture;
        try { Initialize(program, texture); }
        catch { Dispose(); throw; }
    }

    public void Dispose() => owned.Dispose();

    private void Initialize(ReadOnlySpan<byte> program, GameSkyTexture texture)
    {
        uint feature = 0xb000;
        var hr = D3D11CreateDevice(0, 1, 0, 0, &feature, 1, 7, out var device, out _, out var context);
        if (hr < 0) hr = D3D11CreateDevice(0, 5, 0, 0, &feature, 1, 7, out device, out _, out context);
        owned.Keep(hr, device);
        owned.Keep(0, context);
        var vs = Shader(owned, device, 12, VertexProgram.Value);
        var ps = Shader(owned, device, 15, program);
        ((delegate* unmanaged[Stdcall]<nint, nint, nint, uint, void>)Method(context, 11))(context, vs, 0, 0);
        ((delegate* unmanaged[Stdcall]<nint, nint, nint, uint, void>)Method(context, 9))(context, ps, 0, 0);
        ((delegate* unmanaged[Stdcall]<nint, uint, void>)Method(context, 24))(context, 4); // TRIANGLELIST

        // 同一 RGBA8 输出做读回。输入是 BGRA8_UNORM 体积纹理。
        var target = Texture(owned, device, 5, [AtlasWidth, FaceSize, 1, 1, 28, 1, 0, 0, 32, 0, 0]);
        var staging = Texture(owned, device, 5, [AtlasWidth, FaceSize, 1, 1, 28, 1, 0, 3, 0, 0x20000, 0]);
        var rtv = View(owned, device, 9, target);
        ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)Method(context, 33))(context, 1, &rtv, 0);
        fixed (byte* data = texture.BgraPixels.Span)
        {
            var initial = new Subresource { Data = data, RowPitch = GameSkyTexture.Width * 4,
                SlicePitch = GameSkyTexture.Width * GameSkyTexture.Height * 4 };
            var volume = Texture(owned, device, 6, [GameSkyTexture.Width, GameSkyTexture.Height, GameSkyTexture.Depth, 1, 87, 1, 8, 0, 0], &initial);
            Bind(context, 8, 0, View(owned, device, 7, volume));
        }

        // 三线性，U/V 钳制，W 跨夜循环。MinLOD/MaxLOD 为 0，本预览只上传 mip0。
        uint* samplerDesc = stackalloc uint[13] { 0x15, 3, 3, 1, 0, 1, 1, 0, 0, 0, 0, 0, 0 };
        nint sampler;
        hr = ((delegate* unmanaged[Stdcall]<nint, uint*, nint*, int>)Method(device, 23))(device, samplerDesc, &sampler);
        Bind(context, 10, 0, owned.Keep(hr, sampler));
        var proj = Texture(owned, device, 3, [64, 0, 4, 0, 0, 0]);
        var view = Texture(owned, device, 3, [64, 0, 4, 0, 0, 0]);
        var sky = Texture(owned, device, 3, [96, 0, 4, 0, 0, 0]);
        Bind(context, 16, 0, proj);
        Bind(context, 16, 1, view);
        Bind(context, 16, 2, sky);
        Update(context, proj, [1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1]);
        graphicsContext = context;
        renderTarget = target;
        readback = staging;
        viewBuffer = view;
        skyBuffer = sky;
    }

    public BitmapSource Render(float seconds)
    {
        var context = graphicsContext;
        var staging = readback;
        Update(context, skyBuffer, [0,0,0,0, 1,0,0,0, .5f,31.5f/32,0,0, 3.5f/8,-31f/32,0,0,
            seconds/86400 + .5f/24,0,1,0, 0,0,0,0]);
        for (var i = 0; i < Faces.Length; i++)
        {
            var (r,u,f) = Faces[i];
            Update(context, viewBuffer, [r.X,u.X,f.X,0, r.Y,u.Y,f.Y,0, r.Z,u.Z,f.Z,0, 0,0,0,1]);
            float[] viewport = [i * FaceSize, 0, FaceSize, FaceSize, 0, 1];
            fixed (float* vp = viewport)
                ((delegate* unmanaged[Stdcall]<nint, uint, float*, void>)Method(context, 44))(context, 1, vp);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, void>)Method(context, 13))(context, 3, 0);
        }
        ((delegate* unmanaged[Stdcall]<nint, nint, nint, void>)Method(context, 47))(context, staging, renderTarget);
        Subresource mapped;
        var hr = ((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, Subresource*, int>)Method(context, 14))
            (context, staging, 0, 1, 0, &mapped);
        Marshal.ThrowExceptionForHR(hr);
        var pixels = new byte[Width * Height * 4];
        try
        {
            for (var i = 0; i < Projection.Length; i++)
            {
                var offset = Projection[i];
                var src = (byte*)mapped.Data + (offset / AtlasWidth) * mapped.RowPitch + (offset % AtlasWidth) * 4;
                pixels[i*4] = src[2]; pixels[i*4+1] = src[1]; pixels[i*4+2] = src[0]; pixels[i*4+3] = 255;
            }
        }
        finally { ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)Method(context, 15))(context, staging, 0); }
        var bitmap = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, pixels, Width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static int[] CreateProjection()
    {
        var offsets = new int[Width * Height];
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            var lat = MathF.PI * y / (Height-1);
            var lon = MathF.Tau * x / (Width-1);
            var direction = new Vector3(MathF.Sin(lat)*MathF.Cos(lon), MathF.Cos(lat), MathF.Sin(lat)*MathF.Sin(lon));
            var faceIndex = 0;
            var forward = -1f;
            for (var i = 0; i < Faces.Length; i++)
            {
                var dot = Vector3.Dot(Faces[i].Forward, direction);
                if (dot > forward) { faceIndex = i; forward = dot; }
            }
            var face = Faces[faceIndex];
            var u = (1 + Vector3.Dot(face.Right, direction)/forward) * .5f;
            var v = (1 - Vector3.Dot(face.Up, direction)/forward) * .5f;
            offsets[y*Width+x] = faceIndex*FaceSize + Math.Clamp((int)(u*FaceSize),0,FaceSize-1)
                + AtlasWidth*Math.Clamp((int)(v*FaceSize),0,FaceSize-1);
        }
        return offsets;
    }
}
