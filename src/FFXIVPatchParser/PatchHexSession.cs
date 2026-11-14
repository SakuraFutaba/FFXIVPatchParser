using System.IO.Compression;
using System.Security.Cryptography;

namespace FFXIVPatchParser;

public enum PatchHexSource { Delta, Target, Origin }
public sealed record PatchHexRange(long Start, long End)
{
    public long Length => End - Start;
    public string Label => $"0x{Start:X} – 0x{End:X} · {Workspace.Size(Length)}";
}

/// <summary>只读分段查看补丁及经摘要验证的容器；不重建或复制整个游戏容器。</summary>
public sealed class PatchHexSession : IDisposable
{
    private FileStream? delta, target, origin;
    private readonly object filesGate = new();
    private bool disposed;
    private bool emptyOrigin;
    public IReadOnlyList<PatchHexRange> Ranges { get; private set; } = [];
    public long DeltaLength => delta?.Length ?? 0;
    public long? TargetLength => target?.Length;
    public long? OriginLength => emptyOrigin ? 0 : origin?.Length;
    public string TargetStatus { get; private set; } = "未选择本地目标目录";
    public string OriginStatus { get; private set; } = "未提供旧版目录；旧字节未知";
    public string RangeStatus { get; private set; } = "尚未分析写入区间";

    public static async Task<PatchHexSession> OpenAsync(IEnumerable<string> archives, PatchResource resource,
        string? targetGamePath, string? originGamePath, IProgress<string>? progress = null, CancellationToken token = default)
    {
        var session = new PatchHexSession();
        try
        {
            token.ThrowIfCancellationRequested();
            if (!resource.Available) throw new FileNotFoundException("此资源缺少补丁分卷。");
            foreach (var archive in archives.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                using var zip = ZipFile.OpenRead(archive);
                var entry = zip.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/').Equals(resource.Entry, StringComparison.OrdinalIgnoreCase));
                if (entry == null) continue;
                progress?.Report("读取并校验 VCDIFF 原始字节…");
                var folder = Path.Combine(Workspace.Temp, "hex");
                Directory.CreateDirectory(folder);
                session.delta = new FileStream(Path.Combine(folder, Path.GetRandomFileName()), FileMode.CreateNew,
                    FileAccess.ReadWrite, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
                await using var input = entry.Open();
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                var buffer = new byte[65536];
                int count;
                while ((count = await input.ReadAsync(buffer, token)) != 0)
                {
                    if (session.delta.Length > entry.Length - count) throw new InvalidDataException("差分解压长度超出清单。");
                    hash.AppendData(buffer, 0, count);
                    await session.delta.WriteAsync(buffer.AsMemory(0, count), token);
                }
                if (session.delta.Length != entry.Length || !Convert.ToHexString(hash.GetHashAndReset()).Equals(resource.DeltaMd5, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("VCDIFF 大小或 MD5 与补丁清单不符。");
                await session.delta.FlushAsync(token);
                break;
            }
            if (session.delta == null) throw new FileNotFoundException("找不到补丁差分条目。");
            await session.VerifyLocalFilesAsync(resource, targetGamePath, originGamePath, progress, token);
            progress?.Report("解析 VCDIFF 写入区间…");
            try
            {
                session.delta.Position = 0;
                session.Ranges = VcdiffRanges.Read(session.delta, token, session.TargetLength, 32 * 1024 * 1024)
                    .Select(r => new PatchHexRange(r.Start, r.End)).ToArray();
                session.RangeStatus = $"{session.Ranges.Count:N0} 个目标写入区间，可能包含容器重排。";
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or EndOfStreamException or OverflowException)
            {
                session.RangeStatus = "无法解析写入区间，仍可查看原始 VCDIFF：" + ex.Message;
            }
            token.ThrowIfCancellationRequested();
            return session;
        }
        catch { session.Dispose(); throw; }
    }

    public async Task VerifyLocalFilesAsync(PatchResource resource, string? targetGamePath, string? originGamePath,
        IProgress<string>? progress = null, CancellationToken token = default)
    {
        FileStream? newTarget = null, newOrigin = null;
        try
        {
            (newTarget, var targetStatus) = await OpenVerifiedAsync(targetGamePath, resource.Path, resource.ResultMd5, "目标", progress, token);
            var isEmpty = resource.OriginMd5.Equals("d41d8cd98f00b204e9800998ecf8427e", StringComparison.OrdinalIgnoreCase);
            var originStatus = "清单中的旧文件为空";
            if (!isEmpty)
                (newOrigin, originStatus) = await OpenVerifiedAsync(originGamePath, resource.Path, resource.OriginMd5, "旧版", progress, token);

            lock (filesGate)
            {
                token.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(disposed, this);
                target?.Dispose();
                origin?.Dispose();
                (target, TargetStatus) = (newTarget, targetStatus);
                (origin, OriginStatus) = (newOrigin, originStatus);
                emptyOrigin = isEmpty;
                newTarget = newOrigin = null;
            }
        }
        finally
        {
            newTarget?.Dispose();
            newOrigin?.Dispose();
        }
    }

    private static async Task<(FileStream? Stream, string Status)> OpenVerifiedAsync(string? directory, string relative,
        string expectedMd5, string label, IProgress<string>? progress, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(directory)) return (null, label == "旧版" ? "未提供旧版目录；旧字节未知" : "未选择本地目标目录");
        FileStream? stream = null;
        try
        {
            if (expectedMd5.Length != 32 || !expectedMd5.All(Uri.IsHexDigit)) return (null, $"清单缺少有效的{label} MD5；不显示未验证字节");
            var normalized = relative.Replace('\\', '/');
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            if (Path.GetFileName(root).Equals("sqpack", StringComparison.OrdinalIgnoreCase))
            {
                if (normalized.StartsWith("game/sqpack/", StringComparison.OrdinalIgnoreCase)) normalized = normalized[12..];
                else if (Directory.GetParent(root) is { } parent && parent.Name.Equals("game", StringComparison.OrdinalIgnoreCase))
                {
                    root = parent.Parent?.FullName ?? parent.FullName;
                }
            }
            // 接受安装根目录或 game 目录；boot 等安装根目录资源也按原路径读取。
            if (Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)).Equals("game", StringComparison.OrdinalIgnoreCase)
                && normalized.StartsWith("game/", StringComparison.OrdinalIgnoreCase)) normalized = normalized[5..];
            var path = Workspace.Within(root, normalized);
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
            progress?.Report($"校验{label}容器 MD5（{Workspace.Size(stream.Length)}）…");
            var actual = Convert.ToHexString(await MD5.HashDataAsync(stream, token));
            if (!actual.Equals(expectedMd5, StringComparison.OrdinalIgnoreCase))
            {
                stream.Dispose();
                return (null, $"本地{label}文件 MD5 与清单不符；该版本字节不可用");
            }
            return (stream, $"{label}容器 MD5 已验证");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            stream?.Dispose();
            return (null, $"{label}文件不可用：{ex.Message}");
        }
        catch { stream?.Dispose(); throw; }
    }

