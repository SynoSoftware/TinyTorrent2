using System.ComponentModel;

namespace Syno.TableViewSample.Compare;

/// <summary>
/// One row of the comparison page. Both panes show the same instances, so a speed change
/// reaches both through <see cref="PropertyChanged"/> and only the order is left to each pane.
/// </summary>
public sealed class CompareRow : INotifyPropertyChanged
{
    private int _speed;

    public CompareRow(string id, string name, int speed)
    {
        Id = id;
        Name = name;
        _speed = speed;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; }

    public string Name { get; }

    public int Speed
    {
        get => _speed;
        set
        {
            if (_speed == value)
            {
                return;
            }

            _speed = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Speed)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SpeedText)));
        }
    }

    public string SpeedText => $"{_speed:N0} KB/s";
}
