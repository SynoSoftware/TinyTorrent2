using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Settings;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Torrents;
using Windows.System;

namespace Syno.TinyTorrent.Shell;

public sealed partial class MainWindow
{
    private SettingsPage? _settingsPage;
    private ConnectionPage? _connectionPage;
    private AboutPage? _aboutPage;
    private CreditsPage? _creditsPage;
    private readonly Motion _motion = new();
    private readonly Motion _pages = new();
    private readonly Motion _workspaces = new();
    private readonly Motion _filters = new();
    private WindowPage _page;
    private bool _refreshingFilters;
    private bool _selecting;
    private double _splitHeight = 360;

    private bool HasEditorFocus()
    {
        var element = FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject;
        while (element is not null)
        {
            if (element is TextBox or RichEditBox or PasswordBox or ComboBox or AutoSuggestBox)
                return true;
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    private async Task<bool> ShowTorrents()
    {
        if (!await Model.Navigation.Navigate(WindowPage.Torrents))
            return false;
        Torrents.Focus(FocusState.Programmatic);
        return true;
    }

    private async Task GoBack()
    {
        var previous = Model.Navigation.Current;
        var saved = _mapReturn;
        if (!await Model.Navigation.GoBack())
            return;
        var destination = Model.Navigation.Current;
        var settings = Model.Navigation.Settings;
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                if (
                    Model.Navigation.Current != destination
                    || Model.Navigation.Settings != settings
                    || HasDialog
                    || Model.IsClosing
                )
                    return;
                if (destination == WindowPage.Settings && previous == WindowPage.Connection)
                    _settingsPage?.FocusConnection();
                else if (destination == WindowPage.About && previous == WindowPage.Credits)
                    _aboutPage?.FocusCredits();
                else if (
                    saved is not null
                    && _mapReturn is null
                    && saved.Focus is { IsLoaded: true } focus
                    && focus.Focus(FocusState.Programmatic)
                )
                    return;
                else
                    FocusPage();
            }
        );
    }

    private void FocusPage()
    {
        if (
            Model.Page is WindowPage.Torrents or WindowPage.Library
            && Model.HasInspector
            && !Model.IsInspectorCompact
            && Model.IsInspectorMaximized
            && InspectorFrame is { } frame
        )
            frame.FocusHeader(FocusState.Programmatic);
        else if (Model.Navigation.Current is WindowPage.Torrents or WindowPage.Library)
            FocusRows(FocusState.Programmatic);
        else
            BackButton.Focus(FocusState.Programmatic);
    }

    // Focus goes to the current page's rows when a pane or the title search
    // gives it up.
    private void FocusRows(FocusState state)
    {
        if (NavigationPage == WindowPage.Library)
            _libraryTable?.FocusRows(state);
        else
            Torrents.Focus(state);
    }

    private Task ReturnToSettings() =>
        Model.Navigation.Current == WindowPage.Connection ? GoBack() : Task.CompletedTask;

