using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace FFXIVPatchParser.Desktop;

public static class TreeStickyScroll
{
    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(TreeStickyScroll), new PropertyMetadata(false, OnEnabledChanged));
    private static readonly DependencyProperty StateProperty =
        DependencyProperty.RegisterAttached("State", typeof(State), typeof(TreeStickyScroll));

    public static bool GetEnabled(DependencyObject control) => (bool)control.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject control, bool value) => control.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject control, DependencyPropertyChangedEventArgs e)
    {
        if (control is TreeView tree && (bool)e.NewValue && tree.GetValue(StateProperty) == null)
            tree.SetValue(StateProperty, new State(tree));
    }

    private sealed class State
    {
        private readonly TreeView tree;
        private StackPanel? layer;
        private bool queued;
        private List<(TreeViewItem Item, PathTreeNode Node)> pinned = [];

        public State(TreeView tree)
        {
            this.tree = tree;
            tree.Loaded += (_, _) => Schedule();
            tree.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => Schedule()), true);
            tree.AddHandler(TreeViewItem.ExpandedEvent, new RoutedEventHandler((_, _) => Schedule()));
            tree.AddHandler(TreeViewItem.CollapsedEvent, new RoutedEventHandler((_, _) => Schedule()));
            tree.SizeChanged += (_, _) => Schedule();
            tree.IsVisibleChanged += (_, _) => Schedule();
            tree.ItemContainerGenerator.ItemsChanged += (_, _) => Schedule();
            Schedule();
        }

        private void Schedule()
        {
            if (queued || !tree.IsVisible) return;
            queued = true;
            _ = tree.Dispatcher.InvokeAsync(Update, DispatcherPriority.Loaded);
        }

        private void Update()
        {
            queued = false;
            var template = tree.Template;
            if (!tree.IsVisible || template?.FindName("PART_ScrollViewer", tree) is not ScrollViewer viewport ||
                viewport.Template?.FindName("PART_StickyFolders", viewport) is not StackPanel host) return;
            if (!ReferenceEquals(host, layer))
            {
                layer = host;
                pinned.Clear();
                layer.PreviewMouseWheel += (_, e) =>
                {
                    viewport.ScrollToVerticalOffset(viewport.VerticalOffset - e.Delta / 120.0 * 60);
                    e.Handled = true;
                };
            }

            var rows = new List<(TreeViewItem Item, PathTreeNode Node, double Height, double Indent)>();
            if (GetEnabled(tree) && viewport.VerticalOffset > 0 && FindFirstRow(viewport, viewport) is { } first)
            {
                var chain = new Stack<TreeViewItem>();
                for (TreeViewItem? item = first; item != null; item = ItemsControl.ItemsControlFromItemContainer(item) as TreeViewItem)
                    chain.Push(item);
                double height = 0;
                foreach (var item in chain)
                {
                    if (item.Header is not PathTreeNode { IsDirectory: true } node || !item.IsExpanded ||
                        Header(item) is not { } header) continue;
                    var position = header.TranslatePoint(new Point(), viewport);
                    if (position.Y >= height || height + header.ActualHeight > viewport.ViewportHeight - header.ActualHeight)
                        break;
                    rows.Add((item, node, header.ActualHeight, Math.Max(0, position.X - 10)));
                    height += header.ActualHeight;
                }
            }

            if (pinned.SequenceEqual(rows.Select(row => (row.Item, row.Node)))) return;
            pinned = rows.Select(row => (row.Item, row.Node)).ToList();
            layer.Children.Clear();
            foreach (var row in rows)
            {
                var label = new TextBlock { Text = row.Node.Label, TextTrimming = TextTrimming.CharacterEllipsis };
                var button = new Button
                {
                    Content = label, Height = row.Height, Padding = new Thickness(row.Indent, 0, 10, 0),
                    Margin = new Thickness(0), BorderThickness = new Thickness(0),
                    ToolTip = $"折叠 {row.Node.Label}"
                };
                button.SetResourceReference(FrameworkElement.StyleProperty, "StickyFolderButton");
                AutomationProperties.SetName(button, $"折叠 {row.Node.Label}");
                button.Click += (_, _) =>
                {
                    if (!ReferenceEquals(row.Item.Header, row.Node)) return;
                    row.Item.SetCurrentValue(TreeViewItem.IsExpandedProperty, false);
                    row.Item.BringIntoView(new Rect(0, 0, Math.Min(row.Item.ActualWidth, viewport.ViewportWidth), row.Height));
                    Schedule();
                };
                layer.Children.Add(button);
            }
            layer.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static FrameworkElement? Header(TreeViewItem item) =>
            item.Template?.FindName("HeaderBorder", item) as FrameworkElement;

        private static TreeViewItem? FindFirstRow(DependencyObject root, ScrollViewer viewport)
        {
            if (root is TreeViewItem item)
            {
                var top = item.TranslatePoint(new Point(), viewport).Y;
                if (top + item.ActualHeight <= 0 || top >= viewport.ViewportHeight) return null;
                if (Header(item) is { } header && header.TranslatePoint(new Point(0, header.ActualHeight), viewport).Y > 0)
                    return item;
                if (!item.IsExpanded) return null;
            }

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                if (FindFirstRow(VisualTreeHelper.GetChild(root, i), viewport) is { } row) return row;
            return null;
        }
    }
}
