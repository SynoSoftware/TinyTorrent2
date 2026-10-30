using System.ComponentModel;
using Syno.TinyTorrent.Library;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Torrents;

namespace Syno.TinyTorrent.Shell;

public sealed partial class Inspector
{
    private WindowPage? _workspace;
    private FrameCallback? _subjectFrame;
    private IReadOnlyList<Torrent> _selection = [];

    internal bool IsActive => _workspace is not null;
    internal Browser? Library => _workspace == WindowPage.Library ? _owner.Library : null;
    internal event PropertyChangedEventHandler? LibraryChanged;

    internal void Activate()
    {
        WindowPage? workspace =
            !_owner.IsMinimized
            && _owner.HasInspector
            && !_owner.IsInspectorCompact
            && _owner.Page is WindowPage.Torrents or WindowPage.Library
                ? _owner.Page
                : null;
        if (_workspace == workspace)
            return;
        Close();
        if (workspace is null)
            return;
        _workspace = workspace;
        if (workspace == WindowPage.Torrents)
            _owner.SelectionChanged += OnSelection;
        else if (workspace == WindowPage.Library)
        {
            _owner.Library.SelectionChanged += OnSelection;
            _owner.Library.DetailChanged += OnLibraryDetail;
            _owner.Library.PropertyChanged += OnLibrary;
        }
        QueueSubject();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
    }

    internal void Close()
    {
        if (!IsActive)
            return;
        _owner.SelectionChanged -= OnSelection;
        _owner.Library.SelectionChanged -= OnSelection;
        _owner.Library.DetailChanged -= OnLibraryDetail;
        _owner.Library.PropertyChanged -= OnLibrary;
        _subjectFrame?.Dispose();
        _subjectFrame = null;
        var workspace = _workspace;
        _workspace = null;
        Files.IsEnabled = false;
        if (workspace == WindowPage.Torrents)
            Invalidate();
        else
        {
            CancelReading();
            _owner.Library.CancelInformation();
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
    }

    internal bool ChangesSubject(IReadOnlyList<Torrent> previous, IReadOnlyList<Torrent> selected)
    {
        // Filtering a row out is not the person's choice of another subject.
        var selection = previous.ToHashSet();
        var dropped =
            selected.All(selection.Contains)
            && !previous.Except(selected).ToHashSet().Overlaps(_owner.VisibleTorrents);
        return !(dropped && (_target is not null || HasPeerScope))
            && ((selected.Count == 1 ? selected[0] : null) != _target || HasPeerScope);
    }

    private void OnSelection(object? sender, EventArgs args) => QueueSubject();

    private void OnLibraryDetail(object? sender, EventArgs args) => QueueSubject(replace: false);

    private void QueueSubject(bool replace = true)
    {
        if (!IsActive)
            return;
        _subjectFrame?.Dispose();
        if (IsVisible)
            Invalidate();
        else
            CancelReading();
        IsReplacing |= replace;
        _subjectFrame = new FrameCallback(_dispatcher, () =>
        {
            _subjectFrame = null;
            if (Library is { } library)
                _ = ReadLibrary(library);
            else if (IsVisible)
                FollowSelection();
        });
    }

    private void FollowSelection()
    {
        var selected = _owner.Selected;
        if (_target is not null && !_owner.Contains(_target))
            Show(null, allPeers: ShowsAllPeers, selection: PeerSelection);
        if (!_selection.SequenceEqual(selected) && ChangesSubject(_selection, selected))
            Show(selected.Count == 1 ? selected[0] : null);
        else if (_target is null && !HasPeerScope)
            RefreshScope();
        _selection = selected;
        if (!HasContent)
            IsReplacing = false;
        Refresh();
        QueueRead();
    }

    private async Task ReadLibrary(Browser library)
    {
        var context = _context;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsReplacing)));
        await library.ReadDetail(_reading.Token);
        if (Library != library || context != _context)
            return;
        IsReplacing = false;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsReplacing)));
    }

    private void OnLibrary(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(Browser.Section))
        {
            QueueSubject();
            return;
        }
        if (
            !IsReplacing
            && args.PropertyName is not nameof(Browser.Selected) and not nameof(Browser.Selection)
        )
            LibraryChanged?.Invoke(this, args);
    }
}
