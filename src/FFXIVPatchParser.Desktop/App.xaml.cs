using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace FFXIVPatchParser.Desktop;

public partial class App : Application
{
    public static readonly DependencyProperty ShowSearchClearButtonProperty =
        DependencyProperty.RegisterAttached("ShowSearchClearButton", typeof(bool), typeof(App), new PropertyMetadata(false));

    public static bool GetShowSearchClearButton(DependencyObject target) => (bool)target.GetValue(ShowSearchClearButtonProperty);

    public static void SetShowSearchClearButton(DependencyObject target, bool value) =>
        target.SetValue(ShowSearchClearButtonProperty, value);

    private void ClearSearchText(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { TemplatedParent: TextBox search }) return;
        search.Focus();
        search.Clear();
        e.Handled = true;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        var temporary = Path.Combine(Workspace.Temp, "temp");
        Directory.CreateDirectory(temporary);
        Environment.SetEnvironmentVariable("TEMP", temporary);
        Environment.SetEnvironmentVariable("TMP", temporary);
        base.OnStartup(e);
    }
}
