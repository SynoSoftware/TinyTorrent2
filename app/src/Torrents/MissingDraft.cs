using System.ComponentModel;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shared.Engine;
using Syno.TinyTorrent.Shell;

namespace Syno.TinyTorrent.Torrents;

// The question about lost files: one answer, Redownload or Skip, for every
// checked torrent. Each row keeps the question it showed when the dialog
// opened, and the answer sends that question's number, so the engine leaves
// alone a file that became missing later.
public sealed class MissingDraft : FileDraft
{
    // The names a tooltip lists before it counts the rest.
    private const int ListedNames = 10;

    private Dictionary<string, ShownQuestion> _questions = [];
    private CancellationTokenSource? _nameRead;

    // Closing the window drops the dialog; the torrents keep their questions.
    public override bool HasDraft => false;
    public override bool CanSubmit => CanSend && Checked.Any();
    public override ClosePolicy ClosePolicy => new ClosePolicy.Discard();
    public override string Title => Text.Get("missing", "title");

    // The title asks two acts, so its icon names the subject.
    public override string Glyph => Segoe.Files;
    public override string SubmitText => Text.Get("missing", "redownload");
    public override string SubmitGlyph => Segoe.Download;
    public override string SubmitToolTip =>
        Text.FormatCount("missing", "redownload_tip", _questions.Count);
    public string SkipText => Text.Get("missing", "skip");
    public string SkipToolTip => Text.FormatCount("missing", "skip_tip", _questions.Count);
    public string CancelToolTip => Text.Get("missing", "cancel_tip");
    public QuestionFolder[] Folders { get; private set; } = [];

    // A dialog for one torrent has no check boxes, as unchecking its only
    // torrent would be Cancel.
    public bool ShowsChecks => _questions.Count > 1;

    // What Redownload fetches, such as "2 torrents · 4 files · 6.0 GiB";
    // empty while no torrent is checked.
    public string Total
    {
        get
        {
            var questions = Checked.Select(shown => shown.Question).ToArray();
            if (questions.Length == 0)
                return string.Empty;
            return Text.Format(
                "units",
                "detail",
                Text.Format(
                    "units",
                    "detail",
                    Text.FormatCount("window", "torrents", questions.Length),
                    Text.FormatCount("missing", "files", questions.Sum(question => question.Files))
                ),
                Text.Bytes(questions.Sum(question => question.Size))
            );
        }
    }

    private IEnumerable<ShownQuestion> Checked =>
        _questions.Values.Where(question => question.IsChecked);

    internal MissingDraft(MainViewModel owner, PipeClient client)
        : base(owner, client) { }

    // Lists the torrents that have a question, each with the question it has
    // now, and starts reading the names of their missing files.
    internal void Begin(Torrent[] torrents)
    {
        _questions = [];
        foreach (var torrent in torrents)
        {
            if (torrent.Question is { } question)
                _questions[torrent.TorrentId] = new ShownQuestion(this, Text, torrent, question);
        }
        Load([.. _questions.Values.Select(shown => shown.Torrent)]);
        Refresh();
        _nameRead?.Cancel();
        _nameRead = new CancellationTokenSource();
        _ = ReadNames([.. _questions.Values], _nameRead.Token);
    }

    public Task<bool> Skip() => Submit(() => Answer(MissingAnswer.Skip));

    private protected override Task<bool> Send() => Answer(MissingAnswer.Redownload);

    private async Task<bool> Answer(MissingAnswer answer)
    {
        var word = answer.ToString().ToLowerInvariant();
        await Client.Send(
            "answer_missing",
            new
            {
                torrents = Checked
                    .Select(shown => new
                    {
                        torrent_id = shown.Torrent.TorrentId,
                        question = shown.Question.Number,
                    })
                    .ToArray(),
                answer = word,
            },
            Requester.Person
        );
        Owner.Notifications.AnnounceAccepted("missing", word);
        return true;
    }

    internal bool Contains(Torrent torrent) => Owner.Contains(torrent);

    // A snapshot can end a row's question, which its row then shows.
    internal override void Refresh()
    {
        foreach (var question in _questions.Values)
            question.Refresh();
        base.Refresh();
    }

