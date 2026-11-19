using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace FFXIVPatchParser;

public sealed record ModelAnimationClip(string ModelPath, string PapPath, int Slot, float Duration,
    float FramesPerSecond, string[] BoneNames, Matrix4x4[][] Frames)
{
    public bool IsFaceAnimation { get; init; }
    public string? FaceLayerStatus { get; init; }
    internal int[]? ReferenceParents { get; init; }
}

public sealed partial class LocalGameSource
{
    private const long AnimationCacheBudget = 64L * 1024 * 1024;
    private readonly SemaphoreSlim animationBakeGate = new(1, 1);
    private readonly LinkedList<(string Key, ModelAnimationClip Clip, long Bytes)> animationClips = new();
    private long animationClipBytes;

    /// <summary>默认以 120 fps 采样并缓存骨骼帧；窗口呈现频率由播放器独立控制。</summary>
    public Task<ModelAnimationClip> BakeModelAnimationAsync(string modelPath, string papPath, int slot,
        string godbertDirectory, CancellationToken token = default, int samplingRate = 120, int? previewVertexCount = null, string? faceModelPath = null)
    {
        if (previewVertexCount is <= 0) throw new ArgumentOutOfRangeException(nameof(previewVertexCount));
        if (samplingRate is not (60 or 120)) throw new ArgumentOutOfRangeException(nameof(samplingRate));
        return Task.Run(async () =>
        {
            await animationBakeGate.WaitAsync(token);
            try
            {
                token.ThrowIfCancellationRequested();
                var dll = new FileInfo(Path.GetFullPath(Path.Combine(godbertDirectory, "hkAnimationInterop.dll")));
                if (!dll.Exists) throw new FileNotFoundException("所选目录没有 hkAnimationInterop.dll。", dll.FullName);
                var key = $"{modelPath}|{faceModelPath}|{papPath}|{slot}|{samplingRate}|{dll.FullName}|{dll.Length}|{dll.LastWriteTimeUtc.Ticks}";
                for (var node = animationClips.First; node != null; node = node.Next)
                {
                    if (node.Value.Key != key) continue;
                    animationClips.Remove(node);
                    animationClips.AddFirst(node);
                    return node.Value.Clip;
                }
                var clip = await BakeModelAnimationCoreAsync(modelPath, papPath, slot, godbertDirectory, token, samplingRate, faceModelPath: faceModelPath);
                token.ThrowIfCancellationRequested();
                try
                {
                    if (FindEmoteFaceTrack(modelPath, papPath, slot, token, faceModelPath) is { } track)
                    {
                        var face = await BakeModelAnimationCoreAsync(modelPath, track.Path, track.Slot, godbertDirectory, token, samplingRate, faceModelPath: faceModelPath);
                        clip = CombineFaceTrack(clip, face, track, token);
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException
                    or InvalidOperationException or TimeoutException)
                {
                    clip = clip with { FaceLayerStatus = "仅身体动作（表情轨无法解码）" };
                }
                // 计入矩阵、数组及名称的估计开销；限制条目数，避免大量短片段积累。
                var bytes = 256L + clip.Frames.Length * (32L + clip.BoneNames.Length * 64L)
                    + clip.BoneNames.Sum(name => 32L + name.Length * 2L);
                if (bytes <= AnimationCacheBudget)
                {
                    while (animationClips.Last is { } last &&
                        (animationClipBytes + bytes > AnimationCacheBudget || animationClips.Count >= 16))
                    {
                        animationClipBytes -= last.Value.Bytes;
                        animationClips.RemoveLast();
                    }
                    animationClips.AddFirst((key, clip, bytes));
                    animationClipBytes += bytes;
                }
                return clip;
            }
            finally { animationBakeGate.Release(); }
        }, token);
    }

    /// <summary>Samples character PAP poses in an isolated x86 process using the user's Godbert installation.
    /// Native DLLs are loaded in place, never copied or distributed.</summary>
    private async Task<ModelAnimationClip> BakeModelAnimationCoreAsync(string modelPath, string papPath, int slot,
        string godbertDirectory, CancellationToken token, int samplingRate, bool referenceOnly = false, string? faceModelPath = null,
        string? referenceSkeletonPath = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Godbert 动作解码需要 Windows。");
        var owner = AnimationOwner(modelPath) ?? throw new NotSupportedException("当前模型没有可定位的角色骨架。");
        ModelAnimationClip? sharedTarget = null;
        if (!referenceOnly && !papPath.StartsWith(owner + "/animation/", StringComparison.Ordinal))
        {
            if (papPath != FindDefaultModelAnimation(modelPath, token))
                throw new NotSupportedException("请选择当前角色骨架的 PAP。");
            var donor = "character:" + papPath.Split('/')[2];
            var targetPose = await BakeModelAnimationCoreAsync(modelPath, "", 0, godbertDirectory, token, 1, referenceOnly: true);
            sharedTarget = targetPose;
            owner = AnimationOwner(donor)!;

        }
        var id = owner[(owner.LastIndexOf('/') + 1)..];
        var dll = Path.GetFullPath(Path.Combine(godbertDirectory, "hkAnimationInterop.dll"));
        if (!File.Exists(dll)) throw new FileNotFoundException("所选目录没有 hkAnimationInterop.dll。", dll);
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64", "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell)) throw new NotSupportedException("找不到系统 32 位 PowerShell。");
        byte[] skeleton; byte[] pap; byte[]? baseSkeleton = null;
        var skeletonPath = referenceSkeletonPath ?? $"{owner}/skeleton/base/b0001/skl_{id}b0001.sklb";
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            var bank = referenceOnly ? "" : papPath[(owner.Length + "/animation/".Length)..].Split('/')[0];
            if (bank.StartsWith("f", StringComparison.Ordinal))
            {
                var face = ResolvePreviewFaceSkeleton(faceModelPath ?? modelPath, id);
                if (face == null) throw new NotSupportedException("请先选择带脸型的角色，以确定表情骨架。");
                if (face != bank) throw new NotSupportedException($"当前脸型使用 {face} 骨架，此表情包使用 {bank}。请选择 {face} 目录中的表情包或切换脸型。");
                baseSkeleton = game.GetFile(skeletonPath)?.Data ?? throw new FileNotFoundException(skeletonPath);
                skeletonPath = $"{owner}/skeleton/face/{face}/skl_{id}{face}.sklb";
            }
            skeleton = (game.GetFile(skeletonPath) ?? throw new FileNotFoundException("缺少模型基础骨架：" + skeletonPath)).Data;
            pap = referenceOnly ? [] : (game.GetFile(papPath) ?? throw new FileNotFoundException(papPath)).Data;
        }
        if (!referenceOnly && !ParseAnimationMetadata(pap, papPath, token).Slots.Any(s => s.Index == slot)) throw new ArgumentOutOfRangeException(nameof(slot));
        if (skeleton.Length < 16 || BinaryPrimitives.ReadUInt32LittleEndian(skeleton) != 0x736b6c62)
            throw new InvalidDataException("SKLB 文件头无效。");
        var skeletonOffset = HavokSkeletonOffset(skeleton);
        var havokStart = referenceOnly ? 0 : BinaryPrimitives.ReadInt32LittleEndian(pap.AsSpan(18));
        var havokEnd = referenceOnly ? 0 : BinaryPrimitives.ReadInt32LittleEndian(pap.AsSpan(22));
        var cacheRoot = Path.GetFullPath(Path.Combine(Workspace.Temp, "animation-cache"));
        var work = Path.GetFullPath(Path.Combine(cacheRoot, Guid.NewGuid().ToString("N")));
        if (!work.StartsWith(cacheRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("动作临时目录不在工作区内。");
        Directory.CreateDirectory(work);
        var helpers = Path.Combine(cacheRoot, "helpers");
        Directory.CreateDirectory(helpers);
        var helper = Path.Combine(helpers, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(HavokBakeScript))) + ".exe");
        try
        {
        var skelFile = Path.Combine(work, "skeleton.hkx"); var animFile = Path.Combine(work, "animation.hkx");
        var output = Path.Combine(work, "poses.bin");
        await File.WriteAllBytesAsync(skelFile, skeleton[skeletonOffset..], token);
        await File.WriteAllBytesAsync(animFile, pap[havokStart..havokEnd], token);
        var baseFile = Path.Combine(work, "base.hkx");
        if (baseSkeleton != null)
            await File.WriteAllBytesAsync(baseFile, baseSkeleton[HavokSkeletonOffset(baseSkeleton)..], token);
        var compiledHelperExists = File.Exists(helper);
        var start = new ProcessStartInfo(compiledHelperExists ? helper : powershell) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = work };
        if (!compiledHelperExists)
        {
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(HavokBakeScript)) })
                start.ArgumentList.Add(arg);
        }
        start.Environment["FFXIV_HK_REFERENCE"] = referenceOnly ? "1" : "";
        start.Environment["FFXIV_HK_DLL"] = dll;
        start.Environment["FFXIV_HK_SKEL"] = skelFile; start.Environment["FFXIV_HK_ANIM"] = animFile;
        start.Environment["FFXIV_HK_OUTPUT"] = output; start.Environment["FFXIV_HK_SLOT"] = slot.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["FFXIV_HK_HELPER"] = helper;
        start.Environment["FFXIV_HK_FPS"] = samplingRate.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["FFXIV_HK_BASE"] = baseSkeleton == null ? "" : baseFile;
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
        var head = Matrix4x4.Identity;
        if (baseSkeleton != null)
            while (reader.ReadBoolean()) head *= ReadHavokTransform(reader);
        var bones = reader.ReadInt32(); var frames = reader.ReadInt32(); var duration = reader.ReadSingle(); var fps = reader.ReadSingle();
        if (bones is < 1 or > 1024 || frames is < 1 or > 14400 || (long)bones * frames > 1_000_000 || !float.IsFinite(duration) || duration <= 0 || fps != samplingRate)
            throw new InvalidDataException("解码后的动作尺寸无效。");
        var names = new string[bones]; var parents = new int[bones]; var reference = new Matrix4x4[bones];
        for (var b = 0; b < bones; b++) { names[b] = reader.ReadString(); parents[b] = reader.ReadInt32(); reference[b] = ReadHavokTransform(reader); }
        var inverse = new Matrix4x4[bones];
        var bindPose = referenceOnly ? new Matrix4x4[bones] : null;
        for (var b = 0; b < bones; b++)
        {
            var global = reference[b]; var parent = parents[b]; var visited = 0;
            while (parent >= 0)
            {
                if (parent >= bones || ++visited > bones) throw new InvalidDataException("骨架父子层级无效。");
                global *= reference[parent]; parent = parents[parent];
            }
            if (bindPose != null) bindPose[b] = global;
            if (!Matrix4x4.Invert(global, out inverse[b])) throw new InvalidDataException("骨架绑定矩阵不可逆。");
        }
        if (bindPose != null) return new(modelPath, "", 0, 1, 1, names, [bindPose]) { ReferenceParents = parents };
        if (sharedTarget != null && (!names.SequenceEqual(sharedTarget.BoneNames) || !parents.SequenceEqual(sharedTarget.ReferenceParents!)))
            throw new NotSupportedException("共享待机动作与当前角色的骨骼顺序或层级不匹配。");
        var attachment = Matrix4x4.Identity;
        var toLocal = Matrix4x4.Identity;
        if (baseSkeleton != null)
        {
            var root = Array.IndexOf(names, "j_kao");
            if (root < 0 || parents[root] >= 0) throw new InvalidDataException("面部骨架缺少头部根骨。");
            attachment = inverse[root] * head;
            if (!Matrix4x4.Invert(attachment, out toLocal)) throw new InvalidDataException("头部绑定矩阵不可逆。");
        }
        var result = new Matrix4x4[frames][];
        var sharedBind = sharedTarget?.Frames[0];
        var targetInverse = new Matrix4x4[bones];
        var targetLocal = new Matrix4x4[bones];
        var boneOrder = new List<int>();
        if (sharedBind != null)
        {
            for (var b = 0; b < bones; b++)
                if (!Matrix4x4.Invert(sharedBind[b], out targetInverse[b])) throw new InvalidDataException("目标骨架绑定矩阵不可逆。");
            void AddBone(int b)
            {
                if (boneOrder.Contains(b)) return;
                if (parents[b] >= 0) AddBone(parents[b]);
                boneOrder.Add(b);
            }
            for (var b = 0; b < bones; b++)
            {
                targetLocal[b] = parents[b] < 0 ? sharedBind[b] : sharedBind[b] * targetInverse[parents[b]];
                AddBone(b);
            }
        }
        var frameValues = new float[bones * 12];
        for (var f = 0; f < frames; f++)
        {
            token.ThrowIfCancellationRequested();
            reader.BaseStream.ReadExactly(System.Runtime.InteropServices.MemoryMarshal.AsBytes(frameValues.AsSpan()));
            result[f] = new Matrix4x4[bones];
            if (sharedBind != null)
            {
                var posed = new Matrix4x4[bones];
                var posedInverse = new Matrix4x4[bones];
                var targetGlobal = result[f];
                foreach (var b in boneOrder)
                {
                    posed[b] = HavokTransform(frameValues.AsSpan(b * 12, 12));
                    if (!Matrix4x4.Invert(posed[b], out posedInverse[b])) throw new InvalidDataException("共享动作姿势矩阵不可逆。");
                    var parent = parents[b];
                    var local = parent < 0 ? posed[b] : posed[b] * posedInverse[parent];
                    if (!Matrix4x4.Invert(reference[b], out var inverseLocal)) throw new InvalidDataException("共享骨架绑定矩阵不可逆。");
                    var transferred = targetLocal[b] * inverseLocal * local;
                    var length = reference[b].Translation.Length();
                    var ratio = length > 0.00001f ? targetLocal[b].Translation.Length() / length : 1f;
                    transferred.Translation = targetLocal[b].Translation + (local.Translation - reference[b].Translation) * ratio;
                    targetGlobal[b] = parent < 0 ? transferred : transferred * targetGlobal[parent];
                }
                for (var b = 0; b < bones; b++) targetGlobal[b] = targetInverse[b] * targetGlobal[b];
                continue;
            }
            for (var b = 0; b < bones; b++)
            {
                var delta = inverse[b] * HavokTransform(frameValues.AsSpan(b * 12, 12));
                result[f][b] = baseSkeleton == null ? delta : toLocal * delta * attachment;
            }
        }
        return new(modelPath, papPath, slot, duration, fps, names, result) { IsFaceAnimation = baseSkeleton != null };
        }
        finally
        {
            // Each invocation owns a checked, unique directory. Never remove another bake's files.
            try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); }
            catch (IOException) { /* A compiler/virus-scanner may still hold a temporary file. */ }
            catch (UnauthorizedAccessException) { /* Do not hide the original decoder outcome. */ }
        }
    }

    private static int HavokSkeletonOffset(byte[] skeleton)
    {
        var version = Encoding.ASCII.GetString(skeleton, 4, 4);
        var skeletonOffset = version switch
        {
            "0021" => BinaryPrimitives.ReadUInt16LittleEndian(skeleton.AsSpan(10)),
            "0031" or "0032" or "1031" => checked((int)BinaryPrimitives.ReadUInt32LittleEndian(skeleton.AsSpan(12))),
            _ => throw new NotSupportedException("尚未验证此 SKLB 版本：" + version)
        };
        if (skeletonOffset < 16 || skeletonOffset >= skeleton.Length) throw new InvalidDataException("骨架数据范围无效。");
        return skeletonOffset;
    }
    private static Matrix4x4 ReadHavokTransform(BinaryReader reader)
    {
        Span<float> v = stackalloc float[12];
        for (var i = 0; i < v.Length; i++) v[i] = reader.ReadSingle();
        return HavokTransform(v);
    }

    private static Matrix4x4 HavokTransform(ReadOnlySpan<float> v)
    {
        foreach (var value in v)
            if (!float.IsFinite(value)) throw new InvalidDataException("动作变换含非有限数值。");
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
 public static int Main() {
  try { Run(); return 0; }
  catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
 }
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
   if(Environment.GetEnvironmentVariable("FFXIV_HK_REFERENCE")=="1"){
    using(var writer=new BinaryWriter(File.Create(Environment.GetEnvironmentVariable("FFXIV_HK_OUTPUT")))){
     writer.Write(bones);writer.Write(1);writer.Write(1f);writer.Write(1f);
     for(int b=0;b<bones;b++){writer.Write(names[b]);writer.Write(parents[b]);for(int j=0;j<12;j++)writer.Write(reference[b*12+j]);}
    }return;
   }
   var pa=File.ReadAllBytes(Environment.GetEnvironmentVariable("FFXIV_HK_ANIM"));container=Fn<LoadAnim>("loadAnimationContainer")(skeleton,pa,pa.Length);
   if(container==IntPtr.Zero)throw new Exception("Havok rejected animation");
   int slot=int.Parse(Environment.GetEnvironmentVariable("FFXIV_HK_SLOT"));if(slot<0||slot>=Fn<Count>("getNumAnimations")(container))throw new Exception("Invalid clip slot");
   var clip=Fn<GetAnimation>("getAnimation")(container,slot);float duration=Fn<Duration>("getDuration")(clip);
   if(float.IsNaN(duration)||float.IsInfinity(duration)||duration<=0||duration>120)throw new Exception("Unsupported animation duration");
   float fps=int.Parse(Environment.GetEnvironmentVariable("FFXIV_HK_FPS"));int frames=Math.Max(1,(int)Math.Ceiling(duration*fps));
   if((long)frames*bones>1000000)throw new Exception("Animation exceeds preview memory limit");
   using(var writer=new BinaryWriter(File.Create(Environment.GetEnvironmentVariable("FFXIV_HK_OUTPUT")))) {
    string baseFile=Environment.GetEnvironmentVariable("FFXIV_HK_BASE");
    if(!String.IsNullOrEmpty(baseFile)){
     var data=File.ReadAllBytes(baseFile);var body=Fn<Load>("loadSkeleton")(data,data.Length);
     if(body==IntPtr.Zero)throw new Exception("Invalid base skeleton");
     try{
      int n=Fn<Count>("getNumBones")(body);if(n<1||n>1024)throw new Exception("Invalid base bone count");
      var bn=new string[n];Fn<Names>("getBoneNames")(body,bn);var bp=new int[n];Fn<Parents>("getParentIndices")(body,bp);
      var br=new float[n*12];Fn<Reference>("getReferencePose")(body,br);
      int h=Array.IndexOf(bn,"j_kao");if(h<0)throw new Exception("Missing head");
      for(int i=0;h>=0;h=bp[h],i++){if(i>=n||h>=n)throw new Exception("Invalid hierarchy");writer.Write(true);for(int k=0;k<12;k++)writer.Write(br[h*12+k]);}
      writer.Write(false);
     }finally{Fn<Unload>("unloadSkeleton")(body);}
    }
    writer.Write(bones);writer.Write(frames);writer.Write(duration);writer.Write(fps);
    for(int b=0;b<bones;b++){writer.Write(names[b]);writer.Write(parents[b]);for(int j=0;j<12;j++)writer.Write(reference[b*12+j]);}
    var pose=new float[bones*12];var poseBytes=new byte[bones*48];var sample=Fn<Pose>("getPose");
    for(int f=0;f<frames;f++){if(sample(clip,f/fps,pose)!=bones)throw new Exception("Incomplete sampled pose");Buffer.BlockCopy(pose,0,poseBytes,0,poseBytes.Length);writer.Write(poseBytes);}
   }
  } finally { if(container!=IntPtr.Zero)Fn<Unload>("unloadAnimationContainer")(container);if(skeleton!=IntPtr.Zero)Fn<Unload>("unloadSkeleton")(skeleton);Fn<Empty>("quitHkInterop")(); }
 }
}
'@
$helper = $env:FFXIV_HK_HELPER
if (!(Test-Path -LiteralPath $helper)) {
 $compiled = Join-Path $env:TEMP 'helper.exe'
 $parameters = New-Object System.CodeDom.Compiler.CompilerParameters
 $parameters.CompilerOptions = '/platform:x86 /optimize+'
 [void]$parameters.ReferencedAssemblies.Add('System.dll')
 $parameters.GenerateExecutable = $true
 $parameters.GenerateInMemory = $false
 $parameters.OutputAssembly = $compiled
 Add-Type -TypeDefinition $code -CompilerParameters $parameters
 try { [System.IO.File]::Move($compiled, $helper) } catch [System.IO.IOException] {
  if (!(Test-Path -LiteralPath $helper)) { throw }
 }
}
& $helper
exit $LASTEXITCODE
} catch { [Console]::Error.WriteLine($_.Exception.ToString()); exit 1 }
""";
}

