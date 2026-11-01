namespace Syno.TableView;

internal sealed class PositionIndex(IIdentity identity) : IIdentityKeys
{
    internal IdentityMap<int> Map { get; } = identity.CreateMap<int>(keepsKeys: true);
    public IIdentityKeys Captured => Map;
    internal IReadOnlyList<object> Rows { get; private set; } = Array.Empty<object>();
    public bool ContainsReference(object item) => Map.ContainsReference(item);

    internal void Build(IReadOnlyList<object> rows, IIdentityKeys keys, IIdentityKeys? held = null)
    {
        Map.Clear();
        for (int index = 0; index < rows.Count; index++)
        {
            object item = rows[index];
            IIdentityKeys captured = keys;
            if (!keys.ContainsReference(item) && held is PositionIndex previous)
            {
                captured = previous.Map;
            }
            Map.TryAdd(item, index, captured);
        }
        Rows = rows;
    }

    internal int IndexOf(object? item) =>
        item is not null && Map.TryGetValue(item, out int index) ? index : -1;

    internal int IndexOf(object item, IIdentityKeys keys) =>
        Map.TryGetValue(item, keys, out int index) ? index : -1;

    internal int IndexOfKnown(object item, PositionIndex previous) =>
        IndexOf(item, Map.ContainsReference(item) ? Map : previous.Map);

    internal void Bind(IReadOnlyList<object> rows) => Rows = rows;

    internal object? Resolve(object item)
    {
        int index = IndexOf(item);
        return index < 0 ? null : Rows[index];
    }
}
