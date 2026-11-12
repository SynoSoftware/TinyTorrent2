using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Controls;

namespace Syno.TinyTorrent.Shell;

public sealed partial class AboutPage : UserControl
{
    private readonly SettingsLink _company = new()
    {
        Glyph = Segoe.Building2,
        EndGlyph = Segoe.ExternalLink,
    };
    private readonly SettingsLink _source = new()
    {
        Glyph = Segoe.CodeXml,
        EndGlyph = Segoe.ExternalLink,
    };
    private readonly SettingsLink _credits = new() { Glyph = Segoe.Users };
    public MainViewModel Main { get; }
    internal event EventHandler<Uri>? LinkRequested;
    internal event EventHandler? CreditsRequested;

    public AboutPage(MainViewModel main)
    {
        Main = main;
        InitializeComponent();
        Layout.PrimaryWidth = new GridLength(1, GridUnitType.Star);
        Layout.SecondaryWidth = new GridLength(1, GridUnitType.Star);
        Svg.Fit(Logo);
        AutomationProperties.SetAutomationId(_company, "AboutCompany");
        AutomationProperties.SetAutomationId(_source, "AboutSource");
        AutomationProperties.SetAutomationId(_credits, "AboutCredits");
        _company.Description = "synapse.sr";
        _company.Click += (_, _) => LinkRequested?.Invoke(this, new("https://synapse.sr/"));
        _source.Click += (_, _) =>
            LinkRequested?.Invoke(this, new(App.RepositoryPage));
        _credits.Click += (_, _) => CreditsRequested?.Invoke(this, EventArgs.Empty);
        Links.Children.Add(_company);
        Links.Children.Add(_source);
        Links.Children.Add(_credits);
        Loaded += (_, _) =>
        {
            Main.TextChanged += OnText;
            RefreshText();
        };
        Unloaded += (_, _) => Main.TextChanged -= OnText;
        RefreshText();
    }

    internal void FocusCredits() => _credits.Focus(FocusState.Programmatic);

    private void OnText(object? sender, EventArgs args) => RefreshText();

    private void RefreshText()
    {
        VersionLabel.Text = Main.Text.Get("about", "version_label");
        TelemetryHint.Text = Main.Text.Get("about", "telemetry_hint");
        AutomationProperties.SetHelpText(Promises, TelemetryHint.Text);
        _company.Header = Main.Text.Get("about", "company");
        _source.Header = Main.Text.Get("about", "source");
        _source.Description = Main.Text.Get("about", "source_detail");
        _credits.Header = Main.Text.Get("about", "credits");
    }
}
