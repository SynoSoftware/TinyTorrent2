using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent;

public partial class App : Application
{
    private Mutex? _instance;
    private MainWindow? _window;
    private Strings? _strings;
    private int _reportingFailure;
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TinyTorrent");

    public App()
    {
        // Unknown UI failures cannot safely be marked as handled.
        UnhandledException += (_, args) => ReportFatal(args.Exception, "WinUI", args.Message);
        AppDomain.CurrentDomain.UnhandledException += (_, args) => ReportFatal(
            args.ExceptionObject as Exception ?? new Exception("Unhandled non-Exception object."), ".NET");
        TaskScheduler.UnobservedTaskException += (_, args) =>
            ExceptionLog.Write(LogDirectory, args.Exception, "Unobserved task");
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID("Syno.TinyTorrent"));
        _instance = new Mutex(false, "Local\\TinyTorrent.UI." + PipeClient.LogonSid);
        bool owned;
        try { owned = _instance.WaitOne(0); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned)
        {
            if (MainWindow.IsCaptureReview) { _instance.Dispose(); Exit(); return; }
            try
            {
                var text = await Task.Run(() => new Strings());
                try { await PipeClient.ForwardOpen(text); }
                catch (Exception error)
                {
                    var message = text.Error(error);
                    MessageBoxW(IntPtr.Zero, message, text.Get("window", "title"), 0x10);
                }
            }
            finally { _instance.Dispose(); Exit(); }
            return;
        }

        _strings = await Task.Run(() => new Strings());
        _window = new MainWindow(_strings);
        _window.Closed += (_, _) =>
        {
            _instance.ReleaseMutex();
            _instance.Dispose();
            Exit();
        };
#if CAPTURE
        if (MainWindow.IsCaptureReview) { _window.ShowCaptureReview(); return; }
#endif
        _window.Activate();
    }

    private void ReportFatal(Exception error, string source, string? message = null)
    {
        // WinUI and the CLR can report the same fatal exception.
        if (Interlocked.Exchange(ref _reportingFailure, 1) != 0) return;
        var path = ExceptionLog.Write(LogDirectory, error, source, message);
        try
        {
            // Startup can fail before the language catalogue loads.
            var explanation = _strings?.Get("crash", "message") ??
                "The TinyTorrent window encountered an unexpected error and must close. Reopen TinyTorrent to restore the window. The download engine runs separately.";
            var details = path is null
                ? (_strings?.Get("crash", "unsaved") ?? "The error report could not be saved. Press Ctrl+C to copy this message when reporting the problem.") + "\n\n" + error
                : _strings?.Format("crash", "saved", path) ?? $"Include this file when reporting the problem:\n{path}\n\nPress Ctrl+C to copy this message.";
            // A native dialog also works when XAML initialization or rendering failed.
            MessageBoxW(IntPtr.Zero, explanation + "\n\n" + details, "TinyTorrent", 0x2010);
        }
        catch (Exception failure)
        {
            System.Diagnostics.Debug.WriteLine($"Could not show the fatal error: {failure}");
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
