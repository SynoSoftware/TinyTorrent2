using System.ComponentModel;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent.Models;

/// <summary>
/// Published data that the person downloads and keeps on this computer as a
/// replaceable copy, as Settings shows it: the IMDb data and the city data.
/// The owner raises <see cref="INotifyPropertyChanged.PropertyChanged"/> for
/// <see cref="Installed"/>, <see cref="DataWork"/> and <see cref="CanDownload"/>,
/// and reports a failed download or deletion itself.
/// </summary>
internal interface IDataset : INotifyPropertyChanged
{
    /// <summary>The text section whose keys name the data, its row and its dialogs.</summary>
    string Section { get; }

    /// <summary>The installed data's file, or null when it is missing.</summary>
    FileInfo? Installed { get; }

    /// <summary>Whether downloading again would most likely transfer the same file.</summary>
    bool IsCurrent { get; }

    /// <summary>The number of records the row names, or null when the data has no count a person reads.</summary>
    Task<long>? Count { get; }

    /// <summary>The running download, or null.</summary>
    BackgroundWork.Operation? DataWork { get; }

    bool CanDownload { get; }

    /// <summary>Downloads and installs the data; returns whether the new data is installed.</summary>
    Task<bool> Download();

    /// <summary>Deletes the installed data.</summary>
    Task Delete();
}
