using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using Syno.TinyTorrent.Shell;

namespace Syno.TinyTorrent.Shared.Engine;

internal sealed partial class PipeClient : IDisposable
{
    private const int MaximumFrame = 16 * 1024 * 1024;

    // Together these fit the engine pipe's 64 outstanding requests per connection.
    private const int CommandLimit = 32;
    private const int ReadLimit = 32;
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private readonly Strings _strings;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Queue<Request> _commands = new();

    // At most one unsent read per consumer, oldest first, so consumers take turns.
    private readonly List<Request> _reads = [];
    private readonly SemaphoreSlim _queued = new(0);
    private readonly Dictionary<long, PendingReply> _replies = [];
    private Task? _pump;
    private int _commandCount;
    private long _requestId;
    private bool _connected;
    private int? _processId;
    private bool _disposed;
    private bool _hasConnected;
    private string? _dataDirectory;
    internal string? DataDirectory => _dataDirectory;
    internal bool IsConnected
    {
        get
        {
            lock (_gate)
                return _connected;
        }
    }
    internal int? ProcessId
    {
        get
        {
            lock (_gate)
                return _processId;
        }
    }

    internal event Action<JsonElement>? Snapshot;
    internal event Action<Disconnection>? Disconnected;
    internal event Action<string>? Control;

    // The engine names the Settings card to open.
    internal event Action<string>? SettingsRequested;
    internal event Action<string>? TorrentRequested;
    internal event Action<JsonElement>? Toast;
    internal event Action<JsonElement>? Detail;
    internal event Action<CommandReply>? CommandReplied;

    internal PipeClient(Strings strings) => _strings = strings;

    internal void Start()
    {
        lock (_gate)
        {
            if (_disposed || _pump is not null)
                return;
            _pump = Task.Run(Run);
        }
    }

    internal Task<JsonElement> Send(string name, object? arguments, Requester requester)
    {
        lock (_gate)
        {
            if (_disposed || !_connected)
                return Task.FromException<JsonElement>(
                    new IOException(_strings.Get("connection", "unavailable"))
                );
            if (_commandCount >= CommandLimit)
                return Task.FromException<JsonElement>(
                    new IOException(_strings.Get("connection", "overload"))
                );
            var request = new Request(name, arguments, requester);
            _commands.Enqueue(request);
            _commandCount++;
            _queued.Release();
            return request.Completion.Task;
        }
    }

    // A read that only refreshes a view. It waits behind commands, and a newer
    // read from the same consumer replaces it until it is sent; both callers
    // then receive the newer outcome.
    internal Task<JsonElement> Read(Consumer consumer, string name, object? arguments = null)
    {
        lock (_gate)
        {
            if (_disposed || !_connected)
                return Task.FromException<JsonElement>(
                    new IOException(_strings.Get("connection", "unavailable"))
                );
            if (_reads.Find(read => read.Consumer == consumer) is { } pending)
            {
                pending.Replace(name, arguments);
                return pending.Completion.Task;
            }
            var request = new Request(name, arguments, consumer);
            _reads.Add(request);
            _queued.Release();
            return request.Completion.Task;
        }
    }

    // Cancels the consumer's unsent read. A read already sent completes, and
    // its consumer ignores the outcome.
    internal void Withdraw(Consumer consumer)
    {
        lock (_gate)
        {
            if (_reads.Find(read => read.Consumer == consumer) is not { } pending)
                return;
            _reads.Remove(pending);
            pending.Completion.TrySetCanceled();
            Debug.WriteLine(
                $"Unsent engine read '{pending.Name}' cancelled ({consumer}): withdrawn."
            );
        }
    }

    private Request? Next(IEnumerable<Consumer?> active)
    {
        lock (_gate)
        {
            if (_commands.TryDequeue(out var command))
                return command;
            if (active.Count(consumer => consumer is not null) >= ReadLimit)
                return null;
            var index = _reads.FindIndex(read =>
                read.Consumer != Consumer.Files
                && (read.Consumer != Consumer.Summary || !active.Contains(Consumer.Summary))
            );
            if (index < 0)
                index = _reads.FindIndex(read => read.Consumer == Consumer.Files);
            if (index < 0)
                return null;
            var read = _reads[index];
            _reads.RemoveAt(index);
            return read;
        }
    }

