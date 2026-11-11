using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace FFXIVPatchParser;

public static class Workspace
{
    public static string Root => SnapshotExporter.ProjectRoot();
    public static string Size(long bytes) => bytes < 1024 ? $"{bytes:N0} B" : bytes < 1048576 ? $"{bytes / 1024d:N2} KiB" : bytes < 1073741824 ? $"{bytes / 1048576d:N2} MiB" : $"{bytes / 1073741824d:N2} GiB";
    public static string Temp => Path.Combine(Root, "tmp");
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static HttpClient Http { get; } = CreateHttp();
    private static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FFXIVPatchParser", "0.1"));
        return client;
    }
    public static string Within(string root, string path) => SnapshotExporter.Under(root, path);
    public static async Task SaveJson(string path, object value, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".partial";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(value, Json), token);
        File.Move(temp, path, true);
    }
    public static async Task<bool> Matches(string path, long size, string md5, CancellationToken token)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != size) return false;
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await MD5.HashDataAsync(stream, token)).Equals(md5, StringComparison.OrdinalIgnoreCase);
    }
}
