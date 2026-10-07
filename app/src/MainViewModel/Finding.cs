using System.ComponentModel;
using System.Windows.Input;
using Syno.TinyTorrent.Models;

namespace Syno.TinyTorrent;

public sealed partial class MainViewModel
{
    private string _query = string.Empty;
    private TorrentFilter _filter;
    private bool _filterOpen;
    private WindowPage _page;

    public WindowPage Page
    {
        get => _page;
        internal set
        {
            if (_page == value) return;
            _page = value;
            Inspector.SetVisible(value == WindowPage.Torrents);
            Changed(nameof(Page));
        }
    }
    public string AboutTitle => Text.Get("window", "title");
    public string AboutDescription => Text.Get("about", "description");
    public string VersionText => Text.Format("about", "version", typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? string.Empty);

    public IReadOnlyList<Torrent> VisibleTorrents { get; private set; } = [];
    public string Query { get => _query; set { if (_query == value) return; _query = value; Changed(nameof(Query)); } }
    public TorrentFilter Filter
    {
        get => _filter;
        set { if (_filter == value) return; _filter = value; Project(); RefreshWindow(); }
    }
    public bool ErrorsOnly
    {
        get => Filter == TorrentFilter.Errors;
        set { if (value) Filter = TorrentFilter.Errors; else if (ErrorsOnly) Filter = TorrentFilter.All; }
    }
    public bool IsFilterOpen
    {
        get => _filterOpen;
        set { if (_filterOpen == value) return; _filterOpen = value; Changed(nameof(IsFilterOpen)); }
    }
    public ICommand SwitchFilters { get; }
    public IReadOnlyList<FilterChoice> Filters { get; }
    public bool HasFilter => Filter != TorrentFilter.All;
    public string FilterLabel => Filter == TorrentFilter.All ? Text.Get("filters", "title") :
        Text.Format("filters", "active", Text.Get("filters", Filter.ToString().ToLowerInvariant()), VisibleTorrents.Count);
    public ICommand ShowPreferences { get; }
    public ICommand ShowTorrents { get; }
    public ICommand ShowAbout { get; }
    public event EventHandler<PreferenceTarget>? PreferencesRequested;
    public event EventHandler? TorrentsRequested;
    public event EventHandler? AboutRequested;

    internal static bool Matches(Torrent torrent, TorrentFilter filter) => filter switch
    {
        TorrentFilter.All => true,
        TorrentFilter.Downloading => torrent.StatusCode is "downloading" or "metadata",
        TorrentFilter.Seeding => torrent.StatusCode is "seeding" or "completed",
        TorrentFilter.Paused => torrent.StatusCode is "paused" or "all_paused",
        TorrentFilter.Queued => torrent.StatusCode == "queued",
        TorrentFilter.Errors => torrent.IsError,
        _ => false
    };

