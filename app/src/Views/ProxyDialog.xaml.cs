using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Helpers;

namespace Syno.TinyTorrent.Views;

public sealed partial class ProxyDialog : UserControl
{
    public MainViewModel Model { get; }
    public Proxy Proxy => Model.Settings.Proxy;

    public ProxyDialog(MainViewModel model)
    {
        Model = model;
        InitializeComponent();
        // The dialog shows the Check result in its footer, outside this content.
        Root.Children.Remove(Status);
    }

    internal void RefreshText() => Bindings.Update();

    public static string Separated(string error) => error.Length > 0 ? " · " + error : string.Empty;

    // NumberBox changes Text only when its input is committed, so the dialog
    // reads what is typed from its editor, as Settings does.
    private void OnPortLoaded(object sender, RoutedEventArgs args)
    {
        if (TextEditor.Find(Port) is { } editor)
            editor.TextChanged += (_, _) => Proxy.Port = editor.Text;
    }
}
