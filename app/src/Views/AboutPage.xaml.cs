using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Controls;
using Windows.UI.ViewManagement;

namespace Syno.TinyTorrent.Views;

public sealed partial class AboutPage : UserControl
{
    private readonly UISettings _display = new();
    private readonly SettingsLink _company = new()
    {
        Glyph = Lucide.Building2,
        EndGlyph = Lucide.ExternalLink,
    };
    private readonly SettingsLink _source = new()
    {
        Glyph = Lucide.CodeXml,
        EndGlyph = Lucide.ExternalLink,
    };
    private readonly SettingsLink _credits = new() { Glyph = Lucide.Users };
    public MainViewModel Main { get; }
    internal event EventHandler<Uri>? LinkRequested;
    internal event EventHandler? CreditsRequested;

    public AboutPage(MainViewModel main)
    {
        Main = main;
        InitializeComponent();
        AutomationProperties.SetAutomationId(_company, "AboutCompany");
        AutomationProperties.SetAutomationId(_source, "AboutSource");
        AutomationProperties.SetAutomationId(_credits, "AboutCredits");
        _company.Description = "synapse.sr";
        _company.Click += (_, _) => LinkRequested?.Invoke(this, new("https://synapse.sr/"));
        _source.Click += (_, _) =>
            LinkRequested?.Invoke(this, new("https://github.com/SynoSoftware/TinyTorrent2"));
        _credits.Click += (_, _) => CreditsRequested?.Invoke(this, EventArgs.Empty);
        Links.Children.Add(_company);
        Links.Children.Add(_source);
        Links.Children.Add(_credits);
        Loaded += (_, _) =>
        {
            Main.TextChanged += OnText;
            _display.TextScaleFactorChanged += OnTextScale;
            RefreshText();
        };
        Unloaded += (_, _) =>
        {
            Main.TextChanged -= OnText;
            _display.TextScaleFactorChanged -= OnTextScale;
        };
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
        UpdateColumns();
    }

    private void OnLayoutSize(object sender, SizeChangedEventArgs args) => UpdateColumns();

    private void OnTextScale(UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(UpdateColumns);

    private void UpdateColumns()
    {
        var stacked = Layout.ActualWidth < 720 * _display.TextScaleFactor;
        Layout.ColumnDefinitions[1].Width = stacked ? new(0) : new(1, GridUnitType.Star);
        Layout.ColumnSpacing = stacked ? 0 : 24;
        Layout.RowSpacing = stacked ? 24 : 0;
        Identity.Padding =
            stacked ? new(0, 0, 0, 24)
            : Main.Text.IsRightToLeft ? new(32, 0, 0, 0)
            : new(0, 0, 32, 0);
        Identity.BorderThickness =
            stacked ? new(0, 0, 0, 1)
            : Main.Text.IsRightToLeft ? new(1, 0, 0, 0)
            : new(0, 0, 1, 0);
        Grid.SetColumn(Links, stacked ? 0 : 1);
        Grid.SetRow(Links, stacked ? 1 : 0);
    }
}
