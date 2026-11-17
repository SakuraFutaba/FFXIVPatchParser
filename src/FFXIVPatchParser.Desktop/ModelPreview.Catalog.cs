using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

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
    private Window? catalogueOptionsHost;
    public event EventHandler<ModelTableLinkEventArgs>? RelatedTableRequested;

    private void ToggleCatalogueOptions(object sender, RoutedEventArgs e)
    {
        if (CatalogueOptionsPopup.IsOpen) CloseCatalogueOptions();
        else if (IsVisible && IsEnabled && ShowBrowser) CatalogueOptionsPopup.IsOpen = true;
    }

    private void CatalogueOptionsOpened(object? sender, EventArgs e)
    {
        if (Window.GetWindow(this) is { } owner)
        {
            catalogueOptionsHost = owner;
            owner.PreviewMouseDown += DismissCatalogueOptionsOnOutsideClick;
            owner.PreviewKeyDown += CatalogueOptionsKeyDown;
            owner.Deactivated += DismissCatalogueOptions;
            owner.LocationChanged += DismissCatalogueOptions;
            owner.SizeChanged += DismissCatalogueOptions;
        }
        ModelMode.Focus();
    }

    private void CatalogueOptionsClosed(object? sender, EventArgs e)
    {
        ModelMode.IsDropDownOpen = false;
        if (catalogueOptionsHost is not { } owner) return;
        catalogueOptionsHost = null;
        owner.PreviewMouseDown -= DismissCatalogueOptionsOnOutsideClick;
        owner.PreviewKeyDown -= CatalogueOptionsKeyDown;
        owner.Deactivated -= DismissCatalogueOptions;
        owner.LocationChanged -= DismissCatalogueOptions;
        owner.SizeChanged -= DismissCatalogueOptions;
    }

    private void DismissCatalogueOptionsOnOutsideClick(object sender, MouseButtonEventArgs e)
    {
        if (!CatalogueOptionsPopup.IsOpen || CatalogueOptions.IsMouseOver || CatalogueOptionsPanel.IsMouseOver) return;
        if (ModelMode.IsDropDownOpen && ModelMode.Template?.FindName("PART_Popup", ModelMode) is Popup { Child: UIElement { IsMouseOver: true } })
            return;
        CloseCatalogueOptions();
    }

    private void CatalogueOptionsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !CatalogueOptionsPopup.IsOpen) return;
        CloseCatalogueOptions();
        CatalogueOptions.Focus();
        e.Handled = true;
    }

    private void DismissCatalogueOptions(object? sender, EventArgs e) => CloseCatalogueOptions();

    private void CloseCatalogueOptions()
    {
        if (CatalogueOptionsPopup is { IsOpen: true }) CatalogueOptionsPopup.IsOpen = false;
    }

    private async void ModelModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (changingCatalogueMode || ModelSearch == null || NpcNamesButton == null) return;
        await ShowCatalogueMode();
    }

    private async Task ShowCatalogueMode()
    {
        filterCancellation?.Cancel();
        namedSelection?.Cancel();
        scanCancellation?.Cancel();
        var raw = ModelMode.SelectedIndex == 2;
        NpcNamesButton.Visibility = raw ? Visibility.Collapsed : Visibility.Visible;
        PathTreeView.Reset(ModelTree);
        ModelSearch.IsEnabled = raw ? models != null : namedModels != null;
        BrowserStatus.Text = source == null ? "请选择游戏目录" : raw ? "正在读取已知模型路径…" : "尚未读取";
        if (ModelSearch.IsEnabled) await ApplyModelFilter();
        StartCatalogueIfVisible();
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
            modelCatalogRevision = game.MediaCatalogRevision;
            await SelectCatalogueMode(2);
            return $"已扫描 {result.Count:N0} 个模型，含 {result.Count(model => model.Path.StartsWith("sqpack:", StringComparison.Ordinal)):N0} 个路径未识别模型";
        }
        finally { if (scanCancellation == current) scanCancellation = null; }
    }

    private int modelCatalogRevision = -1;

    private void StartCatalogueIfVisible()
    {
        if (!IsVisible || !ShowBrowser || source == null || scanCancellation is { IsCancellationRequested: false }) return;
        if (ModelMode.SelectedIndex == 2)
        {
            if (models == null || modelCatalogRevision != source.MediaCatalogRevision) _ = ReadKnownModelPaths();
        }
        else if (namedModels == null) _ = ReadCatalogue();
    }

    private async Task ReadKnownModelPaths()
    {
        if (source is not { } game) return;
        using var current = new CancellationTokenSource();
        scanCancellation = current;
        var revision = game.MediaCatalogRevision;
        BrowserStatus.Text = "正在读取已知模型路径…";
        try
        {
            var result = await Task.Run(() => game.ListKnownLocalMediaLocations("model", current.Token), current.Token);
            current.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(source, game)) return;
            models = result;
            modelCatalogRevision = revision;
            if (ModelMode.SelectedIndex == 2)
            {
                ModelSearch.IsEnabled = true;
                await ApplyModelFilter();
            }
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!current.IsCancellationRequested && ReferenceEquals(source, game)) BrowserStatus.Text = "读取模型目录失败 · " + ex.Message;
        }
        finally { if (scanCancellation == current) scanCancellation = null; }
    }

    private async void ScanUnknownModels(object sender, RoutedEventArgs e)
    {
        if (source == null) return;
        UnknownModelsButton.IsEnabled = false;
        try { BrowserStatus.Text = await ScanUnknownAsync(CancellationToken.None, new Progress<string>(text => BrowserStatus.Text = text)); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { BrowserStatus.Text = "扫描模型失败 · " + ex.Message; }
        finally { UnknownModelsButton.IsEnabled = true; }
    }

    private async void ImportModelPaths(object sender, RoutedEventArgs e)
    {
        if (source is not { } game) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入已知模型路径（每行一个游戏内路径）", Filter = "路径列表 (*.txt)|*.txt|所有文件 (*.*)|*.*"
        };
        CloseCatalogueOptions();
        var owner = Window.GetWindow(this);
        var accepted = owner == null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        if (accepted != true || !ReferenceEquals(source, game)) return;
        scanCancellation?.Cancel();
        ModelPathsButton.IsEnabled = false;
        try
        {
            var count = await Task.Run(() => game.ImportModelPaths(dialog.FileName));
            if (!ReferenceEquals(source, game)) return;
            models = null;
            if (ModelMode.SelectedIndex == 2)
            {
                filterCancellation?.Cancel();
                PathTreeView.Reset(ModelTree);
                ModelSearch.IsEnabled = false;
            }
            BrowserStatus.Text = $"合并后共 {count:N0} 条候选路径，正在匹配本地索引。";
            await SelectCatalogueMode(2);
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
        var tree = PathTree.Build(entries, entry => TreePath(entry, byNumber), filter,
                entry => $"{entry.Name} {entry.Path} {entry.SourceSheet} {entry.SourceRowId} ModelChara {entry.ModelCharaId} {entry.NameSource}", token, comparer);
        return SortModelNodes(tree, byNumber, comparer, token)
            .OrderBy(node => Array.IndexOf(order, node.Label) is var index && index >= 0 ? index : order.Length).ToArray();
    }

    private static IReadOnlyList<PathTreeNode> SortModelNodes(IReadOnlyList<PathTreeNode> nodes, bool byNumber,
        IComparer<string> labels, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return nodes.Select(node => node.IsDirectory
                ? new PathTreeNode(node.FullPath, node.Label, node.Payload,
                    SortModelNodes(node.Children, byNumber, labels, token), true, node.IsExpanded)
                : node)
            .OrderBy(node => node, Comparer<PathTreeNode>.Create((left, right) =>
            {
                var kind = right.IsDirectory.CompareTo(left.IsDirectory);
                if (kind != 0) return kind;
                if (left.Payload is NamedModelEntry a && right.Payload is NamedModelEntry b)
                {
                    var variant = byNumber ? a.Variant.CompareTo(b.Variant) : 0;
                    if (variant != 0) return variant;
                    var id = a.SourceRowId.CompareTo(b.SourceRowId);
                    if (id != 0) return id;
                }
                else if (byNumber && left.Label.Length > 1 && right.Label.Length > 1 &&
                    uint.TryParse(left.Label.AsSpan(1), out var aNumber) &&
                    uint.TryParse(right.Label.AsSpan(1), out var bNumber))
                {
                    var number = aNumber.CompareTo(bNumber);
                    if (number != 0) return number;
                }
                var label = labels.Compare(left.Label, right.Label);
                return label != 0 ? label : StringComparer.Ordinal.Compare(left.FullPath, right.FullPath);
            })).ToArray();
    }

    private static string TreePath(NamedModelEntry entry, bool byNumber)
    {
        var name = entry.Name.Replace('/', '／').Replace('\\', '＼');
        var label = name.StartsWith('#') || name == $"{entry.SourceSheet} #{entry.SourceRowId}" ? name : $"{name} · #{entry.SourceRowId}";
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
            if (!PathTreeView.IsCurrentSelection(ModelTree, selected)) return;
            MaterialVariant.Text = variant.ToString(CultureInfo.InvariantCulture);
            await OpenAsync(entry.Path);
            if (!pending.IsCancellationRequested && PathTreeView.IsCurrentSelection(ModelTree, selected))
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
