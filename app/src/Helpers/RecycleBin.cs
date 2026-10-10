using System.Runtime.InteropServices;

namespace Syno.TinyTorrent.Helpers;

internal static class RecycleBin
{
    internal static Task Delete(string path, CancellationToken cancellation)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                cancellation.ThrowIfCancellationRequested();
                Recycle(path, cancellation);
                completion.SetResult();
            }
            catch (OperationCanceledException) { completion.SetCanceled(cancellation); }
            catch (Exception error) { completion.SetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void Recycle(string path, CancellationToken cancellation)
    {
        var apartment = CoInitializeEx(IntPtr.Zero, 2);
        IFileOperation? operation = null;
        var item = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(apartment);
            var classId = new Guid("3AD05575-8857-4850-9277-11B85BDB8E09");
            var interfaceId = typeof(IFileOperation).GUID;
            Marshal.ThrowExceptionForHR(CoCreateInstance(ref classId, IntPtr.Zero, 1, ref interfaceId, out operation));
            Marshal.ThrowExceptionForHR(operation.SetOperationFlags(OperationFlags.RecycleOnDelete | OperationFlags.EarlyFailure |
                OperationFlags.NoErrorUI | OperationFlags.Silent | OperationFlags.NoConfirmation | OperationFlags.NoConnectedElements));
            var itemId = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref itemId, out item));
            Marshal.ThrowExceptionForHR(operation.DeleteItem(item, IntPtr.Zero));
            cancellation.ThrowIfCancellationRequested();
            Marshal.ThrowExceptionForHR(operation.PerformOperations());
            Marshal.ThrowExceptionForHR(operation.GetAnyOperationsAborted(out var aborted));
            if (aborted)
                throw new IOException("The file could not be recycled.");
        }
        catch (COMException error) { throw new IOException("The file could not be recycled.", error); }
        finally
        {
            if (item != IntPtr.Zero)
                Marshal.Release(item);
            if (operation is not null)
                Marshal.FinalReleaseComObject(operation);
            if (apartment >= 0)
                CoUninitialize();
        }
    }

    [Flags]
    private enum OperationFlags : uint
    {
        Silent = 0x4,
        NoConfirmation = 0x10,
        NoConnectedElements = 0x2000,
        NoErrorUI = 0x400,
        RecycleOnDelete = 0x80000,
        EarlyFailure = 0x100000,
    }

    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        [PreserveSig] int Advise(IntPtr sink, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetOperationFlags(OperationFlags flags);
        [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        [PreserveSig] int SetProgressDialog(IntPtr dialog);
        [PreserveSig] int SetProperties(IntPtr properties);
        [PreserveSig] int SetOwnerWindow(IntPtr window);
        [PreserveSig] int ApplyPropertiesToItem(IntPtr item);
        [PreserveSig] int ApplyPropertiesToItems(IntPtr items);
        [PreserveSig] int RenameItem(IntPtr item, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        [PreserveSig] int RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int MoveItem(IntPtr item, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        [PreserveSig] int MoveItems(IntPtr items, IntPtr destination);
        [PreserveSig] int CopyItem(IntPtr item, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        [PreserveSig] int CopyItems(IntPtr items, IntPtr destination);
        [PreserveSig] int DeleteItem(IntPtr item, IntPtr sink);
        [PreserveSig] int DeleteItems(IntPtr items);
        [PreserveSig] int NewItem(IntPtr destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name,
            [MarshalAs(UnmanagedType.LPWStr)] string template, IntPtr sink);
        [PreserveSig] int PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr reserved, uint flags);
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(ref Guid classId, IntPtr outer, uint context, ref Guid interfaceId,
        out IFileOperation operation);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid interfaceId, out IntPtr item);
}
