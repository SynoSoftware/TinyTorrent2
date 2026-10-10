using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Syno.TinyTorrent.Subtitles;

internal sealed partial class Acquisition
{
    private static byte[] Protect(string secret) => Protect(Encoding.UTF8.GetBytes(secret), true);
    private static string Unprotect(byte[] secret)
    {
        var bytes = Protect(secret, false);
        try { return Encoding.UTF8.GetString(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static byte[] Protect(byte[] value, bool encrypt)
    {
        var input = new SecretBuffer { Length = value.Length, Data = Marshal.AllocHGlobal(value.Length) };
        try
        {
            Marshal.Copy(value, 0, input.Data, value.Length);
            SecretBuffer output;
            var success = encrypt
                ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success)
                throw new CryptographicException(Marshal.GetLastWin32Error());
            try
            {
                var bytes = new byte[output.Length];
                Marshal.Copy(output.Data, bytes, 0, bytes.Length);
                return bytes;
            }
            finally
            {
                Marshal.Copy(new byte[output.Length], 0, output.Data, output.Length);
                LocalFree(output.Data);
            }
        }
        finally
        {
            Marshal.Copy(new byte[input.Length], 0, input.Data, input.Length);
            Marshal.FreeHGlobal(input.Data);
            if (encrypt)
                CryptographicOperations.ZeroMemory(value);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecretBuffer
    {
        internal int Length;
        internal IntPtr Data;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref SecretBuffer input, string? description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out SecretBuffer output);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref SecretBuffer input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out SecretBuffer output);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
