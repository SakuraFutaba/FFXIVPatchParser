using System.Windows;
using System.Windows.Media.Animation;

namespace FFXIVPatchParser.Desktop;

public partial class MainWindow
{
    private int toastRevision;

    internal static void Notify(DependencyObject origin, string message)
    {
        if (GetWindow(origin) is MainWindow window) window.ShowToast(message);
    }

    private void ShowToast(string message)
    {
        var revision = ++toastRevision;
        ToastText.Text = message;
        ToastSurface.Visibility = Visibility.Visible;
        var animation = new DoubleAnimationUsingKeyFrames();
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2))));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.35))));
        animation.Completed += (_, _) =>
        {
            if (revision == toastRevision) ToastSurface.Visibility = Visibility.Collapsed;
        };
        ToastSurface.BeginAnimation(OpacityProperty, animation);
    }
}
