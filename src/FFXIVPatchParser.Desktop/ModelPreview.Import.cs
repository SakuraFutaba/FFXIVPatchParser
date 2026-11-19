using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace FFXIVPatchParser.Desktop;

public partial class ModelPreview
{
    private string CharacterSaveDirectory()
    {
        var folder = Path.Combine("My Games", "FINAL FANTASY XIV - A Realm Reborn");
        var gameDirectory = source == null ? null : Directory.GetParent(source.SqPackPath);
        if (gameDirectory != null && Directory.Exists(Path.Combine(gameDirectory.FullName, folder)))
            return Path.Combine(gameDirectory.FullName, folder);
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), folder);
    }

    internal async Task ImportCharacterAppearance()
    {
        if (source is not { } game) { SetModelStatus("请先选择游戏资源目录。", error: true); return; }
        var dialog = new OpenFileDialog { Title = "导入角色外观", Filter = "角色外观存档|FFXIV_CHARA_*.dat",
            InitialDirectory = CharacterSaveDirectory() };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            var preset = CharacterAppearancePreset.Read(dialog.FileName);
            var target = "character:" + preset.Code;
            if (scene == null || partCode != preset.Code) await OpenAsync(target);
            if (partCode != preset.Code || source != game || activePalette == null) return;
            var current = scene;
            var options = await Task.Run(() => game.ListCharacterPartOptions(preset.Code));
            if (current != scene || source != game) return;
            var tribe = activePalette.Tribes.First(t => t.Id == preset.Tribe);
            changingAppearance = true;
            try
            {
                TribeColors.SelectedItem = tribe;
                SetTribeColors(tribe, preset.SkinColor, preset.HairColor);
                EyeColors.SelectedIndex = preset.EyeColor;
                HighlightColors.SelectedIndex = preset.Highlights is byte h ? h + 1 : 0;
            }
            finally { changingAppearance = false; }
            var face = tribe.Faces.FirstOrDefault(f => f.Label.StartsWith($"脸型 {preset.Face}（", StringComparison.Ordinal));
            var hair = options.FirstOrDefault(p => p.Slot == "hir" && p.Path.Contains($"/h{preset.Hair:D4}/", StringComparison.Ordinal));
            if (face != null) desiredParts["fac"] = face;
            if (hair != null) desiredParts["hir"] = hair;
            UpdatePartChoices();
            RefreshSlotButtons();
            await UpdateEquippedPreview();
            if (partCode == preset.Code && PartStatus.Text.Length == 0)
                SetModelStatus($"已导入角色外观 · 脸型{(face == null ? "未匹配" : "已匹配")}，发型{(hair == null ? "未匹配" : "已匹配")}。");
        }
        catch (Exception ex) { SetModelStatus($"无法导入角色外观 · {ex.Message}", error: true); }
    }
}
