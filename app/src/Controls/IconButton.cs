using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Syno.TinyTorrent.Controls;

// Gives a button one text as its accessible name and its tooltip, as the UI
// contract's button rulings require of an icon-only button.
internal static class IconButton
{
    // Sets each only when it changed, so a refresh does not replace an open
    // tooltip.
    internal static void Name(FrameworkElement button, string text)
    {
        if (AutomationProperties.GetName(button) != text)
        {
            AutomationProperties.SetName(button, text);
        }
        if (!Equals(ToolTipService.GetToolTip(button), text))
        {
            ToolTipService.SetToolTip(button, text);
        }
    }
}
