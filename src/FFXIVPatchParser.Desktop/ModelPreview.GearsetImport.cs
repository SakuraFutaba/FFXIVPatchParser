using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace FFXIVPatchParser.Desktop;

public partial class ModelPreview
{
    internal async Task ImportCharacterGearset()
    {
        if (source is not { } game || partCode is not { } code)
        {
            SetModelStatus("请先导入外观或打开角色，再选择套装。", error: true);
            return;
        }
        var dialog = new OpenFileDialog { Title = "打开角色目录中的 GEARSET.DAT",
            Filter = "游戏套装配置|GEARSET.DAT", InitialDirectory = CharacterSaveDirectory() };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            var sets = SavedGearset.Read(dialog.FileName);
            if (sets.Count == 0) { SetModelStatus("文件中没有已保存的套装。"); return; }
            var selected = new ListBox { ItemsSource = sets, SelectedIndex = 0 };
            var layout = new DockPanel { Margin = new Thickness(12) };
            var apply = new Button { Content = "应用套装", IsDefault = true, Margin = new Thickness(0,8,0,0) };
            DockPanel.SetDock(apply, Dock.Bottom);
            layout.Children.Add(apply);
            var note = new TextBlock { Text = "按套装保存的投影物品预览。关联模板编号仅供参考。",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,8) };
            DockPanel.SetDock(note, Dock.Top);
            layout.Children.Add(note);
            layout.Children.Add(selected);
            var window = new Window { Title = "选择套装", Owner = Window.GetWindow(this), Content = layout,
                Width = 420, Height = 480, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            apply.Click += (_, _) => window.DialogResult = true;
            if (window.ShowDialog() != true || selected.SelectedItem is not SavedGearset set) return;
            var current = scene;
            var options = await Task.Run(() => game.ListCharacterPartOptions(code));
            if (partCode != code || source != game || current != scene) return;
            foreach (var slot in PartSlots.Where(s => s.Key is not ("fac" or "hir"))) desiredParts.Remove(slot.Key);
            var matched = 0;
            var canAttachWeapon = GodbertAnimationSettings.ReadDirectory() != null;
            foreach (var piece in set.Pieces)
            {
                if (!canAttachWeapon && piece.Slot is ("weapon" or "offhand")) continue;
                var option = options.FirstOrDefault(p => p.Slot == piece.Slot && p.RowId == piece.AppearanceItem);
                if (option == null) continue;
                if (piece.Slot != "met" || set.HeadgearVisible)
                    desiredParts[piece.Slot] = option with { Stain0Id = piece.Stain1, Stain1Id = piece.Stain2 };
                if (piece.Slot == "weapon" && set.Pieces.All(p => p.Slot != "offhand") &&
                    options.FirstOrDefault(p => p.Slot == "offhand" && p.RowId == piece.AppearanceItem) is { } secondary)
                    desiredParts["offhand"] = secondary with { Stain0Id = piece.Stain1, Stain1Id = piece.Stain2 };
                matched++;
            }
            UpdatePartChoices();
            RefreshSlotButtons();
            await UpdateEquippedPreview();
            if (partCode == code && PartStatus.Text.Length == 0)
                SetModelStatus($"已导入套装 {set.Number} · 匹配 {matched}/{set.Pieces.Count} 个装备部位。" +
                    (!canAttachWeapon && set.Pieces.Any(p => p.Slot is "weapon" or "offhand") ? " 武器预览需要配置 Godbert。" : ""));
        }
        catch (Exception ex) { SetModelStatus($"无法导入套装 · {ex.Message}", error: true); }
    }
}
