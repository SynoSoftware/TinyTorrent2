using System.Text.Json;

namespace Syno.TinyTorrent.Shared.Engine;

internal sealed partial class PipeClient
{
    private long _fileContext;
    private PendingFiles? _files;
    private (Request Request, long Context, TaskCompletionSource Completion)? _inspector;

    // A torrent's payload files with their paths.
    internal IAsyncEnumerable<DetailFile> TorrentFiles(string torrentId) =>
        Files(
            async offset =>
                await Read("files", new { torrent_id = torrentId, offset }).ConfigureAwait(false),
            DetailFile.Read
        );

    // The pieces of a torrent's payload files, read without their paths, so
    // the names of a large torrent are never sent.
    internal IAsyncEnumerable<PieceRange> TorrentPieces(string torrentId) =>
        Files(
            async offset =>
                await Read(
                        "files",
                        new { torrent_id = torrentId, offset, paths = false }
                    )
                    .ConfigureAwait(false),
            PieceRange.Read
        );

    // A torrent's file count, padding files included, which a page past its
    // last file carries with no files.
    internal async Task<int> FileCount(string torrentId) =>
        (
            await Read(
                    "files",
                    new { torrent_id = torrentId, offset = int.MaxValue, paths = false }
                )
                .ConfigureAwait(false)
        )
            .GetProperty("file_count")
            .GetInt32();

    // A torrent's payload files, page by page. `read` gives the page at an
    // offset, or null to end the files early. The next page is requested only
    // after the caller has taken every file of the current one.
    private static async IAsyncEnumerable<T> Files<T>(
        Func<int, Task<JsonElement?>> read,
        Func<JsonElement, T> parse
    )
    {
        var offset = 0;
        while (await read(offset).ConfigureAwait(false) is { } page)
        {
            var files = page.GetProperty("files");
            foreach (var file in files.EnumerateArray())
                if (!file.GetProperty("padding").GetBoolean())
                    yield return parse(file);
            offset += files.GetArrayLength();
            if (files.GetArrayLength() == 0 || offset >= page.GetProperty("file_count").GetInt32())
                yield break;
        }
    }

    // Null when the inspector took the connection first; the caller asks again.
    // The pieces are read only when the torrent has wanted bytes present
    // (`completed`), because only then can a file be verified. A torrent
    // without metadata gives an empty list, which is complete and does not
    // hold back cleanup that waits for unread lists.
    internal async Task<FileDetail?> ReadFiles(
        string torrentId,
        bool completed,
        CancellationToken cancellation
    )
    {
        List<DetailFile> files = [];
        var withdrawn = false;
        var payload = Files(
            async offset =>
            {
                var page = await ReadPage(torrentId, offset, cancellation).ConfigureAwait(false);
                withdrawn = page is null;
                return page;
            },
            DetailFile.Read
        );
        await foreach (var file in payload.ConfigureAwait(false))
            files.Add(file);
        if (withdrawn)
            return null;
        bool[]? verified = null;
        if (completed && files.Count > 0)
        {
            if (
                await ReadSection(torrentId, "pieces", cancellation).ConfigureAwait(false)
                is not { } pieces
            )
                return null;
            verified =
            [
                .. pieces.GetProperty("verified").EnumerateArray().Select(bit => bit.GetBoolean()),
            ];
        }
        return new FileDetail(files, verified);
    }

    private async Task<JsonElement?> ReadSection(
        string torrentId,
        string view,
        CancellationToken cancellation
    )
    {
        var pending = await BeginFiles(torrentId, cancellation).ConfigureAwait(false);
        try
        {
            var read = Read(
                Consumer.Files,
                "torrent",
                new
                {
                    torrent_id = torrentId,
                    context = pending.Context,
                    view,
                }
            );
            // The inspector withdraws an unsent read; a cancelled task is
            // awaited without throwing, because this happens every refresh.
            await ((Task)read)
                .WaitAsync(cancellation)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            cancellation.ThrowIfCancellationRequested();
            if (read.IsCanceled)
                return null;
            var reply = await read.ConfigureAwait(false);
            if (reply.GetProperty("ready").GetBoolean())
                pending.Completion.TrySetResult(reply);
            return await pending
                .Completion.Task.WaitAsync(RequestTimeout, cancellation)
                .ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
                if (ReferenceEquals(_files, pending))
                    EndFiles();
        }
    }

