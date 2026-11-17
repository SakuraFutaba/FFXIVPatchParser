using Lumina.Data.Files;
using System.IO;

namespace FFXIVPatchParser;

public sealed partial class LocalGameSource
{
    public IReadOnlyList<string> ListUldPaths(CancellationToken token = default)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var container in ListFileSystemContainers(token).Where(c => c.Category == "ui"))
        foreach (var entry in EnumerateFileSystemEntries(container.Id, token))
        {
            token.ThrowIfCancellationRequested();
            if (entry.KnownPath is { } path && path.EndsWith(".uld", StringComparison.OrdinalIgnoreCase)) paths.Add(path);
        }
        lock (gate) if (game.FileExists("ui/uld/ActionBar.uld")) paths.Add("ui/uld/ActionBar.uld");
        return paths.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public UldFile ReadUld(string path)
    {
        path = path.Trim().Replace('\\', '/');
        if (!path.Contains('/')) path = "ui/uld/" + path;
        if (!path.EndsWith(".uld", StringComparison.OrdinalIgnoreCase)) path += ".uld";
        lock (gate) return game.GetFile<UldFile>(path) ?? throw new FileNotFoundException("未找到 ULD：" + path);
    }

    public bool UldAssetExists(string path)
    {
        lock (gate) return game.FileExists(path);
    }
}
