# IMDb datasets and local answers

Part of the [search protection contract](../search-protection.md). Read its
shared ownership, identity, admission, pacing and persistence rules first.

The [IMDb datasets plan](../imdb-datasets.md#phase-1-the-imdb-provider) owns the
dataset's separate replaceable file, read-only lookup connections, temporary
import, explicit Download/Update/Delete actions and Settings feedback. This
contract protects its remote downloads and does not repeat that design.

**Owner ruling: IMDb offline is managed only in Settings.** It is not an entry
in Library context menus, the individual Fetch/Force fetch provider chooser,
or batch fetch actions. Local movie reads never start a download. Settings
Download/Update requests the complete dataset as one operation; there is no
per-movie IMDb download endpoint. The import format is the IMDb datasets plan's;
it is never split into movie downloads.

RequestPolicy protects remote requests, so it needs no IMDb bypass or special
exemption. **The plugin contract declares whether a provider supports
downloading information for an individual movie: `ProviderOption.IsOnline`,
true for every provider that declares no `Dataset`.** API providers are
online; IMDb offline declares its dataset. Settings still lists both. The
provider chooser, context menus, batch actions and execution admission consume
this same capability; it is never inferred from the provider's name or from
whether its data is installed. Library reads the installed dataset locally under
its ordinary matching owner.

- Reading local movie data is not a protected remote request: it creates no
  remote search count, negative history or access wait. Missing or damaged
  data directs the person to Settings. Reset cannot install or repair it.
- Remote completed-query caching does not absorb local dataset reads. Local
  answers use the installed dataset's freshness: after replacement, neither
  learned candidates nor saved title/year matching shortcuts may settle an
  unresolved movie using stale dataset facts. Accepted identifications and
  useful saved metadata stay. This is local data invalidation at Library's
  existing owner, not permission to bypass any protection or contact IMDb.
- Per-session attempt markers bound an automatic sweep. An empty local answer
  does not cause a busy loop; a later session may read it again. Installing a
  dataset clears only that catalogue's unresolved attempt markers and any
  session query reuse, so eligible unfinished work can see new titles during the
  same session. A failed or cancelled installation leaves the old dataset and its
  eligibility state intact.
- Dataset installation and deletion use Library's pause/drain operation instead
  of ending its batch. Queued entries and explicit decisions stay. Installation
  resumes eligible accepted work; deletion leaves work waiting for available
  data or an explicit Stop, without starting a download. Deletion turns off
  IMDb automatic lookups.
- Download and Update are explicit Settings commands for the entire dataset.
  Each transfer follows the same search protection, pacing and remote waits
  as other outbound calls. Each explicit Download or Update uses the shared
  recheck command: it may try once past an earlier attention or local backoff
  hold. It still respects remote waits, allowance and pacing. Reset uses the
  standard rules; no separate IMDb override exists. A dataset transfer does not
  charge the movie cap, create per-movie misses, redownload a dataset
  automatically, or let a remote download hold stop reads of the installed file.
- Reset for IMDb or all providers first prevents another dataset command from
  starting, then cancels and drains the whole Download/Build/Install command
  and its policy writes **before taking Library's change/execution gates**.
  Otherwise completion can wait for `Reload` while reset waits for completion.
  The existing download owner checks cancellation before installation;
  installed data is preserved and the interruption is reported in its existing
  UI. Reset never restarts the large download. File replacement
  remains serialized with Library reads; no database lock is held while
  draining the transfer or importing the data.

Protection transactions stay in `library.db`, and dataset failures stay out of
that file's incompatible-storage recovery. The cross-provider IMDb rating read
of [phase 2](../imdb-datasets.md#phase-2-imdb-ratings-for-every-provider) retains
these protection and file-preservation rules. A changed local rating never
clears an OMDb/TMDB search hold or grants permission to contact those providers.