    private async Task ShowConnection()
    {
        if (
            Model.Navigation.Current != WindowPage.Settings
            || !await Model.Navigation.Navigate(WindowPage.Connection)
        )
            return;
        Model.Settings.Connection.Open();
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                if (Model.Navigation.Current == WindowPage.Connection)
                    _connectionPage?.FocusHeading();
            }
        );
    }

    private async Task<bool> PrepareNavigation(WindowPage page)
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
            if (
                Model.Navigation.Current == WindowPage.Connection
                && !await Model.Settings.Connection.Depart()
            )
                return false;
            _ = Model.Settings.PrepareLeave();
        }
        if (HasDialog || Model.IsClosing || _allowClose)
            return false;
        return true;
    }

    private async Task ShowAbout()
    {
        if (await Model.Navigation.Navigate(WindowPage.About))
            BackButton.Focus(FocusState.Programmatic);
    }

    private async Task ShowCredits()
    {
        if (!await Model.Navigation.Navigate(WindowPage.Credits))
            return;
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                if (Model.Navigation.Current == WindowPage.Credits)
                    _creditsPage?.FocusHeading();
            }
        );
    }

    private async Task ShowSettings(SettingTarget target)
    {
        if (target.Category is null && Model.SettingsHome is { } home)
            target = new(home);
        await GetSettingsPage().Open(target);
    }

    // Ends every transition at its final state: the new page in place and the
    // outgoing page collapsed.
    private void StopMotion()
    {
        _motion.Stop();
        _pages.Stop();
        _workspaces.Stop();
        _filters.Stop();
    }

    private SettingsPage GetSettingsPage()
    {
        if (_settingsPage is null)
        {
            _settingsPage = new SettingsPage(Model);
            SettingsContent.Children.Add(_settingsPage);
            _settingsPage.FolderRequested += async (_, setting) =>
                await PickSettingsFolder(setting);
            _settingsPage.ProxyRequested += async (_, _) => await ShowProxy();
            _settingsPage.SupplierRequested += async (_, supplierId) =>
                await ShowSupplier(supplierId);
            _settingsPage.KeyRequested += async (_, setup) =>
                await ShowKey(setup.Provider, setup.EnableAutomaticDownload);
            _settingsPage.AutomaticDataRequested += async (_, _) =>
                await ConfirmDownload(Model.Library, Model.PrepareAutomaticDownload);
            _settingsPage.UpdateRequested += async (_, dataset) =>
                await ConfirmDownload(dataset, () => Model.DownloadData(dataset));
            _settingsPage.DeleteRequested += async (_, dataset) =>
                await ConfirmDataDeletion(dataset);
            _settingsPage.ConnectionRequested += async (_, _) => await ShowConnection();
            _settingsPage.ResetRequested += async (_, _) => await ConfirmReset();
            _settingsPage.LayoutChanged += (_, _) => UpdateChrome();
        }
        return _settingsPage;
    }

    private void UpdatePage()
    {
        FrameworkElement content = Model.Page switch
        {
            WindowPage.Settings => SettingsContent,
            WindowPage.About => AboutContent,
            WindowPage.Credits => CreditsContent,
            _ => Workspace,
        };
        var animate = ReferenceEquals(_pages.Current, content) && !_pages.IsActive;
        if (Model.Page == WindowPage.About && _aboutPage is null)
        {
            _aboutPage = new AboutPage(Model);
            _aboutPage.LinkRequested += (_, uri) => Open(new(uri.AbsoluteUri));
            _aboutPage.CreditsRequested += async (_, _) => await ShowCredits();
            AboutContent.Children.Add(_aboutPage);
        }
        if (Model.Page == WindowPage.Credits && _creditsPage is null)
        {
            _creditsPage = new CreditsPage(Model);
            _creditsPage.LinkRequested += (_, uri) => Open(new(uri.AbsoluteUri));
            _creditsPage.ReturnRequested += async (_, _) => await GoBack();
            CreditsContent.Children.Add(_creditsPage);
        }
        if (Model.Page == WindowPage.Settings)
        {
            var settings = GetSettingsPage();
            if (Model.Navigation.Current == WindowPage.Connection)
            {
                if (_connectionPage is null)
                {
                    _connectionPage = new ConnectionPage(Model.Settings.Connection);
                    _connectionPage.ReturnRequested += async (_, _) => await ReturnToSettings();
                    _connectionPage.ParentRequested += async (_, _) =>
                        await ShowSettings(new(SettingsCategory.Limits));
                }
                settings.ShowConnection(_connectionPage, animate);
            }
            else
            {
                settings.ShowConnection(null, animate);
                settings.Navigate(Model.Navigation.Settings ?? new(), animate: animate);
            }
        }
        TorrentMenu.IsEnabled = Model.Page is WindowPage.Torrents or WindowPage.Library;
        ViewMenu.IsEnabled = TorrentMenu.IsEnabled;
        UpdateWorkspacePage();
        // A page restored while the window is still hidden replaces the
        // workspace at once: a transition the person did not start would show
        // both pages in the first visible frames.
        if (_cloaked)
            _pages.Replace(content);
        else
            _pages.Show(content, forward: Model.Page >= _page);
        _page = Model.Page;
    }

    // Shows the current page's content in the shared workspace surfaces. The
    // surfaces keep their visibility; only their content changes.
    private void UpdateWorkspacePage()
    {
        if (Model.Page != WindowPage.Torrents)
            (InspectorContent.Content as InspectorPane)?.CancelReveal();
        var library = Model.Page == WindowPage.Library;
        UpdateLibraryPage();
        Workspace.OpenPaneLength = library ? 280 : 196;
        Search.PlaceholderText = SearchPlaceholder;
        Search.IsSuggestionListOpen = false;
        Search.ItemsSource = null;
        Search.Text = Model.Query;
        RefreshNavigation();
        RefreshMenus();
        ShowPageCommands();
        UpdateInspectorSize();
        UpdateChrome();
        if (Model.Page is not (WindowPage.Torrents or WindowPage.Library))
            return;
        _workspaces.Show(library ? LibraryContent : TorrentContent, forward: library);
        _filters.Show(library ? LibraryFilters : Filters, forward: library);
    }

    private void UpdateBack()
    {
        BackButton.Visibility = Model.Navigation.HasBack
            ? Visibility.Visible
            : Visibility.Collapsed;
        ((RelayCommand)Back).Refresh();
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
            var visible = Model.VisibleTorrents.ToHashSet();
            var retained = Model.Selected.Where(visible.Contains).ToArray();
            Torrents.Selection = new Syno.TableView.Selection(
                retained,
                Model.Current is { } current && visible.Contains(current) ? current : null
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

    private async Task<bool> Ask(IDraft draft, ClosePolicy.Ask question)
    {
        var focused = FocusManager.GetFocusedElement(Root.XamlRoot) as Control;
        return await Interact(
            async interaction =>
            {
                var prompt = new Dialog
                {
                    DefaultButton = ContentDialogButton.Close,
                    Glyph = question.Glyph,
                    PrimaryGlyph = question.Glyph,
                    SecondaryGlyph = Syno.Lucide.Undo2,
                };
                var choice = await ShowDialog(
                    interaction,
                    prompt,
                    () =>
                    {
                        prompt.Title = Model.Text.Get("changes", question.Key + "_title");
                        prompt.Content = Lines([question.Subject()]);
                        prompt.PrimaryButtonText = question.Submit();
                        prompt.PrimaryToolTip = Model.Text.Get(
                            "changes",
                            question.Key + "_save_tip"
                        );
                        prompt.SecondaryButtonText = Model.Text.Get("changes", "discard");
                        prompt.SecondaryToolTip = Model.Text.Get(
                            "changes",
                            question.Key + "_discard_tip"
                        );
                    }
                );
                var resolved = false;
                if (choice == ContentDialogResult.Primary)
                    resolved = await draft.Submit();
                else if (choice == ContentDialogResult.Secondary)
                {
                    await draft.Discard();
                    resolved = true;
                }
                if (!resolved && focused is { IsLoaded: true })
                    focused.Focus(FocusState.Programmatic);
                return resolved;
            },
            isDraftDecision: true
        );
    }

    private void OnFiltersOpen(object? sender, FocusState focus)
    {
        Model.IsFilterOpen = true;
        if (focus != FocusState.Keyboard)
            return;
        var page = Model.Page;
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                if (Model.IsFilterOpen && Model.Page == page && !HasDialog)
                    FiltersPane.FocusHeader(focus);
            }
        );
    }

    private void OnFiltersClose(object? sender, EventArgs args) => CloseFilters();

    private void OnFiltersCollapse(object? sender, FocusState focus)
    {
        Model.FilterPane = FilterPane.Compact;
        FiltersPane.FocusToggle(focus);
    }

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
        FocusRows(FocusState.Keyboard);
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_refreshingFilters && Filters.SelectedItem is FilterChoice choice)
            Model.Filter = choice.Filter;
    }

    private string SearchPlaceholder =>
        Model.Page switch
        {
            WindowPage.Library => Model.Text.Get("library", "search"),
            _ => Model.Text.Get("finding", "placeholder"),
        };

    // On Library the title search filters the page's rows, so Escape clears it
    // and Down moves into the rows.
    private void OnSearchKey(object sender, KeyRoutedEventArgs args)
    {
        if (Model.Page != WindowPage.Library)
            return;
        if (args.Key == VirtualKey.Escape)
            Model.Query = string.Empty;
        else if (args.Key == VirtualKey.Down)
            FocusRows(FocusState.Keyboard);
        else
            return;
        args.Handled = true;
    }

    private void FocusSearch()
    {
        Search.Focus(FocusState.Keyboard);
        ShowSuggestions();
    }

    // A click on a suggestion moves focus into the open list; replacing the
    // list then would remove the item before the click completes.
    private void OnSearchFocus(object sender, RoutedEventArgs args)
    {
        if (!Search.IsSuggestionListOpen)
            ShowSuggestions();
    }

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
            Model.Library.SubmitSearch();
            FocusRows(FocusState.Keyboard);
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

    // The active page's inspector. Torrents and Library share one view.
    private Pane? InspectorFrame => (InspectorContent.Content as InspectorPane)?.Surface;

    private void UpdateInspectorSize()
    {
        // The previous layout shows whether the inspector was already
        // expanded.
        var opening =
            Split.Visibility != Visibility.Visible
            && WorkspaceContent.Visibility == Visibility.Visible;
        var expanded = Model.HasInspector && !Model.IsInspectorCompact;
        var maximized = expanded && Model.IsInspectorMaximized;
        InspectorSurface.Visibility = Model.HasInspector
            ? Visibility.Visible
            : Visibility.Collapsed;
        Split.Visibility = expanded && !maximized ? Visibility.Visible : Visibility.Collapsed;
        // A maximized inspector spans every row below the toolbar. The page
        // content collapses, because it would show through the pane.
        WorkspaceContent.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetRow(InspectorSurface, maximized ? 1 : 3);
        Grid.SetRowSpan(InspectorSurface, maximized ? 3 : 1);
        if (!Model.HasInspector)
        {
            InspectorRow.Height = new GridLength(0);
            return;
        }
        var frame = InspectorFrame ?? CreateInspector();
        ((InspectorPane)InspectorContent.Content).ShowLibrary(
            Model.Page == WindowPage.Library ? Model.Library : null,
            Model.DownloadSubtitles
        );
        frame.IsCompact = Model.IsInspectorCompact;
        frame.IsMaximized = Model.IsInspectorMaximized;
        if (Model.IsInspectorCompact)
        {
            InspectorRow.Height = new GridLength(10);
            return;
        }
        var toolbar = Model.IsToolbarOpen
            ? ToolbarRow.ActualHeight + ToolbarRow.Margin.Top + ToolbarRow.Margin.Bottom
            : 0;
        var maximum = Math.Max(0, Workspace.ActualHeight - toolbar - 126);
        Split.SetBounds(0, maximum, _splitHeight);
        InspectorRow.Height = new GridLength(Split.Value);
        if (opening && Model.Page == WindowPage.Torrents)
            _motion.Enter(InspectorSurface);
    }

    // Creates the inspector view, which Torrents and Library share, when it
    // first opens.
    private Pane CreateInspector()
    {
        var pane = new InspectorPane(Model.Inspector);
        pane.LayoutChanged += (_, _) => KeepMap();
        if (_placement?.Inspector is { } layout)
            pane.Layout = layout;
        InspectorContent.Content = pane;
        var frame = pane.Surface;
        frame.CloseRequested += OnInspectorClose;
        frame.CollapseRequested += OnInspectorCollapse;
        frame.MaximizeRequested += OnInspectorMaximize;
        frame.OpenRequested += OnInspectorOpen;
        return frame;
    }

    private void OnInspectorCollapse(object? sender, FocusState focus) => CollapseInspector(focus);

    // The split height is kept while maximized, so Restore returns to it.
    private void OnInspectorMaximize(object? sender, EventArgs args) =>
        Model.IsInspectorMaximized = !Model.IsInspectorMaximized;

    private void OnSplitCommitted(object? sender, double previous)
    {
        if (InspectorFrame is not { } frame || Split.Value >= 2 * frame.HeaderHeight)
            return;
        _splitHeight = previous;
        CollapseInspector(
            Split.FocusState == FocusState.Keyboard ? FocusState.Keyboard : FocusState.Programmatic
        );
    }

    private void CollapseInspector(FocusState focus)
    {
        Model.IsInspectorCompact = true;
        InspectorFrame?.FocusToggle(focus);
    }

    private void OnInspectorOpen(object? sender, FocusState focus)
    {
        Model.IsInspectorCompact = false;
        InspectorFrame?.FocusHeader(focus);
    }

    private async Task PickSettingsFolder(Setting setting)
    {
        if (!setting.CanEdit || Model.IsPicking)
            return;
        try
        {
            var folder = await PickFolder(setting.Input);
            if (folder is null)
                return;
            setting.Input = folder;
            await setting.Commit();
        }
        catch (Exception error)
        {
            Model.Notifications.Report(error);
        }
    }
}
