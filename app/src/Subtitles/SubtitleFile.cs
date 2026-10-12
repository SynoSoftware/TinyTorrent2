using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent.Subtitles;

internal static partial class SubtitleFile
{
    private static readonly IReadOnlyDictionary<string, string> LanguageNames = Languages();

    internal static bool IsSubtitle(string path) =>
        Path.GetExtension(path).ToLowerInvariant()
            is ".srt"
                or ".ass"
                or ".ssa"
                or ".vtt"
                or ".sub";

    internal static bool BelongsTo(string subtitle, string movie, string folder, bool sole)
    {
        var title = FileName.Interpret(movie).Name;
        if (
            Names(subtitle, folder)
                .Any(name =>
                    name.StartsWith(title, StringComparison.OrdinalIgnoreCase)
                    && (name.Length == title.Length || ". _-([".Contains(name[title.Length]))
                )
        )
            return true;
        if (!sole)
            return false;
        var name = Path.GetFileNameWithoutExtension(subtitle);
        var separator = name.LastIndexOf('.');
        // A Movie.language name explicitly identifies another movie; the
        // single-movie fallback is for generic subtitle names.
        return separator < 0
            || !LanguageNames.ContainsKey(name[(separator + 1)..])
            || !name[..separator].Any(char.IsLetter);
    }

