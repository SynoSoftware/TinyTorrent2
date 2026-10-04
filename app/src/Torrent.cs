using System.ComponentModel;
using System.Globalization;
using System.Text.Json;

namespace Syno.TinyTorrent;

public sealed class Torrent(string torrentId, Strings strings) : INotifyPropertyChanged
{
    private bool _connected;
    public string TorrentId { get; } = torrentId;
    public string Name { get; private set; } = string.Empty;
    public string SavePath { get; private set; } = string.Empty;
    public long Size { get; private set; }
    public long Downloaded { get; private set; }
    public long Uploaded { get; private set; }
    public int Seeds { get; private set; }
    public int Peers { get; private set; }
    public int Queue { get; private set; }
    public int QueueOrder => Queue < 0 ? int.MaxValue : Queue;
    public long Added { get; private set; }
    public string[] Hashes { get; private set; } = [];
    public double Progress { get; private set; }
    public double Remaining => Size * (1 - Progress);
    public double Ratio => Downloaded == 0 ? Uploaded == 0 ? 0 : double.PositiveInfinity : (double)Uploaded / Downloaded;
    public string StatusCode { get; private set; } = string.Empty;
    public string Status => strings.Status(StatusCode);
    public bool IsPaused => StatusCode is "paused" or "all_paused";
    public bool IsError => StatusCode == "error";
    public bool IsProgressNormal => !IsPaused && !IsError;
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
    public string EtaText => !_connected || DownloadRate <= 0 || Remaining <= 0 ? "—" :
        strings.Format("units", "eta", (int)Math.Ceiling(Remaining / DownloadRate / 60));
    public string RatioText => double.IsPositiveInfinity(Ratio) ? "∞" :
        Ratio.ToString(Downloaded == 0 ? "N0" : "N2", CultureInfo.CurrentCulture);
    public string PeersText => strings.Format("units", "peers", Seeds, Peers);
    public string AddedText => Added <= 0 ? "—" : DateTimeOffset.FromUnixTimeSeconds(Added).LocalDateTime.ToString("g", CultureInfo.CurrentCulture);
    public string QueueText => Queue < 0 ? "—" : (Queue + 1).ToString(CultureInfo.CurrentCulture);

    public event PropertyChangedEventHandler? PropertyChanged;

    public override string ToString() => Name;

    internal void RefreshText() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    internal void Disconnect() { _connected = false; RefreshText(); }

    internal void Update(JsonElement row)
    {
        _connected = true;
        Name = row.GetProperty("name").GetString()!;
        SavePath = row.GetProperty("save_path").GetString()!;
        Size = row.GetProperty("size").GetInt64();
        Downloaded = row.GetProperty("downloaded").GetInt64();
        Uploaded = row.GetProperty("uploaded").GetInt64();
        Seeds = row.GetProperty("seeds").GetInt32();
        Peers = row.GetProperty("peers").GetInt32();
        Queue = row.GetProperty("queue").GetInt32();
        Added = row.GetProperty("added").GetInt64();
        Hashes = row.GetProperty("hashes").EnumerateArray().Select(hash => hash.GetString()!).ToArray();
        Progress = row.GetProperty("progress").GetDouble();
        StatusCode = row.GetProperty("status").GetString()!;
        DownloadRate = row.GetProperty("download_rate").GetDouble();
        UploadRate = row.GetProperty("upload_rate").GetDouble();
        ErrorCode = row.GetProperty("error").GetString()!;
        Detail = row.GetProperty("detail").GetString()!;
        RefreshText();
    }
}
