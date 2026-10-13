using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace Syno.TinyTorrent.Controls;

public sealed partial class FieldHeader : UserControl
{
    public static readonly DependencyProperty LabelProperty = Property(nameof(Label));
    public static readonly DependencyProperty ErrorProperty = Property(nameof(Error));
    public static readonly DependencyProperty CautionProperty = Property(nameof(Caution));

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }
    public string Error
    {
        get => (string)GetValue(ErrorProperty);
        set => SetValue(ErrorProperty, value);
    }
    public string Caution
    {
        get => (string)GetValue(CautionProperty);
        set => SetValue(CautionProperty, value);
    }

    public FieldHeader()
    {
        InitializeComponent();
        Refresh();
    }

    private static DependencyProperty Property(string name) =>
        DependencyProperty.Register(
            name,
            typeof(string),
            typeof(FieldHeader),
            new PropertyMetadata(string.Empty, (header, _) => ((FieldHeader)header).Refresh())
        );

    private void Refresh()
    {
        var previousError = ErrorRun.Text;
        LabelRun.Text = Label;
        ErrorRun.Text =
            Error.Length > 0 ? (Label.Length > 0 ? " · " : string.Empty) + Error : string.Empty;
        if (Caution.Length > 0)
        {
            var button = (Button)FindName(nameof(CautionButton));
            button.Visibility = Visibility.Visible;
            CautionText.Text = Caution;
            AutomationProperties.SetName(button, Caution);
            ToolTipService.SetToolTip(button, Caution);
        }
        else if (CautionButton is not null)
        {
            CautionText.Text = string.Empty;
            CautionButton.Flyout.Hide();
            CautionButton.Visibility = Visibility.Collapsed;
        }
        var text = string.Join(
            " · ",
            new[] { Label, Error, Caution }.Where(value => value.Length > 0)
        );
        Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ToolTipService.SetToolTip(Text, text);
        AutomationProperties.SetHelpText(this, text);
        if (IsLoaded && Error.Length > 0 && ErrorRun.Text != previousError)
            FrameworkElementAutomationPeer.CreatePeerForElement(Text)
                ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
