using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Syno.TinyTorrent;

public sealed partial class MainWindow
{
    private PreferencesForm? _preferencesForm;
    private bool _refreshingFilters;
    private bool _refreshingNavigation;
    private bool _selecting;
    private double _splitHeight = 360;
    private Syno.TableView.Selection _selection = new([], null);
    private Task<bool>? _discardDecision;

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

    private void OnTorrents(object sender, RoutedEventArgs args) { if (!HasDialog) Run(Model.ShowTorrents); }

    private void OnNavigationInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer == ExitItem) Run(Model.Exit);
    }

    private async void OnNavigation(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_refreshingNavigation || args.SelectedItem is not NavigationViewItem page) return;
        try
        {
            if (page == TorrentsPage) await ShowTorrents();
            else if (page == SettingsPage) await ShowPreferences(new(PreferenceSection.General));
            else if (page == AboutPage) await ShowAbout();
        }
        finally { UpdateNavigation(); }
    }

    private void UpdateNavigation()
    {
        _refreshingNavigation = true;
        try
        {
            Navigation.SelectedItem = Model.Page switch
            {
                WindowPage.Torrents => TorrentsPage,
                WindowPage.Preferences => SettingsPage,
                _ => AboutPage
            };
        }
        finally { _refreshingNavigation = false; }
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
        if (Model.Page == WindowPage.Preferences && page != WindowPage.Preferences && Model.Preferences.IsPending) return false;
        if (Model.Page == WindowPage.Preferences && page != WindowPage.Preferences && Model.Preferences.HasDraft)
        {
            if (!await ConfirmDiscard()) return false;
            Model.Preferences.CancelDraft();
        }
        if (Model.IsClosing) return false;
        Model.Page = page;
        return true;
    }

    private async Task ShowAbout()
    {
        if (await Navigate(WindowPage.About)) AboutPage.Focus(FocusState.Programmatic);
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
        FilterButton.Visibility = Workspace.Visibility;
        UpdateNavigation();
        UpdateChrome();
    }

    private async Task SelectTorrent()
    {
        if (_selecting) return;
        _selecting = true;
        var desired = Torrents.Selection;
        try
        {
            var changesTarget = Model.Inspector.IsOpen && (desired.Items.Count != 1 ||
                !ReferenceEquals(desired.Items[0], Model.Inspector.Target));
            if (changesTarget && (Model.Inspector.IsPending || Model.Inspector.HasDraft && !await ConfirmDiscard()))
            {
                var retained = _selection.Items.Where(item => Model.VisibleTorrents.Contains((Torrent)item)).ToArray();
                Torrents.Selection = new Syno.TableView.Selection(retained, retained.Contains(_selection.Current) ? _selection.Current : retained.FirstOrDefault());
                _selection = Torrents.Selection;
                Model.Select(retained.Cast<Torrent>(), _selection.Current as Torrent);
                return;
            }
            if (changesTarget) Model.Inspector.CancelDraft();
            _selection = desired;
            Model.Select(desired.Items.Cast<Torrent>(), desired.Current as Torrent);
        }
        finally { _selecting = false; }
    }

    private async Task<bool> ConfirmDiscard()
    {
        if (_discardDecision is { } existing) return await existing;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _discardDecision = completion.Task;
        try
        {
            var prompt = new ContentDialog { XamlRoot = Root.XamlRoot, DefaultButton = ContentDialogButton.Close };
            _closePrompt = prompt;
            RefreshText();
            var discard = await prompt.ShowAsync() == ContentDialogResult.Primary;
            completion.TrySetResult(discard);
            return discard;
        }
        catch (Exception error)
        {
            Model.Report(error);
            completion.TrySetResult(false);
            return false;
        }
        finally { _closePrompt = null; _discardDecision = null; }
    }

    private void OnFiltersClose(object sender, RoutedEventArgs args)
    {
        Model.IsFilterOpen = false;
        FilterButton.Focus(FocusState.Programmatic);
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_refreshingFilters && Filters.SelectedItem is FilterChoice choice) Model.Filter = choice.Filter;
    }

    private void OnSearchFocus(object sender, RoutedEventArgs args)
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
            if (_placement?.Inspector is { } layout) form.Layout = layout;
            InspectorContent.Content = form;
        }
        var maximum = Math.Max(0, TorrentWorkspace.ActualHeight - 126);
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
