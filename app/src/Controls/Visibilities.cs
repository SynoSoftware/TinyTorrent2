using Microsoft.UI.Xaml;

namespace Syno.TinyTorrent.Controls;

/// <summary>Visibility for x:Bind where the implicit bool conversion does not fit.</summary>
public static class Visibilities
{
    public static Visibility Unless(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility HasText(string text) =>
        text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
}
