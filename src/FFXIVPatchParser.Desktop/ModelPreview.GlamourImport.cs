using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace FFXIVPatchParser.Desktop;

public partial class ModelPreview
{
    internal async Task ImportGlamourDesign()
    {
        if (source is not { } game || partCode is not { } code)
        {
            SetModelStatus("请先导入外观或打开角色，再选择幻化设计。", error: true);
            return;
        }
        try
        {
            var input = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = 4096,
                TextAlignment = TextAlignment.Left, VerticalContentAlignment = VerticalAlignment.Top,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var layout = new DockPanel { Margin = new Thickness(12) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,8,0,0) };
            var fileButton = new Button { Content = "打开模板文件" };
            var codeButton = new Button { Content = "读取模板码", Margin = new Thickness(8,0,0,0) };
            buttons.Children.Add(fileButton);
            buttons.Children.Add(codeButton);
            DockPanel.SetDock(buttons, Dock.Bottom);
            layout.Children.Add(buttons);
            var label = new TextBlock { Text = "粘贴幻化柜 HGG 模板码，或打开幻化柜、Glamourer 导出的文件。", TextWrapping = TextWrapping.Wrap };
            DockPanel.SetDock(label, Dock.Top);
            layout.Children.Add(label);
            layout.Children.Add(input);
            var window = new Window { Owner = Window.GetWindow(this), Title = "导入模板码", Content = layout,
                Width = 450, Height = 240, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            window.Loaded += (_, _) => { input.Focus(); input.CaretIndex = 0; };
            var readFile = false;
            fileButton.Click += (_, _) => { readFile = true; window.DialogResult = true; };
            codeButton.Click += (_, _) => window.DialogResult = true;
            if (window.ShowDialog() != true) return;
            OutfitTemplate? template;
            if (readFile)
            {
                var dialog = new OpenFileDialog { Title = "导入幻化模板", Filter = "模板文件|*.json;*.txt;*.hgg;*.hggtemplates|所有文件|*.*",
                    InitialDirectory = CharacterSaveDirectory() };
                if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
                var templates = OutfitTemplate.ReadFile(dialog.FileName);
                template = templates.Count == 1 ? templates[0] : SelectOutfitTemplate(templates);
            }
            else template = OutfitTemplate.ReadCode(input.Text);
            if (template == null) return;
            var slots = template.Slots;
            if (slots.Count == 0) { SetModelStatus("此设计没有勾选装备或染色的部位。"); return; }
            var current = scene;
            var (options, defaults) = await Task.Run(() =>
                (game.ListCharacterPartOptions(code), game.GetDefaultCharacterPartOptions(code)));
            if (partCode != code || source != game || current != scene) return;
            var canAttachWeapon = GodbertAnimationSettings.ReadDirectory() != null;
            var count = 0;
            foreach (var slot in slots)
            {
                if (slot.ApplyItem && slot.ItemId == 0)
                {
                    desiredParts.Remove(slot.Slot);
                    count++;
                    continue;
                }
                if (!canAttachWeapon && slot.Slot is ("weapon" or "offhand")) continue;
                var old = desiredParts.GetValueOrDefault(slot.Slot) ?? defaults.FirstOrDefault(p => p.Slot == slot.Slot);
                var part = slot.ApplyItem
                    ? options.FirstOrDefault(p => p.Slot == slot.Slot && p.RowId is uint id && id == slot.ItemId)
                    : old;
                if (part == null) continue;
                desiredParts[slot.Slot] = part with
                {
                    Stain0Id = slot.ApplyStains ? slot.Stain0 : old?.Stain0Id ?? 0,
                    Stain1Id = slot.ApplyStains ? slot.Stain1 : old?.Stain1Id ?? 0
                };
                count++;
            }
            if (count > 0)
            {
                UpdatePartChoices();
                RefreshSlotButtons();
                await UpdateEquippedPreview();
                if (partCode != code || PartStatus.Text.Length != 0) return;
            }
            SetModelStatus($"幻化设计 · 已应用 {count}/{slots.Count} 个装备部位。" + (template.HasFacewear ? " 面饰尚未预览。" : "") +
                (count < slots.Count ? " 未匹配部位保留原样，特殊模型编号尚未支持。" : ""));
        }
        catch (Exception ex) { SetModelStatus($"无法导入幻化设计 · {ex.Message}", error: true); }
    }
    private OutfitTemplate? SelectOutfitTemplate(IReadOnlyList<OutfitTemplate> templates)
    {
        var list = new ListBox { ItemsSource = templates, SelectedIndex = 0 };
        var panel = new DockPanel { Margin = new Thickness(12) };
        var button = new Button { Content = "应用模板", IsDefault = true };
        DockPanel.SetDock(button, Dock.Bottom);
        panel.Children.Add(button);
        panel.Children.Add(list);
        var window = new Window { Owner = Window.GetWindow(this), Content = panel, Title = "选择幻化模板",
            Width = 400, Height = 450, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        button.Click += (_, _) => window.DialogResult = true;
        return window.ShowDialog() == true ? list.SelectedItem as OutfitTemplate : null;
    }
}
