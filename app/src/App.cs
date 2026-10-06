using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent;

public partial class App : Application
{
    private Mutex? _instance;
    private MainWindow? _window;

    public App() => InitializeComponent();

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
                    var message = error is CommandFailure ? error.Message : text.Error("unknown", error.Message);
                    MessageBoxW(IntPtr.Zero, message, text.Get("window", "title"), 0x10);
                }
            }
            finally { _instance.Dispose(); Exit(); }
            return;
        }

        var strings = await Task.Run(() => new Strings());
        _window = new MainWindow(strings);
        _window.Closed += (_, _) =>
        {
            _instance.ReleaseMutex();
            _instance.Dispose();
            Exit();
        };
        if (MainWindow.IsCaptureReview) _window.ShowCaptureReview();
        else _window.Activate();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
