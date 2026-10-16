using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Syno.TinyTorrent.Shared;

/// <summary>
/// The flyout that tells what an online provider receives and links to the
/// provider's own policies.
/// </summary>
internal static class Disclosure
{
    internal static Flyout Create(Strings text, string name, string body, Uri privacy, Uri terms)
    {
        var panel = new StackPanel { Spacing = 12, MaxWidth = 400 };
        panel.Children.Add(
            new TextBlock
            {
                Text = name,
                Style = (Style)Application.Current.Resources["TinyTorrentGroupTitleTextStyle"],
            }
        );
        panel.Children.Add(new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(
            new HyperlinkButton { Content = text.Get("settings", "privacy"), NavigateUri = privacy }
        );
        panel.Children.Add(
            new HyperlinkButton { Content = text.Get("settings", "terms"), NavigateUri = terms }
        );
        return new Flyout { Content = panel };
    }
}
