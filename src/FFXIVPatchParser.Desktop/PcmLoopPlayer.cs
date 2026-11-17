using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace FFXIVPatchParser.Desktop;

// 预先把循环两端的 PCM 写进同一输出队列，避免到达循环点时重开媒体。
internal sealed class PcmLoopPlayer : IDisposable
{
    private const uint Done = 1;
    private const uint Samples = 2;
    private static readonly int HeaderSize = Marshal.SizeOf<WaveHeader>();
    private readonly FileStream stream;
    private readonly DispatcherTimer refill = new() { Interval = TimeSpan.FromMilliseconds(25) };
    private readonly List<Buffer> buffers = [];
    private readonly long dataOffset;
    private readonly int block;
    private readonly int rate;
    private readonly AudioLoop loop;
    private nint output;
    private int next;
    private long cursor;
    private long origin;
    private uint lastPosition;
    private long positionWrap;
    private bool playing;
    private bool repeating;
    private readonly long frames;

    public event Action? Ended;
    public event Action<Exception>? Failed;
    public double Duration => (double)frames / rate;
    public (double Start, double End)? RecordedLoop { get; }

    public PcmLoopPlayer(string path, AudioLoop? boundaries, uint originalRate)
    {
        stream = File.OpenRead(path);
        try
        {
            using var reader = new BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);
            if (reader.ReadUInt32() != 0x46464952) throw new InvalidDataException("无法读取 PCM WAV。");
            reader.ReadUInt32();
            if (reader.ReadUInt32() != 0x45564157) throw new InvalidDataException("无效的 WAV 文件头。");
            WaveFormat format = default;
            long bytes = 0;
            while (stream.Position + 8 <= stream.Length)
            {
                var kind = reader.ReadUInt32();
                var size = reader.ReadUInt32();
                var start = stream.Position;
                if (size > stream.Length - start) throw new InvalidDataException("WAV 数据不完整。");
                if (kind == 0x20746D66 && size >= 16)
                {
                    format = new(reader.ReadUInt16(), reader.ReadUInt16(), reader.ReadUInt32(),
                        reader.ReadUInt32(), reader.ReadUInt16(), reader.ReadUInt16(), 0);
                }
                else if (kind == 0x61746164)
                {
                    dataOffset = start;
                    bytes = size;
                    break;
                }
                stream.Position = start + size + (size & 1);
            }
            if (format.Tag != 1 || format.Bits != 16 || format.Channels is < 1 or > 2 ||
                format.Block != 2 * format.Channels || format.Rate is 0 or > 384000 || bytes < format.Block)
                throw new InvalidDataException("循环预览需要 16 位 PCM 音轨。");
            block = format.Block;
            rate = (int)format.Rate;
            frames = bytes / block;
            loop = new(0, frames);
            if (boundaries is { } markers && originalRate > 0)
            {
                var first = (long)Math.Round(markers.StartSample * (double)rate / originalRate);
                var last = (long)Math.Round(markers.EndSample * (double)rate / originalRate);
                if (first >= 0 && first < last && last <= frames)
                {
                    loop = new(first, last);
                    RecordedLoop = (first / (double)rate, last / (double)rate);
                }
            }
            Check(waveOutOpen(out var handle, uint.MaxValue, ref format, 0, 0, 0));
            output = handle;
            for (var i = 0; i < 4; i++)
            {
                var buffer = new Buffer(Math.Max(1, rate / 10) * block);
                buffers.Add(buffer);
                Check(waveOutPrepareHeader(output, buffer.Header, HeaderSize));
                buffer.Prepared = true;
            }
            refill.Tick += (_, _) =>
            {
                try { FillQueue(); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException) { Failed?.Invoke(ex); }
            };
        }
        catch { Dispose(); throw; }
    }

    public double Position => (double)CurrentFrame() / rate;

    private long CurrentFrame()
    {
        var time = new WaveTime { Type = Samples };
        if (waveOutGetPosition(output, ref time, 12) != 0) return origin;
        if (time.Value < lastPosition && lastPosition - time.Value > int.MaxValue) positionWrap += 1L << 32;
        lastPosition = time.Value;
        var units = positionWrap + time.Value;
        var frame = origin + (time.Type switch { Samples => units, 4 => units / block, 1 => units * rate / 1000, _ => 0 });
        if (repeating && origin < loop.EndSample && frame >= loop.EndSample)
            return loop.StartSample + (frame - loop.EndSample) % (loop.EndSample - loop.StartSample);
        return Math.Min(frame, frames);
    }

    public void SetVolume(double volume)
    {
        var value = (uint)Math.Round(Math.Clamp(volume, 0, 1) * ushort.MaxValue);
        waveOutSetVolume(output, value | (value << 16));
    }

    public void SetLoop(bool enabled)
    {
        if (repeating == enabled) return;
        var position = CurrentFrame();
        repeating = enabled;
        MoveTo(position);
    }

    public void Seek(double seconds) => MoveTo((long)(Math.Clamp(seconds, 0, Duration) * rate));

    private void MoveTo(long frame)
    {
        Check(waveOutReset(output));
        foreach (var buffer in buffers) buffer.Queued = false;
        next = 0;
        lastPosition = 0;
        positionWrap = 0;
        origin = cursor = repeating && frame >= loop.EndSample ? loop.StartSample : frame;
        if (playing) FillQueue();
    }

    public void Play()
    {
        Check(waveOutRestart(output));
        playing = true;
        refill.Start();
        FillQueue();
    }

    public void Pause()
    {
        playing = false;
        refill.Stop();
        Check(waveOutPause(output));
    }

    private void FillQueue()
    {
        var end = repeating ? loop.EndSample : frames;
        for (var i = 0; i < buffers.Count; i++)
        {
            var buffer = buffers[next];
            var header = Marshal.PtrToStructure<WaveHeader>(buffer.Header);
            if (buffer.Queued && (header.Flags & Done) == 0) break;
            buffer.Queued = false;
            var filled = 0;
            while (filled < buffer.Data.Length)
            {
                if (cursor >= end)
                {
                    if (!repeating) break;
                    cursor = loop.StartSample;
                }
                var read = (int)Math.Min(buffer.Data.Length - filled, (end - cursor) * block);
                stream.Position = dataOffset + cursor * block;
                stream.ReadExactly(buffer.Data.AsSpan(filled, read));
                cursor += read / block;
                filled += read;
            }
            if (filled == 0) break;
            header.Length = (uint)filled;
            Marshal.StructureToPtr(header, buffer.Header, false);
            Check(waveOutWrite(output, buffer.Header, HeaderSize));
            buffer.Queued = true;
            next = (next + 1) % buffers.Count;
        }
        if (!repeating && cursor == frames && buffers.All(buffer =>
                !buffer.Queued || (Marshal.PtrToStructure<WaveHeader>(buffer.Header).Flags & Done) != 0))
        {
            Pause();
            Ended?.Invoke();
        }
    }

    public void Dispose()
    {
        refill.Stop();
        if (output != 0)
        {
            waveOutReset(output);
            foreach (var buffer in buffers)
                if (buffer.Prepared) waveOutUnprepareHeader(output, buffer.Header, HeaderSize);
            waveOutClose(output);
            output = 0;
        }
        foreach (var buffer in buffers) buffer.Dispose();
        buffers.Clear();
        stream.Dispose();
    }

    private static void Check(uint result)
    {
        if (result != 0) throw new InvalidOperationException($"Windows 音频输出错误（{result}）。");
    }

    private sealed class Buffer : IDisposable
    {
        private GCHandle pinned;
        public byte[] Data { get; }
        public nint Header { get; }
        public bool Prepared { get; set; }
        public bool Queued { get; set; }

        public Buffer(int size)
        {
            Data = new byte[size];
            pinned = GCHandle.Alloc(Data, GCHandleType.Pinned);
            Header = Marshal.AllocHGlobal(HeaderSize);
            Marshal.StructureToPtr(new WaveHeader { Data = pinned.AddrOfPinnedObject(), Length = (uint)size }, Header, false);
        }

        public void Dispose()
        {
            Marshal.FreeHGlobal(Header);
            pinned.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private record struct WaveFormat(ushort Tag, ushort Channels, uint Rate, uint BytesPerSecond, ushort Block, ushort Bits, ushort Extra);
    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public nint Data;
        public uint Length, Recorded;
        public nuint User;
        public uint Flags, Loops;
        public nint Next;
        public nuint Reserved;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct WaveTime { public uint Type, Value, Extra; }

    [DllImport("winmm.dll")] private static extern uint waveOutOpen(out nint handle, uint device, ref WaveFormat format, nint callback, nint instance, uint flags);
    [DllImport("winmm.dll")] private static extern uint waveOutPrepareHeader(nint handle, nint header, int size);
    [DllImport("winmm.dll")] private static extern uint waveOutUnprepareHeader(nint handle, nint header, int size);
    [DllImport("winmm.dll")] private static extern uint waveOutWrite(nint handle, nint header, int size);
    [DllImport("winmm.dll")] private static extern uint waveOutPause(nint handle);
    [DllImport("winmm.dll")] private static extern uint waveOutRestart(nint handle);
    [DllImport("winmm.dll")] private static extern uint waveOutReset(nint handle);
    [DllImport("winmm.dll")] private static extern uint waveOutClose(nint handle);
    [DllImport("winmm.dll")] private static extern uint waveOutGetPosition(nint handle, ref WaveTime position, int size);
    [DllImport("winmm.dll")] private static extern uint waveOutSetVolume(nint handle, uint volume);
}
