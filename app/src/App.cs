using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shared.Diagnostics;
using Syno.TinyTorrent.Shared.Engine;
using Syno.TinyTorrent.Shell;
using Supplier = Syno.TinyTorrent.Subtitles.Supplier;

namespace Syno.TinyTorrent;

public partial class App : Application
{
    static App()
    {
        // SEM_NOGPFAULTERRORBOX prevents Windows Error Reporting on a crash.
        SetErrorMode(GetErrorMode() | 0x0002);
    }

    internal static Version Version { get; } =
        typeof(App).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);
    internal static string UserAgent { get; } =
        "TinyTorrent/" + Version + " (+https://github.com/SynoSoftware/TinyTorrent2)";
    private Mutex? _instance;
    private MainWindow? _window;

    // Fatal CLR callbacks may run off the UI thread.
    private IntPtr _windowHandle;
    private Strings? _strings;
    private int _reportingFailure;
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TinyTorrent"
    );

    public App()
    {
        // Unknown UI failures cannot safely be marked as handled.
        UnhandledException += (_, args) => ReportFatal(args.Exception, "WinUI", args.Message);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            ReportFatal(
                args.ExceptionObject as Exception
                    ?? new Exception("Unhandled non-Exception object."),
                ".NET"
            );
        TaskScheduler.UnobservedTaskException += (_, args) =>
            ExceptionLog.Write(LogDirectory, args.Exception, "Unobserved task");
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        Resources["LucideFont"] = Syno.Lucide.Font;
        Resources["FlagsFont"] = Syno.Flags.Font;
        Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID("Syno.TinyTorrent"));
        // The engine owns startup isolation; only its window starts here.
        var fromEngine = Environment.GetCommandLineArgs().Contains("--engine");
#if CAPTURE
        // The review script waits for the window it started.
        fromEngine |= MainWindow.IsCaptureReview;
#endif
        if (!fromEngine)
        {
            var text = await Task.Run(() => new Strings());
#if DEBUG
            // The engine started here would pass the window to another build's
            // engine, so that build stops first, as for a window started here.
            await Task.Run(StopOtherWindow);
            await PipeClient.StopOtherEngine();
#endif
            try
            {
                PipeClient.StartEngine(text);
            }
            catch (Exception error) when (error is FileNotFoundException or Win32Exception)
            {
                MessageBoxW(
                    IntPtr.Zero,
                    text.Error("unknown", error.Message),
                    text.Get("window", "title"),
                    0x10
                );
            }
            Exit();
            return;
        }
        _instance = new Mutex(false, "Local\\TinyTorrent.Window." + PipeClient.LogonSid);
        var owned = Acquire(_instance, 0);
#if CAPTURE
        if (!owned && MainWindow.IsCaptureReview)
        {
            _instance.Dispose();
            Exit();
            return;
        }
#endif
#if DEBUG
        // Every TinyTorrent in a logon session shares this lock, so a development
        // launch would hand off to another build's window. A window started from
        // another path is stopped so this launch takes the lock; a window from
        // this path is shown as usual.
        if (!owned && await Task.Run(StopOtherWindow))
            owned = Acquire(_instance, 0);
#endif
        if (!owned)
        {
            try
            {
                var text = await Task.Run(() => new Strings());
                try
                {
                    await PipeClient.ForwardOpen(text);
                }
                catch (Exception error)
                {
                    // The message box has no title that names the failure.
                    var message = text.Error("unknown", error.Message);
                    MessageBoxW(IntPtr.Zero, message, text.Get("window", "title"), 0x10);
                }
            }
            finally
            {
                _instance.Dispose();
                Exit();
            }
            return;
        }

        _strings = await Task.Run(() => new Strings(Supplier.AllLanguages));
        _window = new MainWindow(_strings);
        _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        _window.Closed += (_, _) =>
        {
            _windowHandle = IntPtr.Zero;
            _instance.ReleaseMutex();
            _instance.Dispose();
            Exit();
        };
#if CAPTURE
        if (MainWindow.IsCaptureReview)
        {
            _window.ShowCaptureReview();
            return;
        }
#endif
        _window.Activate();

        static bool Acquire(Mutex instance, int timeout)
        {
            try
            {
                return instance.WaitOne(timeout);
            }
            catch (AbandonedMutexException)
            {
                return true;
            }
        }
    }

#if DEBUG
    // Returns whether it stopped a window started from another path.
    private static bool StopOtherWindow()
    {
        using var current = Process.GetCurrentProcess();
        var window = current.MainModule!.FileName;
        var stopped = false;
        foreach (var process in Process.GetProcessesByName(current.ProcessName))
        {
            using (process)
            {
                try
                {
                    // A window running as administrator hides its path, so it
                    // keeps the lock. Paths compare as launched, which matches
                    // because a build's processes start each other from one folder.
                    if (
                        process.Id == current.Id
                        || process.SessionId != current.SessionId
                        || process.MainModule?.FileName is not { } path
                        || string.Equals(path, window, StringComparison.OrdinalIgnoreCase)
                    )
                        continue;
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (AggregateException) { }
                    stopped |= process.WaitForExit(5000);
                }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException)
                { }
            }
        }
        return stopped;
    }
#endif

    private void ReportFatal(Exception error, string source, string? message = null)
    {
        // WinUI and the CLR can report the same fatal exception.
        if (Interlocked.Exchange(ref _reportingFailure, 1) != 0)
            return;
        var report = ExceptionLog.Write(LogDirectory, error, source, message);
#if CAPTURE
        if (MainWindow.IsCaptureReview)
            return;
#endif
        try
        {
            CrashDialog.Show(report, _strings, _windowHandle);
        }
        catch (Exception failure)
        {
            System.Diagnostics.Debug.WriteLine($"Could not show the fatal error: {failure}");
            MessageBoxW(IntPtr.Zero, report.Text, "TinyTorrent", 0x2010);
        }
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetErrorMode();

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint SetErrorMode(uint mode);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
