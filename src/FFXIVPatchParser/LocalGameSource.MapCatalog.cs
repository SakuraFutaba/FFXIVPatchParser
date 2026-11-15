using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Lumina.Data.Structs.Excel;
using Lumina.Excel;
using Lumina.Text.ReadOnly;

namespace FFXIVPatchParser;

public sealed record NamedMapEntry(uint TerritoryId, uint MapId, string Name, string Zone, string Region,
    string ContentName, uint IntendedUseId, string IntendedUse, string Category, string Bg, string ScenePath,
    string MapCode, string? MapTexturePath, uint PlaceNameId, uint ZonePlaceNameId, uint RegionPlaceNameId,
    bool HasLocalizedName);

public sealed partial class LocalGameSource
{
    private IReadOnlyList<NamedMapEntry>? namedMaps;

    // 本地国服 2026.11.03.0000.0000 的完整列布局，字段语义与 InfSein 的 9e2c05c 国服定义对照。
    private static readonly IReadOnlyDictionary<string, string> MapCatalogSchemas = new Dictionary<string, string>
    {
        ["TerritoryType"] = "95A17071A1D163059188FE3A93532BB7E202B2F982EA327CD41C1AA706F47C4C",
        ["Map"] = "4F594AB55D850A1DB2C51BC5CD22E4057F9C06F8292F748A94421E25BF340E31",
        ["PlaceName"] = "9077AAD8CDDA511D426E2882442FE88181E63E95913DC8F10E5A3319C3C0569D",
        ["ContentFinderCondition"] = "B4B8973D4495616E829A32C58693CBE88B658013E062C679AD17A0220EC5603A",
        ["QuestBattle"] = "714597950F9896D183AD4957CD32AEC57869F67A90215A31594C0153F121DFAA",
        ["Quest"] = "ABFDCFAE6766DD683E9069A912FF017C8F618234BC3096998A7FD17E5F7C79B9"
    };

    /// <summary>读取本地地图名称并核对场景索引。选中地图后才读取布局与模型。</summary>
    public Task<IReadOnlyList<NamedMapEntry>> ListNamedMapsAsync(CancellationToken token = default) =>
        Task.Run(() => ListNamedMaps(token), token);

