using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Syno.TinyTorrent.Models;
using Windows.Foundation;

namespace Syno.TinyTorrent.Views;

public sealed partial class SettingsPage
{
    private string VideoProvider => Main.Library.IsEnrichmentEnabled && Main.Library.Option is { } option
        ? option.Name : Model.OffText;
    private TextBlock Heading => _category is null ? IdentityTitle : PageTitle;

    private void RefreshHeader()
    {
        if (!IsLoaded || Main.Navigation.Current != WindowPage.Settings)
            return;
        var category = _category is null ? (SettingsCategory?)null
            : (SettingsCategory)Array.IndexOf(CategoryItems, _category);
        PageTitle.Text = ((TextBlock)(_category ?? HomeCategory).Content).Text;
        ToolTipService.SetToolTip(PageTitle, PageTitle.Text);
        PageTitle.Visibility = category is null ? Visibility.Collapsed : Visibility.Visible;
        var (subject, summary) = category switch
        {
            SettingsCategory.Transfers => (Model.Text.Get("settings", "files"), Model.Destination.ConfirmedText),
            SettingsCategory.Limits => (Model.Text.Get("transfer_limits", "control"), Main.LimitsNow),
            SettingsCategory.Schedule => (Model.Text.Get("settings", "weekly_schedule"), Main.ScheduleSummary),
            SettingsCategory.Network => (Model.SelectedAdapter?.Text ?? "—", Main.IsConnected ? Main.IncomingStatus : "—"),
            SettingsCategory.Plugins => (Model.Text.Get("library", "title"), Main.Library.Status),
            null => (PageTitle.Text, Model.Text.Format("settings", "home_activity",
                Main.Torrents.Count(torrent => torrent.StatusCode is "downloading" or "metadata"),
                Main.Torrents.Count(torrent => torrent.StatusCode == "seeding"))),
            _ => (Main.AboutTitle, Main.VersionText),
        };
        IdentityTitle.Text = subject;
        IdentityTitle.Style = (Style)Application.Current.Resources[category is null ? "TinyTorrentPageTitleTextStyle" : "SubtitleTextBlockStyle"];
        ToolTipService.SetToolTip(IdentityTitle, subject);
        AutomationProperties.SetHeadingLevel(IdentityTitle, category is null ? AutomationHeadingLevel.Level1 : AutomationHeadingLevel.Level2);
        PageSummary.Text = summary;
        PageSummary.Visibility = summary.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ToolTipService.SetToolTip(PageSummary, PageSummary.Text);

        if (category is null)
        {
            if (PageArtwork.Source is not SvgImageSource)
                PageArtwork.Source = new SvgImageSource(new Uri("ms-appx:///Assets/tinyTorrent.svg"));
        }
        else
        {
            var artwork = category == SettingsCategory.Limits ? "speed" : category.Value.ToString().ToLowerInvariant();
            var uri = new Uri("ms-appx:///Assets/Settings/" + artwork + ".png");
            if (PageArtwork.Source is not BitmapImage image || image.UriSource != uri)
                PageArtwork.Source = new BitmapImage(uri);
        }

        (string Glyph, string Caption, string Value) first;
        (string Glyph, string Caption, string Value) second;
        switch (category)
        {
            case SettingsCategory.General:
                first = (Lucide.File, Model.Text.Get("settings", "torrent_files"), Model.HasRegistration ? Model.TorrentApp : "—");
                second = (Lucide.Power, Model.Text.Get("settings", "start_signin"),
                    !Model.HasRegistration ? "—" : Model.Startup ? Model.OnText : Model.OffText);
                break;
            case SettingsCategory.Transfers:
                first = (Lucide.ListOrdered, Model.Text.Get("settings", "queue"), Model.QueueSummary);
                second = (Lucide.Upload, Model.Text.Get("settings", "seeding"), Model.SeedingSummary);
                break;
            case SettingsCategory.Limits:
                first = (Lucide.Gauge, Model.Text.Get("settings", "standard_caps"), Model.StandardSummary);
                second = (Lucide.Gauge, Model.Text.Get("settings", "alternative_caps"), Model.AlternativeSummary);
                break;
            case SettingsCategory.Schedule:
                first = (Lucide.SlidersHorizontal, Model.Text.Get("transfer_limits", "control"), Main.LimitsHeading);
                second = (Lucide.CalendarClock, Model.Text.Get("settings", "periods"),
                    Model.Schedule.Periods.Count.ToString("N0", CultureInfo.CurrentCulture));
                break;
            case SettingsCategory.Appearance:
                first = (Lucide.Palette, Model.Text.Get("settings", "theme"),
                    Model.Text.Get("settings", Model.Theme.Input switch { "light" => "light_theme", "dark" => "dark_theme", _ => "system_theme" }));
                second = (Lucide.Languages, Model.Text.Get("settings", "language"), Model.Text.LanguageName(Model.Text.Language));
                break;
            case SettingsCategory.Network:
                first = (Lucide.Globe, Model.Text.Get("settings", "proxy_server"), Model.Proxy.Summary);
                second = (Lucide.Shield, Model.Text.Get("settings", "encryption"), Model.EncryptionSummary);
                break;
            case SettingsCategory.Plugins:
                first = (Lucide.Film, Model.Text.Get("settings", "video_information"), VideoProvider);
                second = (Lucide.Captions, Model.Text.Get("subtitles", "supplier"), Main.Subtitles?.SupplierId.ToString() ?? "—");
                break;
            case SettingsCategory.Advanced:
                first = (Lucide.Check, Model.Text.Get("settings", "checking"), Model.CheckingSummary);
                second = (Lucide.ArrowDownUp, Model.Text.Get("settings", "bandwidth_accounting"), Model.AccountingSummary);
                break;
            default:
                first = (Lucide.Clock, Model.Text.Get("settings", "session_totals"), string.Empty);
                second = (Lucide.History, Model.Text.Get("settings", "lifetime_totals"), string.Empty);
                break;
        }

        var style = (Style)Application.Current.Resources[category is null ? "SubtitleTextBlockStyle" : "TinyTorrentTitleDetailTextStyle"];
        var captionStyle = (Style)Application.Current.Resources[category is null ? "TinyTorrentTitleDetailTextStyle" : "BodyStrongTextBlockStyle"];
        FirstCaption.Style = captionStyle;
        SecondCaption.Style = captionStyle;
        ShowFact(FirstIcon, FirstCaption, FirstValue, first, style);
        ShowFact(SecondIcon, SecondCaption, SecondValue, second, style);
        if (category is null)
        {
            ShowTotals(FirstValue, Main.SessionTotals);
            ShowTotals(SecondValue, Main.LifetimeTotals);
        }
        ArrangeHeader();
    }

