using System.Windows;
using System.Windows.Controls;

namespace FFXIVPatchParser.Desktop;

public partial class ModelPreview
{
    private sealed record RaceChoice(string? Code, string Label);
    private sealed record NpcChoice(GameCharacterNpcPreset Preset)
    {
        public string Label { get; } = $"{Preset.Name} · #{Preset.RowId}";
    }

    private bool showModelBrowser = true;
    private bool syncingCharacterRace;
    private CancellationTokenSource? npcListRequest;
    private NpcChoice[]? npcChoices;
    private bool CharacterMode => ShowBrowser && catalogueMode == ModelCatalogueMode.Character;

    private void InitializeCharacterLayout()
    {
        CharacterRace.ItemsSource = new[] { new RaceChoice(null, "选择种族…") }
            .Concat(LocalGameSource.CharacterPreviewCodes.Select(code => new RaceChoice(code, CharacterBodyLabel(code)))).ToArray();
        SynchronizeCharacterRace(null);
    }

    private void UpdateCharacterLayout()
    {
        Browser.Visibility = ShowBrowser && !CharacterMode ? Visibility.Visible : Visibility.Collapsed;
        CharacterSidebar.Visibility = CharacterMode ? Visibility.Visible : Visibility.Collapsed;
        ModelPathToolbar.Visibility = CharacterMode ? Visibility.Collapsed : Visibility.Visible;
        ModelToolbarRow.SharedSizeGroup = CharacterMode ? null : "ModelToolbar";
        CharacterSidebar.IsEnabled = source != null;
        if (!CharacterMode)
        {
            NpcAppearanceChoice.SelectedItem = null;
            NpcAppearanceStatus.Text = "";
        }
        if (scene == null && loadCancellation == null) EmptyViewport.Text = EmptyMessage;
    }

    private void SynchronizeCharacterRace(string? code)
    {
        syncingCharacterRace = true;
        try
        {
            CharacterRace.SelectedItem = CharacterRace.Items.Cast<RaceChoice>().FirstOrDefault(race => race.Code == code);
            if (code != null)
            {
                SetAppearancePicker(npc: false);
                NpcAppearanceChoice.SelectedItem = null;
                NpcAppearanceStatus.Text = "";
            }
        }
        finally { syncingCharacterRace = false; }
    }

    private async void CharacterRaceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncingCharacterRace || !CharacterMode || source == null || CharacterRace.SelectedItem is not RaceChoice race) return;
        if (race.Code == null)
        {
            if (NpcAppearanceChoice.SelectedItem == null)
            {
                loadCancellation?.Cancel();
                loadCancellation = null;
                ClearScene();
                ModelPath.Clear();
                EmptyViewport.Text = EmptyMessage;
                SetModelStatus("");
            }
            return;
        }
        NpcAppearanceChoice.SelectedItem = null;
        NpcAppearanceStatus.Text = "";
        MaterialVariant.Text = "1";
        await OpenAsync("character:" + race.Code);
    }

    private void ResetNpcPicker()
    {
        npcListRequest?.Cancel();
        npcChoices = null;
        NpcAppearanceChoice.ItemsSource = null;
        NpcAppearanceSearch.Clear();
        NpcAppearanceStatus.Text = "";
        SetAppearancePicker(npc: false);
    }

    private void SetAppearancePicker(bool npc)
    {
        (npc ? NpcPickerTab : RacePickerTab).IsChecked = true;
        RaceAppearancePicker.Visibility = npc ? Visibility.Collapsed : Visibility.Visible;
        NpcAppearancePicker.Visibility = npc ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void SwitchAppearancePicker(object sender, RoutedEventArgs e)
    {
        var npc = sender == NpcPickerTab;
        SetAppearancePicker(npc);
        if (!npc || !CharacterMode || source is not { } game || npcChoices != null ||
            npcListRequest is { IsCancellationRequested: false }) return;
        using var request = new CancellationTokenSource();
        npcListRequest = request;
        var token = request.Token;
        NpcAppearanceStatus.Text = "正在读取 NPC 外观…";
        try
        {
            var choices = await Task.Run(() => game.ListCharacterNpcPresets(token: token).Select(preset => new NpcChoice(preset)).ToArray(), token);
            token.ThrowIfCancellationRequested();
            npcChoices = choices;
            ApplyNpcFilter();
            NpcAppearanceStatus.Text = choices.Length == 0 ? "当前游戏构建没有可预览的 NPC 外观。" : "";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested) NpcAppearanceStatus.Text = $"无法读取 NPC 外观 · {ex.Message}";
        }
        finally { if (npcListRequest == request) npcListRequest = null; }
    }

    private void FilterNpcAppearance(object sender, TextChangedEventArgs e) => ApplyNpcFilter();

    private void NpcAppearanceSearchKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter || NpcAppearanceChoice.Items.Count == 0) return;
        e.Handled = true;
        if (NpcAppearanceChoice.Items.Count == 1) NpcAppearanceChoice.SelectedIndex = 0;
        else
        {
            NpcAppearanceChoice.Focus();
            NpcAppearanceChoice.IsDropDownOpen = true;
        }
    }

    private void ApplyNpcFilter()
    {
        if (npcChoices == null) return;
        var query = NpcAppearanceSearch.Text.Trim();
        NpcAppearanceChoice.ItemsSource = query.Length == 0 ? npcChoices : npcChoices.Where(choice =>
            choice.Label.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            choice.Preset.Code.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private async void NpcAppearanceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!CharacterMode || source is not { } game || NpcAppearanceChoice.SelectedItem is not NpcChoice choice) return;
        loadCancellation?.Cancel();
        using var request = new CancellationTokenSource();
        loadCancellation = request;
        var token = request.Token;
        var preset = choice.Preset;
        ClearScene(keepAppearanceControls: true);
        SynchronizeCharacterRace(null);
        ModelPath.Text = $"npc:{preset.RowId}";
        EmptyViewport.Text = "正在读取 NPC 模型…";
        NpcAppearanceStatus.Text = "正在组装 NPC 外观…";
        try
        {
            var godbert = GodbertAnimationSettings.ReadDirectory();
            var (preview, parts, tribe) = await Task.Run(() =>
            {
                var assembled = BuildScene(game.ReadCharacterNpcPreview(preset, token, godbert), token);
                var equipment = game.DescribeCharacterNpcEquipment(preset, token);
                var clan = game.ReadCharacterPalette(preset.Code[..3] + "01", token)?.Tribes
                    .FirstOrDefault(option => option.Id == preset.TribeId)?.Name;
                return (assembled, equipment, clan);
            }, token);
            token.ThrowIfCancellationRequested();
            ShowNpcAppearance(preset, tribe);
            ShowNpcParts(preset, parts);
            ShowScene(preview);
            MaterialVariant.IsEnabled = false;
            ModelEntryInfo.Text = $"{choice.Label} · {preset.Code}";
            NpcAppearanceStatus.Text = $"{choice.Label} · {preset.Code}";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                ClearScene();
                NpcAppearanceStatus.Text = $"无法预览 NPC · {ex.Message}";
                EmptyViewport.Text = "模型读取失败";
            }
        }
        finally { if (loadCancellation == request) loadCancellation = null; }
    }
}
