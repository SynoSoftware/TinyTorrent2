using Syno.TinyTorrent.Torrents;
using Syno.TinyTorrent.Shared;

namespace Syno.TinyTorrent.Shell;

public sealed partial class MainViewModel
{
    private bool _selectionPending;
    private FrameCallback? _selectionFrame;
    public IReadOnlyList<Torrent> Selected => _selected;
    public Torrent? Current => _current;
    internal event EventHandler? SelectionChanged;

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
            if (
                HasInspector
                && (Inspector.IsPending || Inspector.HasDraft)
                && Inspector.ChangesSubject(_selected, selected)
            )
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
            _selected = selected;
            _current = current;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            QueueSelection();
            return true;
        }
        finally
        {
            _selectionPending = false;
        }
    }

    private void QueueSelection()
    {
        _selectionFrame?.Dispose();
        _selectionFrame = new FrameCallback(_dispatcher, () =>
        {
            _selectionFrame = null;
            if (_closed)
                return;
            RefreshSelection();
            Changed(nameof(CountStatus));
            foreach (RelayCommand command in _commands)
                command.Refresh();
        });
    }

    private void RefreshSelection()
    {
        Changed(nameof(Selected));
        Changed(nameof(Current));
        Changed(nameof(SelectionText));
        Changed(nameof(HasSelection));
        Changed(nameof(CanEditSelection));
        Changed(nameof(Sequential));
        Changed(nameof(FirstLast));
        Changed(nameof(Limited));
    }
}
