using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace FFXIVPatchParser.Desktop;

internal sealed record MusicTrackText(string Title, string Subtitle, string SearchText);

internal static class MusicMetadata
{
    internal static string? DirectoryLabel(string path, MusicTrackText? text)
    {
        if (text is null || text.Title.Equals(path, StringComparison.OrdinalIgnoreCase)) return null;
        var nameStart = path.LastIndexOf('/') + 1;
        if (path.StartsWith("sqpack:", StringComparison.OrdinalIgnoreCase))
        {
            var addressStart = path.LastIndexOf('@');
            if (addressStart >= nameStart) nameStart = addressStart;
        }
        return $"{text.Title} · {path[nameStart..]}";
    }

    private sealed record Track(uint Id, string[] Chinese, string[] Japanese, string[] English, string[] Locations);
    private static readonly Lazy<Dictionary<uint, Track>> Tracks = new(() =>
    {
        using var stream = typeof(MusicMetadata).Assembly.GetManifestResourceStream("FFXIVPatchParser.Desktop.Data.orchestrion.json");
        return stream is null ? [] : (JsonSerializer.Deserialize<Track[]>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? []).ToDictionary(x => x.Id);
    });
    private static readonly ConditionalWeakTable<LocalGameSource, Dictionary<string, MusicTrackText>> Catalogs = new();

    internal static MusicTrackText DescribeDetailed(LocalGameSource source, string path)
    {
        return DescribePath(ReadCatalog(source), path);
    }

    internal static MusicTrackText DescribePath(IReadOnlyDictionary<string, MusicTrackText> catalog, string path)
    {
        if (catalog.TryGetValue(path, out var text)) return text;
        if (path.StartsWith("sqpack:", StringComparison.OrdinalIgnoreCase))
        {
            // 合成地址须匹配 BGM 路径的索引分区与哈希。
            var matches = catalog.Where(pair => LocalGameSource.BgmPathMatches(pair.Key, path)).Take(2).ToArray();
            if (matches.Length == 1) return matches[0].Value;
        }
        return new(path, "", "");
    }

    internal static IReadOnlyDictionary<string, MusicTrackText> ReadCatalog(LocalGameSource source) =>
        Catalogs.GetValue(source, BuildCatalog);

    internal static IReadOnlyDictionary<string, MusicTrackText> ReadCatalogOrEmpty(LocalGameSource source)
    {
        try { return ReadCatalog(source); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or InvalidOperationException
            or KeyNotFoundException or JsonException) { return new Dictionary<string, MusicTrackText>(); }
    }

    private static Dictionary<string, MusicTrackText> BuildCatalog(LocalGameSource game)
    {
        var catalog = game.ListBgmPaths()
            .GroupBy(row => row.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, DescribeRows, StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<(uint Id, string Path, string Title)> rows;
        try { rows = game.ListOrchestrionTitles(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or KeyNotFoundException)
        { return catalog; }

        foreach (var group in rows.GroupBy(row => row.Path, StringComparer.OrdinalIgnoreCase))
        {
            var titles = group.Select(row => row.Title).Where(IsTitle).Distinct(StringComparer.Ordinal).Take(2).ToArray();
            if (titles.Length != 1) continue;
            var previous = catalog.GetValueOrDefault(group.Key);
            catalog[group.Key] = new(titles[0], previous?.Subtitle ?? "",
                string.Join(" · ", group.Select(row => $"Orchestrion {row.Id}").Append(previous?.SearchText ?? "")));
        }
        return catalog;
    }

    private static MusicTrackText DescribeRows(IGrouping<string, (uint Id, string Path)> rows)
    {
        var tracks = rows.Select(row => Tracks.Value.GetValueOrDefault(row.Id)).OfType<Track>().ToArray();
        var titles = tracks.SelectMany(track => track.Chinese)
            .Concat(tracks.SelectMany(track => track.Japanese))
            .Concat(tracks.SelectMany(track => track.English))
            .Where(IsTitle).Distinct().ToArray();
        var locations = tracks.SelectMany(track => track.Locations).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToArray();
        var ids = rows.Select(row => $"BGM {row.Id}").Distinct().ToArray();
        var title = titles.FirstOrDefault() ?? rows.Key;
        var subtitle = string.Join(" · ", locations.Length > 0 ? locations : ids);
        return new(title, subtitle, string.Join(" · ", titles.Concat(locations).Concat(ids)));
    }

    private static bool IsTitle(string value) => !string.IsNullOrWhiteSpace(value) &&
        !value.Trim().All(c => c is '?' or '？') &&
        !value.Equals("test", StringComparison.OrdinalIgnoreCase);
}
