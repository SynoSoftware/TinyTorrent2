using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Views;
using Windows.System;

namespace Syno.TinyTorrent;

public sealed partial class MainWindow
{
    private SettingsPage? _settingsPage;
    private ConnectionPage? _connectionPage;
    private bool _refreshingFilters;
    private bool _selecting;
    private double _splitHeight = 360;

    private bool HasEditorFocus()
    {
        var element = FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject;
        while (element is not null)
        {
            if (
                element
                is TextBox
                    or RichEditBox
                    or PasswordBox
                    or NumberBox
                    or ComboBox
                    or AutoSuggestBox
            )
                return true;
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    private async Task<bool> ShowTorrents()
    {
        if (!await Navigate(WindowPage.Torrents))
            return false;
        Torrents.Focus(FocusState.Programmatic);
        return true;
    }

    private async Task GoBack()
    {
        if (HasDialog || Model.IsClosing || _allowClose)
            return;
        if (Model.Page == WindowPage.Settings && SettingsContent.Content is ConnectionPage)
        {
            await ReturnToSettings();
            return;
        }
        if (Model.Page == WindowPage.Settings && _settingsPage?.BackToIndex() == true)
            return;
        await ShowTorrents();
    }

    private async Task ReturnToSettings()
    {
        if (HasDialog || Model.IsClosing || _allowClose || !Model.Settings.Connection.CanLeave)
            return;
        if (!await Model.Settings.Connection.Depart())
            return;
        if (HasDialog || Model.IsClosing || _allowClose || Model.Page != WindowPage.Settings
            || SettingsContent.Content is not ConnectionPage)
            return;
        SettingsContent.Content = _settingsPage;
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                if (Model.Page == WindowPage.Settings && ReferenceEquals(SettingsContent.Content, _settingsPage))
                    _settingsPage?.FocusConnection();
            }
        );
    }

    private async Task ShowConnection()
    {
        if (
            !await Model.Settings.PrepareLeave()
            || Model.Page != WindowPage.Settings
            || HasDialog
            || Model.IsClosing
            || _allowClose
        )
            return;
        Model.Settings.Connection.Open();
        if (_connectionPage is null)
        {
            _connectionPage = new ConnectionPage(Model.Settings.Connection);
            _connectionPage.ReturnRequested += async (_, _) => await ReturnToSettings();
        }
        SettingsContent.Content = _connectionPage;
    }

    private async Task<bool> Navigate(WindowPage page)
    {
        if (HasDialog || Model.IsClosing || _allowClose)
            return false;
        if (Model.Page == WindowPage.Settings && page == WindowPage.Settings
            && SettingsContent.Content is ConnectionPage && !await Model.Settings.Connection.Depart())
            return false;
        if (
            Model.Page == WindowPage.Torrents
            && page != WindowPage.Torrents
            && !await LeaveInspector()
        )
            return false;
        if (Model.Page == WindowPage.Settings && page != WindowPage.Settings)
        {
            if (!await Model.Settings.PrepareLeave())
            {
                var field = _settingsPage?.Recover(null);
                field?.StartBringIntoView();
                field?.Focus(FocusState.Programmatic);
                return false;
            }
        }
        if (HasDialog || Model.IsClosing || _allowClose)
            return false;
        Model.Page = page;
        return true;
    }

    private async Task ShowAbout()
    {
        if (await Navigate(WindowPage.About))
            BackButton.Focus(FocusState.Programmatic);
    }

    private async Task ShowSettings(SettingTarget target)
    {
        if (!await Navigate(WindowPage.Settings))
            return;
        if (_settingsPage is null)
        {
            _settingsPage = new SettingsPage(Model);
            _settingsPage.FolderRequested += async (_, setting) =>
                await PickSettingsFolder(setting);
            _settingsPage.ProxyRequested += async (_, _) => await ShowProxy();
            _settingsPage.ConnectionRequested += async (_, _) => await ShowConnection();
        }
        SettingsContent.Content = _settingsPage;
        _settingsPage.Navigate(target);
    }

    private void UpdatePage()
    {
        Workspace.Visibility =
            Model.Page == WindowPage.Torrents ? Visibility.Visible : Visibility.Collapsed;
        SettingsContent.Visibility =
            Model.Page == WindowPage.Settings ? Visibility.Visible : Visibility.Collapsed;
        AboutContent.Visibility =
            Model.Page == WindowPage.About ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Visibility =
            Model.Page == WindowPage.Torrents ? Visibility.Collapsed : Visibility.Visible;
        TorrentMenu.IsEnabled = Model.Page == WindowPage.Torrents;
        ViewMenu.IsEnabled = Model.Page == WindowPage.Torrents;
    }

    private async Task<bool> SelectTorrent(Syno.TableView.Selection? selection = null)
    {
        if (_selecting || Model.IsClosing)
            return false;
        _selecting = true;
        var desired = selection ?? Torrents.Selection;
        try
        {
            var accepted = await Model.Select(
                desired.Items.Cast<Torrent>(),
                desired.Current as Torrent,
                LeaveInspector
            );
            var retained = Model.Selected.Where(Model.VisibleTorrents.Contains).ToArray();
            Torrents.Selection = new Syno.TableView.Selection(
                retained,
                Model.Current is { } current && Model.VisibleTorrents.Contains(current)
                    ? current
                    : null
            );
            return accepted;
        }
        finally
        {
            _selecting = false;
        }
    }

