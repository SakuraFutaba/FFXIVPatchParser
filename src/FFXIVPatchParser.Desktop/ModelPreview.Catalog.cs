using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace FFXIVPatchParser.Desktop;

public sealed class ModelTableLinkEventArgs(string sheet, uint rowId) : EventArgs
{
    public string Sheet { get; } = sheet;
    public uint RowId { get; } = rowId;
}

public partial class ModelPreview
{
    private CancellationTokenSource? namesDownload;
    private CancellationTokenSource? namedSelection;
    private NamedModelEntry? selectedNamedModel;
    private bool changingCatalogueMode;
    public event EventHandler<ModelTableLinkEventArgs>? RelatedTableRequested;

    private async void ModelModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (changingCatalogueMode || ModelSearch == null || NpcNamesButton == null) return;
        await ShowCatalogueMode();
    }

    private async Task ShowCatalogueMode()
    {
        filterCancellation?.Cancel();
        namedSelection?.Cancel();
        var raw = ModelMode.SelectedIndex == 2;
        NpcNamesButton.Visibility = raw ? Visibility.Collapsed : Visibility.Visible;
        ModelTree.ItemsSource = null;
        ModelSearch.IsEnabled = raw ? models != null : namedModels != null;
        BrowserStatus.Text = source == null ? "请选择游戏目录" : raw ? "点击「扫描资源索引」查找目录之外的模型" : "尚未读取";
        if (ModelSearch.IsEnabled) await ApplyModelFilter();
        else StartCatalogueIfVisible();
    }

    private async Task SelectCatalogueMode(int index)
    {
        changingCatalogueMode = true;
        try { ModelMode.SelectedIndex = index; }
        finally { changingCatalogueMode = false; }
        await ShowCatalogueMode();
    }

    public async Task<string> ScanUnknownAsync(CancellationToken token, IProgress<string>? progress)
    {
        if (source is not { } game) return "请选择游戏目录";
        scanCancellation?.Cancel();
        using var current = CancellationTokenSource.CreateLinkedTokenSource(token);
        scanCancellation = current;
        var charactersOnly = ScanAllModels.IsChecked != true;
        try
        {
            var result = await Task.Run(() => game.ListModelLocations(current.Token, progress: progress, charactersOnly: charactersOnly), current.Token);
            current.Token.ThrowIfCancellationRequested();
            models = result;
            await SelectCatalogueMode(2);
            return $"已扫描 {result.Count:N0} 个模型，含 {result.Count(model => model.Path.StartsWith("sqpack:", StringComparison.Ordinal)):N0} 个路径未识别模型";
        }
        finally { if (scanCancellation == current) scanCancellation = null; }
    }

    private void StartCatalogueIfVisible()
    {
        if (IsVisible && ShowBrowser && source != null && namedModels == null && ModelMode.SelectedIndex != 2 &&
            scanCancellation is not { IsCancellationRequested: false }) _ = ReadCatalogue();
    }

    private async void ImportModelPaths(object sender, RoutedEventArgs e)
    {
        if (source is not { } game) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入已知模型路径（每行一个游戏内路径）", Filter = "路径列表 (*.txt)|*.txt|所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;
        scanCancellation?.Cancel();
        ModelPathsButton.IsEnabled = false;
        try
        {
            var count = await Task.Run(() => game.ImportModelPaths(dialog.FileName));
            if (!ReferenceEquals(source, game)) return;
            models = null;
            if (ModelMode.SelectedIndex == 2)
            {
                ModelTree.ItemsSource = null;
                ModelSearch.IsEnabled = false;
            }
            BrowserStatus.Text = $"合并后共 {count:N0} 条候选路径。点击「扫描资源索引」按本地索引匹配。";
        }
        catch (Exception ex) { BrowserStatus.Text = "导入模型路径失败 · " + ex.Message; }
        finally { ModelPathsButton.IsEnabled = true; }
    }

    private static IReadOnlyList<PathTreeNode> BuildNamedTree(IReadOnlyList<NamedModelEntry> entries, bool byNumber,
        string filter, CancellationToken token)
    {
        var order = byNumber ? new[] { "怪物", "亚人", "武器" }
            : new[] { "宠物", "召唤兽", "坐骑", "时尚配饰", "战斗NPC", "事件NPC", "其他模型" };
        var comparer = StringComparer.Create(CultureInfo.GetCultureInfo("zh-CN"), ignoreCase: true);
        return PathTree.Build(entries, entry => TreePath(entry, byNumber), filter,
                entry => $"{entry.Name} {entry.Path} {entry.SourceSheet} {entry.SourceRowId} ModelChara {entry.ModelCharaId} {entry.NameSource}", token, comparer)
            .OrderBy(node => Array.IndexOf(order, node.Label) is var index && index >= 0 ? index : order.Length).ToArray();
    }

    private static string TreePath(NamedModelEntry entry, bool byNumber)
    {
        var name = entry.Name.Replace('/', '／').Replace('\\', '＼');
        var label = name.StartsWith('#') ? name : $"{name} · #{entry.SourceRowId}";
        if (!byNumber) return $"{entry.Category}/{label}";
        var parts = entry.Path.Split('/');
        var category = entry.ModelType switch { 3 => "怪物", 2 => "亚人", 4 => "武器", _ => "其他" };
        return $"{category}/{parts[2]}/{parts[5]}/v{entry.Variant:D4} · {label}";
    }

    private async Task OpenNamedModel(NamedModelEntry entry, PathTreeNode selected)
    {
        if (source is not { } game) return;
        namedSelection?.Cancel();
        using var pending = new CancellationTokenSource();
        namedSelection = pending;
        var variant = 1;
        string? warning = null;
        try
        {
            try { variant = await Task.Run(() => game.ResolveNamedModelMaterialVariant(entry, pending.Token), pending.Token); }
            catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException) { warning = ex.Message; }
            pending.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(ModelTree.SelectedItem, selected)) return;
            MaterialVariant.Text = variant.ToString(CultureInfo.InvariantCulture);
            await OpenAsync(entry.Path);
            if (!pending.IsCancellationRequested && ReferenceEquals(ModelTree.SelectedItem, selected))
            {
                var nameRow = entry.NameRowId is { } id ? $" · 名称行 #{id}" : "";
                ModelEntryInfo.Text = $"{entry.Name} · {entry.SourceSheet} #{entry.SourceRowId} → ModelChara #{entry.ModelCharaId} · v{entry.Variant:D4}"
                    + $"\n名称来源：{entry.NameSource}{nameRow}" + (warning == null ? "" : "\n材质变体使用默认值：" + warning);
                selectedNamedModel = entry;
                ModelTableLinks.Visibility = Visibility.Visible;
                NameTableLink.Visibility = entry.NameRowId != null ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested) { }
        finally { if (namedSelection == pending) namedSelection = null; }
    }

    private void OpenModelTable(object sender, RoutedEventArgs e)
    {
        if (selectedNamedModel is not { } entry || sender is not Button button) return;
        var link = (button.Tag as string) switch
        {
            "model" => new ModelTableLinkEventArgs("ModelChara", entry.ModelCharaId),
            "name" when entry.NameRowId is { } id => new ModelTableLinkEventArgs(
                entry.NameSource.Contains("ENpcResident", StringComparison.Ordinal) ? "ENpcResident" : "BNpcName", id),
            "source" => new ModelTableLinkEventArgs(entry.SourceSheet, entry.SourceRowId),
            _ => null
        };
        if (link != null) RelatedTableRequested?.Invoke(this, link);
    }

    private async void DownloadNpcNames(object sender, RoutedEventArgs e)
    {
        if (namesDownload is { IsCancellationRequested: false } running) { running.Cancel(); return; }
        using var pending = new CancellationTokenSource();
        namesDownload = pending;
        NpcNamesButton.Content = "取消补充";
        try
        {
            BrowserStatus.Text = "正在下载 NPC 名称映射…";
            var count = await NpcNameHints.DownloadAsync(pending.Token);
            var translations = 0;
            string? warning = null;
            try
            {
                BrowserStatus.Text = "正在对照中英文名称…";
                translations = await NpcNameHints.DownloadTranslationsAsync(pending.Token);
            }
            catch (Exception ex) when (!pending.IsCancellationRequested) { warning = ex.Message; }
            pending.Token.ThrowIfCancellationRequested();
            scanCancellation?.Cancel();
            await ReadCatalogue(refresh: true);
            BrowserStatus.Text += warning == null ? $" · {count:N0} 条名称映射、{translations:N0} 个英文对照" : " · 英文对照暂未更新";
            if (warning != null) BrowserStatus.ToolTip = warning;
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested) { }
        catch (Exception ex) { BrowserStatus.Text = "补充名称失败 · " + ex.Message; }
        finally
        {
            if (namesDownload == pending) namesDownload = null;
            NpcNamesButton.Content = "补充 NPC 名称";
        }
    }
}
