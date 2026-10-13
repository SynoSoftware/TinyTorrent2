using System.ComponentModel;
using System.Windows.Input;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Views;

namespace Syno.TinyTorrent;

public sealed partial class MainViewModel
{
    private string _query = string.Empty;
    private TorrentFilter _filter;
    private bool _showsFilters;
    private bool _filtersCollapsed;
    internal Navigation Navigation { get; }
    public WindowPage Page => Navigation.Page;
    public ICommand ReturnWorkspace => Navigation.Workspace == WindowPage.Library ? ShowLibrary : ShowTorrents;
    public string ReturnText => Text.Get("settings", Navigation.Workspace == WindowPage.Library ? "return_library" : "return_torrents");

    private void RefreshNavigation()
    {
        if (HasInspector && Page == WindowPage.Torrents && Inspector.Target is null)
            Inspector.Show(_selected.Length == 1 ? _selected[0] : null);
        RefreshInspector();
        Library.SetVisible(Page == WindowPage.Library);
        Changed(nameof(Page));
        Changed(nameof(ReturnWorkspace));
        Changed(nameof(ReturnText));
        Changed(nameof(Query));
        Changed(nameof(FilterStatus));
        Changed(nameof(HasFilter));
        Changed(nameof(CountStatus));
        Changed(nameof(ShowsToolbar));
        ((RelayCommand)ClearFilters).Refresh();
    }

    public string AboutTitle => Text.Get("window", "title");
    public string AboutDescription => Text.Get("about", "description");
    public string VersionText => Text.Format("about", "version", App.Version.ToString());
    public string CreditsText => Text.Get("about", "credits");
    public string TmdbCredit => Text.Get("about", "tmdb");

    public IReadOnlyList<Torrent> VisibleTorrents { get; private set; } = [];
    public string Query
    {
        get => Page == WindowPage.Library ? Library.Query : _query;
        set
        {
            if (Page == WindowPage.Library)
            {
                Library.Query = value;
                return;
            }
            if (_query == value)
                return;
            _query = value;
            Changed(nameof(Query));
        }
    }
    public TorrentFilter Filter
    {
        get => _filter;
        set
        {
            if (_filter == value)
                return;
            // A filter change is the person's own, so its rows move even under the pointer.
            ReleaseRequested?.Invoke(
                this,
                Torrents
                    .Where(torrent => Matches(torrent, _filter) != Matches(torrent, value))
                    .ToArray()
            );
            _filter = value;
            Project();
            RefreshWindow();
        }
    }
    public FilterPane FilterPane
    {
        get =>
            !_showsFilters ? FilterPane.Hidden
            : _filtersCollapsed ? FilterPane.Compact
            : FilterPane.Open;
        set
        {
            if (FilterPane == value)
                return;
            _showsFilters = value != FilterPane.Hidden;
            if (_showsFilters)
                _filtersCollapsed = value == FilterPane.Compact;
            Changed(nameof(FilterPane));
            Changed(nameof(IsFilterOpen));
            Changed(nameof(ShowsFilters));
        }
    }
    public bool IsFilterOpen
    {
        get => FilterPane == FilterPane.Open;
        set => FilterPane = value ? FilterPane.Open : FilterPane.Hidden;
    }
    public bool ShowsFilters
    {
        get => _showsFilters;
        set =>
            FilterPane =
                !value ? FilterPane.Hidden
                : _filtersCollapsed ? FilterPane.Compact
                : FilterPane.Open;
    }
    public ICommand SwitchFilters { get; }
    public IReadOnlyList<FilterChoice> Filters { get; }
    public bool HasFilter => Page != WindowPage.Library && Filter != TorrentFilter.All;
    public string CountStatus => Page == WindowPage.Library ? Library.Status : TorrentCount;
    public string FilterLabel =>
        Filter == TorrentFilter.All
            ? Text.Get("filters", "title")
            : Text.Format("filters", "active", FilterName, VisibleTorrents.Count);
    public string FilterStatus =>
        Text.Format("filters", "count", FilterName, VisibleTorrents.Count);

