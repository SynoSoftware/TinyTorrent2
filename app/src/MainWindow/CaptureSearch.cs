using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Syno.TinyTorrent.Models;
using Windows.Graphics;

namespace Syno.TinyTorrent;

public sealed partial class MainWindow
{
    private async Task CaptureSearch(Torrent target, List<object> outcomes, List<string> completed)
    {
        var failures = new List<string>();
        var baseline =
            Environment.GetEnvironmentVariable("TINYTORRENT_CAPTURE_SEARCH_BASELINE") == "1";
        Model.SelectLanguage("en");
        await CaptureReady(Model, () => Model.CanClose && Model.Text.Language == "en");
        await Model.Settings.SelectTheme("light");
        await CaptureReady(Model, () => Model.CanClose && Model.Theme == "light");
        var scale = Root.XamlRoot.RasterizationScale;
        var minimum =
            ((Microsoft.UI.Windowing.OverlappedPresenter)AppWindow.Presenter).PreferredMinimumWidth
            ?? 0;
        AppWindow.Resize(new SizeInt32(Math.Max((int)(1040 * scale), minimum), (int)(680 * scale)));

        async Task<ListView?> Open(string query, string name)
        {
            await CaptureLayout();
            ThemeButton.Focus(FocusState.Programmatic);
            Search.Text = query;
            Search.Focus(FocusState.Keyboard);
            await CaptureLayout();
            await CaptureUi(name);
            var list = VisualTreeHelper
                .GetOpenPopupsForXamlRoot(Root.XamlRoot)
                .SelectMany(popup => CaptureElements(popup.Child))
                .OfType<ListView>()
                .SingleOrDefault(list => list.Name == "SuggestionsList");
            if (
                list is null
                && Search.ItemsSource is IEnumerable<Suggestion> suggestions
                && suggestions.Any()
            )
                throw new InvalidOperationException(
                    "The native search popup did not open for its results."
                );
            return list;
        }

        async Task Matrix()
        {
            foreach (var language in new[] { "en", "es" })
            foreach (var theme in new[] { "light", "dark" })
            {
                await Model.Settings.SelectTheme(theme);
                await CaptureReady(Model, () => Model.CanClose && Model.Theme == theme);
                Model.SelectLanguage(language);
                await CaptureReady(Model, () => Model.CanClose && Model.Text.Language == language);
                foreach (
                    var size in new[]
                    {
                        new SizeInt32(720, 560),
                        new SizeInt32(1040, 680),
                        new SizeInt32(1280, 800),
                    }
                )
                {
                    AppWindow.Resize(
                        new SizeInt32(
                            Math.Max((int)(size.Width * scale), minimum),
                            (int)(size.Height * scale)
                        )
                    );
                    var prefix =
                        "search-" + language + "-" + theme + "-" + size.Width + "x" + size.Height;
                    await Open(Model.Text.Get("finding", "settings"), prefix + "-settings-results");
                    Search.IsSuggestionListOpen = false;
                    completed.Add(prefix);
                }
            }
        }

        async Task<(bool Available, bool Enabled, bool Submitted)> Choose(
            ListView? list,
            string label
        )
        {
            if (list is null)
                return (false, false, false);
            var suggestion = list
                .Items.OfType<Suggestion>()
                .SingleOrDefault(item => item.Label == label);
            if (suggestion is null)
                return (false, false, false);
            list.ScrollIntoView(suggestion);
            await CaptureLayout();
            var item =
                list.ContainerFromItem(suggestion) as ListViewItem
                ?? throw new InvalidOperationException(
                    "The native search result has no realized container."
                );
            var enabled = item.IsEnabled;
            if (!enabled)
                return (true, false, false);
            var submitted = false;
            void Submitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args) =>
                submitted = true;
            Search.QuerySubmitted += Submitted;
            try
            {
                var peer = FrameworkElementAutomationPeer.CreatePeerForElement(item);
                if (peer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
                    throw new InvalidOperationException(
                        "The native search result does not expose Invoke; selection alone cannot prove submission."
                    );
                invoke.Invoke();
                await CaptureLayout();
            }
            finally
            {
                Search.QuerySubmitted -= Submitted;
            }
            if (!submitted)
                throw new InvalidOperationException(
                    "Invoking the native result did not submit the AutoSuggestBox query."
                );
            return (true, enabled, submitted);
        }

        await ShowTorrents();
        Torrents.Selection = new Syno.TableView.Selection([], null);
        await SelectTorrent();
        Model.CloseInspector();
        var properties = Model.Text.Get("commands", "properties");
        var list = await Open(properties, "search-unavailable-before");
        var choice = await Choose(list, properties);
        await CaptureUi("search-unavailable-after");
        var unavailable = !choice.Available || !choice.Enabled;
        outcomes.Add(
            new
            {
                journey = "unavailable result without selection",
                choice.Available,
                choice.Enabled,
                choice.Submitted,
                inspectorOpened = Model.HasInspector,
                unavailable,
                page = Model.Page.ToString(),
            }
        );
        if (!unavailable)
            failures.Add("Properties remains selectable without a torrent selection.");
        Search.IsSuggestionListOpen = false;
        completed.Add("search-unavailable");

        Torrents.Selection = new Syno.TableView.Selection([target], target);
        await SelectTorrent();
        await ShowSettings(new(SettingsCategory.General));
        list = await Open(properties, "search-properties-settings-before");
        choice = await Choose(list, properties);
        await CaptureLayout();
        await CaptureUi("search-properties-settings-after");
        var visible =
            Model.Page == WindowPage.Torrents
            && Workspace.Visibility == Visibility.Visible
            && Model.HasInspector
            && Model.Inspector.Target?.TorrentId == target.TorrentId
            && InspectorRow.ActualHeight > 0;
        outcomes.Add(
            new
            {
                journey = "Properties from Settings",
                choice.Submitted,
                visible,
                page = Model.Page.ToString(),
                inspectorOpened = Model.HasInspector,
                workspaceVisible = Workspace.Visibility == Visibility.Visible,
            }
        );
        if (!choice.Submitted || !visible)
            failures.Add("Properties selected from Settings does not reveal its inspector.");
        Search.IsSuggestionListOpen = false;
        Model.CloseInspector();
        completed.Add("search-properties-settings");

        await ShowTorrents();
        var rate = Model.Settings.All.Single(setting => setting.Name == "download_limit");
        var rateInput = rate.Input;
        (bool Shown, bool Focused) LimitsState()
        {
            if (_settingsPage is not { } page)
                return (false, false);
            var shown =
                Model.Page == WindowPage.Settings
                && CaptureElements(page)
                    .OfType<SelectorBar>()
                    .Single(control => control.Name == "Categories")
                    .SelectedItem?.Tag?.ToString() == "Limits";
            var editor = CaptureElements(page)
                .OfType<ComboBox>()
                .SingleOrDefault(control => control.Name == "LimitsChoice");
            var focus = FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject;
            while (focus is not null && !ReferenceEquals(focus, editor))
                focus = VisualTreeHelper.GetParent(focus);
            return (shown, editor is not null && focus is not null);
        }
        var limits = Model.Text.Get("commands", "limits");
        list = await Open(limits, "search-limits-before");
        choice = await Choose(list, limits);
        await CaptureLayout();
        await CaptureUi("search-limits-after");
        var limitsState = LimitsState();
        var limitsRetained = rate.Input == rateInput && !rate.HasDraft && !rate.IsPending;
        outcomes.Add(
            new
            {
                journey = "speed limits result",
                choice.Submitted,
                limitsState.Shown,
                limitsState.Focused,
                retained = limitsRetained,
                page = Model.Page.ToString(),
                field = rate.Name,
            }
        );
        if (!choice.Submitted || !limitsState.Shown || !limitsState.Focused || !limitsRetained)
            failures.Add(
                "Speed limits search does not focus the existing Speed limits setting without changing it."
            );
        Search.IsSuggestionListOpen = false;
        completed.Add("search-limits");

        await ShowTorrents();
        var menuPeer = FrameworkElementAutomationPeer.CreatePeerForElement(TorrentMenu);
        if (
            menuPeer.GetPattern(PatternInterface.ExpandCollapse)
            is not IExpandCollapseProvider expand
        )
            throw new InvalidOperationException(
                "The Torrent menu does not expose native expansion."
            );
        expand.Expand();
        await CaptureLayout();
        await CaptureUi("search-menu-limits-before");
        var menu = TorrentMenu
            .Items.OfType<MenuFlyoutItem>()
            .Single(item => item.Command == Model.Limits);
        var itemPeer = FrameworkElementAutomationPeer.CreatePeerForElement(menu);
        if (itemPeer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
            throw new InvalidOperationException(
                "The Speed limits menu item does not expose native Invoke."
            );
        invoke.Invoke();
        await CaptureLayout();
        await CaptureUi("search-menu-limits-after");
        var menuState = LimitsState();
        var menuRetained = rate.Input == rateInput && !rate.HasDraft && !rate.IsPending;
        outcomes.Add(
            new
            {
                journey = "speed limits menu",
                menuState.Shown,
                menuState.Focused,
                retained = menuRetained,
                page = Model.Page.ToString(),
                field = rate.Name,
            }
        );
        if (!menuState.Shown || !menuState.Focused || !menuRetained)
            failures.Add(
                "Speed limits menu does not focus the same Transfers setting as Search without changing it."
            );
        completed.Add("search-menu-limits");

        await ShowTorrents();
        var port = Model.Settings.Port;
        var input = port.Input;
        list = await Open(port.Label, "search-named-setting-before");
        choice = await Choose(list, port.Label);
        await CaptureLayout();
        await CaptureUi("search-named-setting-after");
        var editor = _settingsPage is null
            ? null
            : CaptureElements(_settingsPage)
                .OfType<NumberBox>()
                .SingleOrDefault(control => ReferenceEquals(control.Tag, port));
        var focus = FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject;
        while (focus is not null && !ReferenceEquals(focus, editor))
            focus = VisualTreeHelper.GetParent(focus);
        var focused = editor is not null && focus is not null && Model.Page == WindowPage.Settings;
        var retained = port.Input == input && !port.HasDraft && !port.IsPending;
        outcomes.Add(
            new
            {
                journey = "named Settings result",
                choice.Submitted,
                focused,
                retained,
                field = "listen_port",
                page = Model.Page.ToString(),
            }
        );
        if (!choice.Submitted || !focused || !retained)
            failures.Add("The named setting does not focus its editor without changing the value.");
        Search.IsSuggestionListOpen = false;
        Search.Text = string.Empty;
        completed.Add("search-named-setting");
        if (!baseline)
        {
            await Open(Model.Text.Get("finding", "settings"), "search-reopen-before");
            Search.IsSuggestionListOpen = false;
            await CaptureLayout();
            focus = FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject;
            while (focus is not null && !ReferenceEquals(focus, Search))
                focus = VisualTreeHelper.GetParent(focus);
            if (focus is null || Search.IsSuggestionListOpen)
                throw new InvalidOperationException(
                    "The reopen check requires focused Search with its results closed."
                );
            FocusSearch();
            await CaptureLayout();
            var reopened =
                Search.IsSuggestionListOpen
                && VisualTreeHelper
                    .GetOpenPopupsForXamlRoot(Root.XamlRoot)
                    .SelectMany(popup => CaptureElements(popup.Child))
                    .OfType<ListView>()
                    .Any(control =>
                        control.Name == "SuggestionsList"
                        && control.IsLoaded
                        && control.Items.Count > 0
                    );
            await CaptureUi("search-reopen-after");
            outcomes.Add(
                new
                {
                    journey = "reopen focused Search",
                    reopened,
                    scope = "Production FocusSearch handler; no synthesized shortcut delivery.",
                }
            );
            if (!reopened)
                failures.Add(
                    "The Search focus handler does not reopen results while Search already has focus."
                );
            completed.Add("search-reopen");
        }
        outcomes.Add(
            new
            {
                baseline,
                failures,
                scope = "Native AutoSuggestBox popup item Invoke; programmatic native focus, no synthesized Ctrl+K or desktop input.",
            }
        );
        if (!baseline && failures.Count > 0)
            throw new InvalidOperationException(string.Join(" ", failures));
        if (!baseline)
        {
            Torrents.Selection = new Syno.TableView.Selection([], null);
            await SelectTorrent();
            await Matrix();
            Search.Text = string.Empty;
        }
    }
}
