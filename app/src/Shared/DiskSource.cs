using System.Runtime.InteropServices;
using System.Text.Json;

namespace Syno.TinyTorrent.Shared;

/// <summary>
/// Library's second data source: the media files that gozler finds on the
/// fixed drives. It never lists a folder. It asks gozler when Library is
/// shown or the person asks, compares the answer with the saved disk files,
/// checks that a file it no longer reports is gone before removing it, and
/// hands only the changes to <see cref="FileSources"/>.
/// </summary>
internal sealed class DiskSource : IAsyncDisposable
{
    // One job of a refresh writes at most this many files, so a typed search
    // waits for one job and not for a whole first import.
    private const int JobSize = 2000;
    private const int Offline = 0x1000;
    private const int RecallOnOpen = 0x40000;
    private const int RecallOnDataAccess = 0x400000;
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan IndexingInterval = TimeSpan.FromSeconds(10);

    private readonly FileSources _sources;
    private readonly BackgroundWork _work;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private readonly Timer _timer;
    private CancellationTokenSource _generation;
    private Task? _running;
    private bool _queued;
    private bool _enabled;
    private bool _ready;
    private bool _shown;
    private bool _active;

    // A trigger that came while Library's source was not connected runs
    // when it connects.
    private bool _waiting;
    // The files of the last accepted answer, as gozler wrote them.
    private byte[]? _accepted;

    // Saved files that gozler stopped reporting but the person's check found;
    // each is checked again only when gozler reports it again, or in the
    // next window session.
    private readonly HashSet<string> _present = new(StringComparer.OrdinalIgnoreCase);

    internal DiskSource(FileSources sources, BackgroundWork work)
    {
        _sources = sources;
        _work = work;
        _generation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _timer = new Timer(_ => Trigger(automatic: true));
    }

    internal event EventHandler? Changed;
    internal event EventHandler? Published;

    internal DiskState State { get; private set; } = new(DiskStatus.Off);

    // Whether the saved option has been read, so State says what it holds.
    internal bool IsStarted { get; private set; }

    internal static int HiddenLimit => Gozler.HiddenLimit;

