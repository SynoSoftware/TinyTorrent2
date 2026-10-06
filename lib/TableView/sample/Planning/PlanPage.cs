using Microsoft.UI.Xaml.Controls;

namespace Syno.TableViewSample.Planning;

public sealed partial class PlanPage : Page
{
    public PlanPage()
    {
        InitializeComponent();
        Table.Schema<Activity>()
            .Hierarchy(NameColumn, row => row.Children, row => row.IsExpanded,
                (row, expanded) => row.IsExpanded = expanded)
            .SortKey(NameColumn, row => row.Name)
            .SortKey(HoursColumn, row => row.Hours);
        Table.ItemsSource = new Activity[]
        {
            new("Release", 24,
                new("Design", 8, new("Draft", 5), new("Review", 3)),
                new("Implementation", 12), new("Verification", 4)),
            new("Support", 6),
            new("Backlog", 2000, Enumerable.Range(1, 2000)
                .Select(index => new Activity($"Activity {index}", 1)).ToArray()) { IsExpanded = false }
        };
    }
}
