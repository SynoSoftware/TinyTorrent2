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
        Bindings.Update();
        Destination.Header = Model.Text.Get("file_operation", "destination");
    }

    private void OnDestination(object sender, RoutedEventArgs args) => DestinationRequested?.Invoke(this, EventArgs.Empty);
    private async void OnRefresh(object sender, RoutedEventArgs args) => await Model.Files.RefreshScope();
}
