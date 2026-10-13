using System.ComponentModel;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using Syno.TinyTorrent.Helpers;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent.Library.Public;

internal sealed class Browser(ProviderHttp http) : IPageReader
{
    public bool IsAvailable => Supports(ShellAssociation.Get("https", ShellAssociation.Executable));

    public async Task<string> Read(Uri address, CancellationToken cancellation)
    {
        if (address.Scheme != Uri.UriSchemeHttps || address.UserInfo.Length != 0)
            throw new ArgumentException("A website request requires HTTPS.", nameof(address));
        CancellationTokenSource route;
        try
        {
            route = http.BeginDirect(cancellation);
        }
        catch (HttpRouteException error)
        {
            throw new RouteException("websites", error);
        }
        using (route)
        {
            try
            {
                var html = await Task.Run(() => Capture(address, route.Token), route.Token)
                    .ConfigureAwait(false);
                route.Token.ThrowIfCancellationRequested();
                return html;
            }
            catch (OperationCanceledException error)
                when (!cancellation.IsCancellationRequested && route.IsCancellationRequested)
            {
                throw new RouteException("websites", error);
            }
            catch (Exception error)
                when (error
                        is IOException
                            or UnauthorizedAccessException
                            or Win32Exception
                            or WebSocketException
                )
            {
                cancellation.ThrowIfCancellationRequested();
                throw route.IsCancellationRequested
                    ? new RouteException("websites", error)
                    : Provider.BrowserFailed(error);
            }
        }
    }

