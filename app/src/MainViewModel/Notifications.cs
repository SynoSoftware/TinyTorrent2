using System.Text.Json;
using System.Windows.Input;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent;

public sealed partial class MainViewModel
{
    private Completion? _completion;
    private bool _resumeNotice;
    private sealed record Completion(string TorrentId, string Name, int Count);

    public bool HasCompletion => _completion is not null;
    public string CompletionName => _completion is { } completion && _byId.TryGetValue(completion.TorrentId, out var torrent)
        ? torrent.Name : _completion?.Name ?? string.Empty;
    public string CompletionText => _completion is not { } completion ? string.Empty : completion.Count == 1
        ? Text.Format("notifications", "completed", CompletionName)
        : Text.Format("notifications", "completed_more", CompletionName, completion.Count - 1);
    public string CompletionTip => Text.Format("notifications", "open_folder_tip", CompletionName);
    public ICommand OpenCompletion { get; }
    public bool HasResumeNotice => _resumeNotice && IsSessionPaused;
    public ICommand ResolvePause { get; }
    public string PauseAction => Text.Get("notifications", _pause == PauseReason.Interface ? "settings" : "override");
    public string PauseActionTip => Text.Get("notifications", _pause switch
    {
        PauseReason.Interface => "settings_tip",
        PauseReason.Schedule => "override_schedule_tip",
        _ => "override_tip"
    });
    public string PauseActionGlyph => _pause == PauseReason.Interface ? Syno.Lucide.Settings : Syno.Lucide.LockKeyholeOpen;

    public void DismissResume()
    {
        _resumeNotice = false;
        Changed(nameof(HasResumeNotice));
    }

    private bool CanOpenCompletion => CanEdit && _completion is { } completion && _byId.ContainsKey(completion.TorrentId);

    internal void ReceiveNotice(JsonElement notice)
    {
        if (_closed) return;
        var kind = notice.GetProperty("kind").GetString();
        var name = notice.GetProperty("name").GetString() ?? string.Empty;
        var torrentId = notice.GetProperty("torrent_id").GetString() ?? string.Empty;
        var count = notice.TryGetProperty("count", out var total) ? total.GetInt32() : 1;
        if (kind == "completed")
        {
            _completion = _completion is { } previous ? previous with { Count = previous.Count + count } : new(torrentId, name, count);
            RefreshCompletion();
        }
        else if (kind is "error" or "add_failed" or "delete_failed")
        {
            var detail = notice.GetProperty("detail").GetString() ?? string.Empty;
            if (name.Length > 0) detail = Text.Format("errors", "torrent", name, detail);
            if (count > 1) detail = Text.Format("errors", "detail", detail, Text.Format("errors", "additional_problems", count - 1));
            var code = kind == "error" ? torrentId.Length == 0 ? "unknown" : "torrent_error" : kind;
            Report(new CommandFailure(code, detail, Text));
        }
    }

    public void DismissCompletion()
    {
        _completion = null;
        RefreshCompletion();
    }

    private void RefreshCompletion()
    {
        Changed(nameof(CompletionName));
        Changed(nameof(CompletionText));
        Changed(nameof(CompletionTip));
        Changed(nameof(HasCompletion));
        ((Command)OpenCompletion).Refresh();
    }

    private Task OpenCompleted() => CanOpenCompletion && _completion is { } completion && _byId.TryGetValue(completion.TorrentId, out var torrent)
        ? OpenTorrent(torrent, true) : Task.CompletedTask;
}