    internal static async Task<string?> Identify(
        string subtitle,
        string movie,
        string folder,
        IReadOnlySet<string> tags,
        CancellationToken cancellation
    )
    {
        string? Canonical(string? tag)
        {
            if (tag is null)
                return null;
            var culture = CultureInfo.GetCultureInfo(tag);
            while (culture.Name.Length > 0)
            {
                if (tags.Contains(culture.Name))
                    return culture.Name;
                culture = culture.Parent;
            }
            return tag;
        }
        var title = FileName.Interpret(movie).Name;
        var names = Names(subtitle, folder)
            .Select(name =>
                name.StartsWith(title, StringComparison.OrdinalIgnoreCase)
                    ? name[title.Length..]
                    : name
            )
            .ToArray();
        if (names.Any(name => Forced().IsMatch(name)))
            return null;
        var tokens = names
            .SelectMany(name =>
            {
                name = name.Trim(' ', '.', '_', '-');
                return LanguageNames.ContainsKey(name.Replace('_', '-'))
                    ? new[] { name.Replace('_', '-') }
                    : name.Split(
                            ['.', ' ', '_', '(', ')', '[', ']'],
                            StringSplitOptions.RemoveEmptyEntries
                        )
                        .Select(token => token.Trim('-'));
            })
            .ToArray();
        string[] Recognize(IEnumerable<string> values) =>
            values
                .Select(value => LanguageNames.GetValueOrDefault(value))
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        // Movie.en.hi.srt marks an English subtitle for the hearing impaired,
        // so "hi" means Hindi only when no other language appears.
        var languages = Recognize(
            tokens.Where(token =>
                !Supplier.HearingImpaired.Contains(token, StringComparer.OrdinalIgnoreCase)
            )
        );
        if (languages.Length == 0)
            languages = Recognize(tokens);
        if (languages.Length > 0)
            return languages.Length == 1 ? Canonical(languages[0]) : null;
        if (Path.GetExtension(subtitle).Equals(".sub", StringComparison.OrdinalIgnoreCase))
            return null;
        await using var stream = new FileStream(
            subtitle,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            16384,
            FileOptions.Asynchronous | FileOptions.SequentialScan
        );
        var bytes = new byte[Math.Min(stream.Length, 65536)];
        var count = await stream
            .ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, cancellation)
            .ConfigureAwait(false);
        string text;
        int? ansi = null;
        try
        {
            text = Text(bytes.AsSpan(0, count), null, partial: true);
        }
        catch (DecoderFallbackException)
        {
            // Text that is not UTF-8 is read in the system's ANSI code page, as
            // Windows players read such subtitles.
            var encoding = CodePagesEncodingProvider.Instance.GetEncoding(0);
            if (encoding is null)
                return null;
            ansi = encoding.CodePage;
            try
            {
                text = Text(bytes.AsSpan(0, count), encoding.WebName, partial: true);
            }
            catch (DecoderFallbackException)
            {
                return null;
            }
        }
        var dialogue = new StringBuilder();
        var ass = Path.GetExtension(subtitle).ToLowerInvariant() is ".ass" or ".ssa";
        foreach (var line in text.Split('\n'))
        {
            var value = line.Trim();
            if (ass)
            {
                if (!value.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
                    continue;
                value = value.Split(',', 10).ElementAtOrDefault(9) ?? string.Empty;
            }
            else if (
                value.Length == 0
                || value.Contains("-->", StringComparison.Ordinal)
                || value.All(char.IsDigit)
                || value.StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase)
            )
                continue;
            dialogue.AppendLine(Markup().Replace(value, " ").Replace("\\N", " "));
        }
        var detected = LanguageDetection.Detect(dialogue.ToString());
        // A language Windows writes in another code page means the bytes were
        // read in the wrong one, as Russian read as Windows-1252.
        if (
            detected is not null
            && ansi is { } page
            && CultureInfo.GetCultureInfo(detected).TextInfo.ANSICodePage != page
        )
            return null;
        return Canonical(detected);
    }

    private static IEnumerable<string> Names(string subtitle, string folder)
    {
        yield return Path.GetFileNameWithoutExtension(subtitle);
        var parent = Path.GetDirectoryName(subtitle);
        while (
            parent is not null
            && !StringComparer.OrdinalIgnoreCase.Equals(
                Path.TrimEndingDirectorySeparator(parent),
                Path.TrimEndingDirectorySeparator(folder)
            )
        )
        {
            yield return Path.GetFileName(parent);
            parent = Path.GetDirectoryName(parent);
        }
    }

    private static IReadOnlyDictionary<string, string> Languages()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (
            var culture in CultureInfo
                .GetCultures(CultureTypes.NeutralCultures)
                .Where(culture => culture.Name.Length > 0)
        )
        foreach (
            var name in new[]
            {
                culture.Name,
                culture.ThreeLetterISOLanguageName,
                culture.EnglishName,
                culture.NativeName,
            }
        )
            names.TryAdd(name, culture.Name);
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        foreach (var name in new[] { culture.Name, culture.EnglishName, culture.NativeName })
            names.TryAdd(name, culture.Name);
        // Subtitle names also use the ISO 639-2 bibliographic codes, as
        // Movie.ger.srt, which .NET does not list.
        foreach (
            var (code, language) in new[]
            {
                ("alb", "sq"),
                ("arm", "hy"),
                ("baq", "eu"),
                ("bur", "my"),
                ("chi", "zh"),
                ("cze", "cs"),
                ("dut", "nl"),
                ("fre", "fr"),
                ("geo", "ka"),
                ("ger", "de"),
                ("gre", "el"),
                ("ice", "is"),
                ("mac", "mk"),
                ("mao", "mi"),
                ("may", "ms"),
                ("per", "fa"),
                ("rum", "ro"),
                ("slo", "sk"),
                ("tib", "bo"),
                ("wel", "cy"),
            }
        )
            names.TryAdd(code, language);
        return names;
    }

    private const int HashRange = 65536;
    internal const int HashMinimum = 2 * HashRange;

    // Rank 1 is the subtitle a player loads first, Movie.en.srt; an extra
    // guess for the same language is Movie.en.2.srt, and so on.
    internal static string Destination(string video, string language, int rank = 1)
    {
        if (!Language().IsMatch(language))
            throw new ArgumentException(
                "A subtitle language must be a language tag.",
                nameof(language)
            );
        var suffix = rank == 1 ? string.Empty : "." + rank.ToString(CultureInfo.InvariantCulture);
        return Path.ChangeExtension(video, language.ToLowerInvariant() + suffix + ".srt");
    }

    // The rank Destination gave this path; 1 for a path without a rank.
    internal static int Rank(string path, string language)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var marker = "." + language.ToLowerInvariant() + ".";
        var index = name.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return
            index >= 0
            && int.TryParse(
                name.AsSpan(index + marker.Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var rank
            )
            && rank > 1
            ? rank
            : 1;
    }

    internal static bool Exists(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.Directory) == 0;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    internal static async Task<byte[]> Decode(
        byte[] bytes,
        string? charset,
        CancellationToken cancellation
    )
    {
        if (bytes.Length > ProviderHttp.FileLimit)
            throw new InvalidDataException("Subtitle exceeds its size limit.");
        if (bytes.AsSpan().StartsWith("PK"u8))
        {
            using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            var entries = archive
                .Entries.Where(entry =>
                    entry.Name.EndsWith(".srt", StringComparison.OrdinalIgnoreCase)
                )
                .Take(2)
                .ToArray();
            if (entries.Length != 1)
                throw new InvalidDataException("A subtitle archive must contain exactly one SRT.");
            await using var stream = entries[0].Open();
            bytes = await ProviderHttp
                .Read(stream, ProviderHttp.FileLimit, cancellation)
                .ConfigureAwait(false);
        }
        cancellation.ThrowIfCancellationRequested();
        var text = Text(bytes, charset);
        if (text.IndexOf('\0') >= 0 || !Cue().IsMatch(text))
            throw new InvalidDataException("The supplier did not return an SRT subtitle.");
        return bytes;
    }

    private static string Text(ReadOnlySpan<byte> bytes, string? charset, bool partial = false)
    {
        Encoding encoding;
        var offset = 0;
        if (bytes.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
        {
            encoding = new UTF8Encoding(false, true);
            offset = 3;
        }
        else if (bytes.StartsWith(new byte[] { 0xff, 0xfe }))
        {
            encoding = new UnicodeEncoding(false, false, true);
            offset = 2;
        }
        else if (bytes.StartsWith(new byte[] { 0xfe, 0xff }))
        {
            encoding = new UnicodeEncoding(true, false, true);
            offset = 2;
        }
        else
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            encoding = string.IsNullOrWhiteSpace(charset)
                ? new UTF8Encoding(false, true)
                : Encoding.GetEncoding(
                    charset.Trim(' ', '"'),
                    EncoderFallback.ExceptionFallback,
                    DecoderFallback.ExceptionFallback
                );
        }
        if (!partial)
            return encoding.GetString(bytes[offset..]);
        var characters = new char[encoding.GetMaxCharCount(bytes.Length - offset)];
        var count = encoding.GetDecoder().GetChars(bytes[offset..], characters, flush: false);
        return new string(characters, 0, count);
    }

    // Throws as Publish would when the folder refuses a new file beside path.
    internal static void Probe(string path)
    {
        using var probe = new FileStream(
            Temporary(path),
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            1,
            FileOptions.DeleteOnClose
        );
    }

    private static string Temporary(string path) =>
        path + "." + Guid.NewGuid().ToString("N") + ".tmp";

    internal static async Task<bool> Publish(
        string path,
        byte[] bytes,
        CancellationToken cancellation
    )
    {
        var temporary = Temporary(path);
        try
        {
            await using (
                var stream = new FileStream(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    16384,
                    FileOptions.Asynchronous
                )
            )
            {
                await stream.WriteAsync(bytes, cancellation).ConfigureAwait(false);
                await stream.FlushAsync(cancellation).ConfigureAwait(false);
            }
            cancellation.ThrowIfCancellationRequested();
            try
            {
                File.Move(temporary, path, overwrite: false);
                return true;
            }
            catch (IOException) when (Exists(path))
            {
                return false;
            }
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    // The caller proves torrent completion and settled paths from SQLite before
    // requesting this hash; a preallocated file's length cannot prove readiness.
    internal static async Task<string?> Hash(string path, long size, CancellationToken cancellation)
    {
        if (size < HashMinimum)
            return null;
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                HashRange,
                FileOptions.Asynchronous | FileOptions.RandomAccess
            );
            if (stream.Length != size)
                return null;
            var bytes = new byte[HashRange];
            var hash = (ulong)size;
            foreach (var position in new[] { 0L, size - bytes.Length })
            {
                stream.Position = position;
                await stream.ReadExactlyAsync(bytes, cancellation).ConfigureAwait(false);
                for (var offset = 0; offset < bytes.Length; offset += 8)
                    hash = unchecked(
                        hash + BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset, 8))
                    );
            }
            return stream.Length == size
                ? hash.ToString("x16", CultureInfo.InvariantCulture)
                : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^[a-zA-Z]{2,3}(?:-[a-zA-Z0-9]{2,8})*$", RegexOptions.CultureInvariant)]
    private static partial Regex Language();

    [GeneratedRegex(
        @"(?m)^\s*\d+\s*\r?\n\d{2,}:\d{2}:\d{2},\d{3}\s*-->\s*\d{2,}:\d{2}:\d{2},\d{3}[^\r\n]*\r?\n\S",
        RegexOptions.CultureInvariant
    )]
    private static partial Regex Cue();

    [GeneratedRegex(@"<[^>]*>|\{[^}]*\}", RegexOptions.CultureInvariant)]
    private static partial Regex Markup();

    [GeneratedRegex(
        @"(?:^|[. _\-\[(])forced(?:$|[. _\-\])])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex Forced();
}
