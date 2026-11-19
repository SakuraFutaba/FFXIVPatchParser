using System.Windows;
using System.Windows.Controls;

namespace FFXIVPatchParser.Desktop;

public partial class AnimationDetails : UserControl
{
    public event EventHandler<ModelTableLinkEventArgs>? RelatedTableRequested;

    public AnimationDetails() => InitializeComponent();

    internal void Clear()
    {
        Visibility = Visibility.Collapsed;
        Rows.ItemsSource = null;
        References.IsExpanded = false;
        Summary.Text = "";
        Summary.ToolTip = null;
    }

    internal void Show(GameAnimationDescription description, GameAnimationMetadata? metadata = null)
    {
        var details = new List<string>();
        if (description.PoseName != null || description.Bank?.StartsWith('f') == true ||
            description.References.Any(r => (r.Sheet is "Action" or "Emote") && r.Name != null))
            details.Add(description.Label);
        else if (description.References.Any(r => r.TimelineSlot != null)) details.Add("含 TMB 引用的动作槽");
        if (description.Body is { } body) details.Add(body);
        if (description.Bank is { } bank) details.Add($"动作目录 {bank}");
        if (description.AnimationSet is { } set) details.Add($"动作集 {set}");
        if (description.Key is { } key) details.Add($"动作键 {key}");
        if (metadata is { } animation)
        {
            var slots = string.Join("、", animation.Slots.Take(3).Select(s => s.Name));
            details.Add($"PAP · {animation.Slots.Count:N0} 个槽" + (slots.Length == 0 ? "" : $" · {slots}") +
                (animation.Slots.Count > 3 ? "…" : ""));
            Summary.ToolTip = string.Join(Environment.NewLine, animation.Slots.Select(s => $"{s.Index} · {s.Name}"));
        }
        if (description.References.Count == 0) details.Add("无已核实的表格关联");
        Summary.Text = string.Join(" · ", details);
        References.Visibility = description.References.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        References.Header = $"动作关联 · {description.References.Count:N0} 条";
        Rows.ItemsSource = description.References;
        Rows.SelectedIndex = description.References.Count == 0 ? -1 : 0;
        Visibility = Visibility.Visible;
    }

    private void OpenRow(object sender, RoutedEventArgs e)
    {
        if (Rows.SelectedItem is GameAnimationReference row)
            RelatedTableRequested?.Invoke(this, new(row.Sheet, row.RowId));
    }
}