    // The filter status at the largest count it can reach, every torrent in the list.
    public string WidestFilterStatus => Text.Format("filters", "count", FilterName, Torrents.Count);
    private string FilterName => Text.Get("filters", Filter.ToString().ToLowerInvariant());
    public ICommand ShowSettings { get; }
    public ICommand ShowTorrents { get; }
    public ICommand ShowLibrary { get; }
    public ICommand ShowAbout { get; }
    public event EventHandler<SettingTarget>? SettingsRequested;
    public event EventHandler? TorrentsRequested;
    public event EventHandler? LibraryRequested;
    public event EventHandler? AboutRequested;

    internal static bool Matches(Torrent torrent, TorrentFilter filter) =>
        filter switch
        {
            TorrentFilter.All => true,
            TorrentFilter.Downloading => torrent.StatusCode is "downloading" or "metadata",
            TorrentFilter.Seeding => torrent.StatusCode is "seeding" or "completed",
            TorrentFilter.Paused => torrent.StatusCode == "paused",
            TorrentFilter.Queued => torrent.StatusCode == "queued",
            TorrentFilter.Errors => torrent.IsError,
            _ => false,
        };

    private bool Project()
    {
        var desired = Torrents
            .Where(torrent => Matches(torrent, Filter))
            .OrderBy(torrent => torrent.QueueOrder)
            .ToArray();
        // TableView rebuilds its whole view for every source notification, so a
        // changed projection is published as one new list, not row by row.
        if (!desired.SequenceEqual(VisibleTorrents))
        {
            VisibleTorrents = desired;
            Changed(nameof(VisibleTorrents));
            return true;
        }
        return false;
    }

    internal Task ClearFinding()
    {
        Filter = TorrentFilter.All;
        return Task.CompletedTask;
    }

    private Task Jump(string torrentId)
    {
        if (!_byId.TryGetValue(torrentId, out var torrent))
            return Task.CompletedTask;
        ClearFinding();
        RevealRequested?.Invoke(this, [torrent]);
        return Task.CompletedTask;
    }

    private Task RequestSettings(SettingTarget target)
    {
        SettingsRequested?.Invoke(this, target);
        return Task.CompletedTask;
    }

    internal Task ShowSetting(Setting setting) =>
        RequestSettings(new(setting.Category, setting.Name));

    internal Task ShowProxySetting() => RequestSettings(new(SettingsCategory.Network, "proxy"));

    public IReadOnlyList<Suggestion> FindSuggestions(string query)
    {
        var words = FileName
            .Normalize(query)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var commands = CommandSuggestions().Where(suggestion => suggestion.IsEnabled);
        if (words.Length == 0)
            return commands
                .Where(suggestion =>
                    suggestion.Command == ShowSettings
                    || suggestion.Command == Add
                    || suggestion.Command == ShowTorrents
                    || suggestion.Command == ShowLibrary
                    || suggestion.Command == ShowAbout
                    || suggestion.Command == AddMagnet
                    || suggestion.Command == (IsPausedByChoice ? ResumeAll : PauseAll)
                )
                .ToArray();

        var actions = commands
            .Concat(SettingSuggestions())
            .Where(suggestion => MatchesQuery(suggestion, words))
            .Take(30)
            .ToArray();
        var torrents = Torrents
            .Where(torrent => MatchesQuery(torrent.Name + " " + torrent.Status, words))
            .Take(30 - actions.Length)
            .Select(torrent => new Suggestion(
                torrent.Name,
                Text.Format("finding", "torrent_detail", torrent.SizeText, torrent.Status),
                SuggestionScope.Torrent,
                new RelayCommand(
                    () => Jump(torrent.TorrentId),
                    () => _byId.ContainsKey(torrent.TorrentId)
                )
            ));
        return [.. torrents, .. actions];
    }