    private static void ShowFact(FontIcon icon, TextBlock caption, TextBlock value,
        (string Glyph, string Caption, string Value) fact, Style style)
    {
        icon.Glyph = fact.Glyph;
        caption.Text = fact.Caption;
        ToolTipService.SetToolTip(caption, fact.Caption);
        value.Style = style;
        value.Text = fact.Value;
        ToolTipService.SetToolTip(value, fact.Value);
        AutomationProperties.SetName(value, fact.Value);
    }

    private void ShowTotals(TextBlock block, TransferTotals totals)
    {
        block.Text = Main.IsConnected
            ? Model.Text.Format("settings", "transfer_totals", Model.Text.Bytes(totals.Downloaded), Model.Text.Bytes(totals.Uploaded))
            : "—";
        var description = Main.IsConnected
            ? Model.Text.Format("settings", "transfer_totals_hint", Model.Text.Bytes(totals.Downloaded), Model.Text.Bytes(totals.Uploaded))
            : Model.Text.Get("connection", "unavailable");
        ToolTipService.SetToolTip(block, description);
        AutomationProperties.SetName(block, description);
    }

    private void OnHeaderSize(object sender, SizeChangedEventArgs args) => ArrangeHeader();

    private void ArrangeHeader()
    {
        var width = PageHeader.ActualWidth;
        if (width <= 0)
            return;
        var available = new Size(double.PositiveInfinity, double.PositiveInfinity);
        HeaderIdentity.Measure(available);
        FirstFact.Measure(available);
        SecondFact.Measure(available);
        var factsWidth = FirstFact.DesiredSize.Width + HeaderFacts.ColumnSpacing + SecondFact.DesiredSize.Width;
        var below = HeaderIdentity.DesiredSize.Width + PageHeader.ColumnSpacing + factsWidth > width;
        var stacked = factsWidth > width;
        PageHeader.RowSpacing = below ? 16 : 0;
        HeaderFacts.RowSpacing = stacked ? 16 : 0;
        Grid.SetColumn(HeaderFacts, below ? 0 : 1);
        Grid.SetRow(HeaderFacts, below ? 1 : 0);
        Grid.SetColumnSpan(HeaderFacts, below ? 2 : 1);
        HeaderFacts.ColumnDefinitions[0].Width = below ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        HeaderFacts.ColumnDefinitions[1].Width = stacked ? new GridLength(0)
            : below ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        Grid.SetColumnSpan(FirstFact, stacked ? 2 : 1);
        Grid.SetColumn(SecondFact, stacked ? 0 : 1);
        Grid.SetRow(SecondFact, stacked ? 1 : 0);
        Grid.SetColumnSpan(SecondFact, stacked ? 2 : 1);
    }
}
