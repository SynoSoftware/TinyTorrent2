namespace Syno.TinyTorrent.Shared.Desktop;

internal static class DiskSpace
{
    internal static long? Read(string path)
    {
        try
        {
            // DriveInfo takes only drive letters; a path such as
            // \\server\share is a network share, which counts as unknown below.
            if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
                return null;
            var drive = new DriveInfo(path);
            // A disconnected network drive can block its caller for seconds.
            if (drive.DriveType == DriveType.Network || !drive.IsReady)
                return null;
            return drive.AvailableFreeSpace;
        }
        catch (Exception error)
            when (error is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
