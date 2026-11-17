using System.Collections;
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Uld;
using Nodes = Lumina.Data.Parsing.Uld.NodeData;

namespace FFXIVPatchParser.Desktop;

public partial class UldBrowser : UserControl
{
    private LocalGameSource? source;
    private UldFile? file;
    private int revision;
    private readonly HashSet<Task> activeReads = [];
    private bool stopped;
    private bool catalogStarted;
    private bool loadingUld;
    private bool settingUldPaths;
    private string? requestedUld;
    private CancellationTokenSource sourceLifetime = new();
    private readonly Dictionary<uint, BitmapSource> textures = [];
    private readonly Dictionary<uint, BitmapSource> baseTextures = [];
    private readonly Dictionary<uint, string> texturePaths = [];
    private readonly List<string> textureErrors = [];
    private readonly Dictionary<(uint List, uint Part), BitmapSource> crops = [];
    private Dictionary<uint, UldTimeline> timelines = [];
    private Dictionary<uint, UldRoot.ComponentData> componentsById = [];
    private readonly List<LayoutNode> layoutVisuals = [];
    private readonly List<(LayoutNode Node, UldTimeline Timeline)> animatedNodes = [];
    private Canvas? previewContent;
    private bool updatingFrameRange;
    private int drawn, unsupported;
    private sealed record AssetRow(uint Id, string Path, byte ThemeMask, uint IconId);
    private sealed record PartRow(uint List, int Index, uint Texture, int U, int V, int Width, int Height);
    private sealed record LayoutRow(string Name, UldRoot.NodeData[] Nodes) { public override string ToString() => Name; }

