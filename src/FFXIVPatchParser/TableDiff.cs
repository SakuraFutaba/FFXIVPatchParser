using System.Globalization;

namespace FFXIVPatchParser;

public sealed record SheetColumn(string Id, string Name, string Type, int? Offset = null);
public sealed record SheetCell(string Display, string Canonical);
public readonly record struct RowKey(uint RowId, ushort? SubrowId) : IComparable<RowKey>
{
    public override string ToString() => SubrowId.HasValue ? $"{RowId}.{SubrowId}" : RowId.ToString(CultureInfo.InvariantCulture);
    public int CompareTo(RowKey other) => RowId != other.RowId ? RowId.CompareTo(other.RowId) : Nullable.Compare(SubrowId, other.SubrowId);
    public static RowKey Parse(string value)
    {
        var parts = value.Split('.');
        if (parts.Length > 2) throw new InvalidDataException($"无效行标识：{value}");
        return new(uint.Parse(parts[0], CultureInfo.InvariantCulture), parts.Length == 2 ? ushort.Parse(parts[1], CultureInfo.InvariantCulture) : null);
    }
}
public sealed record SheetData(IReadOnlyList<SheetColumn> Columns, IReadOnlyDictionary<RowKey, SheetCell[]> Rows);
public sealed record ComparedColumn(string Id, SheetColumn? Before, SheetColumn? After)
{
    public bool SchemaChanged => Before != After;
    public string Header => Before?.Name == After?.Name ? (After?.Name ?? Before?.Name ?? Id) : $"{Before?.Name ?? "∅"} → {After?.Name ?? "∅"}";
    public string Detail => $"列 {Id} · {Before?.Type ?? "无"} → {After?.Type ?? "无"}" + (Before?.Offset != null || After?.Offset != null ? $" · 偏移 {Before?.Offset} → {After?.Offset}" : "");
}
public sealed record ChangedRow(RowKey Key, string Kind, SheetCell?[]? Before, SheetCell?[]? After, bool[] Changed);
public sealed record DiffResult(IReadOnlyList<ComparedColumn> Columns, IReadOnlyList<ChangedRow> Rows, int UnchangedRows)
{
    public int Added => Rows.Count(r => r.Kind == "added");
    public int Removed => Rows.Count(r => r.Kind == "removed");
    public int Modified => Rows.Count(r => r.Kind == "modified");
}
public static class TableDiff
{
    public static DiffResult Compare(SheetData? before, SheetData? after, CancellationToken token = default)
    {
        var oldColumns = before?.Columns ?? [];
        var newColumns = after?.Columns ?? [];
        if (oldColumns.Select(c => c.Id).Distinct().Count() != oldColumns.Count || newColumns.Select(c => c.Id).Distinct().Count() != newColumns.Count)
            throw new InvalidDataException("表中包含重复列标识。");
        var ids = oldColumns.Select(c => c.Id).Union(newColumns.Select(c => c.Id)).OrderBy(id => int.TryParse(id, out var n) ? n : int.MaxValue).ThenBy(id => id).ToArray();
        var columns = ids.Select(id => new ComparedColumn(id, oldColumns.FirstOrDefault(c => c.Id == id), newColumns.FirstOrDefault(c => c.Id == id))).ToArray();
        var oldPositions = ids.Select(id => Find(oldColumns, id)).ToArray();
        var newPositions = ids.Select(id => Find(newColumns, id)).ToArray();
        var oldRows = before?.Rows ?? new Dictionary<RowKey, SheetCell[]>();
        var newRows = after?.Rows ?? new Dictionary<RowKey, SheetCell[]>();
        var changes = new List<ChangedRow>();
        var unchanged = 0;
        foreach (var key in oldRows.Keys.Union(newRows.Keys).Order())
        {
            token.ThrowIfCancellationRequested();
            oldRows.TryGetValue(key, out var oldRow);
            newRows.TryGetValue(key, out var newRow);
            var left = Align(oldRow, oldPositions);
            var right = Align(newRow, newPositions);
            var changed = columns.Select((col, i) => oldRow == null || newRow == null || col.SchemaChanged || left![i]?.Canonical != right![i]?.Canonical).ToArray();
            if (oldRow != null && newRow != null && !changed.Any(c => c)) { unchanged++; continue; }
            changes.Add(new ChangedRow(key, oldRow == null ? "added" : newRow == null ? "removed" : "modified", left, right, changed));
        }
        return new DiffResult(columns, changes, unchanged);
    }
    private static int Find(IReadOnlyList<SheetColumn> columns, string id)
    {
        for (var i = 0; i < columns.Count; i++) if (columns[i].Id == id) return i;
        return -1;
    }
    private static SheetCell?[]? Align(SheetCell[]? row, int[] positions) =>
        row == null ? null : positions.Select(p => p < 0 ? null : row[p]).ToArray();
}
