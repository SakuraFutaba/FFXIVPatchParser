using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FFXIVPatchParser.Desktop;

internal static class TableTabs
{
    public static void Render(Panel panel, IEnumerable<string> names, string? current, string? preview,
        Action<string> select, Action<string> pin, Action<string> close)
    {
        panel.Children.Clear();
        foreach (var name in names)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            var tab = new Button { Content = name, Padding = new Thickness(10, 5, 6, 5), Margin = new Thickness(0), BorderThickness = new Thickness(0), Background = Brushes.Transparent, ToolTip = name };
            if (name == preview)
            {
                tab.FontStyle = FontStyles.Italic;
                tab.ToolTip = $"{name} · 预览，双击固定";
                tab.PreviewMouseDoubleClick += (_, e) =>
                {
                    e.Handled = true;
                    pin(name);
                };
            }
            tab.Click += (_, _) => select(name);
            content.Children.Add(tab);
            var dismiss = new Button { Content = "×", Padding = new Thickness(5, 2, 7, 2), Margin = new Thickness(0), BorderThickness = new Thickness(0), Background = Brushes.Transparent, ToolTip = "关闭 " + name };
            dismiss.Click += (_, _) => close(name);
            content.Children.Add(dismiss);
            var border = new Border { Child = content, BorderThickness = new Thickness(0, 0, 1, name == current ? 2 : 0) };
            border.SetResourceReference(Border.BorderBrushProperty, name == current ? "Accent" : "Border");
            border.Background = name == current ? Brushes.White : Brushes.Transparent;
            tab.FontWeight = name == current ? FontWeights.SemiBold : FontWeights.Normal;
            panel.Children.Add(border);
            if (name == current) border.Loaded += (_, _) => border.BringIntoView();
        }
    }

    public static Action Capture(DataGrid grid)
    {
        var restore = TableColumnSizing.Capture(grid, true);
        var index = grid.Items.IndexOf(grid.CurrentCell.Item);
        var column = grid.CurrentCell.Column?.DisplayIndex ?? -1;
        return () =>
        {
            if (index >= 0 && index < grid.Items.Count && column >= 0 && column < grid.Columns.Count)
            {
                var cell = new DataGridCellInfo(grid.Items[index], grid.Columns[column]);
                grid.CurrentCell = cell;
                grid.SelectedCells.Clear();
                grid.SelectedCells.Add(cell);
            }
            restore();
        };
    }

    public static void CopyName(object sender, MouseButtonEventArgs e)
    {
        // 阻止 TreeViewItem 的右键选择，只对点击的叶子显示菜单。
        e.Handled = true;
        var element = e.OriginalSource as DependencyObject;
        while (element != null && element is not TreeViewItem)
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        if (element is not TreeViewItem { DataContext: PathTreeNode { IsDirectory: false } node } item) return;
        var name = node.Payload switch { string text => text, SheetChange sheet => sheet.Name, _ => null };
        if (name == null) return;
        var menu = new ContextMenu { PlacementTarget = item };
        var copy = new MenuItem
        {
            Header = "复制表名",
            Icon = new ContentControl { Content = "复制", ContentTemplate = (DataTemplate)Application.Current.FindResource("CopyIcon"), Focusable = false }
        };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(name); }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                MessageBox.Show("剪贴板正被其他程序占用，请再试一次。", "复制表名", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        };
        menu.Items.Add(copy);
        menu.IsOpen = true;
    }
}

internal sealed class TableTabCollection<T> where T : class
{
    private readonly OrderedDictionary<string, T?> tabs = new(StringComparer.OrdinalIgnoreCase);
    private string? opening;
    private (string Name, bool Pin)? pending;

    public string? Preview { get; private set; }
    public string? Opening => opening;
    public int Count => tabs.Count;
    public IEnumerable<string> Names => tabs.Keys;
    public T? this[string name] { set => tabs[name] = value; }
    public bool Contains(string name) => tabs.ContainsKey(name);
    public T? State(string name) => tabs.GetValueOrDefault(name);

    public void Open(string name)
    {
        if (tabs.ContainsKey(name)) return;
        var index = Preview == null ? tabs.Count : tabs.IndexOf(Preview);
        if (Preview != null) tabs.RemoveAt(index);
        tabs.Insert(index, name, null);
        Preview = name;
    }

    public bool Pin(string name)
    {
        if (!StringComparer.OrdinalIgnoreCase.Equals(Preview, name)) return false;
        Preview = null;
        return true;
    }

    public int Close(string name)
    {
        var index = tabs.IndexOf(name);
        if (index < 0) return index;
        Pin(name);
        tabs.RemoveAt(index);
        return index;
    }

    public void Clear()
    {
        tabs.Clear();
        Preview = null;
        pending = null;
    }

    // 读取表格时目录仍可点击。固定当前表立即生效，切表使用最近一次请求。
    public async Task OpenFromTreeAsync(string name, bool pin, Func<string, bool, Task> open)
    {
        if (opening != null)
        {
            if (opening == name)
            {
                pending = null;
                if (pin) await open(name, true);
            }
            else
            {
                pending = (name, pin || (pending is { Pin: true } next && next.Name == name));
            }
            return;
        }

        (string Name, bool Pin)? request = (name, pin);
        try
        {
            while (request is { } next)
            {
                pending = null;
                opening = next.Name;
                await open(next.Name, next.Pin);
                request = pending;
            }
        }
        finally { opening = null; }
    }
}
