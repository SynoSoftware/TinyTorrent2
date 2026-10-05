using System.Buffers.Binary;
using System.Diagnostics;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Syno.TinyTorrent;

internal sealed class PipeClient : IDisposable
{
    private const int MaximumFrame = 16 * 1024 * 1024;
    private const int CommandLimit = 32;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private readonly Strings _strings;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Queue<Command> _commands = new();
    // At most one unsent read per consumer, oldest first, so consumers take turns.
    private readonly List<Command> _reads = [];
    private readonly SemaphoreSlim _queued = new(0);
    private Task? _pump;
    private TaskCompletionSource<JsonElement>? _reply;
    private long _requestId;
    private long _awaitingId;
    private bool _connected;
    private bool _disposed;
    private bool _hasConnected;
    private string? _enginePath;
    private string? _dataDirectory;
    internal string? DataDirectory => _dataDirectory;

    internal static string LogonSid { get; } = ReadLogonSid();

    private static string ReadLogonSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        const int tokenLogonSid = 28;
        GetTokenInformation(identity.AccessToken, tokenLogonSid, IntPtr.Zero, 0, out var length);
        if (length == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        var buffer = Marshal.AllocHGlobal(checked((int)length));
        try
        {
            if (!GetTokenInformation(identity.AccessToken, tokenLogonSid, buffer, length, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var groups = Marshal.PtrToStructure<TokenGroups>(buffer);
            if (groups.Count != 1) throw new InvalidDataException("The token has no unique logon SID.");
            return new SecurityIdentifier(groups.Group.Sid).Value;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass,
        IntPtr information, uint length, out uint requiredLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAttributes
    {
        internal IntPtr Sid;
        internal uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenGroups
    {
        internal uint Count;
        internal SidAttributes Group;
    }

    internal event Action<JsonElement>? Snapshot;
    internal event Action<string>? Disconnected;
    internal event Action<string>? Control;

    internal PipeClient(Strings strings) => _strings = strings;

    internal void Start()
    {
        lock (_gate)
        {
            if (_disposed || _pump is not null) return;
            _pump = Task.Run(Run);
        }
    }

    internal Task<JsonElement> Send(string name, object? arguments = null)
    {
        lock (_gate)
        {
            if (_disposed || !_connected)
                return Task.FromException<JsonElement>(new IOException(_strings.Get("connection", "unavailable")));
            if (_commands.Count >= CommandLimit)
                return Task.FromException<JsonElement>(new IOException(_strings.Get("connection", "overload")));
            var command = new Command(name, arguments);
            _commands.Enqueue(command);
            _queued.Release();
            return command.Completion.Task;
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
                return Task.FromException<JsonElement>(new IOException(_strings.Get("connection", "unavailable")));
            if (_reads.Find(read => read.Consumer == consumer) is { } pending)
            {
                pending.Replace(name, arguments);
                return pending.Completion.Task;
            }
            var command = new Command(name, arguments, consumer);
            _reads.Add(command);
            _queued.Release();
            return command.Completion.Task;
        }
    }

    // Cancels the consumer's unsent read. A read already sent completes, and
    // its consumer ignores the outcome.
    internal void Withdraw(Consumer consumer)
    {
        lock (_gate)
        {
            if (_reads.Find(read => read.Consumer == consumer) is not { } pending) return;
            _reads.Remove(pending);
            pending.Completion.TrySetCanceled();
        }
    }

    private Command? Next()
    {
        lock (_gate)
        {
            if (_commands.TryDequeue(out var command)) return command;
            if (_reads.Count == 0) return null;
            var read = _reads[0];
            _reads.RemoveAt(0);
            return read;
        }
    }

    private async Task Run()
    {
        var launched = false;
        Exception? launchFailure = null;
        string? lastFailure = null;
        var token = _lifetime.Token;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var refresh = Refresh(timer, token);
        try
        {
            while (!token.IsCancellationRequested)
            {
                using var pipe = CreatePipe();
                try
                {
                    try { await pipe.ConnectAsync(1000, token); }
                    catch (TimeoutException) when (!launched && !_hasConnected)
                    {
                        launched = true;
                        try { LaunchEngine(); }
                        catch (Exception error) { launchFailure = error; throw; }
                        throw;
                    }
                    var hello = await ReadGreeting(pipe, _strings, token);
                    _ = hello.GetProperty("session_id").GetString() ?? throw new InvalidDataException();
                    _enginePath = hello.TryGetProperty("engine_path", out var engine) ? engine.GetString() : _enginePath;
                    _dataDirectory = hello.TryGetProperty("data_directory", out var directory) ? directory.GetString() : _dataDirectory;
                    _hasConnected = true;
                    lock (_gate)
                    {
                        token.ThrowIfCancellationRequested();
                        _connected = true;
                    }
                    launchFailure = null;
                    lastFailure = null;
                    await RunConnection(pipe, token);
                }
                catch (Exception error) when (!token.IsCancellationRequested)
                {
                    Disconnect(error);
                    var message = error is TimeoutException ? launchFailure?.Message ?? _strings.Get("window", "disconnected") : error.Message;
                    if (message != lastFailure) Disconnected?.Invoke(message);
                    lastFailure = message;
                    try { await Task.Delay(1000, token); } catch (OperationCanceledException) { }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (IOException) when (token.IsCancellationRequested) { break; }
            }
        }
        finally
        {
            _lifetime.Cancel();
            Disconnect();
            await refresh;
        }
    }

    private async Task RunConnection(NamedPipeClientStream pipe, CancellationToken token)
    {
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(token);
        var receive = Receive(pipe, connection.Token);
        try
        {
            await Execute(pipe, new Command("snapshot", null), connection.Token);
            while (true)
            {
                while (Next() is { } command) await Execute(pipe, command, connection.Token);
                var queued = _queued.WaitAsync(connection.Token);
                if (await Task.WhenAny(queued, receive) == receive) await receive;
                await queued;
            }
        }
        finally
        {
            connection.Cancel();
            pipe.Dispose();
            try { await receive; } catch (Exception) when (connection.IsCancellationRequested) { }
        }
    }

    private void Disconnect(Exception? error = null)
    {
        lock (_gate)
        {
            _connected = false;
            foreach (var command in _commands.Concat(_reads))
            {
                if (error is null) command.Completion.TrySetCanceled(_lifetime.Token);
                else command.Completion.TrySetException(new IOException(_strings.Get("connection", "unavailable"), error));
            }
            _commands.Clear();
            _reads.Clear();
        }
    }

    // Each refresh waits for the previous one, so a slow engine is not asked
    // again until it has answered.
    private async Task Refresh(PeriodicTimer timer, CancellationToken token)
    {
        Task refresh = Task.CompletedTask;
        try
        {
            while (await timer.WaitForNextTickAsync(token))
                if (refresh.IsCompleted) refresh = Read(Consumer.Summary, "snapshot");
        }
        catch (OperationCanceledException) { }
    }

    private async Task Execute(NamedPipeClientStream pipe, Command command, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(RequestTimeout);
        try
        {
            var requestId = Interlocked.Increment(ref _requestId);
            var fields = command.Arguments is null ? new Dictionary<string, JsonElement>() :
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(command.Arguments))!;
            fields["request_id"] = JsonSerializer.SerializeToElement(requestId);
            fields["command"] = JsonSerializer.SerializeToElement(command.Name);
            _reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _awaitingId = requestId;
            await Write(pipe, fields, deadline.Token);
            var reply = await _reply.Task.WaitAsync(deadline.Token);
            var data = ReadOutcome(reply, _strings, command.Name);
            if (command.Name == "snapshot") Snapshot?.Invoke(data);
            command.Completion.TrySetResult(data);
        }
        catch (CommandFailure error) { command.Completion.TrySetException(error); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            command.Completion.TrySetCanceled(token);
            throw;
        }
        catch (OperationCanceledException error) when (deadline.IsCancellationRequested)
        {
            var failure = new TimeoutException(_strings.Get("connection", "uncertain"), error);
            command.Completion.TrySetException(failure);
            throw failure;
        }
        catch (Exception error)
        {
            command.Completion.TrySetException(error);
            throw;
        }
        finally { _reply = null; }
    }

    private async Task Receive(NamedPipeClientStream pipe, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var message = await Read(pipe, token);
                if (message.TryGetProperty("type", out var type))
                {
                    if (type.GetString() is "activate" or "close" or "sources") Control?.Invoke(type.GetString()!);
                }
                else if (message.TryGetProperty("request_id", out var id) && id.GetInt64() == _awaitingId)
                    _reply?.TrySetResult(message);
            }
        }
        catch (Exception error)
        {
            _reply?.TrySetException(error);
            throw;
        }
    }

    private static NamedPipeClientStream CreatePipe() => new(".", "TinyTorrent." + LogonSid,
        PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);

    private static JsonElement ReadOutcome(JsonElement reply, Strings strings, string command)
    {
        if (!reply.GetProperty("ok").GetBoolean())
        {
            var error = reply.GetProperty("error");
            throw new CommandFailure(error.GetProperty("code").GetString()!,
                error.TryGetProperty("detail", out var detail) ? detail.GetString() : null, strings, command);
        }
        return reply.TryGetProperty("data", out var value) ? value : default;
    }

    private static async Task<JsonElement> ReadGreeting(Stream pipe, Strings strings, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(RequestTimeout);
        try
        {
            var hello = await Read(pipe, deadline.Token);
            if (hello.GetProperty("type").GetString() != "hello" || hello.GetProperty("version").GetInt32() != 1)
                throw new InvalidDataException(strings.Get("connection", "version"));
            return hello;
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException(strings.Get("connection", "unavailable"), error);
        }
    }

    internal static async Task ForwardOpen(Strings strings)
    {
        try
        {
            using var pipe = CreatePipe();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await pipe.ConnectAsync(timeout.Token);
            await ReadGreeting(pipe, strings, timeout.Token);
            await Write(pipe, new { request_id = 1, command = "open" }, timeout.Token);
            while (true)
            {
                var reply = await Read(pipe, timeout.Token);
                if (!reply.TryGetProperty("request_id", out var id) || id.GetInt64() != 1) continue;
                ReadOutcome(reply, strings, "open");
                return;
            }
        }
        catch (Exception error) when (error is OperationCanceledException or TimeoutException)
        {
            throw new IOException(strings.Get("connection", "unavailable"), error);
        }
    }

    internal void LaunchEngine()
    {
        if (_enginePath is not null)
        {
            var start = new ProcessStartInfo(_enginePath) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--background");
            if (_dataDirectory is not null)
            {
                start.ArgumentList.Add("--data");
                start.ArgumentList.Add(_dataDirectory);
            }
            Process.Start(start);
            return;
        }
        // The engine from the same build is always beside the window.
        var adjacent = Path.Combine(AppContext.BaseDirectory, "Engine.exe");
        if (!File.Exists(adjacent)) throw new FileNotFoundException(_strings.Get("connection", "missing"));
        Process.Start(new ProcessStartInfo(adjacent, "--background") { UseShellExecute = false, CreateNoWindow = true });
    }

    private static async Task<JsonElement> Read(Stream pipe, CancellationToken token)
    {
        var header = new byte[4];
        await pipe.ReadExactlyAsync(header, token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaximumFrame) throw new InvalidDataException("Invalid pipe frame length.");
        var bytes = new byte[length];
        await pipe.ReadExactlyAsync(bytes, token);
        using var document = JsonDocument.Parse(bytes);
        return document.RootElement.Clone();
    }

    private static async Task Write(Stream pipe, object message, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        if (bytes.Length > MaximumFrame) throw new InvalidDataException("Pipe frame exceeds the limit.");
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
            if (_disposed) return;
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
            if (_pump is { } pump) await pump.ConfigureAwait(false);
        }
        catch (Exception error) { Debug.WriteLine($"Pipe client stopped: {error.Message}"); }
        finally { _lifetime.Dispose(); }
    }

    private sealed class Command(string name, object? arguments, Consumer? consumer = null)
    {
        internal string Name { get; private set; } = name;
        internal object? Arguments { get; private set; } = arguments;
        internal Consumer? Consumer { get; } = consumer;
        internal TaskCompletionSource<JsonElement> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Replace(string name, object? arguments)
        {
            Name = name;
            Arguments = arguments;
        }
    }
}

internal sealed class CommandFailure(string code, string? detail, Strings strings, string? command = null) : Exception
{
    public string? Command { get; } = command;
    public override string Message => strings.Error(code, detail);
}
