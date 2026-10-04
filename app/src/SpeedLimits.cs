using System.ComponentModel;

namespace Syno.TinyTorrent;

public sealed class SpeedLimits(MainViewModel owner) : INotifyPropertyChanged
{
    public LimitChoice[] Choices { get; } = [new("download_limit"), new("upload_limit"),
        new("alternative_download_limit"), new("alternative_upload_limit")];
    private Exception? _failure;
    public bool HasChanges => Choices.Any(choice => choice.Value != choice.Original);
    public string Message => _failure is null ? string.Empty : owner.FormatError(_failure);
    public bool HasError => _failure is not null;
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Begin()
    {
        foreach (var choice in Choices) choice.Reset(owner.Limit(choice.Name));
        _failure = null;
        Refresh();
    }

    public async Task<bool> Apply()
    {
        try
        {
            await owner.SaveLimits(Choices.ToDictionary(choice => choice.Name, choice => choice.Value));
            Begin();
            return true;
        }
        catch (Exception error) { _failure = error; Refresh(); return false; }
    }

    internal void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

public sealed class LimitChoice(string name) : INotifyPropertyChanged
{
    private double _value;
    public string Name { get; } = name;
    public double Value
    {
        get => _value;
        set { _value = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value))); }
    }
    internal double Original { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Reset(double value) { Original = value; Value = value; }
}
