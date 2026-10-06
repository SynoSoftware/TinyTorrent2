using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Syno.TinyTorrent;

// One setting inside a SettingsSection: title and description on the left, the
// control on the right, and an optional full-width detail below them. The
// implicit style in PreferencesForm.xaml draws it.
public sealed partial class SettingsRow : ContentControl
{
    public static readonly DependencyProperty HeaderProperty = Text(nameof(Header));
    public static readonly DependencyProperty DescriptionProperty = Text(nameof(Description));
    public static readonly DependencyProperty ErrorProperty = Text(nameof(Error));
    public static readonly DependencyProperty DetailProperty = DependencyProperty.Register(nameof(Detail), typeof(object),
        typeof(SettingsRow), new PropertyMetadata(null, (row, _) => ((SettingsRow)row).Update()));

    public string Header { get => (string)GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    public string Error { get => (string)GetValue(ErrorProperty); set => SetValue(ErrorProperty, value); }
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

    // Empty parts collapse, because an empty TextBlock still takes a line.
    private void Update()
    {
        Show("DescriptionText", Description.Length > 0);
        Show("ErrorText", Error.Length > 0);
        Show("DetailPresenter", Detail is not null);
        if (Content is UIElement control) AutomationProperties.SetHelpText(control, Description);
    }

    private void Show(string part, bool visible)
    {
        if (GetTemplateChild(part) is UIElement element) element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }
}
