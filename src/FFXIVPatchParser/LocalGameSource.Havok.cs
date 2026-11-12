using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace FFXIVPatchParser;

public sealed record ModelAnimationClip(string ModelPath, string PapPath, int Slot, float Duration,
    float FramesPerSecond, string[] BoneNames, Matrix4x4[][] Frames);

public sealed partial class LocalGameSource
{
    /// <summary>Samples monster PAP poses in an isolated x86 process using the user's Godbert installation.
    /// Native DLLs are loaded in place, never copied or distributed.</summary>
    public async Task<ModelAnimationClip> BakeModelAnimationAsync(string modelPath, string papPath, int slot,
        string godbertDirectory, CancellationToken token = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Godbert 动作解码需要 Windows。");
        var match = Regex.Match(modelPath, @"^chara/monster/(m\d{4})/obj/body/b\d{4}/model/", RegexOptions.CultureInvariant);
        if (!match.Success) throw new NotSupportedException("当前仅验证怪物本体骨架；其他模型暂不支持播放。");
        var id = match.Groups[1].Value;
        if (!papPath.StartsWith($"chara/monster/{id}/animation/", StringComparison.Ordinal))
            throw new NotSupportedException("PAP 必须属于当前怪物模型。");
        var metadata = ReadAnimationMetadata(papPath, token);
        if (!metadata.Slots.Any(s => s.Index == slot)) throw new ArgumentOutOfRangeException(nameof(slot));
        var dll = Path.GetFullPath(Path.Combine(godbertDirectory, "hkAnimationInterop.dll"));
        if (!File.Exists(dll)) throw new FileNotFoundException("所选目录没有 hkAnimationInterop.dll。", dll);
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64", "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell)) throw new NotSupportedException("找不到系统 32 位 PowerShell。");
        byte[] skeleton; byte[] pap;
        var skeletonPath = $"chara/monster/{id}/skeleton/base/b0001/skl_{id}b0001.sklb";
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            skeleton = (game.GetFile(skeletonPath) ?? throw new FileNotFoundException("缺少怪物骨架：" + skeletonPath)).Data;
            pap = (game.GetFile(papPath) ?? throw new FileNotFoundException(papPath)).Data;
        }
        if (skeleton.Length < 16 || BinaryPrimitives.ReadUInt32LittleEndian(skeleton) != 0x736b6c62)
            throw new InvalidDataException("SKLB 文件头无效。");
        var version = Encoding.ASCII.GetString(skeleton, 4, 4);
        var skeletonOffset = version switch
        {
            "0021" => BinaryPrimitives.ReadUInt16LittleEndian(skeleton.AsSpan(10)),
            "0031" or "0032" => checked((int)BinaryPrimitives.ReadUInt32LittleEndian(skeleton.AsSpan(12))),
            _ => throw new NotSupportedException("尚未验证此 SKLB 版本：" + version)
        };
        if (skeletonOffset < 16 || skeletonOffset >= skeleton.Length) throw new InvalidDataException("骨架数据范围无效。");
        var havokStart = BinaryPrimitives.ReadInt32LittleEndian(pap.AsSpan(18));
        var havokEnd = BinaryPrimitives.ReadInt32LittleEndian(pap.AsSpan(22));
        var cacheRoot = Path.GetFullPath(Path.Combine(Workspace.Temp, "animation-cache"));
        var work = Path.GetFullPath(Path.Combine(cacheRoot, Guid.NewGuid().ToString("N")));
        if (!work.StartsWith(cacheRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("动作临时目录不在工作区内。");
        Directory.CreateDirectory(work);
        try
        {
        var skelFile = Path.Combine(work, "skeleton.hkx"); var animFile = Path.Combine(work, "animation.hkx");
        var output = Path.Combine(work, "poses.bin");
        await File.WriteAllBytesAsync(skelFile, skeleton[skeletonOffset..], token);
        await File.WriteAllBytesAsync(animFile, pap[havokStart..havokEnd], token);
        var start = new ProcessStartInfo(powershell) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = work };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(HavokBakeScript)) }) start.ArgumentList.Add(arg);
        start.Environment["FFXIV_HK_DLL"] = dll;
        start.Environment["FFXIV_HK_SKEL"] = skelFile; start.Environment["FFXIV_HK_ANIM"] = animFile;
        start.Environment["FFXIV_HK_OUTPUT"] = output; start.Environment["FFXIV_HK_SLOT"] = slot.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["TEMP"] = work; start.Environment["TMP"] = work;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动动作解码进程。");
        var stdout = process.StandardOutput.ReadToEndAsync(token); var stderr = process.StandardError.ReadToEndAsync(token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* Process already exited between check and kill. */ }
            await process.WaitForExitAsync(CancellationToken.None);
            token.ThrowIfCancellationRequested();
            throw new TimeoutException("Godbert 动作解码超过 90 秒，已终止独立进程。");
        }
        var diagnostic = (await stderr + " " + await stdout).Trim();
        if (process.ExitCode != 0 || !File.Exists(output))
            throw new InvalidOperationException($"Godbert x86 动作解码失败（退出码 {process.ExitCode}）：{diagnostic}");
        using var reader = new BinaryReader(File.OpenRead(output));
        var bones = reader.ReadInt32(); var frames = reader.ReadInt32(); var duration = reader.ReadSingle(); var fps = reader.ReadSingle();
        if (bones is < 1 or > 1024 || frames is < 1 or > 3600 || (long)bones * frames > 1_000_000 || !float.IsFinite(duration) || duration <= 0)
            throw new InvalidDataException("解码后的动作尺寸无效。");
        var names = new string[bones]; var parents = new int[bones]; var reference = new Matrix4x4[bones];
        for (var b = 0; b < bones; b++) { names[b] = reader.ReadString(); parents[b] = reader.ReadInt32(); reference[b] = ReadHavokTransform(reader); }
        var inverse = new Matrix4x4[bones];
        for (var b = 0; b < bones; b++)
        {
            var global = reference[b]; var parent = parents[b]; var visited = 0;
            while (parent >= 0)
            {
                if (parent >= bones || ++visited > bones) throw new InvalidDataException("骨架父子层级无效。");
                global *= reference[parent]; parent = parents[parent];
            }
            if (!Matrix4x4.Invert(global, out inverse[b])) throw new InvalidDataException("骨架绑定矩阵不可逆。");
        }
        var result = new Matrix4x4[frames][];
        for (var f = 0; f < frames; f++)
        {
            token.ThrowIfCancellationRequested(); result[f] = new Matrix4x4[bones];
            for (var b = 0; b < bones; b++) result[f][b] = inverse[b] * ReadHavokTransform(reader);
        }
        return new(modelPath, papPath, slot, duration, fps, names, result);
        }
        finally
        {
            // Each invocation owns a checked, unique directory. Never remove another bake's files.
            try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); }
            catch (IOException) { /* A compiler/virus-scanner may still hold a temporary file. */ }
            catch (UnauthorizedAccessException) { /* Do not hide the original decoder outcome. */ }
        }
    }

    private static Matrix4x4 ReadHavokTransform(BinaryReader reader)
    {
        var v = new float[12];
        for (var i = 0; i < v.Length; i++) { v[i] = reader.ReadSingle(); if (!float.IsFinite(v[i])) throw new InvalidDataException("动作变换含非有限数值。"); }
        return Matrix4x4.CreateScale(v[4], v[5], v[6])
            * Matrix4x4.CreateFromQuaternion(new Quaternion(v[8], v[9], v[10], v[11]))
            * Matrix4x4.CreateTranslation(v[0], v[1], v[2]);
    }

    private const string HavokBakeScript = """
$ErrorActionPreference = 'Stop'
try {
$code = @'
using System;
using System.IO;
using System.Runtime.InteropServices;
public static class BakeHavok {
 [DllImport("kernel32", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr LoadLibraryW(string path);
 [DllImport("kernel32", CharSet=CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr module,string name);
 [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void Empty();
 [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr Load(byte[] data,int size);
 [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr LoadAnim(IntPtr skeleton,byte[] data,int size);
 [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void Unload(IntPtr p);
 [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Count(IntPtr p);
 [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Names(IntPtr p,[In,Out] string[] names);
 [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Parents(IntPtr p,[In,Out] int[] parents);
 [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Reference(IntPtr p,[In,Out] float[] transforms);
 [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr GetAnimation(IntPtr p,int index);
 [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate float Duration(IntPtr p);
 [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Pose(IntPtr p,float time,[In,Out] float[] transforms);
 static IntPtr library;
 static T Fn<T>(string name) where T:class { var p=GetProcAddress(library,name);if(p==IntPtr.Zero)throw new Exception("Missing native export: "+name);return Marshal.GetDelegateForFunctionPointer(p,typeof(T)) as T; }
 public static void Run() {
  library=LoadLibraryW(Environment.GetEnvironmentVariable("FFXIV_HK_DLL"));
  if(library==IntPtr.Zero)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Cannot load Godbert x86 Havok DLL; check x86 VC++ 2012 runtime.");
  Fn<Empty>("initHkInterop")(); IntPtr skeleton=IntPtr.Zero,container=IntPtr.Zero;
  try {
   var sk=File.ReadAllBytes(Environment.GetEnvironmentVariable("FFXIV_HK_SKEL"));skeleton=Fn<Load>("loadSkeleton")(sk,sk.Length);
   if(skeleton==IntPtr.Zero)throw new Exception("Havok rejected skeleton");
   int bones=Fn<Count>("getNumBones")(skeleton);if(bones<1||bones>1024)throw new Exception("Invalid bone count");
   var names=new string[bones];Fn<Names>("getBoneNames")(skeleton,names);
   var parents=new int[bones];Fn<Parents>("getParentIndices")(skeleton,parents);
   var reference=new float[bones*12];Fn<Reference>("getReferencePose")(skeleton,reference);
   var pa=File.ReadAllBytes(Environment.GetEnvironmentVariable("FFXIV_HK_ANIM"));container=Fn<LoadAnim>("loadAnimationContainer")(skeleton,pa,pa.Length);
   if(container==IntPtr.Zero)throw new Exception("Havok rejected animation");
   int slot=int.Parse(Environment.GetEnvironmentVariable("FFXIV_HK_SLOT"));if(slot<0||slot>=Fn<Count>("getNumAnimations")(container))throw new Exception("Invalid clip slot");
   var clip=Fn<GetAnimation>("getAnimation")(container,slot);float duration=Fn<Duration>("getDuration")(clip);
   if(float.IsNaN(duration)||float.IsInfinity(duration)||duration<=0||duration>120)throw new Exception("Unsupported animation duration");
   float fps=30;int frames=Math.Max(1,(int)Math.Ceiling(duration*fps));if((long)frames*bones>1000000)throw new Exception("Animation exceeds preview memory limit");
   using(var writer=new BinaryWriter(File.Create(Environment.GetEnvironmentVariable("FFXIV_HK_OUTPUT")))) {
    writer.Write(bones);writer.Write(frames);writer.Write(duration);writer.Write(fps);
    for(int b=0;b<bones;b++){writer.Write(names[b]);writer.Write(parents[b]);for(int j=0;j<12;j++)writer.Write(reference[b*12+j]);}
    var pose=new float[bones*12];var sample=Fn<Pose>("getPose");
    for(int f=0;f<frames;f++){if(sample(clip,f/fps,pose)!=bones)throw new Exception("Incomplete sampled pose");foreach(float v in pose)writer.Write(v);}
   }
  } finally { if(container!=IntPtr.Zero)Fn<Unload>("unloadAnimationContainer")(container);if(skeleton!=IntPtr.Zero)Fn<Unload>("unloadSkeleton")(skeleton);Fn<Empty>("quitHkInterop")(); }
 }
}
'@
Add-Type -TypeDefinition $code
[BakeHavok]::Run()
exit 0
} catch { [Console]::Error.WriteLine($_.Exception.ToString()); exit 1 }
""";
}

