using Microsoft.UI.Xaml.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Synapse;

namespace Synapse_Tests;

/// <summary>
/// Proves the test host itself works: plain logic, the XAML runtime, the control's resource
/// lookup, and one full construct-and-load of <see cref="TableView"/>.
/// </summary>
[TestClass]
public class TestHostTests
{
    [TestMethod]
    public void TheUiThreadStartedBeforeTheFirstTest() => Assert.IsNotNull(TestHost.Dispatcher);

    [TestMethod]
    public Task AXamlControlCanBeConstructedOnTheTestUiThread() => TestHost.RunAsync(() =>
    {
        Grid grid = new();
        Assert.AreEqual(0d, grid.MinWidth, 0d);
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task TheControlCanReadItsOwnResourceFile() => TestHost.RunAsync(() =>
    {
        // TableHeaderStrip's constructor reads Synapse/Resources through
        // ResourceLoader.GetDefaultResourceFilePath(). It throws when the merged resources.pri is
        // missing beside the running executable.
        TableHeaderStrip strip = new();

        Assert.IsNotNull(strip);
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task ATableViewCanBeConstructedAndLoaded() => TestHost.RunAsync(async () =>
    {
        TableView table = TestData.Table(TestData.Column("a"));

        await TableHarness.LoadAsync(table);

        CollectionAssert.AreEqual(new[] { "a" }, table.Layout.Order.ToArray());
    });
}
