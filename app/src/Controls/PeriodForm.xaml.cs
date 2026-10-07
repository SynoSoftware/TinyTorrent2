using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Views;

namespace Syno.TinyTorrent.Controls;

public sealed partial class PeriodForm : UserControl
{
    public Schedule Model { get; }
    internal event EventHandler? Removing;

    public PeriodForm(Schedule model)
    {
        Model = model;
        InitializeComponent();
    }

    internal Control Editor(string? name) => (name is null ? null : FindName(name) as Control) ?? StartTime;

    internal FrameworkElement Feedback => Model.DaysMessage.Length > 0 ? DaysRow : PeriodError;

    private async void OnRemove(object sender, RoutedEventArgs args)
    {
        if (Model.OpenPeriod is not { } period) return;
        Removing?.Invoke(this, EventArgs.Empty);
        await Model.Remove(period);
    }
}
