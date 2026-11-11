using FFXIVPatchParser;

try
{
    if (args.Length == 0 || args[0] is "--help" or "help")
    {
        Console.WriteLine("FFXIVPatchParser\n  snapshot --game <game-root-or-sqpack> [--output tmp/snapshots/<name>] [--sheets Item,Action]\n  self-test\nExports CHS and language-neutral sheets, including raw column types and SeString bytes.");
        return 0;
    }
    if (args[0] == "self-test") { SnapshotTests.Run(); return 0; }
    if (args[0] != "snapshot") throw new ArgumentException("Unknown command.");
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 1; i < args.Length; i += 2)
    {
        if (i + 1 >= args.Length || args[i] is not ("--game" or "--output" or "--sheets") || !options.TryAdd(args[i], args[i + 1]))
            throw new ArgumentException($"Invalid or duplicate option: {args[i]}");
    }
    var output = options.GetValueOrDefault("--output") ?? $"tmp/snapshots/chs-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}";
    var game = options.GetValueOrDefault("--game") ?? Environment.GetEnvironmentVariable("FFXIV_GAME_PATH");
    if (string.IsNullOrWhiteSpace(game)) throw new ArgumentException("Specify --game or set FFXIV_GAME_PATH.");
    return SnapshotExporter.Run(game, output,
        options.GetValueOrDefault("--sheets")?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 1;
}
