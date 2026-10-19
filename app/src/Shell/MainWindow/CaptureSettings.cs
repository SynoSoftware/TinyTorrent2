using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Settings;
using Syno.TinyTorrent.Shared;
using Windows.Graphics;

namespace Syno.TinyTorrent.Shell;

public sealed partial class MainWindow
{
    private async Task CaptureSettings(List<object> outcomes, List<string> completed)
    {
        Model.Features.CanStart = true;
        Model.RequestSnapshot();
        await CaptureReady(Model, () => Model.Subtitles is not null);
        var acquisition =
            Model.Subtitles ?? throw new InvalidOperationException("Subtitles did not start.");
        await acquisition.SetEnabled(false);
        try
        {
            await CaptureReady(Model.Library, () => Model.Library.IsAvailable);
        }
        catch (TimeoutException error)
        {
            await CaptureUi("settings-startup-failure");
            throw new InvalidOperationException(
                $"Library startup did not notify readiness: available={Model.Library.IsAvailable}, failure={Model.Library.FailureCause}",
                error
            );
        }
        var resizeScale = Root.XamlRoot.RasterizationScale;
        AppWindow.Resize(new SizeInt32((int)(1920 * resizeScale), (int)(680 * resizeScale)));
        await CaptureFill(outcomes);
        AppWindow.Resize(new SizeInt32((int)(1040 * resizeScale), (int)(680 * resizeScale)));
        await CaptureToastReplay(outcomes);
        await CaptureLookupLimit(outcomes);
        await CaptureProviderChoice(outcomes, completed);
        await CaptureHeaderLanguage(outcomes);
        await ShowSettings(new(SettingsCategory.Transfers));
        await CaptureUi("settings-resize-compact");
        AppWindow.Resize(new SizeInt32((int)(1538 * resizeScale), (int)(1246 * resizeScale)));
        await CaptureUi("settings-resize-expanded");
        var resized =
            _settingsPage ?? throw new InvalidOperationException("Settings did not open.");
        var heading = (FrameworkElement)resized.FindName("PageHeader");
        var transfers = (FrameworkElement)resized.FindName("Transfers");
        var scroll = (ScrollViewer)resized.FindName("Body");
        var content = (FrameworkElement)resized.FindName("BodyContent");
        var headingLeft = heading.TransformToVisual(Root).TransformPoint(new()).X;
        var panelLeft = transfers.TransformToVisual(Root).TransformPoint(new()).X;
        var panelRight = panelLeft + transfers.ActualWidth;
        outcomes.Add(
            new
            {
                journey = "settings compact to expanded resize",
                headingLeft,
                panelLeft,
                panelRight,
                windowWidth = Root.ActualWidth,
                scrollLeft = scroll.TransformToVisual(Root).TransformPoint(new()).X,
                scroll.ViewportWidth,
                scroll.ExtentWidth,
                scroll.HorizontalOffset,
                contentLeft = content.TransformToVisual(Root).TransformPoint(new()).X,
                contentWidth = content.ActualWidth,
                translation = (transfers.RenderTransform as TranslateTransform)?.X,
            }
        );
        if (Math.Abs(panelLeft - headingLeft) > 1 || panelRight > Root.ActualWidth - 16)
            throw new InvalidOperationException(
                $"Settings content escaped the resized viewport: heading={headingLeft}, panel={panelLeft}..{panelRight}, window={Root.ActualWidth}."
            );
        foreach (
            var (language, theme, width, height) in new[]
            {
                ("en", "dark", 1280, 800),
                ("es", "light", 720, 560),
            }
        )
        {
            Model.SelectLanguage(language);
            await CaptureReady(Model, () => Model.CanClose && Model.Text.Language == language);
            await Model.Settings.SelectTheme(theme);
            await CaptureReady(Model, () => Model.CanClose && Model.Theme == theme);
            var scale = Root.XamlRoot.RasterizationScale;
            var minimum =
                (
                    (Microsoft.UI.Windowing.OverlappedPresenter)AppWindow.Presenter
                ).PreferredMinimumWidth
                ?? 0;
            AppWindow.Resize(
                new SizeInt32(Math.Max((int)(width * scale), minimum), (int)(height * scale))
            );
            await ShowSettings(new());
            var page =
                _settingsPage ?? throw new InvalidOperationException("Settings did not open.");
            var prefix = $"settings-{language}-{theme}-{width}";
            foreach (var showAdvanced in new[] { false, true })
            {
                page.ShowAdvanced(showAdvanced);
                await ShowSettings(new());
                await CaptureReady(Model.Settings, () => !Model.Settings.IsPending);
                var state = showAdvanced ? "advanced" : "standard";
                await CapturePage(prefix + "-index-" + state, page);
                foreach (var category in Enum.GetValues<SettingsCategory>())
                {
                    if (!showAdvanced && category == SettingsCategory.Advanced)
                        continue;
                    if (language == "en" && !showAdvanced)
                        await CaptureMotion(
                            prefix + "-" + category + "-navigation",
                            () => ShowSettings(new(category)),
                            outcomes
                        );
                    else
                        await ShowSettings(new(category));
                    await CaptureLayout();
                    await CaptureMotion(
                        prefix + "-" + category + "-collapse",
                        () =>
                        {
                            foreach (
                                var expander in CaptureElements(page).OfType<Expander>().ToArray()
                            )
                                expander.IsExpanded = false;
                            return Task.CompletedTask;
                        },
                        outcomes
                    );
                    await CapturePage(prefix + "-" + category + "-" + state + "-collapsed", page);
                    await CaptureMotion(
                        prefix + "-" + category + "-expand",
                        () =>
                        {
                            foreach (
                                var expander in CaptureElements(page).OfType<Expander>().ToArray()
                            )
                                expander.IsExpanded = true;
                            return Task.CompletedTask;
                        },
                        outcomes
                    );
                    await CaptureLayout();
                    foreach (var expander in CaptureElements(page).OfType<Expander>().ToArray())
                        expander.IsExpanded = true;
                    await CapturePage(prefix + "-" + category + "-" + state + "-expanded", page);
                    if (category == SettingsCategory.Plugins)
                    {
                        var database = (Expander)page.FindName("DatabaseSummary");
                        if ((database.Visibility == Visibility.Visible) != showAdvanced)
                            throw new InvalidOperationException(
                                "Database summary did not follow advanced settings."
                            );
                        if (showAdvanced)
                        {
                            database.IsExpanded = true;
                            database.StartBringIntoView();
                            await CaptureLayout();
                            var details = (FrameworkElement)database.Content;
                            if (
                                !details.IsLoaded
                                || details.ActualWidth <= 0
                                || details.ActualHeight <= 0
                                || details.ActualWidth > database.ActualWidth
                            )
                                throw new InvalidOperationException(
                                    "Database details did not fit the window."
                                );
                            database.IsExpanded = false;
                            await CaptureLayout();
                        }
                    }
                }
            }

            await ShowSettings(new(SettingsCategory.Limits));
            await CaptureLayout();
            var body = (ScrollViewer)page.FindName("Body");
            body.ChangeView(null, body.ScrollableHeight / 2, null, true);
            await CaptureLayout();
            var position = body.VerticalOffset;
            await ShowSettings(new(SettingsCategory.Schedule, "weekly_schedule"));
            await CaptureLayout();
            if (page.Category != SettingsCategory.Schedule)
                throw new InvalidOperationException("The Schedule link did not select Schedule.");
            await CaptureUi(prefix + "-schedule-destination");
            await GoBack();
            await CaptureLayout();
            if (
                page.Category != SettingsCategory.Limits
                || Math.Abs(body.VerticalOffset - position) > 1
            )
                throw new InvalidOperationException(
                    $"Back restored {page.Category} at {body.VerticalOffset}; expected Speed limits at {position}, extent {body.ScrollableHeight}."
                );
            await ShowSettings(new(SettingsCategory.Transfers, "queue"));
            await CaptureLayout();
            if (
                page.Category != SettingsCategory.Transfers
                || !((SettingsSection)page.FindName("QueueSection")).IsExpanded
            )
                throw new InvalidOperationException(
                    "The Queue link did not reveal Queue in Transfers."
                );
            await CaptureUi(prefix + "-queue-destination");
            var header = (FrameworkElement)page.FindName("PageHeader");
            header.Width = 280;
            try
            {
                await CaptureUi(prefix + "-header-narrow");
            }
            finally
            {
                header.ClearValue(FrameworkElement.WidthProperty);
            }
            outcomes.Add(new { journey = "settings links and Back", restored = true });

            CaptureInvoke((Control)page.FindName("Brand"));
            await CaptureReady(
                Model.Navigation,
                () => Model.Navigation.Current == WindowPage.Torrents
            );
            await ShowSettings(new());
            var categories = (NavigationView)page.FindName("Categories");
            await CaptureMotion(
                prefix + "-rapid-navigation",
                async () =>
                {
                    for (var visit = 0; visit < 4; visit++)
                    {
                        foreach (
                            var name in new[]
                            {
                                "GeneralCategory",
                                "TransfersCategory",
                                "LimitsCategory",
                                "NetworkCategory",
                                "HomeCategory",
                            }
                        )
                        {
                            var item = (NavigationViewItem)page.FindName(name);
                            categories.SelectedItem = item;
                            await CaptureReady(
                                Model.Navigation,
                                () =>
                                    ReferenceEquals(categories.SelectedItem, item)
                                    && Model.Navigation.CanGoBack
                            );
                            await Task.Delay(40);
                        }
                    }
                },
                outcomes
            );
            await GoBack();
            if (Model.Navigation.Current != WindowPage.Torrents || Model.Navigation.HasBack)
                throw new InvalidOperationException(
                    "Sidebar navigation accumulated Settings history."
                );
            outcomes.Add(
                new { journey = "Settings exit and twenty sidebar selections", returned = true }
            );

            await Model.Navigation.Navigate(WindowPage.Library);
            await ShowSettings(new());
            await CaptureLayout();
            CaptureInvoke((Button)page.FindName("Brand"));
            await CaptureReady(
                Model.Navigation,
                () => Model.Navigation.Current == WindowPage.Library
            );
            if (Model.Navigation.HasBack)
                throw new InvalidOperationException(
                    "The Settings brand did not exit to the originating workspace."
                );
            await ShowTorrents();
            outcomes.Add(new { journey = "Settings brand returns to Library", returned = true });

            await ShowSettings(new(SettingsCategory.Plugins, "subtitle_languages"));
            await CaptureLayout();
            var subtitles = (Subtitles.Settings)
                ((ContentControl)page.FindName("SubtitlesContent")).Content;
            var languages = (RadioButtons)subtitles.Field("subtitle_languages")!;
            languages.SelectedIndex = 1;
            await acquisition.Saving;
            await CaptureLayout();
            var list = CaptureElements(subtitles).OfType<ListView>().Single();
            var choices = list.Items.Cast<ListViewItem>().ToArray();
            foreach (var tag in new[] { "en", "es" })
            {
                var choice = choices.Single(choice => (string)choice.Tag == tag);
                if (!list.SelectedItems.Contains(choice))
                    list.SelectedItems.Add(choice);
            }
            await acquisition.Saving;
            list.ScrollIntoView(choices[0]);
            list.StartBringIntoView();
            await CaptureLayout();
            await CaptureUi(prefix + "-subtitle-catalogue");
            var filter = CaptureElements(subtitles).OfType<AutoSuggestBox>().Single();
            filter.Text = "en";
            var english = list
                .Items.Cast<ListViewItem>()
                .Single(choice => (string)choice.Tag == "en");
            list.ScrollIntoView(english);
            await CaptureLayout();
            await CaptureUi(prefix + "-subtitle-languages");
            var label = (TextBlock)english.Content;
            var point = label
                .TransformToVisual(Root)
                .TransformPoint(
                    new Windows.Foundation.Point(label.ActualWidth / 2, label.ActualHeight / 2)
                );
            if (
                english.Visibility != Visibility.Visible
                || !label.IsLoaded
                || label.ActualWidth <= 0
                || label.ActualHeight <= 0
                || !VisualTreeHelper.FindElementsInHostCoordinates(point, list).Contains(label)
            )
                throw new InvalidOperationException(
                    "The filtered English language is not visible in the list."
                );
            if (
                new[] { "en", "es" }.Any(tag =>
                    !acquisition.Languages.Contains(tag)
                    || !list
                        .SelectedItems.Cast<ListViewItem>()
                        .Any(choice => (string)choice.Tag == tag)
                ) || list.ContainerFromItem(english) is not ListViewItem { IsSelected: true }
            )
                throw new InvalidOperationException(
                    "Filtering subtitle languages lost a selection: "
                        + string.Join(
                            ", ",
                            new[] { "en", "es" }.Select(tag =>
                                tag
                                + " stored="
                                + acquisition.Languages.Contains(tag)
                                + " selected="
                                + list.SelectedItems.Cast<ListViewItem>()
                                    .Any(choice => (string)choice.Tag == tag)
                            )
                        )
                        + "; English container="
                        + (list.ContainerFromItem(english) as ListViewItem)?.IsSelected
                );
            var automatic = (ToggleSwitch)subtitles.Field("subtitle_automatic")!;
            automatic.IsOn = true;
            automatic.IsOn = false;
            await acquisition.Saving;
            if (acquisition.Enabled || automatic.IsOn)
                throw new InvalidOperationException(
                    "Rapid subtitle changes lost the final choice."
                );
            filter.Text = string.Empty;
            languages.SelectedIndex = 0;
            await acquisition.Saving;
            if (acquisition.Languages.Length != 0)
                throw new InvalidOperationException(
                    "The default subtitle language was not restored."
                );
            outcomes.Add(
                new { journey = "subtitle language filtering and rapid changes", retained = true }
            );

            await CapturePlugins(prefix, page);

            await ShowSettings(new(SettingsCategory.Limits));
            await CaptureMotion(prefix + "-connection-enter", ShowConnection, outcomes);
            if (
                Model.Navigation.Current != WindowPage.Connection
                || _settingsPage?.FindName("ChildContent")
                    is not ContentControl
                    {
                        Content: ConnectionPage,
                        Visibility: Visibility.Visible
                    }
            )
                throw new InvalidOperationException(
                    "Connection setup did not open. "
                        + string.Join(
                            "; ",
                            Model
                                .Settings.All.Where(setting =>
                                    setting.HasDraft || setting.Message.Length > 0
                                )
                                .Select(setting => setting.Name + ": " + setting.Message)
                        )
                        + " Schedule: "
                        + Model.Settings.Schedule.Message
                );
            var connection = Model.Settings.Connection;
            await CapturePage(prefix + "-connection-empty", _connectionPage);
            var testInfo = CaptureElements(_connectionPage!)
                .OfType<Button>()
                .Single(button =>
                    AutomationProperties.GetAutomationId(button) == "ConnectionTestInfo"
                );
            testInfo.StartBringIntoView();
            await CaptureLayout();
            CaptureInvoke(testInfo);
            try
            {
                await CaptureLayout();
                await CaptureUi(prefix + "-connection-test-info");
            }
            finally
            {
                testInfo.Flyout!.Hide();
            }
            connection.Download = "200";
            connection.Upload = "40";
            connection.StandardShare = 50;
            connection.StandardShare = 85;
            connection.AlternativeShare = 25;
            await CapturePage(prefix + "-connection-balanced", _connectionPage);
            if (language == "en")
            {
                if (!await connection.Apply())
                    throw new InvalidOperationException(
                        "Connection proposal was not applied: " + connection.Message
                    );
                await CaptureReady(connection, () => !connection.CanApply);
                await CapturePage(prefix + "-connection-applied", _connectionPage);
                if (
                    Model.Settings.ActiveTotal.ConfirmedNumber != 8
                    || Model.Settings.Download.ConfirmedNumber != 21250000
                    || Model.Settings.Upload.ConfirmedNumber != 4250000
                    || Model.Settings.AlternativeDownload.ConfirmedNumber != 6250000
                    || Model.Settings.AlternativeUpload.ConfirmedNumber != 1250000
                )
                    throw new InvalidOperationException(
                        "Connection proposal did not reach confirmed settings."
                    );
                outcomes.Add(new { journey = "connection settings submission", applied = true });
            }
            await CaptureMotion(prefix + "-connection-return", ReturnToSettings, outcomes);
            await CaptureProxy(prefix);
            await CaptureLayout();
            var drafts = Model.Settings.All.Where(setting => setting.HasDraft).ToArray();
            if (drafts.Length > 0)
                throw new InvalidOperationException(
                    "Reading settings created drafts: "
                        + string.Join(
                            "; ",
                            drafts.Select(setting =>
                                setting.Name + "=" + setting.Input + ": " + setting.Message
                            )
                        )
                );

            await ShowSettings(new());
            page.ShowAdvanced(false);
            var search = Search;
            await CaptureLayout();
            var editor =
                TextEditor.Find(search)
                ?? throw new InvalidOperationException("Settings search has no native editor.");
            editor.Focus(FocusState.Keyboard);
            editor.Text = Model.Settings.RefreshInterval.Label;
            var matches = Model.FindSuggestions(editor.Text);
            if (matches.Count == 0)
                throw new InvalidOperationException(
                    "Settings search did not find an advanced setting."
                );
            search.ItemsSource = matches;
            search.IsSuggestionListOpen = true;
            await CaptureLayout();
            await CaptureUi(prefix + "-search");
            search.IsSuggestionListOpen = false;
            search.Text = string.Empty;
            completed.Add(prefix);
        }
        await Model.Settings.PrepareLeave();
    }

