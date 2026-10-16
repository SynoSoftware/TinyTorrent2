using System.ComponentModel;
using System.Diagnostics;

namespace Syno.TinyTorrent.Shell;

public sealed partial class MainViewModel
{
    private long? _engineMemory;
    private long? _uiMemory;
    private long? _memoryChecked;
    private Task? _memoryRead;

    public string AboutTitle => Text.Get("window", "title");
    public string AboutDescription => Text.Get("about", "description");
    public string AboutPromises => Text.Get("about", "promises");
    public string Version => App.Version.ToString();
    public string VersionText => Text.Format("about", "version", Version);
    internal bool IsAbout => Page is WindowPage.About or WindowPage.Credits;
    internal string EngineMemory =>
        IsConnected && _engineMemory is { } bytes ? Text.Bytes(bytes) : "—";
    internal string UiMemory => _uiMemory is { } bytes ? Text.Bytes(bytes) : "—";
    internal string LifetimeDownloaded =>
        !IsLoading && !IsStorageFailed ? Text.Bytes(LifetimeTotals.Downloaded) : "—";
    internal string LifetimeUploaded =>
        !IsLoading && !IsStorageFailed ? Text.Bytes(LifetimeTotals.Uploaded) : "—";

    private void ObserveMemory()
    {
        if (
            !IsAbout
            || _closed
            || _memoryRead is { IsCompleted: false }
            || _memoryChecked is { } checkedAt && Environment.TickCount64 - checkedAt < 2000
        )
            return;
        _memoryChecked = Environment.TickCount64;
        _memoryRead = ReadMemory();
    }

    private async Task ReadMemory()
    {
        var engine = _client.ProcessId;
        var sample = await Task.Run(() =>
            (Engine: Memory(engine), UI: Memory(Environment.ProcessId))
        );
        if (_closed || !IsAbout)
            return;
        _engineMemory = engine == _client.ProcessId ? sample.Engine : null;
        _uiMemory = sample.UI;
        Changed(nameof(EngineMemory));
        Changed(nameof(UiMemory));
    }

    private static long? Memory(int? id)
    {
        if (id is null)
            return null;
        try
        {
            using var process = Process.GetProcessById(id.Value);
            return process.WorkingSet64;
        }
        catch (Exception error)
            when (error is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }
}
