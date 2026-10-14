using System.Text.Json;

namespace Syno.TinyTorrent.Services;

internal sealed class TorrentSource : IAsyncDisposable
{
    private readonly PipeClient _client;
    private readonly FileSources _sources;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private JsonElement? _latest;
    private Task? _work;
    private bool _invalidated;
    private bool _reread;
    private bool _connected;
    private bool _failureReported;
    private string? _session;
    private long _revision;
    private Dictionary<string, (Contribution Contribution, long Size)>? _contributions;
    private CancellationTokenSource _generation = new();

    internal event EventHandler? Changed;
    internal event EventHandler<Exception>? Failed;
    internal bool IsCurrent { get; private set; }
    internal bool IsReady { get; private set; }

    internal TorrentSource(PipeClient client, FileSources sources)
    {
        _client = client;
        _sources = sources;
        client.Snapshot += Observe;
        client.Disconnected += Disconnect;
        client.FilesChanged += Reread;
    }

    internal void Observe(JsonElement snapshot)
    {
        if (
            snapshot.GetProperty("loading").GetBoolean()
            || snapshot.GetProperty("storage_failed").GetBoolean()
        )
        {
            Disconnect(string.Empty);
            return;
        }
        var invalidated = false;
        lock (_gate)
        {
            if (_lifetime.IsCancellationRequested)
                return;
            var session = snapshot.GetProperty("session_id").GetString();
            if (!_connected || _session != session)
            {
                Restart();
                _invalidated = true;
                IsCurrent = false;
                IsReady = false;
                invalidated = true;
            }
            _session = session;
            _connected = true;
            _latest = snapshot;
        }
        if (invalidated)
            Changed?.Invoke(this, EventArgs.Empty);
        lock (_gate)
            if (!_lifetime.IsCancellationRequested && (_work is null || _work.IsCompleted))
                _work = Task.Run(Run);
    }