    public async Task<byte[]> ReadPageAsync(PatchHexSource source, long offset, int count = 512, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (count < 1 || count > 65536) throw new ArgumentOutOfRangeException(nameof(count), "每页读取 1–65536 字节。");
        if (source == PatchHexSource.Origin && emptyOrigin) return [];
        var stream = source switch { PatchHexSource.Delta => delta, PatchHexSource.Target => target, PatchHexSource.Origin => origin, _ => throw new ArgumentOutOfRangeException(nameof(source)) };
        if (stream == null) throw new InvalidOperationException("此字节来源不可用或会话已关闭。");
        if (offset >= stream.Length) return [];
        var bytes = new byte[(int)Math.Min(count, stream.Length - offset)];
        var read = 0;
        while (read < bytes.Length)
        {
            var length = await RandomAccess.ReadAsync(stream.SafeFileHandle, bytes.AsMemory(read), offset + read, token);
            if (length == 0) throw new EndOfStreamException("已验证的文件在读取时提前结束。");
            read += length;
        }
        return bytes;
    }

    public void Dispose()
    {
        lock (filesGate)
        {
            disposed = true;
            delta?.Dispose();
            target?.Dispose();
            origin?.Dispose();
            delta = target = origin = null;
            emptyOrigin = false;
        }
    }
}

