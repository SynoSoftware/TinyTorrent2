using Syno.TinyTorrent.Models;

namespace Syno.TinyTorrent;

public sealed partial class MainViewModel
{
    private bool _selectionPending;
    public IReadOnlyList<Torrent> Selected => _selected;
    public Torrent? Current => _current;

    public async Task<bool> Select(IEnumerable<Torrent> items, Torrent? current, Func<Task<bool>>? resolveDraft = null)
    {
        if (_selectionPending || IsClosing) return false;
        var selected = items.Distinct().ToArray();
        if (selected.Any(torrent => !Contains(torrent)) || current is not null && !Contains(current)) return false;
        if (selected.SequenceEqual(_selected) && ReferenceEquals(current, _current)) return true;
        var session = _sessionId;
        var connected = IsConnected;
        var target = Inspector.Target;
        _selectionPending = true;
        try
        {
            var changesTarget = Inspector.IsOpen && (selected.Length != 1 || selected[0] != Inspector.Target);
            if (changesTarget)
            {
                if (Inspector.IsPending) return false;
                if (Inspector.HasDraft && (resolveDraft is null || !await resolveDraft())) return false;
            }
            if (IsClosing || session != _sessionId || connected != IsConnected || target != Inspector.Target ||
                selected.Any(torrent => !Contains(torrent)) || current is not null && !Contains(current)) return false;
            if (changesTarget)
            {
                if (Inspector.IsPending || Inspector.HasDraft) return false;
                var accepted = selected.Length == 1 ? Inspector.Open(selected[0]) : Inspector.Close();
                if (!accepted) return false;
            }
            _selected = selected;
            _current = current;
            RefreshWindow();
            return true;
        }
        finally { _selectionPending = false; }
    }
}
