using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace Syno.TinyTorrent.Shared;

// A file's Authenticode signature, which Windows shows as its publisher.
internal static class Authenticode
{
    private const uint NoInterface = 2;
    private const uint NoRevocationCheck = 0;
    private const uint FileChoice = 1;
    private const uint Verify = 1;
    private const uint Close = 2;
    private const uint CacheOnly = 0x1000;
    private const uint SkipRevocation = 0x10;
    private static readonly Guid GenericVerify = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    /// <summary>
    /// The subject of the certificate that signed <paramref name="path"/>, or
    /// null when the file is unsigned or Windows does not trust its signature.
    /// It checks no revocation and fetches no certificate, because Windows
    /// would send those requests outside the saved proxy and network adapter.
    /// </summary>
    internal static string? TrustedPublisher(string path)
    {
        var name = Marshal.StringToHGlobalUni(path);
        var file = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = name };
        var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        Marshal.StructureToPtr(file, filePointer, false);
        var data = new TrustData
        {
            Size = (uint)Marshal.SizeOf<TrustData>(),
            Interface = NoInterface,
            Revocation = NoRevocationCheck,
            Choice = FileChoice,
            File = filePointer,
            Action = Verify,
            Flags = CacheOnly | SkipRevocation,
        };
        var action = GenericVerify;
        try
        {
            if (WinVerifyTrust(-1, ref action, ref data) != 0)
                return null;
            var signer = WTHelperGetProvSignerFromChain(
                WTHelperProvDataFromStateData(data.State),
                0,
                false,
                0
            );
            var certificate = signer == 0 ? 0 : WTHelperGetProvCertFromChain(signer, 0);
            if (certificate == 0)
                return null;
            // CRYPT_PROVIDER_CERT holds its size, then the certificate context.
            using var context = new X509Certificate2(Marshal.ReadIntPtr(certificate, IntPtr.Size));
            return context.Subject;
        }
        finally
        {
            data.Action = Close;
            WinVerifyTrust(-1, ref action, ref data);
            Marshal.FreeHGlobal(filePointer);
            Marshal.FreeHGlobal(name);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(nint window, ref Guid action, ref TrustData data);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern nint WTHelperProvDataFromStateData(nint state);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern nint WTHelperGetProvSignerFromChain(
        nint provider,
        uint signer,
        [MarshalAs(UnmanagedType.Bool)] bool counterSigner,
        uint counterSignerIndex
    );

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern nint WTHelperGetProvCertFromChain(nint signer, uint certificate);

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustFile
    {
        public uint Size;
        public nint Path;
        public nint Handle;
        public nint Subject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size;
        public nint PolicyData;
        public nint ClientData;
        public uint Interface;
        public uint Revocation;
        public uint Choice;
        public nint File;
        public uint Action;
        public nint State;
        public nint Reference;
        public uint Flags;
        public uint Context;
        public nint Settings;
    }
}
