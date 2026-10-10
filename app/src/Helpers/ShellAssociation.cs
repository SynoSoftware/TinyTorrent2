using System.Runtime.InteropServices;
using System.Text;

namespace Syno.TinyTorrent.Helpers;

internal static class ShellAssociation
{
    internal const uint Executable = 2;
    internal const uint FriendlyDocument = 3;
    internal const uint FriendlyApplication = 4;
    internal const uint ApplicationId = 21;

    internal static string Get(string association, uint kind)
    {
        uint length = 0;
        if (AssocQueryStringW(0, kind, association, "open", null, ref length) < 0 || length == 0)
            return string.Empty;
        var value = new StringBuilder(checked((int)length));
        return AssocQueryStringW(0, kind, association, "open", value, ref length) == 0
            ? value.ToString() : string.Empty;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int AssocQueryStringW(uint flags, uint kind, string association,
        string extra, StringBuilder? value, ref uint length);
}
