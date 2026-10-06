using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Syno.TinyTorrent.Views;

public sealed partial class FileForm : UserControl
{
    public MainViewModel Model { get; }
    public event EventHandler? DestinationRequested;

    public FileForm(MainViewModel model)
    {
        Model = model;
        InitializeComponent();
        RefreshText();
    }

    internal void RefreshText()
    {
        CurrentLabel.Text = Model.Text.Get("file_operation", "current");
        Destination.Header = Model.Text.Get("file_operation", "destination");
        Browse.Content = Model.Text.Get("add", "browse");
        ResultLabel.Text = Model.Text.Get("file_operation", "resulting");
        IncludeShared.Content = Model.Text.Get("file_operation", "include_shared");
        UseExisting.Content = Model.Text.Get("file_operation", "use_existing");
        ExistingWarning.Text = Model.Text.Get("file_operation", "existing_warning");
        Refresh.Content = Model.Text.Get("file_operation", "refresh");
    }

    private void OnDestination(object sender, RoutedEventArgs args) => DestinationRequested?.Invoke(this, EventArgs.Empty);
    private async void OnRefresh(object sender, RoutedEventArgs args) => await Model.Files.RefreshScope();
}
