using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Syno.TinyTorrent.Controls;

public sealed partial class CheckStatus : UserControl
{
    public CheckStatus() => InitializeComponent();

    internal void Refresh(bool checking, bool? succeeded, string message, string tip)
    {
        Progress.IsActive = checking;
        Progress.Visibility = checking ? Visibility.Visible : Visibility.Collapsed;
        Success.Visibility = !checking && succeeded == true ? Visibility.Visible : Visibility.Collapsed;
        Failure.Visibility = !checking && succeeded == false ? Visibility.Visible : Visibility.Collapsed;
        Message.Text = message;
        AutomationProperties.SetHelpText(Message, tip);
        ToolTipService.SetToolTip(Root, tip);
    }
}
