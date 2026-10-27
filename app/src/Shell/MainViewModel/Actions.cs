using System.Text.Json;
using System.Windows.Input;
using Syno.TinyTorrent.Library;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Torrents;

namespace Syno.TinyTorrent.Shell;

public sealed partial class MainViewModel
{
    private bool _receivingActivations;
    private bool _activationsPending;
    private bool _resettingProtection;
    private IReadOnlyList<ProtectionScope> _protectionScopes = [];
    private string? _protectionProviderId;
    public bool IsPaused { get; private set; }
    public bool HasIncoming { get; private set; }
    private string _externalIpv4 = string.Empty;
    private string _externalIpv6 = string.Empty;
    internal string PublicAddress =>
        _connected
            ? _externalIpv4.Length > 0
                ? _externalIpv4
                : _externalIpv6
            : string.Empty;
    public bool ShowsExternalIp => Settings.ShowExternalIp.IsOn;
    public string ExternalIp =>
        !_connected || _externalIpv4.Length + _externalIpv6.Length == 0
            ? Text.Get("window", "external_unknown")
            : Text.Format(
                "window",
                "external_ip",
                string.Join(
                    " · ",
                    new[] { _externalIpv4, _externalIpv6 }.Where(address => address.Length > 0)
                )
            );
    public string MissingAdapter { get; private set; } = string.Empty;
    public string Incoming =>
        MissingAdapter.Length > 0 ? Text.Format("window", "no_adapter", MissingAdapter)
        : Settings.Proxy.IsInUse ? Settings.Proxy.Route
        : Text.Get("window", HasIncoming ? "incoming" : "no_incoming");
    public string IncomingStatus =>
        Text.Get(
            "window",
            MissingAdapter.Length > 0 ? "no_adapter_status"
                : Settings.Proxy.IsInUse ? "proxy_status"
                : HasIncoming ? "incoming_status"
                : "no_incoming_status"
        );
    public string IncomingGlyph =>
        MissingAdapter.Length > 0 ? Syno.Lucide.Unplug
        : Settings.Proxy.IsInUse ? Syno.Lucide.Route
        : Syno.Lucide.Network;

    // A proxy route says nothing about the port, so only a direct connection
    // reports the port as open or closed.
    public bool IsIncomingProxied => MissingAdapter.Length == 0 && Settings.Proxy.IsInUse;
    public bool IsIncomingOpen =>
        MissingAdapter.Length == 0 && !Settings.Proxy.IsInUse && HasIncoming;
    public bool IsIncomingClosed => !IsIncomingProxied && !IsIncomingOpen;
    public string SelectionText => Text.Format("window", "selected", _selected.Length);
    public bool HasSelection => _selected.Length > 0;
    public bool CanReorder => CanEdit;
    private bool CanMove => CanEdit && _selected.Any(torrent => torrent.IsQueued);
    public string EmptyTitle =>
        Text.Get("window", Torrents.Count > 0 ? "no_matches" : "empty_title");
    public string EmptyInstruction =>
        Text.Get("window", Torrents.Count > 0 ? "no_matches_detail" : "empty");
    public string EmptyActionText =>
        Text.Get("window", Torrents.Count > 0 ? "clear_filters" : "add");
    public string EmptyActionGlyph =>
        Torrents.Count > 0 ? Syno.Lucide.FunnelX : Syno.Lucide.FilePlus;
    public string EmptyActionToolTip =>
        Text.Get("window", Torrents.Count > 0 ? "clear_filters_tip" : "add_tip");
    public ICommand EmptyAction => Torrents.Count > 0 ? ClearFilters : Add;
    public ICommand ClearFilters { get; }
    public bool CanEditSelection => CanEdit && _selected.Length > 0;

