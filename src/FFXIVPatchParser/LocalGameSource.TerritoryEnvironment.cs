using System.Buffers.Binary;
using System.Text;
using Lumina.Data;
using Lumina.Data.Structs.Excel;
using Lumina.Excel;
using Lumina.Text.ReadOnly;

namespace FFXIVPatchParser;

public sealed record GameTerritoryWeather(ushort RawValue, string Name, uint IconId = 0)
{
    public ushort Id => (ushort)(RawValue & 0x7FFF);
    public bool HasFlag => (RawValue & 0x8000) != 0;
    public string? IconPath => IconId == 0 ? null : RelationResolver.IconPath(IconId);
}
public sealed record GameTerritoryEnvironment(IReadOnlyList<GameTerritoryWeather> Weathers,
    IReadOnlyList<string> EnvironmentPaths);

public sealed partial class LocalGameSource
{
    /// <summary>读取 LVB 声明的天气槽与环境文件，不将天气编号解释为通用渲染预设。</summary>
    public GameTerritoryEnvironment ReadTerritoryEnvironment(string scenePath, CancellationToken token = default)
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            var path = scenePath.Replace('\\', '/').TrimStart('/');
            if (!path.StartsWith("bg/", StringComparison.OrdinalIgnoreCase)) path = "bg/" + path;
            if (!path.EndsWith(".lvb", StringComparison.OrdinalIgnoreCase)) path += ".lvb";
            var data = game.GetFile<FileResource>(path)?.Data ?? throw new FileNotFoundException("缺少场景环境：" + path);
            void Check(int offset, int length)
            {
                if (offset < 0 || length < 0 || offset > data.Length - length)
                    throw new InvalidDataException("LVB 环境字段超出文件范围。");
            }
            int Int(int offset) { Check(offset, 4); return BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4)); }
            string Text(int offset)
            {
                Check(offset, 1);
                var end = Array.IndexOf(data, (byte)0, offset, Math.Min(1024, data.Length - offset));
                if (end < 0) throw new InvalidDataException("LVB 环境路径未终止。");
                return Encoding.UTF8.GetString(data, offset, end - offset);
            }
            Check(0, 0x54);
            if (!data.AsSpan(0, 4).SequenceEqual("LVB1"u8) || Int(4) != data.Length || Int(8) != 1
                || !data.AsSpan(12, 4).SequenceEqual("SCN1"u8) || Int(16) != 0x48)
                throw new NotSupportedException("尚未验证此 LVB 场景版本。");
            // SCN 偏移以 magic 和 size 后为基准。国服样本的天气表为 General 相对偏移、32 个 ushort。
            var general = checked(20 + Int(28));
            Check(general, 0x5C);
            var spaces = checked(general + Int(general + 8));
            var count = Int(general + 12);
            if (count is < 0 or > 8) throw new InvalidDataException("LVB 环境空间数量无效。");
            Check(spaces, checked(count * 24));
            var paths = new List<string>();
            for (var i = 0; i < count; i++)
            {
                var entry = spaces + i * 24;
                var offset = Int(entry);
                if (offset == 0) continue;
                var resource = Text(checked(entry + offset));
                if (!resource.StartsWith("bg", StringComparison.Ordinal) || !resource.EndsWith(".envb", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("LVB 环境资源路径无效。");
                if (!paths.Contains(resource)) paths.Add(resource);
            }
            var weatherOffset = Int(general + 64);
            if (weatherOffset == 0) return new([], paths);
            var weatherStart = checked(general + weatherOffset);
            Check(weatherStart, 64);
            var ids = Enumerable.Range(0, 32).Select(i => BinaryPrimitives.ReadUInt16LittleEndian(
                data.AsSpan(weatherStart + i * 2, 2))).Where(id => id != 0).Distinct().ToArray();
            ExcelSheet<RawRow>? sheet = null;
            if (SheetNames.Contains("Weather"))
            {
                var (header, language) = ReadSheetHeader("Weather");
                if (header.Header.Variant == ExcelVariant.Default && header.ColumnDefinitions.Length == 7
                    && header.ColumnDefinitions[0].Type == ExcelColumnDataType.Int32
                    && header.ColumnDefinitions[0].Offset == 24
                    && header.ColumnDefinitions.Skip(1).Select((column, i) =>
                        column.Type == ExcelColumnDataType.String && column.Offset == i * 4).All(valid => valid))
                    sheet = game.Excel.GetSheet<RawRow>(language, "Weather");
            }
            var weathers = ids.Select(id =>
            {
                if (sheet == null || !sheet.TryGetRow((uint)(id & 0x7FFF), out var row))
                    return new GameTerritoryWeather(id, "");
                var name = ((ReadOnlySeString)row.ReadColumn(1)).ExtractText();
                var icon = row.ReadColumn(0) is int value && value > 0 ? (uint)value : 0;
                return new GameTerritoryWeather(id, name, icon);
            }).ToArray();
            return new(weathers, paths);
        }
    }
}
