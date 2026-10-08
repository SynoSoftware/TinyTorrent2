namespace Syno.TinyTorrent.Services;

internal static class DiskSpace
{
    internal static long? Read(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path))
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
