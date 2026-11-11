using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;

namespace FFXIVPatchParser;

public sealed record SheetEntry(string Name, string Fingerprint);
public interface ITableSource
{
    string Label { get; }
    string Format { get; }
    IReadOnlyDictionary<string, SheetEntry> Sheets { get; }
    Task<SheetData?> Read(string name, CancellationToken token);
}
public sealed record SheetChange(string Name, string Kind)
{
    public string State => Kind switch { "added" => "新增", "removed" => "删除", _ => "修改" };
}
public static class SheetComparison
{
    public static IReadOnlyList<SheetChange> List(ITableSource before, ITableSource after)
    {
        if (before.Format != after.Format) throw new InvalidOperationException("请比较同一格式的两份快照；Raw 和 CSV 的语义不同。");
        if (before is RawSnapshotSource left && after is RawSnapshotSource right
            && (!left.IsFullSnapshot || !right.IsFullSnapshot) && !left.Sheets.Keys.ToHashSet().SetEquals(right.Sheets.Keys))
            throw new InvalidOperationException("部分导出的 Raw 快照必须包含相同的表集合，以免把未导出的表误报为新增或删除。");
        return before.Sheets.Keys.Union(after.Sheets.Keys).Order(StringComparer.Ordinal).Select(name =>
        {
            before.Sheets.TryGetValue(name, out var left);
            after.Sheets.TryGetValue(name, out var right);
            return left?.Fingerprint == right?.Fingerprint ? null : new SheetChange(name, left == null ? "added" : right == null ? "removed" : "modified");
        }).OfType<SheetChange>().ToArray();
    }
}
public sealed class RawSnapshotSource : ITableSource
{
    private readonly string root;
    private readonly Dictionary<string, (string Schema, string Rows, string Hash)> files = new();
    public string Label { get; }
    public string Format => "RawSnapshot-v1";
    public bool IsFullSnapshot { get; }
    public IReadOnlyDictionary<string, SheetEntry> Sheets { get; }
    public RawSnapshotSource(string manifestPath)
    {
        root = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var manifest = document.RootElement;
        if (manifest.GetProperty("kind").GetString() != "FFXIVRawExcelSnapshot" || manifest.GetProperty("formatVersion").GetInt32() != 1 || !manifest.GetProperty("complete").GetBoolean())
            throw new InvalidDataException("请选择完整的 Raw Excel 快照 manifest.json（格式 v1）。");
        IsFullSnapshot = manifest.GetProperty("selectedSheetCount").GetInt32() == manifest.GetProperty("discoveredSheetCount").GetInt32();
        Label = "Raw · " + manifest.GetProperty("gameVersion").GetString();
        var sheets = new Dictionary<string, SheetEntry>();
        foreach (var entry in manifest.GetProperty("sheets").EnumerateArray())
        {
            if (entry.GetProperty("status").GetString() != "exported") continue;
            var name = entry.GetProperty("name").GetString()!;
            var schema = Workspace.Within(root, entry.GetProperty("schemaFile").GetString()!);
            var rows = Workspace.Within(root, entry.GetProperty("rowsFile").GetString()!);
            var sha = entry.GetProperty("sha256").GetString()!;
            using var stream = File.OpenRead(schema);
            var fingerprint = sha + ":" + Convert.ToHexString(SHA256.HashData(stream));
            files.Add(name, (schema, rows, sha));
            sheets.Add(name, new SheetEntry(name, fingerprint));
        }
        Sheets = sheets;
    }
    public Task<SheetData?> Read(string name, CancellationToken token) => Task.Run<SheetData?>(() =>
    {
        if (!files.TryGetValue(name, out var item)) return null;
        using (var stream = File.OpenRead(item.Rows))
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(item.Hash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("快照行文件的 SHA256 校验失败。");
        using var schema = JsonDocument.Parse(File.ReadAllText(item.Schema));
        var columns = schema.RootElement.GetProperty("columns").EnumerateArray().Select(c =>
            new SheetColumn(c.GetProperty("index").GetInt32().ToString(), "Column" + c.GetProperty("index").GetInt32(), c.GetProperty("type").GetString()!, c.GetProperty("offset").GetInt32())).ToArray();
        var rows = new Dictionary<RowKey, SheetCell[]>();
        using var source = File.OpenRead(item.Rows);
        using var gzip = new GZipStream(source, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        while (reader.ReadLine() is { } line)
        {
            token.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(line);
            var row = document.RootElement;
            var subrow = row.GetProperty("subrowId");
            var key = new RowKey(row.GetProperty("rowId").GetUInt32(), subrow.ValueKind == JsonValueKind.Null ? null : subrow.GetUInt16());
            var values = row.GetProperty("values").EnumerateArray().Select(value =>
            {
                if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("raw", out var raw))
                    return new SheetCell(value.GetProperty("text").GetString() ?? "", "raw:" + raw.GetString());
                if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("bits", out var bits))
                    return new SheetCell(value.GetProperty("value").ToString(), "bits:" + bits.GetString());
                return new SheetCell(value.ToString(), value.GetRawText());
            }).ToArray();
            if (values.Length != columns.Length || !rows.TryAdd(key, values)) throw new InvalidDataException("快照列数或行标识不合法。");
        }
        return new SheetData(columns, rows);
    }, token);
}
public static class CsvSheetReader
{
    public static SheetData Read(string path, CancellationToken token = default)
    {
        using var parser = new TextFieldParser(path, System.Text.Encoding.UTF8) { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        parser.SetDelimiters(",");
        var ids = parser.ReadFields() ?? throw new InvalidDataException("CSV 缺少列编号。");
        var names = parser.ReadFields() ?? throw new InvalidDataException("CSV 缺少列名。");
        var types = parser.ReadFields() ?? throw new InvalidDataException("CSV 缺少列类型。");
        if (ids.Length != names.Length || ids.Length != types.Length || ids[0].TrimStart('\uFEFF') != "key") throw new InvalidDataException("不是支持的 CHS datamining CSV 格式。");
        var columns = ids.Skip(1).Select((id, i) => new SheetColumn(id, string.IsNullOrEmpty(names[i + 1]) ? "Column" + id : names[i + 1], types[i + 1])).ToArray();
        var rows = new Dictionary<RowKey, SheetCell[]>();
        while (!parser.EndOfData)
        {
            token.ThrowIfCancellationRequested();
            var row = parser.ReadFields();
            if (row == null) continue;
            if (row.Length != ids.Length) throw new InvalidDataException($"CSV 第 {parser.LineNumber} 行列数异常。");
            if (!rows.TryAdd(RowKey.Parse(row[0]), row.Skip(1).Select(v => new SheetCell(v, v)).ToArray()))
                throw new InvalidDataException("CSV 包含重复行标识。");
        }
        return new SheetData(columns, rows);
    }
}
