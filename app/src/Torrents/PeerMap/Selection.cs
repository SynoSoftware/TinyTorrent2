using System.Collections.Frozen;
using Syno.TinyTorrent.Shared;

namespace Syno.TinyTorrent.Torrents;

public sealed partial class PeerMap
{
    private IReadOnlyList<object> _selectedPeers = [];
    private MapModel.Selection _selection = MapModel.Selection.Empty;
    private SelectionRequest? _selectionRequest;
    private bool _selecting;
#if CAPTURE
    internal Action? PreparingSelection { get; set; }
#endif

    private sealed record SelectionRequest(IReadOnlyList<object> Peers, int Version,
        object Subject, bool Cities, object? Lifetime);

    private void SelectPeers(IReadOnlyList<object> peers)
    {
        _selectedPeers = peers;
        _selectionRequest = null;
        CaptureSelection();
    }

    private void CaptureSelection()
    {
        if (_selectionRequest is { } previous && !ReferenceEquals(previous.Subject, _model.Subject))
            _selectedPeers = [];
        if (_selectedPeers.Count == 0)
        {
            _selectionRequest = null;
            _selection = MapModel.Selection.Empty;
            return;
        }
        var request = new SelectionRequest(_selectedPeers, _model.PeerVersion,
            _model.Subject, _model.Cities, _loading);
        if (request == _selectionRequest)
            return;
        _selectionRequest = request;
        if (IsLoaded && _loading is not null && !_selecting)
            PrepareSelection();
    }

    private async void PrepareSelection()
    {
        _selecting = true;
        try
        {
            while (IsLoaded && _loading is not null && _selectionRequest is { } request)
            {
                var previous = _selection;
#if CAPTURE
                var preparing = PreparingSelection;
#endif
                var preparation = Task.Run(() =>
                {
#if CAPTURE
                    preparing?.Invoke();
#endif
                    return CountSelection(request, previous);
                });
                // Yield even when the count already finished, so a synchronous
                // mutation batch on the UI thread bumps the version before the
                // acceptance checks below read it.
                await ((Task)preparation).ConfigureAwait(
                    ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.ForceYielding);
                var prepared = await preparation;
                if (ReferenceEquals(request, _selectionRequest)
                    && ReferenceEquals(request.Peers, _selectedPeers)
                    && ReferenceEquals(request.Subject, _model.Subject)
                    && request.Version == _model.PeerVersion && request.Cities == _model.Cities
                    && ReferenceEquals(request.Lifetime, _loading) && IsLoaded)
                {
                    if (!ReferenceEquals(_selection, prepared))
                    {
                        _selection = prepared;
                        Invalidate();
                    }
                    return;
                }
                CaptureSelection();
            }
        }
        finally
        {
            _selecting = false;
        }
    }

    private static MapModel.Selection CountSelection(SelectionRequest request, MapModel.Selection previous)
    {
        var hasPeers = false;
        Dictionary<PeerPlace, int> counts = [];
        foreach (var item in request.Peers)
        {
            if (item is not Peer peer)
                continue;
            hasPeers = true;
            if (peer.Location is not { } location)
                continue;
            var place = PeerPlace.Of(location, request.Cities);
            counts[place] = counts.GetValueOrDefault(place) + 1;
        }
        if (!hasPeers)
            return MapModel.Selection.Empty;
        if (previous.HasPeers && request.Cities == previous.Cities && counts.Count == previous.Counts.Count
            && counts.All(entry => previous.Counts.GetValueOrDefault(entry.Key) == entry.Value))
            return previous;
        return new(true, request.Cities, counts.ToFrozenDictionary());
    }
}
