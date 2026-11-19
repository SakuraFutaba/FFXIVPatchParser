using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace FFXIVPatchParser.Desktop;

internal static class PathTreeView
{
    private static readonly ConditionalWeakTable<TreeView, ViewState> States = new();

    public static void SetItems(TreeView view, IReadOnlyList<PathTreeNode> items, string? filter = null,
        Func<string, bool>? expandByDefault = null, bool compactDirectories = true)
    {
        var state = States.GetValue(view, tree => new ViewState(tree));
        state.Replace(compactDirectories ? CompactDirectories(items) : items,
            !string.IsNullOrWhiteSpace(filter), expandByDefault);
    }

    internal static IReadOnlyList<PathTreeNode> CompactDirectories(IReadOnlyList<PathTreeNode> nodes)
    {
        PathTreeNode[]? changed = null;
        for (var i = 0; i < nodes.Count; i++)
        {
            var first = nodes[i];
            if (!first.IsDirectory) continue;
            var last = first;
            var label = first.Label;
            List<string>? parents = null;
            while (last.CanCompactWithChild && last.Payload == null &&
                last.Children is [{ IsDirectory: true, CanCompactWithChild: true, Payload: null } only])
            {
                (parents ??= []).Add(last.FullPath);
                last = only;
                label += "/" + only.Label;
            }
            var children = CompactDirectories(last.Children);
            if (last == first && children == first.Children) continue;
            changed ??= nodes.ToArray();
            changed[i] = new(last.FullPath, label, last.Payload, children, true, last.IsExpanded)
            {
                CanCompactWithChild = last.CanCompactWithChild,
                CollapsedLabel = last != first ? first.CollapsedLabel ?? first.Label : first.CollapsedLabel,
                CompactParents = parents?.ToArray() ?? []
            };
        }
        return changed ?? nodes;
    }
    public static void SwitchCatalogue(TreeView view, int previous, int next) =>
        States.GetValue(view, tree => new ViewState(tree)).SwitchCatalogue(previous, next);

    public static void Reset(TreeView view)
    {
        if (States.TryGetValue(view, out var state)) state.Reset();
        else view.ItemsSource = null;
    }

    public static PathTreeNode? SelectedNode(TreeView view) =>
        view.SelectedItem as PathTreeNode ?? (States.TryGetValue(view, out var state) ? state.RestoreNode : null);

    public static bool IsRestoringSelection(TreeView view) =>
        States.TryGetValue(view, out var state) && state.Restoring;

    public static bool IsCurrentSelection(TreeView view, PathTreeNode node)
    {
        if (view.SelectedItem is PathTreeNode current) return SelectionKey.From(current) == SelectionKey.From(node);
        return States.TryGetValue(view, out var state) && state.Selected == SelectionKey.From(node);
    }

    public static bool SelectItem(TreeView view, IReadOnlyList<int> path)
    {
        ItemsControl parent = view;
        for (var level = 0; level < path.Count; level++)
        {
            var index = path[level];
            parent.UpdateLayout();
            if (parent.ItemContainerGenerator.ContainerFromIndex(index) == null)
                FindItemsHost(parent, parent)?.BringIndexIntoViewPublic(index);
            if (parent.ItemContainerGenerator.ContainerFromIndex(index) is not TreeViewItem item) return false;
            if (level == path.Count - 1)
            {
                item.SetCurrentValue(TreeViewItem.IsSelectedProperty, true);
                item.BringIntoView();
                return true;
            }

            item.SetCurrentValue(TreeViewItem.IsExpandedProperty, true);
            parent = item;
        }
        return false;
    }

    internal static bool SelectItem(TreeView view, IReadOnlyList<int> path, bool notify) =>
        notify ? SelectItem(view, path) : States.GetValue(view, tree => new ViewState(tree)).SelectQuietly(path);

