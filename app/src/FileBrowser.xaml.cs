using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Syno.TinyTorrent;

public sealed partial class FileBrowser : UserControl
{
    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(nameof(Model), typeof(FileSelection), typeof(FileBrowser), new PropertyMetadata(null));
    public FileSelection Model { get => (FileSelection)GetValue(ModelProperty); set => SetValue(ModelProperty, value); }

    public FileBrowser(FileSelection model)
    {
        Model = model;
        InitializeComponent();
        RefreshText();
    }

    internal void RefreshText()
    {
        Search.PlaceholderText = Model.Text.Get("files", "search");
        All.Content = Model.Text.Get("files", "all_matching");
        None.Content = Model.Text.Get("files", "none_matching");
        Model.Refresh();
    }

    private void OnAll(object sender, RoutedEventArgs args) => Model.SelectMatching(true);
    private void OnNone(object sender, RoutedEventArgs args) => Model.SelectMatching(false);
}
