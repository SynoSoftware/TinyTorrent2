using System.ComponentModel;

namespace Syno.TinyTorrent.Shell;

internal sealed class Navigation(Func<WindowPage, Task<bool>> prepare) : INotifyPropertyChanged
{
    private sealed record Destination(
        WindowPage Page,
        SettingTarget? Settings = null,
        Action? Restore = null
    );

    private readonly List<Destination> _history = [new(WindowPage.Torrents)];
    private bool _pending;

    internal WindowPage Current => _history[^1].Page;
    internal SettingTarget? Settings => _history[^1].Settings;
    internal WindowPage Page => Current == WindowPage.Connection ? WindowPage.Settings : Current;
    internal WindowPage Workspace => _history.Last(item => IsWorkspace(item.Page)).Page;
    internal bool HasBack => _history.Count > 1;
    internal bool CanGoBack => HasBack && !_pending;
    public event PropertyChangedEventHandler? PropertyChanged;

    internal Task<bool> Navigate(WindowPage page) =>
        IsWorkspace(page) ? Change(new(page), 0) : Open(page);

    internal Task<bool> Navigate(SettingTarget target) =>
        Open(new Destination(WindowPage.Settings, target));

    internal Task<bool> SelectCategory(SettingsCategory? category)
    {
        var index = _history.FindIndex(item => item.Page == WindowPage.Settings);
        return Change(new(WindowPage.Settings, new(category)), index < 0 ? _history.Count : index);
    }

    internal Task<bool> Open(WindowPage page) => Open(new Destination(page));

    internal Task<bool> Visit(WindowPage page, Action restore) =>
        Change(new(page, Restore: restore), _history.Count);

    internal void KeepWorkspace()
    {
        var page = Current;
        _history.Clear();
        _history.Add(new(page));
        Changed(nameof(CanGoBack));
    }

    private Task<bool> Open(Destination destination)
    {
        var index = _history.LastIndexOf(destination);
        return Change(destination, index < 0 ? _history.Count : index);
    }

    internal Task<bool> GoBack() =>
        HasBack ? Change(_history[^2], _history.Count - 2) : Task.FromResult(false);

    private async Task<bool> Change(Destination destination, int retained)
    {
        if (_pending)
            return false;
        _pending = true;
        Changed(nameof(CanGoBack));
        try
        {
            if (!await prepare(destination.Page))
                return false;
            if (_history[^1] == destination && retained == _history.Count - 1)
                return true;
            var removed = _history.Skip(retained).Reverse().ToArray();
            _history.RemoveRange(retained, _history.Count - retained);
            _history.Add(destination);
            foreach (var item in removed)
                if (item != destination)
                    item.Restore?.Invoke();
            Changed(nameof(Current));
            return true;
        }
        finally
        {
            _pending = false;
            Changed(nameof(CanGoBack));
        }
    }

    private static bool IsWorkspace(WindowPage page) =>
        page is WindowPage.Torrents or WindowPage.Library;

    private void Changed(string property) => PropertyChanged?.Invoke(this, new(property));
}
