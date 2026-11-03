namespace Syno.TableViewSample;

/// <summary>
/// What a render job is doing. Nine members and no qualifying flag: the three waiting kinds differ
/// only in what they are waiting for, and a boolean beside a smaller enum would leave that
/// difference unnamed. <see cref="Cancelling"/> is the display-only one — the page refuses
/// interaction on it, so the sample drives a non-interactive item as well as the rest.
/// </summary>
public enum JobState
{
    Rendering,
    Publishing,
    Verifying,
    Stalled,
    QueuedToRender,
    QueuedToVerify,
    QueuedToPublish,
    Paused,
    Cancelling,
}
