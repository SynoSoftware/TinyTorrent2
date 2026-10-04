using Microsoft.VisualStudio.TestTools.UnitTesting;
using Synapse;

namespace Synapse_Tests;

internal static class TestData
{
    /// <summary>A column that satisfies every section 6.1 invariant.</summary>
    internal static TableColumn Column(string id) => new() { Id = id, DisplayName = id.ToUpperInvariant() };

    internal static TableColumn Column(string id, double defaultWidth) =>
        new() { Id = id, DisplayName = id.ToUpperInvariant(), Width = defaultWidth };

    /// <summary>A table with the given columns declared, not yet loaded.</summary>
    internal static TableView Table(params TableColumn[] columns)
    {
        TableView table = new();
        foreach (TableColumn column in columns)
        {
            table.Columns.Add(column);
        }

        return table;
    }

    internal static TableLayout Layout(
        IReadOnlyList<string>? order = null,
        IReadOnlyDictionary<string, bool>? visibility = null,
        IReadOnlyDictionary<string, double>? widths = null,
        string? sortColumnId = null,
        TableSortDirection direction = TableSortDirection.Ascending) =>
        new(
            order ?? Array.Empty<string>(),
            visibility ?? new Dictionary<string, bool>(),
            widths ?? new Dictionary<string, double>(),
            sortColumnId,
            direction);
}

internal static class Expect
{
    /// <summary>
    /// Assert that <paramref name="action"/> throws <typeparamref name="T"/> and return it.
    /// Written by hand so the suite does not depend on which Assert.Throws overload this MSTest
    /// version ships.
    /// </summary>
    internal static T Throws<T>(Action action)
        where T : Exception
    {
        try
        {
            action();
        }
        catch (T expected)
        {
            return expected;
        }
        catch (Exception other)
        {
            throw new AssertFailedException(
                $"Expected {typeof(T).Name} but got {other.GetType().Name}: {other.Message}");
        }

        throw new AssertFailedException($"Expected {typeof(T).Name} but nothing was thrown.");
    }
}