    public UldBrowser()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => { if (IsVisible) InitializeUld(); };
    }

    public async Task StopAsync()
    {
        stopped = true;
        sourceLifetime.Cancel();
        revision++;
        try { await Task.WhenAll(activeReads.ToArray()); }
        catch { /* 读取异常由对应操作显示；关闭时只等待读取结束。 */ }
    }

    private async Task<T> ReadTracked<T>(Func<T> read)
    {
        var task = Task.Run(read);
        activeReads.Add(task);
        try { return await task; }
        finally { activeReads.Remove(task); }
    }

    public void SetSource(LocalGameSource? value)
    {
        if (ReferenceEquals(source, value)) return;
        source = value;
        stopped = false;
        sourceLifetime.Cancel(); sourceLifetime.Dispose(); sourceLifetime = new();
        catalogStarted = false;
        loadingUld = false;
        requestedUld = null;
        revision++;
        file = null;
        textures.Clear(); baseTextures.Clear(); texturePaths.Clear(); textureErrors.Clear(); crops.Clear();
        timelines.Clear(); componentsById.Clear();
        Assets.ItemsSource = null; Parts.ItemsSource = null; Layouts.ItemsSource = null;
        TimelineTree.Items.Clear(); NodeTree.Items.Clear(); PreviewCanvas.Children.Clear();
        AssetImage.Source = null; PartImage.Source = null;
        AssetInfo.Text = PartInfo.Text = RenderInfo.Text = "";
        Status.Text = value == null ? "请在设置中连接游戏目录。" : "";
        UldPath.ItemsSource = null;
        Render();
        if (IsVisible) InitializeUld();
    }

    private void InitializeUld()
    {
        if (source is null || stopped) return;
        if (requestedUld is null) _ = OpenUldAsync(UldPath.Text);
        StartCatalog();
    }

    private async void StartCatalog()
    {
        if (catalogStarted || stopped || source is not { } game) return;
        catalogStarted = true;
        var token = sourceLifetime.Token;
        try
        {
            var paths = await ReadTracked(() => game.ListUldPaths(token));
            if (token.IsCancellationRequested || !ReferenceEquals(source, game)) return;
            var selected = UldPath.Text;
            settingUldPaths = true;
            try
            {
                UldPath.ItemsSource = paths;
                UldPath.SelectedItem = paths.FirstOrDefault(path => path.Equals(selected, StringComparison.OrdinalIgnoreCase));
                UldPath.Text = selected;
            }
            finally { settingUldPaths = false; }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) Status.Text = "读取已知 ULD 目录失败，仍可输入路径：" + ex.Message; }
    }

    private void PreviousTheme(object sender, RoutedEventArgs e) => Theme.SelectedIndex = (Theme.SelectedIndex + Theme.Items.Count - 1) % Theme.Items.Count;
    private void NextTheme(object sender, RoutedEventArgs e) => Theme.SelectedIndex = (Theme.SelectedIndex + 1) % Theme.Items.Count;
    private void PreviousUld(object sender, RoutedEventArgs e) => StepUld(-1);
    private void NextUld(object sender, RoutedEventArgs e) => StepUld(1);
    private void StepUld(int delta)
    {
        if (UldPath.Items.Count == 0) return;
        var current = UldPath.SelectedIndex;
        UldPath.SelectedIndex = current < 0 ? 0 : (current + delta + UldPath.Items.Count) % UldPath.Items.Count;
    }

    private async void UldSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!settingUldPaths && UldPath?.SelectedItem is string path) await OpenUldAsync(path);
    }

    private async void UldPathKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || UldPath.IsDropDownOpen) return;
        e.Handled = true;
        await OpenUldAsync(UldPath.Text, retry: true);
    }

    private async Task OpenUldAsync(string path, bool retry = false)
    {
        if (!IsVisible || stopped) return;
        if (source is not { } game) { Status.Text = "请先连接游戏目录。"; return; }
        if (string.IsNullOrWhiteSpace(path) || (!retry && string.Equals(requestedUld, path, StringComparison.OrdinalIgnoreCase))) return;
        requestedUld = path;
        var request = ++revision;
        loadingUld = true;
        Status.Text = "正在读取 ULD…";
        try
        {
            var result = await ReadTracked(() => game.ReadUld(path));
            if (request != revision) return;
            loadingUld = false;
            file = result;
            TimelineFrame.Value = 0;
            timelines = result.Timelines.GroupBy(t => t.Id).ToDictionary(group => group.Key, group => new UldTimeline(group.First()));
            componentsById = result.Components.GroupBy(c => c.Id).ToDictionary(group => group.Key, group => group.First());
            textures.Clear();
            baseTextures.Clear();
            texturePaths.Clear();
            crops.Clear();
            Assets.ItemsSource = result.AssetData.Select(a => new AssetRow(a.Id, new string(a.Path).TrimEnd('\0'), a.ThemeSupportBitmask, a.IconId)).ToArray();
            Parts.ItemsSource = result.Parts.SelectMany(p => p.Parts.Select((a, i) => new PartRow(p.Id, i, a.TextureId, a.U, a.V, a.W, a.H))).ToArray();
            Layouts.ItemsSource = PreviewLayouts(path, result).ToArray();
            TimelineTree.Items.Clear();
            foreach (var t in result.Timelines) TimelineTree.Items.Add(Inspect($"时间线 {t.Id}", t));
            NodeTree.Items.Clear();
            NodeTree.Items.Add(Inspect("主布局", result.WidgetData));
            foreach (var c in result.Components) NodeTree.Items.Add(Inspect($"组件 {c.Id} · {c.Type}", c));
            Layouts.SelectedIndex = 0;
            await LoadTexturesAsync();
        }
        catch (Exception ex)
        {
            if (request != revision) return;
            loadingUld = false;
            Status.Text = "读取失败：" + ex.Message;
        }
    }

    private static IEnumerable<LayoutRow> PreviewLayouts(string path, UldFile uld)
    {
        var nodes = uld.WidgetData.Nodes;
        var name = path.Trim().Replace('\\', '/').Split('/')[^1];
        // 黑魔法师元素量谱的普通版与简易版同存于主布局，游戏运行时只启用其中一个分支。
        if ((name.Equals("jobhudblm0.uld", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("jobhudblm0", StringComparison.OrdinalIgnoreCase)) &&
            nodes.Any(n => n.NodeId == 2 && n.ParentId == 1 && n.TimelineId == 59) &&
            nodes.Any(n => n.NodeId == 33 && n.ParentId == 1 && n.TimelineId == 87))
        {
            yield return new LayoutRow("普通量谱", GaugeBranch(2));
            yield return new LayoutRow("简易量谱", GaugeBranch(33));
            yield return new LayoutRow("完整主布局", nodes);
        }
        else yield return new LayoutRow("主布局", nodes);
        foreach (var c in uld.Components) yield return new LayoutRow($"组件 {c.Id} · {c.Type}", c.Nodes);

        UldRoot.NodeData[] GaugeBranch(int root)
        {
            var children = nodes.ToLookup(n => n.ParentId);
            var branch = new HashSet<uint>();
            var pending = new Queue<int>();
            pending.Enqueue(root);
            while (pending.TryDequeue(out var id) && branch.Add((uint)id))
                foreach (var child in children[id]) pending.Enqueue((int)child.NodeId);
            return nodes.Where(n => n.NodeId == 1 || branch.Contains(n.NodeId)).ToArray();
        }
    }

    private async void ThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!stopped && !loadingUld && file != null) await LoadTexturesAsync();
    }

    private async Task LoadTexturesAsync()
    {
        if (stopped || file is not { } uld || source is not { } game) return;
        var request = ++revision;
        var theme = Theme.SelectedIndex;
        var token = sourceLifetime.Token;
        Status.Text = "正在读取纹理…";
        try
        {
            var loaded = await ReadTracked(() => uld.AssetData.Select(asset =>
            {
                token.ThrowIfCancellationRequested();
                var path = new string(asset.Path).TrimEnd('\0');
                var original = DecodeTexture(game, path, out var error);
                if (theme > 0 && path.StartsWith("ui/uld/", StringComparison.OrdinalIgnoreCase) &&
                    (asset.ThemeSupportBitmask & (1 << (theme - 1))) != 0)
                {
                    var themed = $"ui/uld/img{theme:D2}/" + path[7..];
                    if (game.UldAssetExists(themed) && DecodeTexture(game, themed, out _) is { } bitmap)
                        return (asset.Id, Path: themed, Bitmap: (BitmapSource?)bitmap, Original: original, Error: "");
                }
                return (asset.Id, Path: path, Bitmap: original, Original: (BitmapSource?)null, Error: error);
            }).ToArray());
            if (request != revision) return;
            textures.Clear(); baseTextures.Clear(); texturePaths.Clear(); textureErrors.Clear(); crops.Clear();
            foreach (var item in loaded)
            {
                texturePaths[item.Id] = item.Path;
                if (item.Bitmap != null) textures[item.Id] = item.Bitmap;
                else textureErrors.Add($"{item.Path}: {item.Error}");
                if (item.Original != null) baseTextures[item.Id] = item.Original;
            }
            Render(); UpdateAsset(); UpdatePart();
            Status.Text = $"{uld.AssetData.Length} 个资源，{uld.Parts.Sum(p => p.Parts.Length)} 个切片，{uld.Components.Length} 个组件，{uld.Timelines.Length} 条时间线。" +
                (textureErrors.Count > 0 ? $" {textureErrors.Count} 个纹理无法读取；首项：{textureErrors[0]}" : "");
        }
        catch (Exception ex) { if (request == revision) Status.Text = "纹理加载失败：" + ex.Message; }
    }

    private static BitmapSource? DecodeTexture(LocalGameSource game, string path, out string error)
    {
        error = "纹理路径为空";
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var tex = game.ReadTexture(path);
            var bitmap = BitmapSource.Create(tex.Width, tex.Height, 96, 96, PixelFormats.Bgra32, null, tex.Bgra32, tex.Width * 4);
            bitmap.Freeze();
            error = "";
            return bitmap;
        }
        catch (Exception ex) { error = ex.Message; return null; }
    }

    private void AssetSelected(object sender, SelectionChangedEventArgs e) => UpdateAsset();
    private void UpdateAsset()
    {
        var a = Assets.SelectedItem as AssetRow;
        CopyTexturePath.IsEnabled = !string.IsNullOrEmpty(a?.Path);
        BaseAssetImage.Source = a == null ? null : baseTextures.GetValueOrDefault(a.Id);
        BaseAssetPreview.Visibility = BaseAssetImage.Source == null ? Visibility.Collapsed : Visibility.Visible;
        BaseAssetInfo.Text = a?.Path ?? "";
        if (a == null) { AssetImage.Source = null; AssetInfo.Text = ""; return; }
        AssetImage.Source = textures.GetValueOrDefault(a.Id);
        AssetInfo.Text = texturePaths.GetValueOrDefault(a.Id, a.Path) + (AssetImage.Source == null ? "\n纹理不可用（可能由游戏运行时填充）。" : "");
    }
    private void PartSelected(object sender, SelectionChangedEventArgs e) => UpdatePart();
    private void UpdatePart()
    {
        var p = Parts.SelectedItem as PartRow;
        var path = p == null ? null : texturePaths.GetValueOrDefault(p.Texture);
        CopyPartPath.IsEnabled = !string.IsNullOrEmpty(path);
        if (p == null) { PartImage.Source = null; PartInfo.Text = ""; return; }
        PartImage.Source = Crop(p.List, (uint)p.Index);
        PartInfo.Text = $"列表 {p.List} / 切片 {p.Index} · ({p.U}, {p.V}) {p.Width} × {p.Height}\n" + (path ?? "纹理不可用");
    }

    private async void CopyResourcePath(object sender, RoutedEventArgs e)
    {
        var path = sender == CopyTexturePath && Assets.SelectedItem is AssetRow asset
            ? texturePaths.GetValueOrDefault(asset.Id, asset.Path)
            : Parts.SelectedItem is PartRow part ? texturePaths.GetValueOrDefault(part.Texture) : null;
        if (string.IsNullOrEmpty(path) || Window.GetWindow(this) is not { } window) return;
        try
        {
            MainWindow.Notify(this, await ClipboardText.TryCopyAsync(new WindowInteropHelper(window).Handle, path)
                ? "已复制路径" : "剪贴板正被其他程序占用。");
        }
        catch (Win32Exception ex) { MainWindow.Notify(this, "无法复制路径：" + ex.Message); }
    }

    private BitmapSource? Crop(uint list, uint part)
    {
        if (crops.TryGetValue((list, part), out var cached)) return cached;
        var parts = file?.Parts.FirstOrDefault(p => p.Id == list).Parts;
        if (parts == null || part >= parts.Length) return null;
        var p = parts[part];
        if (!textures.TryGetValue(p.TextureId, out var bitmap) || p.W == 0 || p.H == 0 || p.U + p.W > bitmap.PixelWidth || p.V + p.H > bitmap.PixelHeight) return null;
        var crop = new CroppedBitmap(bitmap, new Int32Rect(p.U, p.V, p.W, p.H));
        crop.Freeze();
        crops[(list, part)] = crop;
        return crop;
    }

    private void LayoutChanged(object sender, RoutedEventArgs e) => Render();
    private void SelectedLayoutChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PreviewCanvas == null) return;
        PreviousLayoutButton.IsEnabled = NextLayoutButton.IsEnabled = Layouts.Items.Count > 1;
        Render();
    }

    private void PreviousLayout(object sender, RoutedEventArgs e) => StepLayout(-1);
    private void NextLayout(object sender, RoutedEventArgs e) => StepLayout(1);
    private void StepLayout(int delta)
    {
        var count = Layouts.Items.Count;
        if (count > 1) Layouts.SelectedIndex = (Layouts.SelectedIndex + delta + count) % count;
    }

    private void NodeMarkersChanged(object sender, RoutedEventArgs e)
    {
        foreach (var visual in layoutVisuals) visual.ShowMarker(ShowNodeMarkers.IsChecked == true);
    }

    private void PreviousFrame(object sender, RoutedEventArgs e) => TimelineFrame.Value--;
    private void NextFrame(object sender, RoutedEventArgs e) => TimelineFrame.Value++;
    private void ResetTimeline(object sender, RoutedEventArgs e) => TimelineFrame.Value = 0;
    private void TimelineFrameChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!updatingFrameRange) ApplyTimelineFrame();
    }

    private void ApplyTimelineFrame()
    {
        if (FrameInfo == null || TimelineFrame == null) return;
        var frame = (uint)TimelineFrame.Value;
        ResetTimelineButton.IsEnabled = frame != 0;
        FrameInfo.Text = !TimelineControls.IsEnabled ? "无可预览帧" :
            frame == 0 ? $"共 {TimelineFrame.Maximum:0} 帧" : $"第 {frame} / {TimelineFrame.Maximum:0} 帧";
        foreach (var (visual, timeline) in animatedNodes)
            visual.Apply(frame != 0 ? timeline.Sample(frame) : default);
    }

    private void Render()
    {
        if (PreviewCanvas == null) return;
        PreviewCanvas.Children.Clear();
        PreviewCanvas.Width = PreviewCanvas.Height = 0;
        layoutVisuals.Clear();
        animatedNodes.Clear();
        drawn = unsupported = 0;
        var endFrame = 0u;
        if (Layouts.SelectedItem is LayoutRow layout && file != null)
        {
            previewContent = new Canvas();
            PreviewCanvas.Children.Add(previewContent);
            AddNodes(previewContent, layout.Nodes, new HashSet<uint>(), 0);
            CenterLayout();
            endFrame = animatedNodes.Select(pair => pair.Timeline.End).DefaultIfEmpty().Max();
            RenderInfo.Text = $"{drawn} 个图片节点 · {unsupported} 个节点未渲染";
        }
        else
        {
            previewContent = null;
            RenderInfo.Text = "";
        }
        TimelineControls.IsEnabled = endFrame > 0;
        updatingFrameRange = true;
        TimelineFrame.Maximum = Math.Max(1, endFrame);
        if (endFrame == 0) TimelineFrame.Value = 0;
        updatingFrameRange = false;
        ApplyTimelineFrame();
    }

    private void CenterLayout()
    {
        if (previewContent is not { } content) return;
        var bounds = Rect.Empty;
        var nodeBounds = Rect.Empty;
        foreach (var visual in layoutVisuals)
        {
            if (visual.Node.W == 0 || visual.Node.H == 0) continue;
            var transform = Matrix.Identity;
            var visible = true;
            for (var current = visual.Canvas; !ReferenceEquals(current, content); current = (Canvas)current.Parent)
            {
                if (current.Visibility == Visibility.Hidden) { visible = false; break; }
                transform.Append(current.RenderTransform.Value);
                transform.Translate(Canvas.GetLeft(current), Canvas.GetTop(current));
            }
            if (!visible) continue;
            var rect = Rect.Transform(new Rect(0, 0, visual.Node.W, visual.Node.H), transform);
            if (!double.IsFinite(rect.X + rect.Y + rect.Width + rect.Height)) continue;
            nodeBounds.Union(rect);
            if (visual.HasImage) bounds.Union(rect);
        }
        if (bounds.IsEmpty) bounds = nodeBounds;
        if (bounds.IsEmpty) return;
        PreviewCanvas.Width = bounds.Width;
        PreviewCanvas.Height = bounds.Height;
        Canvas.SetLeft(content, -bounds.Left);
        Canvas.SetTop(content, -bounds.Top);
    }

    private void AddNodes(Canvas target, UldRoot.NodeData[] nodes, HashSet<uint> components, int depth)
    {
        if (depth > 24 || nodes == null) return;
        var containers = new Dictionary<uint, Canvas>();
        foreach (var node in nodes)
        {
            if (containers.ContainsKey(node.NodeId)) continue;
            var visualNode = new LayoutNode(node);
            layoutVisuals.Add(visualNode);
            if (node.TimelineId != 0 && timelines.TryGetValue(node.TimelineId, out var timeline) && timeline.End > 0)
                animatedNodes.Add((visualNode, timeline));
            var canvas = visualNode.Canvas;
            // 使用磁盘上的原始标记。Lumina 7.7.2 的 Visible 属性读的是 0x80。
            var initiallyVisible = (node.field1 & 0x10) != 0;
            if (!initiallyVisible && ShowHidden.IsChecked != true) canvas.Visibility = Visibility.Hidden;
            canvas.ToolTip = $"节点 {node.NodeId} · 类型 {node.NodeType} · ({node.X}, {node.Y}) · {node.W} × {node.H} · {node.Rotation:0.##}°\n" +
                (initiallyVisible ? "初始可见" : "初始隐藏");
            containers[node.NodeId] = canvas;
            if (node.Node is Nodes.ImageNode image && Crop(image.PartListId, image.PartId) is { } bitmap)
            {
                var visual = new Image { Source = bitmap, Width = node.W, Height = node.H, Stretch = Stretch.Fill };
                if (image.FlipH || image.FlipV) { visual.RenderTransformOrigin = new Point(.5, .5); visual.RenderTransform = new ScaleTransform(image.FlipH ? -1 : 1, image.FlipV ? -1 : 1); }
                canvas.Children.Add(visual); drawn++;
                visualNode.HasImage = true;
            }
            else if (node.NodeType > 1000 && componentsById.TryGetValue((uint)node.NodeType, out var component) && components.Add(component.Id))
            {
                AddNodes(canvas, component.Nodes, components, depth + 1);
                components.Remove(component.Id);
            }
            else if (node.NodeType != 1)
            {
                unsupported++;
            }
            visualNode.ShowMarker(ShowNodeMarkers.IsChecked == true);
        }
        foreach (var node in nodes)
        {
            if (!containers.TryGetValue(node.NodeId, out var canvas) || canvas.Parent != null) continue;
            // 验证父链，防止异常 ULD 的循环引用破坏可视树。
            var seen = new HashSet<uint> { node.NodeId };
            var parentId = node.ParentId;
            var valid = true;
            while (parentId > 0 && containers.ContainsKey((uint)parentId))
            {
                if (!seen.Add((uint)parentId)) { valid = false; break; }
                parentId = nodes.First(n => n.NodeId == parentId).ParentId;
            }
            var parent = valid && node.ParentId > 0 && containers.TryGetValue((uint)node.ParentId, out var found) ? found : target;
            parent.Children.Add(canvas);
        }
    }

    private static TreeViewItem Inspect(string name, object? value, int depth = 0)
    {
        if (value == null) return new TreeViewItem { Header = name + ": 空" };
        var type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || value is string) return new TreeViewItem { Header = name + ": " + value };
        var item = new TreeViewItem { Header = name + " · " + type.Name };
        var fields = value is IEnumerable ? [] : type.GetFields(BindingFlags.Public | BindingFlags.Instance);
        if (depth > 12 || value is Array { Length: 0 } || (fields.Length == 0 && value is not IEnumerable)) return item;
        item.Items.Add("…");
        item.Expanded += Expand;
        return item;

        void Expand(object sender, RoutedEventArgs args)
        {
            if (args.OriginalSource != item) return;
            item.Expanded -= Expand;
            item.Items.Clear();
            if (value is IEnumerable sequence)
            {
                var i = 0;
                foreach (var entry in sequence)
                {
                    if (i >= 10000) { item.Items.Add("其余条目已省略"); break; }
                    item.Items.Add(Inspect((i++).ToString(), entry, depth + 1));
                }
            }
            else
                foreach (var member in fields) item.Items.Add(Inspect(member.Name, member.GetValue(value), depth + 1));
        }
    }
}
