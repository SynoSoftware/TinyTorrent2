using System.Collections.ObjectModel;

namespace Syno.TableViewSample.Planning;

public sealed class Activity(string name, double hours, params Activity[] children)
{
    public string Name { get; } = name;
    public double Hours { get; } = hours;
    public ObservableCollection<Activity> Children { get; } = new(children);
    public bool IsExpanded { get; set; } = true;
    public override string ToString() => Name;
}
