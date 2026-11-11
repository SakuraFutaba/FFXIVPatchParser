namespace FFXIVPatchParser;

public static class CellPresentation
{
    public static bool IsString(SheetColumn? column) =>
        column?.Type.Equals("str", StringComparison.OrdinalIgnoreCase) == true ||
        column?.Type.Equals("string", StringComparison.OrdinalIgnoreCase) == true;

    public static TextDifference Compare(ComparedColumn column, SheetCell? before, SheetCell? after)
    {
        var left = before?.Display ?? "";
        var right = after?.Display ?? "";
        // The schema is authoritative. Never infer a string from its display value.
        if ((column.Before == null || IsString(column.Before)) &&
            (column.After == null || IsString(column.After)) &&
            (IsString(column.Before) || IsString(column.After)))
            return TextDiff.Compare(left, right);
        return new([new(left, false)], [new(right, false)]);
    }

    // Each column filter matches either version; filters across columns combine with AND.
    public static bool Matches(ChangedRow row, IReadOnlyDictionary<int, string> filters) =>
        filters.All(filter => filter.Key == -1
            ? row.Key.ToString().Contains(filter.Value, StringComparison.OrdinalIgnoreCase)
            : Contains(row.Before, filter.Key, filter.Value) || Contains(row.After, filter.Key, filter.Value));
    private static bool Contains(SheetCell?[]? cells, int index, string value) =>
        cells != null && index >= 0 && index < cells.Length &&
        cells[index]?.Display.Contains(value, StringComparison.OrdinalIgnoreCase) == true;
}
