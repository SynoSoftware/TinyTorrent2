using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Controls;

namespace Syno.TinyTorrent.Shell;

public sealed partial class CreditsPage : UserControl
{
    private sealed record Licence(string Name, string Address);

    private sealed record Credit(string Name, string Key, string Address, Licence? Licence = null);

    private sealed record Group(string Key, string Glyph, Credit[] Credits);

    private static readonly Group[] Contributions =
    [
        new(
            "built",
            Lucide.Package,
            [
                new(
                    "libtorrent",
                    "libtorrent",
                    "https://libtorrent.org/",
                    new(
                        "BSD-3-Clause",
                        "https://github.com/SynoSoftware/libtorrent/blob/627695bf2026faebbc0c1ec8575e2a8700af7f27/LICENSE"
                    )
                ),
                new(
                    "Windows App SDK / WinUI 3",
                    "winui",
                    "https://github.com/microsoft/WindowsAppSDK",
                    new(
                        "Microsoft",
                        "https://www.nuget.org/packages/Microsoft.WindowsAppSDK.WinUI/2.3.6/License"
                    )
                ),
                new(
                    ".NET",
                    "dotnet",
                    "https://github.com/dotnet/runtime",
                    new("MIT", "https://github.com/dotnet/runtime/blob/main/LICENSE.TXT")
                ),
                new(
                    "SQLite",
                    "sqlite",
                    "https://sqlite.org/",
                    new("Public domain", "https://sqlite.org/copyright.html")
                ),
                new(
                    "Microsoft.Data.Sqlite",
                    "data_sqlite",
                    "https://learn.microsoft.com/dotnet/standard/data/sqlite/",
                    new("MIT", "https://github.com/dotnet/efcore/blob/main/LICENSE.txt")
                ),
                new(
                    "SQLitePCLRaw",
                    "sqlite_pcl",
                    "https://github.com/ericsink/SQLitePCL.raw",
                    new("Apache-2.0", "https://www.apache.org/licenses/LICENSE-2.0")
                ),
                new(
                    "OpenSSL",
                    "openssl",
                    "https://openssl.org/",
                    new("Apache-2.0", "https://www.apache.org/licenses/LICENSE-2.0")
                ),
                new(
                    "Boost",
                    "boost",
                    "https://boost.org/",
                    new("Boost Software License 1.0", "https://www.boost.org/LICENSE_1_0.txt")
                ),
                new(
                    "nlohmann/json",
                    "json",
                    "https://github.com/nlohmann/json",
                    new("MIT", "https://github.com/nlohmann/json/blob/develop/LICENSE.MIT")
                ),
                new(
                    "Windows SDK .NET projections",
                    "windows_sdk",
                    "https://learn.microsoft.com/windows/apps/desktop/modernize/desktop-to-uwp-enhance",
                    new("Microsoft", "https://aka.ms/WinSDKLicenseURL")
                ),
                new(
                    "MaxMind.Db",
                    "maxmind_db",
                    "https://github.com/maxmind/MaxMind-DB-Reader-dotnet",
                    new("Apache-2.0", "https://www.apache.org/licenses/LICENSE-2.0")
                ),
                new(
                    "Win2D",
                    "win2d",
                    "https://github.com/microsoft/Win2D",
                    new("MIT", "https://github.com/microsoft/Win2D/blob/master/LICENSE.txt")
                ),
            ]
        ),
        new(
            "artwork",
            Lucide.Palette,
            [
                new(
                    "Lucide",
                    "lucide",
                    "https://lucide.dev/",
                    new("ISC / MIT", "https://lucide.dev/license")
                ),
                new(
                    "Fluent UI System Icons",
                    "fluent",
                    "https://github.com/microsoft/fluentui-system-icons",
                    new(
                        "MIT",
                        "https://github.com/microsoft/fluentui-system-icons/blob/main/LICENSE"
                    )
                ),
                new(
                    "Twemoji",
                    "twemoji",
                    "https://github.com/mozilla/twemoji-colr",
                    new("CC BY 4.0", "https://creativecommons.org/licenses/by/4.0/")
                ),
            ]
        ),
        new(
            "location",
            Lucide.MapPin,
            [
                new(
                    "DB-IP",
                    "dbip",
                    "https://db-ip.com/",
                    new("CC BY 4.0", "https://creativecommons.org/licenses/by/4.0/")
                ),
                new(
                    "Natural Earth",
                    "natural_earth",
                    "https://www.naturalearthdata.com/",
                    new("Public domain", "https://www.naturalearthdata.com/about/terms-of-use/")
                ),
            ]
        ),
        new(
            "video",
            Lucide.Film,
            [
                new("TMDB", "tmdb", "https://www.themoviedb.org/"),
                new("IMDb", "imdb", "https://www.imdb.com/"),
                new("Wikidata", "wikidata", "https://www.wikidata.org/"),
                new("OMDb", "omdb", "https://www.omdbapi.com/"),
                new("MDBList", "mdblist", "https://mdblist.com/"),
            ]
        ),
        new(
            "subtitles",
            Lucide.Captions,
            [
                new("OpenSubtitles", "opensubtitles", "https://www.opensubtitles.com/"),
                new("SubDL", "subdl", "https://subdl.com/"),
                new("SubSource", "subsource", "https://subsource.net/"),
            ]
        ),
        new(
            "connection",
            Lucide.Network,
            [new("Cloudflare", "cloudflare", "https://speed.cloudflare.com/")]
        ),
    ];

