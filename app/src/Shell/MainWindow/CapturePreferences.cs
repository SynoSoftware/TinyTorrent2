using System.Reflection;

namespace Syno.TinyTorrent.Shell;

public sealed partial class MainWindow
{
    private async Task CapturePreferences(List<object> outcomes)
    {
        Model.OpenInspector();
        Model.Inspector.Select(InspectorSection.Peers);
        await CaptureLayout();
        var pane = (InspectorPane)InspectorContent.Content;
        var layout = pane.Layout;
        var placement = _placement;
        var file = _placementFile;
        var restored = _viewRestored;
        var directory = Model.DataDirectory!;
        var expected = new PeerView(PeerLayout.Map, .65, .4)
        {
            Cities = true,
            SpeedCorner = MapCorner.TopLeft,
            Animation = MapAnimation.Slow,
            DotTotal = MapDotTotal.High,
            ArcLimit = 50,
            Grouping = MapGrouping.Large,
        };
        try
        {
            await RestorePlacement(directory);
            _viewRestored = true;
            var choose = typeof(InspectorPane).GetMethod("SetPeerView", BindingFlags.Instance | BindingFlags.NonPublic)!;
            choose.Invoke(pane, [expected with { Animation = MapAnimation.Fast }]);
            choose.Invoke(pane, [expected]);
            await _placementSave;
            var current = _placementFile!;
            var saved = await current.Read();
            if (saved is null || saved.Inspector?.View != expected)
                throw new InvalidOperationException("Peer choices were not saved before closing the window.");
            // The layout reader clamps saved window bounds onto a display.
            // Omit geometry so this diagnostic window stays off-screen.
            if (!await current.Save(saved with { Width = 0, Height = 0 }))
                throw new InvalidOperationException("The layout without window geometry could not be saved.");
            pane.Layout = layout with { View = PeerView.Default };
            await RestorePlacement(directory);
            if (pane.Layout.View != expected)
                throw new InvalidOperationException("The saved peer choices did not restore.");
            outcomes.Add(new { check = "peer-preferences", savedBeforeClose = true, restored = true });
        }
        finally
        {
            await _placementSave;
            _placement = placement;
            _placementFile = file;
            _viewRestored = restored;
            pane.Layout = layout;
        }
    }
}
