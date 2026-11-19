using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FFXIVPatchParser.Desktop;

public partial class ModelPreview
{
    private bool changingAppearance;
    private GameCharacterPalette? activePalette;
    private CancellationTokenSource? recolorCancellation;
    private GameCharacterColors? shownColors;

    private void ShowCharacterAppearance(GameCharacterPalette? palette)
    {
        recolorCancellation?.Cancel();
        changingAppearance = true;
        try
        {
            activePalette = palette;
            shownColors = palette?.DefaultColors;
            PlayerColors.Visibility = Visibility.Visible;
            NpcColors.Visibility = Visibility.Collapsed;
            NpcColorItems.ItemsSource = null;
            CharacterAppearance.Visibility = palette == null ? Visibility.Collapsed : Visibility.Visible;
            AppearanceStatus.Text = "";
            TribeColors.ItemsSource = palette?.Tribes;
            TribeColors.IsEnabled = palette?.Tribes.Count > 1;
            TribeColors.SelectedIndex = palette == null ? -1 : 0;
            EyeColors.ItemsSource = palette?.Eyes.Select(CreateColorItem).ToArray();
            HighlightColors.ItemsSource = palette == null ? null :
                new[] { new CharacterColorItem("无", Brushes.Transparent, null, "不使用挑染") }
                    .Concat(palette.Highlights.Select(CreateColorItem)).ToArray();
            EyeColors.SelectedIndex = palette == null ? -1 : GameCharacterPalette.DefaultEyeIndex;
            HighlightColors.SelectedIndex = palette == null ? -1 : 0;
            SetTribeColors(TribeColors.SelectedItem as GameCharacterTribe,
                GameCharacterPalette.DefaultSkinIndex, GameCharacterPalette.DefaultHairIndex);
        }
        finally { changingAppearance = false; }
    }

    private void ShowNpcAppearance(GameCharacterNpcPreset preset, string? tribe)
    {
        ShowCharacterAppearance(null);
        PlayerColors.Visibility = Visibility.Collapsed;
        NpcColors.Visibility = CharacterAppearance.Visibility = Visibility.Visible;
        NpcTribeName.Text = tribe ?? $"部族 #{preset.TribeId}";
        var colors = preset.Colors;
        NpcColorItems.ItemsSource = new[]
        {
            NpcColor("肤色", colors.Skin), NpcColor("发色", colors.Hair),
            NpcColor("瞳色", colors.Eyes), NpcColor("挑染", colors.Highlights)
        };
    }

    private static CharacterColorItem NpcColor(string label, GameRgb? color)
    {
        if (color is not { } rgb) return new($"{label} 无", Brushes.Transparent, null, label);
        var brush = new SolidColorBrush(Color.FromRgb(rgb.R, rgb.G, rgb.B));
        brush.Freeze();
        return new($"{label} {rgb.Hex}", brush, rgb, rgb.Hex);
    }

    private void SetTribeColors(GameCharacterTribe? tribe, int skin, int hair)
    {
        SkinColors.ItemsSource = tribe?.Skin.Select(CreateColorItem).ToArray();
        HairColors.ItemsSource = tribe?.Hair.Select(CreateColorItem).ToArray();
        SkinColors.SelectedIndex = tribe == null ? -1 : Math.Clamp(skin, 0, tribe.Skin.Count - 1);
        HairColors.SelectedIndex = tribe == null ? -1 : Math.Clamp(hair, 0, tribe.Hair.Count - 1);
    }

