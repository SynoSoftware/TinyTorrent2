using System.Text.Json;
using Syno.TinyTorrent.Models;

namespace Syno.TinyTorrent.Services;

internal sealed partial class PipeClient
{
    private long _fileContext;
    private PendingFiles? _files;
    private long? _inspectorContext;
    private TaskCompletionSource? _inspector;

    // Null when the inspector took the connection first; the caller asks again.
    internal async Task<FileDetail?> ReadFiles(string torrentId, CancellationToken cancellation)
    {
        if (await ReadSection(torrentId, "files", cancellation).ConfigureAwait(false) is not { } files)
            return null;
        JsonElement? pieces = null;
        if (
            files.GetProperty("metadata_ready").GetBoolean()
            && files
                .GetProperty("files")
                .EnumerateArray()
                .Any(file =>
                    !file.GetProperty("padding").GetBoolean()
                    && file.GetProperty("downloaded").ValueKind == JsonValueKind.Number
                    && file.GetProperty("downloaded").GetInt64()
                        >= file.GetProperty("size").GetInt64()
                )
        )
        {
            if (await ReadSection(torrentId, "pieces", cancellation).ConfigureAwait(false) is not { } section)
                return null;
            pieces = section;
        }
        return new FileDetail(files, pieces);
    }

    private async Task<JsonElement?> ReadSection(
        string torrentId,
        string view,
        CancellationToken cancellation
    )
    {
        Task? inspector;
        lock (_gate)
            inspector = _inspector?.Task;
        if (inspector is not null)
            await inspector.WaitAsync(cancellation).ConfigureAwait(false);

        PendingFiles pending;
        lock (_gate)
        {
            if (_files is not null)
                throw new InvalidOperationException("File collection already has a pending read.");
            pending = new PendingFiles(torrentId, --_fileContext);
            _files = pending;
        }
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
                    include_files = view == "pieces",
                }
            );
            // The inspector withdraws an unsent read; a cancelled task is
            // awaited without throwing, because this happens every refresh.
            await ((Task)read).WaitAsync(cancellation).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
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

    private bool BeginDetail(Command command)
    {
        lock (_gate)
        {
            if (command.Consumer == Consumer.Files)
            {
                if (_files is not null && _inspector is null)
                    return true;
                command.Completion.TrySetCanceled();
                return false;
            }
            if (command.Consumer != Consumer.Inspector && command.Consumer is not null)
                return true;
            EndFiles();
            if (command.Consumer == Consumer.Inspector)
            {
                EndInspector();
                var arguments = JsonSerializer.SerializeToElement(command.Arguments);
                if (
                    arguments.ValueKind == JsonValueKind.Object
                    && arguments.TryGetProperty("context", out var context)
                )
                {
                    _inspectorContext = context.GetInt64();
                    _inspector = new(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }
            return true;
        }
    }

    private void CompleteDetail(Command command, JsonElement data)
    {
        if (command.Consumer != Consumer.Inspector)
            return;
        // Readiness describes cached facts; the fresh push ends the demand.
        lock (_gate)
            if (
                data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("ready", out _)
            )
                EndInspector();
    }

    private void ReceiveDetail(JsonElement message)
    {
        var context = message.GetProperty("context").GetInt64();
        lock (_gate)
        {
            if (_inspectorContext != context)
            {
                if (
                    _files is not { } pending
                    || pending.Context != context
                    || message.GetProperty("torrent_id").GetString() != pending.TorrentId
                )
                    return;
                try
                {
                    pending.Completion.TrySetResult(ReadOutcome(message, _strings, "torrent"));
                }
                catch (CommandException error)
                {
                    pending.Completion.TrySetException(error);
                }
                return;
            }
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
        _inspector?.TrySetResult();
        _inspector = null;
        _inspectorContext = null;
    }

    private sealed record PendingFiles(string TorrentId, long Context)
    {
        // Null when the inspector took the connection.
        internal TaskCompletionSource<JsonElement?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

internal sealed record FileDetail(JsonElement Files, JsonElement? Pieces);
