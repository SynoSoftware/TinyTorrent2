using Microsoft.UI.Xaml;

namespace Syno.TinyTorrent.Settings;

public sealed partial class SettingsPage
{
    /// <summary>Raised to open Library folders.</summary>
    internal event EventHandler? FoldersRequested;

    private async void OnMediaSwitch(object sender, RoutedEventArgs args)
    {
        // The binding's own update is no choice.
        if (MediaSwitch.IsOn != Model.DiscoversMedia)
            await Model.DiscoverMedia(MediaSwitch.IsOn);
    }

    private void OnGozlerInstall(object sender, RoutedEventArgs args) =>
        Main.DiscoverMedia.Execute(null);

    private void OnGozlerUpdate(object sender, RoutedEventArgs args) =>
        Main.UpdateGozler.Execute(null);

    private void OnMediaRetry(object sender, RoutedEventArgs args) =>
        Main.RefreshDiscovery.Execute(null);

    private void OnFolders(object sender, RoutedEventArgs args) =>
        FoldersRequested?.Invoke(this, EventArgs.Empty);

    // Back from Library folders returns focus to its link, inside Media
    // discovery, which stays open as the person left it.
    internal void FocusFolders()
    {
        if (MediaSection is null || FoldersLink is null)
            return;
        MediaSection.IsExpanded = true;
        FoldersLink.Focus(FocusState.Programmatic);
    }

    private void RefreshFoldersLink()
    {
        if (FoldersLink is null)
            return;
        FoldersLink.Header = Model.Text.Get("discovery", "folders");
    }
}
