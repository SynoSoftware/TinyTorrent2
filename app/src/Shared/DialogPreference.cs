using System.ComponentModel;
using Syno.TinyTorrent.Settings;

namespace Syno.TinyTorrent.Shared;

public sealed class DialogPreference(Editor settings) : INotifyPropertyChanged
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
