namespace FFXIVPatchParser;

/// <summary>RFC 3284 default-table instruction reader. Tracks target ranges without materializing either game container.</summary>
internal static class VcdiffRanges
{
    internal readonly record struct Range(long Start, long End);
    private readonly record struct Instruction(int Type, int Size, int Mode = 0);
    private static readonly (Instruction A, Instruction B)[] Codes = BuildCodes();
    private static (Instruction, Instruction)[] BuildCodes()
    {
        var list = new List<(Instruction, Instruction)> { (new(0, 0), default), (new(1, 0), default) };
        for (var size = 1; size <= 17; size++) list.Add((new(1, size), default));
        for (var mode = 0; mode < 9; mode++) { list.Add((new(2, 0, mode), default)); for (var size = 4; size <= 18; size++) list.Add((new(2, size, mode), default)); }
        for (var mode = 0; mode < 6; mode++) for (var add = 1; add <= 4; add++) for (var copy = 4; copy <= 6; copy++) list.Add((new(1, add), new(2, copy, mode)));
        for (var mode = 6; mode < 9; mode++) for (var add = 1; add <= 4; add++) list.Add((new(1, add), new(2, 4, mode)));
        for (var mode = 0; mode < 9; mode++) list.Add((new(2, 4, mode), new(1, 1)));
        return list.ToArray();
    }
    private static long Number(Stream stream) => Number(stream, out _);

    private static long Number(Stream stream, out int byteCount)
    {
        long n = 0;
        for (byteCount = 1; byteCount <= 9; byteCount++)
        {
            var b = stream.ReadByte();
            if (b < 0) throw new EndOfStreamException();
            n = n * 128 + (b & 127);
            if (b < 128) return n;
        }
        throw new InvalidDataException("VCDIFF 整数溢出。");
    }

    private static long AddLength(long start, long length)
    {
        if (length > long.MaxValue - start) throw new InvalidDataException("VCDIFF 地址或长度溢出。");
        return start + length;
    }

