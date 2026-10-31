using System.Runtime.InteropServices;

namespace Syno.TinyTorrent.Shared.Desktop;

internal abstract record DiskSpace
{
    internal sealed record Available(long Bytes) : DiskSpace;
    internal sealed record Unavailable : DiskSpace;

    internal static DiskSpace? Read(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path))
                return null;
            var folder = Path.GetFullPath(path);
            while (true)
            {
                if (GetDiskFreeSpaceExW(Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar,
                    out var free, out _, out _))
                    return new Available((long)Math.Min(free, long.MaxValue));
                var error = Marshal.GetLastWin32Error();
                // A new folder uses its nearest existing parent's space.
                if (error is 2 or 3 && GetFileAttributesW(folder) != uint.MaxValue)
                    return new Unavailable();
                var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(folder));
                if (error is 2 or 3 && !string.IsNullOrEmpty(parent))
                {
                    folder = parent;
                    continue;
                }
                return error is 2 or 3 or 15 or 21 or 53 or 55 or 64 or 67 or 121 or 1167 or 1231 or 1232 or 2250
                    ? new Unavailable()
                    : null;
            }
        }
        catch (Exception error)
            when (error is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExW(string directory, out ulong available, out ulong total, out ulong free);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetFileAttributesW(string path);
}