    private async Task CaptureLookupLimit(List<object> outcomes)
    {
        await ShowSettings(new(SettingsCategory.Plugins));
        var page = _settingsPage ?? throw new InvalidOperationException("Settings did not open.");
        var input = (TextBox)page.FindName("LookupLimit");
        var setting = Model.Settings.LookupLimit;
        var saved = Model.LookupLimit;
        try
        {
            input.Text = "0";
            await setting.Commit();
            if (!setting.HasDraft || setting.IsPending || Model.LookupLimit != saved)
                throw new InvalidOperationException("An invalid lookup limit changed the saved policy.");
            setting.Cancel();
            var changed = saved == 1 ? 2 : 1;
            input.Text = changed.ToString(System.Globalization.CultureInfo.CurrentCulture);
            await setting.Commit();
            if (Model.LookupLimit != changed || setting.HasDraft || setting.IsPending)
                throw new InvalidOperationException("The lookup limit did not save through the settings editor.");
            input.Text = saved.ToString(System.Globalization.CultureInfo.CurrentCulture);
            await setting.Depart();
            if (Model.LookupLimit != saved || setting.HasDraft || setting.IsPending)
                throw new InvalidOperationException("Leaving the lookup limit did not restore the saved policy.");
            outcomes.Add(new { journey = "lookup limit validation, commit and departure", restored = true });
        }
        finally
        {
            setting.Cancel();
            await Model.Features.SaveLookupLimit(saved);
        }
    }