    // Null when the selected torrents differ. Switching a mixed selection
    // turns the choice on for all of them.
    public bool? Sequential => Shared(torrent => torrent.Sequential);
    public bool? FirstLast => Shared(torrent => torrent.FirstLast);
    public ICommand SwitchSequential { get; }
    public ICommand SwitchFirstLast { get; }
    public bool? Limited => Shared(torrent => torrent.IsLimited);
    public ICommand LimitSpeed { get; }
    public SpeedLimit SpeedLimit { get; }
    public Inspector Inspector { get; }
    private bool _minimized;
    internal bool IsMinimized
    {
        get => _minimized;
        set
        {
            if (_minimized == value)
                return;
            _minimized = value;
            RefreshInspector();
        }
    }
    private bool _inspectorOpen;
    public bool HasInspector
    {
        get => _inspectorOpen;
        private set
        {
            if (_inspectorOpen == value)
                return;
            _inspectorOpen = value;
            RefreshInspector();
            Changed(nameof(HasInspector));
        }
    }
    private bool _inspectorCompact;
    public bool IsInspectorCompact
    {
        get => _inspectorCompact;
        set
        {
            if (_inspectorCompact == value)
                return;
            _inspectorCompact = value;
            RefreshInspector();
            Changed(nameof(IsInspectorCompact));
        }
    }
    // Kept while the inspector is compact or hidden, so expanding it again
    // restores the maximized presentation.
    private bool _inspectorMaximized;
    public bool IsInspectorMaximized
    {
        get => _inspectorMaximized;
        set
        {
            if (_inspectorMaximized == value)
                return;
            _inspectorMaximized = value;
            Changed(nameof(IsInspectorMaximized));
        }
    }
    public ICommand AddMagnet { get; }
    public ICommand Force { get; }
    public ICommand Retry { get; }
    public ICommand Verify { get; }
    public ICommand Remove { get; }
    public ICommand MoveFiles { get; }
    public FileDraft FileDraft { get; }
    public ICommand Up { get; }
    public ICommand Down { get; }
    public ICommand Top { get; }
    public ICommand Bottom { get; }
    public ICommand PauseAll { get; }
    public ICommand ResumeAll { get; }
    public ICommand Open { get; }
    public ICommand OpenFolder { get; }
    public ICommand CopyMagnet { get; }
    public ICommand CopyHash { get; }
    public ICommand Properties { get; }
    public ICommand Limits { get; }
    public event EventHandler? FilesRequested;
    public event EventHandler<Torrent[]>? RemoveRequested;
    public event EventHandler? MergeRequested;
    public event EventHandler<Torrent[]>? MoveRequested;
    public event EventHandler<Torrent[]>? SpeedLimitRequested;
    public event EventHandler<OpenRequestedEventArgs>? OpenRequested;
    public event EventHandler<string>? CopyRequested;

    internal ICommand SpeedCommand(Func<Torrent[]> targets) =>
        new RelayCommand(
            () =>
            {
                SpeedLimitRequested?.Invoke(this, targets().ToArray());
                return Task.CompletedTask;
            },
            () => CanEdit && targets() is { Length: > 0 } torrents && torrents.All(Contains)
        );

    internal ICommand MoveCommand(Func<Torrent[]> targets) =>
        new RelayCommand(
            () =>
            {
                MoveRequested?.Invoke(this, targets().ToArray());
                return Task.CompletedTask;
            },
            () =>
                CanEdit
                && targets() is { Length: > 0 } torrents
                && torrents.All(torrent => Contains(torrent) && !torrent.IsMoving)
        );

    internal Torrent? Find(IEnumerable<string> hashes) =>
        Torrents.FirstOrDefault(torrent =>
            torrent.Hashes.Intersect(hashes, StringComparer.OrdinalIgnoreCase).Any()
        );

    internal Torrent? Find(string torrentId) => _byId.GetValueOrDefault(torrentId);

    internal void RequestOpen(string folder) => OpenRequested?.Invoke(this, new(folder));

    // Picked sources come from Add torrent file; the others are dropped,
    // pasted or sent by another instance.
    public async Task AddSources(IEnumerable<string> sources, bool picked, bool needsDialog = false)
    {
        var outcome = await AddDraft.Receive([.. sources], picked, needsDialog);
        if (outcome == ArrivalOutcome.Dialog)
            AddRequested?.Invoke(this, EventArgs.Empty);
        else if (outcome == ArrivalOutcome.Merge)
            MergeRequested?.Invoke(this, EventArgs.Empty);
    }

    // The engine keeps offered sources until activations_received, so sources the
    // window has not taken when closing begins stay with the engine.
    internal async Task ReceiveActivations()
    {
        _activationsPending = true;
        if (_receivingActivations || !_ready || !_connected || IsClosing)
            return;
        _receivingActivations = true;
        try
        {
            while (_activationsPending && _ready && _connected && !IsClosing)
            {
                _activationsPending = false;
                var pending = await _client.Send("pending_activations", null, Requester.Window);
                if (IsClosing)
                {
                    _activationsPending = true;
                    return;
                }
                var activations = pending.GetProperty("activations").EnumerateArray().ToArray();
                if (activations.Length == 0)
                    continue;
                var sources = activations
                    .SelectMany(activation =>
                        activation
                            .GetProperty("sources")
                            .EnumerateArray()
                            .Select(source => source.GetString()!)
                    )
                    .ToArray();
                var arrival = AddSources(sources, picked: false);
                await _client.Send(
                    "activations_received",
                    new
                    {
                        activation_ids = activations
                            .Select(activation =>
                                activation.GetProperty("activation_id").GetString()
                            )
                            .ToArray(),
                    },
                    Requester.Window
                );
                await arrival;
            }
        }
        catch (Exception error)
        {
            Notifications.Report(error);
        }
        finally
        {
            _receivingActivations = false;
            Refresh();
        }
    }