    private static void Discard(Stream stream, int length, CancellationToken token)
    {
        Span<byte> buffer = stackalloc byte[8192];
        while (length > 0)
        {
            token.ThrowIfCancellationRequested();
            var count = Math.Min(length, buffer.Length);
            stream.ReadExactly(buffer[..count]);
            length -= count;
        }
    }
    internal static IReadOnlyList<Range> Read(Stream stream, CancellationToken token, long? expectedTargetLength = null, int maxInstructionBytes = 256 * 1024 * 1024)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true);
        if (!reader.ReadBytes(4).SequenceEqual(new byte[] { 0xd6, 0xc3, 0xc4, 0 })) throw new InvalidDataException("不支持的 VCDIFF 头。");
        var header = reader.ReadByte();
        if ((header & ~4) != 0) throw new NotSupportedException("不支持 VCDIFF 自定义码表/二级压缩。");
        if ((header & 4) != 0)
        {
            var size = Number(stream);
            if (size > 1024 * 1024) throw new InvalidDataException("VCDIFF 应用头过大。");
            Discard(stream, (int)size, token);
        }
        long targetBase = 0;
        var ranges = new List<Range>();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var flags = stream.ReadByte(); if (flags < 0) break;
            if ((flags & ~7) != 0 || (flags & 3) == 3) throw new InvalidDataException("VCDIFF 窗口标记异常。");
            long sourceLength = 0, sourcePosition = 0;
            if ((flags & 3) != 0)
            {
                sourceLength = Number(stream);
                sourcePosition = Number(stream);
                var sourceEnd = AddLength(sourcePosition, sourceLength);
                if ((flags & 2) != 0 && sourceEnd > targetBase)
                    throw new InvalidDataException("VCDIFF 目标源段超出已生成数据。");
            }
            var deltaLength = Number(stream);
            if (deltaLength > 256 * 1024 * 1024) throw new InvalidDataException("VCDIFF 窗口超过256MiB限制。");
            var targetLength = Number(stream, out var targetBytes);
            var targetEnd = AddLength(targetBase, targetLength);
            AddLength(sourceLength, targetLength);
            if (reader.ReadByte() != 0) throw new NotSupportedException("不支持压缩VCDIFF节。");
            var dataLength = Number(stream, out var dataBytes);
            var instructionsLength = Number(stream, out var instructionsBytes);
            var addressesLength = Number(stream, out var addressesBytes);
            var checksumBytes = (flags & 4) != 0 ? 4 : 0;
            var sectionLength = deltaLength - targetBytes - 1 - dataBytes - instructionsBytes - addressesBytes - checksumBytes;
            if (dataLength > sectionLength || instructionsLength > sectionLength - dataLength
                || addressesLength != sectionLength - dataLength - instructionsLength)
                throw new InvalidDataException("VCDIFF 节长度异常。");
            if (instructionsLength + addressesLength > maxInstructionBytes) throw new NotSupportedException("VCDIFF 指令及地址节超出内存分析限制；仍可查看原始差分。");
            Discard(stream, checksumBytes, token);
            Discard(stream, (int)dataLength, token);
            using var instructions = new MemoryStream(reader.ReadBytes((int)instructionsLength));
            using var addresses = new MemoryStream(reader.ReadBytes((int)addressesLength));
            if (instructions.Length != instructionsLength || addresses.Length != addressesLength) throw new EndOfStreamException();
            var near = new long[4]; var same = new long[768]; var nextNear = 0;
            long position = 0, dataRemaining = dataLength;
            void Execute(Instruction instruction, bool second)
            {
                if (second && instruction == default) return;
                var size = instruction.Size == 0 ? Number(instructions) : instruction.Size;
                if (size <= 0 || size > targetLength - position) throw new InvalidDataException("VCDIFF 指令长度异常。");
                var changed = true;
                if (instruction.Type == 2)
                {
                    long address;
                    if (instruction.Mode < 6)
                    {
                        var n = Number(addresses);
                        address = instruction.Mode switch
                        {
                            0 => n,
                            1 => sourceLength + position - n,
                            _ => AddLength(near[instruction.Mode - 2], n)
                        };
                    }
                    else { var b = addresses.ReadByte(); if (b < 0) throw new EndOfStreamException(); address = same[(instruction.Mode - 6) * 256 + b]; }
                    if (address < 0 || address >= sourceLength + position) throw new InvalidDataException("VCDIFF COPY地址异常。");
                    if (address < sourceLength && size > sourceLength - address)
                        throw new InvalidDataException("VCDIFF COPY跨越源段边界。");
                    near[nextNear] = address; nextNear = (nextNear + 1) % 4; same[address % same.Length] = address;
                    changed = !((flags & 1) != 0 && address < sourceLength && sourcePosition + address == targetBase + position);
                }
                else
                {
                    var consumed = instruction.Type == 0 ? 1 : size;
                    if (consumed > dataRemaining) throw new InvalidDataException("VCDIFF 指令数据不足。");
                    dataRemaining -= consumed;
                }
                if (changed)
                {
                    var start = targetBase + position; var end = start + size;
                    if (ranges.Count > 0 && ranges[^1].End == start) ranges[^1] = new(ranges[^1].Start, end); else ranges.Add(new(start, end));
                }
                if (ranges.Count > 1_000_000) throw new NotSupportedException("VCDIFF 写入区间过多。");
                position += size;
            }
            while (instructions.Position < instructions.Length) { token.ThrowIfCancellationRequested(); var code = Codes[instructions.ReadByte()]; Execute(code.A, false); Execute(code.B, true); }
            if (position != targetLength || addresses.Position != addresses.Length || dataRemaining != 0)
                throw new InvalidDataException("VCDIFF 窗口未完整消费。");
            targetBase = targetEnd;
        }
        if (expectedTargetLength.HasValue && targetBase != expectedTargetLength.Value)
            throw new InvalidDataException("VCDIFF目标总长度与已验证容器不一致。");
        return ranges;
    }
}