    private async Task CaptureProviderChoice(List<object> outcomes, List<string> completed)
    {
        var library = Model.Library;

        async Task ChooseProvider(ComboBox choice, int index)
        {
            choice.SelectedIndex = index;
            if (library.ProviderIndex != index)
                throw new InvalidOperationException(
                    "The provider list does not show the choice while it saves."
                );
            await CaptureReady(
                library,
                () => ReferenceEquals(library.Option, library.Providers[index])
            );
            await CaptureLayout();
            if (choice.SelectedIndex != index || library.ProviderIndex != index)
                throw new InvalidOperationException(
                    "The provider list does not show the saved provider."
                );
        }

        await ShowSettings(new(SettingsCategory.Plugins, "video_provider"));
        await CaptureReady(library, () => library.IsAvailable && library.ProviderIndex >= 0);
        await CaptureLayout();
        var providers = (ComboBox)_settingsPage!.FindName("ProviderChoice");
        // Automatic downloads would send the fixture's videos to the provider.
        if (library.AutomaticDownload)
            throw new InvalidOperationException(
                "The provider review requires automatic downloads off."
            );
        var originalProvider = library.ProviderIndex;
        if (CaptureChanges(library, () => CaptureReselect(providers)))
            throw new InvalidOperationException("Selecting the current provider saved it.");
        try
        {
            await ChooseProvider(providers, originalProvider == 0 ? 1 : 0);
        }
        finally
        {
            await ChooseProvider(providers, originalProvider);
        }
        outcomes.Add(
            new
            {
                journey = "provider choice saves",
                providerRestored = library.Option?.ProviderId,
                currentSelectionSaved = false,
            }
        );
        completed.Add("settings-provider-choice");
    }