    private Task Queue(string direction) =>
        Queue(
            new
            {
                torrent_ids = _selected
                    .Where(torrent => torrent.IsQueued)
                    .Select(torrent => torrent.TorrentId)
                    .ToArray(),
                direction,
            }
        );

    public Task Reorder(IReadOnlyList<Torrent> torrents, Torrent? before) =>
        Queue(
            new
            {
                torrent_ids = torrents.Select(torrent => torrent.TorrentId).ToArray(),
                before_torrent_id = before?.TorrentId,
            }
        );

    private async Task Queue(object arguments)
    {
        if (!CanEdit)
            return;
        try
        {
            await _client.Send("queue", arguments, Requester.Person);
            Notifications.AnnounceAccepted("outcomes", "queue");
        }
        catch (Exception error)
        {
            Notifications.Report(error);
        }
    }

    private bool? Shared(Func<Torrent, bool> choice) =>
        _selected.All(choice) ? _selected.Length > 0
        : _selected.Any(choice) ? null
        : false;

    private static string PieceOrderKey(PieceOrder order, bool enabled) =>
        (order, enabled) switch
        {
            (PieceOrder.Sequential, true) => "sequential",
            (PieceOrder.Sequential, false) => "sequential_off",
            (PieceOrder.FirstLast, true) => "first_last",
            (PieceOrder.FirstLast, false) => "first_last_off",
            _ => throw new ArgumentOutOfRangeException(nameof(order)),
        };

    private async Task SetPieceOrder(PieceOrder order, bool enabled)
    {
        if (!CanEditSelection)
            return;
        var arguments = new Dictionary<string, object>
        {
            ["torrent_ids"] = _selected.Select(torrent => torrent.TorrentId).ToArray(),
            [order == PieceOrder.Sequential ? "sequential" : "first_last"] = enabled,
        };
        try
        {
            await _client.Send("piece_order", arguments, Requester.Person);
            Notifications.AnnounceAccepted("commands", PieceOrderKey(order, enabled));
        }
        catch (Exception error)
        {
            Notifications.Report(error);
        }
    }

    private async Task SessionPause(bool paused)
    {
        if (!CanEdit)
            return;
        try
        {
            await _client.Send("session_pause", new { paused }, Requester.Person);
            // Resume all cannot lift a missing adapter's pause, so the toast
            // says why nothing starts.
            _waiting = !paused && MissingAdapter.Length > 0 ? [] : null;
            Changed(nameof(HasResumeToast));
            if (HasResumeToast)
                Notifications.Announce(ResumeMessage);
            else
                Notifications.AnnounceAccepted("commands", paused ? "pause_all" : "resume_all");
        }
        catch (Exception error)
        {
            Notifications.Report(error);
        }
    }

    private async Task<JsonElement> Detail(Torrent torrent) =>
        await _client.Send("torrent", new { torrent_id = torrent.TorrentId }, Requester.Window);

    private Task OpenTorrent(bool folder) =>
        _selected.Length == 1 ? OpenTorrent(_selected[0], folder) : Task.CompletedTask;

    internal async Task OpenTorrent(Torrent torrent, bool folder)
    {
        try
        {
            var detail = await Detail(torrent);
            var destination = detail.GetProperty("save_path").GetString()!;
            var files = detail
                .GetProperty("files")
                .EnumerateArray()
                .Where(file => !file.GetProperty("padding").GetBoolean())
                .ToArray();
            var path = detail.GetProperty("folder").GetString()!;
            string? extension = null;
            if (!folder && files.Length == 1)
            {
                var logical = files[0].GetProperty("path").GetString()!;
                var physical = files[0].TryGetProperty("disk_path", out var disk)
                    ? disk.GetString()!
                    : logical;
                path = Path.Combine(destination, physical);
                if (logical != physical)
                    extension = Path.GetExtension(logical);
            }
            OpenRequested?.Invoke(this, new(path, extension));
        }
        catch (Exception error)
        {
            Notifications.Report(error);
        }
    }

    private async Task CopyTorrent(bool hashes)
    {
        if (_selected.Length != 1)
            return;
        try
        {
            var detail = await _client.Send(
                "torrent",
                new { torrent_id = _selected[0].TorrentId, view = "general" },
                Requester.Window
            );
            await Copy(
                hashes
                    ? string.Join(
                        Environment.NewLine,
                        detail
                            .GetProperty("hashes")
                            .EnumerateArray()
                            .Select(hash => hash.GetString())
                    )
                    : detail.GetProperty("magnet").GetString()!
            );
        }
        catch (Exception error)
        {
            Notifications.Report(error);
        }
    }

    internal Task Copy(string text)
    {
        CopyRequested?.Invoke(this, text);
        return Task.CompletedTask;
    }

