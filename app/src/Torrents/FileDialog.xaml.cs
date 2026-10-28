using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Shell;

namespace Syno.TinyTorrent.Torrents;

// Shows Move files or Remove torrents: the parts both share bind Draft, and
// each dialog's own parts bind its draft and show only for it.
public sealed partial class FileDialog : UserControl
{
    public MainViewModel Model { get; }
    public FileDraft Draft { get; }
    public MoveDraft? Move => Draft as MoveDraft;
    public RemovalDraft? Removal => Draft as RemovalDraft;
    public bool ShowsMove => Move is not null;
    public bool ShowsRemoval => Removal is not null;
    public event EventHandler? DestinationRequested;

    public FileDialog(MainViewModel model, FileDraft draft)
    {
        Model = model;
        Draft = draft;
        InitializeComponent();
        RefreshText();
    }

    internal void RefreshText() => Bindings.Update();

    private void OnDestination(object sender, RoutedEventArgs args) =>
        DestinationRequested?.Invoke(this, EventArgs.Empty);

    private async void OnRefresh(object sender, RoutedEventArgs args) =>
        await Draft.RefreshScope();
}
