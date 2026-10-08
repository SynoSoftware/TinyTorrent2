using System.Reflection;
using Microsoft.UI.Xaml;

namespace Syno.TableView.Tests;

/// <summary>
/// Attaches a <see cref="Table"/> to the live test window and reads the control's internal
/// effective layout.
/// </summary>
/// <remarks>
/// <c>Table.EffectiveLayout</c> and <c>EffectiveLayout</c> are internal to TableView and the control
/// assembly grants no <c>InternalsVisibleTo</c>. Reflection is the only way to observe the effective
/// layout without changing the control, which this task must not do.
/// </remarks>
internal static class TableHarness
{
    private static readonly PropertyInfo EffectiveLayoutProperty =
        typeof(Table).GetProperty("EffectiveLayout", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMemberException("Table.EffectiveLayout");

    /// <summary>Put the table in the live tree and wait for its first <c>Loaded</c>.</summary>
    internal static async Task LoadAsync(Table table)
    {
        TaskCompletionSource loaded = new();
        void OnLoaded(object sender, RoutedEventArgs e) => loaded.TrySetResult();

        table.Loaded += OnLoaded;
        TestHost.RootPanel.Children.Add(table);

        try
        {
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            table.Loaded -= OnLoaded;
        }
    }

    /// <summary>
    /// Wait for an observable state rather than for a fixed time, and fail with
    /// <paramref name="what"/> when it does not arrive within ten seconds.
    /// </summary>
    internal static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.IsTrue(DateTime.UtcNow < deadline, $"Waited 10 s for {what}.");
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Put the table in the live tree and return the exception its schema capture throws.
    /// The control validates inside its <c>Loaded</c> handler, so the throw reaches the
    /// application's unhandled-exception hook rather than the caller.
    /// </summary>
    internal static async Task<Exception> LoadExpectingFailureAsync(Table table)
    {
        Task<Exception> trapped = TestHost.TrapAsync();

        TaskCompletionSource loaded = new();
        void OnLoaded(object sender, RoutedEventArgs e) => loaded.TrySetResult();
        table.Loaded += OnLoaded;
        TestHost.RootPanel.Children.Add(table);

        Task completed = await Task.WhenAny(
            trapped,
            loaded.Task,
            Task.Delay(TimeSpan.FromSeconds(10))
        );
        table.Loaded -= OnLoaded;
        TestHost.DisarmTrap();

        // The control's own Loaded handler runs before this one, so a throw and a completed Loaded
        // can both be observed. The throw is the answer.
        if (trapped.IsCompleted)
        {
            return await trapped;
        }

        throw new AssertFailedException(
            completed == loaded.Task
                ? "Schema capture accepted the column set; an exception was expected."
                : "Neither Loaded nor an exception was observed within 10 s."
        );
    }

    // ------------------------------------------------------------ effective layout

    /// <summary>
    /// The control's internal <c>EffectiveLayout</c>. Every test that needs the effective layout
    /// comes through here, so the one string this suite cannot have the compiler check is written
    /// once, and a rename of the control's accessor fails loudly at the top of this file rather
    /// than as a stray null somewhere in a test.
    /// </summary>
    internal static object EffectiveLayout(Table table) =>
        EffectiveLayoutProperty.GetValue(table)
        ?? throw new InvalidOperationException("EffectiveLayout was null.");

    internal static double TotalWidth(Table table) =>
        (double)Read(EffectiveLayout(table), "TotalWidth")!;

    /// <summary>Ids of the effective order, hidden columns included.</summary>
    internal static string[] Order(Table table) =>
        EffectiveColumns(table).Select(c => (string)Read(c, "Id")!).ToArray();

    /// <summary>Effective width of one column by id, whether it is visible or not.</summary>
    internal static double EffectiveWidth(Table table, string id) =>
        (double)Read(EffectiveColumn(table, id), "Width")!;

    internal static bool IsVisible(Table table, string id) =>
        (bool)Read(EffectiveColumn(table, id), "IsVisible")!;

    /// <summary>Visible columns as (id, cumulative left edge, width), in effective order.</summary>
    internal static (string Id, double Offset, double Width)[] VisibleColumns(Table table) =>
        ((System.Collections.IEnumerable)Read(EffectiveLayout(table), "VisibleColumns")!)
            .Cast<object>()
            .Select(v =>
                (
                    Id: (string)Read(Read(v, "Column")!, "Id")!,
                    Offset: (double)Read(v, "Offset")!,
                    Width: (double)Read(v, "Width")!
                )
            )
            .ToArray();

    /// <summary>
    /// One internal <c>EffectiveColumn</c>, which is what the control's own column operations take.
    /// </summary>
    internal static object EffectiveColumn(Table table, string id) =>
        EffectiveColumns(table).FirstOrDefault(c => (string?)Read(c, "Id") == id)
        ?? throw new AssertFailedException($"No effective column '{id}'.");

    private static IEnumerable<object> EffectiveColumns(Table table) =>
        ((System.Collections.IEnumerable)Read(EffectiveLayout(table), "Order")!).Cast<object>();

    private static object? Read(object target, string name) =>
        Property(target.GetType(), name).GetValue(target);

    private static PropertyInfo Property(Type type, string name) =>
        type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new MissingMemberException(type.Name, name);
}