    // A page is background demand like a section: the inspector or a command
    // withdraws it, and the caller then asks again.
    private async Task<JsonElement?> ReadPage(
        string torrentId,
        int offset,
        CancellationToken cancellation
    )
    {
        var pending = await BeginFiles(torrentId, cancellation).ConfigureAwait(false);
        try
        {
            var read = Read(Consumer.Files, "files", new { torrent_id = torrentId, offset });
            await ((Task)read)
                .WaitAsync(cancellation)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            cancellation.ThrowIfCancellationRequested();
            if (read.IsCanceled)
                return null;
            return await read.ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
                if (ReferenceEquals(_files, pending))
                    EndFiles();
        }
    }

    private async Task<PendingFiles> BeginFiles(string torrentId, CancellationToken cancellation)
    {
        Task? inspector;
        lock (_gate)
            inspector = _inspector?.Completion.Task;
        if (inspector is not null)
            await inspector.WaitAsync(cancellation).ConfigureAwait(false);

        lock (_gate)
        {
            if (_files is not null)
                throw new InvalidOperationException("File collection already has a pending read.");
            return _files = new PendingFiles(torrentId, --_fileContext);
        }
    }

    private bool BeginDetail(Request request, long? context)
    {
        lock (_gate)
        {
            if (request.Consumer == Consumer.Files)
            {
                if (_files is not null && _inspector is null)
                    return true;
                request.Completion.TrySetCanceled();
                System.Diagnostics.Debug.WriteLine(
                    "Background torrent file read cancelled: demand changed."
                );
                return false;
            }
            if (request.Consumer != Consumer.Inspector && request.Consumer is not null)
                return true;
            EndFiles();
            if (request.Consumer == Consumer.Inspector)
            {
                EndInspector();
                if (context is { } value)
                {
                    _inspector = (
                        request,
                        value,
                        new(TaskCreationOptions.RunContinuationsAsynchronously)
                    );
                }
            }
            return true;
        }
    }

    private void CompleteDetail(Request request, JsonElement data)
    {
        if (request.Consumer != Consumer.Inspector)
            return;
        // Readiness describes cached facts; the fresh push ends the demand.
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("ready", out _))
            return;
        lock (_gate)
            if (ReferenceEquals(_inspector?.Request, request))
                EndInspector();
    }

    private void ReceiveDetail(JsonElement message)
    {
        var context = message.GetProperty("context").GetInt64();
        lock (_gate)
        {
            if (_inspector?.Context != context)
            {
                if (
                    _files is not { } pending
                    || pending.Context != context
                    || message.GetProperty("torrent_id").GetString() != pending.TorrentId
                )
                    return;
                try
                {
                    var data = ReadOutcome(message, _strings, "torrent");
                    if (data.GetProperty("ready").GetBoolean())
                        pending.Completion.TrySetResult(data);
                }
                catch (CommandException error)
                {
                    pending.Completion.TrySetException(error);
                }
                return;
            }
            if (
                !message.GetProperty("ok").GetBoolean()
                || message.GetProperty("data").GetProperty("ready").GetBoolean()
            )
                EndInspector();
        }
        Detail?.Invoke(message);
    }

    private void EndFiles()
    {
        _files?.Completion.TrySetResult(null);
        _files = null;
        Withdraw(Consumer.Files);
    }

    private void EndInspector()
    {
        _inspector?.Completion.TrySetResult();
        _inspector = null;
    }

    private sealed record PendingFiles(string TorrentId, long Context)
    {
        // Null when the inspector took the connection.
        internal TaskCompletionSource<JsonElement?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

// The payload files of a torrent as the engine's file pages describe them.
// Verified holds the torrent's verified pieces; it is null when the torrent had
// no wanted bytes present, so the pieces were not read.
internal sealed record FileDetail(IReadOnlyList<DetailFile> Files, bool[]? Verified);

// DiskPath is the file's current name on disk, or null until the engine has
// read the names.
internal sealed record DetailFile(
    int Index,
    string Path,
    string? DiskPath,
    long Size,
    PieceRange Pieces,
    int Priority
)
{
    internal static DetailFile Read(JsonElement file) =>
        new(
            file.GetProperty("index").GetInt32(),
            file.GetProperty("path").GetString()!,
            file.GetProperty("disk_path").GetString(),
            file.GetProperty("size").GetInt64(),
            PieceRange.Read(file),
            file.GetProperty("priority").GetInt32()
        );
}

// A file's pieces, End exclusive.
internal readonly record struct PieceRange(int First, int End)
{
    internal static PieceRange Read(JsonElement file) =>
        new(file.GetProperty("first_piece").GetInt32(), file.GetProperty("end_piece").GetInt32());
}
