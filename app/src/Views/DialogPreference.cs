using System.ComponentModel;

namespace Syno.TinyTorrent.Views;

public sealed class DialogPreference(Settings settings) : INotifyPropertyChanged
{
    private bool _neverShow;

    public bool NeverShow
    {
        get => _neverShow;
        set
        {
            if (_neverShow == value)
                return;
            _neverShow = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NeverShow)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal Task Save(params Setting[] choices)
    {
        if (!NeverShow)
            return Task.CompletedTask;
        var values = choices
            .Distinct()
            .Where(setting => setting.ConfirmedOn)
            .ToDictionary(setting => setting.Name, _ => false);
        return values.Count > 0 ? settings.Save(values) : Task.CompletedTask;
    }
}
