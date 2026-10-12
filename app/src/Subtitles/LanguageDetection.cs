using System.Runtime.InteropServices;
using System.Text;

namespace Syno.TinyTorrent.Subtitles;

internal static class LanguageDetection
{
    internal static string? Detect(string text)
    {
        if (text.Count(char.IsLetter) < 160)
            return null;
        var guid = Marshal.AllocHGlobal(Marshal.SizeOf<Guid>());
        try
        {
            Marshal.StructureToPtr(new Guid("CF7E00B1-909B-4D95-A8F4-611F7C377702"), guid, false);
            var options = new Enumeration
            {
                Size = (nuint)Marshal.SizeOf<Enumeration>(),
                Guid = guid,
            };
            if (MappingGetServices(ref options, out var services, out var count) < 0)
                return null;
            try
            {
                if (count == 0)
                    return null;
                text = text.Normalize(NormalizationForm.FormC);
                var language = Recognize(services, text);
                var middle = text.Length / 2;
                if (char.IsLowSurrogate(text[middle]))
                    middle++;
                // ELS gives no scores; agreement across separate samples avoids
                // treating its first answer for short or mixed text as certain.
                return
                    language is not null
                    && language == Recognize(services, text[..middle])
                    && language == Recognize(services, text[middle..])
                    ? language
                    : null;
            }
            finally
            {
                MappingFreeServices(services);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(guid);
        }
    }

    private static string? Recognize(nint service, string text)
    {
        var bag = new PropertyBag { Size = (nuint)Marshal.SizeOf<PropertyBag>() };
        if (MappingRecognizeText(service, text, (uint)text.Length, 0, 0, ref bag) < 0)
            return null;
        try
        {
            return bag.Count == 0
                ? null
                : Marshal.PtrToStringUni(Marshal.PtrToStructure<DataRange>(bag.Ranges).Data);
        }
        finally
        {
            MappingFreePropertyBag(ref bag);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Enumeration
    {
        internal nuint Size;
        internal nint Category,
            InputLanguage,
            OutputLanguage,
            InputScript,
            OutputScript,
            InputContent,
            OutputContent,
            Guid;
        internal uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyBag
    {
        internal nuint Size;
        internal nint Ranges;
        internal uint Count;
        internal nint ServiceData;
        internal uint ServiceBytes;
        internal nint CallerData;
        internal uint CallerBytes;
        internal nint Context;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataRange
    {
        internal uint Start,
            End;
        internal nint Description;
        internal uint DescriptionLength;
        internal nint Data;
        internal uint Bytes;
        internal nint ContentType,
            Actions;
        internal uint ActionCount;
        internal nint ActionNames;
    }

    [DllImport("elscore.dll", ExactSpelling = true)]
    private static extern int MappingGetServices(
        ref Enumeration options,
        out nint services,
        out uint count
    );

    [DllImport("elscore.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int MappingRecognizeText(
        nint service,
        string text,
        uint length,
        uint index,
        nint options,
        ref PropertyBag bag
    );

    [DllImport("elscore.dll", ExactSpelling = true)]
    private static extern int MappingFreePropertyBag(ref PropertyBag bag);

    [DllImport("elscore.dll", ExactSpelling = true)]
    private static extern int MappingFreeServices(nint services);
}
