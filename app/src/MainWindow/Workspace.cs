using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Helpers;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Views;
using Windows.System;

namespace Syno.TinyTorrent;

public sealed partial class MainWindow
{
    private SettingsPage? _settingsPage;
    private ConnectionPage? _connectionPage;
    private readonly Motion _motion = new();
    private readonly Motion _pages = new();
    private readonly Motion _settings = new();
    private WindowPage _page;
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
        if (Model.Page == WindowPage.Settings && _settings.Current is ConnectionPage)
        {
            await ReturnToSettings();
            return;
        }
        _fromLibrary = false;
        await Navigate(_returnPage);
    }

    private async Task ReturnToSettings()
    {
        if (HasDialog || Model.IsClosing || _allowClose || !Model.Settings.Connection.CanLeave)
            return;
        if (!await Model.Settings.Connection.Depart())
            return;
        if (HasDialog || Model.IsClosing || _allowClose || Model.Page != WindowPage.Settings
            || _settings.Current is not ConnectionPage)
            return;
        if (_settingsPage is { } page)
            _settings.Show(page, -1);
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                if (Model.Page == WindowPage.Settings && ReferenceEquals(_settings.Current, _settingsPage))
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
        _settingsPage?.Depart();
        if (_connectionPage is null)
        {
            _connectionPage = new ConnectionPage(Model.Settings.Connection);
            _connectionPage.ReturnRequested += async (_, _) => await ReturnToSettings();
            SettingsContent.Children.Add(_connectionPage);
        }
        _settings.Show(_connectionPage);
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                if (Model.Page == WindowPage.Settings && _settings.Current is ConnectionPage page)
                    page.FocusInput();
            }
        );
    }

    private async Task<bool> Navigate(WindowPage page)
    {
        if (HasDialog || Model.IsClosing || _allowClose)
            return false;
        if (
            Model.Page == WindowPage.Torrents
            && page != WindowPage.Torrents
            && !await LeaveInspector()
        )
            return false;
        if (Model.Page == WindowPage.Settings)
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
        if (page is WindowPage.Settings or WindowPage.About && Model.Page is WindowPage.Torrents or WindowPage.Library)
            _returnPage = Model.Page;
        if (Model.Page == WindowPage.Settings && page != WindowPage.Settings && ReferenceEquals(_settings.Current, _settingsPage))
            _settingsPage?.Depart();
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
            SettingsContent.Children.Add(_settingsPage);
            _settingsPage.FolderRequested += async (_, setting) =>
                await PickSettingsFolder(setting);
            _settingsPage.ProxyRequested += async (_, _) => await ShowProxy();
            _settingsPage.SupplierRequested += async (_, _) => await ShowSupplier();
            _settingsPage.ConnectionRequested += async (_, _) => await ShowConnection();
            _settingsPage.LayoutChanged += (_, _) => UpdateChrome();
        }
        _settingsPage.Navigate(target);
        _settings.Show(_settingsPage, -1);
    }

    private void UpdatePage()
    {
        FrameworkElement content = Model.Page switch
        {
            WindowPage.Settings => SettingsContent,
            WindowPage.About => AboutContent,
            _ => Workspace,
        };
        _pages.Show(content, Model.Page >= _page ? 1 : -1);
        _page = Model.Page;
        BackButton.Visibility =
            Model.Page is WindowPage.Torrents or WindowPage.Library && !_fromLibrary ? Visibility.Collapsed : Visibility.Visible;
        TorrentMenu.IsEnabled = Model.Page is WindowPage.Torrents or WindowPage.Library;
        ViewMenu.IsEnabled = TorrentMenu.IsEnabled;
        UpdateLibraryPage();
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
    private async Task<bool> ResolveDraft(string editor)
    {
        var isAdd = editor == "add";
        var focused = FocusManager.GetFocusedElement(Root.XamlRoot) as Control;
        return await Interact(
            async interaction =>
            {
                var prompt = new Dialog
                {
                    DefaultButton = ContentDialogButton.Close,
                    SecondaryGlyph = Syno.Lucide.Undo2,
                };
                var choice = await ShowDialog(
                    interaction,
                    prompt,
                    () =>
                    {
                        prompt.Title = Model.Text.Get("changes", editor + "_title");
                        (prompt.Content, prompt.PrimaryButtonText, prompt.Glyph) =
                            isAdd
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
                    }
                );
                var resolved = false;
                if (choice == ContentDialogResult.Primary)
                    resolved = isAdd ? await Model.AddDraft.Submit() : await Model.FileDraft.Submit();
                else if (choice == ContentDialogResult.Secondary)
                {
                    if (isAdd)
                        await Model.AddDraft.Cancel();
                    else
                        Model.FileDraft.Cancel();
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
        if (Model.Page == WindowPage.Library)
            _libraryTable.FocusRows();
        else
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
        if (Model.Page == WindowPage.Library)
        {
            Search.IsSuggestionListOpen = false;
            return;
        }
        Search.ItemsSource = Model.FindSuggestions(Search.Text);
        Search.IsSuggestionListOpen = true;
    }

    private void OnSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
            return;
        Model.Query = sender.Text;
        if (Model.Page == WindowPage.Library)
        {
            sender.IsSuggestionListOpen = false;
            return;
        }
        sender.ItemsSource = Model.FindSuggestions(sender.Text);
    }

    private async void OnSearchSubmitted(
        AutoSuggestBox sender,
        AutoSuggestBoxQuerySubmittedEventArgs args
    )
    {
        if (HasDialog || Model.IsClosing)
            return;
        if (Model.Page == WindowPage.Library)
        {
            _libraryTable.FocusRows();
            return;
        }
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
        ((InspectorPane)InspectorContent.Content).ShowLibrary(Model.Page == WindowPage.Library ? Model.Library : null);
        var toolbar = Model.ShowsToolbar
            ? Toolbar.ActualHeight + Toolbar.Margin.Top + Toolbar.Margin.Bottom
            : 0;
        var maximum = Math.Max(0, Workspace.ActualHeight - toolbar - 126);
        var minimum = Math.Min(300, maximum);
        var opening = InspectorRow.Height.Value == 0;
        Split.SetBounds(minimum, maximum, _splitHeight);
        InspectorRow.Height = new GridLength(Math.Clamp(_splitHeight, minimum, maximum));
        if (opening && Model.Page == WindowPage.Torrents)
            _motion.Play(InspectorSurface, 12);
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
