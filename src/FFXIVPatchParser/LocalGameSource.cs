using System.Globalization;
using Lumina;
using Lumina.Data;
using Lumina.Data.Files;
using Lumina.Data.Files.Excel;
using Lumina.Data.Structs.Excel;
using Lumina.Excel;
using Lumina.Text.ReadOnly;

namespace FFXIVPatchParser;

public sealed record LocalSheetPage(IReadOnlyList<SheetColumn> Columns, IReadOnlyDictionary<RowKey, SheetCell[]> Rows, int Offset, bool HasMore, string Language, int NextOffset = 0);
public sealed record GameTexture(string Path, int Width, int Height, byte[] Bgra32);

/// <summary>Read-only, serialized access to the installed SqPack. Cells are decoded only for the requested page.</summary>
public sealed partial class LocalGameSource : IDisposable
{
    private readonly GameData game;
    private readonly object gate = new();
    private readonly HashSet<string> validated = new();
    private IReadOnlyList<string>? icons;
    private IReadOnlyList<SqPackImageLocation>? imageTextures;
    private readonly Dictionary<string, (SqPack Pack, long Offset)> textureAddresses = new();
    public string SqPackPath { get; }
    public IReadOnlyList<string> SheetNames { get; }
    public LocalGameSource(string gamePath)
    {
        var input = Path.GetFullPath(gamePath);
        SqPackPath = Directory.Exists(Path.Combine(input, "game", "sqpack")) ? Path.Combine(input, "game", "sqpack") : Directory.Exists(Path.Combine(input, "sqpack")) ? Path.Combine(input, "sqpack") : input;
        if (!Directory.Exists(Path.Combine(SqPackPath, "ffxiv"))) throw new DirectoryNotFoundException("请选择游戏目录或 sqpack 目录。");
        game = new GameData(SqPackPath, new LuminaOptions { DefaultExcelLanguage = Language.ChineseSimplified, CacheFileResources = false });
        SheetNames = game.Excel.SheetNames.Order(StringComparer.Ordinal).ToArray();
    }
    public LocalSheetPage ReadSheetPage(string name, int offset, int pageSize, CancellationToken token = default)
        => ReadSheetPage(name, offset, pageSize, null, token);
    public LocalSheetPage ReadSheetPage(string name, int offset, int pageSize, Func<RowKey, SheetCell[], bool>? filter, CancellationToken token = default)
    {
        if (offset < 0 || pageSize is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(pageSize));
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            var (header, language) = ReadSheetHeader(name);
            var columns = header.ColumnDefinitions.Select((c, i) => new SheetColumn(i.ToString(CultureInfo.InvariantCulture), "Column" + i, c.Type.ToString(), c.Offset)).ToArray();
            var rows = new Dictionary<RowKey, SheetCell[]>();
            var visited = 0;
            bool Add(uint id, ushort? subId, Func<int, object> read)
            {
                token.ThrowIfCancellationRequested();
                if (visited++ < offset) return true;
                var key = new RowKey(id, subId);
                var cells = Enumerable.Range(0, columns.Length).Select(i => Cell(read(i))).ToArray();
                if (filter != null && !filter(key, cells)) return true;
                if (rows.Count == pageSize) return false;
                rows.Add(key, cells);
                return true;
            }
            var more = false;
            if (header.Header.Variant == ExcelVariant.Default)
            {
                foreach (var row in game.Excel.GetSheet<RawRow>(language, name))
                    if (!Add(row.RowId, null, row.ReadColumn)) { more = true; break; }
            }
            else if (header.Header.Variant == ExcelVariant.Subrows)
            {
                foreach (var group in game.Excel.GetSubrowSheet<RawSubrow>(language, name))
                {
                    foreach (var row in group)
                        if (!Add(row.RowId, row.ExcelPage.ReadUInt16(row.RowOffset - 2), row.ReadColumn)) { more = true; break; }
                    if (more) break;
                }
            }
            else throw new NotSupportedException("不支持的表类型。");
            return new(columns, rows, offset, more, language.ToString(), more ? visited - 1 : visited);
        }
    }
    private (ExcelHeaderFile Header, Language Language) ReadSheetHeader(string name)
    {
        if (!SheetNames.Contains(name)) throw new ArgumentException("未知数据表。", nameof(name));
        var header = game.GetFile<ExcelHeaderFile>($"exd/{name}.exh") ?? throw new FileNotFoundException("缺少 EXH：" + name);
        var language = header.Languages.Contains(Language.ChineseSimplified) ? Language.ChineseSimplified
            : header.Languages.Contains(Language.None) ? Language.None
            : throw new NotSupportedException("此表没有简体中文或无语言数据。");
        if (!validated.Contains(name))
        {
            foreach (var page in header.DataPages)
            {
                var file = $"exd/{name}_{page.StartId}" + (language == Language.None ? "" : "_chs") + ".exd";
                if (!game.FileExists(file)) throw new FileNotFoundException("缺少表数据页：" + file);
            }
            validated.Add(name);
        }
        return (header, language);
    }
    private static SheetCell Cell(object value) => value switch
    {
        ReadOnlySeString text => new(text.ExtractText(), "raw:" + Convert.ToBase64String(text.Data.Span)),
        float number => new(number.ToString("R", CultureInfo.InvariantCulture), "bits:" + BitConverter.SingleToUInt32Bits(number).ToString("X8", CultureInfo.InvariantCulture)),
        _ => new(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "", Convert.ToString(value, CultureInfo.InvariantCulture) ?? "")
    };
    public GameTexture ReadTexture(string path)
    {
        lock (gate)
        {
            var tex = (textureAddresses.TryGetValue(path, out var address) ? address.Pack.ReadFile<TexFile>(address.Offset) : game.GetFile<TexFile>(path)) ?? throw new FileNotFoundException("未找到纹理：" + path);
            if ((long)tex.Header.Width * tex.Header.Height > 32 * 1024 * 1024) throw new NotSupportedException("纹理超过3200万像素预览限制。");
            var pixels = tex.ImageData;
            if (pixels.Length != checked(tex.Header.Width * tex.Header.Height * 4)) throw new InvalidDataException("纹理解码尺寸异常。");
            return new(path, tex.Header.Width, tex.Header.Height, pixels);
        }
    }
    public IReadOnlyList<string> ListIcons(CancellationToken token = default)
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            if (icons != null) return icons;
            var categories = game.Repositories.Values
                .Where(repo => repo.Categories.ContainsKey(6)).SelectMany(repo => repo.Categories[6])
                .ToArray();
            var hashes = categories.SelectMany(category => category.IndexHashTableEntries?.Keys.AsEnumerable() ?? []).ToHashSet();
            // 只有 index2 的安装仍交给 Lumina 查找，避免按缺失的目录哈希误删候选。
            var needsPathProbe = categories.Any(category => category.IndexHashTableEntries == null);
            var folders = hashes.Select(hash => hash >> 32).ToHashSet();
            var result = new List<string>();
            for (var first = 0; first < 300000; first += 1000)
            {
                token.ThrowIfCancellationRequested();
                foreach (var folder in new[] { $"ui/icon/{first:D6}", $"ui/icon/{first:D6}/chs" })
                {
                    // 目录哈希不存在时，整个编号段都无需探测；只读取内存索引，不扫描 dat。
                    if (!needsPathProbe && !folders.Contains(GameData.ParseFilePath(folder + "/000000.tex")!.IndexHash >> 32)) continue;
                    for (var id = first; id < first + 1000; id++)
                    {
                        token.ThrowIfCancellationRequested();
                        AddIfPresent($"{folder}/{id:D6}.tex");
                        AddIfPresent($"{folder}/{id:D6}_hr1.tex");
                    }
                }
            }
            icons = result.Order(StringComparer.Ordinal).ToArray();
            return icons;

            void AddIfPresent(string path)
            {
                if ((needsPathProbe || hashes.Contains(GameData.ParseFilePath(path)!.IndexHash)) && game.FileExists(path)) result.Add(path);
            }
        }
    }
    internal static readonly string[] ImageCategories = ["ui", "vfx", "chara", "bg", "bgcommon", "common", "cut"];

    public IReadOnlyList<string> ListUiTextures(CancellationToken token = default)
    {
        var containers = ListFileSystemContainers(token).Where(container => container.Category == "ui")
            .Select(container => container.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return EnumerateImageTextures(token, containers).Select(texture => texture.Path).ToArray();
    }

    public IReadOnlyList<string> ListImageTextures(CancellationToken token = default) =>
        EnumerateImageTextures(token).Select(texture => texture.Path).ToArray();

    internal IReadOnlyList<SqPackImageLocation> EnumerateImageTextures(CancellationToken token, IReadOnlySet<string>? containers = null,
        Func<string, long, long, bool>? includeRange = null)
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            if (imageTextures != null) return imageTextures.Where(item =>
                (containers is null || containers.Contains(item.Container)) &&
                (includeRange is null || includeRange(item.Container, item.Offset, item.End))).ToArray();
            var result = new List<SqPackImageLocation>();
            foreach (var container in ListFileSystemContainers(token))
            {
                if (!ImageCategories.Contains(container.Category) || (containers is not null && !containers.Contains(container.Id))) continue;
                var entries = EnumerateFileSystemEntries(container.Id, token);
                var pack = fileSystemPacks![container.Id].Pack;
                using var stream = File.OpenRead(pack.FullName);
                using var reader = new BinaryReader(stream);
                var isUi = container.Category == "ui";
                for (var next = 0; next < entries.Count;)
                {
                    token.ThrowIfCancellationRequested();
                    var first = next++;
                    var entry = entries[first];
                    var offset = entry.Offset;
                    while (next < entries.Count && entries[next].Offset == offset) next++;
                    var end = next < entries.Count ? entries[next].Offset : stream.Length;
                    if (includeRange is not null && !includeRange(container.Id, offset, end)) continue;

                    string? name = null;
                    var ambiguous = false;
                    for (var i = first; i < next; i++)
                    {
                        var known = entries[i].KnownPath;
                        if (known is null) continue;
                        if (!IsAtexPath(known) && !(isUi && known.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))) continue;
                        if (name is not null && name != known) ambiguous = true;
                        name = known;
                    }

                    // 标准文件通过已确认的 .atex 路径识别。UI 中的 Texture 条目也支持按地址预览。
                    if (!isUi && name is null) continue;
                    stream.Position = offset + 4;
                    var type = reader.ReadUInt32();
                    if (!IsImageTexture(type, name)) continue;
                    var path = ambiguous ? entry.Locator : name ?? entry.Locator;
                    textureAddresses[path] = (pack, offset);
                    result.Add(new(path, container.Id, offset, end));
                }
            }
            if (containers is null && includeRange is null) imageTextures = result;
            return result;
        }
    }

    private static bool IsAtexPath(string path) => path.EndsWith(".atex", StringComparison.OrdinalIgnoreCase);

    private static bool IsImageTexture(uint type, string? path) => type == 4 || (type == 2 && path is not null && IsAtexPath(path));

    public void Dispose() { lock (gate) game.Dispose(); }
}

internal sealed record SqPackImageLocation(string Path, string Container, long Offset, long End);

