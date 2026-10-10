using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;

namespace Syno.TinyTorrent.Services;

internal sealed partial class ProviderHttp
{
    private static async Task<IPAddress[]> Resolve(string host, uint index, CancellationToken cancellation)
    {
        var addresses = new List<IPAddress>();
        foreach (var type in new ushort[] { 1, 28 })
        {
            cancellation.ThrowIfCancellationRequested();
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            DnsCompletion callback = (_, _) => completed.TrySetResult();
            var name = Marshal.StringToHGlobalUni(host);
            var result = Marshal.AllocHGlobal(Marshal.SizeOf<DnsResult>());
            var cancel = Marshal.AllocHGlobal(32);
            try
            {
                Marshal.StructureToPtr(new DnsResult { Version = 1 }, result, false);
                Marshal.Copy(new byte[32], 0, cancel, 32);
                var request = new DnsRequest
                {
                    Version = 1,
                    Name = name,
                    Type = type,
                    Options = 0x100 | 0x800 | 0x1000,
                    Interface = index,
                    Completion = Marshal.GetFunctionPointerForDelegate(callback),
                };
                var status = DnsQueryEx(ref request, result, cancel);
                if (status == 9506)
                {
                    using var registration = cancellation.Register(() => DnsCancelQuery(cancel));
                    await completed.Task.ConfigureAwait(false);
                }
                var answer = Marshal.PtrToStructure<DnsResult>(result);
                try
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (answer.Status is not (0 or 9501 or 9003))
                        throw new Win32Exception(answer.Status);
                    for (var current = answer.Records; current != IntPtr.Zero;)
                    {
                        var record = Marshal.PtrToStructure<DnsRecord>(current);
                        if (record.Type == type && record.Length >= (type == 1 ? 4 : 16))
                        {
                            var bytes = new byte[type == 1 ? 4 : 16];
                            Marshal.Copy(current + Marshal.SizeOf<DnsRecord>(), bytes, 0, bytes.Length);
                            addresses.Add(new IPAddress(bytes));
                        }
                        current = record.Next;
                    }
                }
                finally
                {
                    if (answer.Records != IntPtr.Zero)
                        DnsRecordListFree(answer.Records, 1);
                }
                if (status != 0 && status != 9506 && answer.Status == 0)
                    throw new Win32Exception(status);
            }
            finally
            {
                GC.KeepAlive(callback);
                Marshal.FreeHGlobal(cancel);
                Marshal.FreeHGlobal(result);
                Marshal.FreeHGlobal(name);
            }
        }
        return addresses.ToArray();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DnsRequest
    {
        internal uint Version;
        internal IntPtr Name;
        internal ushort Type;
        internal ulong Options;
        internal IntPtr Servers;
        internal uint Interface;
        internal IntPtr Completion;
        internal IntPtr Context;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DnsResult
    {
        internal uint Version;
        internal int Status;
        internal ulong Options;
        internal IntPtr Records;
        internal IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DnsRecord
    {
        internal IntPtr Next;
        internal IntPtr Name;
        internal ushort Type;
        internal ushort Length;
        internal uint Flags;
        internal uint Ttl;
        internal uint Reserved;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void DnsCompletion(IntPtr context, IntPtr result);

    [DllImport("dnsapi.dll", ExactSpelling = true)]
    private static extern int DnsQueryEx(ref DnsRequest request, IntPtr result, IntPtr cancel);

    [DllImport("dnsapi.dll", ExactSpelling = true)]
    private static extern int DnsCancelQuery(IntPtr cancel);

    [DllImport("dnsapi.dll", ExactSpelling = true)]
    private static extern void DnsRecordListFree(IntPtr records, int freeType);
}
