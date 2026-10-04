using Microsoft.UI.Xaml.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Syno.TableView.Tests;

/// <summary>
/// Proves the test host itself works: plain logic, the XAML runtime, the control's resource
/// lookup, and one full construct-and-load of <see cref="Table"/>.
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
        // Header.Strip's constructor reads its accessible name from the en.json embedded in the
        // control's library.
        Header.Strip strip = new();

        Assert.IsNotNull(strip);
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task ATableViewCanBeConstructedAndLoaded() => TestHost.RunAsync(async () =>
    {
        Table table = TestData.Table(TestData.Column("a"));

        await TableHarness.LoadAsync(table);

        CollectionAssert.AreEqual(new[] { "a" }, table.Layout.Order.ToArray());
    });
}