    // A successful torrent command can change the files of any torrent. The
    // listed files stay while every torrent is read again from a snapshot
    // taken after the command. Changed lets Library show what the read that
    // Restart ended saved but did not report.
    private void Reread()
    {
        lock (_gate)
        {
            if (_lifetime.IsCancellationRequested)
                return;
            Restart();
            _reread = true;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        _ = _client.RefreshSnapshot();
    }

    // Ends the current read, which then saves nothing more. The next read
    // starts from a newer snapshot. The caller holds _gate.
    private void Restart()
    {
        _revision++;
        _contributions = null;
        _latest = null;
        _generation.Cancel();
        _generation.Dispose();
        _generation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
    }

    private void Disconnect(string reason)
    {
        lock (_gate)
        {
            if (_lifetime.IsCancellationRequested)
                return;
            _revision++;
            _connected = false;
            _latest = null;
            IsCurrent = false;
            IsReady = false;
            _generation.Cancel();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task Run()
    {
        CancellationToken cancellation;
        lock (_gate)
            cancellation = _generation.Token;
        var changed = false;
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                JsonElement? snapshot;
                bool invalidated;
                bool reread;
                long revision;
                lock (_gate)
                {
                    snapshot = _latest;
                    _latest = null;
                    invalidated = _invalidated;
                    reread = _reread;
                    revision = _revision;
                    if (!_connected || ((invalidated || reread) && snapshot is null))
                    {
                        return;
                    }
                }
                if (snapshot is { } current)
                {
                    changed |= await Reconcile(current, cancellation).ConfigureAwait(false);
                    if (invalidated)
                        await _sources.Invalidate(cancellation).ConfigureAwait(false);
                    else if (reread)
                        await _sources.Reread(cancellation).ConfigureAwait(false);
                    bool synchronized;
                    lock (_gate)
                    {
                        if (
                            !_connected
                            || revision != _revision
                            || cancellation.IsCancellationRequested
                        )
                            continue;
                        _invalidated = false;
                        _reread = false;
                        synchronized = !IsCurrent;
                        IsCurrent = true;
                    }
                    if (synchronized)
                        Changed?.Invoke(this, EventArgs.Empty);
                }

                var next = await _sources.Next(cancellation).ConfigureAwait(false);
                if (next is null)
                {
                    var ready = await _sources.IsReady(cancellation).ConfigureAwait(false);
                    lock (_gate)
                    {
                        // A read that Restart ended leaves readiness to the
                        // next read, because Reread keeps it.
                        if (_latest is not null || revision != _revision)
                            continue;
                        ready = ready && _connected && !cancellation.IsCancellationRequested;
                        changed |= IsReady != ready;
                        IsReady = ready;
                        if (ready)
                            _failureReported = false;
                    }
                    if (changed)
                        Changed?.Invoke(this, EventArgs.Empty);
                    return;
                }
                try
                {
                    var detail = await _client
                        .ReadFiles(next.Origin, cancellation)
                        .ConfigureAwait(false);
                    lock (_gate)
                    {
                        if (!_connected || revision != _revision)
                            continue;
                        snapshot = _latest;
                        _latest = null;
                    }
                    if (snapshot is { } latest)
                        changed |= await Reconcile(latest, cancellation).ConfigureAwait(false);
                    if (Files(next, detail) is { } files)
                        changed |= await _sources
                            .Replace(next, files, cancellation)
                            .ConfigureAwait(false);
                    else
                        await _sources.MarkChecked(next, cancellation).ConfigureAwait(false);
                    if (changed)
                    {
                        Changed?.Invoke(this, EventArgs.Empty);
                        changed = false;
                    }
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                {
                    // Foreground demand yields the connection without ending collection.
                    if (!_client.IsConnected)
                        return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (IOException) when (!_client.IsConnected) { }
        catch (Exception error)
        {
            lock (_gate)
            {
                if (_lifetime.IsCancellationRequested || cancellation.IsCancellationRequested)
                    return;
                IsReady = false;
            }
            Changed?.Invoke(this, EventArgs.Empty);
            lock (_gate)
            {
                if (
                    !_lifetime.IsCancellationRequested
                    && !cancellation.IsCancellationRequested
                    && !_failureReported
                )
                {
                    _failureReported = true;
                    Failed?.Invoke(this, error);
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _work = null;
                if (_connected && _latest is not null && !_lifetime.IsCancellationRequested)
                    _work = Task.Run(Run);
            }
        }
    }

    private async Task<bool> Reconcile(JsonElement snapshot, CancellationToken cancellation)
    {
        Dictionary<string, (Contribution Contribution, long Size)>? previous;
        lock (_gate)
            previous = _contributions;
        var contributions = snapshot.GetProperty("torrents").EnumerateArray()
            .Select(row => Read(row, previous)).ToDictionary(item => item.Contribution.Origin);
        lock (_gate)
        {
            cancellation.ThrowIfCancellationRequested();
            if (_contributions is not null && _contributions.SequenceEqual(contributions))
                return false;
        }
        var changed = await _sources.Reconcile(
            contributions.Values.Select(item => item.Contribution).ToArray(), cancellation).ConfigureAwait(false);
        lock (_gate)
        {
            cancellation.ThrowIfCancellationRequested();
            _contributions = contributions;
        }
        return changed;
    }

    private static (Contribution Contribution, long Size) Read(
        JsonElement row,
        IReadOnlyDictionary<string, (Contribution Contribution, long Size)>? contributions
    )
    {
        var settled =
            !row.GetProperty("moving").GetBoolean()
            && row.GetProperty("move_destination").GetString() is not { Length: > 0 }
            && row.GetProperty("status").GetString() != "checking";
        var path = row.GetProperty("save_path").GetString()!;
        var complete = row.GetProperty("complete").GetBoolean();
        var origin = row.GetProperty("torrent_id").GetString()!;
        var name = row.GetProperty("name").GetString()!;
        var size = row.GetProperty("size").GetInt64();
        if (contributions is not null && contributions.TryGetValue(origin, out var previous))
        {
            var contribution = previous.Contribution;
            if (previous.Size == size && contribution.Name == name && contribution.SavePath == path
                && contribution.Complete == complete && contribution.Settled == settled)
                return previous;
        }
        return (new Contribution(origin, name, complete, settled)
        {
            SavePath = path,
            Stamp = JsonSerializer.Serialize(
                new
                {
                    path,
                    complete,
                    settled,
                    size,
                }
            ),
        }, size);
    }

    private static IReadOnlyList<SourceFile>? Files(Contribution contribution, FileDetail detail)
    {
        // A torrent without metadata has no files yet, so its empty list is
        // complete and does not hold back cleanup that waits for unread lists.
        if (!detail.Files.GetProperty("metadata_ready").GetBoolean())
            return [];
        var verified = new HashSet<string>(StringComparer.Ordinal);
        if (detail.Pieces is { } pieces)
        {
            var bits = pieces
                .GetProperty("verified")
                .EnumerateArray()
                .Select(bit => bit.GetBoolean())
                .ToArray();
            foreach (var file in pieces.GetProperty("files").EnumerateArray())
            {
                var first = file.GetProperty("first_piece").GetInt32();
                var end = file.GetProperty("end_piece").GetInt32();
                if (
                    first >= 0
                    && end <= bits.Length
                    && end >= first
                    && Enumerable.Range(first, end - first).All(index => bits[index])
                )
                    verified.Add(file.GetProperty("path").GetString()!);
            }
        }
        List<SourceFile> files = [];
        foreach (var file in detail.Files.GetProperty("files").EnumerateArray())
        {
            if (file.GetProperty("padding").GetBoolean())
                continue;
            if (file.GetProperty("disk_path").GetString() is not { } disk)
                return null;
            var path = file.GetProperty("path").GetString()!;
            files.Add(
                new SourceFile(
                    file.GetProperty("index").GetInt32(),
                    Path.GetFullPath(Path.Combine(contribution.SavePath, disk)),
                    Path.GetFullPath(Path.Combine(contribution.SavePath, path)),
                    file.GetProperty("size").GetInt64()
                )
                {
                    Complete = verified.Contains(path),
                    Wanted = file.GetProperty("priority").GetInt32() > 0,
                }
            );
        }
        return files;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Snapshot -= Observe;
        _client.Disconnected -= Disconnect;
        _client.FilesChanged -= Reread;
        Task? work;
        lock (_gate)
        {
            _lifetime.Cancel();
            work = _work;
        }
        if (work is not null)
            await work.ConfigureAwait(false);
        _lifetime.Dispose();
        _generation.Dispose();
    }
}
