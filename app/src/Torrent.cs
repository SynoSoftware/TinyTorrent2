using System.ComponentModel;
using System.Globalization;
using System.Text.Json;

namespace Syno.TinyTorrent;

public sealed class Torrent(string torrentId, Strings strings) : INotifyPropertyChanged
{
    private bool _connected;
    public string TorrentId { get; } = torrentId;
    public string Name { get; private set; } = string.Empty;
    public long Size { get; private set; }
    public double Progress { get; private set; }
    public string StatusCode { get; private set; } = string.Empty;
    public string Status => strings.Status(StatusCode);
    public string StatusGlyph => StatusCode switch
    {
        "downloading" => Syno.Lucide.ArrowDownToLine,
        "seeding" => Syno.Lucide.ArrowUpFromLine,
        "paused" or "all_paused" => Syno.Lucide.Pause,
        "completed" => Syno.Lucide.CircleCheck,
        "checking" => Syno.Lucide.RefreshCw,
        "metadata" => Syno.Lucide.Hourglass,
        "queued" => Syno.Lucide.Clock,
        "error" => Syno.Lucide.CircleAlert,
        _ => Syno.Lucide.CircleQuestionMark
    };
    public double DownloadRate { get; private set; }
    public double UploadRate { get; private set; }
    public string ErrorCode { get; private set; } = string.Empty;
    public string Detail { get; private set; } = string.Empty;
    public string ErrorText => ErrorCode.Length == 0 ? string.Empty : strings.Error(ErrorCode, Detail);
    public string SizeText => strings.Bytes(Size);
    public string ProgressText => Progress.ToString("P1", CultureInfo.CurrentCulture);
    public string DownloadText => _connected ? strings.Format("units", "rate", strings.Bytes(DownloadRate)) : "—";
    public string UploadText => _connected ? strings.Format("units", "rate", strings.Bytes(UploadRate)) : "—";

    public event PropertyChangedEventHandler? PropertyChanged;

    public override string ToString() => Name;

    internal void RefreshText() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    internal void Disconnect() { _connected = false; RefreshText(); }

    internal void Update(JsonElement row)
    {
        _connected = true;
        Name = row.GetProperty("name").GetString()!;
        Size = row.GetProperty("size").GetInt64();
        Progress = row.GetProperty("progress").GetDouble();
        StatusCode = row.GetProperty("status").GetString()!;
        DownloadRate = row.GetProperty("download_rate").GetDouble();
        UploadRate = row.GetProperty("upload_rate").GetDouble();
        ErrorCode = row.GetProperty("error").GetString()!;
        Detail = row.GetProperty("detail").GetString()!;
        RefreshText();
    }
}