    private IEnumerable<Suggestion> CommandSuggestions()
    {
        var selection = Text.FormatCount("finding", "selection", _selected.Length);
        var all = Text.Get("speed", "all");
        yield return new(
            Text.Get("finding", "settings"),
            string.Empty,
            SuggestionScope.Command,
            ShowSettings
        );
        yield return new(
            Text.Get("window", "torrents"),
            string.Empty,
            SuggestionScope.Command,
            ShowTorrents
        );
        yield return new(
            Text.Get("library", "title"),
            string.Empty,
            SuggestionScope.Command,
            ShowLibrary
        );
        yield return new(
            Text.Get("about", "title"),
            string.Empty,
            SuggestionScope.Command,
            ShowAbout
        );
        if (HasUpdate)
            yield return new(UpdateText, string.Empty, SuggestionScope.Command, OpenUpdate);
        yield return new(
            Text.Get("commands", "add_file"),
            string.Empty,
            SuggestionScope.Command,
            Add
        );
        yield return new(
            Text.Get("commands", "add_magnet"),
            string.Empty,
            SuggestionScope.Command,
            AddMagnet
        );
        yield return new(
            Text.Get("commands", IsPausedByChoice ? "resume_all" : "pause_all"),
            all,
            SuggestionScope.Command,
            IsPausedByChoice ? ResumeAll : PauseAll
        );
        yield return new(
            Text.Get("commands", "limits"),
            Text.Format("finding", "setting_detail", Text.Get("settings", "limits")),
            SuggestionScope.Settings,
            Limits
        );
        yield return new(
            Text.Get("chrome", IsDark ? "light" : "dark"),
            string.Empty,
            SuggestionScope.Command,
            SwitchTheme
        );
        yield return new(
            Text.Get("commands", "close"),
            string.Empty,
            SuggestionScope.Command,
            CloseWindow
        );
        yield return new(Text.Get("commands", "exit"), string.Empty, SuggestionScope.Command, Exit);
        yield return new(RestartText, string.Empty, SuggestionScope.Command, Restart);
        foreach (
            var (key, command) in new (string, ICommand)[]
            {
                ("pause", Pause),
                ("resume", Resume),
                ("force", Force),
                ("verify", Verify),
                ("remove", Remove),
                ("move", MoveFiles),
                ("delete_files", DeleteFiles),
                ("speed_limit", LimitSpeed),
                ("open", Open),
                ("open_folder", OpenFolder),
                ("copy_magnet", CopyMagnet),
                ("copy_hash", CopyHash),
                ("properties", Properties),
                ("up", Up),
                ("down", Down),
                ("top", Top),
                ("bottom", Bottom),
            }
        )
            yield return new(
                Text.Get("commands", key),
                selection,
                command == Properties ? SuggestionScope.Navigation : SuggestionScope.Command,
                command
            );
        foreach (
            var (order, state, command) in new[]
            {
                (PieceOrder.Sequential, Sequential, SwitchSequential),
                (PieceOrder.FirstLast, FirstLast, SwitchFirstLast),
            }
        )
            yield return new(
                Text.Get("commands", PieceOrderKey(order, state != true)),
                selection,
                SuggestionScope.Command,
                command
            );
        foreach (var choice in Filters)
            yield return new(
                choice.Label,
                Text.Get("finding", "filter"),
                SuggestionScope.Navigation,
                new RelayCommand(
                    () =>
                    {
                        Filter = choice.Filter;
                        IsFilterOpen = true;
                        return Task.CompletedTask;
                    },
                    () => true
                )
            );
        yield return new(
            Text.Get("filters", "title"),
            Text.Get("finding", "filter"),
            SuggestionScope.Navigation,
            SwitchFilters
        );
        yield return new(
            Text.Get("menus", "toolbar"),
            string.Empty,
            SuggestionScope.Navigation,
            SwitchToolbar
        );
        foreach (var section in Enum.GetValues<InspectorSection>())
            yield return new(
                Text.Get("inspector", section.ToString().ToLowerInvariant()),
                Text.Get("commands", "properties") + " · " + selection,
                SuggestionScope.Navigation,
                new RelayCommand(() => Inspect(section), () => Properties.CanExecute(null))
            );
    }

