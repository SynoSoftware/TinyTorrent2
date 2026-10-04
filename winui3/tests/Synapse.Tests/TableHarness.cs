using System.Reflection;
using Microsoft.UI.Xaml;
using Synapse;

namespace Synapse_Tests;

/// <summary>
/// Attaches a <see cref="TableView"/> to the live test window and reads the control's internal
/// resolved geometry.
/// </summary>
/// <remarks>
/// <c>TableView.Geometry</c> and <c>ResolvedLayout</c> are internal to Synapse and the control
/// assembly grants no <c>InternalsVisibleTo</c>. Reflection is the only way to observe the resolved
/// geometry without changing the control, which this task must not do.
/// </remarks>
internal static class TableHarness
{
    private static readonly PropertyInfo GeometryProperty =
        typeof(TableView).GetProperty("Geometry", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMemberException("TableView.Geometry");

    /// <summary>Put the table in the live tree and wait for its first <c>Loaded</c>.</summary>
    internal static async Task LoadAsync(TableView table)
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
    /// Put the table in the live tree and return the exception its schema capture throws.
    /// The control validates inside its <c>Loaded</c> handler, so the throw reaches the
    /// application's unhandled-exception hook rather than the caller.
    /// </summary>
    internal static async Task<Exception> LoadExpectingFailureAsync(TableView table)
    {
        Task<Exception> trapped = TestHost.TrapAsync();

        TaskCompletionSource loaded = new();
        void OnLoaded(object sender, RoutedEventArgs e) => loaded.TrySetResult();
        table.Loaded += OnLoaded;
        TestHost.RootPanel.Children.Add(table);

        Task completed = await Task.WhenAny(trapped, loaded.Task, Task.Delay(TimeSpan.FromSeconds(10)));
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
                : "Neither Loaded nor an exception was observed within 10 s.");
    }

    // ------------------------------------------------------------ resolved geometry

    /// <summary>
    /// The control's internal <c>ResolvedLayout</c>. Every test that needs the resolved geometry
    /// comes through here, so the one string this suite cannot have the compiler check is written
    /// once, and a rename of the control's accessor fails loudly at the top of this file rather
    /// than as a stray null somewhere in a test.
    /// </summary>
    internal static object Geometry(TableView table) =>
        GeometryProperty.GetValue(table) ?? throw new InvalidOperationException("Geometry was null.");

    internal static double TotalWidth(TableView table) =>
        (double)Read(Geometry(table), "TotalWidth")!;

    /// <summary>Ids of the resolved order, hidden columns included.</summary>
    internal static string[] Order(TableView table) =>
        ResolvedColumns(table).Select(c => (string)Read(c, "Id")!).ToArray();

    /// <summary>Resolved width of one column by id, whether it is visible or not.</summary>
    internal static double ResolvedWidth(TableView table, string id) =>
        (double)Read(ResolvedColumn(table, id), "Width")!;

    internal static bool IsVisible(TableView table, string id) =>
        (bool)Read(ResolvedColumn(table, id), "IsVisible")!;

    /// <summary>Visible columns as (id, cumulative left edge, width), in effective order.</summary>
    internal static (string Id, double Offset, double Width)[] VisibleColumns(TableView table) =>
        ((System.Collections.IEnumerable)Read(Geometry(table), "VisibleColumns")!)
        .Cast<object>()
        .Select(v => (
            Id: (string)Read(Read(v, "Column")!, "Id")!,
            Offset: (double)Read(v, "Offset")!,
            Width: (double)Read(v, "Width")!))
        .ToArray();

    /// <summary>
    /// One internal <c>ResolvedColumn</c>, which is what the control's own column operations take.
    /// </summary>
    internal static object ResolvedColumn(TableView table, string id) =>
        ResolvedColumns(table).FirstOrDefault(c => (string?)Read(c, "Id") == id)
        ?? throw new AssertFailedException($"No resolved column '{id}'.");

    /// <summary>The table-owned horizontal offset the header strip and every row panel subtract.</summary>
    internal static void SetHorizontalOffset(TableView table, double value)
    {
        object geometry = Geometry(table);
        Property(geometry.GetType(), "HorizontalOffset").SetValue(geometry, value);
    }

    /// <summary>The resize separator's own arithmetic, in header-strip coordinates.</summary>
    internal static int TrailingEdgeNear(TableView table, double x, double tolerance)
    {
        object geometry = Geometry(table);
        MethodInfo method = geometry.GetType().GetMethod(
            "TrailingEdgeNear", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(geometry.GetType().Name, "TrailingEdgeNear");
        return (int)method.Invoke(geometry, new object[] { x, tolerance })!;
    }

    private static IEnumerable<object> ResolvedColumns(TableView table) =>
        ((System.Collections.IEnumerable)Read(Geometry(table), "Order")!).Cast<object>();

    private static object? Read(object target, string name) =>
        Property(target.GetType(), name).GetValue(target);

    private static PropertyInfo Property(Type type, string name) =>
        type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new MissingMemberException(type.Name, name);
}
