using System.Security.Cryptography;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace FFXIVPatchParser;

public static class NpcNameHints
{
    private const string Revision = "e180a0f124cb45db5bf56b283b41b8920a7e4c2e";
    private const string BlobHash = "744ab8f43e0adf4708a69ec630279ea0705a7328";
    private const string NamesUrl = $"https://raw.githubusercontent.com/imchillin/Anamnesis/{Revision}/Anamnesis/Data/NpcNames.json";
    private static string CacheDirectory => Path.Combine(Workspace.Temp, "npc-names");
    private static string NamesPath => Path.Combine(CacheDirectory, Revision + ".json");
    private static string TranslationsPath => Path.Combine(CacheDirectory, "ffcafe-bnpc-name.json");

    public static IReadOnlyDictionary<string, string> ReadCached()
    {
        if (!File.Exists(NamesPath)) return new Dictionary<string, string>();
        Dictionary<string, string> names;
        try { names = ParseNames(File.ReadAllBytes(NamesPath)); }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        { return new Dictionary<string, string>(); }
        if (File.Exists(TranslationsPath))
        {
            try
            {
                var translations = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(TranslationsPath));
                if (translations != null)
                    foreach (var key in names.Keys.ToArray())
                        if (key.StartsWith("B:", StringComparison.Ordinal) && translations.TryGetValue(names[key].Trim().ToLowerInvariant(), out var translated))
                            names[key] = translated;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return names;
    }

    public static async Task<int> DownloadAsync(CancellationToken token = default)
    {
        if (File.Exists(NamesPath))
        {
            try { return ParseNames(await File.ReadAllBytesAsync(NamesPath, token)).Count; }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException) { }
        }
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(20));
        var bytes = await Workspace.Http.GetByteArrayAsync(NamesUrl, budget.Token);
        var names = ParseNames(bytes);
        await WriteCacheFile(NamesPath, bytes, budget.Token);
        return names.Count;
    }

    public static async Task<int> DownloadTranslationsAsync(CancellationToken token = default)
    {
        if (!File.Exists(NamesPath)) return 0;
        var names = ParseNames(await File.ReadAllBytesAsync(NamesPath, token));
        var wanted = names.Where(entry => entry.Key.StartsWith("B:", StringComparison.Ordinal) &&
                !entry.Value.StartsWith("N:", StringComparison.Ordinal))
            .Select(entry => entry.Value.Trim().ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        if (wanted.Count == 0) return 0;

        try { return await DownloadCafeTranslations(wanted, token); }
        catch (Exception ex) when (!token.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException or IOException or JsonException or InvalidDataException)
        {
            // A previously verified bilingual cache provides broader coverage than the historical fallback.
            if (File.Exists(TranslationsPath))
            {
                try
                {
                    var cached = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllBytesAsync(TranslationsPath, token));
                    if (cached is { Count: > 0 }) return cached.Count;
                }
                catch (JsonException) { }
            }
            return await DownloadInfSeinTranslations(wanted, token);
        }
    }

    private static async Task<int> DownloadCafeTranslations(HashSet<string> wanted, CancellationToken token)
    {
        const string table = "BNpcName.csv";
        const int pageSize = 4096;
        var matches = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(35));
        var lastRow = -1;
        for (var page = 0; page < 8; page++)
        {
            var request = new
            {
                size = pageSize,
                _source = new[] { "index", "filename", "en", "cn" },
                sort = new[] { new { index = "asc" } },
                query = new
                {
                    @bool = new
                    {
                        must = new object[] { new { match = new { filename = table } }, new { range = new { index = new { gt = lastRow } } } }
                    }
                }
            };
            using var response = await Workspace.Http.PostAsJsonAsync("https://strings-legacy.ffcafe.cn/xivcsv/_search", request, budget.Token);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(budget.Token));
            var hits = document.RootElement.GetProperty("hits").GetProperty("hits");
            foreach (var hit in hits.EnumerateArray())
            {
                budget.Token.ThrowIfCancellationRequested();
                var row = hit.GetProperty("_source");
                lastRow = Math.Max(lastRow, row.GetProperty("index").GetInt32());
                if (row.GetProperty("filename").GetString() != table || !row.TryGetProperty("en", out var english)) continue;
                var source = FirstLine(english.GetString()).ToLowerInvariant();
                if (!wanted.Contains(source)) continue;
                var translated = row.TryGetProperty("cn", out var chinese) ? FirstLine(chinese.GetString()) : "";
                if (!matches.TryGetValue(source, out var values)) matches.Add(source, values = new(StringComparer.Ordinal));
                values.Add(translated);
            }
            if (hits.GetArrayLength() < pageSize)
            {
                var translations = Unambiguous(matches);
                await SaveTranslations(translations, budget.Token);
                return translations.Count;
            }
        }
        throw new InvalidDataException("FFCafé 名称表超过读取上限，已保留原缓存。");

        static string FirstLine(string? text) => (text ?? "").Split('\n', 2)[0].Trim();
    }

    private static async Task<int> DownloadInfSeinTranslations(HashSet<string> wanted, CancellationToken token)
    {
        const string pin = "9e2c05c0ed8205d5397031152f62a34900355de4";
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(25));
        var sheets = await Task.WhenAll(Read("en", "147ca5a6c39ca0a4f1dd4da681d2b23116feecf7"),
            Read("chs", "b4d82cf1181ca4b683435ed302def59bb10bdc98"));
        var matches = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (id, fields) in sheets[0].Rows)
        {
            var source = fields[0].Display.Trim().ToLowerInvariant();
            if (!wanted.Contains(source)) continue;
            if (!matches.TryGetValue(source, out var values)) matches.Add(source, values = new(StringComparer.Ordinal));
            values.Add(sheets[1].Rows.TryGetValue(id, out var chinese) ? chinese[0].Display.Trim() : "");
        }
        var translations = Unambiguous(matches);
        await SaveTranslations(translations, budget.Token);
        return translations.Count;

        async Task<SheetData> Read(string language, string hash)
        {
            var target = Path.Combine(CacheDirectory, $"{pin}-{language}-BNpcName.csv");
            if (File.Exists(target))
            {
                try
                {
                    VerifyBlob(await File.ReadAllBytesAsync(target, budget.Token), hash);
                    return CsvSheetReader.Read(target, budget.Token);
                }
                catch (InvalidDataException) { }
            }
            var bytes = await Workspace.Http.GetByteArrayAsync($"https://raw.githubusercontent.com/InfSein/ffxiv-datamining-mixed/{pin}/{language}/BNpcName.csv", budget.Token);
            VerifyBlob(bytes, hash);
            await WriteCacheFile(target, bytes, budget.Token);
            return CsvSheetReader.Read(target, budget.Token);
        }
    }

    private static Dictionary<string, string> Unambiguous(Dictionary<string, HashSet<string>> matches) =>
        matches.Where(pair => pair.Value.Count == 1 && !pair.Value.Contains("")).ToDictionary(pair => pair.Key, pair => pair.Value.Single());

    private static Task SaveTranslations(Dictionary<string, string> translations, CancellationToken token) =>
        WriteCacheFile(TranslationsPath, JsonSerializer.SerializeToUtf8Bytes(translations, Workspace.Json), token);

    private static async Task WriteCacheFile(string path, byte[] bytes, CancellationToken token)
    {
        Directory.CreateDirectory(CacheDirectory);
        var pending = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            await File.WriteAllBytesAsync(pending, bytes, token);
            token.ThrowIfCancellationRequested();
            File.Move(pending, path, true);
        }
        finally { File.Delete(pending); }
    }

    private static Dictionary<string, string> ParseNames(byte[] bytes)
    {
        VerifyBlob(bytes, BlobHash);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(bytes) ?? throw new InvalidDataException("NPC 名称表为空。");
    }

    private static void VerifyBlob(byte[] bytes, string expected)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(Encoding.ASCII.GetBytes($"blob {bytes.Length}\0"));
        hash.AppendData(bytes);
        if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("NPC 名称数据与固定版本的 Git blob 校验值不一致。");
    }
}
