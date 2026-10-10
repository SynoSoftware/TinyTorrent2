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
    private bool _connected;
    private bool _failureReported;
    private string? _session;
    private long _revision;
    private CancellationTokenSource _generation = new();

    internal event EventHandler? Changed;
    internal event EventHandler<Exception>? Failed;
    internal bool IsReady { get; private set; }

    internal TorrentSource(PipeClient client, FileSources sources)
    {
        _client = client;
        _sources = sources;
        client.Snapshot += Observe;
        client.Disconnected += Disconnect;
        client.FilesChanged += Invalidate;
    }

    internal void Observe(JsonElement snapshot)
    {
        if (snapshot.GetProperty("loading").GetBoolean() || snapshot.GetProperty("storage_failed").GetBoolean())
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
                _generation.Cancel();
                _generation.Dispose();
                _generation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                _revision++;
                _invalidated = true;
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

    private void Invalidate()
    {
        lock (_gate)
        {
            if (_lifetime.IsCancellationRequested)
                return;
            _revision++;
            _invalidated = true;
            _latest = null;
            IsReady = false;
            _generation.Cancel();
            _generation.Dispose();
            _generation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        _ = _client.RefreshSnapshot();
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
                long revision;
                lock (_gate)
                {
                    snapshot = _latest;
                    _latest = null;
                    invalidated = _invalidated;
                    if (snapshot is not null)
                        _invalidated = false;
                    revision = _revision;
                    if (!_connected || (invalidated && snapshot is null))
                    {
                        return;
                    }
                }
                if (snapshot is { } current)
                {
                    var contributions = current.GetProperty("torrents").EnumerateArray().Select(Read).ToArray();
                    changed |= await _sources.Reconcile(contributions, cancellation).ConfigureAwait(false);
                    if (invalidated)
                        await _sources.Invalidate(cancellation).ConfigureAwait(false);
                }

                var next = await _sources.Next(cancellation).ConfigureAwait(false);
                if (next is null)
                {
                    var ready = await _sources.IsReady(cancellation).ConfigureAwait(false);
                    lock (_gate)
                    {
                        if (_latest is not null)
                            continue;
                        ready = ready && _connected && revision == _revision && !cancellation.IsCancellationRequested;
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
                    var detail = await _client.ReadFiles(next.Origin, cancellation).ConfigureAwait(false);
                    lock (_gate)
                    {
                        if (!_connected || revision != _revision)
                            continue;
                        snapshot = _latest;
                        _latest = null;
                    }
                    if (snapshot is { } latest)
                        changed |= await _sources.Reconcile(latest.GetProperty("torrents")
                            .EnumerateArray().Select(Read).ToArray(), cancellation).ConfigureAwait(false);
                    if (Files(next, detail) is { } files)
                        changed |= await _sources.Replace(next, files, cancellation).ConfigureAwait(false);
                    else
                        await _sources.MarkChecked(next, cancellation).ConfigureAwait(false);
                    if (IsReady && changed)
                    {
                        Changed?.Invoke(this, EventArgs.Empty);
                        changed = false;
                    }
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                {
                    // Foreground detail replaces background demand on the connection.
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
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
                if (!_lifetime.IsCancellationRequested && !cancellation.IsCancellationRequested && !_failureReported)
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

    private static Contribution Read(JsonElement row)
    {
        var settled = !row.GetProperty("moving").GetBoolean() &&
            row.GetProperty("move_destination").GetString() is not { Length: > 0 } &&
            row.GetProperty("status").GetString() != "checking";
        var path = row.GetProperty("save_path").GetString()!;
        var complete = row.GetProperty("complete").GetBoolean();
        return new Contribution(row.GetProperty("torrent_id").GetString()!,
            row.GetProperty("name").GetString()!, complete, settled)
        {
            SavePath = path,
            Stamp = JsonSerializer.Serialize(new
            {
                path, complete, settled,
                size = row.GetProperty("size").GetInt64(),
            }),
        };
    }

    private static IReadOnlyList<SourceFile>? Files(Contribution contribution, FileDetail detail)
    {
        if (!detail.Files.GetProperty("metadata_ready").GetBoolean())
            return null;
        var verified = new HashSet<string>(StringComparer.Ordinal);
        if (detail.Pieces is { } pieces)
        {
            var bits = pieces.GetProperty("verified").EnumerateArray().Select(bit => bit.GetBoolean()).ToArray();
            foreach (var file in pieces.GetProperty("files").EnumerateArray())
            {
                var first = file.GetProperty("first_piece").GetInt32();
                var end = file.GetProperty("end_piece").GetInt32();
                if (first >= 0 && end <= bits.Length && end >= first &&
                    Enumerable.Range(first, end - first).All(index => bits[index]))
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
            files.Add(new SourceFile(file.GetProperty("index").GetInt32(),
                Path.GetFullPath(Path.Combine(contribution.SavePath, disk)),
                Path.GetFullPath(Path.Combine(contribution.SavePath, path)),
                file.GetProperty("size").GetInt64())
            {
                Complete = verified.Contains(path),
                Wanted = file.GetProperty("priority").GetInt32() > 0,
            });
        }
        return files;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Snapshot -= Observe;
        _client.Disconnected -= Disconnect;
        _client.FilesChanged -= Invalidate;
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
