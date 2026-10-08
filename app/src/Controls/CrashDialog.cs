using System.Runtime.InteropServices;
using System.Text;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent.Controls;

// Uses native controls because the failure may be in XAML itself.
internal static class CrashDialog
{
    private const int CopyButton = 100;
    private const uint SetElementText = 0x046c;

    internal static void Show(ExceptionReport report, Strings? text, IntPtr owner)
    {
        // Startup can fail before the language catalogue loads.
        var explanation = text?.Get("crash", "message") ??
            "The TinyTorrent window encountered an unexpected error and must close. Reopen TinyTorrent to restore the window. The download engine runs separately.";
        var location = report.Path is { } path
            ? text?.Format("crash", "saved", path) ?? $"Log saved to:\n{path}"
            : text?.Get("crash", "unsaved") ?? "The log could not be saved. You can still copy it.";
        var copied = text?.Get("crash", "copied") ?? "Log copied.";
        var failed = text?.Get("crash", "copy_failed") ?? "The log could not be copied. Try again.";
        DialogCallback callback = (window, notification, button, _, _) =>
        {
            if (notification != 2 || button != CopyButton) return 0; // TDN_BUTTON_CLICKED
            SendMessageW(window, SetElementText, 2, CopyLog(window, report.Text) ? copied : failed);
            return 1; // S_FALSE keeps the dialog open after copying.
        };
        var buttonText = IntPtr.Zero;
        var buttons = IntPtr.Zero;
        try
        {
            buttonText = Marshal.StringToHGlobalUni(text?.Get("crash", "copy") ?? "Copy log");
            buttons = Marshal.AllocHGlobal(Marshal.SizeOf<NativeButton>());
            Marshal.StructureToPtr(new NativeButton { Id = CopyButton, Text = buttonText }, buttons, false);
            var configuration = new Configuration
            {
                Size = (uint)Marshal.SizeOf<Configuration>(),
                Owner = owner,
                Flags = 0x1008, // TDF_POSITION_RELATIVE_TO_WINDOW | TDF_ALLOW_DIALOG_CANCELLATION
                CommonButtons = 0x0020, // TDCBF_CLOSE_BUTTON
                Title = "TinyTorrent",
                Icon = new IntPtr(65534), // TD_ERROR_ICON
                Instruction = text?.Get("crash", "title") ?? "TinyTorrent must close",
                Content = explanation + "\n\n" + location,
                ButtonCount = 1,
                Buttons = buttons,
                DefaultButton = 8, // IDCLOSE
                Footer = text?.Get("crash", "copy_hint") ?? "Copy the log to include it when reporting the problem.",
                Callback = callback
            };
            Marshal.ThrowExceptionForHR(TaskDialogIndirect(in configuration, out _, IntPtr.Zero, IntPtr.Zero));
        }
        finally
        {
            Marshal.FreeHGlobal(buttons);
            Marshal.FreeHGlobal(buttonText);
            GC.KeepAlive(callback);
        }
    }

    private static bool CopyLog(IntPtr owner, string text)
    {
        var memory = IntPtr.Zero;
        try
        {
            var bytes = Encoding.Unicode.GetBytes(text + '\0');
            memory = GlobalAlloc(0x0002, (nuint)bytes.Length); // GMEM_MOVEABLE
            if (memory == IntPtr.Zero) return false;
            var address = GlobalLock(memory);
            if (address == IntPtr.Zero) return false;
            try { Marshal.Copy(bytes, 0, address, bytes.Length); }
            finally { GlobalUnlock(memory); }
            if (!OpenClipboard(owner)) return false;
            try
            {
                if (!EmptyClipboard() || SetClipboardData(13, memory) == IntPtr.Zero) return false; // CF_UNICODETEXT
                // Windows owns the memory now, so the log survives this process exiting.
                memory = IntPtr.Zero;
                return true;
            }
            finally { CloseClipboard(); }
        }
        catch (Exception) { return false; }
        finally { if (memory != IntPtr.Zero) GlobalFree(memory); }
    }

    private delegate int DialogCallback(IntPtr window, uint notification, nuint parameter, nint data, nint reference);

    // Task dialog structures use one-byte packing in CommCtrl.h.
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct NativeButton
    {
        public int Id;
        public IntPtr Text;
    }

#pragma warning disable CS0649 // Unused native fields must retain their positions in the structures.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
    private struct Configuration
    {
        public uint Size;
        public IntPtr Owner;
        public IntPtr Instance;
        public uint Flags;
        public uint CommonButtons;
        [MarshalAs(UnmanagedType.LPWStr)] public string Title;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.LPWStr)] public string Instruction;
        [MarshalAs(UnmanagedType.LPWStr)] public string Content;
        public uint ButtonCount;
        public IntPtr Buttons;
        public int DefaultButton;
        public uint RadioCount;
        public IntPtr RadioButtons;
        public int DefaultRadio;
        public IntPtr Verification;
        public IntPtr Expanded;
        public IntPtr ExpandText;
        public IntPtr CollapseText;
        public IntPtr FooterIcon;
        [MarshalAs(UnmanagedType.LPWStr)] public string Footer;
        public DialogCallback Callback;
        public nint Reference;
        public uint Width;
    }

#pragma warning restore CS0649

    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern int TaskDialogIndirect(in Configuration configuration, out int button, IntPtr radio, IntPtr verified);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint SendMessageW(IntPtr window, uint message, nuint parameter, string text);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memory);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr GlobalFree(IntPtr memory);
}