    private async Task<bool> LeaveInspector()
    {
        if (await Model.Inspector.Depart())
            return true;
        (InspectorContent.Content as InspectorPane)?.Recover().Focus(FocusState.Programmatic);
        return false;
    }

    // Only Add and Move still ask, because applying them on close would start
    // a download or a file move that the person has not confirmed.
    private async Task<bool> ResolveDraft(string editor, Func<Task<bool>> save, Func<Task> discard)
    {
        var focused = FocusManager.GetFocusedElement(Root.XamlRoot) as Control;
        return await Interact(
            async interaction =>
            {
                var prompt = new Dialog
                {
                    XamlRoot = Root.XamlRoot,
                    DefaultButton = ContentDialogButton.Close,
                    SecondaryGlyph = Syno.Lucide.Undo2,
                    CloseGlyph = Syno.Lucide.Pencil,
                };
                var choice = await ShowDialog(
                    interaction,
                    prompt,
                    () =>
                    {
                        prompt.Title = Model.Text.Get("changes", editor + "_title");
                        (prompt.Content, prompt.PrimaryButtonText, prompt.Glyph) =
                            editor == "add"
                                ? (
                                    Lines([Model.AddDraft.Heading]),
                                    Model.AddDraft.SubmitText,
                                    Syno.Lucide.CirclePlus
                                )
                                : (
                                    Lines([Model.FileDraft.Destination]),
                                    Model.FileDraft.SubmitText,
                                    Syno.Lucide.FolderInput
                                );
                        prompt.PrimaryGlyph = prompt.Glyph;
                        prompt.PrimaryToolTip = Model.Text.Get("changes", editor + "_save_tip");
                        prompt.SecondaryButtonText = Model.Text.Get("changes", "discard");
                        prompt.SecondaryToolTip = Model.Text.Get(
                            "changes",
                            editor + "_discard_tip"
                        );
                        // Two words, because Cancel would not say whether it cancels the
                        // edit or the act that is leaving it.
                        prompt.CloseButtonText = Model.Text.Get("changes", "keep_editing");
                    }
                );
                var resolved = false;
                if (choice == ContentDialogResult.Primary)
                    resolved = await save();
                else if (choice == ContentDialogResult.Secondary)
                {
                    await discard();
                    resolved = true;
                }
                if (!resolved && focused is { IsLoaded: true })
                    focused.Focus(FocusState.Programmatic);
                return resolved;
            },
            isDraftDecision: true
        );
    }

    private void OnFiltersClose(object sender, RoutedEventArgs args) => CloseFilters();

    private void OnFiltersKey(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Escape)
            return;
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
        if (!_refreshingFilters && Filters.SelectedItem is FilterChoice choice)
            Model.Filter = choice.Filter;
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
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
            return;
        Model.Query = sender.Text;
        sender.ItemsSource = Model.FindSuggestions(sender.Text);
    }

    private async void OnSearchSubmitted(
        AutoSuggestBox sender,
        AutoSuggestBoxQuerySubmittedEventArgs args
    )
    {
        if (HasDialog || Model.IsClosing)
            return;
        var suggestion =
            args.ChosenSuggestion as Suggestion
            ?? Model.FindSuggestions(args.QueryText).FirstOrDefault(value => value.IsEnabled);
        if (suggestion is null || !suggestion.IsEnabled)
            return;
        if (suggestion.Scope == SuggestionScope.Navigation && !await ShowTorrents())
            return;
        Model.Query = string.Empty;
        sender.IsSuggestionListOpen = false;
        Run(suggestion.Command);
    }

    private void UpdateInspectorSize()
    {
        if (!Model.HasInspector)
        {
            InspectorRow.Height = new GridLength(0);
            return;
        }
        if (InspectorContent.Content is null)
        {
            var pane = new InspectorPane(Model.Inspector);
            pane.Close.Click += OnInspectorClose;
            if (_placement?.Inspector is { } layout)
                pane.Layout = layout;
            InspectorContent.Content = pane;
        }
        var toolbar = Model.IsToolbarOpen
            ? Toolbar.ActualHeight + Toolbar.Margin.Top + Toolbar.Margin.Bottom
            : 0;
        var maximum = Math.Max(0, Workspace.ActualHeight - toolbar - 126);
        var minimum = Math.Min(300, maximum);
        Split.SetBounds(minimum, maximum, _splitHeight);
        InspectorRow.Height = new GridLength(Math.Clamp(_splitHeight, minimum, maximum));
    }

    private async Task PickSettingsFolder(Setting setting)
    {
        if (!setting.CanEdit || Model.IsPicking)
            return;
        try
        {
            var folder = await PickFolder();
            if (folder is null)
                return;
            setting.Input = folder;
            await Model.Settings.Commit(setting);
        }
        catch (Exception error)
        {
            Model.Report(error);
        }
    }
}
