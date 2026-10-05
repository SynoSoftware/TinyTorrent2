using System.ComponentModel;
using System.Globalization;

namespace Syno.TinyTorrent;

public sealed class SpeedLimits(MainViewModel owner) : INotifyPropertyChanged
{
    public LimitChoice[] Choices { get; } = [new("download_limit"), new("upload_limit"),
        new("alternative_download_limit"), new("alternative_upload_limit")];
    private Exception? _failure;
    public bool HasChanges => Choices.Any(choice => choice.Input != choice.Original);
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
            var values = new Dictionary<string, double>();
            foreach (var choice in Choices)
            {
                if (!double.TryParse(choice.Input, NumberStyles.Float | NumberStyles.AllowThousands,
                        CultureInfo.CurrentCulture, out var value))
                    throw new CommandFailure("invalid_limits", null, owner.Text);
                values.Add(choice.Name, value);
            }
            await owner.SaveLimits(values);
            Begin();
            return true;
        }
        catch (Exception error) { _failure = error; Refresh(); return false; }
    }

    internal void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

public sealed class LimitChoice(string name) : INotifyPropertyChanged
{
    private string _input = string.Empty;
    public string Name { get; } = name;
    public string Input
    {
        get => _input;
        set { _input = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Input))); }
    }
    internal string Original { get; private set; } = string.Empty;
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Reset(double value) { Original = value.ToString(CultureInfo.CurrentCulture); Input = Original; }
}
