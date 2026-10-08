using Syno.TinyTorrent.Models;

namespace Syno.TinyTorrent;

public sealed partial class MainViewModel
{
    private bool _selectionPending;
    public IReadOnlyList<Torrent> Selected => _selected;
    public Torrent? Current => _current;

    public async Task<bool> Select(
        IEnumerable<Torrent> items,
        Torrent? current,
        Func<Task<bool>>? resolveDraft = null
    )
    {
        if (_selectionPending || IsClosing)
            return false;
        var selected = items.Distinct().ToArray();
        if (
            selected.Any(torrent => !Contains(torrent))
            || current is not null && !Contains(current)
        )
            return false;
        if (selected.SequenceEqual(_selected) && ReferenceEquals(current, _current))
            return true;
        var session = _sessionId;
        var connected = IsConnected;
        var target = Inspector.Target;
        _selectionPending = true;
        try
        {
            // The table drops a row that leaves the view, such as a torrent that
            // finishes under the Downloading filter. The person did not choose
            // another torrent, so the selection follows the table while the
            // inspector, and any unfinished edit in it, stays on its torrent.
            var dropped =
                selected.All(_selected.Contains)
                && !_selected.Except(selected).Any(VisibleTorrents.Contains);
            var keepsTarget = dropped && Inspector.Target is not null;
            var next = selected.Length == 1 ? selected[0] : null;
            var changesTarget = Inspector.IsOpen && next != Inspector.Target && !keepsTarget;
            if (changesTarget)
            {
                if (Inspector.IsPending)
                    return false;
                if (Inspector.HasDraft && (resolveDraft is null || !await resolveDraft()))
                    return false;
            }
            if (
                IsClosing
                || session != _sessionId
                || connected != IsConnected
                || target != Inspector.Target
                || selected.Any(torrent => !Contains(torrent))
                || current is not null && !Contains(current)
            )
                return false;
            if (changesTarget && !Inspector.Show(next))
                return false;
            _selected = selected;
            _current = current;
            // The inspector shows the selected count when it has no target.
            Inspector.Refresh();
            RefreshWindow();
            return true;
        }
        finally
        {
            _selectionPending = false;
        }
    }
}
