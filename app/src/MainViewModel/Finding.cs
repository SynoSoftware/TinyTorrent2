using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using Syno.TinyTorrent.Models;

namespace Syno.TinyTorrent;

public sealed partial class MainViewModel
{
    private string _search = string.Empty;
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

    public ObservableCollection<Torrent> VisibleTorrents { get; } = [];
    public string Search { get => _search; set { if (_search == value) return; _search = value; Refresh(); } }
    public string Query { get => _query; set { if (_query == value) return; _query = value; Changed(nameof(Query)); } }
    public TorrentFilter Filter
    {
        get => _filter;
        set { if (_filter == value) return; _filter = value; Refresh(); }
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
        TorrentFilter.Errors => torrent.ErrorCode.Length > 0 || torrent.IsError,
        _ => false
    };

    private void Project()
    {
        var desired = Torrents.Where(torrent => torrent.Name.Contains(_search, StringComparison.CurrentCultureIgnoreCase) &&
            Matches(torrent, Filter)).OrderBy(torrent => torrent.QueueOrder).ToArray();
        var desiredSet = desired.ToHashSet();
        foreach (var torrent in VisibleTorrents.Where(torrent => !desiredSet.Contains(torrent)).ToArray()) VisibleTorrents.Remove(torrent);
        var visibleSet = VisibleTorrents.ToHashSet();
        foreach (var torrent in desired)
            if (!visibleSet.Contains(torrent)) VisibleTorrents.Add(torrent);
        for (var index = 0; index < desired.Length; index++)
        {
            if (ReferenceEquals(VisibleTorrents[index], desired[index])) continue;
            var current = VisibleTorrents.IndexOf(desired[index]);
            VisibleTorrents.Move(current, index);
        }
        if (Inspector.Target is { } target && !Torrents.Contains(target)) CloseInspector();
    }

    private Task ClearFinding()
    {
        _search = string.Empty;
        _filter = TorrentFilter.All;
        Refresh();
        return Task.CompletedTask;
    }

    private Task Jump(string torrentId)
    {
        if (!_byId.TryGetValue(torrentId, out var torrent)) return Task.CompletedTask;
        ClearFinding();
        RevealRequested?.Invoke(this, torrent);
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
        var application = Text.Get("window", "title");
        var selection = Text.FormatCount("finding", "selection", _selected.Length);
        var all = Text.Get("speed", "all");
        yield return new(Text.Get("finding", "settings"), application, SuggestionScope.Command, ShowPreferences);
        yield return new(Text.Get("window", "torrents"), application, SuggestionScope.Command, ShowTorrents);
        yield return new(Text.Get("about", "title"), application, SuggestionScope.Command, ShowAbout);
        if (HasUpdate) yield return new(UpdateText, application, SuggestionScope.Command, OpenUpdate);
        yield return new(Text.Get("commands", "add_file"), application, SuggestionScope.Command, Add);
        yield return new(Text.Get("commands", "add_magnet"), application, SuggestionScope.Command, AddMagnet);
        yield return new(Text.Get("commands", AllPaused ? "resume_all" : "pause_all"), all, SuggestionScope.Command, AllPaused ? ResumeAll : PauseAll);
        yield return new(Text.Get("commands", "limits"), Text.Format("finding", "preference_detail", Text.Get("preferences", "transfers")),
            SuggestionScope.Settings, Limits);
        yield return new(Text.Get("chrome", IsDark ? "light" : "dark"), application, SuggestionScope.Command, SwitchTheme);
        yield return new(Text.Get("commands", "exit"), application, SuggestionScope.Command, Exit);
        yield return new(RestartText, application, SuggestionScope.Command, Restart);
        foreach (var (key, command) in new (string, ICommand)[]
        {
            ("pause", Pause), ("resume", Resume), ("force", Force), ("verify", Verify), ("remove", Remove),
            ("move", MoveFiles), ("delete_files", DeleteFiles),
            ("open", Open), ("open_folder", OpenFolder), ("copy_magnet", CopyMagnet), ("copy_hash", CopyHash),
            ("properties", Properties), ("up", Up), ("down", Down), ("top", Top), ("bottom", Bottom)
        })
            yield return new(Text.Get("commands", key), selection,
                command == Properties ? SuggestionScope.Navigation : SuggestionScope.Command, command);
        foreach (var choice in Filters)
            yield return new(choice.Label, Text.Get("finding", "filter"), SuggestionScope.Navigation,
                new Command(() => { Filter = choice.Filter; IsFilterOpen = true; return Task.CompletedTask; }, () => true));
        yield return new(Text.Get("filters", "title"), Text.Get("finding", "filter"), SuggestionScope.Navigation,
            new Command(() => { IsFilterOpen = !IsFilterOpen; return Task.CompletedTask; }, () => true));
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
            var section = field.Name switch
            {
                "download_limit" or "upload_limit" or "alternative_download_limit" or "alternative_upload_limit" or
                    "active_downloads" or "active_seeds" or "ratio_limit" or "seeding_minutes" => PreferenceSection.Transfers,
                "connection_limit" or "network_interface" or "port_mapping" or "listen_port" => PreferenceSection.Network,
                "schedule_enabled" => PreferenceSection.Schedule,
                _ => PreferenceSection.General
            };
            yield return PreferenceSuggestion(new(section, field.Name), field.Label);
        }
        foreach (var key in new[] { "start_signin", "startup_settings", "open_defaults", "remove_handler" })
            yield return PreferenceSuggestion(new(PreferenceSection.General, key), Text.Get("preferences", key));
        yield return PreferenceSuggestion(new(PreferenceSection.Appearance, "language"), Text.Get("preferences", "language"));
        yield return PreferenceSuggestion(new(PreferenceSection.Appearance, "theme"), Text.Get("preferences", "theme"));
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
    public override string ToString() => Label;
}

public sealed record PreferenceTarget(PreferenceSection Section, string? Field = null);
