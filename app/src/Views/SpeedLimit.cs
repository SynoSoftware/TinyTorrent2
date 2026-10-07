using System.ComponentModel;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent.Views;

// The Limit torrent speed dialog's limits for the selected torrents.
public sealed class SpeedLimit : INotifyPropertyChanged
{
    private readonly MainViewModel owner;
    private readonly PipeClient client;
    private string[] _identities = [];
    private string _name = string.Empty;
    private Field _download = new(string.Empty, false);
    private Field _upload = new(string.Empty, false);
    private string _downloadInput = string.Empty;
    private string _uploadInput = string.Empty;
    private Exception? _failure;
    private bool _invalid;
    public bool IsPending { get; private set; }
    public bool HasDraft => _downloadInput != _download.Input || _uploadInput != _upload.Input;
    private bool CanSave => owner.CanSave && !IsPending;
    public bool CanSubmit => CanSave && !owner.IsClosing;
    public string Subject => _identities.Length > 1 ? owner.Text.Format("window", "selected", _identities.Length) : _name;
    public string DownloadHint => Hint(_download);
    public string UploadHint => Hint(_upload);
    public string Global => owner.FormatCaps("speed_limit", "global");
    public string Message => _invalid ? owner.Text.Get("errors", "invalid_limits") :
        _failure is null ? string.Empty : owner.Text.Error(_failure);
    public bool HasError => Message.Length > 0;
    public string Download
    {
        get => _downloadInput;
        set { if (_downloadInput == value) return; _downloadInput = value; _invalid = false; _failure = null; Refresh(); }
    }
    public string Upload
    {
        get => _uploadInput;
        set { if (_uploadInput == value) return; _uploadInput = value; _invalid = false; _failure = null; Refresh(); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;

    internal SpeedLimit(MainViewModel owner, PipeClient client)
    {
        this.owner = owner;
        this.client = client;
    }

    internal void Begin(Torrent[] torrents)
    {
        _identities = torrents.Select(torrent => torrent.TorrentId).ToArray();
        _name = torrents.Length == 1 ? torrents[0].Name : string.Empty;
        _download = Start(torrents.Select(torrent => torrent.DownloadLimit));
        _upload = Start(torrents.Select(torrent => torrent.UploadLimit));
        _failure = null;
        Cancel();
    }

    public async Task<bool> Submit()
    {
        if (!CanSave) return false;
        if (!TryLimit(_downloadInput, _download, out var download) || !TryLimit(_uploadInput, _upload, out var upload))
        {
            _invalid = true;
            Refresh();
            return false;
        }
        if (download is null && upload is null) return true;
        var arguments = new Dictionary<string, object> { ["torrent_ids"] = _identities };
        if (download is { } downloadLimit) arguments["download_limit"] = downloadLimit;
        if (upload is { } uploadLimit) arguments["upload_limit"] = uploadLimit;
        IsPending = true;
        Refresh();
        try
        {
            await client.Send("speed_limit", arguments);
            owner.Accepted("speed_limit", "title");
            _failure = null;
            Cancel();
            owner.RequestSnapshot();
            return true;
        }
        catch (Exception error) { _failure = error; return false; }
        finally { IsPending = false; Refresh(); }
    }

    internal void Cancel()
    {
        _downloadInput = _download.Input;
        _uploadInput = _upload.Input;
        _invalid = false;
        Refresh();
    }

    internal void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    private string Hint(Field field) => field.IsMixed ? owner.Text.Get("menus", "mixed") : owner.Text.Get("speed_limit", "none");

    // A field starts empty when the torrents differ or have no limit.
    private static Field Start(IEnumerable<int> limits) => limits.Distinct().ToArray() switch
    {
        [> 0 and var limit] => new(Preferences.RateInput(limit), false),
        [_] => new(string.Empty, false),
        _ => new(string.Empty, true)
    };

    // An unchanged field gives no limit, so each torrent keeps its own; an
    // empty one gives 0, which means no limit.
    private static bool TryLimit(string input, Field field, out int? limit)
    {
        limit = null;
        if (input == field.Input) return true;
        if (string.IsNullOrWhiteSpace(input))
        {
            limit = 0;
            return true;
        }
        if (!Preferences.TryRate(input, out var bytes)) return false;
        limit = bytes;
        return true;
    }

    private sealed record Field(string Input, bool IsMixed);
}
