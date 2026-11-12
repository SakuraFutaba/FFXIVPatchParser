using Lumina.Data.Structs.Excel;
using Lumina.Excel;

namespace FFXIVPatchParser;

public sealed partial class LocalGameSource
{
    public int? FindSheetRowOffset(string name, RowKey key, CancellationToken token = default)
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            var (header, language) = ReadSheetHeader(name);

            var offset = 0;
            if (header.Header.Variant == ExcelVariant.Default)
            {
                if (key.SubrowId != null) return null;
                foreach (var row in game.Excel.GetSheet<RawRow>(language, name))
                {
                    token.ThrowIfCancellationRequested();
                    if (row.RowId == key.RowId) return offset;
                    offset++;
                }
            }
            else if (header.Header.Variant == ExcelVariant.Subrows)
            {
                if (key.SubrowId == null) return null;
                foreach (var group in game.Excel.GetSubrowSheet<RawSubrow>(language, name))
                {
                    token.ThrowIfCancellationRequested();
                    foreach (var row in group)
                    {
                        token.ThrowIfCancellationRequested();
                        if (row.RowId == key.RowId && row.ExcelPage.ReadUInt16(row.RowOffset - 2) == key.SubrowId)
                            return offset;
                        offset++;
                    }
                }
            }
            else throw new NotSupportedException("不支持的表类型。");
            return null;
        }
    }
}
