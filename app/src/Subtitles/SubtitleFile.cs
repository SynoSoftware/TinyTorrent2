using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent.Subtitles;

internal static partial class SubtitleFile
{
    private const int HashRange = 65536;
    internal const int HashMinimum = 2 * HashRange;
    internal static string Destination(string video, string language)
    {
        if (!Language().IsMatch(language))
            throw new ArgumentException("A subtitle language must be a language tag.", nameof(language));
        return Path.ChangeExtension(video, language.ToLowerInvariant() + ".srt");
    }

    internal static bool Exists(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.Directory) == 0;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    internal static async Task<byte[]> Decode(byte[] bytes, string? charset, CancellationToken cancellation)
    {
        if (bytes.Length > ProviderHttp.FileLimit)
            throw new InvalidDataException("Subtitle exceeds its size limit.");
        if (bytes.AsSpan().StartsWith("PK"u8))
        {
            using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            var entries = archive.Entries.Where(entry =>
                entry.Name.EndsWith(".srt", StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            if (entries.Length != 1)
                throw new InvalidDataException("A subtitle archive must contain exactly one SRT.");
            await using var stream = entries[0].Open();
            bytes = await ProviderHttp.Read(stream, ProviderHttp.FileLimit, cancellation).ConfigureAwait(false);
        }
        cancellation.ThrowIfCancellationRequested();
        Encoding encoding;
        var offset = 0;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
        {
            encoding = new UTF8Encoding(false, true);
            offset = 3;
        }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe }))
        {
            encoding = new UnicodeEncoding(false, false, true);
            offset = 2;
        }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff }))
        {
            encoding = new UnicodeEncoding(true, false, true);
            offset = 2;
        }
        else
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            encoding = string.IsNullOrWhiteSpace(charset)
                ? new UTF8Encoding(false, true)
                : Encoding.GetEncoding(charset.Trim(' ', '"'), EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }
        var text = encoding.GetString(bytes, offset, bytes.Length - offset);
        if (text.IndexOf('\0') >= 0 || !Cue().IsMatch(text))
            throw new InvalidDataException("The supplier did not return an SRT subtitle.");
        return bytes;
    }

    internal static async Task<bool> Publish(string path, byte[] bytes, CancellationToken cancellation)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 16384, FileOptions.Asynchronous))
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
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, HashRange, FileOptions.Asynchronous | FileOptions.RandomAccess);
            if (stream.Length != size)
                return null;
            var bytes = new byte[HashRange];
            var hash = (ulong)size;
            foreach (var position in new[] { 0L, size - bytes.Length })
            {
                stream.Position = position;
                await stream.ReadExactlyAsync(bytes, cancellation).ConfigureAwait(false);
                for (var offset = 0; offset < bytes.Length; offset += 8)
                    hash = unchecked(hash + BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset, 8)));
            }
            return stream.Length == size ? hash.ToString("x16", CultureInfo.InvariantCulture) : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    [GeneratedRegex(@"^[a-zA-Z]{2,3}(?:-[a-zA-Z0-9]{2,8})*$", RegexOptions.CultureInvariant)]
    private static partial Regex Language();

    [GeneratedRegex(@"(?m)^\s*\d+\s*\r?\n\d{2,}:\d{2}:\d{2},\d{3}\s*-->\s*\d{2,}:\d{2}:\d{2},\d{3}[^\r\n]*\r?\n\S", RegexOptions.CultureInvariant)]
    private static partial Regex Cue();
}
