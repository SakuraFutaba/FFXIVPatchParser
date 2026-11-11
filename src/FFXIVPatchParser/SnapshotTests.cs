using System.Text.Json;
using Lumina.Text.ReadOnly;

namespace FFXIVPatchParser;

internal static class SnapshotTests
{
    public static void Run()
    {
        using var stream = new MemoryStream();
        object[] values = [long.MinValue, ulong.MaxValue, -0.0f, float.NaN, new ReadOnlySeString("中文\n测试"), true, uint.MaxValue];
        SnapshotValue.WriteRow(stream, 42, 7, values.Length, i => values[i]);
        using var document = JsonDocument.Parse(stream.ToArray());
        var root = document.RootElement;
        var cells = root.GetProperty("values");
        Check(root.GetProperty("rowId").GetUInt32() == 42 && root.GetProperty("subrowId").GetUInt16() == 7, "row identity");
        Check(cells[0].GetString() == "-9223372036854775808", "Int64 precision");
        Check(cells[1].GetString() == "18446744073709551615", "UInt64 precision");
        Check(cells[2].GetProperty("bits").GetString() == "80000000", "negative zero");
        Check(cells[3].GetProperty("value").GetString() == "NaN", "non-finite float");
        Check(cells[4].GetProperty("raw").GetBytesFromBase64().SequenceEqual(System.Text.Encoding.UTF8.GetBytes("中文\n测试")), "SeString bytes");
        Check(cells[4].GetProperty("text").GetString() == "中文\n测试", "CHS display");
        using var empty = new MemoryStream();
        SnapshotValue.WriteRow(empty, 0, null, 0, _ => throw new Exception());
        using var emptyDoc = JsonDocument.Parse(empty.ToArray());
        Check(emptyDoc.RootElement.GetProperty("values").GetArrayLength() == 0, "zero-column row");
        try { SnapshotExporter.Under(Path.Combine(SnapshotExporter.ProjectRoot(), "tmp"), "../outside"); throw new Exception("guard failed"); }
        catch (ArgumentException) { }
        Console.WriteLine("PASS: row/subrow IDs, 64-bit precision, float bits, SeString bytes, CHS, empty row, output boundary.");
    }
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception("FAIL: " + description);
    }
}
