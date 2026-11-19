using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace FFXIVPatchParser.Desktop;

public partial class ModelPreview
{
    private sealed record SlotItem(string Key, string Label, int Row, int Column);
    private sealed record PartItem(string Label, GameCharacterPartOption? Option);
    private static readonly SlotItem[] PartSlots =
    [
        new("weapon", "武器", 0, 0), new("met", "头部", 1, 0), new("top", "身体", 2, 0),
        new("glv", "手部", 3, 0), new("dwn", "腿部", 4, 0), new("sho", "脚部", 5, 0),
        new("offhand", "副手", 0, 2), new("ear", "耳饰", 1, 2), new("nek", "项链", 2, 2), new("wrs", "手镯", 3, 2),
        new("rir", "戒指", 4, 2), new("ril", "戒指", 5, 2), new("hir", "发型", 6, 0), new("fac", "脸型", 6, 2)
    ];

    private CancellationTokenSource? partOptionsCancellation, partPreviewCancellation;
    private string? partCode;
    private string? npcPartCode;
    private uint? npcPartTribe;
    private IReadOnlyList<GameCharacterPartOption>? partOptions;
    private Dictionary<string, GameCharacterPartOption> appliedParts = new();
    private Dictionary<string, GameCharacterPartOption> desiredParts = new();
    private Dictionary<string, GameCharacterPartOption> defaultParts = new();
    private bool updatingParts, appliedSheathed;

    private void OpenInspectorPopup(object sender, RoutedEventArgs e) => MaterialsPopup.IsOpen = true;

    private void ShowCharacterParts(string? code)
    {
        partOptionsCancellation?.Cancel();
        partPreviewCancellation?.Cancel();
        slotIcons.Clear();
        slotFrame = null;
        dyeGlyph = null;
        partCode = code;
        npcPartCode = null;
        npcPartTribe = null;
        partOptions = null;
        defaultParts.Clear();
        appliedParts = new();
        desiredParts = new();
        appliedSheathed = false;
        WeaponSheathed.IsChecked = false;
        AppearanceContent.IsEnabled = true;
        CharacterParts.Visibility = code == null ? Visibility.Collapsed : Visibility.Visible;
        CharacterPartEditor.Visibility = Visibility.Visible;
        PartStatus.Text = "";
        PartFilter.Clear();
        if (code == null)
        {
            SlotGrid.Children.Clear();
            PartChoice.ItemsSource = null;
            return;
        }
        PartSlot.ItemsSource = PartSlots;
        BuildSlotButtons();
        PartSlot.SelectedIndex = 0;
        UpdatePartChoices();
        RefreshSlotButtons();
        if (CharacterParts.IsExpanded) CharacterPartsExpanded(CharacterParts, new RoutedEventArgs());
    }

    private void ShowNpcParts(GameCharacterNpcPreset preset, IReadOnlyList<GameCharacterPartOption> parts)
    {
        ShowCharacterParts(null);
        npcPartCode = preset.Code;
        npcPartTribe = preset.TribeId;
        defaultParts = parts.DistinctBy(part => part.Slot).ToDictionary(part => part.Slot);
        CharacterParts.Visibility = Visibility.Visible;
        CharacterPartEditor.Visibility = Visibility.Collapsed;
        BuildSlotButtons();
        RefreshSlotButtons();
    }

