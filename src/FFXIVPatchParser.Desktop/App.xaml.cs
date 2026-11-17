using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FFXIVPatchParser.Desktop;

public partial class App : Application
{
    static App()
    {
        EventManager.RegisterClassHandler(typeof(FrameworkElement), Keyboard.PreviewGotKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler(ReplaceThemeFocusVisual), handledEventsToo: true);
    }

    private static void ReplaceThemeFocusVisual(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not FrameworkElement element || !ReferenceEquals(element, e.NewFocus)) return;
        var property = FrameworkElement.FocusVisualStyleProperty;
        // 原生主题可能直接指定虚线模板，应用级的默认焦点样式无法覆盖它。
        if (DependencyPropertyHelper.GetValueSource(element, property).BaseValueSource is BaseValueSource.DefaultStyle or BaseValueSource.DefaultStyleTrigger)
            element.SetCurrentValue(property, Current?.TryFindResource(SystemParameters.FocusVisualStyleKey));
    }

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
