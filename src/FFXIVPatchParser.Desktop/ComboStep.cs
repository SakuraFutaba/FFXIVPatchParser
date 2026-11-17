using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FFXIVPatchParser.Desktop;

public static class ComboStep
{
    public static readonly DependencyProperty HasExternalButtonsProperty =
        DependencyProperty.RegisterAttached("HasExternalButtons", typeof(bool), typeof(ComboStep));
    public static bool GetHasExternalButtons(DependencyObject control) => (bool)control.GetValue(HasExternalButtonsProperty);
    public static void SetHasExternalButtons(DependencyObject control, bool value) => control.SetValue(HasExternalButtonsProperty, value);

    public static RoutedCommand Previous { get; } = new(nameof(Previous), typeof(ComboStep));
    public static RoutedCommand Next { get; } = new(nameof(Next), typeof(ComboStep));

    static ComboStep()
    {
        Register(Previous, -1);
        Register(Next, 1);
        EventManager.RegisterClassHandler(typeof(ComboBox), System.Windows.Controls.Primitives.Selector.SelectionChangedEvent,
            new SelectionChangedEventHandler((_, _) => CommandManager.InvalidateRequerySuggested()));
    }

    private static void Register(RoutedCommand command, int step) =>
        CommandManager.RegisterClassCommandBinding(typeof(ComboBox), new CommandBinding(command,
            (sender, e) =>
            {
                var combo = (ComboBox)sender;
                combo.SetCurrentValue(ComboBox.SelectedIndexProperty, Target(combo, step));
                e.Handled = true;
            },
            (sender, e) =>
            {
                var combo = (ComboBox)sender;
                var target = Target(combo, step);
                e.CanExecute = combo.IsEnabled && target >= 0 && target < combo.Items.Count;
                e.Handled = true;
            }));

    private static int Target(ComboBox combo, int step) => combo.SelectedIndex < 0 ?
        (step < 0 ? combo.Items.Count - 1 : 0) : combo.SelectedIndex + step;
}
