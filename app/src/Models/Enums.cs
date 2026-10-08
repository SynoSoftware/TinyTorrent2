namespace Syno.TinyTorrent.Models;

public enum WindowPage
{
    Torrents,
    Settings,
    About,
}

public enum Consumer
{
    Summary,
    Inspector,
    Draft,
}

public enum PieceOrder
{
    Sequential,
    FirstLast,
}

public enum FileAction
{
    Move,
    Delete,
}

public enum TorrentFilter
{
    All,
    Downloading,
    Seeding,
    Paused,
    Queued,
    Errors,
}

public enum SuggestionScope
{
    Torrent,
    Command,
    Settings,
    Navigation,
}

// The saved window placement stores the category as its number, so members keep
// their positions; the tab order on the page is separate.
public enum SettingsCategory
{
    General,
    Transfers,
    Network,
    Limits,
    Appearance,
    Advanced,
}

public enum InspectorSection
{
    General,
    Files,
    Peers,
    Trackers,
    Speed,
    Pieces,
}

public enum PieceKind
{
    Unavailable,
    Rare,
    Common,
    Missing,
    Downloading,
    Verified,
}

public enum TrackerStatus
{
    Unknown,
    Waiting,
    Announcing,
    Working,
    Error,
    Disabled,
}

public enum ScheduleMode
{
    Normal,
    Alternative,
    Paused,
}

public enum LimitMode
{
    None,
    Speed,
    Alternative,
    Schedule,
}

internal enum LimitOrigin
{
    Manual,
    Schedule,
    Override,
}

internal enum PauseReason
{
    None,
    Adapter,
    Manual,
    Schedule,
}

internal enum RefusalReason
{
    InvalidPeriod,
    DuplicatePeriod,
    SaveFailed,
}

internal enum PeriodAction
{
    Create,
    Move,
    Start,
    End,
}

// In the order of the Edit proxy server dialog's list.
public enum ProxyType
{
    None,
    Socks5,
    Socks4,
    Http,
}

public enum ProxyOutcome
{
    Connected,
    SignInFailed,
    Unreachable,
    NotFound,
    WrongType,
    TimedOut,
}

public enum SettingKind
{
    Text,
    Boolean,
    Rate,
    Number,
    Integer,
}

#if CAPTURE
internal enum CaptureMode
{
    None,
    Full,
    Smoke,
    Shell,
    Schedule,
    Desktop,
    Details,
    DetailsFiles,
    Files,
    FilesLayout,
    Search,
    Library,
    Traffic,
    Edits,
    AddLayout,
    SettingsLayout,
    Footer,
}
#endif
