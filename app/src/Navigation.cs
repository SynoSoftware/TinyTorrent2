using System.ComponentModel;
using Syno.TinyTorrent.Models;

namespace Syno.TinyTorrent;

internal sealed class Navigation(Func<WindowPage, Task<bool>> prepare) : INotifyPropertyChanged
{
    private readonly List<WindowPage> _history = [WindowPage.Torrents];
    private bool _pending;

    internal WindowPage Current => _history[^1];
    internal WindowPage Page => Current == WindowPage.Connection ? WindowPage.Settings : Current;
    internal WindowPage Workspace => _history.Last(IsWorkspace);
    internal bool HasBack => _history.Count > 1;
    internal bool CanGoBack => HasBack && !_pending;
    public event PropertyChangedEventHandler? PropertyChanged;

    internal Task<bool> Navigate(WindowPage page) =>
        IsWorkspace(page) ? Change(page, 0) : Open(page);

    internal Task<bool> Open(WindowPage page)
    {
        var index = _history.LastIndexOf(page);
        return Change(page, index < 0 ? _history.Count : index);
    }

    internal Task<bool> GoBack() =>
        HasBack ? Change(_history[^2], _history.Count - 2) : Task.FromResult(false);

    private async Task<bool> Change(WindowPage page, int retained)
    {
        if (_pending)
            return false;
        _pending = true;
        Changed(nameof(CanGoBack));
        try
        {
            if (!await prepare(page))
                return false;
            if (Current == page && retained == _history.Count - 1)
                return true;
            _history.RemoveRange(retained, _history.Count - retained);
            _history.Add(page);
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
