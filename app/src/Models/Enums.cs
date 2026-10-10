namespace Syno.TinyTorrent.Models;

public enum FileKind
{
    Video,
    Audio,
    Picture,
    Document,
    Archive,
    Other,
}

public enum WindowPage
{
    Torrents,
    Settings,
    About,
    Library,
}

public enum LibraryConfiguration
{
    Videos,
    Music,
    Files,
}

public enum TransferPreset
{
    Reduced,
    Balanced,
    FullSpeed,
    Custom,
}

public enum Consumer
{
    Summary,
    Inspector,
    Draft,
    Files,
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

public enum DeletionMode
{
    Recycle,
    Permanent,
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
    Schedule,
    Subtitles,
}

internal enum SubtitleSupplier
{
    OpenSubtitles,
    SubDL,
    SubSource,
}

internal enum SubtitleStage
{
    Release,
    Complete,
}

internal enum SubtitleOutcome
{
    Pending,
    NoMatch,
    Unavailable,
    Failed,
}

internal enum SubtitleFailure
{
    None,
    Unconfigured,
    Unavailable,
    Authentication,
    Quota,
    Network,
    Route,
    Database,
    Save,
    Format,
}

internal enum SubtitlePathAccess
{
    Pending,
    Protected,
    Allowed,
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

// The window's answer when the engine asks it to confirm Exit.
internal enum ExitAnswer
{
    Confirmed,
    Cancelled,
    // The window cannot show the prompt now, so the engine asks natively.
    Unavailable,
}

public enum VideoKind
{
    Movie,
    Series,
    Episode,
}

internal enum PauseReason
{
    None,
    Adapter,
    Manual,
    Schedule,
    ConnectionTest,
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

internal enum ConnectionPhase
{
    Idle,
    Stopping,
    Downloading,
    Uploading,
    Holding,
    Restoring,
    Completed,
    Cancelled,
    Failed,
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
    LibraryFiles,
    Edits,
    AddLayout,
    SettingsLayout,
    SettingsPrototype,
    Footer,
}
#endif
