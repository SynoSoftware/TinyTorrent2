using System.ComponentModel;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Settings;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shared.Engine;
using Syno.TinyTorrent.Shell;

namespace Syno.TinyTorrent.Torrents;

// The Limit torrent speed dialog's limits for the selected torrents.
public sealed class SpeedLimit : IDraft
{
    private readonly MainViewModel _owner;
    private readonly PipeClient _client;
    private string[] _torrentIds = [];
    private string _name = string.Empty;
    private Field _download = new(string.Empty, false);
    private Field _upload = new(string.Empty, false);
    private string _downloadInput = string.Empty;
    private string _uploadInput = string.Empty;
    private Exception? _failure;
    private bool _invalid;
    public bool IsPending { get; private set; }
    public bool HasDraft => _downloadInput != _download.Input || _uploadInput != _upload.Input;
    public bool CanSubmit => _owner.CanSave && !IsPending;
    public ClosePolicy ClosePolicy =>
        _owner.CanSave ? new ClosePolicy.Save() : new ClosePolicy.Discard();
    public string Subject =>
        _torrentIds.Length > 1
            ? _owner.Text.Format("window", "selected", _torrentIds.Length)
            : _name;
    public string DownloadHint => Hint(_download);
    public string UploadHint => Hint(_upload);
    public string Global => _owner.FormatCaps("speed_limit", "global");
    public string DownloadError =>
        _invalid && !TryLimit(_downloadInput, _download, out _)
            ? _owner.Text.Get("errors", "invalid_limits")
            : string.Empty;
    public string UploadError =>
        _invalid && !TryLimit(_uploadInput, _upload, out _)
            ? _owner.Text.Get("errors", "invalid_limits")
            : string.Empty;
    public string DownloadHelp => DownloadError.Length > 0 ? DownloadError : Global;
    public string UploadHelp => UploadError.Length > 0 ? UploadError : Global;
    public string Message => _failure is null ? string.Empty : _owner.Text.Error(_failure);
    public bool HasError => Message.Length > 0;
    public string Download
    {
        get => _downloadInput;
        set
        {
            if (_downloadInput == value)
                return;
            _downloadInput = value;
            _invalid = false;
            _failure = null;
            Refresh();
        }
    }
    public string Upload
    {
        get => _uploadInput;
        set
        {
            if (_uploadInput == value)
                return;
            _uploadInput = value;
            _invalid = false;
            _failure = null;
            Refresh();
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;

    internal SpeedLimit(MainViewModel owner, PipeClient client)
    {
        _owner = owner;
        _client = client;
    }

    internal void Begin(Torrent[] torrents)
    {
        _torrentIds = torrents.Select(torrent => torrent.TorrentId).ToArray();
        _name = torrents.Length == 1 ? torrents[0].Name : string.Empty;
        _download = Start(torrents.Select(torrent => torrent.DownloadLimit));
        _upload = Start(torrents.Select(torrent => torrent.UploadLimit));
        _failure = null;
        Discard();
    }

    public async Task<bool> Submit()
    {
        if (!CanSubmit)
            return false;
        if (
            !TryLimit(_downloadInput, _download, out var download)
            || !TryLimit(_uploadInput, _upload, out var upload)
        )
        {
            _invalid = true;
            Refresh();
            return false;
        }
        if (download is null && upload is null)
            return true;
        var arguments = new Dictionary<string, object> { ["torrent_ids"] = _torrentIds };
        if (download is { } downloadLimit)
            arguments["download_limit"] = downloadLimit;
        if (upload is { } uploadLimit)
            arguments["upload_limit"] = uploadLimit;
        IsPending = true;
        Refresh();
        try
        {
            await _client.Send("speed_limit", arguments);
            _owner.Notifications.AnnounceAccepted("speed_limit", "title");
            _failure = null;
            await Discard();
            return true;
        }
        catch (Exception error)
        {
            _failure = error;
            return false;
        }
        finally
        {
            IsPending = false;
            Refresh();
        }
    }

    public Task Discard()
    {
        _downloadInput = _download.Input;
        _uploadInput = _upload.Input;
        _invalid = false;
        Refresh();
        return Task.CompletedTask;
    }

    public void Stop() { }

    internal void Refresh() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    private string Hint(Field field) =>
        field.IsMixed ? _owner.Text.Get("menus", "mixed") : _owner.Text.Get("speed_limit", "none");

    // A field starts empty when the torrents differ or have no limit.
    private static Field Start(IEnumerable<int> limits) =>
        limits.Distinct().ToArray() switch
        {
            [> 0 and var limit] => new(Editor.RateInput(limit, RateScale.MbPerSecond), false),
            [_] => new(string.Empty, false),
            _ => new(string.Empty, true),
        };

    // An unchanged field gives no limit, so each torrent keeps its own; an
    // empty one gives 0, which means no limit.
    private static bool TryLimit(string input, Field field, out int? limit)
    {
        limit = null;
        if (input == field.Input)
            return true;
        if (string.IsNullOrWhiteSpace(input))
        {
            limit = 0;
            return true;
        }
        if (!Editor.TryRate(input, out var bytes, RateScale.MbPerSecond))
            return false;
        limit = bytes;
        return true;
    }

    private sealed record Field(string Input, bool IsMixed);
}

public sealed partial class SpeedLimitDialog : UserControl
{
    public MainViewModel Model { get; }
    public string DownloadLabel => Label("download_limit");
    public string UploadLabel => Label("upload_limit");

    public SpeedLimitDialog(MainViewModel model)
    {
        Model = model;
        InitializeComponent();
    }

    private string Label(string name) =>
        Model.Text.Format(
            "speed_limit",
            "label",
            Model.Text.Get("limits", name),
            Model.Text.RateUnit(RateScale.MbPerSecond)
        );

    internal void RefreshText() => Bindings.Update();
}
