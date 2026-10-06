namespace Syno.TinyTorrent.Models;

public enum WindowPage
{
    Torrents,
    Preferences,
    About
}

public enum Consumer
{
    Summary,
    Inspector,
    Draft
}

public enum FileAction
{
    Move,
    Delete
}

public enum TorrentFilter
{
    All,
    Downloading,
    Seeding,
    Paused,
    Queued,
    Errors
}

public enum SuggestionScope
{
    Torrent,
    Command,
    Settings,
    Navigation
}

public enum PreferenceSection
{
    General,
    Transfers,
    Network,
    Schedule,
    Appearance
}

public enum InspectorSection
{
    General,
    Files,
    Peers,
    Trackers,
    Speed,
    Pieces
}

public enum PieceKind
{
    Unavailable,
    Rare,
    Common,
    Missing,
    Downloading,
    Verified
}

public enum TrackerStatus
{
    Unknown,
    Waiting,
    Announcing,
    Working,
    Error,
    Disabled
}

public enum ScheduleMode
{
    Normal,
    Alternative,
    Paused
}

internal enum PeriodAction
{
    Create,
    Move,
    Start,
    End
}