    // A language change must reach the header of the tab that stays shown.
    private async Task CaptureHeaderLanguage(List<object> outcomes)
    {
        var original = Model.Text.Language;
        var other = original == "es" ? "en" : "es";
        await ShowSettings(new(SettingsCategory.Network));
        await CaptureLayout();
        var fact = (Syno.TinyTorrent.Settings.Fact)_settingsPage!.FindName("FirstFact");
        try
        {
            Model.SelectLanguage(other);
            await CaptureReady(Model, () => Model.CanClose && Model.Text.Language == other);
            await CaptureLayout();
            if (fact.Label != Model.Text.Get("settings", "proxy_server"))
                throw new InvalidOperationException(
                    $"The Network fact caption kept '{fact.Label}' after the language changed."
                );
        }
        finally
        {
            Model.SelectLanguage(original);
            await CaptureReady(Model, () => Model.CanClose && Model.Text.Language == original);
        }
        outcomes.Add(new { journey = "header follows a language change", updated = true });
    }

    private async Task CaptureToastReplay(List<object> outcomes)
    {
        await ShowSettings(new(SettingsCategory.Plugins));
        var page = _settingsPage ?? throw new InvalidOperationException("Settings did not open.");
        if (!BackButton.Focus(FocusState.Programmatic))
            throw new InvalidOperationException(
                "The toast review could not focus the window Back button."
            );
        await CaptureLayout();
        var toasts = Model.Notifications.Toasts;
        var editor = (TextBox)page.FindName("LookupLimit");
        Model.Notifications.Report(new IOException("Proxy connection review"));
        await CaptureLayout();
        var error = toasts.OfType<Toast.CommandError>().Single();
        if (ToastStack.Visibility != Visibility.Visible)
            throw new InvalidOperationException(
                "The proxy-error review did not display its toast."
            );
        if (!editor.Focus(FocusState.Programmatic))
            throw new InvalidOperationException(
                "The toast review could not focus the search-limit editor."
            );
        await CaptureLayout();
        if (ToastStack.Visibility == Visibility.Visible || !toasts.Contains(error))
            throw new InvalidOperationException(
                "A displayed proxy error did not survive hiding for editor focus."
            );
        Model.Notifications.ShowOutcome("subtitles", "connected", string.Empty);
        var unseen = toasts.Last();
        await CaptureLayout();
        if (!toasts.Contains(unseen))
            throw new InvalidOperationException(
                "A toast received while editing did not wait unseen."
            );
        if (!BackButton.Focus(FocusState.Programmatic))
            throw new InvalidOperationException(
                "The toast review could not leave the search-limit editor."
            );
        await CaptureLayout();
        if (
            ToastStack.Visibility != Visibility.Visible
            || !toasts.Contains(error)
            || !toasts.Contains(unseen)
        )
            throw new InvalidOperationException(
                $"Toast reveal failed: errorRetained={toasts.Contains(error)}, "
                    + $"unseenRetained={toasts.Contains(unseen)}, "
                    + $"editorFocused={HasEditorFocus()}, focusedElement={(FocusManager.GetFocusedElement(Root.XamlRoot) as FrameworkElement)?.Name}, "
                    + $"stackVisibility={ToastStack.Visibility}, popupOpen={ToastLayer.IsOpen}."
            );
        if (!editor.Focus(FocusState.Programmatic))
            throw new InvalidOperationException(
                "The toast review could not focus the search-limit editor again."
            );
        await CaptureLayout();
        if (toasts.Contains(unseen) || !toasts.Contains(error))
            throw new InvalidOperationException(
                $"Hiding the stack again kept the shown outcome ({toasts.Contains(unseen)}) "
                    + $"or retired the error ({!toasts.Contains(error)})."
            );
        Model.Notifications.ClearError();
        if (!BackButton.Focus(FocusState.Programmatic))
            throw new InvalidOperationException(
                "The toast review could not leave the search-limit editor."
            );
        await CaptureLayout();
        outcomes.Add(
            new
            {
                journey = "shown error survives a hide; unseen toast waits; seen timed one retires",
                retained = true,
            }
        );
    }

