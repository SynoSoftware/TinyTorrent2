using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Models;

namespace Syno.TinyTorrent;

public sealed partial class MainWindow
{
    private async Task CapturePieces(List<object> outcomes, List<string> completed)
    {
        var original = CaptureElements(InspectorContent).OfType<PiecesMap>().Single();
        var parent = original.Parent as Panel ?? throw new InvalidOperationException("The Pieces map has no panel.");
        var index = parent.Children.IndexOf(original);
        var map = new PiecesMap();
        var image = (Image)map.FindName("Bitmap");
        parent.Children.RemoveAt(index);
        try
        {
            parent.Children.Insert(index, map);
            foreach (var count in new[] { 20, 20000 })
            {
                var pieces = CreateFixture(count);
                var counts = pieces.Counts(0, pieces.Count);
                var scenario = count == 20 ? "mixed" : "aggregate";
                await CaptureMatrix(async (suffix, size) =>
                {
                    map.Show(null, Model.Text);
                    var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var token = image.RegisterPropertyChangedCallback(Image.SourceProperty, (_, _) =>
                    {
                        if (image.Source is not null) ready.TrySetResult();
                    });
                    try
                    {
                        map.Show(pieces, Model.Text);
                        await CaptureLayout();
                        await ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
                    }
                    finally { image.UnregisterPropertyChangedCallback(Image.SourceProperty, token); }
                    var name = "synthetic-pieces-" + scenario + "-" + suffix;
                    await CaptureUi(name);
                    completed.Add(name);
                    outcomes.Add(new { scene = name, synthetic = true, pieces = pieces.Count,
                        requestedWidth = size.Width, requestedHeight = size.Height, clientWidth = Root.ActualWidth, clientHeight = Root.ActualHeight,
                        counts = Enum.GetValues<PieceKind>().ToDictionary(kind => kind.ToString(), kind => counts[(int)kind]),
                        width = image.ActualWidth, height = image.ActualHeight,
                        scope = "Production Pieces model and renderer with synthetic data; the live torrent header describes a different fixture. No engine accuracy, transfer or native input is established." });
                });
            }
        }
        finally
        {
            parent.Children.Remove(map);
            parent.Children.Insert(index, original);
            await CaptureLayout();
        }

        static Pieces CreateFixture(int count)
        {
            PieceKind[] kinds = [PieceKind.Verified, PieceKind.Downloading, PieceKind.Rare, PieceKind.Common, PieceKind.Unavailable];
            var verified = new bool[count];
            var availability = new int[count];
            var downloading = new List<object>();
            for (var piece = 0; piece < count; piece++)
            {
                var kind = kinds[piece / (count / kinds.Length)];
                if (count > 20)
                {
                    if (piece % 17 == 0) kind = PieceKind.Unavailable;
                    else if (piece % 19 == 0) kind = PieceKind.Verified;
                }
                verified[piece] = kind == PieceKind.Verified;
                availability[piece] = kind == PieceKind.Unavailable ? 0 : kind == PieceKind.Rare ? 1 : 20;
                if (kind == PieceKind.Downloading)
                    downloading.Add(new { index = piece, progress = 0.2 + piece % 4 * 0.2 });
            }
            return new Pieces(JsonSerializer.SerializeToElement(new { metadata_ready = true, piece_size = 262144,
                peers = 20, verified, availability, downloading }), [new PieceFile("Synthetic renderer fixture.bin", 0, count)]);
        }
    }
}
