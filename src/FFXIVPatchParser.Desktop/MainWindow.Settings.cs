using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace FFXIVPatchParser.Desktop;

public partial class MainWindow
{
    private static string DefaultPatchDirectory => Path.Combine(Workspace.Temp, "patches");
    private static string PatchPathSetting => Path.Combine(Workspace.Temp, "ui", "patch-path.txt");
    private string? configuredPatchDirectory;
    private string PatchDirectory => configuredPatchDirectory ?? DefaultPatchDirectory;

    private void ReadPathSettings()
    {
        if (File.Exists(PatchPathSetting))
        {
            var saved = File.ReadAllText(PatchPathSetting).Trim();
            if (Path.IsPathFullyQualified(saved)) configuredPatchDirectory = Path.GetFullPath(saved);
        }
        RefreshPathSettings();
    }

    private void RefreshPathSettings()
    {
        PatchDirectorySetting.Text = PatchDirectory;
        try { GameDirectorySetting.Text = SqPackView.ConnectedGamePath ?? SqPackBrowser.RememberedGamePath() ?? ""; }
        catch (IOException ex) { SetStatus("无法读取游戏路径：" + ex.Message, true); }
        catch (UnauthorizedAccessException ex) { SetStatus("无法读取游戏路径：" + ex.Message, true); }
    }

    private async void ChooseGameDirectory(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择游戏安装目录、game 或 sqpack" };
        if (Directory.Exists(GameDirectorySetting.Text)) dialog.InitialDirectory = GameDirectorySetting.Text;
        if (dialog.ShowDialog(this) != true) return;

        ChooseGameDirectoryButton.IsEnabled = false;
        try
        {
            await SqPackView.ConnectGame(dialog.FolderName);
            RefreshPathSettings();
            SetStatus(SqPackView.ConnectionStatus, SqPackView.HasConnectionError);
        }
        finally { ChooseGameDirectoryButton.IsEnabled = true; }
    }

    private async void ChoosePatchDirectory(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择已有补丁的目录" };
        if (Directory.Exists(PatchDirectory)) dialog.InitialDirectory = PatchDirectory;
        if (dialog.ShowDialog(this) == true) await SavePatchDirectory(dialog.FolderName);
    }

    private async void ResetPatchDirectory(object sender, RoutedEventArgs e) => await SavePatchDirectory(null);

    private Task SavePatchDirectory(string? directory) => Work("正在保存补丁路径…", async token =>
    {
        var selected = directory == null ? null : Path.GetFullPath(directory);
        Directory.CreateDirectory(Path.GetDirectoryName(PatchPathSetting)!);
        var partial = PatchPathSetting + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            await File.WriteAllTextAsync(partial, selected ?? "", token);
            token.ThrowIfCancellationRequested();
            File.Move(partial, PatchPathSetting, overwrite: true);
        }
        finally { File.Delete(partial); }
        configuredPatchDirectory = selected;
        RefreshPathSettings();
        ApplyCatalogFilter();
    });

    private async void LoadLatestPatchFromSettings(object sender, RoutedEventArgs e)
    {
        await Work("正在查找最新补丁…", async token =>
        {
            if (!await OpenLatestPatch(token, showWorkspace: true)) throw new FileNotFoundException("补丁目录中找不到带原始文件名的 ZIP。");
        });
    }

    private async Task<bool> OpenLatestPatch(CancellationToken token, bool showWorkspace = false)
    {
        var root = PatchDirectory;
        if (!Directory.Exists(root)) return false;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = 3,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true
        };
        var files = await Task.Run(() =>
        {
            var latest = Directory.EnumerateFiles(root, "*.zip", options)
                .Select(path =>
                {
                    token.ThrowIfCancellationRequested();
                    var versions = PatchVersions(path);
                    return (Path: path, Versions: versions, Target: Version.TryParse(versions.LastOrDefault(), out var target) ? target : null);
                })
                .Where(entry => entry.Target != null)
                .OrderByDescending(entry => entry.Target)
                .ThenByDescending(entry => File.GetLastWriteTimeUtc(entry.Path)).FirstOrDefault();
            if (latest.Path == null) return [];
            return Directory.EnumerateFiles(Path.GetDirectoryName(latest.Path)!, "*.zip")
                .Where(path => PatchVersions(path).SequenceEqual(latest.Versions)).ToArray();
        }, token);
        if (files.Length == 0) return false;
        if (showWorkspace) PatchNav.IsChecked = true;
        await LoadPatchFiles(files, token);
        return true;
    }
}
