using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Views;

namespace Syno.TinyTorrent;

public sealed partial class MainWindow
{
    private PreferencesForm? _preferencesForm;
    private bool _refreshingFilters;
    private bool _selecting;
    private double _splitHeight = 360;

    private bool HasEditorFocus()
    {
        var element = FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject;
        while (element is not null)
        {
            if (element is TextBox or RichEditBox or PasswordBox or NumberBox or ComboBox or AutoSuggestBox) return true;
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }
    private async Task<bool> ShowTorrents()
    {
        if (!await Navigate(WindowPage.Torrents)) return false;
        Torrents.Focus(FocusState.Programmatic);
        return true;
    }

    private async Task<bool> Navigate(WindowPage page)
    {
        if (HasDialog || Model.IsClosing || _allowClose) return false;
        if (Model.Page == WindowPage.Preferences && page != WindowPage.Preferences)
        {
            if (!await Model.Preferences.PrepareLeave()) return false;
            if (Model.Preferences.HasDraft && !await ResolveDraft(Model.Preferences.Schedule.CommitPeriod, () =>
                { Model.Preferences.CancelDraft(); return Task.CompletedTask; })) return false;
        }
        if (Model.IsClosing) return false;
        Model.Page = page;
        return true;
    }

    private async Task ShowAbout()
    {
        if (await Navigate(WindowPage.About)) BackButton.Focus(FocusState.Programmatic);
    }

    private async Task ShowPreferences(PreferenceTarget target)
    {
        if (!await Navigate(WindowPage.Preferences)) return;
        if (_preferencesForm is null)
        {
            _preferencesForm = new PreferencesForm(Model.Preferences);
            _preferencesForm.DestinationRequested += async (_, _) => await PickPreferenceFolder();
            PreferencesContent.Content = _preferencesForm;
        }
        _preferencesForm.Navigate(target);
    }

    private void UpdatePage()
    {
        Workspace.Visibility = Model.Page == WindowPage.Torrents ? Visibility.Visible : Visibility.Collapsed;
        PreferencesContent.Visibility = Model.Page == WindowPage.Preferences ? Visibility.Visible : Visibility.Collapsed;
        AboutContent.Visibility = Model.Page == WindowPage.About ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Visibility = Model.Page == WindowPage.Torrents ? Visibility.Collapsed : Visibility.Visible;
        TorrentMenu.IsEnabled = Model.Page == WindowPage.Torrents;
        ViewMenu.IsEnabled = Model.Page == WindowPage.Torrents;
    }

    private async Task<bool> SelectTorrent(Syno.TableView.Selection? selection = null)
    {
        if (_selecting || Model.IsClosing) return false;
        _selecting = true;
        var desired = selection ?? Torrents.Selection;
        try
        {
            var accepted = await Model.Select(desired.Items.Cast<Torrent>(), desired.Current as Torrent, () =>
                ResolveDraft(Model.Inspector.SaveDraft, () => { Model.Inspector.CancelDraft(); return Task.CompletedTask; }));
            var retained = Model.Selected.Where(Model.VisibleTorrents.Contains).ToArray();
            Torrents.Selection = new Syno.TableView.Selection(retained, Model.Current is { } current && Model.VisibleTorrents.Contains(current) ? current : null);
            return accepted;
        }
        finally { _selecting = false; }
    }

    private async Task<bool> ResolveDraft(Func<Task<bool>> save, Func<Task> discard)
    {
        var focused = FocusManager.GetFocusedElement(Root.XamlRoot) as Control;
        return await Interact(async interaction =>
        {
            var prompt = new ContentDialog { XamlRoot = Root.XamlRoot, DefaultButton = ContentDialogButton.Primary };
            var choice = await ShowDialog(interaction, prompt, () =>
            {
                prompt.Title = Model.Text.Get("changes", "title");
                prompt.Content = Model.Text.Get("changes", "detail");
                prompt.PrimaryButtonText = Model.Text.Get("changes", "save");
                prompt.SecondaryButtonText = Model.Text.Get("changes", "discard");
                prompt.CloseButtonText = Model.Text.Get("add", "cancel");
            });
            var resolved = false;
            if (choice == ContentDialogResult.Primary) resolved = await save();
            else if (choice == ContentDialogResult.Secondary) { await discard(); resolved = true; }
            if (!resolved && focused is { IsLoaded: true }) focused.Focus(FocusState.Programmatic);
            return resolved;
        }, isDraftDecision: true);
    }

    private void OnFiltersClose(object sender, RoutedEventArgs args) => CloseFilters();

    private void OnFiltersKey(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Escape) return;
        CloseFilters();
        args.Handled = true;
    }

    private void CloseFilters()
    {
        Model.IsFilterOpen = false;
        Torrents.Focus(FocusState.Programmatic);
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_refreshingFilters && Filters.SelectedItem is FilterChoice choice) Model.Filter = choice.Filter;
    }

    private void FocusSearch()
    {
        Search.Focus(FocusState.Keyboard);
        ShowSuggestions();
    }

    private void OnSearchFocus(object sender, RoutedEventArgs args) => ShowSuggestions();

    private void ShowSuggestions()
    {
        Search.ItemsSource = Model.FindSuggestions(Search.Text);
        Search.IsSuggestionListOpen = true;
    }

    private void OnSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        Model.Query = sender.Text;
        sender.ItemsSource = Model.FindSuggestions(sender.Text);
    }

    private async void OnSearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (HasDialog || Model.IsClosing) return;
        var suggestion = args.ChosenSuggestion as Suggestion ?? Model.FindSuggestions(args.QueryText).FirstOrDefault(value => value.IsEnabled);
        if (suggestion is null || !suggestion.IsEnabled) return;
        if (suggestion.Scope == SuggestionScope.Navigation && !await ShowTorrents()) return;
        Model.Query = string.Empty;
        sender.IsSuggestionListOpen = false;
        Run(suggestion.Command);
    }

    private void UpdateInspectorSize()
    {
        if (!Model.HasInspector) { InspectorRow.Height = new GridLength(0); return; }
        if (InspectorContent.Content is null)
        {
            var form = new InspectorForm(Model.Inspector);
            form.Close.Click += OnInspectorClose;
            if (_placement?.Inspector is { } layout) form.Layout = layout;
            InspectorContent.Content = form;
        }
        var maximum = Math.Max(0, Workspace.ActualHeight - 126);
        var minimum = Math.Min(300, maximum);
        Split.SetBounds(minimum, maximum, _splitHeight);
        InspectorRow.Height = new GridLength(Math.Clamp(_splitHeight, minimum, maximum));
    }

    private async Task PickPreferenceFolder()
    {
        if (!Model.Preferences.Destination.CanEdit || Model.IsPicking) return;
        try
        {
            var folder = await PickFolder();
            if (folder is null) return;
            Model.Preferences.Destination.Input = folder;
            await Model.Preferences.Commit(Model.Preferences.Destination);
        }
        catch (Exception error) { Model.Report(error); }
    }
}
