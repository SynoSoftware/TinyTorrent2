using System.ComponentModel;
using System.Text.Json;
using Microsoft.UI.Xaml.Controls;

namespace Syno.TinyTorrent;

public sealed class AddDraft : INotifyPropertyChanged
{
    private readonly MainViewModel _owner;
    private readonly PipeClient _client;
    private readonly Strings _strings;
    private string _source = string.Empty;
    private string _destination;
    private bool _paused;
    private string _defaultDestination;
    private string? _previewId;
    private string _name = string.Empty;
    private double _size;
    private Exception? _failure;
    private InfoBarSeverity _severity = InfoBarSeverity.Error;

    public string Source => _source;
    public string Destination
    {
        get => _destination;
        set { if (_destination == value) return; _destination = value; Refresh(); }
    }
    public bool Paused
    {
        get => _paused;
        set { if (_paused == value) return; _paused = value; Refresh(); }
    }
    public string Preview => _name.Length == 0 ? string.Empty :
        _strings.Format("add", "preview", _name, _strings.Bytes(_size));
    public string Message => _failure is null ? string.Empty : _owner.FormatError(_failure);
    public bool HasError => _failure is not null;
    public InfoBarSeverity Severity => _severity;
    public bool CanEdit => _owner.CanEdit;
    public bool CanSubmit => _owner.CanEdit && !string.IsNullOrWhiteSpace(_source) && !string.IsNullOrWhiteSpace(_destination);
    public bool HasChanges => _source.Length > 0 || _destination != _defaultDestination || _paused;
    public event PropertyChangedEventHandler? PropertyChanged;

    internal AddDraft(MainViewModel owner, PipeClient client, Strings strings)
    {
        _owner = owner;
        _client = client;
        _strings = strings;
        _destination = _defaultDestination = Windows.Storage.UserDataPaths.GetDefault().Downloads;
    }

    public async Task<bool> Prepare(string source)
    {
        if (!_owner.CanEdit) return false;
        _owner.Busy(true);
        try
        {
            var preview = await PreviewSource(source);
            if (preview.TryGetProperty("duplicate", out var duplicate) && duplicate.ValueKind == JsonValueKind.String &&
                !string.IsNullOrEmpty(duplicate.GetString()))
            {
                Fail(new CommandFailure("duplicate", null, _strings), InfoBarSeverity.Informational);
                await _client.Send("cancel_preview", new { preview_id = preview.GetProperty("preview_id").GetString() });
                await _owner.Reveal(duplicate.GetString()!);
                return false;
            }
            var previous = _previewId;
            _previewId = preview.GetProperty("preview_id").GetString();
            _source = source;
            _name = preview.GetProperty("name").GetString()!;
            _size = preview.GetProperty("size").GetDouble();
            _failure = null;
            _owner.ClearError();
            if (previous is not null) await _client.Send("cancel_preview", new { preview_id = previous });
            return true;
        }
        catch (Exception error)
        {
            Fail(error);
            if (!_owner.IsAddOpen) _owner.Report(error);
            return false;
        }
        finally { _owner.Busy(false); }
    }

    private Task<JsonElement> PreviewSource(string source) =>
        _client.Send("preview", new { source, destination = _destination });

    public async Task<bool> Submit()
    {
        if (!CanSubmit) return false;
        _owner.Busy(true);
        try
        {
            if (_previewId is null)
            {
                var preview = await PreviewSource(_source);
                _previewId = preview.GetProperty("preview_id").GetString();
            }
            var previewId = _previewId;
            _previewId = null;
            var addition = await _client.Send("add", new { preview_id = previewId, destination = _destination, paused = _paused });
            Clear();
            try { await _owner.Reveal(addition.GetProperty("torrent_id").GetString()!); }
            catch (Exception error) { _owner.Report(error); }
            return true;
        }
        catch (Exception error) { Fail(error); return false; }
        finally { _owner.Busy(false); }
    }

    public async Task Cancel()
    {
        _owner.Busy(true);
        try
        {
            if (_previewId is not null && _owner.IsConnected)
                await _client.Send("cancel_preview", new { preview_id = _previewId });
            Clear();
        }
        finally { _owner.Busy(false); }
    }

    internal void UseDefault(string destination)
    {
        if (_source.Length != 0 || _destination != _defaultDestination) return;
        _defaultDestination = _destination = destination;
        Refresh();
    }

    internal void Invalidate() { _previewId = null; Refresh(); }

    private void Clear()
    {
        _source = string.Empty;
        _previewId = null;
        _name = string.Empty;
        _size = 0;
        _paused = false;
        _failure = null;
        _defaultDestination = _destination;
        Refresh();
    }

    private void Fail(Exception error, InfoBarSeverity severity = InfoBarSeverity.Error)
    {
        _failure = error;
        _severity = severity;
        Refresh();
    }

    internal void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}
