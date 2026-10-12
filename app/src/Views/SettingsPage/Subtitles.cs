using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Models;
using SubtitleSettings = Syno.TinyTorrent.Subtitles.Settings;

namespace Syno.TinyTorrent.Views;

public sealed partial class SettingsPage
{
    private bool _readingCoverage;

    private async void RefreshCoverage()
    {
        if (!IsLoaded || _category != PluginsCategory || _readingCoverage)
            return;
        _readingCoverage = true;
        try
        {
            var coverage = await Main.Library.ReadCoverage();
            VideoCoverage.Text = coverage is null
                ? "—"
                : Model.Text.Format("plugins", "coverage", coverage.Informed, coverage.Total);
            SubtitleCoverage.Text =
                coverage is null || Main.Subtitles is null
                    ? "—"
                    : Model.Text.Format(
                        "plugins",
                        "coverage",
                        Main.Subtitles.WithSubtitles,
                        coverage.Total
                    );
            var statistics = await Main.ReadStatistics();
            DatabaseSize.Text = statistics is null ? "—" : Model.Text.Bytes(statistics.Size);
            ReusableSpace.Text = statistics is null ? "—" : Model.Text.Bytes(statistics.Reusable);
            StoredMovies.Text = Number(statistics?.Movies);
            StoredSeries.Text = Number(statistics?.Series);
            StoredEpisodes.Text = Number(statistics?.Episodes);
            LibraryFiles.Text = Number(statistics?.Files);
            CachedResponses.Text = Number(statistics?.Responses);
            TrackedSubtitles.Text = Number(statistics?.Subtitles);
            CreatedSubtitles.Text = Number(statistics?.Created);
            FoundSubtitles.Text = Number(statistics?.Found);
            SubtitleLanguages.Text = Number(statistics?.Languages);
        }
        finally
        {
            _readingCoverage = false;
        }

        static string Number(long? value) =>
            value?.ToString("N0", CultureInfo.CurrentCulture) ?? "—";
    }

    private void OnSubtitleMain(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(MainViewModel.CurrentStatus) or "")
            RefreshCoverage();
        if (
            args.PropertyName is nameof(MainViewModel.Subtitles) or ""
            && SubtitlesContent.Content is SubtitleSettings subtitles
        )
            subtitles.Attach(Main.Subtitles);
        if (args.PropertyName is nameof(MainViewModel.SubtitleHelpSeen) or "")
            RefreshSubtitles();
    }

    private void RefreshSubtitles()
    {
        RefreshCoverage();
        if (IsLoaded && SubtitlesContent.Content is SubtitleSettings content)
            content.Attach(Main.Subtitles);
        SubtitleTitle.Text = Model.Text.Get("subtitles", "automatic");
        SubtitleDescription.Text = Model.Text.Get("subtitles", "help");
        SubtitleCue.Text = Model.Text.Get("subtitles", "setup");
        ToolTipService.SetToolTip(SubtitleSetup, Model.Text.Get("subtitles", "help"));
        ToolTipService.SetToolTip(SubtitleDismiss, Model.Text.Get("subtitles", "dismiss"));
        AutomationProperties.SetName(SubtitleSetup, SubtitleTitle.Text + " " + SubtitleCue.Text);
        AutomationProperties.SetHelpText(SubtitleSetup, SubtitleDescription.Text);
        AutomationProperties.SetName(SubtitleDismiss, Model.Text.Get("subtitles", "dismiss"));
        if (Main.SubtitleHelpSeen)
            IndexContent.Children.Remove(SubtitleHelp);
        else if (!IndexContent.Children.Contains(SubtitleHelp))
            IndexContent.Children.Insert(0, SubtitleHelp);
    }

    private void DismissSubtitles() => Main.SubtitleHelpSeen = true;

    internal void Depart()
    {
        if (_category is null && IsLoaded)
            DismissSubtitles();
    }

    private void OnSubtitleSetup(object sender, RoutedEventArgs args)
    {
        DismissSubtitles();
        Navigate(new(SettingsCategory.Plugins));
    }

    private void OnSubtitleDismiss(object sender, RoutedEventArgs args)
    {
        DismissSubtitles();
        CategoryIndex
            .Children.OfType<Button>()
            .FirstOrDefault(button => ReferenceEquals(button.Tag, PluginsCategory))
            ?.Focus(FocusState.Programmatic);
    }
}
