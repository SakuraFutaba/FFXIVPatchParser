using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace FFXIVPatchParser.Desktop;

public partial class ModelPreview
{
    private Task<BitmapSource?>? dyeGlyph;

    private CancellationTokenSource? itemDyeCancellation;

    private void ShowStandaloneDyes(GameCharacterPartOption part, int variant)
    {
        ItemDyePanel.Visibility = part.DyeCount == 0 ? Visibility.Collapsed : Visibility.Visible;
        ItemDyeControls.Children.Clear();
        AddDyeControls(ItemDyeControls, part, updated => ApplyStandaloneDyes(updated, variant));
    }

    private async Task ApplyStandaloneDyes(GameCharacterPartOption part, int variant)
    {
        if (scene is not { } preview || source is not { } game) return;
        itemDyeCancellation?.Cancel();
        using var pending = new CancellationTokenSource();
        itemDyeCancellation = pending;
        var token = pending.Token;
        ShowStandaloneDyes(part, variant);
        try
        {
            var materials = await Task.Run(() => game.ReadItemDyeMaterials(preview.SourceModel, part, variant, token)
                .Select(BuildPreviewMaterial).ToArray(), token);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(scene, preview)) return;
            materials.CopyTo(preview.Materials, 0);
            RenderScene();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) SetModelStatus(ex.Message, error: true); }
        finally { if (itemDyeCancellation == pending) itemDyeCancellation = null; }
    }

    private static SolidColorBrush StainBrush(GameStain stain)
    {
        var rgb = stain.Color;
        return new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
    }

    private void AddDyeControls(Grid sprite, GameCharacterPartOption part, Func<GameCharacterPartOption, Task>? standalone = null)
    {
        if (source is not { } game || part.DyeCount == 0) return;
        dyeGlyph ??= Task.Run(() => game.ReadCharacterDyeGlyph() is { } glyph ? CreateBitmap(glyph, PixelFormats.Bgra32) : null);
        for (int index = 0; index < Math.Min(2, (int)part.DyeCount); index++)
        {
            var channel = index;
            var current = channel == 0 ? part.Stain0Id : part.Stain1Id;
            var image = new Image { Width = 18, Height = 18 };
            _ = SetSlotFrame(image, dyeGlyph);
            var selectedStain = current == 0 ? null : game.ListCharacterStains().FirstOrDefault(stain => stain.Id == current);
            var button = new Button
            {
                Content = image, Width = 18, Height = 18, Padding = new Thickness(0), BorderThickness = new Thickness(0),
                Background = selectedStain == null ? Brushes.Transparent : StainBrush(selectedStain), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = standalone == null ? new Thickness(0, channel * 16, 0, 0) : new Thickness(0, 0, (part.DyeCount - channel - 1) * 23, 0), ToolTip = $"选择染色区 {channel + 1}（{selectedStain?.Name ?? "原色"}）"
            };
            button.Click += (_, e) =>
            {
                e.Handled = true;
                var menu = new ContextMenu { PlacementTarget = button };
                foreach (var stain in game.ListCharacterStains())
                {
                    var item = new MenuItem { Header = stain.Name, IsChecked = stain.Id == current,
                        Icon = new Rectangle { Width = 12, Height = 12, Fill = StainBrush(stain) } };
                    item.Click += async (_, _) =>
                    {
                        var updated = channel == 0 ? part with { Stain0Id = stain.Id } : part with { Stain1Id = stain.Id };
                        if (standalone != null) { await standalone(updated); return; }
                        if (VisiblePart(part.Slot) is not { } selected || selected != part) return;
                        desiredParts[part.Slot] = updated;
                        RefreshSlotButtons();
                        UpdatePartChoices();
                        await UpdateEquippedPreview();
                    };
                    menu.Items.Add(item);
                }
                button.ContextMenu = menu;
                menu.IsOpen = true;
            };
            sprite.Children.Add(button);
        }
    }
}
