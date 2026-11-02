using System.Runtime.InteropServices;

namespace Syno.TinyTorrent;

/// <summary>
/// CPU cycles the current thread has used, which capture timings report
/// beside milliseconds, because other work on the machine stretches
/// milliseconds but not a thread's own cycles.
/// </summary>
internal static class CaptureCycles
{
    internal static ulong Current() => QueryThreadCycleTime(GetCurrentThread(), out var cycles) ? cycles : 0;

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr GetCurrentThread();
}