    private async Task CaptureMotion(string journey, Func<Task> change, List<object> outcomes)
    {
        var page = _settingsPage ?? throw new InvalidOperationException("Settings did not open.");
        var surface = (FrameworkElement)page.FindName("PageContent");
        var child = (FrameworkElement)page.FindName("ChildContent");
        var samples = new List<object>();
        var clock = Stopwatch.StartNew();
        void Frame(object? sender, object args)
        {
            if (samples.Count >= 60)
                return;
            samples.Add(
                new
                {
                    elapsedMs = clock.Elapsed.TotalMilliseconds,
                    page.Category,
                    pageX = (surface.RenderTransform as TranslateTransform)?.X ?? 0,
                    childX = (child.RenderTransform as TranslateTransform)?.X ?? 0,
                    drawers = CaptureElements(page, includeCollapsed: true)
                        .Where(element => element.Name == "ExpanderContentClip" && element.IsLoaded)
                        .Select(element => new
                        {
                            element.ActualHeight,
                            element.Visibility,
                            clipHeight = (element.Clip as RectangleGeometry)?.Rect.Height,
                        })
                        .ToArray(),
                }
            );
        }
        CompositionTarget.Rendering += Frame;
        try
        {
            await change();
            await Task.Delay(350);
            outcomes.Add(
                new
                {
                    journey,
                    animationsEnabled = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled,
                    samples,
                }
            );
        }
        finally
        {
            CompositionTarget.Rendering -= Frame;
        }
    }

