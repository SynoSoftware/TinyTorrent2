using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Syno.TinyTorrent.Controls;

// PreferencesForm.xaml supplies the shared template for each setting.
public sealed partial class SettingsRow : ContentControl
{
    public static readonly DependencyProperty HeaderProperty = Text(nameof(Header));
    public static readonly DependencyProperty DescriptionProperty = Text(nameof(Description));
    public static readonly DependencyProperty ErrorProperty = Text(nameof(Error));
    public static readonly DependencyProperty CautionProperty = Text(nameof(Caution));
    public static readonly DependencyProperty DetailProperty = DependencyProperty.Register(nameof(Detail), typeof(object),
        typeof(SettingsRow), new PropertyMetadata(null, (row, args) => ((SettingsRow)row).OnDetail(args.OldValue, args.NewValue)));

    private long _detailVisibility;

    public string Header { get => (string)GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    public string Error { get => (string)GetValue(ErrorProperty); set => SetValue(ErrorProperty, value); }
    public string Caution { get => (string)GetValue(CautionProperty); set => SetValue(CautionProperty, value); }
    public object? Detail { get => GetValue(DetailProperty); set => SetValue(DetailProperty, value); }

    private static DependencyProperty Text(string name) => DependencyProperty.Register(name, typeof(string),
        typeof(SettingsRow), new PropertyMetadata(string.Empty, (row, _) => ((SettingsRow)row).Update()));

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        Update();
    }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        Update();
    }

    // A collapsed detail element still leaves the presenter's margin, so the
    // presenter follows the element's visibility.
    private void OnDetail(object? old, object? detail)
    {
        if (old is UIElement previous) previous.UnregisterPropertyChangedCallback(VisibilityProperty, _detailVisibility);
        if (detail is UIElement element) _detailVisibility = element.RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => Update());
        Update();
    }

    // Empty parts collapse, because an empty TextBlock still takes a line.
    private void Update()
    {
        Show("ErrorText", Error.Length > 0);
        Show("CautionText", Caution.Length > 0);
        Show("DetailPresenter", Detail is not null and not UIElement { Visibility: Visibility.Collapsed });
        var help = string.Join(" ", new[] { Error, Description, Caution }.Where(text => text.Length > 0));
        if (Content is UIElement control)
        {
            AutomationProperties.SetHelpText(control, help);
            ToolTipService.SetToolTip(control, help);
        }
        if (Detail is UIElement detail)
        {
            AutomationProperties.SetHelpText(detail, help);
            ToolTipService.SetToolTip(detail, help);
        }
    }

    private void Show(string part, bool visible)
    {
        if (GetTemplateChild(part) is UIElement element) element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }
}
