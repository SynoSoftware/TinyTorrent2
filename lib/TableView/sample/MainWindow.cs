using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Syno.TableViewSample.Board;
using Syno.TableViewSample.Jobs;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Syno.TableViewSample;

/// <summary>
/// The application window. It hosts the sample's two consumers: a render farm's job list and a
/// departures board, which share nothing but the control.
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>Sample diagnostics use this to resize the client area.</summary>
    public static MainWindow? Instance;

    public MainWindow()
    {
        Instance = this;
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");

        RootFrame.Navigate(typeof(JobsPage));
    }

    private void OnPageSelected(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) =>
        RootFrame.Navigate(sender.Items.IndexOf(sender.SelectedItem) == 1
            ? typeof(DeparturesPage)
            : typeof(JobsPage));
}
