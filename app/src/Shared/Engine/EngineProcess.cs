using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Syno.TinyTorrent.Shared.Engine;

internal static class EngineProcess
{
    // TinyTorrent.csproj records the engine's file name from the shared build properties.
    private static readonly string FileName = typeof(EngineProcess)
        .Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(metadata => metadata.Key == "EngineFile")
        .Value!;

    private static string Adjacent => Path.Combine(AppContext.BaseDirectory, FileName);

    internal static string LogonSid { get; } = ReadLogonSid();

    private static string ReadLogonSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        const int tokenLogonSid = 28;
        GetTokenInformation(identity.AccessToken, tokenLogonSid, IntPtr.Zero, 0, out var length);
        if (length == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var buffer = Marshal.AllocHGlobal(checked((int)length));
        try
        {
            if (!GetTokenInformation(identity.AccessToken, tokenLogonSid, buffer, length, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var groups = Marshal.PtrToStructure<TokenGroups>(buffer);
            if (groups.Count != 1)
                throw new InvalidDataException("The token has no unique logon SID.");
            return new SecurityIdentifier(groups.Group.Sid).Value;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        SafeAccessTokenHandle token,
        int informationClass,
        IntPtr information,
        uint length,
        out uint requiredLength
    );

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

    // Every engine process opens this mutex, so when it does not exist no
    // engine is running.
    internal static bool IsRunning()
    {
        if (!Mutex.TryOpenExisting("Local\\TinyTorrent.Engine." + LogonSid, out var mutex))
            return false;
        mutex.Dispose();
        return true;
    }

    internal static int? ProcessId(NamedPipeClientStream pipe) =>
        GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var id) ? (int)id : null;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    private static bool IsAdjacent(string path) =>
        string.Equals(path, Adjacent, StringComparison.OrdinalIgnoreCase);

    // The executable of the engine serving `pipe`, compared as launched, which
    // matches because the window starts the engine beside it. Windows hides the
    // path of an engine running as administrator.
    private static string? Executable(NamedPipeClientStream pipe)
    {
        if (ProcessId(pipe) is not { } id)
            return null;
        try
        {
            using var engine = Process.GetProcessById(id);
            return engine.MainModule?.FileName;
        }
        catch (Exception error)
            when (error is ArgumentException or Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    // Another version stays unusable however often the window retries, so the
    // message says where that engine runs: another folder's engine exits from
    // the tray, while a folder holding both versions needs reinstalling.
    internal static string VersionMessage(NamedPipeClientStream pipe, Strings strings) =>
        Executable(pipe) switch
        {
            null => strings.Get("connection", "version_hidden"),
            { } path when IsAdjacent(path) => strings.Get("connection", "version"),
            { } path => strings.Format(
                "connection",
                "version_elsewhere",
                Path.GetDirectoryName(path)!
            ),
        };

    // A restart keeps `store`, the data directory the engine last reported.
    internal static void Launch(Strings strings, string? store)
    {
        if (store is null)
            Start(strings, "--background");
        else
            Start(strings, "--background", "--data", store);
    }

    // Starts the engine from the same build, which is always beside the window,
    // rather than a path a pipe peer reported.
    internal static void Start(Strings strings, params string[] arguments)
    {
        if (!File.Exists(Adjacent))
            throw new FileNotFoundException(strings.Format("connection", "missing", FileName));
        var start = new ProcessStartInfo(Adjacent)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        Process.Start(start);
    }

#if DEBUG
    // Every engine in a logon session serves this pipe, so a development window
    // would use another build's engine. Stops the engine that owns the pipe
    // unless it runs from beside this window, so the window starts its own. A
    // running executable is locked and the build stops that engine before
    // replacing it, so its path identifies this build; identical bytes do not,
    // because the capture output's copy may serve a review's disposable store.
    internal static bool StopOther(NamedPipeClientStream pipe)
    {
        // An engine running as administrator hides its path, so it keeps running.
        if (Executable(pipe) is not { } path || IsAdjacent(path) || ProcessId(pipe) is not { } id)
            return false;
        try
        {
            using var engine = Process.GetProcessById(id);
            engine.Kill();
            return engine.WaitForExit(5000);
        }
        catch (Exception error)
            when (error is ArgumentException or Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    // For a window that hands off to the engine and never connects itself.
    internal static async Task StopOther()
    {
        using var pipe = PipeClient.CreatePipe();
        try
        {
            await pipe.ConnectAsync(1000);
        }
        catch (TimeoutException)
        {
            return;
        }
        StopOther(pipe);
    }
#endif

    // Runs the engine beside the window as an administrator to remove the
    // all-users open commands of `classes` that start a missing program. A
    // declined prompt, a failed start or a failed repair all leave those
    // handlers reported, which is the person's evidence that they remain.
    internal static async Task RepairMachine(IEnumerable<string> classes)
    {
        var start = new ProcessStartInfo(Adjacent) { UseShellExecute = true, Verb = "runas" };
        start.ArgumentList.Add("--headless");
        foreach (var progId in classes)
        {
            start.ArgumentList.Add("--repair-class");
            start.ArgumentList.Add(progId);
        }
        try
        {
            using var process = Process.Start(start);
            if (process is not null)
                await process.WaitForExitAsync();
        }
        catch (Win32Exception) { }
    }
}
