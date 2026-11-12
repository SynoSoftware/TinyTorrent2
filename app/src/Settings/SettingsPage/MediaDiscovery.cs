using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Controls;

namespace Syno.TinyTorrent.Settings;

public sealed partial class SettingsPage
{
    private async void OnMediaSwitch(object sender, RoutedEventArgs args)
    {
        // The binding's own update is no choice.
        if (MediaSwitch.IsOn != Model.DiscoversMedia)
            await Model.DiscoverMedia(MediaSwitch.IsOn);
    }

    private void OnGozlerInstall(object sender, RoutedEventArgs args) =>
        Main.DiscoverMedia.Execute(null);

    private void OnMediaRetry(object sender, RoutedEventArgs args) =>
        Main.RefreshDiscovery.Execute(null);

    // One row for each hidden folder: its name, its path and Remove.
    private void ShowHiddenFolders()
    {
        if (HiddenFolders is null)
            return;
        var folders = Model.HiddenFolders;
        HiddenRow.BorderThickness = folders.Count > 0 ? new(0, 0, 0, 1) : new(0);
        HiddenFolders.Children.Clear();
        for (var index = 0; index < folders.Count; index++)
        {
            var folder = folders[index];
            var position = index;
            var remove = new ActionButton
            {
                Glyph = Segoe.X,
                Text = Model.Text.Get("discovery", "remove"),
            };
            ToolTipService.SetToolTip(remove, Model.Text.Get("discovery", "remove_tip"));
            AutomationProperties.SetName(remove, Model.Text.Format("discovery", "remove_name", folder));
            AutomationProperties.SetAutomationId(remove, "HiddenFolderRemove");
            remove.Click += async (_, _) => await Unhide(folder, position);
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
            var row = new SettingsRow
            {
                Header = name.Length > 0 ? name : folder,
                Description = folder,
                Content = remove,
            };
            if (index == folders.Count - 1)
                row.BorderThickness = new(0);
            HiddenFolders.Children.Add(row);
        }
    }

    // Focus moves to the next folder's Remove, or the one before it, or the
    // switch when no folder is left, so it never falls out of Settings.
    private async Task Unhide(string folder, int position)
    {
        await Model.Unhide(folder);
        var rows = HiddenFolders.Children.OfType<SettingsRow>().ToArray();
        Control target = rows.Length > 0
            ? (Control)rows[Math.Min(position, rows.Length - 1)].Content
            : MediaSwitch;
        target.Focus(FocusState.Programmatic);
    }
}
