using System.Globalization;
using Lumina.Data.Structs.Excel;
using Lumina.Excel;

namespace FFXIVPatchParser;

public interface IRelationSchemaSource
{
    Task<SheetData?> ReadSemanticSchema(string sheet, CancellationToken token);
}

/// <summary>Returns at most two matching rows so ambiguous subrows remain detectable.</summary>
public interface IRelationRowSource
{
    Task<SheetData?> ReadRelationRow(string sheet, uint rowId, CancellationToken token);
}

internal interface IModelRelationNames
{
    Task<string?> ReadModelNames(uint modelId, string sourceSheet, uint sourceRowId, CancellationToken token);
}

public sealed partial class LocalGameSource
{
    public ITableSource AsTableSource() => new LocalTableSource(this);
    public string? GameVersion
    {
        get
        {
            var file = Path.Combine(Path.GetDirectoryName(SqPackPath)!, "ffxivgame.ver");
            return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
        }
    }
    private sealed class LocalTableSource(LocalGameSource owner) : ITableSource, IRelationSchemaSource, IRelationRowSource, IModelRelationNames
    {
        private DataminingSource? semantic;
        private ILookup<uint, NamedModelEntry>? modelNames;
        public string Label => "Local · " + owner.GameVersion + " · " + owner.SqPackPath;
        public async Task<SheetData?> ReadSemanticSchema(string sheet, CancellationToken token)
        {
            if (owner.GameVersion is not { } version) return null;
            semantic ??= await DataminingSource.ForBuild(version, token);
            return await semantic.ReadSchema(sheet, token);
        }
        public async Task<string?> ReadModelNames(uint modelId, string sourceSheet, uint sourceRowId, CancellationToken token)
        {
            if (modelNames == null)
            {
                IReadOnlyList<NamedModelEntry> entries;
                try { entries = await owner.ListNamedModelsAsync(token); }
                catch (Exception ex) when (ex is NotSupportedException or FileNotFoundException) { entries = []; }
                modelNames = entries.Where(entry => entry.Name != $"{entry.SourceSheet} #{entry.SourceRowId}")
                    .ToLookup(entry => entry.ModelCharaId);
            }
            var entriesForModel = modelNames[modelId];
            var nameForSource = entriesForModel.FirstOrDefault(entry => entry.SourceSheet == sourceSheet && entry.SourceRowId == sourceRowId)?.Name;
            if (nameForSource != null) return nameForSource;
            var names = entriesForModel.Select(entry => entry.Name).Distinct(StringComparer.Ordinal).Take(4).ToArray();
            if (names.Length == 0) return null;
            return names.Length == 4 ? string.Join("、", names[..3]) + "等" : string.Join("、", names);
        }
        public Task<SheetData?> ReadRelationRow(string name, uint rowId, CancellationToken token) => Task.Run<SheetData?>(() =>
        {
            if (!Sheets.ContainsKey(name)) return null;
            lock (owner.gate)
            {
                token.ThrowIfCancellationRequested();
                var (header, language) = owner.ReadSheetHeader(name);
                var columns = header.ColumnDefinitions.Select((c, i) => new SheetColumn(i.ToString(CultureInfo.InvariantCulture), "Column" + i, c.Type.ToString(), c.Offset)).ToArray();
                var rows = new Dictionary<RowKey, SheetCell[]>();
                void Add(uint id, ushort? subId, Func<int, object> read)
                {
                    token.ThrowIfCancellationRequested();
                    rows.Add(new(id, subId), Enumerable.Range(0, columns.Length).Select(i => Cell(read(i))).ToArray());
                }
                if (header.Header.Variant == ExcelVariant.Default)
                {
                    if (owner.game.Excel.GetSheet<RawRow>(language, name).TryGetRow(rowId, out var row)) Add(row.RowId, null, row.ReadColumn);
                }
                else if (header.Header.Variant == ExcelVariant.Subrows)
                {
                    if (owner.game.Excel.GetSubrowSheet<RawSubrow>(language, name).TryGetRow(rowId, out var group))
                        foreach (var row in group)
                        {
                            Add(row.RowId, row.ExcelPage.ReadUInt16(row.RowOffset - 2), row.ReadColumn);
                            if (rows.Count == 2) break;
                        }
                }
                else throw new NotSupportedException("不支持的表类型。");
                return new SheetData(columns, rows);
            }
        }, token);
        public string Format => "LocalRaw";
        public IReadOnlyDictionary<string, SheetEntry> Sheets { get; } = owner.SheetNames.ToDictionary(n => n, n => new SheetEntry(n, "local"));
        public Task<SheetData?> Read(string name, CancellationToken token) => Task.Run<SheetData?>(() =>
        {
            if (!Sheets.ContainsKey(name)) return null;
            var rows = new Dictionary<RowKey, SheetCell[]>();
            // Scan once. Repeated page requests would re-enumerate all preceding rows.
            var page = owner.ReadSheetPage(name, 0, 1, (key, cells) =>
            {
                rows.Add(key, cells);
                return false;
            }, token);
            return new SheetData(page.Columns, rows);
        }, token);
    }
}
