using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Helpers;

namespace Syno.TinyTorrent.Views;

public sealed partial class SpeedLimitForm : UserControl
{
    public MainViewModel Model { get; }

    public SpeedLimitForm(MainViewModel model)
    {
        Model = model;
        InitializeComponent();
    }

    internal void RefreshText() => Bindings.Update();

    // NumberBox changes Text only when its input is committed, so the dialog
    // reads what is typed from its editor, as Settings does.
    private void OnNumberLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is not NumberBox number || TextEditor.Find(number) is not { } editor) return;
        editor.TextChanged += (_, _) =>
        {
            if (number == Download) Model.SpeedLimit.Download = editor.Text;
            else Model.SpeedLimit.Upload = editor.Text;
        };
    }
}
