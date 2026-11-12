using System.Globalization;
using System.Text.Json;

namespace FFXIVPatchParser;

public sealed record RelatedCell(string Display, string? TargetSheet = null, RowKey? TargetKey = null, string? IconPath = null,
    string Status = "raw", string? Label = null, string? TargetIconPath = null)
{
    public string? Reference => TargetSheet is { } sheet && TargetKey is { } key ? $"{sheet}#{key}" : null;
}
public sealed record RelatedSheetPage(SheetData Data, IReadOnlyDictionary<(RowKey Row, int Column), RelatedCell> Cells);

/// <summary>One resolver belongs to one version. It never consults another game or snapshot.</summary>
public sealed class RelationResolver(SaintCoinachDefinitions definitions, ITableSource source)
{
    private readonly Dictionary<string, SheetData?> cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<uint, RowKey[]>> rowIndexes = new(StringComparer.Ordinal);
    private readonly LinkedList<string> cacheOrder = new();
    private readonly Dictionary<string, SheetDefinition?> effective = new(StringComparer.Ordinal);
    private readonly HashSet<string> verified = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, string> failures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> retryAfter = new(StringComparer.Ordinal);
    private long operationDeadline;
    public string? LastWarning { get; private set; }
    private CancellationTokenSource Budget(CancellationToken token)
    {
        var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        var remaining = Math.Max(1, Math.Min(5000, operationDeadline - Environment.TickCount64));
        budget.CancelAfter(TimeSpan.FromMilliseconds(remaining));
        return budget;
    }
    public string SourceLabel => source.Label;
    private async Task<SheetDefinition?> Definition(string sheet, SheetData raw, CancellationToken token)
    {
        if (effective.TryGetValue(sheet, out var existing)) return existing;
        var imported = definitions.Get(sheet);
        var schemaKey = "schema:" + sheet;
        if (retryAfter.TryGetValue(schemaKey, out var retry) && retry > Environment.TickCount64)
        { LastWarning = failures[schemaKey]; return imported; }
        void SchemaFailed(string message)
        {
            retryAfter[schemaKey] = Environment.TickCount64 + 15000;
            LastWarning = failures[schemaKey] = message + "；15秒后可重试。";
        }
        var semantic = raw;
        if (source is IRelationSchemaSource schemaSource)
        {
            if (operationDeadline <= Environment.TickCount64)
            {
                LastWarning ??= "部分关联未加载：本次读取已达到12秒预算；可再次打开以重试。";
                return imported;
            }
            using var budget = Budget(token);
            try { semantic = await schemaSource.ReadSemanticSchema(sheet, budget.Token) ?? raw; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or InvalidOperationException)
            { SchemaFailed($"表 {sheet} 的版本语义元数据读取失败（{ex.GetType().Name}）：{ex.Message}"); return imported; }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { SchemaFailed($"表 {sheet} 的版本语义元数据读取超时；已启动的 CSV 缓存下载会继续"); return imported; }
            if (!ReferenceEquals(semantic, raw))
            {
                var aligned = AlignSchema(raw.Columns, semantic.Columns);
                if (aligned == null) return effective[sheet] = null;
                semantic = semantic with { Columns = aligned };
                verified.Add(sheet);
            }
        }
        if (ReferenceEquals(semantic, raw) && !raw.Columns.Any(c => c.Name != "Column" + c.Id)) return effective[sheet] = imported;
        // Some sheets use Singular, Text, etc. as the default display field. Obtain
        // that explicit declaration instead of guessing the first string column.
        if (imported == null && !semantic.Columns.Any(c => c.Name == "Name") && operationDeadline > Environment.TickCount64)
        {
            using var budget = Budget(token);
            try { await definitions.DownloadAsync([sheet], budget.Token); imported = definitions.Get(sheet); }
            catch (Exception ex) when (ex is HttpRequestException or IOException) { LastWarning = "部分默认显示列定义未加载：" + ex.Message; }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { LastWarning = "部分默认显示列定义读取超时。"; }
        }
        var fields = new Dictionary<int, FieldDefinition>();
        foreach (var c in semantic.Columns)
        {
            if (!int.TryParse(c.Id, out var index)) continue;
            JsonElement? converter = null;
            if (c.Type == "Image") converter = JsonSerializer.SerializeToElement(new { type = "icon" });
            else if (source.Sheets.ContainsKey(c.Type)) converter = JsonSerializer.SerializeToElement(new { type = "link", target = c.Type });
            else if (imported?.Fields.TryGetValue(index, out var f) == true && f.Name == c.Name) converter = f.Converter;
            fields[index] = new(index, c.Name, converter);
        }
        return effective[sheet] = new(sheet, imported?.DefaultColumn, fields);
    }
    private static IReadOnlyList<SheetColumn>? AlignSchema(IReadOnlyList<SheetColumn> raw, IReadOnlyList<SheetColumn> csv)
    {
        if (raw.Count != csv.Count || raw.Select(c => c.Id).Distinct().Count() != raw.Count) return null;
        var csvById = new Dictionary<string, SheetColumn>(csv.Count, StringComparer.Ordinal);
        foreach (var column in csv)
            if (!csvById.TryAdd(column.Id, column)) return null;
        var columns = new List<SheetColumn>(raw.Count);
        foreach (var column in raw)
        {
            if (!csvById.TryGetValue(column.Id, out var target)) return null;
            if (MatchesType(column.Type, target.Type)) columns.Add(target);
        }
        return columns;
    }

