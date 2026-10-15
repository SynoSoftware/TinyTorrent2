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
    private AboutPage? _aboutPage;
    private CreditsPage? _creditsPage;
    private readonly Motion _motion = new();
    private readonly Motion _pages = new();
    private readonly Motion _workspaces = new();
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
        if (!await Model.Navigation.Navigate(WindowPage.Torrents))
            return false;
        Torrents.Focus(FocusState.Programmatic);
        return true;
    }

    private async Task GoBack()
    {
        var previous = Model.Navigation.Current;
        if (!await Model.Navigation.GoBack())
            return;
        var destination = Model.Navigation.Current;
        var settings = Model.Navigation.Settings;
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                if (Model.Navigation.Current != destination || Model.Navigation.Settings != settings || HasDialog || Model.IsClosing)
                    return;
                if (destination == WindowPage.Settings && previous == WindowPage.Connection)
                    _settingsPage?.FocusConnection();
                else if (destination == WindowPage.About && previous == WindowPage.Credits)
                    _aboutPage?.FocusCredits();
                else
                    FocusPage();
            }
        );
    }

    private void FocusPage()
    {
        if (Model.Navigation.Current == WindowPage.Torrents)
            Torrents.Focus(FocusState.Programmatic);
        else if (Model.Navigation.Current == WindowPage.Library)
            _libraryTable?.FocusRows();
        else
            BackButton.Focus(FocusState.Programmatic);
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
            if (Model.Navigation.Current == WindowPage.Connection && !await Model.Settings.Connection.Depart())
                return false;
            if (_settingsPage is { } settings)
                _ = settings.SaveFeatures();
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
            _settingsPage.DeleteRequested += async (_, dataset) => await ConfirmDataDeletion(dataset);
            _settingsPage.ConnectionRequested += async (_, _) => await ShowConnection();
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
                    _connectionPage.ParentRequested += async (_, _) => await ShowSettings(new(SettingsCategory.Limits));
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
        UpdateLibraryPage();
        _pages.Show(content, forward: Model.Page >= _page);
        _page = Model.Page;
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
                Model.Current is { } current && visible.Contains(current)
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
                        (prompt.Content, prompt.PrimaryButtonText, prompt.Glyph) = isAdd
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
                    resolved = isAdd
                        ? await Model.AddDraft.Submit()
                        : await Model.FileDraft.Submit();
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
        if (Model.Page == WindowPage.Library)
            _libraryTable?.FocusRows();
        else
            Torrents.Focus(FocusState.Keyboard);
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
            Model.Library.SubmitSearch();
            _libraryTable?.FocusRows();
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
        var pane = InspectorContent.Content as InspectorPane;
        var expanded = Model.HasInspector && !Model.IsInspectorCompact;
        var maximized = expanded && pane?.Surface.IsMaximized == true;
        var opening = InspectorSurface.Visibility != Visibility.Visible
            || pane?.Surface.IsCompact == true;
        InspectorSurface.Visibility = Model.HasInspector ? Visibility.Visible : Visibility.Collapsed;
        Split.Visibility = expanded && !maximized ? Visibility.Visible : Visibility.Collapsed;
        // A maximized inspector spans every workspace row on both pages. The
        // workspace content collapses, because it would show through the pane.
        WorkspaceContent.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetRow(InspectorSurface, maximized ? 0 : 2);
        Grid.SetRowSpan(InspectorSurface, maximized ? 3 : 1);
        if (!Model.HasInspector)
        {
            InspectorRow.Height = new GridLength(0);
            return;
        }
        if (pane is null)
        {
            pane = new InspectorPane(Model.Inspector);
            pane.Surface.CloseRequested += OnInspectorClose;
            pane.Surface.CollapseRequested += OnInspectorCollapse;
            pane.Surface.MaximizeRequested += OnInspectorMaximize;
            pane.Surface.OpenRequested += OnInspectorOpen;
            if (_placement?.Inspector is { } layout)
                pane.Layout = layout;
            InspectorContent.Content = pane;
        }
        pane.ShowLibrary(
            Model.Page == WindowPage.Library ? Model.Library : null,
            Model.DownloadSubtitles
        );
        pane.Surface.IsCompact = Model.IsInspectorCompact;
        if (Model.IsInspectorCompact)
        {
            InspectorRow.Height = new GridLength(10);
            return;
        }
        var toolbar = Model.ShowsToolbar
            ? Toolbar.ActualHeight + Toolbar.Margin.Top + Toolbar.Margin.Bottom
            : 0;
        var maximum = Math.Max(0, Workspace.ActualHeight - toolbar - 126);
        Split.SetBounds(0, maximum, _splitHeight);
        InspectorRow.Height = new GridLength(Split.Value);
        if (opening && Model.Page == WindowPage.Torrents)
            _motion.Enter(InspectorSurface);
    }

    private void OnInspectorCollapse(object? sender, FocusState focus) => CollapseInspector(focus);

    // The split height is kept while maximized, so Restore returns to it.
    private void OnInspectorMaximize(object? sender, EventArgs args)
    {
        var pane = (InspectorPane)InspectorContent.Content;
        pane.Surface.IsMaximized = !pane.Surface.IsMaximized;
        UpdateInspectorSize();
    }

    private void OnSplitCommitted(object? sender, double previous)
    {
        if (
            InspectorContent.Content is not InspectorPane pane
            || Split.Value >= 2 * pane.Surface.HeaderHeight
        )
            return;
        _splitHeight = previous;
        CollapseInspector(
            Split.FocusState == FocusState.Keyboard ? FocusState.Keyboard : FocusState.Programmatic
        );
    }

    private void CollapseInspector(FocusState focus)
    {
        Model.IsInspectorCompact = true;
        if (InspectorContent.Content is InspectorPane pane)
            pane.Surface.FocusToggle(focus);
    }

    private void OnInspectorOpen(object? sender, FocusState focus)
    {
        Model.IsInspectorCompact = false;
        if (InspectorContent.Content is InspectorPane pane)
            pane.Surface.FocusHeader(focus);
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
