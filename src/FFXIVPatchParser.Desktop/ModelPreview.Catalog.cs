using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace FFXIVPatchParser.Desktop;

internal enum ModelCatalogueMode { ResourceType, Number, Character }

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
    private ModelCatalogueMode catalogueMode;
    internal event Action<ModelCatalogueMode>? CatalogueModeChanged;
    private Window? catalogueOptionsHost;
    public event EventHandler<ModelTableLinkEventArgs>? RelatedTableRequested;

    private static readonly string[] AssetCategories =
        ["角色部件", "武器", "防具", "饰品", "怪物", "亚人", "家具与住宅", "场景", "共享场景", "过场", "其他角色模型", "路径未识别"];
    private static readonly string[] CreatureCategories =
        ["战斗 NPC", "事件 NPC", "坐骑", "时尚配饰", "召唤兽", "宠物", "未关联来源"];
    private static readonly string[] CharacterRaces =
        ["中原之民", "高地之民", "精灵族", "猫魅族", "鲁加族", "拉拉菲尔族", "敖龙族", "硌狮族", "维埃拉族"];

    private IReadOnlyList<GameCharacterPartOption> itemOptions = [];
    private IReadOnlyDictionary<string, string> assetNames = new Dictionary<string, string>();

    private static string? GearFamily(string path)
    {
        if (path.StartsWith("chara/weapon/", StringComparison.Ordinal)) return "武器";
        if (path.StartsWith("chara/equipment/", StringComparison.Ordinal)) return "防具";
        if (path.StartsWith("chara/accessory/", StringComparison.Ordinal)) return "饰品";
        return null;
    }

    private static IReadOnlyList<PathTreeNode> BuildNumberTree(IReadOnlyList<NamedModelEntry> named,
        IReadOnlyList<SqPackModelLocation> raw, IReadOnlyList<GameCharacterPartOption> options, string query, CancellationToken token)
    {
        var known = named.Where(x => x.ModelType != 2).Select(x => x.Path).ToHashSet(StringComparer.Ordinal);
        var candidates = raw.Concat(options.Select(x => new SqPackModelLocation(x.Path, "", 0, 0)))
            .DistinctBy(x => x.Path).Where(x => !known.Contains(x.Path) && NumberPath(x.Path) != null);
        var extra = PathTree.Build(candidates, x => x.Path, query, null, token,
            displayPathSelector: path => NumberPath(path)!);
        string[] families = ["怪物", "亚人", "武器", "防具", "饰品", "角色部件"];
        return MergeCatalogNodes(BuildNamedNumberTree(named, query, token).Concat(extra), token)
            .OrderBy(n => Array.IndexOf(families, n.Label)).ToArray();

        static string? NumberPath(string path)
        {
            var parts = path.Split('/');
            if (parts.Length < 4 || parts[0] != "chara") return null;
            var family = parts[1] switch { "human" => "角色部件", "monster" => "怪物", "demihuman" => "亚人", _ => GearFamily(path) };
            return family == null ? null : $"{family}/{string.Join('/', parts.Skip(2).Where(p => p is not ("model" or "obj" or "body" or "equipment")))}";
        }
    }

    private static IReadOnlyList<PathTreeNode> MergeCatalogNodes(IEnumerable<PathTreeNode> nodes, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var list = nodes.ToArray();
            var directories = list.Where(n => n.IsDirectory).GroupBy(n => n.FullPath)
                .Select(g => g.Count() == 1 ? g.First() : new PathTreeNode(g.Key, g.First().Label, null,
                    MergeCatalogNodes(g.SelectMany(n => n.Children), token), true, g.Any(n => n.IsExpanded)))
                .OrderBy(n => n.Label, StringComparer.Ordinal);
            return directories.Concat(list.Where(n => !n.IsDirectory)).ToArray();
        }

    private static IReadOnlyList<PathTreeNode> BuildResourceTree(IReadOnlyList<SqPackModelLocation> raw,
        IReadOnlyList<GameCharacterPartOption> options, IReadOnlyList<NamedModelEntry> named,
        IReadOnlyDictionary<string, string> labels, string query, CancellationToken token)
    {
        var creatures = named.Where(entry => entry.ModelType is 2 or 3).ToList();
        var knownMonsters = creatures.Where(entry => entry.ModelType == 3).Select(entry => entry.Path).ToHashSet(StringComparer.Ordinal);
        var demiNumbers = creatures.Where(entry => entry.ModelType == 2).Select(entry => (entry.ModelId, entry.BodyId)).ToHashSet();
        var gearKeys = options.Select(option => GearKey(option.Path)).ToHashSet(StringComparer.Ordinal);
        var other = new List<SqPackModelLocation>();
        foreach (var entry in raw)
        {
            token.ThrowIfCancellationRequested();
            if (knownMonsters.Contains(entry.Path)) continue;
            if (TryDemiHumanParts(entry.Path, out var model, out var equipment))
            {
                if (demiNumbers.Add((model, equipment)))
                    creatures.Add(new("其他模型", $"d{model:D4} · e{equipment:D4}", "", 0, 0, 2,
                        model, equipment, 0, entry.Path, "装备部件组合"));
                continue;
            }
            if (GearFamily(entry.Path) != null && gearKeys.Contains(GearKey(entry.Path))) continue;
            other.Add(entry);
        }

        var creatureTree = PathTree.Build(creatures, CreatureDisplayPath, query,
            entry => $"{entry.Path} {entry.SourceSheet} {entry.SourceRowId} ModelChara {entry.ModelCharaId} {entry.NameSource}", token,
            StringComparer.Create(CultureInfo.GetCultureInfo("zh-CN"), ignoreCase: true));
        var assets = PathTree.Build(other, entry => entry.Path, query, entry => entry.Container, token,
            displayPathSelector: path => AssetModelPath(path, labels));

        var items = options.Where(option => option.RowId.HasValue && GearFamily(option.Path) != null)
            .DistinctBy(option => option.RowId);
        var gear = PathTree.Build(items, x => $"{GearFamily(x.Path)}/{x.RowId:D8}", query, x => x.Label + " " + x.Path,
            token, leafLabelSelector: x => x.Label);
        return MergeCatalogNodes(creatureTree.Concat(assets).Concat(gear), token)
            .Select(category =>
            {
                if (category.Label == "家具与住宅") return category;
                var children = category.Label switch
                {
                    "怪物" or "亚人" => category.Children.OrderBy(child => CategoryOrder(CreatureCategories, child.Label)),
                    "武器" or "防具" or "饰品" => category.Children.OrderBy(child =>
                        (child.Payload as GameCharacterPartOption)?.RowId ?? uint.MaxValue),
                    _ => category.Children.AsEnumerable()
                };
                if (category.Label is "怪物" or "亚人" or "武器" or "防具" or "饰品")
                    children = children.Select(child => child.IsDirectory ? CatalogueCategory(child, child.Children) : child);
                return CatalogueCategory(category, children.ToArray());
            })
            .OrderBy(category => CategoryOrder(AssetCategories, category.Label)).ToArray();
    }

    private static PathTreeNode CatalogueCategory(PathTreeNode node, IReadOnlyList<PathTreeNode> children)
        => new(node.FullPath, node.Label, node.Payload, children, true, node.IsExpanded) { CanCompactWithChild = false };

    private static int CategoryOrder(string[] order, string label)
        => Array.IndexOf(order, label) is var index && index >= 0 ? index : order.Length;

    private static string LabelSegment(string text) => text.Replace('/', '／').Replace('\\', '＼');

    private static string CreatureDisplayPath(NamedModelEntry entry)
    {
        var demiHuman = entry.ModelType == 2;
        var category = entry.Category switch
        {
            "战斗NPC" => "战斗 NPC",
            "事件NPC" => "事件 NPC",
            "其他模型" => CreatureSourceByNumber(demiHuman ? 0 : entry.ModelId),
            _ => entry.Category
        };
        var name = LabelSegment(entry.Name);
        var label = entry.ModelCharaId == 0 ? $"{name} · 组合模型"
            : entry.SourceSheet == "ModelChara" ? $"{name} · {(demiHuman ? 'd' : 'm')}{entry.ModelId:D4} · v{entry.Variant:D4}"
            : $"{name} · #{entry.SourceRowId} · v{entry.Variant:D4}";
        return $"{(demiHuman ? "亚人" : "怪物")}/{category}/{label}";
    }

    private static string CreatureSourceByNumber(int model) => (model / 1000) switch
    {
        6 => "时尚配饰",
        7 => "召唤兽",
        8 => "宠物",
        _ => "未关联来源"
    };

    private static bool TryDemiHumanParts(string path, out int model, out int equipment)
    {
        model = equipment = 0;
        if (!path.StartsWith("chara/demihuman/", StringComparison.Ordinal) ||
            path.Split('/') is not ["chara", "demihuman", var modelId, "obj", "equipment", var equipmentId, "model", var file] ||
            modelId.Length != 5 || modelId[0] != 'd' || equipmentId.Length != 5 || equipmentId[0] != 'e' ||
            !file.StartsWith(modelId + equipmentId + "_", StringComparison.Ordinal)) return false;
        return file[11..] is "met.mdl" or "top.mdl" or "glv.mdl" or "dwn.mdl" or "sho.mdl"
            && int.TryParse(modelId.AsSpan(1), out model) && int.TryParse(equipmentId.AsSpan(1), out equipment);
    }

    private static string GearKey(string path)
    {
        var parts = path.Split('/');
        if (parts is ["chara", "weapon", var weapon, "obj", "body", var body, "model", var file])
            return file == weapon + body + ".mdl" ? $"{weapon}/{body}" : path;
        if (parts is ["chara", "equipment" or "accessory", var model, "model", var piece])
        {
            var suffix = piece.LastIndexOf('_');
            if (suffix >= 0)
            {
                var slot = piece[(suffix + 1)..];
                return $"{model}/{(slot == "ril.mdl" ? "rir.mdl" : slot)}";
            }
        }
        return path;
    }

    private async Task OpenItemModel(GameCharacterPartOption option, PathTreeNode selected)
    {
        if (source is not { } game) return;
        try
        {
            var (variant, mask) = await Task.Run(() => game.ResolveItemPartVariant(option));
            if (!StillSelected()) return;
            MaterialVariant.Text = variant.ToString(CultureInfo.InvariantCulture);
            await OpenAsync(option.Path, startIdle: false, imcMask: mask);
            if (scene == null || ModelPath.Text != option.Path || !StillSelected()) return;
            ShowItemInfo($"{option.Label} · IMC {option.ImcSubset} · v{variant:D4}");
            ShowStandaloneDyes(option, variant);
            BeginItemEffects(game, option);
        }
        catch (Exception ex)
        {
            if (!StillSelected()) return;
            ClearScene();
            ModelPath.Text = option.Path;
            ShowItemInfo($"{option.Label} · IMC {option.ImcSubset}");
            SetModelStatus(ex.Message, error: true);
        }

        bool StillSelected() => ReferenceEquals(game, source) && PathTreeView.IsCurrentSelection(ModelTree, selected);

        void ShowItemInfo(string text)
        {
            Inspector.Visibility = Visibility.Visible;
            ModelEntryInfo.Text = text;
            var other = itemOptions.FirstOrDefault(x => x.RowId == option.RowId && x != option);
            ItemHandButton.Tag = other;
            ItemHandButton.Content = other?.Slot == "offhand" ? "查看副手" : "查看主手";
            ItemHandButton.Visibility = other == null ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private async void SwitchItemHand(object sender, RoutedEventArgs e)
    {
        if (ItemHandButton.Tag is GameCharacterPartOption option && ModelTree.SelectedItem is PathTreeNode selected)
            await OpenItemModel(option, selected);
    }

    private static string AssetModelPath(string path, IReadOnlyDictionary<string, string> labels)
    {
        const string characterPrefix = "character:";
        if (path.StartsWith(characterPrefix, StringComparison.Ordinal))
            return $"角色部件/{CharacterBodyLabel(path[characterPrefix.Length..])}/预览角色";
        if (path.StartsWith("sqpack:", StringComparison.OrdinalIgnoreCase)) return "路径未识别/" + path[7..].Replace("@", "/@");
        var parts = path.Split('/');
        if (parts.Length < 3) return path;
        if (parts[0] != "chara")
        {
            var category = parts[0] switch
            {
                "bg" => "场景",
                "bgcommon" when parts[1] == "hou" => "家具与住宅",
                "bgcommon" => "共享场景",
                "cut" => "过场",
                _ => parts[0]
            };
            if (labels.TryGetValue(path, out var name))
                parts[^1] = $"{LabelSegment(name)} · {parts[^1]}";
            return $"{category}/{string.Join('/', parts, 1, parts.Length - 1)}";
        }

        var file = parts[^1];
        if (parts[1] is "equipment" or "accessory")
        {
            var slot = Path.GetFileNameWithoutExtension(file).Split('_')[^1] switch
            {
                "met" => "头部", "top" => "身体", "glv" => "手部", "dwn" => "腿部", "sho" => "脚部",
                "ear" => "耳饰", "nek" => "项链", "wrs" => "手镯", "rir" => "右戒指", "ril" => "左戒指",
                _ => "其他部位"
            };
            return $"{(parts[1] == "equipment" ? "防具" : "饰品")}/未关联物品/{slot}/{parts[2]}/{file}";
        }
        if (parts is ["chara", "human", var race, "obj", var component, var id, ..])
        {
            var part = component switch { "body" => "躯体", "face" => "脸", "hair" => "发型", "tail" => "尾巴", "zear" => "耳朵", _ => component };
            if (component == "hair" && labels.TryGetValue(path, out var name)) id += " · " + LabelSegment(name);
            return $"角色部件/{CharacterBodyLabel(race)}/{part}/{id}/{file}";
        }
        var group = parts[1] switch { "weapon" => "武器", "monster" => "怪物", "demihuman" => "亚人", _ => null };
        var compactPath = SqPackBrowser.CompactModelPath(path);
        if (group == null) return $"其他角色模型/{compactPath["chara/".Length..]}";
        var branch = group == "武器" ? "未关联物品" : group == "怪物" && parts[2].StartsWith('m') &&
            int.TryParse(parts[2].AsSpan(1), out var number) ? CreatureSourceByNumber(number) : "未关联来源";
        return $"{group}/{branch}/{compactPath[("chara//".Length + parts[1].Length)..]}";
    }

    private static string CharacterBodyLabel(string code)
    {
        if (code.Length != 5 || code[0] != 'c' || !int.TryParse(code.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ||
            id % 100 != 1 || id / 100 is < 1 or > 18) return code;
        var index = id / 100 - 1;
        return $"{code} · {CharacterRaces[index / 2]}{(index % 2 == 0 ? "男" : "女")}";
    }

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
        if (NpcNamesButton.IsVisible) NpcNamesButton.Focus();
        else CatalogueScanOptions.Focus();
    }

    private void CatalogueOptionsClosed(object? sender, EventArgs e)
    {
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

    internal async Task SelectCatalogueMode(ModelCatalogueMode mode, bool refresh = false)
    {
        if (mode == catalogueMode && !refresh) return;
        var wasCharacter = catalogueMode == ModelCatalogueMode.Character;
        var isCatalogue = mode != ModelCatalogueMode.Character;
        if (mode != catalogueMode)
        {
            restoreCataloguePreview = isCatalogue;
            PathTreeView.SwitchCatalogue(ModelTree, (int)catalogueMode, (int)mode);
        }
        catalogueMode = mode;
        UpdateCharacterLayout();
        CatalogueModeChanged?.Invoke(mode);
        CloseCatalogueOptions();
        if (wasCharacter || !isCatalogue)
        {
            loadCancellation?.Cancel();
            ClearScene();
            ModelPath.Clear();
        }
        filterCancellation?.Cancel();
        namedSelection?.Cancel();
        scanCancellation?.Cancel();
        NpcNamesButton.Visibility = mode == ModelCatalogueMode.ResourceType ? Visibility.Visible : Visibility.Collapsed;
        ModelSearch.IsEnabled = isCatalogue && models != null && namedModels != null;
        BrowserStatus.Text = source == null ? "请选择游戏目录" : "正在读取模型目录…";
        if (ModelSearch.IsEnabled && modelCatalogRevision == source?.MediaCatalogRevision)
            await ApplyModelFilter();
        StartCatalogueIfVisible();
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
            var snapshot = await Task.Run(() => LoadModelAssets(game, current.Token), current.Token);
            current.Token.ThrowIfCancellationRequested();
            var named = namedModels ?? await ReadAssemblyCatalogue(game, current.Token);
            current.Token.ThrowIfCancellationRequested();

            namedModels = named;
            itemOptions = snapshot.Items;
            assetNames = snapshot.Labels;
            models = snapshot.Models.Concat(result).DistinctBy(model => model.Path, StringComparer.Ordinal).ToArray();
            modelCatalogRevision = snapshot.Revision;
            scanCancellation = null;
            await SelectCatalogueMode(ModelCatalogueMode.ResourceType, refresh: true);
            return $"已扫描 {result.Count:N0} 个模型，含 {result.Count(model => model.Path.StartsWith("sqpack:", StringComparison.Ordinal)):N0} 个路径未识别模型";
        }
        finally { if (scanCancellation == current) scanCancellation = null; }
    }

    private static async Task<IReadOnlyList<NamedModelEntry>> ReadAssemblyCatalogue(LocalGameSource game, CancellationToken token,
        IProgress<string>? progress = null, bool refresh = false)
    {
        try { return await game.ListNamedModelsAsync(token, progress, refresh); }
        catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or IOException)
        {
            progress?.Report("模型来源表暂不可用 · " + ex.Message);
            return [];
        }
    }

    private static (IReadOnlyList<SqPackModelLocation> Models, IReadOnlyList<GameCharacterPartOption> Items,
        IReadOnlyDictionary<string, string> Labels, int Revision) LoadModelAssets(LocalGameSource game, CancellationToken token)
    {
        var revision = game.MediaCatalogRevision;
        var locations = game.ListKnownLocalMediaLocations("model", token);
        var items = game.ListStandaloneItemParts(token);
        var labels = game.ListCatalogPartNames(locations.Select(location => location.Path), token);
        return (locations, items, labels, revision);
    }

    private int modelCatalogRevision = -1;

    private void StartCatalogueIfVisible()
    {
        if (!IsVisible || !ShowBrowser || source == null || scanCancellation is { IsCancellationRequested: false }) return;
        if (catalogueMode == ModelCatalogueMode.Character) return;
        if (namedModels == null || models == null || modelCatalogRevision != source.MediaCatalogRevision)
            _ = ReadCatalogue();
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
            if (catalogueMode == ModelCatalogueMode.ResourceType)
            {
                filterCancellation?.Cancel();
                PathTreeView.SetItems(ModelTree, [], ModelSearch.Text);
                ModelSearch.IsEnabled = false;
            }
            BrowserStatus.Text = $"合并后共 {count:N0} 条候选路径，正在匹配本地索引。";
            await SelectCatalogueMode(ModelCatalogueMode.ResourceType, refresh: true);
        }
        catch (Exception ex) { BrowserStatus.Text = "导入模型路径失败 · " + ex.Message; }
        finally { ModelPathsButton.IsEnabled = true; }
    }

    private static IReadOnlyList<PathTreeNode> BuildNamedNumberTree(IReadOnlyList<NamedModelEntry> entries,
        string filter, CancellationToken token)
    {
        var comparer = StringComparer.Create(CultureInfo.GetCultureInfo("zh-CN"), ignoreCase: true);
        var tree = PathTree.Build(entries, NumberDisplayPath, filter,
                entry => $"{entry.Name} {entry.Path} {entry.SourceSheet} {entry.SourceRowId} ModelChara {entry.ModelCharaId} {entry.NameSource}", token, comparer);
        return SortModelNodes(tree, comparer, token);
    }

    private static IReadOnlyList<PathTreeNode> SortModelNodes(IReadOnlyList<PathTreeNode> nodes,
        IComparer<string> labels, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return nodes.Select(node => node.IsDirectory
                ? new PathTreeNode(node.FullPath, node.Label, node.Payload,
                    SortModelNodes(node.Children, labels, token), true, node.IsExpanded)
                : node)
            .OrderBy(node => node, Comparer<PathTreeNode>.Create((left, right) =>
            {
                var kind = right.IsDirectory.CompareTo(left.IsDirectory);
                if (kind != 0) return kind;
                if (left.Payload is NamedModelEntry a && right.Payload is NamedModelEntry b)
                {
                    var variant = a.Variant.CompareTo(b.Variant);
                    if (variant != 0) return variant;
                    var id = a.SourceRowId.CompareTo(b.SourceRowId);
                    if (id != 0) return id;
                }
                else if (left.Label.Length > 1 && right.Label.Length > 1 &&
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

    private static string NumberDisplayPath(NamedModelEntry entry)
    {
        var name = LabelSegment(entry.Name);
        var label = name.StartsWith('#') || name == $"{entry.SourceSheet} #{entry.SourceRowId}" ? name : $"{name} · #{entry.SourceRowId}";
        var parts = entry.Path.Split('/');
        var category = entry.ModelType switch { 3 => "怪物", 2 => "亚人", 4 => "武器", _ => "其他" };
        var prefix = entry.ModelType == 2 ? "组合模型 · " : "";
        return $"{category}/{parts[2]}/{parts[5]}/{prefix}v{entry.Variant:D4} · {label}";
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
            if (entry.ModelType == 2) await OpenAsync(entry.Path, true, null, entry);
            else await OpenAsync(entry.Path);
            if (!pending.IsCancellationRequested && PathTreeView.IsCurrentSelection(ModelTree, selected))
            {
                selectedNamedModel = entry;
                MaterialVariant.IsReadOnly = entry.ModelType == 2;
                if (entry.ModelCharaId == 0)
                {
                    ModelEntryInfo.Text = $"{entry.Name} · {entry.NameSource}";
                    return;
                }
                var nameRow = entry.NameRowId is { } id ? $" · 名称行 #{id}" : "";
                ModelEntryInfo.Text = $"{entry.Name} · {entry.SourceSheet} #{entry.SourceRowId} → ModelChara #{entry.ModelCharaId} · v{entry.Variant:D4}"
                    + $"\n名称来源：{entry.NameSource}{nameRow}" + (warning == null ? "" : "\n材质变体使用默认值：" + warning);
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
