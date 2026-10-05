using System.Buffers.Binary;
using System.Diagnostics;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

namespace Syno.TinyTorrent;

internal sealed class PipeClient : IDisposable
{
    private const int MaximumFrame = 16 * 1024 * 1024;
    private readonly Strings _strings;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<Command> _commands = Channel.CreateBounded<Command>(32);
    private TaskCompletionSource<JsonElement>? _reply;
    private long _requestId;
    private long _awaitingId;
    private int _refreshPending;
    private volatile bool _connected;
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

    internal void Start() => _ = Task.Run(Run);

    internal Task<JsonElement> Send(string name, object? arguments = null)
    {
        if (!_connected)
            return Task.FromException<JsonElement>(new IOException(_strings.Get("connection", "unavailable")));
        var command = new Command(name, arguments);
        if (!_commands.Writer.TryWrite(command))
            command.Completion.SetException(new IOException(_strings.Get("connection", "overload")));
        return command.Completion.Task;
    }

    private async Task Run()
    {
        var launched = false;
        Exception? launchFailure = null;
        string? lastFailure = null;
        var token = _lifetime.Token;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        _ = Refresh(timer, token);
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
                _connected = true;
                launchFailure = null;
                lastFailure = null;
                using var connection = CancellationTokenSource.CreateLinkedTokenSource(token);
                var receive = Receive(pipe, connection.Token);
                try
                {
                    await Execute(pipe, new Command("snapshot", null), connection.Token);
                    while (!connection.IsCancellationRequested)
                    {
                        var waiting = _commands.Reader.WaitToReadAsync(connection.Token).AsTask();
                        if (await Task.WhenAny(waiting, receive) == receive)
                            await receive;
                        if (!await waiting) break;
                        while (_commands.Reader.TryRead(out var command))
                        {
                            if (command.Optional && _commands.Reader.Count > 0)
                            {
                                Interlocked.Exchange(ref _refreshPending, 0);
                                command.Completion.TrySetResult(default);
                                continue;
                            }
                            await Execute(pipe, command, connection.Token);
                        }
                    }
                }
                finally
                {
                    connection.Cancel();
                    pipe.Dispose();
                    try { await receive; } catch (Exception) when (connection.IsCancellationRequested) { }
                }
            }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                _connected = false;
                _reply?.TrySetException(error);
                while (_commands.Reader.TryRead(out var command))
                    command.Completion.TrySetException(new IOException(_strings.Get("connection", "uncertain"), error));
                Interlocked.Exchange(ref _refreshPending, 0);
                var message = error is TimeoutException ? launchFailure?.Message ?? _strings.Get("window", "disconnected") : error.Message;
                if (message != lastFailure) Disconnected?.Invoke(message);
                lastFailure = message;
                try { await Task.Delay(1000, token); } catch (OperationCanceledException) { }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (IOException) when (token.IsCancellationRequested) { break; }
        }
    }

    private async Task Refresh(PeriodicTimer timer, CancellationToken token)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                if (!_connected || _commands.Reader.Count != 0 || Interlocked.CompareExchange(ref _refreshPending, 1, 0) != 0)
                    continue;
                var refresh = new Command("snapshot", null, true);
                if (!_commands.Writer.TryWrite(refresh)) Interlocked.Exchange(ref _refreshPending, 0);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task Execute(NamedPipeClientStream pipe, Command command, CancellationToken token)
    {
        try
        {
            var requestId = Interlocked.Increment(ref _requestId);
            var fields = command.Arguments is null ? new Dictionary<string, JsonElement>() :
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(command.Arguments))!;
            fields["request_id"] = JsonSerializer.SerializeToElement(requestId);
            fields["command"] = JsonSerializer.SerializeToElement(command.Name);
            _reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _awaitingId = requestId;
            await Write(pipe, fields, token);
            var reply = await _reply.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
            var data = ReadOutcome(reply, _strings, command.Name);
            if (command.Name == "snapshot") Snapshot?.Invoke(data);
            command.Completion.TrySetResult(data);
        }
        catch (CommandFailure error) { command.Completion.TrySetException(error); }
        catch (Exception error)
        {
            command.Completion.TrySetException(error);
            throw;
        }
        finally
        {
            _reply = null;
            if (command.Name == "snapshot") Interlocked.Exchange(ref _refreshPending, 0);
        }
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
        var hello = await Read(pipe, token);
        if (hello.GetProperty("type").GetString() != "hello" || hello.GetProperty("version").GetInt32() != 1)
            throw new InvalidDataException(strings.Get("connection", "version"));
        return hello;
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
        var adjacent = Path.Combine(AppContext.BaseDirectory, "Engine.exe");
        if (File.Exists(adjacent))
        {
            Process.Start(new ProcessStartInfo(adjacent, "--background") { UseShellExecute = false, CreateNoWindow = true });
            return;
        }
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            var path = Path.Combine(folder.FullName, "artifacts", "bin", "Engine", "Release", "Engine.exe");
            if (!File.Exists(path)) continue;
            Process.Start(new ProcessStartInfo(path, "--background") { UseShellExecute = false, CreateNoWindow = true });
            return;
        }
        throw new FileNotFoundException(_strings.Get("connection", "missing"));
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
        _connected = false;
        _lifetime.Cancel();
        _commands.Writer.TryComplete();
        _lifetime.Dispose();
    }

    private sealed class Command(string name, object? arguments, bool optional = false)
    {
        internal string Name { get; } = name;
        internal object? Arguments { get; } = arguments;
        internal bool Optional { get; } = optional;
        internal TaskCompletionSource<JsonElement> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

internal sealed class CommandFailure(string code, string? detail, Strings strings, string? command = null) : Exception
{
    public string? Command { get; } = command;
    public override string Message => strings.Error(code, detail);
}
