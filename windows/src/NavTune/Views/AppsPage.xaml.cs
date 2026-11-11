using System.Windows.Controls;
using NavTune.ViewModels;

namespace NavTune.Views;

public partial class AppsPage : UserControl
{
    private bool watching;

    public AppsPage()
    {
        InitializeComponent();
        // Refresh the list once a second, only while the page is on screen.
        IsVisibleChanged += (_, _) => Watch(IsVisible);
        Unloaded += (_, _) => Watch(false);
    }

    private void Watch(bool on)
    {
        if (on == watching || DataContext is not AppState state) return;
        watching = on;
        state.WatchApps(on);
    }
}
