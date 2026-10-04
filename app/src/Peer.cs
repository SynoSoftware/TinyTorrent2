using System.ComponentModel;
using System.Globalization;
using System.Text.Json;

namespace Syno.TinyTorrent;

public sealed class Peer : INotifyPropertyChanged
{
    private readonly Strings _text;
    public string Endpoint { get; }
    public string Client { get; }
    public string Transport { get; }
    public bool Incoming { get; }
    public bool Encrypted { get; }
    public double Progress { get; }
    public double DownloadRate { get; }
    public double UploadRate { get; }
    public long Downloaded { get; }
    public long Uploaded { get; }
    public string ProgressText => Progress.ToString("P1", CultureInfo.CurrentCulture);
    public string DownloadText => _text.Format("units", "rate", _text.Bytes(DownloadRate));
    public string UploadText => _text.Format("units", "rate", _text.Bytes(UploadRate));
    public string DownloadedText => _text.Bytes(Downloaded);
    public string UploadedText => _text.Bytes(Uploaded);
    public string ConnectionText => _text.Format("peers", "connection", Transport.ToUpperInvariant(),
        _text.Get("peers", Incoming ? "incoming" : "outgoing"), _text.Get("peers", Encrypted ? "encrypted" : "unencrypted"));
    public event PropertyChangedEventHandler? PropertyChanged;

    internal Peer(Strings text, JsonElement data)
    {
        _text = text;
        Endpoint = data.GetProperty("endpoint").GetString()!;
        Client = data.GetProperty("client").GetString()!;
        Transport = data.GetProperty("transport").GetString()!;
        Incoming = data.GetProperty("incoming").GetBoolean();
        Encrypted = data.GetProperty("encrypted").GetBoolean();
        Progress = data.GetProperty("progress").GetDouble();
        DownloadRate = data.GetProperty("download_rate").GetDouble();
        UploadRate = data.GetProperty("upload_rate").GetDouble();
        Downloaded = data.GetProperty("downloaded").GetInt64();
        Uploaded = data.GetProperty("uploaded").GetInt64();
    }

    internal void RefreshText() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    public override string ToString() => Endpoint;
}
