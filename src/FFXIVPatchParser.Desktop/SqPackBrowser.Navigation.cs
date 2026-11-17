namespace FFXIVPatchParser.Desktop;

public partial class SqPackBrowser
{
    internal async Task ShowAudioFromPlayerAsync(GameAudio? selected)
    {
        PageRequested?.Invoke(SqPackPage.Audio);
        if (selected is not null && await EnsureGameForNavigation()) await LocalAudioPanel.RevealAsync(selected);
    }

    internal event Action? NavigationStarting;
    internal string? CurrentTexturePath { get; private set; }
    internal Task WaitForCurrentWorkAsync() => runningWork ?? Task.CompletedTask;

    internal Task RestoreTextureLocationAsync(string? path)
    {
        if (CurrentTexturePath == path) return Task.CompletedTask;
        if (path != null) return ShowTextureAsync(path);
        CurrentTexturePath = null;
        TexturePath.Clear();
        LocalTexturePreview.SetImage(null);
        return Task.CompletedTask;
    }

    internal (RowKey? Row, int Column) CurrentTableCell() =>
        (TableGrid.CurrentCell.Item is TableRow row ? row.Id : null, (TableGrid.CurrentCell.Column?.DisplayIndex ?? 0) - 1);

    internal async Task RestoreTableLocationAsync(string? sheet, RowKey? key, int column)
    {
        if (sheet == null)
        {
            SaveLocalTableTab();
            selectedTable = null;
            ResetTable();
            RenderLocalTableTabs();
            return;
        }
        if (!await EnsureGameForNavigation()) return;
        await OpenLocalTableAsync(sheet);
        if (key is not { } id) return;
        if (CurrentTableName == sheet && tablePage?.Rows.FirstOrDefault(row => row.Id == id) is { } found && TableGrid.Columns.Count > 0)
            SelectTableCell(found, Math.Clamp(column + 1, 0, TableGrid.Columns.Count - 1), true);
        else await JumpToCellAsync(sheet, id, column);
    }
}
