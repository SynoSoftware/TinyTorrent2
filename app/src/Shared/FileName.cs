using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Syno.TinyTorrent.Shared;

internal static partial class FileName
{
    // The subtitle formats, which Subtitles reads and Kind counts as documents.
    private static readonly HashSet<string> SubtitleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt",
        ".ass",
        ".ssa",
        ".vtt",
        ".sub",
    };

    internal static bool IsSubtitle(string path) =>
        SubtitleExtensions.Contains(Path.GetExtension(path));

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
            or ".md" => FileKind.Document,
            var extension when SubtitleExtensions.Contains(extension) => FileKind.Document,
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".bz2" or ".xz" or ".iso" =>
                FileKind.Archive,
            _ => FileKind.Other,
        };

    internal static string Glyph(FileKind kind) =>
        kind switch
        {
            FileKind.Video => Segoe.FileVideoCamera,
            FileKind.Audio => Segoe.FileMusic,
            FileKind.Picture => Segoe.FileImage,
            FileKind.Document => Segoe.FileText,
            FileKind.Archive => Segoe.FileArchive,
            _ => Segoe.File,
        };

    internal static string Normalize(string text)
    {
        var folded = new StringBuilder(text.Length);
        foreach (var character in text.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                folded.Append(char.ToLowerInvariant(character));
        return folded.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// The runs of letters and digits in <paramref name="text"/>, as written.
    /// A caller that compares words normalizes the text first.
    /// </summary>
    internal static string[] Words(string text) =>
        Separators().Split(text).Where(word => word.Length > 0).ToArray();

    /// <summary>
    /// The words of a title, as written, with each apostrophe joining its word:
    /// "Ocean's Eleven" gives Oceans and Eleven, as the file name Oceans.Eleven
    /// does. Search splits at every such character instead; see
    /// <see cref="Words"/>.
    /// </summary>
    internal static string[] TitleWords(string title) => TitleWords(title, apostropheJoins: true);

    /// <summary>
    /// A title in lower case without accents or punctuation, with each
    /// apostrophe joining its word: "Ocean's Eleven" gives "oceans eleven".
    /// The installed IMDb data stores its titles under this key.
    /// </summary>
    internal static string TitleKey(string title) => string.Join(' ', TitleWords(Normalize(title)));

    /// <summary>
    /// A title's words in lower case without accents, in the two forms a file
    /// name writes: with each apostrophe joining its word, as Oceans.Eleven
    /// writes "Ocean's Eleven", and with each apostrophe separating words, as
    /// L.Auberge.Espagnole writes "L'Auberge espagnole". Two titles agree when
    /// their words agree in either form.
    /// </summary>
    internal static string[][] TitleForms(string title)
    {
        var text = Normalize(title);
        return [TitleWords(text, apostropheJoins: true), TitleWords(text, apostropheJoins: false)];
    }

    /// <summary>
    /// The <see cref="TitleForms"/> of a title, each with its words joined by
    /// spaces.
    /// </summary>
    internal static string[] TitleKeys(string title) =>
        [.. TitleForms(title).Select(words => string.Join(' ', words))];

    /// <summary>
    /// What follows <paramref name="title"/> at the start of
    /// <paramref name="name"/>, or the whole name when it does not start with
    /// the title. Only letters and digits are compared, without case, and the
    /// title must end at a word boundary: Forced.Vengeance.en gives ".en" for
    /// the title "Forced Vengeance".
    /// </summary>
    internal static string TrimTitle(string name, string title)
    {
        var index = 0;
        foreach (var character in title)
        {
            if (!char.IsLetterOrDigit(character))
            {
                continue;
            }
            while (index < name.Length && !char.IsLetterOrDigit(name[index]))
            {
                index++;
            }
            if (
                index == name.Length
                || char.ToLowerInvariant(name[index]) != char.ToLowerInvariant(character)
            )
            {
                return name;
            }
            index++;
        }
        return index < name.Length && char.IsLetterOrDigit(name[index]) ? name : name[index..];
    }

    private static string[] TitleWords(string title, bool apostropheJoins)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        foreach (var character in title)
        {
            if (char.IsLetterOrDigit(character))
            {
                word.Append(character);
            }
            else if (!(apostropheJoins && character is ('\'' or '’')) && word.Length > 0)
            {
                words.Add(word.ToString());
                word.Clear();
            }
        }
        if (word.Length > 0)
        {
            words.Add(word.ToString());
        }
        return [.. words];
    }

    /// <summary>
    /// The normalized words of a search query, which <see cref="Matches"/>
    /// finds in normalized text.
    /// </summary>
    internal static string[] SearchWords(string query) =>
        Words(Normalize(query)).Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>
    /// Whether every one of <paramref name="words"/> starts a word of the
    /// normalized <paramref name="text"/>, in any order, as the repository's
    /// search ruling requires: "50 para" matches "paracetamol 500mg", and
    /// "para" does not match "comparable".
    /// </summary>
    internal static bool Matches(string text, IReadOnlyCollection<string> words) =>
        words.All(word => StartsWord(text, word));

    /// <summary>
    /// Whether <paramref name="word"/> occurs at the start of
    /// <paramref name="text"/> or after a character that is not a letter or a
    /// digit, the separators <see cref="Words"/> splits at.
    /// </summary>
    internal static bool StartsWord(string text, string word)
    {
        for (
            var index = text.IndexOf(word, StringComparison.Ordinal);
            index >= 0;
            index = text.IndexOf(word, index + 1, StringComparison.Ordinal)
        )
            if (index == 0 || !(char.IsLetter(text[index - 1]) || char.IsNumber(text[index - 1])))
                return true;
        return false;
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

    [GeneratedRegex(@"[^\p{L}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex Separators();
}

internal sealed record Release(string Title, string Name)
{
    internal int? Year { get; init; }
    internal int? Season { get; init; }
    internal IReadOnlyList<int> Episodes { get; init; } = [];
    internal bool IsExtra { get; init; }

    // A name with a season names a series; any other names a movie.
    internal VideoKind Kind => Season is null ? VideoKind.Movie : VideoKind.Series;

    /// <summary>
    /// Whether a catalogue can be asked for the release: it is no sample or
    /// trailer and has a title.
    /// </summary>
    internal bool IsSearchable => !IsExtra && Title.Length > 0;
}
