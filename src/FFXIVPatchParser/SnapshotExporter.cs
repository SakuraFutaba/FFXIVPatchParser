using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using Lumina;
using Lumina.Data;
using Lumina.Data.Files.Excel;
using Lumina.Data.Structs.Excel;
using Lumina.Excel;

namespace FFXIVPatchParser;

internal static class SnapshotExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string ProjectRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "global.json")) && Directory.Exists(Path.Combine(dir.FullName, "src")))
                return dir.FullName;
        // A portable release can run outside the source checkout.
        return AppContext.BaseDirectory;
    }

    public static string Under(string root, string relative)
    {
        var full = Path.GetFullPath(relative, root);
        if (!full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Path escapes output root: {relative}");
        return full;
    }

    public static int Run(string gamePath, string outputPath, string[]? selected)
    {
        var project = ProjectRoot();
        var output = Under(Path.Combine(project, "tmp"), Path.GetFullPath(outputPath, project));
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new IOException("Snapshot output must be empty; choose a new directory.");
        var input = Path.GetFullPath(gamePath);
        var sqpack = Directory.Exists(Path.Combine(input, "game", "sqpack")) ? Path.Combine(input, "game", "sqpack")
            : Directory.Exists(Path.Combine(input, "sqpack")) ? Path.Combine(input, "sqpack") : input;
        if (!Directory.Exists(Path.Combine(sqpack, "ffxiv"))) throw new DirectoryNotFoundException("Expected a SqPack directory.");
        var versionPath = Path.Combine(Directory.GetParent(sqpack)!.FullName, "ffxivgame.ver");
        var version = File.Exists(versionPath) ? File.ReadAllText(versionPath).Trim() : null;
        var before = Fingerprints(sqpack);
        Directory.CreateDirectory(output);
        using var game = new GameData(sqpack, new LuminaOptions { DefaultExcelLanguage = Language.ChineseSimplified, CacheFileResources = false });
        var sheetNames = game.Excel.SheetNames.Order(StringComparer.Ordinal).ToArray();
        if (selected != null)
        {
            var missing = selected.Except(sheetNames, StringComparer.Ordinal).ToArray();
            if (missing.Length != 0) throw new ArgumentException("Unknown sheets: " + string.Join(", ", missing));
            sheetNames = sheetNames.Where(selected.Contains).ToArray();
        }
        var results = new List<SheetResult>();
        long recordCount = 0;
        var started = DateTimeOffset.UtcNow;
        foreach (var name in sheetNames)
        {
            var result = new SheetResult { Name = name };
            results.Add(result);
            try
            {
                var header = game.GetFile<ExcelHeaderFile>($"exd/{name}.exh") ?? throw new FileNotFoundException($"Missing EXH for {name}");
                var language = header.Languages.Contains(Language.ChineseSimplified) ? Language.ChineseSimplified :
                    header.Languages.Contains(Language.None) ? Language.None : (Language?)null;
                result.Columns = header.ColumnDefinitions.Length;
                result.Variant = header.Header.Variant.ToString();
                if (language == null)
                {
                    result.Status = "skipped-language";
                    result.Error = "No CHS or language-neutral data.";
                    continue;
                }
                result.Language = language.Value.ToString();
                // Lumina can silently omit missing pages: check every declared page explicitly.
                var sources = new List<object> { new { path = $"exd/{name}.exh", bytes = header.Data.Length, sha256 = Hash(header.Data) } };
                foreach (var page in header.DataPages)
                {
                    var path = $"exd/{name}_{page.StartId}" + (language == Language.None ? "" : "_chs") + ".exd";
                    var data = game.GetFile(path) ?? throw new FileNotFoundException($"Missing declared EXD page: {path}");
                    sources.Add(new { path, bytes = data.Data.Length, sha256 = Hash(data.Data) });
                }
                var schemaPath = Under(output, $"sheets/{name}.schema.json");
                var rowsPath = Under(output, $"sheets/{name}.rows.jsonl.gz");
                Directory.CreateDirectory(Path.GetDirectoryName(schemaPath)!);
                var columnDefs = header.ColumnDefinitions.Select((c, i) => new { index = i, type = c.Type.ToString(), typeId = (int)c.Type, offset = c.Offset }).ToArray();
                var schema = new
                {
                    formatVersion = 1, name, language = result.Language, variant = result.Variant,
                    fixedDataSize = header.Header.DataOffset, declaredRowCount = header.Header.RowCount,
                    columnsHash = header.GetColumnsHashString(), columns = columnDefs, sourceFiles = sources,
                    stringEncoding = "SeString: raw base64 is authoritative; text is display-only",
                    integer64Encoding = "decimal string", float32Encoding = "value plus exact IEEE754 bits"
                };
                SaveJson(schemaPath, schema);
                using (var file = File.Create(rowsPath + ".partial"))
                using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
                {
                    if (header.Header.Variant == ExcelVariant.Default)
                    {
                        var sheet = game.Excel.GetSheet<RawRow>(language, name);
                        result.RowCount = sheet.Count;
                        foreach (var row in sheet)
                        {
                            SnapshotValue.WriteRow(gzip, row.RowId, null, result.Columns, row.ReadColumn);
                            result.RecordCount++;
                        }
                    }
                    else if (header.Header.Variant == ExcelVariant.Subrows)
                    {
                        var sheet = game.Excel.GetSubrowSheet<RawSubrow>(language, name);
                        result.RowCount = sheet.Count;
                        foreach (var group in sheet)
                            foreach (var row in group)
                            {
                                // Preserve the stored subrow ID, not merely its enumeration index.
                                var storedId = row.ExcelPage.ReadUInt16(row.RowOffset - 2);
                                SnapshotValue.WriteRow(gzip, row.RowId, storedId, result.Columns, row.ReadColumn);
                                result.RecordCount++;
                            }
                    }
                    else throw new NotSupportedException($"Unsupported sheet variant {header.Header.Variant}.");
                }
                File.Move(rowsPath + ".partial", rowsPath);
                result.Status = "exported";
                result.RowsFile = Path.GetRelativePath(output, rowsPath).Replace('\\', '/');
                result.SchemaFile = Path.GetRelativePath(output, schemaPath).Replace('\\', '/');
                result.Bytes = new FileInfo(rowsPath).Length;
                using (var stream = File.OpenRead(rowsPath)) result.Sha256 = Convert.ToHexString(SHA256.HashData(stream));
                recordCount += result.RecordCount;
            }
            catch (Exception ex)
            {
                result.Status = "error";
                result.Error = ex.Message.Replace(sqpack, "<sqpack>", StringComparison.OrdinalIgnoreCase).Replace(input, "<game>", StringComparison.OrdinalIgnoreCase);
                Console.Error.WriteLine($"{name}: {ex.Message}");
            }
            if (results.Count % 100 == 0) Console.WriteLine($"Sheets {results.Count}/{sheetNames.Length}; records {recordCount:N0}");
        }
        var after = Fingerprints(sqpack);
        var unchanged = JsonSerializer.Serialize(before) == JsonSerializer.Serialize(after) &&
            (!File.Exists(versionPath) || File.ReadAllText(versionPath).Trim() == version);
        var errors = results.Count(r => r.Status == "error");
        SaveJson(Path.Combine(output, "manifest.json"), new
        {
            formatVersion = 1, kind = "FFXIVRawExcelSnapshot", startedAt = started, finishedAt = DateTimeOffset.UtcNow,
            gameVersion = version, requestedLanguage = "ChineseSimplified", luminaVersion = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(GameData).Assembly)?.InformationalVersion,
            sourceType = "SqPack", inputUnchanged = unchanged, sourceFiles = before,
            discoveredSheetCount = game.Excel.SheetNames.Count, selectedSheetCount = sheetNames.Length,
            exportedSheetCount = results.Count(r => r.Status == "exported"),
            skippedSheetCount = results.Count(r => r.Status == "skipped-language"), errorCount = errors,
            recordCount, complete = unchanged && errors == 0, sheets = results
        });
        Console.WriteLine($"Snapshot: {output}\nExported {results.Count(r => r.Status == "exported")}, skipped {results.Count(r => r.Status == "skipped-language")}, errors {errors}; records {recordCount:N0}; source unchanged: {unchanged}");
        return unchanged && errors == 0 ? 0 : 2;
    }

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
    private static List<SourceFingerprint> Fingerprints(string sqpack) =>
        Directory.EnumerateFiles(sqpack, "0a*.win32.*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(path =>
        {
            using var stream = File.OpenRead(path);
            return new SourceFingerprint(Path.GetRelativePath(sqpack, path).Replace('\\', '/'), stream.Length, Convert.ToHexString(SHA256.HashData(stream)));
        }).ToList();
    private static void SaveJson(string path, object data) => File.WriteAllText(path, JsonSerializer.Serialize(data, JsonOptions));
    private sealed record SourceFingerprint(string Path, long Bytes, string Sha256);
    private sealed class SheetResult
    {
        public string Name { get; set; } = "";
        public string Status { get; set; } = "pending";
        public string? Language { get; set; }
        public string? Variant { get; set; }
        public int Columns { get; set; }
        public int RowCount { get; set; }
        public long RecordCount { get; set; }
        public string? RowsFile { get; set; }
        public string? SchemaFile { get; set; }
        public long Bytes { get; set; }
        public string? Sha256 { get; set; }
        public string? Error { get; set; }
    }
}
