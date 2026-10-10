using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using SubtitleSettings = Syno.TinyTorrent.Subtitles.Settings;

namespace Syno.TinyTorrent.Views;

public sealed partial class SettingsPage
{
    private TextBlock? _subtitleProblem;

    private void OnSubtitleMain(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(MainViewModel.Subtitles) or "" && SubtitlesContent.Content is SubtitleSettings subtitles)
            subtitles.Attach(Main.Subtitles);
        if (args.PropertyName is nameof(MainViewModel.SubtitleHelpSeen) or "")
            RefreshSubtitles();
    }

    private void RefreshSubtitles()
    {
        if (IsLoaded && SubtitlesContent.Content is SubtitleSettings content)
            content.Attach(Main.Subtitles);
        SubtitleTitle.Text = Model.Text.Get("subtitles", "automatic");
        SubtitleCue.Text = Model.Text.Get("subtitles", "setup");
        ToolTipService.SetToolTip(SubtitleSetup, Model.Text.Get("subtitles", "help"));
        ToolTipService.SetToolTip(SubtitleDismiss, Model.Text.Get("subtitles", "dismiss"));
        AutomationProperties.SetName(SubtitleSetup, SubtitleTitle.Text + " " + SubtitleCue.Text);
        AutomationProperties.SetName(SubtitleDismiss, Model.Text.Get("subtitles", "dismiss"));
        if (_subtitleProblem is not null && SubtitlesContent.Content is SubtitleSettings subtitles)
        {
            _subtitleProblem.Text = subtitles.Problem;
            _subtitleProblem.Visibility = subtitles.Problem.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
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
        Categories.SelectedItem = SubtitlesCategory;
        SubtitlesCategory.Focus(FocusState.Programmatic);
    }

    private void OnSubtitleDismiss(object sender, RoutedEventArgs args)
    {
        DismissSubtitles();
        CategoryIndex.Children.OfType<Button>().FirstOrDefault(button => ReferenceEquals(button.Tag, SubtitlesCategory))?.Focus(FocusState.Programmatic);
    }
}