    internal async Task Start()
    {
        var enabled = await _sources.ReadDiscovery(_lifetime.Token).ConfigureAwait(false);
        lock (_gate)
        {
            _enabled = enabled;
            State = new(enabled ? DiskStatus.Current : DiskStatus.Off);
            IsStarted = true;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        if (enabled)
        {
            lock (_gate)
            {
                if (!_shown)
                {
                    return;
                }
            }
            Trigger(automatic: false);
        }
    }

    // Library's readiness: refreshes run only while its source is connected.
    internal void SetReady(bool ready)
    {
        bool waiting;
        lock (_gate)
        {
            _ready = ready;
            waiting = ready && _waiting;
        }
        if (waiting)
        {
            Trigger(automatic: false);
        }
    }

    internal void SetShown(bool shown)
    {
        lock (_gate)
        {
            if (_shown == shown)
            {
                return;
            }
            _shown = shown;
            Schedule();
        }
        if (shown)
        {
            Trigger(automatic: false);
        }
    }

    // The moment the window becomes active is when the person may have moved
    // or deleted a file in another program.
    internal void SetActive(bool active)
    {
        bool trigger;
        lock (_gate)
        {
            trigger = active && !_active && _shown;
            _active = active;
            Schedule();
        }
        if (trigger)
        {
            Trigger(automatic: true);
        }
    }

    // Refresh, Retry, and showing Library again.
    internal void Refresh() => Trigger(automatic: false);

    internal async Task Enable()
    {
        await _sources.EnableDiscovery(_lifetime.Token).ConfigureAwait(false);
        lock (_gate)
        {
            _enabled = true;
            State = new(DiskStatus.Current);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        Trigger(automatic: false);
    }

    internal async Task Disable()
    {
        Task? running;
        lock (_gate)
        {
            _enabled = false;
            _waiting = false;
            _present.Clear();
            State = new(DiskStatus.Off);
            Schedule();
            Restart();
            running = _running;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        if (running is not null)
        {
            await running.ConfigureAwait(false);
        }
        if (await _sources.DisableDiscovery(_lifetime.Token).ConfigureAwait(false))
        {
            Published?.Invoke(this, EventArgs.Empty);
        }
    }

    // Returns false when the list is full: one gozler query holds every
    // hidden folder beside its fixed terms.
    internal async Task<bool> Hide(string folder)
    {
        var hidden = await _sources.ReadHidden(_lifetime.Token).ConfigureAwait(false);
        if (hidden.Count >= HiddenLimit)
        {
            return false;
        }
        // A refresh that read the hidden folders before this job would save
        // the folder's files again, so it is ended before and after it.
        lock (_gate)
        {
            Restart();
        }
        var changed = await _sources.Hide(folder, _lifetime.Token).ConfigureAwait(false);
        lock (_gate)
        {
            Restart();
        }
        if (changed)
        {
            Published?.Invoke(this, EventArgs.Empty);
        }
        return true;
    }

    // The folder's files return as new entries at the next refresh.
    internal async Task Unhide(string folder)
    {
        lock (_gate)
        {
            Restart();
        }
        await _sources.Unhide(folder, _lifetime.Token).ConfigureAwait(false);
        Trigger(automatic: false);
    }

    // Ends the running refresh, whose answer was asked with the old hidden
    // folders or option, so it saves nothing; a new one follows it when the
    // caller triggers. The caller holds _gate.
    private void Restart()
    {
        _queued = _running is not null;
        _accepted = null;
        _generation.Cancel();
        _generation.Dispose();
        _generation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
    }

    internal Task<IReadOnlyList<string>> ReadHiddenFolders() => _sources.ReadHidden(_lifetime.Token);

    // An automatic trigger does not retry a failure: after one, only the
    // person's Refresh or Retry, or showing Library again, asks gozler.
    private void Trigger(bool automatic)
    {
        lock (_gate)
        {
            if (_lifetime.IsCancellationRequested || !_enabled)
            {
                return;
            }
            if (automatic && State.IsFailure)
            {
                return;
            }
            if (!_ready)
            {
                _waiting = true;
                return;
            }
            _waiting = false;
            // The running answer may be older than the change that caused
            // this trigger, so one more refresh follows it.
            if (_running is not null)
            {
                _queued = true;
                return;
            }
            _running = Task.Run(Run);
        }
    }

    private async Task Run()
    {
        while (true)
        {
            CancellationToken cancellation;
            lock (_gate)
            {
                _queued = false;
                cancellation = _generation.Token;
            }
            await RefreshOnce(cancellation).ConfigureAwait(false);
            lock (_gate)
            {
                Schedule();
                if (!_queued || !_enabled || _lifetime.IsCancellationRequested)
                {
                    _running = null;
                    return;
                }
            }
        }
    }

    private async Task RefreshOnce(CancellationToken cancellation)
    {
        await using var run = new CancellableWork(
            _work,
            new(
                new WorkDescription.Discovery(),
                new WorkProgress.Unknown(),
                new WorkExecution.Running()
            ),
            cancellation: CancellationTokenSource.CreateLinkedTokenSource(cancellation)
        );
        // Library refreshes by itself; the person is not waiting for a result.
        run.Operation.ReportsSuccess = false;
        try
        {
            var hidden = await _sources.ReadHidden(run.Token).ConfigureAwait(false);
            var absent = await _sources.ReadAbsent(run.Token).ConfigureAwait(false);
            var returning = Task.Run(() => Returned(absent, run.Token), run.Token);
            GozlerAnswer answer;
            IReadOnlyList<GozlerVolume> volumes = [];
            try
            {
                answer = await Gozler.Search(hidden, run.Token).ConfigureAwait(false);
                // gozler exits 2 whenever volumes_incomplete is set.
                if (answer.Incomplete)
                {
                    volumes = await Gozler.Status(run.Token).ConfigureAwait(false);
                }
            }
            catch (GozlerException failure)
            {
                // A file that returned comes back although gozler failed.
                var back = await returning.ConfigureAwait(false);
                if (
                    back.Count > 0
                    && await _sources
                        .AcceptDisk(new DiskChanges { Returned = back }, run.Token)
                        .ConfigureAwait(false)
                )
                {
                    Published?.Invoke(this, EventArgs.Empty);
                }
                Report(failure.State);
                run.Operation.Outcome = WorkOutcome.Failed;
                run.Operation.Error = failure;
                return;
            }
            var returned = await returning.ConfigureAwait(false);
            var changed = await Accept(answer.Json, volumes, returned, run.Token).ConfigureAwait(false);
            var unready = volumes.Where(volume => volume.Readiness != VolumeReadiness.Ready).ToArray();
            Report(
                new DiskState(
                    unready.Length == 0 ? DiskStatus.Current
                    : unready.Any(volume => volume.Readiness == VolumeReadiness.Indexing)
                        ? DiskStatus.Indexing
                    : DiskStatus.OutOfDate
                )
                {
                    // A volume with no drive letter is named by its GUID path.
                    Drives = unready
                        .SelectMany(volume => volume.Mounts.Count > 0 ? volume.Mounts : [volume.Volume])
                        .ToArray(),
                }
            );
            run.Operation.Outcome = WorkOutcome.Completed;
            if (changed)
            {
                Published?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException) when (run.Token.IsCancellationRequested)
        {
            run.Operation.Outcome = WorkOutcome.Cancelled;
        }
        catch (Exception error)
        {
            run.Operation.Error = error;
            System.Diagnostics.Debug.WriteLine(error);
        }
    }

    /// <summary>
    /// Saves what an answer changed and reports whether the catalogue changed.
    /// An answer whose files are the same bytes as the last accepted one's
    /// writes nothing. A saved file on a volume that
    /// <paramref name="volumes"/> names as not ready is left alone.
    /// </summary>
    internal async Task<bool> Accept(
        byte[] json,
        IReadOnlyList<GozlerVolume> volumes,
        IReadOnlyList<long> returned,
        CancellationToken cancellation
    )
    {
        var items = Items(json);
        lock (_gate)
        {
            if (returned.Count == 0 && _accepted is not null && items.Span.SequenceEqual(_accepted))
            {
                return false;
            }
        }
        var found = Read(json);
        var saved = await _sources.ReadDisk(cancellation).ConfigureAwait(false);
        var unready = volumes
            .Where(volume => volume.Readiness != VolumeReadiness.Ready)
            .Select(volume => volume.Volume)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var (added, updated, missing) = Compare(saved, found);
        var removed = new List<SavedDiskFile>();
        foreach (var volume in missing.GroupBy(file => file.File.Volume, StringComparer.OrdinalIgnoreCase))
        {
            if (unready.Contains(volume.Key) || !Answers(volume.First().File.Path, volume.Key))
            {
                continue;
            }
            foreach (var file in volume)
            {
                cancellation.ThrowIfCancellationRequested();
                bool? exists = Exists(file.File.Path);
                if (exists == false)
                {
                    removed.Add(file);
                }
                else if (exists == true)
                {
                    lock (_gate)
                    {
                        _present.Add(file.File.Path);
                    }
                }
            }
        }
        PairMoves(removed, added, updated);
        // A torrent may still list a moved file at its old path; a path the
        // person's check finds gone leaves Library there too.
        var before = saved.ToDictionary(file => file.Index, file => file.File.Path);
        var vacated = new List<string>();
        foreach (var (index, file) in updated)
        {
            var old = before[index];
            if (!StringComparer.OrdinalIgnoreCase.Equals(old, file.Path) && Exists(old) == false)
            {
                vacated.Add(old);
            }
        }
        var changed = false;
        var jobs = Jobs(added, updated, removed.Select(file => file.Index).ToList(), returned, vacated);
        for (var job = 0; job < jobs.Count; job++)
        {
            changed |= await _sources
                .AcceptDisk(jobs[job] with { Confirms = job == jobs.Count - 1 }, cancellation)
                .ConfigureAwait(false);
        }
        lock (_gate)
        {
            cancellation.ThrowIfCancellationRequested();
            // An answer with a volume not ready left files unchecked, so the
            // same files once the volume is ready are compared again.
            _accepted = unready.Count == 0 ? items.ToArray() : null;
        }
        return changed;
    }

    // The bytes of the answer's files. The rest of an answer, such as
    // gozler's index generation, changes with every change on a volume.
    private static ReadOnlyMemory<byte> Items(byte[] json)
    {
        var reader = new Utf8JsonReader(json);
        reader.Read();
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var items = reader.ValueTextEquals("items"u8);
            reader.Read();
            var start = (int)reader.TokenStartIndex;
            reader.Skip();
            if (items)
            {
                return json.AsMemory(start, (int)reader.BytesConsumed - start);
            }
        }
        return json;
    }

    // The same path is the same entry. A saved path that the answer lacks,
    // whose file ID the answer has at exactly one new path, was renamed or
    // moved on its volume; a hard link pairs only when its ID lost exactly
    // one path and gained exactly one.
    private (List<DiskFile> Added, List<(int, DiskFile)> Updated, List<SavedDiskFile> Missing) Compare(
        IReadOnlyList<SavedDiskFile> saved,
        IReadOnlyList<DiskFile> found
    )
    {
        var paths = new Dictionary<string, DiskFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in found)
        {
            paths.TryAdd(file.Path, file);
        }
        var updated = new List<(int, DiskFile)>();
        var missing = new List<SavedDiskFile>();
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in saved)
        {
            if (!paths.TryGetValue(file.File.Path, out var current))
            {
                lock (_gate)
                {
                    if (_present.Contains(file.File.Path))
                    {
                        continue;
                    }
                }
                missing.Add(file);
                continue;
            }
            known.Add(current.Path);
            var merged = Merge(file.File, current) with { Path = file.File.Path };
            if (merged != file.File)
            {
                updated.Add((file.Index, merged));
            }
        }
        lock (_gate)
        {
            _present.ExceptWith(paths.Keys);
        }
        var added = found.Where(file => !known.Contains(file.Path)).ToList();
        var lost = missing.ToLookup(file => (file.File.Volume, file.File.Id));
        var gained = added.ToLookup(file => (file.Volume, file.Id));
        foreach (var identity in lost)
        {
            if (identity.Count() != 1 || gained[identity.Key].Count() != 1)
            {
                continue;
            }
            var before = identity.Single();
            var after = gained[identity.Key].Single();
            updated.Add((before.Index, Merge(before.File, after)));
            missing.Remove(before);
            added.Remove(after);
        }
        return (added, updated, missing);
    }

    // A file moved to another volume has a new ID. When exactly one file
    // with a name and size was found gone and exactly one new file has that
    // name and size, it is the same entry at its new path.
    private static void PairMoves(
        List<SavedDiskFile> removed,
        List<DiskFile> added,
        List<(int, DiskFile)> updated
    )
    {
        var gone = removed.ToLookup(file => Key(file.File));
        var arrived = added.Where(file => file.Size is not null).ToLookup(Key);
        foreach (var name in gone)
        {
            if (name.Count() != 1 || arrived[name.Key].Count() != 1)
            {
                continue;
            }
            var before = name.Single();
            var after = arrived[name.Key].Single();
            updated.Add((before.Index, after));
            removed.Remove(before);
            added.Remove(after);
        }
    }

    private static (string, long?) Key(DiskFile file) =>
        (Path.GetFileName(file.Path).ToUpperInvariant(), file.Size);

    // An unknown size or date keeps the saved value.
    private static DiskFile Merge(DiskFile saved, DiskFile current) =>
        current with
        {
            Size = current.Size ?? saved.Size,
            Modified = current.Modified ?? saved.Modified,
        };

    // Splits the changes into jobs of at most JobSize files. The old paths of
    // moved files go into the last job, after every move is saved.
    private static List<DiskChanges> Jobs(
        List<DiskFile> added,
        List<(int, DiskFile)> updated,
        List<int> removed,
        IReadOnlyList<long> returned,
        IReadOnlyList<string> vacated
    )
    {
        var jobs = new List<DiskChanges> { new() { Returned = returned } };
        foreach (var chunk in removed.Chunk(JobSize))
        {
            Pack(job => job with { Removed = [.. job.Removed, .. chunk] }, chunk.Length);
        }
        foreach (var chunk in updated.Chunk(JobSize))
        {
            Pack(job => job with { Updated = [.. job.Updated, .. chunk] }, chunk.Length);
        }
        foreach (var chunk in added.Chunk(JobSize))
        {
            Pack(job => job with { Added = [.. job.Added, .. chunk] }, chunk.Length);
        }
        jobs[^1] = jobs[^1] with { Vacated = vacated };
        return jobs;

        void Pack(Func<DiskChanges, DiskChanges> add, int count)
        {
            var job = jobs[^1];
            if (job.Removed.Count + job.Updated.Count + job.Added.Count + count > JobSize)
            {
                jobs.Add(new());
            }
            jobs[^1] = add(jobs[^1]);
        }
    }

    // The absent locations whose file the person's check finds again. They
    // are checked before gozler's answer is used, so a file that returns in
    // a folder that gozler does not report still comes back.
    private static IReadOnlyList<long> Returned(
        IReadOnlyList<(long Entry, string Path)> absent,
        CancellationToken cancellation
    )
    {
        var returned = new List<long>();
        foreach (var (entry, path) in absent)
        {
            cancellation.ThrowIfCancellationRequested();
            if (Exists(path) == true)
            {
                returned.Add(entry);
            }
        }
        return returned;
    }

    private static List<DiskFile> Read(byte[] json)
    {
        using var answer = JsonDocument.Parse(json);
        var files = new List<DiskFile>();
        foreach (var item in answer.RootElement.GetProperty("items").EnumerateArray())
        {
            if (
                !item.TryGetProperty(Gozler.VolumeField, out var volume)
                || !item.TryGetProperty(Gozler.IdField, out var id)
            )
            {
                continue;
            }
            // gozler replaces an unpaired surrogate in path and gives the
            // exact name as UTF-16 code units.
            var path = item.TryGetProperty("path_u16", out var units)
                ? new string(units.EnumerateArray().Select(unit => (char)unit.GetUInt16()).ToArray())
                : item.GetProperty("path").GetString()!;
            var attributes = item.TryGetProperty("attributes", out var bits) ? bits.GetInt64() : 0;
            files.Add(
                new DiskFile(path, volume.GetString()!, id.GetString()!)
                {
                    Size = item.TryGetProperty("size", out var size) ? size.GetInt64() : null,
                    Modified = item.TryGetProperty("mtime_ft", out var modified)
                        ? modified.GetInt64()
                        : null,
                    Placeholder = (attributes & (Offline | RecallOnOpen | RecallOnDataAccess)) != 0,
                }
            );
        }
        return files;
    }

    // File presence: only the person's own check says a file is gone. A
    // file or folder not found is gone; any other failure, such as access
    // denied, says nothing.
    private static bool? Exists(string path)
    {
        try
        {
            File.GetAttributes(path);
            return true;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Whether the drive that holds path is connected and is the volume that
    // gozler named. Another drive can take a missing drive's letter, and a
    // check there would find every file gone.
    private static bool Answers(string path, string volume)
    {
        var mount = new char[Buffer];
        var name = new char[Buffer];
        return GetVolumePathNameW(path, mount, Buffer)
            && GetVolumeNameForVolumeMountPointW(new string(mount).TrimEnd('\0'), name, Buffer)
            && StringComparer.OrdinalIgnoreCase.Equals(new string(name).TrimEnd('\0'), volume);
    }

    private const int Buffer = 1024;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathNameW(string fileName, char[] volumePath, int length);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(
        string mountPoint,
        char[] volumeName,
        int length
    );

    private void Report(DiskState state)
    {
        lock (_gate)
        {
            if (!_enabled || State == state)
            {
                return;
            }
            State = state;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // While Library is shown, a drive that gozler is still indexing is asked
    // about again soon, because the person watches a list that says it is
    // incomplete; otherwise every 5 minutes while the window is active. The
    // caller holds _gate.
    private void Schedule()
    {
        if (_lifetime.IsCancellationRequested)
        {
            return;
        }
        var due =
            !_enabled || !_shown || State.IsFailure ? Timeout.InfiniteTimeSpan
            : State.Status == DiskStatus.Indexing ? IndexingInterval
            : _active ? Interval
            : Timeout.InfiniteTimeSpan;
        _timer.Change(due, Timeout.InfiniteTimeSpan);
    }

    public async ValueTask DisposeAsync()
    {
        Task? running;
        lock (_gate)
        {
            _lifetime.Cancel();
            running = _running;
        }
        await _timer.DisposeAsync().ConfigureAwait(false);
        if (running is not null)
        {
            await running.ConfigureAwait(false);
        }
        _generation.Dispose();
        _lifetime.Dispose();
    }
}

/// <summary>What the person sees about disk discovery.</summary>
internal sealed record DiskState(DiskStatus Status)
{
    // The drives that gozler is indexing or cannot keep current.
    internal IReadOnlyList<string> Drives { get; init; } = [];

    // The installed gozler's version when it is older than TinyTorrent
    // needs, when gozler could report it.
    internal string? Version { get; init; }

    // A failure ends only by the person's action, so nothing retries it by
    // itself.
    internal bool IsFailure =>
        Status
            is DiskStatus.NotInstalled
                or DiskStatus.ProgramMissing
                or DiskStatus.Stopped
                or DiskStatus.NoAnswer
                or DiskStatus.Timeout
                or DiskStatus.Older
                or DiskStatus.Failed;

    // The state in the words that Settings, Library's message bar and a
    // failed refresh share.
    internal string Describe(Strings text) =>
        Status switch
        {
            DiskStatus.Off => string.Empty,
            DiskStatus.Older when Version is { } version => text.Format(
                "discovery",
                "older",
                version,
                Gozler.ExpectedVersion.ToString(3)
            ),
            DiskStatus.Older => text.Format(
                "discovery",
                "older_unknown",
                Gozler.ExpectedVersion.ToString(3)
            ),
            DiskStatus.Indexing or DiskStatus.OutOfDate => text.Format(
                "discovery",
                Status.ToString().ToLowerInvariant(),
                string.Join(", ", Drives)
            ),
            _ => text.Get("discovery", Status.ToString().ToLowerInvariant()),
        };

    public bool Equals(DiskState? other) =>
        other is not null
        && Status == other.Status
        && Version == other.Version
        && Drives.SequenceEqual(other.Drives);

    public override int GetHashCode() => HashCode.Combine(Status, Version);
}

internal enum DiskStatus
{
    Off,
    NotInstalled,
    ProgramMissing,
    Stopped,
    NoAnswer,
    Timeout,
    Older,
    Failed,
    Indexing,
    OutOfDate,
    Current,
}
