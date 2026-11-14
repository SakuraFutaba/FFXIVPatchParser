using System.IO;
using System.Reflection.PortableExecutable;

namespace FFXIVPatchParser.Desktop;

internal static class GodbertAnimationSettings
{
    public const string LibraryName = "hkAnimationInterop.dll";
    private static string SettingPath => Path.Combine(Workspace.Temp, "ui", "godbert-path.txt");

    public static string? ReadDirectory()
    {
        if (!File.Exists(SettingPath)) return null;
        var directory = File.ReadAllText(SettingPath).Trim();
        return Path.IsPathFullyQualified(directory) && File.Exists(Path.Combine(directory, LibraryName)) ? directory : null;
    }

    public static void SaveLibraryPath(string input)
    {
        var path = input.Trim().Trim('"');
        if (path.Length != 0)
        {
            if (!Path.IsPathFullyQualified(path))
                throw new ArgumentException("请输入 Godbert 安装目录或 DLL 的完整路径。");
            path = Path.GetFullPath(path);
            if (Directory.Exists(path)) path = Path.Combine(path, LibraryName);
            if (!Path.GetFileName(path).Equals(LibraryName, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                throw new FileNotFoundException("找不到 hkAnimationInterop.dll。请选择 Godbert 安装目录中的文件。");

            using var stream = File.OpenRead(path);
            using var reader = new PEReader(stream);
            var header = reader.PEHeaders.CoffHeader;
            if (header.Machine != Machine.I386 || !header.Characteristics.HasFlag(Characteristics.Dll) || reader.HasMetadata)
                throw new InvalidDataException("请选择 Godbert 附带的 32 位原生动作解码库。");
            path = Path.GetDirectoryName(path)!;
        }

        var settings = SettingPath;
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        var pending = settings + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            File.WriteAllText(pending, path);
            File.Move(pending, settings, overwrite: true);
        }
        finally { File.Delete(pending); }
    }
}
