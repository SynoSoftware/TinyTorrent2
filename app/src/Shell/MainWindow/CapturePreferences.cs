using System.Reflection;
using System.Text.Json;

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
        var path = _placementPath;
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
            var saved = JsonSerializer.Deserialize<Placement>(await File.ReadAllTextAsync(_placementPath!));
            if (saved is null || saved.Inspector?.View != expected)
                throw new InvalidOperationException("Peer choices were not saved before closing the window.");
            // The layout reader clamps saved window bounds onto a display.
            // Omit geometry so this diagnostic window stays off-screen.
            await File.WriteAllTextAsync(_placementPath!,
                JsonSerializer.Serialize(saved with { Width = 0, Height = 0 }));
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
            _placementPath = path;
            _viewRestored = restored;
            pane.Layout = layout;
        }
    }
}
