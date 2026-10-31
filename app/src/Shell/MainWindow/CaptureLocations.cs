using System.Reflection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Syno.TinyTorrent.Shell;

public sealed partial class MainWindow
{
    private async Task CaptureLocationWarning(List<object> outcomes)
    {
        var field = typeof(MainViewModel).GetField("_locationSilence", BindingFlags.Instance | BindingFlags.NonPublic)!;
        const string key = "location:capture";
        var location = new MainViewModel.LocationCondition(@"\\server\downloads", "download_path_unavailable", 1);
        try
        {
            for (var choice = 0; choice < 3; choice++)
            {
                field.SetValue(Model, null);
                Model.ObserveLocation(key, location);
                await CaptureLayout();
                if (!Model.ShowsStatus || Model.CurrentStatus?.CanSilence != true)
                    throw new InvalidOperationException("The location warning did not appear.");
                CaptureInvoke(StatusMore);
                await CaptureLayout();
                var items = VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot)
                    .Where(popup => popup.IsOpen)
                    .SelectMany(popup => CaptureElements(popup.Child))
                    .OfType<MenuFlyoutItem>().ToArray();
                if (items.Length != 3)
                    throw new InvalidOperationException("The location warning has no complete silence menu.");
                if (choice == 0)
                    await CapturePage("location-silence-menu");
                CaptureInvoke(items[choice]);
                Model.ObserveLocation(key, null);
                Model.ObserveLocation(key, location with { Count = 4 });
                if (Model.CurrentStatus?.CanSilence == true)
                    throw new InvalidOperationException("A changed location warning bypassed silence.");
                Model.ObserveStopped(new MainViewModel.StoppedCondition.One("Capture torrent", "storage_failed", string.Empty));
                if (!Model.ShowsStatus || Model.CurrentStatus?.CanSilence != false)
                    throw new InvalidOperationException("Silence hid a higher-priority torrent problem.");
                Model.ObserveStopped(null);
                outcomes.Add(new { choice, silenced = Model.CurrentStatus?.CanSilence != true });
            }
            field.SetValue(Model, null);
            Model.ObserveLocation(key, location);
            Model.SilenceLocations(TimeSpan.FromMilliseconds(50));
            await Task.Delay(100);
            Model.ObserveLocation(key, location);
            if (!Model.ShowsStatus || Model.CurrentStatus?.CanSilence != true)
                throw new InvalidOperationException("The location warning did not return after silence expired.");
            outcomes.Add(new { expiry = true });
        }
        finally
        {
            field.SetValue(Model, null);
            Model.ObserveStopped(null);
            Model.ObserveLocation(key, null);
        }
    }
}
