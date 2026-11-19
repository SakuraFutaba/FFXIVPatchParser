using System.IO;
using System.Text;

namespace FFXIVPatchParser.Desktop;

internal static class ShaderInspection
{
    private const int MaxDisplayedRows = 256;

    public static string Describe(byte[] file)
    {
        var output = new StringBuilder();
        try
        {
            int U32(int offset) => BitConverter.ToInt32(file, offset);
            if (file.Length < 32) throw new InvalidDataException();
            output.AppendLine($"版本 0x{U32(4):X8} · {Encoding.ASCII.GetString(file, 8, 4)} · 声明长度 {U32(12)}");
            var start = checked(U32(16) + U32(24));
            var length = U32(28);
            if (U32(12) != file.Length || start < 32 || length < 32 || start > file.Length - length)
                throw new InvalidDataException();
            var code = file.AsSpan(start, length);
            // 部分顶点程序在 DXBC 前保存额外头部。
            var prefix = code.IndexOf("DXBC"u8);
            if (prefix < 0) throw new InvalidDataException();
            code = code[prefix..];
            if (code.Length < 32 || BitConverter.ToInt32(code[24..]) != code.Length) throw new InvalidDataException();
            var count = BitConverter.ToInt32(code[28..]);
            if (count < 0 || count > (code.Length - 32) / 4) throw new InvalidDataException();
            output.AppendLine($"DXBC 偏移 0x{start + prefix:X} · {code.Length} 字节");
            for (int i = 0; i < Math.Min(count, MaxDisplayedRows); i++)
            {
                if (output.Length >= 32768)
                {
                    output.AppendLine($"其余 {count - i} 个块可在 Hex 中查看。");
                    return output.ToString().TrimEnd();
                }
                var offset = BitConverter.ToInt32(code[(32 + i * 4)..]);
                var chunk = code[offset..];
                var tag = Encoding.ASCII.GetString(chunk[..4]);
                chunk = chunk.Slice(8, BitConverter.ToInt32(chunk[4..]));
                output.AppendLine($"{tag} · {chunk.Length} 字节");
                if (tag == "ISGN") AppendRows(output, chunk, true);
                if (tag == "RDEF") AppendRows(output, chunk, false);
            }
            if (count > MaxDisplayedRows) output.AppendLine($"其余 {count - MaxDisplayedRows} 个块可在 Hex 中查看。");
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException or InvalidDataException)
        {
            output.AppendLine("结构不完整或此程序布局尚未支持，可查看 Hex。");
        }
        return output.ToString().TrimEnd();
    }

    private static void AppendRows(StringBuilder output, ReadOnlySpan<byte> chunk, bool inputs)
    {
        var header = inputs ? chunk : chunk[8..];
        var count = BitConverter.ToInt32(header);
        var start = BitConverter.ToInt32(header[4..]);
        var stride = inputs ? 24 : 32;
        if (start < 0 || start > chunk.Length || count < 0 || count > (chunk.Length - start) / stride)
            throw new InvalidDataException();
        for (int i = 0; i < Math.Min(count, MaxDisplayedRows); i++)
        {
            var row = chunk.Slice(start + i * stride, stride);
            var text = chunk[BitConverter.ToInt32(row)..];
            var end = text.IndexOf((byte)0);
            if (end < 0) throw new InvalidDataException();
            var name = Encoding.ASCII.GetString(text[..Math.Min(end, MaxDisplayedRows)]) + (end > MaxDisplayedRows ? "…" : "");
            output.AppendLine(inputs
                ? $"  输入 {name}{BitConverter.ToUInt32(row[4..])} · 寄存器 {BitConverter.ToUInt32(row[16..])}"
                : $"  {name} · 资源类型 {BitConverter.ToUInt32(row[4..])} · 槽 {BitConverter.ToUInt32(row[20..])} · 数量 {BitConverter.ToUInt32(row[24..])}");
        }
        if (count > MaxDisplayedRows) output.AppendLine($"  其余 {count - MaxDisplayedRows} 项可在 Hex 中查看。");
    }
}