    // How long an engine this window started may take to accept the pipe; until
    // then a timeout is a cold start, not a failure that reveals the window.
    private static readonly TimeSpan StartGrace = TimeSpan.FromSeconds(5);

    private async Task Run()
    {
        long? launchedAt = null;
        Exception? launchFailure = null;
        string? lastFailure = null;
        var token = _lifetime.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                using var pipe = CreatePipe();
                try
                {
                    // Without a running engine, waiting for its pipe only
                    // delays the start.
                    if (launchedAt is null && !_hasConnected && !EngineProcess.IsRunning())
                        Launch();
                    // An engine this window started is awaited for the rest of
                    // its grace, so the window connects once the pipe exists.
                    var timeout = 1000;
                    if (launchedAt is { } started && !_hasConnected)
                        timeout = Math.Max(
                            timeout,
                            (int)(
                                StartGrace.TotalMilliseconds - (Environment.TickCount64 - started)
                            )
                        );
                    try
                    {
                        await pipe.ConnectAsync(timeout, token);
                    }
                    catch (TimeoutException) when (launchedAt is null && !_hasConnected)
                    {
                        Launch();
                        throw;
                    }
                    var hello = await ReadGreeting(pipe, _strings, token);
                    var store = hello.TryGetProperty("data_directory", out var directory)
                        ? directory.GetString()
                        : null;
#if DEBUG
                    // Preserve this engine's store before replacing its process.
                    // Another build's engine stops before its version is
                    // checked, because a different version is the usual case.
                    if (!_hasConnected && EngineProcess.StopOther(pipe))
                    {
                        _dataDirectory = store ?? _dataDirectory;
                        continue;
                    }
#endif
                    CheckVersion(pipe, hello, _strings);
                    _ =
                        hello.GetProperty("session_id").GetString()
                        ?? throw new InvalidDataException();
                    _dataDirectory = store ?? _dataDirectory;
                    _hasConnected = true;
                    lock (_gate)
                    {
                        token.ThrowIfCancellationRequested();
                        _processId = EngineProcess.ProcessId(pipe);
                        _connected = true;
                    }
                    launchFailure = null;
                    lastFailure = null;
                    await RunConnection(pipe, token);
                }
                catch (Exception error) when (!token.IsCancellationRequested)
                {
                    Disconnect(error);
                    var starting =
                        error is TimeoutException
                        && launchFailure is null
                        && launchedAt is { } launched
                        && Environment.TickCount64 - launched < StartGrace.TotalMilliseconds;
                    if (!starting)
                    {
                        // A timeout adds nothing to the window's own disconnected message.
                        var message =
                            error is TimeoutException
                                ? launchFailure?.Message ?? string.Empty
                                : error.Message;
                        if (message != lastFailure)
                            Disconnected?.Invoke(new(message, error is VersionException));
                        lastFailure = message;
                    }
                    try
                    {
                        await Task.Delay(1000, token);
                    }
                    catch (OperationCanceledException)
                    {
                        Debug.WriteLine(
                            "Engine reconnect wait cancelled: pipe client is stopping."
                        );
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    Debug.WriteLine("Engine connection loop cancelled: pipe client is stopping.");
                    break;
                }
                catch (IOException) when (token.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            _lifetime.Cancel();
            Disconnect();
        }

        void Launch()
        {
            launchedAt = Environment.TickCount64;
            try
            {
                EngineProcess.Launch(_strings, _dataDirectory);
            }
            catch (Exception error)
            {
                launchFailure = error;
                throw;
            }
        }
    }

    private async Task RunConnection(NamedPipeClientStream pipe, CancellationToken token)
    {
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(token);
        var receive = Receive(pipe, connection.Token);
        List<(Request Request, Task Task)> active = [];
        try
        {
            await Execute(
                pipe,
                new Request("snapshot", new { subscribe = true }, Consumer.Summary),
                connection.Token
            );
            var queued = _queued.WaitAsync(connection.Token);
            while (!connection.IsCancellationRequested)
            {
                if (receive.IsCompleted)
                    await receive;
                foreach (var completed in active.Where(item => item.Task.IsCompleted).ToArray())
                {
                    await completed.Task;
                    active.Remove(completed);
                }
                if (queued.IsCompleted)
                {
                    await queued;
                    queued = _queued.WaitAsync(connection.Token);
                }
                var request = Next(active.Select(item => item.Request.Consumer));
                if (request is not null)
                {
                    active.Add((request, Execute(pipe, request, connection.Token)));
                    await request.Written.Task;
                    continue;
                }
                await Task.WhenAny(active.Select(item => item.Task).Append(queued).Append(receive));
            }
        }
        finally
        {
            connection.Cancel();
            pipe.Dispose();
            await Task.WhenAll(active.Select(item => item.Task).Append(receive))
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private void Disconnect(Exception? error = null)
    {
        lock (_gate)
        {
            _connected = false;
            _processId = null;
            foreach (var request in _commands.Concat(_reads))
            {
                if (error is null)
                {
                    request.Completion.TrySetCanceled(_lifetime.Token);
                    Debug.WriteLine(
                        $"Queued engine request '{request.Name}' cancelled: connection closed."
                    );
                }
                else
                    request.Completion.TrySetException(
                        new IOException(_strings.Get("connection", "unavailable"), error)
                    );
            }
            _commandCount -= _commands.Count;
            _commands.Clear();
            _reads.Clear();
            EndFiles();
            EndInspector();
        }
    }

    internal async Task RefreshSnapshot()
    {
        Task read;
        lock (_gate)
        {
            if (_disposed || !_connected)
                return;
            read = Read(Consumer.Summary, "snapshot");
        }
        // The connection loop reports failures from these optional refreshes.
        await read.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private async Task Execute(NamedPipeClientStream pipe, Request request, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(RequestTimeout);
        var requestId = Interlocked.Increment(ref _requestId);
        try
        {
            var arguments = JsonSerializer.SerializeToElement(request.Arguments);
            var fields = request.Arguments is null
                ? new Dictionary<string, JsonElement>()
                : arguments
                    .EnumerateObject()
                    .ToDictionary(property => property.Name, property => property.Value);
            long? context = fields.TryGetValue("context", out var value) ? value.GetInt64() : null;
            if (!BeginDetail(request, context))
                return;
            fields["request_id"] = JsonSerializer.SerializeToElement(requestId);
            fields["command"] = JsonSerializer.SerializeToElement(request.Name);
            var pending = new PendingReply(request, arguments);
            lock (_gate)
                _replies.Add(requestId, pending);
            await Write(pipe, fields, deadline.Token);
            request.Written.TrySetResult();
            var reply = await pending.Completion.Task.WaitAsync(deadline.Token);
            var data = ReadOutcome(reply, _strings, request.Name);
            CompleteDetail(request, data);
            request.Completion.TrySetResult(data);
        }
        catch (CommandException error)
        {
            CompleteDetail(request, default);
            request.Completion.TrySetException(error);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Debug.WriteLine($"Engine request '{request.Name}' cancelled: connection closed.");
            request.Completion.TrySetCanceled(token);
        }
        catch (OperationCanceledException error) when (deadline.IsCancellationRequested)
        {
            Debug.WriteLine(
                $"Engine request '{request.Name}' timed out after {RequestTimeout.TotalSeconds} seconds."
            );
            var failure = new TimeoutException(_strings.Get("connection", "uncertain"), error);
            request.Completion.TrySetException(failure);
            throw failure;
        }
        catch (Exception error)
        {
            request.Completion.TrySetException(error);
            throw;
        }
        finally
        {
            request.Written.TrySetResult();
            lock (_gate)
            {
                _replies.Remove(requestId);
                if (request.Consumer is null)
                    _commandCount--;
            }
        }
    }

    private async Task Receive(NamedPipeClientStream pipe, CancellationToken token)
    {
        JsonElement? snapshot = null;
        try
        {
            while (true)
            {
                var message = await Read(pipe, token);
                if (message.TryGetProperty("type", out var type))
                {
                    if (
                        type.GetString() is "activate" or "close" or "confirm_exit" or "activations"
                    )
                        Control?.Invoke(type.GetString()!);
                    else if (type.GetString() == "settings")
                        SettingsRequested?.Invoke(message.GetProperty("setting").GetString()!);
                    else if (type.GetString() == "torrent")
                        TorrentRequested?.Invoke(message.GetProperty("torrent_id").GetString()!);
                    else if (type.GetString() == "toast")
                        Toast?.Invoke(message);
                    else if (type.GetString() == "detail")
                        ReceiveDetail(message);
                    else if (type.GetString() == "snapshot")
                        Publish(ReadOutcome(message, _strings, "snapshot"));
                }
                else if (message.TryGetProperty("request_id", out var id))
                {
                    PendingReply? pending;
                    lock (_gate)
                        _replies.TryGetValue(id.GetInt64(), out pending);
                    if (pending is null)
                        continue;
                    // Publish snapshots in receive order, before later save
                    // continuations can confirm newer settings on the UI thread.
                    if (
                        pending.Request.Name == "snapshot"
                        && message.GetProperty("ok").GetBoolean()
                    )
                        Publish(message.GetProperty("data"));
                    if (pending.Request.Requester is { } requester)
                        CommandReplied?.Invoke(Reply(pending, requester, message));
                    pending.Completion.TrySetResult(message);
                }
            }
        }
        catch (Exception error)
            when (token.IsCancellationRequested
                && error is OperationCanceledException or IOException or ObjectDisposedException
            )
        {
            Debug.WriteLine("Engine message receive cancelled: connection closed.");
            lock (_gate)
                foreach (var pending in _replies.Values)
                    pending.Completion.TrySetCanceled(token);
        }
        catch (Exception error)
        {
            lock (_gate)
                foreach (var pending in _replies.Values)
                    pending.Completion.TrySetException(error);
            throw;
        }

        void Publish(JsonElement data)
        {
            if (
                snapshot is { } latest
                && data.GetProperty("sequence").GetInt64()
                    <= latest.GetProperty("sequence").GetInt64()
            )
                return;
            snapshot = data;
            Snapshot?.Invoke(data);
        }

        CommandReply Reply(PendingReply pending, Requester requester, JsonElement message)
        {
            var succeeded = message.GetProperty("ok").GetBoolean();
            JsonElement? confirmed = null;
            if (
                succeeded
                && message.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("snapshot_sequence", out var sequence)
                && snapshot is { } latest
                && latest.GetProperty("sequence").GetInt64() >= sequence.GetInt64()
            )
                confirmed = latest;
            return new(
                pending.Request.Name,
                pending.Arguments,
                requester,
                succeeded,
                confirmed
            );
        }
    }

    internal static NamedPipeClientStream CreatePipe() =>
        new(
            ".",
            "TinyTorrent." + EngineProcess.LogonSid,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification
        );

    internal static JsonElement ReadOutcome(JsonElement reply, Strings strings, string command)
    {
        if (!reply.GetProperty("ok").GetBoolean())
        {
            var error = reply.GetProperty("error");
            throw new CommandException(
                error.GetProperty("code").GetString()!,
                error.TryGetProperty("detail", out var detail) ? detail.GetString() : null,
                strings,
                command
            );
        }
        return reply.TryGetProperty("data", out var value) ? value : default;
    }

    private static async Task<JsonElement> ReadGreeting(
        Stream pipe,
        Strings strings,
        CancellationToken token
    )
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(RequestTimeout);
        try
        {
            var hello = await Read(pipe, deadline.Token);
            if (hello.GetProperty("type").GetString() != "hello")
                throw new InvalidDataException(strings.Get("connection", "unavailable"));
            return hello;
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested)
        {
            Debug.WriteLine("Engine greeting wait timed out.");
            throw new TimeoutException(strings.Get("connection", "unavailable"), error);
        }
    }

    private static void CheckVersion(NamedPipeClientStream pipe, JsonElement hello, Strings strings)
    {
        if (hello.TryGetProperty("version", out var version) && version.GetInt32() == 29)
            return;
        throw new VersionException(EngineProcess.VersionMessage(pipe, strings));
    }

    internal static async Task ForwardOpen(Strings strings)
    {
        try
        {
            using var pipe = CreatePipe();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await pipe.ConnectAsync(timeout.Token);
            CheckVersion(pipe, await ReadGreeting(pipe, strings, timeout.Token), strings);
            await Write(pipe, new { request_id = 1, command = "open" }, timeout.Token);
            while (true)
            {
                var reply = await Read(pipe, timeout.Token);
                if (!reply.TryGetProperty("request_id", out var id) || id.GetInt64() != 1)
                    continue;
                ReadOutcome(reply, strings, "open");
                return;
            }
        }
        catch (Exception error) when (error is OperationCanceledException or TimeoutException)
        {
            Debug.WriteLine("Forwarding the open request to the engine timed out.");
            throw new IOException(strings.Get("connection", "unavailable"), error);
        }
    }

    private static async Task<JsonElement> Read(Stream pipe, CancellationToken token)
    {
        var header = new byte[4];
        await pipe.ReadExactlyAsync(header, token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaximumFrame)
            throw new InvalidDataException("Invalid pipe frame length.");
        var bytes = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            var message = bytes.AsMemory(0, length);
            await pipe.ReadExactlyAsync(message, token);
            using var document = JsonDocument.Parse(message);
            return document.RootElement.Clone();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    private static async Task Write(Stream pipe, object message, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        if (bytes.Length > MaximumFrame)
            throw new InvalidDataException("Pipe frame exceeds the limit.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await pipe.WriteAsync(header, token);
        await pipe.WriteAsync(bytes, token);
        await pipe.FlushAsync(token);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _connected = false;
            _lifetime.Cancel();
        }
        Disconnect();
        _ = Stop();
    }

    private async Task Stop()
    {
        try
        {
            if (_pump is { } pump)
                await pump.ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Debug.WriteLine($"Pipe client stopped: {error.Message}");
        }
        finally
        {
            _lifetime.Dispose();
        }
    }

    // Why the window has no engine. An engine of another version keeps running,
    // so restarting the window's own engine cannot replace it.
    internal readonly record struct Disconnection(string Reason, bool IsOtherVersion);

    // Snapshot is the received snapshot that already holds a successful
    // command's effect. It is null when that snapshot arrives later, through
    // the Snapshot event, or when the command publishes none.
    internal readonly record struct CommandReply(
        string Command,
        JsonElement Arguments,
        Requester Requester,
        bool Succeeded,
        JsonElement? Snapshot
    );

    private sealed class VersionException(string message) : Exception(message);

    private sealed record PendingReply(Request Request, JsonElement Arguments)
    {
        internal TaskCompletionSource<JsonElement> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // A command names who requested it, and a read names the view it
    // refreshes; the other of the two is null.
    private sealed class Request
    {
        internal Request(string name, object? arguments, Requester requester)
        {
            Name = name;
            Arguments = arguments;
            Requester = requester;
        }

        internal Request(string name, object? arguments, Consumer consumer)
        {
            Name = name;
            Arguments = arguments;
            Consumer = consumer;
        }

        internal string Name { get; private set; }
        internal object? Arguments { get; private set; }
        internal Consumer? Consumer { get; }
        internal Requester? Requester { get; }
        internal TaskCompletionSource Written { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<JsonElement> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Replace(string name, object? arguments)
        {
            Name = name;
            Arguments = arguments;
        }
    }
}

internal sealed class CommandException(
    string code,
    string? detail,
    Strings strings,
    string? command = null
) : Exception
{
    public string Code { get; } = code;
    public string? Command { get; } = command;
    public override string Message => strings.Error(Code, detail);
}