    private readonly List<(SettingsSection Section, string Key)> _sections = [];
    private readonly List<(SettingsRow Row, string Key)> _rows = [];
    private readonly List<(SettingsLink Link, string Key)> _links = [];
    private readonly List<(TextBlock Text, string Section, string Key)> _notices = [];
    private TextBlock? _publicDomain;
    private readonly SettingsSection _licences = new() { Glyph = Lucide.ScrollText };
    private readonly TextBlock _project = new()
    {
        Style = (Style)Application.Current.Resources["TinyTorrentTitleDetailTextStyle"],
    };
    private readonly TextBlock _licence = new()
    {
        Style = (Style)Application.Current.Resources["TinyTorrentTitleDetailTextStyle"],
    };
    private readonly TextBlock _licenceNote = new()
    {
        Style = (Style)Application.Current.Resources["TinyTorrentTitleDetailTextStyle"],
        TextWrapping = TextWrapping.Wrap,
        Margin = new(0, 16, 0, 16),
    };
    public MainViewModel Main { get; }
    internal event EventHandler<Uri>? LinkRequested;
    internal event EventHandler? ReturnRequested;

    public CreditsPage(MainViewModel main)
    {
        Main = main;
        InitializeComponent();
        foreach (var group in Contributions)
        {
            if (group.Credits.Length == 1)
            {
                var credit = group.Credits[0];
                var link = new SettingsLink
                {
                    Header = credit.Name,
                    Glyph = group.Glyph,
                    EndGlyph = Lucide.ExternalLink,
                };
                AutomationProperties.SetAutomationId(link, "Credit_" + credit.Key);
                link.Click += (_, _) => LinkRequested?.Invoke(this, new(credit.Address));
                _links.Add((link, credit.Key));
                Groups.Children.Add(link);
                continue;
            }
            var panel = new StackPanel();
            var section = new SettingsSection { Glyph = group.Glyph, Content = panel };
            _sections.Add((section, group.Key));
            foreach (var credit in group.Credits)
            {
                var icon = new FontIcon
                {
                    FontFamily = Lucide.Font,
                    Glyph = Lucide.ExternalLink,
                    FontSize = (double)Application.Current.Resources["TinyTorrentInlineIconSize"],
                };
                AutomationProperties.SetAccessibilityView(
                    icon,
                    Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw
                );
                var link = new HyperlinkButton { Content = icon };
                AutomationProperties.SetAutomationId(link, "Credit_" + credit.Key);
                AutomationProperties.SetName(link, credit.Name);
                link.Click += (_, _) => LinkRequested?.Invoke(this, new(credit.Address));
                var row = new SettingsRow { Header = credit.Name, Content = link };
                if (credit == group.Credits[^1])
                    row.BorderThickness = new(0);
                _rows.Add((row, credit.Key));
                panel.Children.Add(row);
                if (credit.Key == "tmdb")
                {
                    var logo = new Image
                    {
                        Source = new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource(
                            new("ms-appx:///Assets/tmdb.svg")
                        ),
                        Width = 64,
                        Height = 32,
                    };
                    AutomationProperties.SetAccessibilityView(
                        logo,
                        Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw
                    );
                    row.Icon = logo;
                }
                if (credit.Key is "tmdb" or "imdb" or "omdb")
                {
                    var notice = new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        Style = (Style)
                            Application.Current.Resources["TinyTorrentTitleDetailTextStyle"],
                    };
                    var detail = new StackPanel { Spacing = 8, Children = { notice } };
                    if (credit.Key == "omdb")
                    {
                        _notices.Add((notice, "credits", "omdb_notice"));
                        var licence = new HyperlinkButton
                        {
                            Content = "CC BY-NC 4.0",
                            HorizontalAlignment = HorizontalAlignment.Left,
                            Padding = new(0),
                        };
                        licence.Click += (_, _) =>
                            LinkRequested?.Invoke(
                                this,
                                new("https://creativecommons.org/licenses/by-nc/4.0/")
                            );
                        detail.Children.Add(licence);
                    }
                    else
                        _notices.Add((notice, "about", credit.Key));
                    row.Detail = new Border
                    {
                        Style = (Style)Resources["AttributionStyle"],
                        Child = detail,
                    };
                }
            }
            Groups.Children.Add(section);
        }
        InitializeLicences();
        Groups.Children.Add(_licences);
        Loaded += (_, _) =>
        {
            Main.TextChanged += OnText;
            RefreshText();
        };
        Unloaded += (_, _) => Main.TextChanged -= OnText;
        RefreshText();
    }

    internal void FocusHeading() => Hierarchy.Focus(FocusState.Programmatic);

    private void OnHierarchy(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Index == 0)
            ReturnRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnText(object? sender, EventArgs args) => RefreshText();

    private void RefreshText()
    {
        var trail = new[] { Main.Text.Get("about", "title"), Main.Text.Get("about", "credits") };
        if (Hierarchy.ItemsSource is not string[] current || !current.SequenceEqual(trail))
            Hierarchy.ItemsSource = trail;
        Introduction.Text = Main.Text.Get("credits", "introduction");
        foreach (var (section, key) in _sections)
        {
            section.Header = Main.Text.Get("credits", key);
            section.Description = Main.Text.Get("credits", key + "_hint");
        }
        foreach (var (row, key) in _rows)
            row.Description = Main.Text.Get("credits", key);
        foreach (var (link, key) in _links)
            link.Description = Main.Text.Get("credits", key);
        foreach (var (notice, section, key) in _notices)
            notice.Text = Main.Text.Get(section, key);
        _licences.Header = Main.Text.Get("credits", "licences");
        _licences.Description = Main.Text.Get("credits", "licences_hint");
        _project.Text = Main.Text.Get("credits", "project");
        _licence.Text = Main.Text.Get("credits", "licence");
        _licenceNote.Text = Main.Text.Get("credits", "licences_note");
        if (_publicDomain is not null)
            _publicDomain.Text = Main.Text.Get("credits", "public_domain");
    }

    private void InitializeLicences()
    {
        var table = new Grid { ColumnSpacing = 24, Margin = new(0, 8, 0, 0) };
        table.ColumnDefinitions.Add(new() { Width = new(2, GridUnitType.Star) });
        table.ColumnDefinitions.Add(new() { Width = new(3, GridUnitType.Star) });
        table.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _project.Margin = _licence.Margin = new(0, 12, 0, 12);
        Grid.SetColumn(_licence, 1);
        table.Children.Add(_project);
        table.Children.Add(_licence);
        var credits = Contributions
            .SelectMany(group => group.Credits)
            .Prepend(
                new(
                    "TinyTorrent",
                    "tinytorrent",
                    "https://github.com/SynoSoftware/TinyTorrent2",
                    new("MPL-2.0", "https://www.mozilla.org/MPL/2.0/")
                )
            );
        foreach (var credit in credits)
        {
            if (credit.Licence is not { } terms)
                continue;
            var index = table.RowDefinitions.Count;
            table.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var divider = new Border { Style = (Style)Resources["LicenceDividerStyle"] };
            Grid.SetRow(divider, index);
            Grid.SetColumnSpan(divider, 2);
            table.Children.Add(divider);
            var project = new HyperlinkButton
            {
                Content = new TextBlock { Text = credit.Name, TextWrapping = TextWrapping.Wrap },
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new(0, 12, 0, 12),
            };
            AutomationProperties.SetName(project, credit.Name);
            project.Click += (_, _) => LinkRequested?.Invoke(this, new(terms.Address));
            var licence = new TextBlock
            {
                Text = terms.Name,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (credit.Key == "sqlite")
                _publicDomain = licence;
            Grid.SetRow(project, index);
            Grid.SetRow(licence, index);
            Grid.SetColumn(licence, 1);
            table.Children.Add(project);
            table.Children.Add(licence);
        }
        _licences.Content = new StackPanel { Children = { table, _licenceNote } };
    }
}