    internal static async Task<string> Capture(Uri address, CancellationToken cancellation)
    {
        var executable = ShellAssociation.Get("https", ShellAssociation.Executable);
        if (!Supports(executable))
            throw new ProviderException("websites", "browser_unavailable");

        var profile = Path.Combine(
            Path.GetTempPath(),
            "TinyTorrent",
            "Browser",
            Guid.NewGuid().ToString("N")
        );
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var command = new StringBuilder().Append('"').Append(executable).Append('"');
        // Headless Chrome and Edge name themselves HeadlessChrome in the user
        // agent, and IMDb answers that name with 403 Forbidden.
        var userAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/"
            + FileVersionInfo.GetVersionInfo(executable).ProductMajorPart
            + ".0.0.0 Safari/537.36";
        foreach (
            var argument in new[]
            {
                "--headless",
                "--no-first-run",
                "--no-default-browser-check",
                "--disable-background-networking",
                "--remote-debugging-port=0",
                "--user-agent=" + userAgent,
                "--user-data-dir=" + profile,
            }
        )
            command.Append(" \"").Append(argument).Append('"');

        SafeFileHandle? job = null;
        Process? process = null;
        try
        {
            timeout.Token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(profile);
            job = CreateJob();
            process = Start(executable, command, job);
            return await Download(address, profile, process, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new ProviderException("websites", "timed_out");
        }
        finally
        {
            // Closing the last handle to the job ends the browser and every
            // process it started. Windows does the same when TinyTorrent stops
            // without reaching this code, such as a crash.
            job?.Dispose();
            if (process is not null)
            {
                using (process)
                    await process.WaitForExitAsync().ConfigureAwait(false);
            }
            // Ending the job returns before the browser's processes release
            // their files in the profile.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    Directory.Delete(profile, recursive: true);
                    break;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    if (attempt == 20)
                    {
                        Debug.WriteLine("Browser profile cleanup: " + error.GetType().Name);
                        break;
                    }
                    await Task.Delay(100).ConfigureAwait(false);
                }
            }
        }
    }

    private static bool Supports(string executable) =>
        File.Exists(executable)
        && (
            Path.GetFileName(executable).Equals("msedge.exe", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(executable).Equals("chrome.exe", StringComparison.OrdinalIgnoreCase)
        );

    // Returns the main document's response body as soon as it has downloaded,
    // without waiting for the page's scripts, images and trackers.
    private static async Task<string> Download(
        Uri address,
        string profile,
        Process process,
        CancellationToken cancellation
    )
    {
        using var socket = new ClientWebSocket();
        // The debugging port is local; a system proxy must never carry it.
        socket.Options.Proxy = null;
        await socket
            .ConnectAsync(
                await Endpoint(profile, process, cancellation).ConfigureAwait(false),
                cancellation
            )
            .ConfigureAwait(false);
        var devtools = new Devtools(socket, cancellation);
        var target = await devtools
            .Call("Target.createTarget", new { url = "about:blank" })
            .ConfigureAwait(false);
        var attached = await devtools
            .Call(
                "Target.attachToTarget",
                new { targetId = target.GetProperty("targetId").GetString(), flatten = true }
            )
            .ConfigureAwait(false);
        devtools.Session = attached.GetProperty("sessionId").GetString();
        var tree = await devtools.Call("Page.getFrameTree").ConfigureAwait(false);
        var frame = tree.GetProperty("frameTree")
            .GetProperty("frame")
            .GetProperty("id")
            .GetString();
        await devtools.Call("Network.enable").ConfigureAwait(false);
        await devtools
            .Send("Page.navigate", new { url = address.AbsoluteUri })
            .ConfigureAwait(false);

        string? document = null;
        while (true)
        {
            using var message = await devtools.Receive().ConfigureAwait(false);
            var root = message.RootElement;
            if (root.TryGetProperty("id", out _))
            {
                // Page.navigate is the only command still unanswered.
                if (root.TryGetProperty("error", out _))
                    throw Provider.BrowserFailed();
                if (root.GetProperty("result").TryGetProperty("errorText", out var navigation))
                    throw Provider.Unreachable(navigation.GetString() ?? string.Empty);
                continue;
            }
            if (
                !root.TryGetProperty("params", out var parameters)
                || !parameters.TryGetProperty("requestId", out var request)
            )
                continue;
            switch (root.GetProperty("method").GetString())
            {
                case "Network.responseReceived"
                    when parameters.GetProperty("type").GetString() == "Document"
                        && parameters.TryGetProperty("frameId", out var owner)
                        && owner.GetString() == frame:
                    var response = parameters.GetProperty("response");
                    var status = response.GetProperty("status").GetInt32();
                    // IMDb's firewall answers a browser it scores as a bot
                    // with a picture puzzle that only a person can solve.
                    if (
                        response
                            .GetProperty("headers")
                            .EnumerateObject()
                            .Any(header =>
                                header.Name.Equals("x-amzn-waf-action", StringComparison.OrdinalIgnoreCase)
                                && header.Value.GetString() == "captcha"
                            )
                    )
                        throw new ProviderException("websites", "verification");
                    // IMDb answers its bot challenge with 202 and a script that
                    // reloads the real page, which arrives as the next Document.
                    if (status == 202)
                        document = null;
                    else if (status == 200)
                        document = request.GetString();
                    else
                        throw Provider.Failure(status);
                    break;
                case "Network.loadingFailed"
                    when document is not null && request.GetString() == document:
                    throw Provider.Unreachable(parameters.GetProperty("errorText").GetString() ?? string.Empty);
                case "Network.loadingFinished"
                    when document is not null && request.GetString() == document:
                    var reply = await devtools
                        .Call("Network.getResponseBody", new { requestId = document })
                        .ConfigureAwait(false);
                    var body = reply.GetProperty("body").GetString() ?? string.Empty;
                    if (reply.GetProperty("base64Encoded").GetBoolean())
                        body = Encoding.UTF8.GetString(Convert.FromBase64String(body));
                    return body.Length > Provider.PageLimit ? throw Provider.Invalid() : body;
            }
        }
    }

    // The browser writes the debugging port and the browser socket path into
    // its profile once it listens.
    private static async Task<Uri> Endpoint(
        string profile,
        Process process,
        CancellationToken cancellation
    )
    {
        var file = Path.Combine(profile, "DevToolsActivePort");
        while (!process.HasExited)
        {
            try
            {
                var lines = await File.ReadAllLinesAsync(file, cancellation).ConfigureAwait(false);
                if (lines.Length >= 2)
                    return new Uri("ws://127.0.0.1:" + lines[0] + lines[1]);
            }
            catch (IOException) { }
            await Task.Delay(20, cancellation).ConfigureAwait(false);
        }
        throw Provider.BrowserFailed();
    }

    private static SafeFileHandle CreateJob()
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            var error = new Win32Exception();
            job.Dispose();
            throw error;
        }
        const uint KillOnJobClose = 0x2000;
        const int ExtendedLimitInformation = 9;
        var limits = new JobLimits { LimitFlags = KillOnJobClose };
        if (!SetInformationJobObject(job, ExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<JobLimits>()))
        {
            var error = new Win32Exception();
            job.Dispose();
            throw error;
        }
        return job;
    }

    private static Process Start(string executable, StringBuilder command, SafeFileHandle job)
    {
        nuint size = 0;
        // The sizing call fails with ERROR_INSUFFICIENT_BUFFER by design.
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        if (size == 0)
            throw new Win32Exception();
        var attributes = Marshal.AllocHGlobal(checked((nint)size));
        var handles = IntPtr.Zero;
        var initialized = false;
        try
        {
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size))
                throw new Win32Exception();
            initialized = true;
            handles = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(handles, job.DangerousGetHandle());
            const nuint JobListAttribute = 0x2000d;
            if (
                !UpdateProcThreadAttribute(
                    attributes,
                    0,
                    JobListAttribute,
                    handles,
                    (nuint)IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero
                )
            )
                throw new Win32Exception();

            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Size = (uint)Marshal.SizeOf<StartupInfoEx>(),
                    Flags = 1, // STARTF_USESHOWWINDOW; ShowWindow is SW_HIDE.
                },
                AttributeList = attributes,
            };
            const uint CreateNoWindow = 0x08000000;
            const uint ExtendedStartupInfoPresent = 0x00080000;
            // Assign the job during creation: even a crash before this call
            // returns must terminate the browser and every descendant.
            if (
                !CreateProcessW(
                    executable,
                    command,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    CreateNoWindow | ExtendedStartupInfoPresent,
                    IntPtr.Zero,
                    null,
                    ref startup,
                    out var information
                )
            )
                throw new Win32Exception();
            try
            {
                var process = Process.GetProcessById(checked((int)information.ProcessId));
                // Retain a process handle before releasing the creation handle,
                // so an early exit cannot let this PID identify another process.
                _ = process.SafeHandle;
                return process;
            }
            catch (ArgumentException error)
            {
                throw Provider.BrowserFailed(error);
            }
            finally
            {
                CloseHandle(information.Thread);
                CloseHandle(information.Process);
            }
        }
        finally
        {
            if (initialized)
                DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(handles);
            Marshal.FreeHGlobal(attributes);
            GC.KeepAlive(job);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle job,
        int informationClass,
        ref JobLimits information,
        uint length
    );

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr attributes,
        uint count,
        uint flags,
        ref nuint size
    );

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr attributes,
        uint flags,
        nuint attribute,
        IntPtr value,
        nuint size,
        IntPtr previous,
        IntPtr returnedSize
    );

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr attributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string executable,
        StringBuilder command,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint flags,
        IntPtr environment,
        string? directory,
        ref StartupInfoEx startup,
        out ProcessInformation information
    );

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public uint Size;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort ReservedSize;
        public IntPtr ReservedBytes;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    // JOBOBJECT_EXTENDED_LIMIT_INFORMATION.
    [StructLayout(LayoutKind.Sequential)]
    private struct JobLimits
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    // A Chrome DevTools Protocol client. After Session is set, commands go to
    // the attached page instead of the browser.
    private sealed class Devtools(ClientWebSocket socket, CancellationToken cancellation)
    {
        // A body arrives base64 encoded or JSON escaped inside one message.
        private const int MessageLimit = 4 * Provider.PageLimit;
        private static readonly JsonSerializerOptions Options = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        private int _last;

        internal string? Session { get; set; }

        internal async Task<int> Send(string method, object? parameters = null)
        {
            var id = ++_last;
            var message = JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    id,
                    method,
                    @params = parameters,
                    sessionId = Session,
                },
                Options
            );
            await socket
                .SendAsync(message, WebSocketMessageType.Text, true, cancellation)
                .ConfigureAwait(false);
            return id;
        }

        internal async Task<JsonDocument> Receive()
        {
            using var message = new MemoryStream();
            Memory<byte> buffer = new byte[16 * 1024];
            ValueWebSocketReceiveResult part;
            do
            {
                part = await socket.ReceiveAsync(buffer, cancellation).ConfigureAwait(false);
                if (part.MessageType == WebSocketMessageType.Close)
                    throw Provider.BrowserFailed();
                if (message.Length + part.Count > MessageLimit)
                    throw Provider.Invalid();
                message.Write(buffer.Span[..part.Count]);
            } while (!part.EndOfMessage);
            message.Position = 0;
            return await JsonDocument
                .ParseAsync(message, cancellationToken: cancellation)
                .ConfigureAwait(false);
        }

        // Events that arrive before the reply are dropped.
        internal async Task<JsonElement> Call(string method, object? parameters = null)
        {
            var id = await Send(method, parameters).ConfigureAwait(false);
            while (true)
            {
                using var message = await Receive().ConfigureAwait(false);
                var root = message.RootElement;
                if (!root.TryGetProperty("id", out var reply) || reply.GetInt32() != id)
                    continue;
                if (root.TryGetProperty("error", out _))
                    throw Provider.BrowserFailed();
                return root.GetProperty("result").Clone();
            }
        }
    }
}
