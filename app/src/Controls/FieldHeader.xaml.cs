using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Syno.TinyTorrent.Controls;

public sealed partial class FieldHeader : UserControl
{
    public static readonly DependencyProperty LabelProperty = Property(nameof(Label));
    public static readonly DependencyProperty ErrorProperty = Property(nameof(Error));
    public static readonly DependencyProperty CautionProperty = Property(nameof(Caution));

    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string Error { get => (string)GetValue(ErrorProperty); set => SetValue(ErrorProperty, value); }
    public string Caution { get => (string)GetValue(CautionProperty); set => SetValue(CautionProperty, value); }

    public FieldHeader()
    {
        InitializeComponent();
        Refresh();
    }

    private static DependencyProperty Property(string name) => DependencyProperty.Register(
        name, typeof(string), typeof(FieldHeader), new PropertyMetadata(string.Empty, (header, _) => ((FieldHeader)header).Refresh()));

    private void Refresh()
    {
        LabelRun.Text = Label;
        ErrorRun.Text = Error.Length > 0 ? (Label.Length > 0 ? " · " : string.Empty) + Error : string.Empty;
        CautionRun.Text = Caution.Length > 0 ? (Label.Length > 0 || Error.Length > 0 ? " · " : string.Empty) + Caution : string.Empty;
        var text = string.Join(" · ", new[] { Label, Error, Caution }.Where(value => value.Length > 0));
        Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ToolTipService.SetToolTip(Text, text);
        AutomationProperties.SetHelpText(this, text);
    }
}
