using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Synapse;

namespace Synapse_Sample;

/// <summary>
/// A second consumer that is not a torrent list, and the only one that reads a source the way
/// specification 5.3's other half describes it: a plain enumerable with no change notification and
/// no identity selector, replaced whole every time the board is posted again.
/// </summary>
/// <remarks>
/// Without an identity selector a replacement row is a different row, so a new board clears the
/// selection. That is the contract rather than a defect, and the status line says so as it happens
/// — a host that needs selection to survive a refresh keeps its row objects or gives the schema a
/// key, which is what the torrent list does.
/// </remarks>
public sealed partial class DeparturesPage : Page
{
    private const int DepartureCount = 60;

    private int _edition;

    public DeparturesPage()
    {
        InitializeComponent();

        Table.Schema<Departure>()
            .Sort(TimeColumn, row => row.Scheduled)
            .Sort(FlightColumn, row => row.Flight)
            .Sort(DestinationColumn, row => row.Destination)
            .Sort(GateColumn, row => row.Gate ?? string.Empty)
            .Sort(StatusColumn, row => row.Delay?.TotalMinutes ?? 0);

        Post();
    }

    private void OnPostClick(object sender, RoutedEventArgs e) => Post();

    /// <summary>An empty board with nothing configured: the control supplies its own presentation.</summary>
    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        Table.ItemsSource = Array.Empty<Departure>();
        Status.Text = "board cleared";
    }

    private void Post()
    {
        int selected = Table.Selection.Items.Count;

        Table.ItemsSource = Departure.Post(DepartureCount, ++_edition);

        Status.Text = selected == 0
            ? $"board {_edition}, {DepartureCount} departures"
            : $"board {_edition} — the {selected} selected rows are gone with the board that held them";
    }

    private void OnSelectionStateChanged(object sender, TableSelectionStateChangedEventArgs e) =>
        Status.Text = e.Selection.Current is Departure row
            ? $"{e.Selection.Items.Count} selected, current {row.Flight} to {row.Destination}"
            : "nothing selected";
}
