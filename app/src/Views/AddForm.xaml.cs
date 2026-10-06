using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Windows.ApplicationModel.DataTransfer;
using Syno.TinyTorrent.Controls;

namespace Syno.TinyTorrent.Views;

public sealed partial class AddForm : UserControl
{
    public MainViewModel Model { get; }
    private readonly FileBrowser _browser;
    public event EventHandler? FilesRequested;
    public event EventHandler? DestinationRequested;

    public AddForm(MainViewModel model)
    {
        Model = model;
        InitializeComponent();
        _browser = new FileBrowser(model.Draft.Files);
        _browser.SetBinding(FileBrowser.ModelProperty, new Binding { Source = model.Draft, Path = new PropertyPath(nameof(AddDraft.Files)), Mode = BindingMode.OneWay });
        FileHost.Content = _browser;
        RefreshText();
    }

    internal void RefreshText()
    {
        Bindings.Update();
        Files.Text = Model.Text.Get("commands", "add_file");
        Magnet.Text = Model.Text.Get("commands", "add_magnet");
        MagnetInput.Header = Model.Text.Get("add", "magnet");
        _browser.RefreshText();
    }

    public static Visibility Hidden(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
    private void OnFiles(object sender, RoutedEventArgs args) => FilesRequested?.Invoke(this, EventArgs.Empty);
    private void OnDestination(object sender, RoutedEventArgs args) => DestinationRequested?.Invoke(this, EventArgs.Empty);
    private async void OnPaste(object sender, RoutedEventArgs args)
    {
        if (!Model.Draft.CanEdit) return;
        try
        {
            var content = Clipboard.GetContent();
            if (content.Contains(StandardDataFormats.Text))
            {
                var text = await content.GetTextAsync();
                if (IsLoaded && Model.Draft.CanEdit) Model.Draft.Magnet = text;
            }
        }
        catch (Exception error) { Model.Report(error); }
    }
    private async void OnPreview(object sender, RoutedEventArgs args)
    {
        await Model.Draft.PrepareMagnet();
        FocusMagnetError();
    }

    internal void FocusMagnetError()
    {
        if (Model.Draft.HasMagnetError) MagnetInput.Focus(FocusState.Programmatic);
    }
}
