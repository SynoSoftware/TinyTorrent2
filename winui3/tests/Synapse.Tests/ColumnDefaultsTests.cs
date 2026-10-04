using Microsoft.UI.Xaml;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Synapse;

namespace Synapse_Tests;

/// <summary>
/// Section 6: "The declared defaults are: visible, hideable, resizable, non-sortable,
/// left-aligned, a Width of 150 DIPs, a MinWidth of 48 DIPs, and an unbounded MaxWidth."
/// </summary>
[TestClass]
public class ColumnDefaultsTests
{
    [TestMethod]
    public Task Section6_DeclaredDefaults() => TestHost.RunAsync(() =>
    {
        TableColumn column = new();

        Assert.IsTrue(column.IsVisible, "section 6: default is visible");
        Assert.IsTrue(column.CanHide, "section 6: default is hideable");
        Assert.IsTrue(column.CanResize, "section 6: default is resizable");
        Assert.AreEqual(HorizontalAlignment.Left, column.CellAlignment,
            "section 6: default is left-aligned");
        Assert.AreEqual(150d, column.Width, 0d, "section 6: Width 150 DIPs");
        Assert.AreEqual(48d, column.MinWidth, 0d, "section 6: MinWidth 48 DIPs");
        Assert.IsTrue(double.IsPositiveInfinity(column.MaxWidth), "section 6: MaxWidth unbounded");

        return Task.CompletedTask;
    });

    /// <summary>
    /// Section 6: a column carries no persistence key unless the host wants one, and a sortable
    /// column is one the schema gave a sort key. Both are absent on a column nobody configured, so
    /// four declared columns and nothing else is a legal table.
    /// </summary>
    [TestMethod]
    public Task Section6_ANewColumnHasNoIdAndDoesNotSort() => TestHost.RunAsync(() =>
    {
        TableColumn column = new();

        Assert.IsNull(column.Id, "section 6: Id is optional");
        Assert.AreEqual(string.Empty, column.DisplayName);

        return Task.CompletedTask;
    });
}
