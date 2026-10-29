# Subtitle configuration

Part of the [subtitle contract](../subtitles.md). Read its shared outcome,
ownership and recovery rules before the sections here that the change affects.

## Configuration

Settings groups third-party features in Plugins, with SQLite coverage counts.
The Library card moves from General unchanged. One Subtitles card groups
supplier access, languages, download preferences and explicit actions.
Persistent configuration comes first: supplier access, wanted languages, then
automatic downloading and its extra subtitle count. The search limit is the
[shared Plugins setting](../architecture.md#third-party-provider-access), on the
Search protection card below the Library and Subtitles cards. Spacing separates
configuration groups, with supplier and access together and the language filter
beside the wanted languages. Dividers separate configuration from Library
subtitle search and supplier operations from the local subtitle-folder scan, so
the saved limit stays distinct from the choices for one batch.

**Owner ruling: Download when a torrent finishes controls automatic downloading only.**
It does not enable the subtitle feature. Supplier setup, language selection,
batch downloads and local scans remain available while it is Off, so choosing
manual downloads never requires enabling automatic work.

| Input | Behavior and reason |
| --- | --- |
| Download when a torrent finishes | Saves automatic intent independently of readiness. While it is On, each torrent that finishes gets one search. Missing credentials or temporary failures pause work without changing the switch. |
| Additional automatic matches | Saves 0 to 3 alternatives for new automatic searches, default 1. Accepted jobs retain their matching choice, and batch choices remain independent. |
| Supplier | Selection applies immediately, with empty credentials for a different supplier. Only Account opens the account dialog. Selection preserves automatic intent. |
| API key | OpenSubtitles uses the free application key without customer setup. SubDL and SubSource use the free application key built into the release, and the person may enter their own key instead, following [third-party provider access](../architecture.md#third-party-provider-access). |
| User name and password | OpenSubtitles optionally accepts a personal login alongside the application key. Both blank omits user login; entering either requires the complete pair. Other suppliers show only the key. |
| Wanted languages | A choice between the interface language and chosen languages. Chosen languages are a checklist of the supplier's catalog, narrowed by a filter box; typing filters the list and never adds a language. An explicit choice downloads one suitable subtitle for **each** selected language, so this is not an ordered fallback list. |
| Library subtitle search | Movies to search: New, Missing data, All selected, with nested movie counts. Matches to download: Exact match only, Best match, Up to 2, 3, or 4 subtitles. These choices apply only to the requested batch. |
| Scan torrent folders | On request, finds bundled subtitles in the folders of every finished torrent, without supplier access or changes to files. Reports files found and read errors. |

**Owner ruling: when subtitle languages are unset, use the current interface
language.** Do not save a copy of that default: an interface-language change
continues to apply until the person explicitly chooses subtitle languages.
Explicit choices remain independent of later interface-language changes.
Removing the last explicit language restores the default. Offer the supplier's complete
language catalog, independently of TinyTorrent's translated interface languages,
preserving regional distinctions where available; if the interface
language is unsupported, ask for a supported choice inline in Settings rather
than silently substituting another language. At least one supported language and
the supplier's required access make the configuration usable. Readiness means
that the shipped integration is authorized, required credentials are supplied,
and an effective language is available; access status separately reports observed
results. OpenSubtitles can be configured without personal credentials when the
free application key is available. Configuration stays editable while automatic downloading
is Off, so setup never depends on first enabling unconfigured work.

OpenSubtitles uses the existing free application key with an optional user login. Its
[getting-started guide](https://opensubtitles.tawk.help/article/getting-started)
requires a key on every API request and describes limited free downloads without
user login. No paid application package is part of this plan. Actual replies
establish available access and allowance; quota exhaustion waits for the supplier's
reset without purchasing access. Clearing the login restores anonymous use with
the application key. Failed login never silently
falls back to anonymous access, because that changes the allowance being used.

**Owner ruling: supplier selection is immediate and never opens a dialog.**
Edit opens the selected supplier in the
[account dialog](../architecture.md#third-party-provider-access). A draft never
changes live credentials. Check validates the complete draft without saving it
or sending movie information. Save does not require a preceding Check; Cancel
and Escape discard the draft. Failed saves retain the draft and explain the
failure in the dialog. Check is disabled while checking or when the required
account values are incomplete, and a click while a check runs does nothing;
saved secrets count toward readiness.

Apply [saved-value recovery](../architecture.md#saved-value-recovery) when loading:
an unreadable secret becomes no saved secret, malformed language choices use
the interface-language default, and an
out-of-range extras count is clamped to 0 to 3, and an invalid date is cleared. Repair
only damaged values and retain subtitle records, pending work and valid quota
deadlines, so recovery cannot cause repeat downloads.

Use Windows user-scoped DPAPI protection in the C# database owner. Presentation
exposes account status, never a saved secret. Personal supplier keys and optional
login belong to the person's configuration; the free OpenSubtitles application
key belongs to the integration.
Reopening the editor preserves a saved secret without returning it to its fields.
Distinguish keeping, replacing and clearing that secret; masked placeholder text
is never a credential. A kept password belongs to the user name it was saved
with, so changing the user name asks for the password again instead of sending
the old one for another account. An unreadable saved password keeps the user
name and asks for the password. Check uses the same draft interpretation as Save.
Configuration and protected secrets commit together in C# SQLite. No subtitle
secret belongs in engine settings or the pipe.

The supplier dialog reuses the proxy editor's matching field presentation and
dialog mechanics, as the [Settings contract](../ui/settings.md#proxy-server)
describes. Proxy and supplier drafts, validation, secrets and network operations
keep their own owners: shared controls do not require a configurable editor
workflow with mode flags or callback lists. Supplier Check and saved-access
validation call the same supplier operation. Account requirements remain
supplier-specific.

The Proxy and supplier dialogs share one Check footer, `CheckStatus`: progress,
success and failure glyphs and one-line accessible feedback. Each draft keeps
its own validation, cancellation and operations. Account Save validation sits
beside its credential field, separate from the Check outcome. Operation
failures stay exceptions and validation messages stay catalogue keys, so a
language switch translates feedback already shown. Subtitle Settings' persistent
status and its file problems stay independent, with one classification for
persistent account failures. Each failure line is trimmed, and tooltips and
accessibility expose the full text.

Store stable language identifiers, not display names or one supplier's codes;
each supplier maps them to its own codes. TinyTorrent ships each supplier's
language catalog as that mapping, so the language list works offline and
opening Settings never contacts a supplier before the person enables the
feature. Display names come from Windows for each identifier, in the interface
language, so no catalog of language names needs translating. The filter box searches the saved supplier's catalog, so a
language the supplier does not offer cannot be chosen.
After a supplier change, a chosen language that the new supplier does not offer
stays in the list, is marked inline as not offered, and is not requested. It
never vanishes on its own, because the person chose it and may switch back.
Unsupported choices do not enter Find's missing counts or queued work; if none
of the effective languages is supported, the configuration is not usable.

Automatic downloading searches only when a torrent finishes, under the
[completion ruling](../subtitles.md#outcome). A torrent whose completion search
is not decided yet is [new](../architecture.md#third-party-provider-access).
When a new torrent's files are complete, at rest and confirmed, TinyTorrent
scans its folders for bundled subtitles and decides its completion search: one
search for each wanted video and wanted language without a subtitle, when the
switch is On. The torrent is then known, whatever its videos, languages and
supplier, so the decision is made once: switching on, adding a language or
fixing the supplier later starts no search for it. A torrent whose movie moved
during the scan stays new until the reconciliation that follows the move,
because its search would use the old path. Stopping that scan in the
work list decides the search as none, so no subtitle is downloaded beside one
the scan had not found yet. The completion search replaces an automatic job an
earlier version left for the same subtitle. Because the files are confirmed,
the search can use the movie hash at once. Changing languages
cancels accepted work in a language no longer wanted. Changing supplier
preserves the automatic switch and accepted work, and drops the pending
candidate references of extra subtitles, because a reference is valid only at
the supplier that returned it. Correcting the same supplier's account preserves
accepted work. Work waits until the supplier is configured. Every other file
needs Download subtitles or Find, so enabling the feature or reopening the
window never starts a backlog against the supplier's allowance. Reopening
cancels a completion search that had not finished, while retaining its search
history. Accepted manual jobs resume. A requested search that a failure stopped
is tried once more on reopening, because the reason for the failure is not
saved.

**Owner ruling: Batch download works while automatic downloading is Off.**
Scope refers to the selected matching goal, not simply files on disk. New
includes unresolved questions without local protection. Missing data adds
locally blocked questions, including earlier searches with no exact match and
downloaded likely matches. All selected also revisits satisfied movies.
The Library selection dialog uses the same
batch control, limiting both counts and requests to its captured selection.
Find is the dialog's primary action beside Close. The host owns attaching the
batch control to acquisition, so a visual reload preserves its selection and counts.
While a batch runs, Find becomes Stop in both places. Stop ends the batch
and the manual jobs kept from an earlier session. Saved subtitles stay, and a
stopped movie whose torrent has not finished yet still gets its completion
search.

Find queues complete, wanted movies in every supported wanted language through
the existing acquisition owner. An exact release or hash match gets one
subtitle. Otherwise the choice limits the total likely subtitles, including
existing alternatives. Exact match only permits no guesses. Files are preserved;
supplier references prevent repeat downloads. A newly available exact match can
be saved beside earlier guesses. Changing choices restores the count for the
next search. Status counts movies, not languages or output paths. There
is no separate limit on how many movies one Find searches. The first Find on a
large library searches every movie once, and the supplier's own quota slows that
traffic. There is no billing ledger or unconditional maximum.

Find works on finished movies only, so Library, the Plugins coverage count and
Find describe one set of movies; a movie still downloading gets its automatic
search when its torrent finishes. The batch count is derived from SQLite when
it is read, with the selection
the batch captured, and is never kept as a copy that can disagree with
Library's queries. Manual jobs still advance through their evidence stages
while automatic downloading is Off, so a manual job reaches its hash stage.

Manual jobs retain their matching choice and authorization across restarts.
Switching automatic downloading Off cancels automatic pending work only.
Allowance and retries remain shared with automatic work. The default scope
never repeats a saved miss; Missing data, All selected or an individual Search
again authorize one fresh attempt. Reset search protection is a separate
Settings action and enqueues no searches.

## Discovery and permission

The Settings index carries the Download subtitles when a torrent finishes
switch, the
same saved setting as on the Subtitles card, and a link to that card. The Help
menu has no setup entry, because subtitles, the IMDb data and city-level peer
locations are all optional and Settings already leads to each of them.

Each supplier owns its disclosure and privacy/terms links, shown in an
information flyout from the supplier name; never show OpenSubtitles terms for
another supplier. The flyout is not a modal dialog or a consent step.

[Store policy 10.5.2](https://learn.microsoft.com/en-us/windows/apps/publish/store-policy-archive/store-policy-7-19#105-personal-information)
requires explaining applicable third-party data sharing before opt-in, but does
not mandate a particular card or dialog. The supplier link makes its information
available before enabling without forcing users through Edit. Validate this final
permission presentation against the release's actual data flow and agreement
under the [release requirements](../licences.md#store-privacy-and-permission); the
presentation does not establish certification. No checkbox or additional
consent step is introduced.

The switch authorizes automatic requests; Find authorizes its requested batch
independently. Saving a supplier or account never changes that choice. Correcting
access resumes accepted work without requiring the person to turn the switch
back on. Check sends no movie information.

The Settings page's Check tests the saved configuration; the dialog's Check
tests its draft. Both call the same supplier check operation. Neither enables
automatic downloading, saves a draft, or changes credentials. Check reports only
what the supplier's non-content endpoint establishes: API reachability and
accepted application/account credentials where supported. It does not spend a
subtitle download to prove access, promise a future match, or clear an
outstanding save error.

Supplier status shows Checking, Access checked after a check or request
succeeded, the time a used-up allowance returns, or Not checked. Refused access
returns it to Not checked. Failures are reported through the window's feedback,
because the status line holds state, not events. Filled fields never imply access.
Saving a changed supplier, account, proxy, or network adapter returns the
status to Not checked, because the old result no longer describes the saved
configuration. The one exception: saving the exact supplier values the dialog
just checked keeps that result. Check
remains optional; ordinary automatic requests can establish access after
enabling. The switch itself is the saved permission; no separate consent record
exists.

This design uses one clear action, without additional checkboxes, confirmation
dialogs, reminders, or legal banners. Any additional permission needs a specific
applicable requirement, recorded with evidence in the
[release blockers](../licences.md#remaining-release-blockers).
