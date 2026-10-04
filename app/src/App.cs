using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;

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
            await PipeClient.ForwardOpen();
            _instance.Dispose();
            Exit();
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
        _window.Activate();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
