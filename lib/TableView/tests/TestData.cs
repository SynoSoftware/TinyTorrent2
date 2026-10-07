using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Syno.TableView.Tests;

internal static class TestData
{
    /// <summary>A column that satisfies every section 6.1 invariant.</summary>
    internal static Column Column(string id) => new() { Id = id, DisplayName = id.ToUpperInvariant() };

    internal static Column Column(string id, double defaultWidth) =>
        new() { Id = id, DisplayName = id.ToUpperInvariant(), Width = defaultWidth };

    /// <summary>
    /// A saved layout holding every column's declared width, so the table keeps those widths
    /// instead of fitting its first rows.
    /// </summary>
    internal static ColumnLayout DeclaredWidths(Table table) =>
        Layout(widths: table.Columns.ToDictionary(column => column.Id!, column => column.Width));

    /// <summary>A table with the given columns declared, not yet loaded.</summary>
    internal static Table Table(params Column[] columns)
    {
        Table table = new();
        foreach (Column column in columns)
        {
            table.Columns.Add(column);
        }

        return table;
    }

    internal static ColumnLayout Layout(
        IReadOnlyList<string>? order = null,
        IReadOnlyDictionary<string, bool>? visibility = null,
        IReadOnlyDictionary<string, double>? widths = null,
        string? sortColumnId = null,
        SortDirection direction = SortDirection.Ascending) =>
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