    private async Task Inspect(InspectorSection section)
    {
        if (_selected.Length != 1)
            return;
        if (Inspector.Show(_selected[0], section))
        {
            HasInspector = true;
            IsInspectorCompact = false;
            await Inspector.Navigate(section);
        }
        Refresh();
    }

    public void OpenInspector(bool expand = true)
    {
        if (
            Page == WindowPage.Torrents
            && !Inspector.Show(_selected.Length == 1 ? _selected[0] : null)
        )
            return;
        HasInspector = true;
        if (expand)
            IsInspectorCompact = false;
    }

    internal void SetInspector(bool open, bool compact)
    {
        HasInspector = open;
        IsInspectorCompact = compact;
    }

    public bool CloseInspector()
    {
        if (Inspector.HasDraft || Inspector.IsPending || !Inspector.Show(null))
            return false;
        HasInspector = false;
        return true;
    }

    private void RefreshInspector()
    {
        Inspector.SetVisible(
            !IsMinimized && HasInspector && !IsInspectorCompact && Page == WindowPage.Torrents
        );
        Library.WatchInspector(HasInspector && !IsInspectorCompact && Page == WindowPage.Library);
    }

    internal event EventHandler? DatabaseDeletionRequested;
    internal ICommand DownloadSubtitles { get; }
    internal event EventHandler<IReadOnlySet<long>>? SubtitlesRequested;
    private bool CanDownloadSubtitles =>
        Subtitles is { Ready: true }
        && Library.IsAvailable
        && Library.Selection.Items.Cast<LibraryRow>().Any(row => row.Entry.Kind == FileKind.Video);

    internal Task DownloadData(IDataset dataset) => AnnounceData(dataset, dataset.Download());

    /// <summary>
    /// Downloads the data of the provider the person chose, which the
    /// installed data selects.
    /// </summary>
    internal Task PrepareAutomaticDownload() =>
        AnnounceData(Library, Library.Download(enableAutomaticDownload: true));

    private async Task AnnounceData(IDataset dataset, Task<bool> download)
    {
        if (await download)
            Notifications.Announce(Text.Get(dataset.Section, "ready"));
    }

    /// <summary>All providers and suppliers first, then each one, for the Settings reset.</summary>
    public IReadOnlyList<ProtectionScope> ProtectionScopes
    {
        get
        {
            ProtectionScope[] scopes =
            [
                new(null, Text.Get("request_protection", "reset_all")),
                .. Library.Providers.Select(provider => new ProtectionScope(
                    provider.ProviderId,
                    provider.Name
                )),
                .. (Subtitles?.Suppliers ?? []).Select(supplier => new ProtectionScope(
                    supplier.ProviderId,
                    supplier.SupplierId.ToString()
                )),
            ];
            // A new list clears the choice's selection, so an equal list is kept.
            if (!_protectionScopes.SequenceEqual(scopes))
                _protectionScopes = scopes;
            return _protectionScopes;
        }
    }

    /// <summary>
    /// The scope that <see cref="ResetProtection"/> resets; all of them until
    /// the person chooses one.
    /// </summary>
    public ProtectionScope ProtectionScope
    {
        get =>
            ProtectionScopes.FirstOrDefault(scope => scope.ProviderId == _protectionProviderId)
            ?? ProtectionScopes[0];
        set
        {
            // The choice clears its selection while its list is replaced.
            if (value is not null)
                _protectionProviderId = value.ProviderId;
        }
    }

    // The scope follows, because a replaced list clears the choice's selection.
    private void RefreshProtectionScopes()
    {
        Changed(nameof(ProtectionScopes));
        Changed(nameof(ProtectionScope));
    }

    public bool CanResetProtection => !_resettingProtection;

    /// <summary>
    /// Resets request protection for <see cref="ProtectionScope"/>, and
    /// reports a failure.
    /// </summary>
    internal async Task ResetProtection()
    {
        if (_resettingProtection)
            return;
        _resettingProtection = true;
        Changed(nameof(CanResetProtection));
        try
        {
            await Features.ResetProtection(ProtectionScope.ProviderId);
        }
        catch (Exception error)
        {
            Notifications.Report(error);
        }
        finally
        {
            _resettingProtection = false;
            Changed(nameof(CanResetProtection));
        }
    }
}

/// <summary>
/// A choice of the request-protection reset: one provider or subtitle
/// supplier, or every one of them when <see cref="ProviderId"/> is null.
/// </summary>
public sealed record ProtectionScope(string? ProviderId, string Name)
{
    public override string ToString() => Name;
}

public sealed class OpenRequestedEventArgs(string path, string? extension = null) : EventArgs
{
    public string Path { get; } = path;
    public string? Extension { get; } = extension;
}
