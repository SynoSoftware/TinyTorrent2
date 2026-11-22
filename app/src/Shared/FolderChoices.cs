namespace Syno.TinyTorrent.Shared;

/// <summary>
/// The person's choices of the folders whose media Library shows. The nearest
/// choice at or above a path decides, and a path with none above it is shown.
/// Every choice differs from the nearest choice above it, so each one changes
/// something. Immutable: a change gives new choices.
/// </summary>
internal sealed class FolderChoices
{
    /// <summary>A place Library shows, with the folders left out inside it.</summary>
    internal sealed record Place(string Folder, IReadOnlyList<string> LeftOut);

    internal static FolderChoices Empty { get; } = new([]);

    // Windows keeps its own sounds there and programs keep their clips there,
    // and a person does not keep a collection there.
    internal static FolderChoices Defaults { get; } =
        new(
            new[]
            {
                Environment.SpecialFolder.Windows,
                Environment.SpecialFolder.ProgramFiles,
                Environment.SpecialFolder.ProgramFilesX86,
                Environment.SpecialFolder.CommonApplicationData,
            }
                .Select(folder => Environment.GetFolderPath(folder))
                .Where(folder => folder.Length > 0)
                .Select(folder => new FolderChoice(folder, Shown: false))
        );

    // In path order, so a folder comes before the folders inside it.
    private readonly FolderChoice[] _choices;

    internal FolderChoices(IEnumerable<FolderChoice> choices)
    {
        var kept = new List<FolderChoice>();
        foreach (
            var choice in choices
                .Select(choice => choice with { Path = Normal(choice.Path) })
                .DistinctBy(choice => choice.Path, StringComparer.OrdinalIgnoreCase)
                .OrderBy(choice => choice.Path, StringComparer.OrdinalIgnoreCase)
        )
        {
            if ((Nearest(kept, choice.Path)?.Shown ?? true) != choice.Shown)
                kept.Add(choice);
        }
        _choices = [.. kept];
    }

    internal IReadOnlyList<FolderChoice> All => _choices;

    internal int LeftOut => _choices.Count(choice => !choice.Shown);

    internal bool Shows(string path) =>
        (Find(path) ?? Nearest(_choices, path))?.Shown ?? true;

    /// <summary>
    /// The check box of <paramref name="folder"/>: partial when a folder
    /// inside it differs.
    /// </summary>
    internal bool? Check(string folder) =>
        _choices.Any(choice => FolderComparer.Instance.Contains(folder, choice.Path)) ? null
        : Shows(folder);

    /// <summary>The choices at or inside <paramref name="folder"/>.</summary>
    internal IReadOnlyList<FolderChoice> Within(string folder) =>
        [.. _choices.Where(choice => Covers(folder, choice.Path))];

    /// <summary>
    /// Sets <paramref name="folder"/> and everything inside it, as a folder's
    /// check box does: the choices inside it give way.
    /// </summary>
    internal FolderChoices Set(string folder, bool shown) => Replace(folder, [new(folder, shown)]);

    /// <summary>
    /// Replaces the choices at or inside <paramref name="folder"/>, as Undo
    /// puts back what a change replaced.
    /// </summary>
    internal FolderChoices Replace(string folder, IEnumerable<FolderChoice> within) =>
        new([.. _choices.Where(choice => !Covers(folder, choice.Path)), .. within]);

    /// <summary>
    /// The places Library shows, as Windows' indexing summary lists them: each
    /// drive of <paramref name="drives"/> that is shown and each shown folder
    /// inside a left-out one, with the folders left out inside it.
    /// </summary>
    internal IReadOnlyList<Place> Places(IEnumerable<string> drives)
    {
        var folders = drives
            .Select(Normal)
            .Where(drive => Find(drive) is null)
            .Concat(_choices.Where(choice => choice.Shown).Select(choice => choice.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase);
        var places = new List<Place>();
        foreach (var folder in folders)
        {
            IReadOnlyList<string> leftOut =
            [
                .. _choices
                    .Where(choice =>
                        !choice.Shown
                        && FolderComparer.Instance.Contains(folder, choice.Path)
                        && FolderComparer.Instance.Equals(Owner(choice.Path), folder)
                    )
                    .Select(choice => choice.Path),
            ];
            places.Add(new(folder, leftOut));
        }
        return places;
    }

    // The place a left-out folder lies in: the nearest choice above it, which
    // is shown because every choice differs from the one above it, or else
    // its drive.
    private string? Owner(string path) =>
        Nearest(_choices, path)?.Path ?? Path.GetPathRoot(path);

    private FolderChoice? Find(string path) =>
        _choices.FirstOrDefault(choice => FolderComparer.Instance.Equals(choice.Path, path));

    // The deepest choice strictly above `path`.
    private static FolderChoice? Nearest(IEnumerable<FolderChoice> choices, string path) =>
        choices
            .Where(choice => FolderComparer.Instance.Contains(choice.Path, path))
            .MaxBy(choice => choice.Path.Length);

    private static bool Covers(string folder, string path) =>
        FolderComparer.Instance.Equals(folder, path) || FolderComparer.Instance.Contains(folder, path);

    // A drive root keeps its separator; any other folder has none at its end.
    private static string Normal(string folder) =>
        Path.TrimEndingDirectorySeparator(folder.Replace('/', '\\'));
}

internal sealed record FolderChoice(string Path, bool Shown);

// What a saved change did: the choices before and after it, and whether it
// withdrew disk files.
internal sealed record FolderChange(FolderChoices Before, FolderChoices After, bool Withdrew)
{
    // Whether a folder left out before is shown now. Its files return only
    // at the next refresh, because gozler is asked for them.
    internal bool Returns => Flips(Before, After).Any();

    /// <summary>
    /// The folders left out in <paramref name="from"/> and shown in
    /// <paramref name="to"/>. Every path whose state the change flips lies at
    /// or inside one of them, because the topmost such path is a choice of
    /// one side.
    /// </summary>
    internal static IEnumerable<string> Flips(FolderChoices from, FolderChoices to) =>
        from.All.Concat(to.All)
            .Select(choice => choice.Path)
            .Where(path => !from.Shows(path) && to.Shows(path));
}
