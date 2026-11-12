namespace Syno.TinyTorrent.Shared;

/// <summary>
/// Compares folders as text: two paths name the same folder when they are
/// equal after <c>/</c> becomes <c>\</c> and a trailing separator is removed,
/// ignoring case. It reads no disk, because <see cref="Path.GetFullPath(string)"/>
/// reads one to expand a name with <c>~</c>, and callers compare on the UI
/// thread, where a sleeping drive would hold the window.
/// </summary>
internal sealed class FolderComparer : IEqualityComparer<string>
{
    internal static FolderComparer Instance { get; } = new();

    private FolderComparer() { }

    public bool Equals(string? first, string? second) =>
        first is null || second is null
            ? first is null && second is null
            : string.Equals(Key(first), Key(second), StringComparison.OrdinalIgnoreCase);

    public int GetHashCode(string path) => StringComparer.OrdinalIgnoreCase.GetHashCode(Key(path));

    /// <summary>
    /// Whether <paramref name="path"/> lies below <paramref name="folder"/>, at
    /// any depth. A folder does not lie inside itself.
    /// </summary>
    internal bool Contains(string folder, string path)
    {
        var parent = Key(folder);
        var child = Key(path);
        // A root such as C:\ keeps its separator.
        var length = Path.EndsInDirectorySeparator(parent) ? parent.Length : parent.Length + 1;
        return child.Length > length
            && child.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
            && Path.EndsInDirectorySeparator(child.AsSpan(0, length));
    }

    private static string Key(string path) =>
        Path.TrimEndingDirectorySeparator(path.Replace('/', '\\'));
}