    private async Task CaptureProxy(string prefix)
    {
        await ShowSettings(new(SettingsCategory.Network));
        var closed = ShowProxy();
        var proxy = Model.Settings.Proxy;
        try
        {
            await CaptureLayout();
            var dialog =
                _interaction?.Dialog
                ?? throw new InvalidOperationException("The proxy editor did not open.");
            foreach (
                var (type, name) in new[] { (0, "none"), (1, "socks5"), (2, "socks4"), (3, "http") }
            )
            {
                proxy.TypeIndex = type;
                proxy.Host = type == 0 ? string.Empty : "proxy.example";
                proxy.Port = type == 0 ? string.Empty : "1080";
                proxy.Username = type == 0 ? string.Empty : "review";
                proxy.Password = type == 0 ? string.Empty : "review-password";
                await CapturePage(prefix + "-proxy-" + name, dialog.Content as FrameworkElement);
            }
        }
        finally
        {
            proxy.Begin();
            _interaction?.Dialog?.Hide();
            await closed;
        }
    }

    private async Task CaptureFill(List<object> outcomes)
    {
        await ShowTorrents();
        await CaptureLayout();
        var bars = CaptureElements(Torrents, includeCollapsed: true)
            .OfType<ProgressBar>()
            .ToDictionary(
                bar => bar,
                bar => new
                {
                    bar.Value,
                    Token = bar.RegisterPropertyChangedCallback(
                        Microsoft.UI.Xaml.Controls.Primitives.RangeBase.ValueProperty,
                        (_, _) =>
                        {
                            if (bar.Value != bar.Maximum)
                                bar.Value = bar.Maximum;
                        }
                    ),
                }
            );
        if (bars.Count == 0)
            throw new InvalidOperationException("The fill review needs a realized progress cell.");
        double[] Widths() =>
            CaptureElements(Torrents)
                .OfType<Syno.TableView.Header.Cell>()
                .Select(cell => cell.ActualWidth)
                .ToArray();
        try
        {
            // The paused fixture has no completed payload; full indicators exercise
            // native ProgressBar measurement without changing the engine or store.
            foreach (var bar in bars.Keys)
                bar.Value = bar.Maximum;
            await CaptureUi("table-full-progress-preparation");
            Torrents.FillWidth();
            await CaptureUi("table-full-progress-before");
            var before = Widths();
            for (var visit = 0; visit < 3; visit++)
            {
                await Model.Navigation.Navigate(WindowPage.Library);
                await CaptureLayout();
                await ShowTorrents();
                await CaptureUi($"table-full-progress-return-{visit + 1}");
                var after = Widths();
                outcomes.Add(
                    new
                    {
                        journey = "full-progress presentation after Library round trip",
                        visit = visit + 1,
                        before,
                        after,
                    }
                );
                if (
                    before.Length != after.Length
                    || before.Zip(after).Any(pair => Math.Abs(pair.First - pair.Second) > 1)
                )
                    throw new InvalidOperationException(
                        "Returning from Library changed the fitted column proportions."
                    );
            }
        }
        finally
        {
            foreach (var (bar, state) in bars)
            {
                bar.UnregisterPropertyChangedCallback(
                    Microsoft.UI.Xaml.Controls.Primitives.RangeBase.ValueProperty,
                    state.Token
                );
                bar.Value = state.Value;
            }
        }
    }

