using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using IconPath = System.Windows.Shapes.Path;

namespace FFXIVPatchParser.Desktop;

public sealed class PlayPauseButton : Button
{
    private static readonly Geometry PlayIcon = CreateIcon("M 3,1 L 13,7 L 3,13 Z");
    private static readonly Geometry PauseIcon = CreateIcon("M 2,1 H 5.5 V 13 H 2 Z M 8.5,1 H 12 V 13 H 8.5 Z");
    private readonly IconPath icon = new() { Width = 14, Height = 14, Stretch = Stretch.None };

    public PlayPauseButton()
    {
        SetResourceReference(StyleProperty, typeof(Button));
        icon.SetBinding(IconPath.FillProperty, new System.Windows.Data.Binding(nameof(Foreground)) { Source = this });
        Content = icon;
        IsPlaying = false;
    }

    public bool IsPlaying
    {
        get => ReferenceEquals(icon.Data, PauseIcon);
        set
        {
            icon.Data = value ? PauseIcon : PlayIcon;
            var label = value ? "暂停" : "播放";
            ToolTip = label;
            AutomationProperties.SetName(this, label);
        }
    }

    private static Geometry CreateIcon(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }
}
