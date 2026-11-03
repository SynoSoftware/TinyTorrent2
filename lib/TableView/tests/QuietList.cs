using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace Syno.TableView.Tests;

/// <summary>A notifying list that can also change without a notification, then raise Reset.</summary>
internal sealed class QuietList<T> : ObservableCollection<T>
{
    internal QuietList()
    {
    }

    internal QuietList(IEnumerable<T> items)
        : base(items)
    {
    }

    internal void InsertQuietly(int index, T item) => Items.Insert(index, item);

    internal void RemoveQuietly(int index) => Items.RemoveAt(index);

    internal void RaiseReset() =>
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
}