    private bool Project()
    {
        var desired = Torrents.Where(torrent => Matches(torrent, Filter)).OrderBy(torrent => torrent.QueueOrder).ToArray();
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

    private Task ClearFinding()
    {
        Filter = TorrentFilter.All;
        return Task.CompletedTask;
    }

    private Task Jump(string torrentId)
    {
        if (!_byId.TryGetValue(torrentId, out var torrent)) return Task.CompletedTask;
        ClearFinding();
        RevealRequested?.Invoke(this, [torrent]);
        return Task.CompletedTask;
    }

    private Task RequestPreferences(PreferenceTarget target)
    {
        PreferencesRequested?.Invoke(this, target);
        return Task.CompletedTask;
    }

    public IReadOnlyList<Suggestion> FindSuggestions(string query)
    {
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var commands = CommandSuggestions().Where(suggestion => suggestion.IsEnabled);
        if (words.Length == 0)
            return commands.Where(suggestion => suggestion.Command == ShowPreferences || suggestion.Command == Add ||
                suggestion.Command == ShowTorrents || suggestion.Command == ShowAbout || suggestion.Command == AddMagnet ||
                suggestion.Command == (AllPaused ? ResumeAll : PauseAll)).ToArray();

        bool MatchesQuery(string text) => words.All(word => text.Contains(word, StringComparison.CurrentCultureIgnoreCase));
        var actions = commands.Concat(PreferenceSuggestions())
            .Where(suggestion => MatchesQuery(suggestion.Label + " " + suggestion.Detail)).Take(30).ToArray();
        var torrents = Torrents.Where(torrent => MatchesQuery(torrent.Name + " " + torrent.Status))
            .Take(30 - actions.Length).Select(torrent => new Suggestion(torrent.Name,
                Text.Format("finding", "torrent_detail", torrent.SizeText, torrent.Status), SuggestionScope.Torrent,
                new Command(() => Jump(torrent.TorrentId), () => _byId.ContainsKey(torrent.TorrentId))));
        return [.. torrents, .. actions];
    }

    private IEnumerable<Suggestion> CommandSuggestions()
    {
        var selection = Text.FormatCount("finding", "selection", _selected.Length);
        var all = Text.Get("speed", "all");
        yield return new(Text.Get("finding", "settings"), string.Empty, SuggestionScope.Command, ShowPreferences);
        yield return new(Text.Get("window", "torrents"), string.Empty, SuggestionScope.Command, ShowTorrents);
        yield return new(Text.Get("about", "title"), string.Empty, SuggestionScope.Command, ShowAbout);
        if (HasUpdate) yield return new(UpdateText, string.Empty, SuggestionScope.Command, OpenUpdate);
        yield return new(Text.Get("commands", "add_file"), string.Empty, SuggestionScope.Command, Add);
        yield return new(Text.Get("commands", "add_magnet"), string.Empty, SuggestionScope.Command, AddMagnet);
        yield return new(Text.Get("commands", AllPaused ? "resume_all" : "pause_all"), all, SuggestionScope.Command, AllPaused ? ResumeAll : PauseAll);
        yield return new(Text.Get("commands", "limits"), Text.Format("finding", "preference_detail", Text.Get("preferences", "transfers")),
            SuggestionScope.Settings, Limits);
        yield return new(Text.Get("chrome", IsDark ? "light" : "dark"), string.Empty, SuggestionScope.Command, SwitchTheme);
        yield return new(Text.Get("commands", "exit"), string.Empty, SuggestionScope.Command, CloseWindow);
        yield return new(Text.Get("commands", "exit_all"), string.Empty, SuggestionScope.Command, Exit);
        yield return new(RestartText, string.Empty, SuggestionScope.Command, Restart);
        foreach (var (key, command) in new (string, ICommand)[]
        {
            ("pause", Pause), ("resume", Resume), ("force", Force), ("verify", Verify), ("remove", Remove),
            ("move", MoveFiles), ("delete_files", DeleteFiles),
            ("open", Open), ("open_folder", OpenFolder), ("copy_magnet", CopyMagnet), ("copy_hash", CopyHash),
            ("properties", Properties), ("up", Up), ("down", Down), ("top", Top), ("bottom", Bottom)
        })
            yield return new(Text.Get("commands", key), selection,
                command == Properties ? SuggestionScope.Navigation : SuggestionScope.Command, command);
        foreach (var (order, state, command) in new[] { (PieceOrder.Sequential, Sequential, SwitchSequential),
            (PieceOrder.FirstLast, FirstLast, SwitchFirstLast) })
            yield return new(Text.Get("commands", PieceOrderKey(order, state != true)), selection, SuggestionScope.Command, command);
        foreach (var choice in Filters)
            yield return new(choice.Label, Text.Get("finding", "filter"), SuggestionScope.Navigation,
                new Command(() => { Filter = choice.Filter; IsFilterOpen = true; return Task.CompletedTask; }, () => true));
        yield return new(Text.Get("filters", "title"), Text.Get("finding", "filter"), SuggestionScope.Navigation, SwitchFilters);
        yield return new(Text.Get("menus", "toolbar"), string.Empty, SuggestionScope.Navigation, SwitchToolbar);
        foreach (var section in Enum.GetValues<InspectorSection>())
            yield return new(Text.Get("inspector", section.ToString().ToLowerInvariant()),
                Text.Get("commands", "properties") + " · " + selection, SuggestionScope.Navigation,
                new Command(() => Inspect(section), () => Properties.CanExecute(null)));
    }

    private IEnumerable<Suggestion> PreferenceSuggestions()
    {
        foreach (var section in Enum.GetValues<PreferenceSection>())
            yield return PreferenceSuggestion(new(section), Text.Get("preferences", section.ToString().ToLowerInvariant()));
        foreach (var field in Preferences.Fields)
        {
            yield return PreferenceSuggestion(new(field.Section, field.Name), field.Label);
        }
        foreach (var key in new[] { "start_signin", "startup_settings", "open_defaults" })
            yield return PreferenceSuggestion(new(PreferenceSection.General, key), Text.Get("preferences", key));
        yield return PreferenceSuggestion(new(PreferenceSection.Schedule, "add_period"), Text.Get("preferences", "add_period"));
    }

    private Suggestion PreferenceSuggestion(PreferenceTarget target, string label) => new(label,
        target.Field is null ? Text.Get("finding", "settings") : Text.Format("finding", "preference_detail",
            Text.Get("preferences", target.Section.ToString().ToLowerInvariant())), SuggestionScope.Settings,
        new Command(() => RequestPreferences(target), () => true));
}

public sealed class FilterChoice(MainViewModel owner, TorrentFilter filter) : INotifyPropertyChanged
{
    public TorrentFilter Filter { get; } = filter;
    public string Label => owner.Text.Get("filters", Filter.ToString().ToLowerInvariant());
    public int Count => owner.Torrents.Count(torrent => MainViewModel.Matches(torrent, Filter));
    public string Caption => owner.Text.Format("filters", "count", Label, Count);
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    public override string ToString() => Caption;
}

public sealed record Suggestion(string Label, string Detail, SuggestionScope Scope, ICommand Command)
{
    public bool IsEnabled => Command.CanExecute(null);
    public bool HasDetail => Detail.Length > 0;
    public override string ToString() => Label;
}

public sealed record PreferenceTarget(PreferenceSection Section, string? Field = null);