    private async Task CapturePlugins(string prefix, SettingsPage page)
    {
        await ShowSettings(new(SettingsCategory.Plugins));
        await CaptureDialog(
            prefix + "-imdb-delete-confirmation",
            () =>
                Interact(async interaction =>
                {
                    var dialog = new Dialog
                    {
                        Glyph = Lucide.Database,
                        PrimaryGlyph = Lucide.Trash2,
                    };
                    await ShowDialog(
                        interaction,
                        dialog,
                        () =>
                        {
                            dialog.Title = Model.Text.Get("imdb", "delete_title");
                            dialog.Content = DataConfirmation(
                                [
                                    Model.Text.Format(
                                        "imdb",
                                        "delete_body",
                                        Model.Text.Bytes(496815309)
                                    ),
                                    Model.Text.Get("imdb", "delete_kept"),
                                ],
                                [
                                    Model.Text.Get("imdb", "restore_time"),
                                    Model.Text.Get("imdb", "preparation_time"),
                                ]
                            );
                            dialog.PrimaryButtonText = Model.Text.Get("file_action", "delete");
                            dialog.PrimaryToolTip = Model.Text.Get("imdb", "delete_tip");
                        }
                    );
                    return false;
                }),
            () => _interaction?.Dialog
        );
        await CaptureInformation(
            "provider-information",
            (Button)page.FindName("ProviderInformation")
        );
        var subtitles =
            Model.Subtitles ?? throw new InvalidOperationException("Subtitles did not start.");
        var original = subtitles.SupplierId;
        var information = CaptureElements(page)
            .OfType<Button>()
            .Single(button =>
                AutomationProperties.GetAutomationId(button) == "SubtitleSupplierInformation"
            );
        try
        {
            foreach (var supplier in Enum.GetValues<SubtitleSupplier>())
            {
                await CaptureDialog(
                    prefix + "-supplier-" + supplier,
                    () => ShowSupplier(supplier),
                    () => _interaction?.Dialog
                );
                await subtitles.SelectSupplier(supplier);
                await CaptureInformation("supplier-information-" + supplier, information);
            }
        }
        finally
        {
            await subtitles.SelectSupplier(original);
        }

        async Task CaptureInformation(string name, Button button)
        {
            button.StartBringIntoView();
            await CaptureLayout();
            CaptureInvoke(button);
            await CaptureLayout();
            var popup = VisualTreeHelper
                .GetOpenPopupsForXamlRoot(Root.XamlRoot)
                .Single(popup => popup.Child is FlyoutPresenter);
            try
            {
                await CaptureUi(prefix + "-" + name);
            }
            finally
            {
                popup.IsOpen = false;
            }
        }
    }
}
