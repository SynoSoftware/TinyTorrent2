namespace Synapse_Sample;

/// <summary>
/// One departure, as a board publishes them: a plain value with no change notification and no
/// stable key. A board never edits a row — it posts a whole new list — which is the half of
/// specification 5.3 that a live, keyed source never reaches.
/// </summary>
public sealed class Departure
{
    public required DateTimeOffset Scheduled { get; init; }

    public required string Flight { get; init; }

    public required string Destination { get; init; }

    /// <summary>Null until the gate is called.</summary>
    public required string? Gate { get; init; }

    /// <summary>Null while the departure is on time.</summary>
    public required TimeSpan? Delay { get; init; }

    public string TimeText => Scheduled.ToString("HH:mm");

    public string GateText => Gate ?? "—";

    public string StatusText => Delay is TimeSpan late
        ? $"Delayed {late.TotalMinutes:0} min"
        : "On time";

    /// <summary>A fresh board: the same flights, with gates called and delays that have moved.</summary>
    public static List<Departure> Post(int count, int edition)
    {
        (string Code, string City)[] routes =
        {
            ("BA 1442", "Edinburgh"), ("LH 0937", "Frankfurt"), ("AF 1281", "Paris"),
            ("KL 1008", "Amsterdam"), ("EI 0157", "Dublin"), ("SK 0538", "Copenhagen"),
            ("IB 3167", "Madrid"), ("AZ 0205", "Rome"), ("LX 0347", "Zurich"),
            ("TP 1359", "Lisbon"), ("OS 0454", "Vienna"), ("SN 2094", "Brussels"),
        };

        DateTimeOffset first = DateTimeOffset.Now.Date.AddHours(6);
        List<Departure> board = new(count);

        for (int i = 0; i < count; i++)
        {
            (string code, string city) = routes[i % routes.Length];
            int minutes = ((i * 17) + (edition * 3)) % 60;

            board.Add(new Departure
            {
                Scheduled = first.AddMinutes((i * 25) + edition),
                Flight = $"{code[..2]} {(int.Parse(code[3..]) + (i / routes.Length)):0000}",
                Destination = city,
                Gate = (i + edition) % 4 == 0 ? null : $"{"ABCD"[(i + edition) % 4]}{(i % 30) + 1}",
                Delay = minutes < 40 ? null : TimeSpan.FromMinutes(minutes - 35),
            });
        }

        return board;
    }
}
