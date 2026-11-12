namespace Syno.TinyTorrent.Shared;

/// <summary>
/// Published data that the person downloads and keeps on this computer as a
/// replaceable copy: the IMDb data and the city data. A dataset states what
/// is its own: its names, its file, its build and how its reads give way to
/// a replacement. <see cref="DataDownload"/> owns everything else.
/// </summary>
internal interface IDataset
{
    /// <summary>The text section whose keys name the data, its row and its dialogs.</summary>
    string Section { get; }

    /// <summary>The publisher's name, which a failure's text names.</summary>
    string Name { get; }

    /// <summary>The installed file's full path, or null while the dataset serves no folder.</summary>
    string? FilePath { get; }

    /// <summary>The description a download starts with.</summary>
    WorkDescription Downloading { get; }

    /// <summary>The description of the replacement, which cannot be cancelled.</summary>
    WorkDescription Installing { get; }

    /// <summary>The number of records the row names, or null when the data has no count a person reads.</summary>
    Task<long>? Count { get; }

    /// <summary>
    /// Files beside the data that only a build or an earlier version of the
    /// app used. They are deleted after an installation and with the data.
    /// </summary>
    IReadOnlyList<string> Leftovers { get; }

    /// <summary>Whether a file written at <paramref name="written"/> is most likely the one a new download would transfer.</summary>
    bool IsCurrent(DateTime written);

    /// <summary>
    /// Fetches the data and writes the complete file to <paramref name="path"/>,
    /// which is not the installed file. A cancel can end it with the error
    /// that the interruption caused instead of a cancellation.
    /// </summary>
    Task Build(string path, Action<WorkDescription, WorkProgress> report, CancellationToken cancellation);

    /// <summary>
    /// Runs <paramref name="change"/>, which replaces or deletes the installed
    /// file, while no read runs and the dataset holds the file closed. The
    /// dataset then serves the file that remains, also when the change failed.
    /// </summary>
    void Replace(Action change);
}
