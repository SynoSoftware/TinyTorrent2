using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Shared;

namespace Syno.TinyTorrent.Controls;

// A statistic value with a download amount and an upload amount, each in its
// direction's colour. The colours are theme resources in XAML, so they follow
// a live theme change.
public sealed partial class TransferPair : UserControl
{
    public TransferPair() => InitializeComponent();

    internal void Show(string download, string upload, string description, Strings text)
    {
        Download.Text = text.Format("units", "download", download);
        Upload.Text = text.Format("units", "upload", upload);
        AutomationProperties.SetName(Value, description);
        ToolTipService.SetToolTip(Value, description);
    }
}
