using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;
using Supplier = Syno.TinyTorrent.Subtitles.Supplier;

namespace Syno.TinyTorrent;

public partial class App : Application
{
    internal static Version Version { get; } = typeof(App).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);
    internal static string UserAgent { get; } = "TinyTorrent/" + Version;
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
        Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID("Syno.TinyTorrent"));
        _instance = new Mutex(false, "Local\\TinyTorrent.Window." + PipeClient.LogonSid);
        bool owned;
        try
        {
            owned = _instance.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            owned = true;
        }
        if (!owned)
        {
            if (MainWindow.IsCaptureReview)
            {
                _instance.Dispose();
                Exit();
                return;
            }
            try
            {
                var text = await Task.Run(() => new Strings());
                try
                {
                    await PipeClient.ForwardOpen(text);
                }
                catch (Exception error)
                {
                    var message = text.Error(error);
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

        _strings = await Task.Run(() => new Strings(Supplier.CreateAll().SelectMany(supplier => supplier.Languages)));
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
    }

    private void ReportFatal(Exception error, string source, string? message = null)
    {
        // WinUI and the CLR can report the same fatal exception.
        if (Interlocked.Exchange(ref _reportingFailure, 1) != 0)
            return;
        var report = ExceptionLog.Write(LogDirectory, error, source, message);
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

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