    private static bool MatchesType(string raw, string csv)
    {
        if (csv.Length == 0) return false;
        var type = raw.ToLowerInvariant();
        var declared = csv.ToLowerInvariant();
        if (declared.StartsWith("bit&", StringComparison.Ordinal))
            return type.StartsWith("packedbool", StringComparison.Ordinal) && int.TryParse(type[10..], out var bit) &&
                declared == "bit&" + (1 << bit).ToString("x2", CultureInfo.InvariantCulture);

        declared = declared switch
        {
            "byte" => "uint8", "sbyte" => "int8", "single" or "float" => "float32", _ => declared
        };
        if (type == "string" || declared == "str") return type == "string" && declared == "str";
        if (type == "float32" || declared == "float32") return type == declared;
        if (type.StartsWith("packedbool", StringComparison.Ordinal) || type == "bool") return declared == "bool";
        if (declared == "bool") return false;
        // SaintCoinach 把 Item 等表的 64 位模型字段写成四段数值，类型仍标为 int64。
        if (type is ("int64" or "uint64") && declared is ("int64" or "uint64")) return true;
        return declared is not ("int8" or "uint8" or "int16" or "uint16" or "int32" or "uint32" or "int64" or "uint64") || declared == type;
    }
    private async Task<SheetData?> Read(string name, CancellationToken token, uint? rowId = null)
    {
        var cacheKey = rowId is { } id && source is IRelationRowSource ? name + "#" + id : name;
        if (cache.TryGetValue(cacheKey, out var sheet))
        {
            cacheOrder.Remove(cacheKey); cacheOrder.AddLast(cacheKey);
            return sheet;
        }
        if (!source.Sheets.ContainsKey(name)) return null;
        if (retryAfter.TryGetValue(name, out var retry) && retry > Environment.TickCount64)
        { LastWarning = failures.GetValueOrDefault(name, "关联读取失败：" + name) + "；15秒后可重试。"; return null; }
        if (operationDeadline <= Environment.TickCount64)
        { LastWarning ??= "部分关联未加载：本次读取已达到12秒预算；可再次打开以重试。"; return null; }
        using var budget = Budget(token);
        try { sheet = rowId is { } requested && source is IRelationRowSource rows ? await rows.ReadRelationRow(name, requested, budget.Token) : await source.Read(name, budget.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { retryAfter[name] = Environment.TickCount64 + 15000; LastWarning = failures[name] = $"关联目标 {name} 读取超时（{source.Format}，单次5秒/整页12秒预算）；15秒后可重试。"; return null; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or HttpRequestException or InvalidOperationException)
        { retryAfter[name] = Environment.TickCount64 + 15000; LastWarning = failures[name] = $"关联目标 {name} 读取失败（{source.Format}，{ex.GetType().Name}）：{ex.Message}"; return null; }
        // Bounded cache: large historical comparisons must not retain every sheet indefinitely.
        if (cache.Count >= 32 && cacheOrder.First is { } first)
        {
            cache.Remove(first.Value); rowIndexes.Remove(first.Value); cacheOrder.RemoveFirst();
        }
        cache[cacheKey] = sheet;
        cacheOrder.AddLast(cacheKey);
        return sheet;
    }
    private bool Compatible(string sheet, SheetData raw, SheetDefinition def)
    {
        if (verified.Contains(sheet)) return true;
        if (def.Fields.Keys.Any(index => !raw.Columns.Any(c => c.Id == index.ToString(CultureInfo.InvariantCulture)))) return false;
        if (definitions.IsBound(sheet, source.Label, raw.Columns)) return true;
        // CSV headers are version-specific semantic evidence. Every named field must agree by ID.
        return def.Fields.Values.Where(f => f.Name.Length > 0).All(f => raw.Columns.Any(c => c.Id == f.Index.ToString(CultureInfo.InvariantCulture) && c.Name == f.Name));
    }
    public async Task<RelatedCell> ResolveAsync(string sheet, SheetData raw, RowKey row, int columnPosition, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try { LastWarning = null; operationDeadline = Environment.TickCount64 + 12000; return await Resolve(sheet, raw, row, columnPosition, new HashSet<(string, RowKey, int)>(), token); }
        finally { gate.Release(); }
    }
    private async Task<RelatedCell> Resolve(string sheet, SheetData raw, RowKey key, int position, HashSet<(string, RowKey, int)> visited, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!raw.Rows.TryGetValue(key, out var values) || position < 0 || position >= values.Length || position >= raw.Columns.Count)
            return new("", Status: "invalid-cell");
        var value = values[position].Display;
        if (visited.Count >= 12 || !visited.Add((sheet, key, position))) return new(value, Status: "cycle");
        try
        {
            var def = await Definition(sheet, raw, token);
            if (def == null) return new(value, Status: "missing-definition");
            if (!Compatible(sheet, raw, def)) return new(value, Status: "unverified-schema");
            if (!int.TryParse(raw.Columns[position].Id, out var index) || !def.Fields.TryGetValue(index, out var field) || field.Converter is not { } converter) return new(value);
            var kind = converter.GetProperty("type").GetString();
            if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) return new(value, Status: "non-numeric-reference");
            if (kind == "icon") return id == 0 ? new(value, Status: "empty") : new(value, IconPath: IconPath(id), Status: "icon");
            if (id == 0) return new(value, Status: "empty");
            var candidates = new List<(string Sheet, string? Key, string? Project)>();
            if (kind == "link") candidates.Add((converter.GetProperty("target").GetString()!, null, null));
            else if (kind == "multiref")
                foreach (var target in converter.GetProperty("targets").EnumerateArray()) candidates.Add((target.GetString()!, null, null));
            else if (kind == "complexlink")
            {
                foreach (var link in converter.GetProperty("links").EnumerateArray())
                {
                    if (link.TryGetProperty("when", out var when))
                    {
                        var condition = FindColumn(raw, def, when.GetProperty("key").GetString());
                        if (condition < 0 || !EqualsScalar(values[condition].Display, when.GetProperty("value").ToString())) continue;
                    }
                    var keyColumn = link.TryGetProperty("key", out var k) ? k.GetString() : null;
                    var project = link.TryGetProperty("project", out var p) ? p.GetString() : null;
                    if (link.TryGetProperty("sheet", out var target)) candidates.Add((target.GetString()!, keyColumn, project));
                    else if (link.TryGetProperty("sheets", out var targets))
                        foreach (var targetSheet in targets.EnumerateArray()) candidates.Add((targetSheet.GetString()!, keyColumn, project));
                }
            }
            else return new(value, Status: "unsupported-converter:" + kind);
            foreach (var candidate in candidates)
            {
                var target = await Read(candidate.Sheet, token, candidate.Key == null ? id : null);
                if (target == null) continue;
                var targetDef = await Definition(candidate.Sheet, target, token);
                KeyValuePair<RowKey, SheetCell[]>[] matches;
                if (candidate.Key != null)
                {
                    if (targetDef == null || !Compatible(candidate.Sheet, target, targetDef)) continue;
                    var lookup = FindColumn(target, targetDef, candidate.Key);
                    if (lookup < 0) continue;
                    matches = target.Rows.Where(r => EqualsScalar(r.Value[lookup].Display, value)).Take(2).ToArray();
                }
                else
                {
                    if (source is IRelationRowSource)
                        matches = target.Rows.Where(r => r.Key.RowId == id).Take(2).ToArray();
                    else
                    {
                        if (!rowIndexes.TryGetValue(candidate.Sheet, out var rowIndex))
                            rowIndexes[candidate.Sheet] = rowIndex = target.Rows.Keys.GroupBy(k => k.RowId).ToDictionary(g => g.Key, g => g.Take(2).ToArray());
                        matches = rowIndex.TryGetValue(id, out var keys)
                            ? keys.Select(k => new KeyValuePair<RowKey, SheetCell[]>(k, target.Rows[k])).ToArray() : [];
                    }
                }
                if (matches.Length == 0) continue;
                if (matches.Length > 1) return new(value, Status: "ambiguous-subrow-or-key");
                var matched = matches[0];
                string? label = null;
                string? targetIcon = null;
                if (targetDef != null && Compatible(candidate.Sheet, target, targetDef))
                {
                    var labelPosition = FindColumn(target, targetDef, candidate.Project ?? targetDef.DefaultColumn);
                    if (labelPosition < 0 && candidate.Project == null)
                    {
                        labelPosition = FindColumn(target, targetDef, "Name");
                        if (labelPosition < 0) labelPosition = FindColumn(target, targetDef, "Singular");
                    }
                    if (labelPosition >= 0)
                    {
                        var resolved = await Resolve(candidate.Sheet, target, matched.Key, labelPosition, visited, token);
                        if (resolved.Status == "cycle") return new(value, Status: "cycle");
                        label = resolved.Label ?? (resolved.Status == "raw" ? resolved.Display : resolved.Reference);
                    }
                    targetIcon = FindTargetIcon(target, targetDef, matched.Value);
                }
                if (string.IsNullOrWhiteSpace(label) && candidate.Sheet == "ModelChara" && source is IModelRelationNames names)
                    label = await names.ReadModelNames(matched.Key.RowId, sheet, key.RowId, token);
                var reference = $"{candidate.Sheet}#{matched.Key}";
                return string.IsNullOrWhiteSpace(label)
                    ? new(reference, candidate.Sheet, matched.Key, Status: "linked-no-label", TargetIconPath: targetIcon)
                    : new($"{reference} · {label}", candidate.Sheet, matched.Key, Status: "linked", Label: label, TargetIconPath: targetIcon);
            }
            return new(value, Status: candidates.Count == 0 ? "condition-unmatched" : LastWarning != null ? "temporarily-unavailable" : "missing-target");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return new(value, Status: "invalid-definition");
        }
        finally { visited.Remove((sheet, key, position)); }
    }
    private static string IconPath(uint id) => $"ui/icon/{id / 1000 * 1000:D6}/{id:D6}.tex";