    private async void AppearanceTribeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (changingAppearance || activePalette == null || TribeColors.SelectedItem is not GameCharacterTribe tribe) return;
        changingAppearance = true;
        try { SetTribeColors(tribe, SkinColors.SelectedIndex, HairColors.SelectedIndex); }
        finally { changingAppearance = false; }
        await ApplySelectedTribeAsync(tribe);
    }

    private async void AppearanceColorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!changingAppearance && activePalette != null) await RecolorCharacterAsync();
    }

    private async void ResetAppearance(object sender, RoutedEventArgs e)
    {
        if (activePalette == null) return;
        changingAppearance = true;
        try
        {
            TribeColors.SelectedIndex = 0;
            SetTribeColors(activePalette.Tribes[0], GameCharacterPalette.DefaultSkinIndex, GameCharacterPalette.DefaultHairIndex);
            HighlightColors.SelectedIndex = 0;
            EyeColors.SelectedIndex = GameCharacterPalette.DefaultEyeIndex;
        }
        finally { changingAppearance = false; }
        await ApplySelectedTribeAsync(activePalette.Tribes[0]);
    }

    private GameCharacterPartOption? SelectedTribeDefaultFace() =>
        (TribeColors.SelectedItem as GameCharacterTribe)?.Faces.FirstOrDefault();

    private async Task ApplySelectedTribeAsync(GameCharacterTribe tribe)
    {
        var currentPath = desiredParts.GetValueOrDefault("fac")?.Path ?? scene?.SourceModel.FaceModelPath;
        if (activePalette != null && currentPath != null)
        {
            foreach (var group in activePalette.Tribes)
            {
                var ordinal = group.Faces.ToList().FindIndex(face => face.Path == currentPath);
                if (ordinal < 0 || ordinal >= tribe.Faces.Count) continue;
                var target = tribe.Faces[ordinal];
                desiredParts["fac"] = target;
                if (target.Path == currentPath) break;
                RefreshSlotButtons();
                UpdatePartChoices();
                await UpdateEquippedPreview();
                return;
            }
        }
        if (tribe.Faces.Count > 0 && tribe.Faces.All(face => face.Path != currentPath))
        {
            desiredParts["fac"] = tribe.Faces[0];
            RefreshSlotButtons();
            UpdatePartChoices();
            await UpdateEquippedPreview();
            return;
        }
        UpdatePartChoices();
        RefreshSlotButtons();
        await RecolorCharacterAsync();
    }

    private GameCharacterColors? ReadSelectedCharacterColors()
    {
        if (SkinColors.SelectedItem is not CharacterColorItem { Value: { } skin } ||
            HairColors.SelectedItem is not CharacterColorItem { Value: { } hair } ||
            EyeColors.SelectedItem is not CharacterColorItem { Value: { } eyes }) return null;
        return new(skin, hair, (HighlightColors.SelectedItem as CharacterColorItem)?.Value, eyes);
    }

    private async Task RecolorCharacterAsync()
    {
        if (scene is not { } preview || ReadSelectedCharacterColors() is not { } colors) return;
        recolorCancellation?.Cancel();
        if (colors == shownColors) { AppearanceStatus.Text = ""; return; }
        using var pending = new CancellationTokenSource();
        recolorCancellation = pending;
        var token = pending.Token;
        AppearanceStatus.Text = "正在更新…";
        try
        {
            var materials = await Task.Run(() =>
            {
                var updated = LocalGameSource.RecolorCharacterMaterials(preview.SourceModel, colors, token);
                var brushes = new PreviewMaterial?[updated.Count];
                // 材质槽可能共享同一张肤色贴图；只创建一份画刷。
                var cache = new Dictionary<GameModelMaterial, PreviewMaterial>(ReferenceEqualityComparer.Instance);
                for (var i = 0; i < updated.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var material = updated[i];
                    if (ReferenceEquals(material, preview.SourceModel.Materials[i])) continue;
                    if (!cache.TryGetValue(material, out var brush))
                        cache.Add(material, brush = BuildPreviewMaterial(material));
                    brushes[i] = brush;
                }
                return brushes;
            }, token);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(scene, preview)) return;
            for (var i = 0; i < materials.Length; i++)
                if (materials[i] is { } material) preview.Materials[i] = material;
            shownColors = colors;
            RenderScene();
            AppearanceStatus.Text = "";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { if (!token.IsCancellationRequested) AppearanceStatus.Text = "无法预览所选配色"; }
        finally { if (ReferenceEquals(recolorCancellation, pending)) recolorCancellation = null; }
    }

    private static CharacterColorItem CreateColorItem(GameCharacterColorChoice value)
    {
        var brush = new SolidColorBrush(Color.FromRgb(value.Swatch.R, value.Swatch.G, value.Swatch.B));
        brush.Freeze();
        return new($"#{value.Index:D3}", brush, value.Color, value.Swatch.Hex);
    }

    private sealed record CharacterColorItem(string Label, Brush Brush, GameRgb? Value, string Tooltip);
}
