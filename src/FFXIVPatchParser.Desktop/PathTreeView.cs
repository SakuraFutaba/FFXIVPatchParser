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
        Func<string, bool>? expandByDefault = null)
    {
        var state = States.GetValue(view, tree => new ViewState(tree));
        state.Replace(items, !string.IsNullOrWhiteSpace(filter), expandByDefault);
    }

    public static void Reset(TreeView view)
    {
        if (States.TryGetValue(view, out var state)) state.Reset();
        else view.ItemsSource = null;
    }

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
        private readonly Dictionary<string, bool> expanded = new(StringComparer.Ordinal);
        private readonly Dictionary<string, bool> searchExpanded = new(StringComparer.Ordinal);
        private bool searching;
        private List<int>? restorePath;

        public bool Restoring { get; private set; }
        public SelectionKey? Selected { get; private set; }

        public ViewState(TreeView view)
        {
            this.view = view;
            view.SelectedItemChanged += SelectionChanged;
            view.IsVisibleChanged += (_, _) => ScheduleSelection();
            if (view.SelectedItem is PathTreeNode node) Selected = SelectionKey.From(node);
        }

        public void Reset()
        {
            restorePath = null;
            Selected = null;
            searching = false;
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
                if (candidatePath != null && SelectionKey.From(node) == Selected) restorePath = [.. candidatePath];
                if (node.IsDirectory)
                {
                    var lookingForSelection = restorePath == null && candidatePath != null;
                    RestoreExpansion(node.Children, overrides, expandByDefault, candidatePath);
                    node.IsExpanded = (lookingForSelection && restorePath != null) ||
                        overrides.GetValueOrDefault(node.FullPath, node.IsExpanded || expandByDefault?.Invoke(node.FullPath) == true);
                    node.InitialExpansion = node.IsExpanded;
                }
                candidatePath?.RemoveAt(candidatePath.Count - 1);
            }
        }

        private void SelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (Restoring || e.NewValue is not PathTreeNode node) return;
            Selected = SelectionKey.From(node);
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
                restorePath = null;
                return true;
            }
            finally { Restoring = false; }
        }
    }
}
