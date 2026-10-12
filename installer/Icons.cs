using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Syno.TinyTorrent.Installer
{
    public static class Icons
    {
        public static void Check(string executable, string icon)
        {
            byte[] expected = File.ReadAllBytes(icon);
            IntPtr module = LoadLibraryEx(executable, IntPtr.Zero, 0x22);
            if (module == IntPtr.Zero)
                throw new Win32Exception();
            try
            {
                bool matches = false;
                EnumResourceNames(
                    module,
                    new IntPtr(14),
                    delegate(IntPtr library, IntPtr type, IntPtr name, IntPtr parameter)
                    {
                        byte[] group = Read(library, type, name);
                        int count = BitConverter.ToUInt16(group, 4);
                        if (count != BitConverter.ToUInt16(expected, 4))
                            return true;
                        for (int index = 0; index < count; ++index)
                        {
                            int actualEntry = 6 + index * 14;
                            int expectedEntry = 6 + index * 16;
                            if (
                                !group
                                    .Skip(actualEntry)
                                    .Take(12)
                                    .SequenceEqual(expected.Skip(expectedEntry).Take(12))
                            )
                                return true;
                            byte[] image = Read(
                                library,
                                new IntPtr(3),
                                new IntPtr(BitConverter.ToUInt16(group, actualEntry + 12))
                            );
                            int offset = BitConverter.ToInt32(expected, expectedEntry + 12);
                            int length = BitConverter.ToInt32(expected, expectedEntry + 8);
                            if (!image.SequenceEqual(expected.Skip(offset).Take(length)))
                                return true;
                        }
                        matches = true;
                        return false;
                    },
                    IntPtr.Zero
                );
                if (!matches)
                    throw new InvalidDataException(
                        executable + " does not contain the canonical TinyTorrent icon."
                    );
            }
            finally
            {
                FreeLibrary(module);
            }
        }

        private static byte[] Read(IntPtr module, IntPtr type, IntPtr name)
        {
            IntPtr resource = FindResource(module, name, type);
            if (resource == IntPtr.Zero)
                throw new Win32Exception();
            byte[] bytes = new byte[SizeofResource(module, resource)];
            IntPtr data = LockResource(LoadResource(module, resource));
            if (data == IntPtr.Zero)
                throw new Win32Exception();
            Marshal.Copy(data, bytes, 0, bytes.Length);
            return bytes;
        }

        private delegate bool ResourceName(
            IntPtr module,
            IntPtr type,
            IntPtr name,
            IntPtr parameter
        );

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool EnumResourceNames(
            IntPtr module,
            IntPtr type,
            ResourceName callback,
            IntPtr parameter
        );

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SizeofResource(IntPtr module, IntPtr resource);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LockResource(IntPtr resource);

        [DllImport("kernel32.dll")]
        private static extern bool FreeLibrary(IntPtr module);
    }
}
