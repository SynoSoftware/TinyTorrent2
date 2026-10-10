using System.Runtime.InteropServices;
using Syno.TinyTorrent.Helpers;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace Syno.TinyTorrent;

public sealed partial class MainWindow
{
    private readonly SemaphoreSlim _associationAccess = new(1, 1);

    // Call on the window dispatcher: the returned icon belongs to that thread.
    internal async Task<FileAssociation> ReadAssociation(string extension, CancellationToken cancellation)
    {
        if (string.IsNullOrEmpty(extension))
            return new(string.Empty, string.Empty, null);
        await _associationAccess.WaitAsync(cancellation);
        try
        {
            var association = await Task.Run(() => QueryAssociation(extension, cancellation), cancellation);
            using var stream = association.Icon;
            cancellation.ThrowIfCancellationRequested();
            if (stream is null)
                return new(association.Type, association.Application, null);
            var icon = new BitmapImage();
            try
            {
                await icon.SetSourceAsync(stream).AsTask(cancellation);
                return new(association.Type, association.Application, icon);
            }
            catch (Exception error) when (error is COMException or IOException or ArgumentException)
            {
                return new(association.Type, association.Application, null);
            }
        }
        finally { _associationAccess.Release(); }
    }

    private static async Task<(string Type, string Application, IRandomAccessStream? Icon)> QueryAssociation(
        string extension, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var type = ShellAssociation.Get(extension, ShellAssociation.FriendlyDocument);
        var application = ShellAssociation.Get(extension, ShellAssociation.FriendlyApplication);
        var appId = ShellAssociation.Get(extension, ShellAssociation.ApplicationId);
        if (appId.Length > 0 && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            try
            {
                var display = AppInfo.GetFromAppUserModelId(appId).DisplayInfo;
                application = display.DisplayName;
                var logo = await display.GetLogo(new Size(32, 32)).OpenReadAsync().AsTask(cancellation).ConfigureAwait(false);
                return (type, application, logo);
            }
            catch (Exception error) when (error is COMException or IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        var executable = ShellAssociation.Get(extension, ShellAssociation.Executable);
        if (executable.Length > 0)
        {
            try
            {
                // Only the associated application's executable is inspected, never the payload.
                var file = await StorageFile.GetFileFromPathAsync(executable).AsTask(cancellation).ConfigureAwait(false);
                var icon = await file.GetThumbnailAsync(ThumbnailMode.ListView, 32).AsTask(cancellation).ConfigureAwait(false);
                return (type, application, icon);
            }
            catch (Exception error) when (error is COMException or IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return (type, application, null);
    }

}

internal sealed record FileAssociation(string Type, string Application, ImageSource? Icon);