    private static string? FindTargetIcon(SheetData target, SheetDefinition definition, SheetCell[] values)
    {
        var iconFields = definition.Fields.Values.Where(field => field.Converter is { } converter &&
            converter.TryGetProperty("type", out var type) && type.GetString() == "icon").ToArray();
        var field = iconFields.Length == 1 ? iconFields[0] : iconFields.FirstOrDefault(field => field.Name == "Icon");
        if (field == null) return null;
        var position = FindColumn(target, definition, field.Name);
        return position >= 0 && uint.TryParse(values[position].Display, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
            ? IconPath(id) : null;
    }
    private static bool EqualsScalar(string left, string right) => decimal.TryParse(left, NumberStyles.Number, CultureInfo.InvariantCulture, out var a) && decimal.TryParse(right, NumberStyles.Number, CultureInfo.InvariantCulture, out var b) ? a == b : string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static int FindColumn(SheetData sheet, SheetDefinition def, string? name)
    {
        if (name == null) return -1;
        var fields = def.Fields.Values.Where(f => f.Name == name).Take(2).ToArray();
        if (fields.Length != 1) return -1;
        for (var i = 0; i < sheet.Columns.Count; i++) if (sheet.Columns[i].Id == fields[0].Index.ToString(CultureInfo.InvariantCulture)) return i;
        return -1;
    }
    public async Task<SheetData> EnrichAsync(string sheet, SheetData raw, CancellationToken token = default)
        => (await EnrichWithDetailsAsync(sheet, raw, token)).Data;
    public Task<RelatedSheetPage> EnrichWithDetailsAsync(string sheet, SheetData raw, CancellationToken token = default)
        => EnrichWithDetailsAsync(sheet, raw, resolveValues: true, token);
    public async Task<RelatedSheetPage> EnrichWithDetailsAsync(string sheet, SheetData raw, bool resolveValues, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try { LastWarning = null; operationDeadline = Environment.TickCount64 + 12000; return await Enrich(sheet, raw, resolveValues, token); }
        finally { gate.Release(); }
    }
    private async Task<RelatedSheetPage> Enrich(string sheet, SheetData raw, bool resolveValues, CancellationToken token)
    {
        var columns = raw.Columns.ToArray();
        var def = await Definition(sheet, raw, token);
        var positions = new List<int>();
        if (def != null && Compatible(sheet, raw, def))
            for (var i = 0; i < columns.Length; i++)
                if (int.TryParse(columns[i].Id, out var id) && def.Fields.TryGetValue(id, out var field))
                {
                    if (field.Name.Length > 0) columns[i] = columns[i] with { Name = field.Name };
                    if (field.Converter != null) positions.Add(i);
                }
        var details = new Dictionary<(RowKey Row, int Column), RelatedCell>();
        if (!resolveValues || positions.Count == 0) return new(raw with { Columns = columns }, details);
        var rows = new Dictionary<RowKey, SheetCell[]>();
        foreach (var row in raw.Rows)
        {
            var cells = row.Value.ToArray();
            foreach (var i in positions)
            {
                var resolved = await Resolve(sheet, raw, row.Key, i, new HashSet<(string, RowKey, int)>(), token);
                cells[i] = cells[i] with { Display = resolved.Display };
                if (resolved.IconPath != null || resolved.TargetKey != null) details[(row.Key, i)] = resolved;
            }
            rows[row.Key] = cells;
        }
        return new(new(columns, rows), details);
    }
}