    private static VirtualizingPanel? FindItemsHost(DependencyObject root, ItemsControl owner)
    {
        if (root is VirtualizingPanel panel && ItemsControl.GetItemsOwner(panel) == owner) return panel;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindItemsHost(VisualTreeHelper.GetChild(root, i), owner) is { } found) return found;
        return null;
    }

    private readonly record struct SelectionKey(string Path, bool IsDirectory, object? Payload)
    {
        public static SelectionKey From(PathTreeNode node) => new(node.FullPath, node.IsDirectory, node.Payload);
    }

    private sealed class ViewState
    {
        private readonly TreeView view;
        private Dictionary<string, bool> expanded = new(StringComparer.Ordinal);
        private Dictionary<string, bool> searchExpanded = new(StringComparer.Ordinal);
        private bool searching;
        private List<int>? restorePath;

        public PathTreeNode? RestoreNode { get; private set; }
        public bool Restoring { get; private set; }
        public SelectionKey? Selected { get; private set; }

        public ViewState(TreeView view)
        {
            this.view = view;
            view.SelectedItemChanged += SelectionChanged;
            view.IsVisibleChanged += (_, _) => ScheduleSelection();
            if (view.SelectedItem is PathTreeNode node) Selected = SelectionKey.From(node);
        }

        private readonly Dictionary<int, (Dictionary<string, bool> Expanded, Dictionary<string, bool> Search, bool Searching, SelectionKey? Selection)> catalogues = new();

        public void SwitchCatalogue(int previous, int next)
        {
            if (view.ItemsSource is IReadOnlyList<PathTreeNode> nodes)
                RememberExpansion(nodes, searching ? searchExpanded : expanded);
            catalogues[previous] = (expanded, searchExpanded, searching, Selected);
            catalogues.TryGetValue(next, out var saved);
            expanded = saved.Expanded ?? new(StringComparer.Ordinal);
            searchExpanded = saved.Search ?? new(StringComparer.Ordinal);
            searching = saved.Searching;
            Selected = saved.Selection;
            RestoreNode = null;
            restorePath = null;
            ReplaceSource(null);
        }

        public void Reset()
        {
            RestoreNode = null;
            restorePath = null;
            Selected = null;
            searching = false;
            catalogues.Clear();
            expanded.Clear();
            searchExpanded.Clear();
            ReplaceSource(null);
        }

        public void Replace(IReadOnlyList<PathTreeNode> items, bool isSearch, Func<string, bool>? expandByDefault)
        {
            if (view.ItemsSource is IReadOnlyList<PathTreeNode> previous)
                RememberExpansion(previous, searching ? searchExpanded : expanded);
            if (searching && !isSearch)
            {
                // 仅保留用户在筛选期间改过的展开项。
                foreach (var (path, isExpanded) in searchExpanded) expanded[path] = isExpanded;
                searchExpanded.Clear();
            }

            searching = isSearch;
            RestoreNode = null;
            restorePath = null;
            var selectionPath = Selected.HasValue ? new List<int>() : null;
            var overrides = searching ? searchExpanded : expanded;
            RestoreExpansion(items, overrides, expandByDefault, selectionPath);
            ReplaceSource(items);
            ScheduleSelection();
        }

        private void ReplaceSource(IReadOnlyList<PathTreeNode>? items)
        {
            Restoring = true;
            try { view.ItemsSource = items; }
            finally { Restoring = false; }
        }

        private static void RememberExpansion(IReadOnlyList<PathTreeNode> items, Dictionary<string, bool> overrides)
        {
            foreach (var node in items)
            {
                if (!node.IsDirectory) continue;
                if (node.IsExpanded != node.InitialExpansion) overrides[node.FullPath] = node.IsExpanded;
                RememberExpansion(node.Children, overrides);
            }
        }

        private void RestoreExpansion(IReadOnlyList<PathTreeNode> items, Dictionary<string, bool> overrides,
            Func<string, bool>? expandByDefault, List<int>? path)
        {
            for (var index = 0; index < items.Count; index++)
            {
                var node = items[index];
                var candidatePath = restorePath == null ? path : null;
                candidatePath?.Add(index);
                if (candidatePath != null && (SelectionKey.From(node) == Selected ||
                    Selected is { IsDirectory: true, Payload: null } selected && node.CompactParents.Contains(selected.Path)))
                {
                    restorePath = [.. candidatePath];
                    RestoreNode = node;
                }
                if (node.IsDirectory)
                {
                    var lookingForSelection = restorePath == null && candidatePath != null;
                    RestoreExpansion(node.Children, overrides, expandByDefault, candidatePath);
                    node.IsExpanded = (lookingForSelection && restorePath != null) ||
                        overrides.GetValueOrDefault(node.FullPath, node.IsExpanded || expandByDefault?.Invoke(node.FullPath) == true ||
                            expandByDefault != null && node.CompactParents.Any(p => expandByDefault(p)));
                    node.InitialExpansion = node.IsExpanded;
                }
                candidatePath?.RemoveAt(candidatePath.Count - 1);
            }
        }

        private void SelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (Restoring || e.NewValue is not PathTreeNode node) return;
            Selected = SelectionKey.From(node);
            RestoreNode = null;
            restorePath = null;
        }

        private void ScheduleSelection()
        {
            if (!view.IsVisible || restorePath is not { } path) return;
            view.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (!view.IsVisible || !ReferenceEquals(path, restorePath)) return;
                Restoring = true;
                try
                {
                    if (SelectItem(view, path)) restorePath = null;
                }
                finally { Restoring = false; }
            });
        }
        public bool SelectQuietly(IReadOnlyList<int> path)
        {
            Restoring = true;
            try
            {
                if (!SelectItem(view, path)) return false;
                if (view.SelectedItem is PathTreeNode node) Selected = SelectionKey.From(node);
                RestoreNode = null;
            restorePath = null;
                return true;
            }
            finally { Restoring = false; }
        }
    }
}
