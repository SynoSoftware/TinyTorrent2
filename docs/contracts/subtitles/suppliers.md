# Supplier access and privacy

Part of the [subtitle contract](../subtitles.md). Read its shared outcome,
ownership and recovery rules before the sections here that the change affects.

OpenSubtitles.com access follows the free application key and optional login in
[configuration](configuration.md#configuration), subject to release authorization. Its
[official overview](https://opensubtitles.tawk.help/article/about-the-api)
documents searches by title, release name, IMDb ID, and movie hash. Its
[hash reference](https://opensubtitles.github.io/oshash/) specifies the required
video bytes, and its
[integration guide](https://opensubtitles.tawk.help/article/getting-started)
describes API keys and download quotas. These establish a possible integration
route, not proof that TinyTorrent has acquired the necessary rights.
[Licences](../licences.md#opensubtitles) records the evidence and gaps.

Each shipped supplier needs terms covering the intended application distribution,
automated search and local subtitle downloads. Use its authorized API, supported
authentication, quotas, and retry instructions. Request subtitles for managed
movies rather than harvesting a catalog; save them locally without a TinyTorrent
subtitle mirror or redistribution service. Preserve required notices in files.
Use a small supplier credit/link in Settings and any required notices in About;
confirm placement against the agreement rather than inventing branding rules.

Send only matching inputs the selected lookup needs: a movie/release name or
identifier, requested languages, and file size/hash when applicable. Compute
hashes locally. Torrent info hashes, magnets, trackers, peer lists, absolute
paths, unrelated filenames, movie bytes, and library inventories stay out of
supplier requests. Authentication and the required app/version identification
are separate from matching. Use HTTPS, protect saved credentials, and exclude
credentials, tokens, signed download links, and search contents from routine
logs. The supplier necessarily sees the request's network address; a hash is
not a promise of anonymity.

Publish one accurate TinyTorrent privacy policy, accessible from Settings,
About, and the Store listing. For this feature it states what this section
sends, the local records and their retention in [file handling](files.md#subtitle-records-and-accepted-work), and the
publisher's identity, contact, recipients and routing. Supplier website
policies do not establish API-specific retention.

All supplier operations follow the product's [network route](../architecture.md#network-route).
Verify the shared transport before describing its behavior as provided.

### SubDL

**Owner ruling: SubDL is the second supplier.** It gives development a supplier
that can be tested with the person's own key alongside OpenSubtitles' free
application-key access. Facts below come from
SubDL's [API documentation](https://subdl.com/developers) and
[terms](https://subdl.com/terms), read 2026-10-08.

- Access is the free API key built into the release, or the person's own key
  from their SubDL account, which replaces it. SubDL's terms, read
  2026-10-13, require a paid plan for commercial use and say a free key must
  not be used as a shared key to power a commercial service for its users;
  they allow an app in which each user brings their own key. They do not
  address a shared key in a non-commercial app. The built-in key follows the
  [owner ruling](../architecture.md#third-party-provider-access). TinyTorrent
  asks only for an API key, never a SubDL password.
- Matching uses the release filename search, `GET /api/v2/files/search`, and
  accepts an individual SRT only when its release and language match exactly
  under the [matching rules](matching.md#early-lookup-and-matching). Explicit forced or foreign-part labels are
  rejected. SubDL has no movie-hash search, so [step 4's completed-file hashing](matching.md#early-lookup-and-matching)
  does not apply; filename search can start as soon as metadata names the file.
- Check calls `GET /api/v2/me`, which needs no movie information; a reply
  confirms the key.
- A free key allows 2,000 searches and 50 downloads a day. Quota waits use the
  shared rate-limit handling: remaining-allowance and reset headers,
  `Retry-After`, and 429 and 402 replies.
- Downloads default to a ZIP archive; request the single subtitle file and
  accept only SRT, under the [saving rules](files.md#saving-and-following-the-movie).

### SubSource

**Owner ruling: SubSource is the third supplier.** Facts below come from
SubSource's [API documentation](https://subsource.net/api-docs) and
[terms](https://subsource.net/terms), read 2026-10-08.

- Access is the API key built into the release, or the person's own key,
  generated under My Profile on subsource.net, which replaces it. Either is
  sent as `X-API-Key`. SubSource limits requests per key, so every
  installation shares the built-in key's limits.
- Matching takes two steps. `GET /movies/search` finds the title by text, with
  the season for an episode; TinyTorrent compares the year itself and gives up
  when more than one title matches. `GET /subtitles` then lists that title's
  subtitles by `movieId` and language. Accept a result only when its
  release information names the file's release, under [rule 3](matching.md#early-lookup-and-matching). Results marked
  `foreignParts` are forced-only and never accepted. SubSource has no
  movie-hash search, so [step 4's completed-file hashing](matching.md#early-lookup-and-matching) does not apply. A
  movie year may be missing from the title search only while exact release
  matching and a unique movie identity still protect the download.
- Language filters use names such as `english`; the shipped catalog maps
  stable identifiers to them.
- Every call counts against 60 requests a minute, 1,800 an hour, and 7,200 a
  day per key; responses carry rate-limit headers. No separate download quota
  is documented.
- Downloads are always ZIP archives, unpacked under the [saving rules](files.md#saving-and-following-the-movie).
- No account-information endpoint is documented. Check sends one fixed title
  search unrelated to the person's movies; a rejected key returns 401.
- The terms say subtitles belong to their translators and must not be
  altered, which the rule to save text unchanged already satisfies. They say
  nothing about API use by applications; [licences](../licences.md#subsource)
  records that gap.

Keep matching, languages, file work, and recovery independent of a supplier's
account model. The three suppliers differ in credentials, search steps, the
access check, download format, and quota reporting; one supplier interface
owns those differences.
Legal uncertainty is resolved at release, without delaying matching until movie
completion or disguising torrent features.