    private async void CharacterPartsExpanded(object sender, RoutedEventArgs e)
    {
        if (partCode is not { } code || source is not { } game || partOptions != null ||
            partOptionsCancellation is { IsCancellationRequested: false }) return;
        using var pending = new CancellationTokenSource();
        partOptionsCancellation = pending;
        var token = pending.Token;
        PartStatus.Text = "正在读取角色部件…";
        try
        {
            var (options, defaults) = await Task.Run(() => (game.ListCharacterPartOptions(code, token), game.GetDefaultCharacterPartOptions(code, token)), token);
            token.ThrowIfCancellationRequested();
            partOptions = options;
            defaultParts = defaults.DistinctBy(p => p.Slot).ToDictionary(p => p.Slot);
            RefreshSlotButtons();
            UpdatePartChoices();
            PartStatus.Text = "";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) PartStatus.Text = ex.Message; }
        finally { if (partOptionsCancellation == pending) partOptionsCancellation = null; }
    }

    private void BuildSlotButtons()
    {
        SlotGrid.Children.Clear();
        SlotGrid.RowDefinitions.Clear();
        for (int row = 0; row < 7; row++) SlotGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        foreach (var slot in PartSlots)
        {
            var button = new Button
            {
                Tag = slot, Height = 54, Margin = new Thickness(0, 1, 0, 1), Padding = new Thickness(4, 2, 4, 2),
                HorizontalContentAlignment = HorizontalAlignment.Stretch, Focusable = npcPartCode == null
            };

            if (npcPartCode == null) button.Click += (_, _) => PartSlot.SelectedItem = slot;
            Grid.SetColumn(button, slot.Column);
            Grid.SetRow(button, slot.Row);
            SlotGrid.Children.Add(button);
        }
    }

    private readonly Dictionary<(string Code, string Slot, uint Icon, string? Path, uint? Tribe), Task<BitmapSource?>> slotIcons = new();
    private Task<BitmapSource?>? slotFrame;

    private Task<BitmapSource?> ReadSlotIcon(string code, string slot, GameCharacterPartOption? part)
    {
        var game = source;
        if (game == null) return Task.FromResult<BitmapSource?>(null);
        var tribe = npcPartTribe ?? (TribeColors.SelectedItem as GameCharacterTribe)?.Id;
        var key = (code, slot, part?.IconId ?? 0, part?.IconId > 0 ? null : part?.Path, tribe);
        if (slotIcons.TryGetValue(key, out var existing)) return existing;
        var pending = Task.Run(() =>
        {
            var texture = game.ReadCharacterPartIcon(code, part, slot, tribe);
            return texture == null ? null : CreateBitmap(texture, System.Windows.Media.PixelFormats.Bgra32);
        });
        if (slotIcons.Count >= 512) slotIcons.Clear();
        slotIcons[key] = pending;
        return pending;
    }

    private void PartIconLoaded(object sender, RoutedEventArgs e) => ShowPartIcon((Image)sender);

    private void PartIconChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (((Image)sender).IsLoaded) ShowPartIcon((Image)sender);
    }

    private async void ShowPartIcon(Image image)
    {
        image.Source = null;
        if (image.DataContext is not PartItem item || PartSlot.SelectedItem is not SlotItem slot || partCode is not { } code) return;
        try
        {
            var icon = await ReadSlotIcon(code, slot.Key, item.Option ?? DefaultPart(slot.Key));
            if (ReferenceEquals(image.DataContext, item)) image.Source = icon;
        }
        catch (Exception ex) { image.ToolTip = ex.Message; }
    }

    private static string GetPartName(GameCharacterPartOption part)
    {
        var suffix = part.RowId is { } row ? $" · #{row}" : null;
        return suffix != null && part.Label.EndsWith(suffix, StringComparison.Ordinal) ? part.Label[..^suffix.Length] : part.Label;
    }

    private GameCharacterPartOption? DefaultPart(string slot) =>
        (slot == "fac" ? SelectedTribeDefaultFace() : null) ?? defaultParts.GetValueOrDefault(slot);

    private GameCharacterPartOption? VisiblePart(string slot) => desiredParts.GetValueOrDefault(slot) ?? DefaultPart(slot);

    private async void CopySelectedPartName(object sender, RoutedEventArgs e)
    {
        if (PartSlot.SelectedItem is SlotItem slot && VisiblePart(slot.Key) is { } part) await CopyPartNameAsync(GetPartName(part));
    }

    private async Task CopyPartNameAsync(string name)
    {
        if (PresentationSource.FromVisual(this) is not HwndSource window) return;
        try
        {
            var copied = await ClipboardText.TryCopyAsync(window.Handle, name);
            MainWindow.Notify(this, copied ? "已复制装备名" : "剪贴板正被占用，请稍后重试。");
        }
        catch (System.ComponentModel.Win32Exception ex) { MainWindow.Notify(this, "无法复制装备名 · " + ex.Message); }
    }

    private void RefreshSlotButtons()
    {
        foreach (var button in SlotGrid.Children.OfType<Button>().ToArray())
        {
            var slot = (SlotItem)button.Tag;
            var part = VisiblePart(slot.Key);
            var label = part?.Label ?? slot.Label;
            var id = part?.RowId is { } row ? $"#{row}" : null;
            var name = part == null ? label : GetPartName(part);
            var onRight = slot.Column == 2;
            var align = onRight ? TextAlignment.Right : TextAlignment.Left;
            button.ToolTip = npcPartCode != null && part != null ? $"{label}\n{part.Path}" : label;
            button.ContextMenu = null;
            if (part != null)
            {
                var copy = new MenuItem { Header = "复制装备名" };
                copy.Click += async (_, _) => await CopyPartNameAsync(name);
                button.ContextMenu = new ContextMenu { Items = { copy } };
            }
            var panel = new DockPanel();
            var image = new Image { Stretch = System.Windows.Media.Stretch.Uniform };
            // ULD 图标相对组件根节点定位。外框沿自己的父节点左移 2 像素。
            var glyph = new Grid { Margin = new Thickness(4, 3, 4, 5) };
            var fallback = new TextBlock
            {
                Text = slot.Label[..1], Foreground = (System.Windows.Media.Brush)FindResource("ShellAccent"),
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center
            };
            glyph.Children.Add(fallback);
            glyph.Children.Add(image);
            var frame = new Image { IsHitTestVisible = false };
            var sprite = new Grid { Width = 48, Height = 48 };
            sprite.Children.Add(glyph);
            sprite.Children.Add(frame);
            if (part is { DyeCount: > 0 } && npcPartCode == null) AddDyeControls(sprite, part);
            var tile = new Border
            {
                Width = 48, Height = 48, CornerRadius = new CornerRadius(5), Child = sprite,
                Background = (System.Windows.Media.Brush)FindResource("ShellBase"),
                Margin = onRight ? new Thickness(5, 0, 0, 0) : new Thickness(0, 0, 5, 0)
            };
            DockPanel.SetDock(tile, onRight ? Dock.Right : Dock.Left);
            panel.Children.Add(tile);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock
            {
                Text = name, TextAlignment = align, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.Wrap,
                LineHeight = 16, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, MaxHeight = 32,
                FontWeight = Equals(PartSlot.SelectedItem, slot) ? FontWeights.SemiBold : FontWeights.Normal
            });
            if (id != null)
                text.Children.Add(new TextBlock { Text = id, TextAlignment = align, FontSize = 10, Foreground = (System.Windows.Media.Brush)FindResource("Muted") });
            panel.Children.Add(text);
            button.Content = panel;
            if ((partCode ?? npcPartCode) is { } code)
            {
                _ = SetSlotImage(image, fallback, code, slot.Key, part);
                if (source is { } game) _ = SetSlotFrame(frame, slotFrame ??= Task.Run(() =>
                {
                    var texture = game.ReadCharacterSlotFrame();
                    return texture == null ? null : CreateBitmap(texture, System.Windows.Media.PixelFormats.Bgra32);
                }));
            }
        }
    }

    private static async Task SetSlotFrame(Image image, Task<BitmapSource?> pending)
    {
        try { image.Source = await pending; }
        catch (Exception ex) { image.ToolTip = ex.Message; }
    }

    private async Task SetSlotImage(Image image, TextBlock fallback, string code, string slot, GameCharacterPartOption? part)
    {
        try
        {
            image.Source = await ReadSlotIcon(code, slot, part);
            fallback.Visibility = image.Source == null ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) { image.ToolTip = ex.Message; }
    }

    private void PartSlotChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshSlotButtons();
        PartFilter?.Clear();
        UpdatePartChoices();
    }

    private void PartFilterChanged(object sender, TextChangedEventArgs e) => UpdatePartChoices();

    private void UpdatePartChoices()
    {
        if (PartChoice == null || PartSlot.SelectedItem is not SlotItem slot) return;
        updatingParts = true;
        try
        {
            var defaultPart = DefaultPart(slot.Key);
            var items = new List<PartItem> { new(defaultPart?.Label ?? (slot.Key is "weapon" or "offhand" ? "无武器" : "空槽位"), null) };
            var current = desiredParts.GetValueOrDefault(slot.Key);
            if (partOptions != null)
            {
                var filter = PartFilter.Text.Trim();
                var options = slot.Key == "fac" ? (TribeColors.SelectedItem as GameCharacterTribe)?.Faces ?? [] : partOptions;
                items.AddRange(options.Where(p => p.Slot == slot.Key &&
                    (slot.Key != "fac" || p.Path != defaultPart?.Path) &&
                    (p == current || p.Label.Contains(filter, StringComparison.OrdinalIgnoreCase))).Select(p => new PartItem(p.Label, p)));
            }
            if (current != null && !items.Any(item => item.Option == current))
            {
                var match = items.FindIndex(item => item.Option is { } option && option.Path == current.Path && option.RowId == current.RowId);
                var selected = new PartItem(current.Label, current);
                if (match >= 0) items[match] = selected;
                else items.Add(selected);
            }
            PartChoice.ItemsSource = items;
            PartChoice.SelectedItem = items.FirstOrDefault(p => p.Option == current || (slot.Key == "fac" && p.Option?.Path == current?.Path)) ?? items[0];
            PartChoice.IsEnabled = items.Count > 1;
            CopyPartName.IsEnabled = VisiblePart(slot.Key) != null;
        }
        finally { updatingParts = false; }
    }

    private async void WeaponSheathedChanged(object sender, RoutedEventArgs e) => await UpdateEquippedPreview();

    private async void PartChoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingParts || PartSlot.SelectedItem is not SlotItem slot || PartChoice.SelectedItem is not PartItem item) return;
        if (item.Option == null)
        {
            desiredParts.Remove(slot.Key);
            if (slot.Key == "fac" && SelectedTribeDefaultFace() is { } face)
                desiredParts[slot.Key] = face;
        }
        else desiredParts[slot.Key] = item.Option;
        CopyPartName.IsEnabled = VisiblePart(slot.Key) != null;
        RefreshSlotButtons();
        await UpdateEquippedPreview();
    }

    private async Task UpdateEquippedPreview()
    {
        if (partCode is not { } code || source is not { } game || scene is not { } current) return;
        var sheathed = WeaponSheathed.IsChecked == true;
        var selected = new Dictionary<string, GameCharacterPartOption>(desiredParts);
        partPreviewCancellation?.Cancel();
        using var pending = new CancellationTokenSource();
        partPreviewCancellation = pending;
        var token = pending.Token;
        PartStatus.Text = "正在更新角色…";
        try
        {
            var colors = ReadSelectedCharacterColors() ?? activePalette?.DefaultColors ?? GameCharacterColors.Neutral;
            var variant = int.TryParse(MaterialVariant.Text, out var value) && value is >= 1 and <= 9999 ? value : 1;
            var directory = GodbertAnimationSettings.ReadDirectory();
            var preview = await Task.Run(() => BuildScene(game.ReadCharacterPreview(code, colors, selected.Values.ToArray(), variant, token, directory, sheathed), token, current), token);
            token.ThrowIfCancellationRequested();
            var view = camera.CaptureView();
            ShowCharacterSceneWithPlayback(preview);
            camera.RestoreView(view);
            appliedParts = selected;
            appliedSheathed = sheathed;
            shownColors = colors;
            PartStatus.Text = "";
            await RecolorCharacterAsync();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                desiredParts = new(appliedParts);
                WeaponSheathed.IsChecked = appliedSheathed;
                RefreshSlotButtons();
                UpdatePartChoices();
                PartStatus.Text = ex.Message;
            }
        }
        finally { if (partPreviewCancellation == pending) partPreviewCancellation = null; }
    }
}
