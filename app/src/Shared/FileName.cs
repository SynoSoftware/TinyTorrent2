using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Syno.TinyTorrent.Shared;

internal static partial class FileName
{
    internal static FileKind Kind(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".mkv"
            or ".mp4"
            or ".avi"
            or ".mov"
            or ".wmv"
            or ".m4v"
            or ".webm"
            or ".mpg"
            or ".mpeg"
            or ".ts"
            or ".m2ts"
            or ".mts"
            or ".flv"
            or ".f4v"
            or ".ogv"
            or ".vob"
            or ".divx"
            or ".3gp"
            or ".asf"
            or ".rmvb" => FileKind.Video,
            ".mp3"
            or ".flac"
            or ".m4a"
            or ".aac"
            or ".ogg"
            or ".opus"
            or ".wav"
            or ".wma"
            or ".aiff"
            or ".alac" => FileKind.Audio,
            ".jpg"
            or ".jpeg"
            or ".png"
            or ".gif"
            or ".webp"
            or ".bmp"
            or ".tif"
            or ".tiff"
            or ".heic"
            or ".avif"
            or ".svg" => FileKind.Picture,
            ".pdf"
            or ".txt"
            or ".rtf"
            or ".doc"
            or ".docx"
            or ".odt"
            or ".epub"
            or ".xls"
            or ".xlsx"
            or ".ppt"
            or ".pptx"
            or ".nfo"
            or ".md"
            or ".srt"
            or ".ass"
            or ".sub" => FileKind.Document,
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".bz2" or ".xz" or ".iso" =>
                FileKind.Archive,
            _ => FileKind.Other,
        };

    internal static string Glyph(FileKind kind) =>
        kind switch
        {
            FileKind.Video => Lucide.FileVideoCamera,
            FileKind.Audio => Lucide.FileMusic,
            FileKind.Picture => Lucide.FileImage,
            FileKind.Document => Lucide.FileText,
            FileKind.Archive => Lucide.FileArchive,
            _ => Lucide.File,
        };

    internal static string Normalize(string text)
    {
        var folded = new StringBuilder(text.Length);
        foreach (var character in text.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                folded.Append(char.ToLowerInvariant(character));
        return folded.ToString().Normalize(NormalizationForm.FormC);
    }

    internal static Release Interpret(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        var episode = Episode().Match(stem);
        var year = Year().Matches(stem).FirstOrDefault(match => match.Index > 0) ?? Match.Empty;
        var technical = Technical().Match(stem);
        var end = stem.Length;
        foreach (var match in new[] { episode, year, technical })
            if (match.Success && match.Index > 0)
                end = Math.Min(end, match.Index);
        var title = Spaces()
            .Replace(stem[..end].Replace('.', ' ').Replace('_', ' '), " ")
            .Trim(' ', '-', '(', '[', '{');
        if (title.Length == 0)
            title = stem;
        return new Release(title, stem)
        {
            Year = year.Success
                ? int.Parse(year.Groups[1].Value, CultureInfo.InvariantCulture)
                : null,
            Season = episode.Success
                ? int.Parse(episode.Groups["season"].Value, CultureInfo.InvariantCulture)
                : null,
            Episodes = episode.Success
                ? EpisodeNumber()
                    .Matches(episode.Groups["episodes"].Value)
                    .Select(match => int.Parse(match.Value, CultureInfo.InvariantCulture))
                    .ToArray()
                : [],
            IsExtra = Extra().IsMatch(stem),
        };
    }

    // S01E02 or S01E02E03, 1x02, and Season 1 with an optional Episode 2.
    // 1x264 is the x264 codec after an audio channel count, not an episode.
    [GeneratedRegex(
        @"(?i)(?<![a-z0-9])(?:s(?<season>\d{1,2})(?<episodes>(?:e\d{1,3})+)(?!\d)|"
            + @"(?<season>\d{1,2})x(?<episodes>(?!26[45](?!\d))\d{2,3})(?![a-z0-9])|"
            + @"season[ ._-]*(?<season>\d{1,2})(?:[ ._-]*(?:episode|ep)[ ._-]*(?<episodes>\d{1,3}))?(?!\d))",
        RegexOptions.CultureInvariant
    )]
    private static partial Regex Episode();

    [GeneratedRegex(@"\d{1,3}", RegexOptions.CultureInvariant)]
    private static partial Regex EpisodeNumber();

    [GeneratedRegex(@"(?<!\d)((?:19|20)\d{2})(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex Year();

    [GeneratedRegex(
        @"(?i)(?<![a-z0-9])(?:480p|576p|720p|1080[pi]|2160p|4320p|bluray|blu-ray|bdrip|brrip|webrip|web-dl|hdtv|dvdrip|x264|x265|h264|h265|hevc)(?![a-z0-9])",
        RegexOptions.CultureInvariant
    )]
    private static partial Regex Technical();

    [GeneratedRegex(
        @"(?i)(?:^|[. _-])(?:sample|trailer)(?:$|[. _-])",
        RegexOptions.CultureInvariant
    )]
    private static partial Regex Extra();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();
}

internal sealed record Release(string Title, string Name)
{
    internal int? Year { get; init; }
    internal int? Season { get; init; }
    internal IReadOnlyList<int> Episodes { get; init; } = [];
    internal bool IsExtra { get; init; }
}