    private static bool MatchesQuery(string text, string[] words)
    {
        var normalized = FileName.Normalize(text);
        return words.All(word => normalized.Contains(word, StringComparison.Ordinal));
    }

    private static bool MatchesQuery(Suggestion suggestion, string[] words) =>
        MatchesQuery(
            suggestion.Label + " " + suggestion.Detail + " " + suggestion.SearchTerms,
            words
        );

    private IEnumerable<Suggestion> SettingSuggestions()
    {
        foreach (var category in Enum.GetValues<SettingsCategory>())
            yield return SettingSuggestion(
                new(category),
                Text.Get("settings", category.ToString().ToLowerInvariant())
            );
        foreach (var setting in Settings.All)
        {
            if (setting == Settings.CapacityDownload || setting == Settings.CapacityUpload)
                continue;
            yield return SettingSuggestion(new(setting.Category, setting.Name), setting.Label);
        }
        yield return SettingSuggestion(
            new(SettingsCategory.Limits, "connection_setup"),
            Text.Get("settings", "connection_setup")
        );
        foreach (var key in new[] { "start_signin", "open_defaults", "settings_index" })
            yield return SettingSuggestion(
                new(SettingsCategory.General, key),
                Text.Get("settings", key)
            );
        yield return SettingSuggestion(
            new(SettingsCategory.Schedule, "add_period"),
            Text.Get("settings", "add_period")
        );
        yield return SettingSuggestion(
            new(SettingsCategory.Network, "proxy"),
            Text.Get("settings", "proxy")
        );
        yield return SettingSuggestion(
            new(SettingsCategory.Plugins, "video_information"),
            Library.DownloadLabel
        );
        yield return SettingSuggestion(
            new(SettingsCategory.Plugins, "lookup_limit"),
            Text.Get("plugins", "lookups")
        );
        foreach (var name in new[] { "automatic", "extras", "supplier", "languages", "batch", "scan" })
            yield return SettingSuggestion(
                new(SettingsCategory.Plugins, "subtitle_" + name),
                Text.Get("subtitles", name)
            );
    }

    private Suggestion SettingSuggestion(SettingTarget target, string label) =>
        new(
            label,
            target.Name is null || target.Category is not { } category
                ? Text.Get("finding", "settings")
                : Text.Format(
                    "finding",
                    "setting_detail",
                    Text.Get("settings", category.ToString().ToLowerInvariant())
                ),
            SuggestionScope.Settings,
            new RelayCommand(() => RequestSettings(target), () => true),
            Text.Find(
                "search_terms",
                target.Name ?? target.Category?.ToString().ToLowerInvariant() ?? string.Empty
            ) ?? string.Empty
        );
}

public sealed class FilterChoice(MainViewModel owner, TorrentFilter filter) : INotifyPropertyChanged
{
    public TorrentFilter Filter { get; } = filter;
    public string Label => owner.Text.Get("filters", Filter.ToString().ToLowerInvariant());
    public int Count => owner.Torrents.Count(torrent => MainViewModel.Matches(torrent, Filter));
    public string Caption => owner.Text.Format("filters", "count", Label, Count);
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Refresh() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    public override string ToString() => Caption;
}

public sealed record Suggestion(
    string Label,
    string Detail,
    SuggestionScope Scope,
    ICommand Command,
    string SearchTerms = ""
)
{
    public bool IsEnabled => Command.CanExecute(null);
    public bool HasDetail => Detail.Length > 0;

    public override string ToString() => Label;
}

public sealed record SettingTarget(SettingsCategory? Category = null, string? Name = null);