    private protected override void Clear()
    {
        _nameRead?.Cancel();
        _nameRead = null;
        _questions = [];
        Folders = [];
    }

    private protected override void RefreshItems()
    {
        base.RefreshItems();
        Folders = Locations
            .GroupBy(torrent => torrent.SavePath, StringComparer.OrdinalIgnoreCase)
            .Select(folder => new QuestionFolder(
                folder.Key,
                folder.Select(torrent => _questions[torrent.TorrentId]).ToArray()
            ))
            .ToArray();
    }

    // One torrent after another, so a large torrent's pages do not crowd the
    // pipe.
    private async Task ReadNames(ShownQuestion[] questions, CancellationToken cancellation)
    {
        foreach (var question in questions)
        {
            var names = await Task.Run(() => MissingNames(question, cancellation));
            if (cancellation.IsCancellationRequested)
                return;
            question.ShowNames(names);
        }
    }

    // The names of the question's first files, up to ListedNames. A read that
    // fails keeps the names it has, and the tooltip counts the rest.
    private async Task<IReadOnlyList<string>> MissingNames(
        ShownQuestion question,
        CancellationToken cancellation
    )
    {
        List<string> names = [];
        var wanted = Math.Min(ListedNames, question.Question.Files);
        try
        {
            await foreach (
                var file in Client.TorrentFiles(question.Torrent.TorrentId).ConfigureAwait(false)
            )
            {
                if (cancellation.IsCancellationRequested)
                    break;
                if (!file.Missing)
                    continue;
                names.Add(Path.GetFileName(file.Path));
                if (names.Count == wanted)
                    break;
            }
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine(error);
        }
        return names;
    }
}

// The torrents of the question saved in one folder, as the dialog lists them.
public sealed record QuestionFolder(string Folder, ShownQuestion[] Questions);

// One torrent's question as the dialog showed it when it opened. A question
// that ends while the dialog is open keeps its row, so rows never move under
// the person: its check box turns off and is disabled, and its count reads
// Found.
public sealed class ShownQuestion : INotifyPropertyChanged
{
    private static readonly PropertyChangedEventArgs Everything = new(string.Empty);
    private readonly MissingDraft _draft;
    private readonly Strings _text;
    private bool _checked = true;
    private IReadOnlyList<string> _names = [];

    internal ShownQuestion(MissingDraft draft, Strings text, Torrent torrent, Question question)
    {
        _draft = draft;
        _text = text;
        Torrent = torrent;
        Question = question;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public Torrent Torrent { get; }
    internal Question Question { get; }
    public bool ShowsCheck => _draft.ShowsChecks;

    // The answer reaches only a torrent that still has a question.
    private bool IsAsked => _draft.Contains(Torrent) && Torrent.Question is not null;
    public bool CanCheck => IsAsked && _draft.CanEdit;
    public bool IsChecked
    {
        get => _checked && IsAsked;
        set
        {
            if (_checked == value)
                return;
            _checked = value;
            _draft.Refresh();
        }
    }

    // A torrent removed meanwhile keeps its count; the engine ignores it.
    public string CountText =>
        _draft.Contains(Torrent) && Torrent.Question is null
            ? _text.Get("missing", "found")
            : _text.Format(
                "units",
                "detail",
                _text.FormatCount("missing", "files", Question.Files),
                _text.Bytes(Question.Size)
            );

    // The missing files' names, one a line, then how many more files are
    // missing; empty until the names are read. The count's tooltip and the
    // check box's help text show them.
    public string Names =>
        _names.Count == 0 ? string.Empty
        : _names.Count < Question.Files
            ? string.Join(
                Environment.NewLine,
                _names.Append(_text.Format("pieces", "more", Question.Files - _names.Count))
            )
        : string.Join(Environment.NewLine, _names);
    public bool HasNames => _names.Count > 0;

    internal void ShowNames(IReadOnlyList<string> names)
    {
        _names = names;
        Refresh();
    }

    internal void Refresh() => PropertyChanged?.Invoke(this, Everything);
}