    private IReadOnlyList<NamedMapEntry> ListNamedMaps(CancellationToken token)
        {
            lock (gate)
            {
                token.ThrowIfCancellationRequested();
                if (namedMaps is not null) return namedMaps;

                ExcelSheet<RawRow> Read(string sheet)
                {
                    token.ThrowIfCancellationRequested();
                    var (header, language) = ReadSheetHeader(sheet);
                    var signature = string.Join(";", header.ColumnDefinitions.Select((c, i) => FormattableString.Invariant($"{i}:{c.Type}:{c.Offset}")));
                    if (header.Header.Variant != ExcelVariant.Default ||
                        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature))) != MapCatalogSchemas[sheet])
                        throw new NotSupportedException($"{sheet} 的本地 EXH 与已验证的地图目录定义不一致。");
                    return game.Excel.GetSheet<RawRow>(language, sheet);
                }

                ExcelSheet<RawRow>? ReadOptional(string sheet)
                {
                    if (!SheetNames.Contains(sheet)) return null;
                    try { return Read(sheet); }
                    catch (Exception ex) when (ex is NotSupportedException or FileNotFoundException)
                    {
                        // 附加表缺失或列布局变化时，仍可从 TerritoryType 查找场景。
                        return null;
                    }
                }

                var territories = Read("TerritoryType");
                var maps = ReadOptional("Map");
                var places = ReadOptional("PlaceName");
                var duties = ReadOptional("ContentFinderCondition");
                var questBattles = new Lazy<ExcelSheet<RawRow>?>(() => ReadOptional("QuestBattle"));
                var quests = new Lazy<ExcelSheet<RawRow>?>(() => ReadOptional("Quest"));
                var result = new List<NamedMapEntry>(territories.Count);

                // Hyperborea 通过 ECommons TerritorySelector 分别显示地点、区域、地域和副本名。
                foreach (var territory in territories)
                {
                    token.ThrowIfCancellationRequested();
                    var bg = MapCatalogText(territory, 1);
                    if (!IsMapCatalogPath(bg)) continue;
                    var scene = $"bg/{bg}.lvb";
                    if (!game.FileExists(scene)) continue;

                    var mapId = MapCatalogId(territory, 6);
                    RawRow map = default;
                    var hasMap = mapId != 0 && maps?.TryGetRow(mapId, out map) == true;
                    var mapCode = hasMap ? MapCatalogText(map, 6) : "";
                    var placeId = MapCatalogId(territory, 5);
                    var zoneId = MapCatalogId(territory, 4);
                    var regionId = MapCatalogId(territory, 3);
                    var place = MapCatalogName(places, placeId, 0);
                    var zone = MapCatalogName(places, zoneId, 0);
                    var region = MapCatalogName(places, regionId, 0);
                    if (region.Length == 0 && hasMap)
                    {
                        var mapRegionId = MapCatalogId(map, 10);
                        region = MapCatalogName(places, mapRegionId, 0);
                        if (region.Length > 0) regionId = mapRegionId;
                    }
                    var content = MapCatalogName(duties, MapCatalogId(territory, 10), 43);
                    var questBattleId = MapCatalogId(territory, 23);
                    if (content.Length == 0 && questBattleId != 0 &&
                        questBattles.Value is { } battles && battles.TryGetRow(questBattleId, out var battle))
                        content = MapCatalogName(quests.Value, MapCatalogId(battle, 0), 0);

                    var mapName = hasMap ? MapCatalogName(places, MapCatalogId(map, 11), 0) : "";
                    var localizedName = FirstMapName(place, content, mapName, zone);
                    var name = localizedName ?? MapCatalogText(territory, 0);
                    if (name.Length == 0) name = $"TerritoryType #{territory.RowId}";
                    var useId = MapCatalogId(territory, 9);
                    var (use, category) = MapCatalogUse(useId);
                    result.Add(new(territory.RowId, mapId, name, zone, region, content, useId, use, category, bg, scene,
                        mapCode, FindMapTexture(mapCode), placeId, zoneId, regionId, localizedName is not null));
                }

                namedMaps = Array.AsReadOnly(result.OrderBy(entry => entry.TerritoryId).ToArray());
                return namedMaps;
            }
        }

    private string? FindMapTexture(string mapCode)
    {
        if (!IsMapCatalogPath(mapCode) || mapCode == "default/00") return null;
        var fileName = mapCode.Replace("/", "", StringComparison.Ordinal);
        var root = $"ui/map/{mapCode}/{fileName}";
        var medium = root + "_m.tex";
        if (game.FileExists(medium)) return medium;
        var small = root + "_s.tex";
        return game.FileExists(small) ? small : null;
    }

    private static string? FirstMapName(params string[] names) => names.FirstOrDefault(name => name.Length > 0);
    private static string MapCatalogName(ExcelSheet<RawRow>? sheet, uint id, int column) =>
        id != 0 && sheet is not null && sheet.TryGetRow(id, out var row) ? MapCatalogText(row, column) : "";
    private static string MapCatalogText(RawRow row, int column) =>
        ((ReadOnlySeString)row.ReadColumn(column)).ExtractText().Trim();
    private static uint MapCatalogId(RawRow row, int column) => Convert.ToUInt32(row.ReadColumn(column), CultureInfo.InvariantCulture);

    private static bool IsMapCatalogPath(string path) =>
        path.Contains('/') && path[0] != '/' && path[^1] != '/' && !path.Contains("//", StringComparison.Ordinal) &&
        path.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '/');

    // 用途编号及分组参考 Hyperborea 固定版本的 ECommons TerritoryIntendedUseEnum。
    private static (string Use, string Category) MapCatalogUse(uint id) => id switch
    {
        0 => ("城镇", "野外与城镇"),
        1 => ("野外", "野外与城镇"),
        2 => ("旅馆", "旅馆"),
        3 => ("迷宫", "迷宫"),
        4 => ("多变迷宫", "迷宫"),
        5 => ("监狱", "其他"),
        6 => ("初始区域", "其他"),
        7 or 12 or 15 or 54 => ("任务区域", "其他"),
        8 => ("团队任务", "团队与大型任务"),
        9 or 29 => ("任务战斗", "其他"),
        10 => ("讨伐歼灭战", "讨伐歼灭战"),
        13 => ("住宅区", "住宅"),
        14 => ("住宅室内", "住宅"),
        16 or 17 => ("大型任务", "团队与大型任务"),
        18 => ("纷争前线", "其他"),
        20 => ("陆行鸟广场", "其他"),
        21 => ("重建庆典", "其他"),
        23 => ("金碟游乐场", "其他"),
        25 => ("萌宠之王", "其他"),
        26 or 38 or 47 => ("云冠群岛", "其他"),
        27 => ("新人训练", "其他"),
        28 or 37 => ("水晶冲突", "其他"),
        30 => ("兵营", "其他"),
        31 => ("深层迷宫", "深层迷宫"),
        32 or 43 => ("季节活动", "其他"),
        33 => ("寻宝副本", "其他"),
        34 => ("季节活动副本", "其他"),
        35 or 50 or 51 => ("对战大厅", "其他"),
        39 => ("争夺战", "其他"),
        41 => ("禁地优雷卡", "其他"),
        44 => ("跳跳乐", "其他"),
        45 => ("假面狂欢", "其他"),
        46 => ("出海垂钓", "其他"),
        48 => ("博兹雅", "其他"),
        49 => ("无人岛", "其他"),
        52 or 53 => ("大规模团队任务", "团队与大型任务"),
        56 => ("友好部族副本", "其他"),
        57 => ("异闻迷宫", "迷宫"),
        58 => ("零式异闻迷宫", "迷宫"),
        59 => ("糖豆人联动", "其他"),
        61 => ("蜃景幻界新月岛", "其他"),
        _ => ($"用途 #{id}", "其他")
    };
}
