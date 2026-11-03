using System.Collections.Specialized;

namespace Syno.TableView;

internal abstract record Change
{
    internal sealed record Whole(Body.Source.Capture Capture) : Change;
    internal sealed record Collection(Body.Source.Capture Capture,
        NotifyCollectionChangedEventArgs Notification, bool HasPredecessor) : Change;
    internal sealed record Values(IEnumerable<object> Rows) : Change;
    /// <summary>A notification from a hierarchy's child list.</summary>
    internal sealed record Children(Hierarchy.ChildSource Source,
        NotifyCollectionChangedEventArgs Notification, bool HasPredecessor) : Change;
}
