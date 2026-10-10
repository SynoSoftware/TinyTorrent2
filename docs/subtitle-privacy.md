# Automatic subtitles: privacy policy draft

Draft for the planned feature, 2026-10-08. Merge this into TinyTorrent's single
public privacy policy before release. The HTML prototype performs no supplier
requests and keeps sample entries only in memory. This text is not a claim that
the production feature or a supplier agreement already exists.

## What the feature does

When you enable Automatic subtitles, TinyTorrent searches your selected supplier
for subtitles in your chosen languages and saves matches beside your movies.
Searching can begin before the movie finishes while the product window is open.
Closing the window pauses subtitle work; reopening resumes accepted work from
saved records. Movies added and finished entirely while the window was closed
need Find for finished downloads, as do movies that finished before you enabled
the feature. Opening setup does not enable automatic searching.

## Information used

TinyTorrent uses the movie's filename locally to identify its title and release.
A lookup sends only the identification fields it needs: movie/release name or
identifier, chosen languages, and file size or a locally calculated movie
fingerprint when needed. A fingerprint can identify a movie; it is not anonymous
merely because it is a hash. The supplier sees the network address used for the
request and the required application/version identification.

The default OpenSubtitles integration uses TinyTorrent's free application key,
without requiring an end-user account. If you enter an optional OpenSubtitles
account, its credentials are sent to OpenSubtitles for authentication. SubDL and
SubSource each require your own API key for that supplier, which is sent with
each request to it; neither receives movie fingerprints. TinyTorrent protects saved
credentials using Windows user protection
and uses encrypted HTTPS connections. It does not send movie contents, torrent
info hashes, magnet links, trackers, peers, absolute folder paths, unrelated
filenames, or a library inventory to the subtitle supplier.

## Local records and sharing

Settings, pending requests, and associations between movies and downloaded
subtitles are kept locally to apply your choices, avoid duplicate work, recover
after interruption, and follow known movie moves while the window is open or
when it next reconciles still-managed torrents. C# manages these records and
files; no subtitle work runs while the window is closed. Removing a torrent deletes
its subtitle records when the window observes removal or next reconciles with
the engine. Remove leaves subtitle files on disk. Delete files from the window
also tries to delete subtitles TinyTorrent created when no other managed file
uses them; subtitles found on disk remain untouched. Downloaded subtitles are
ordinary local files. TinyTorrent does not operate a subtitle mirror or upload
those subtitle files through this feature. Routine diagnostics exclude secrets
and matching queries.

The selected supplier processes requests under its applicable terms and privacy
policy. Supplier information identifies the service and links to its policy.
The final policy must distinguish supplier processing from anything the
TinyTorrent publisher itself receives; it must not promise that third parties
keep no records.

## Your choices

Automatic subtitles starts Off until setup is ready and you enable it. Turning
it Off stops new lookups and retries and leaves existing files in place. To
remove credentials held by TinyTorrent, open the supplier dialog in Settings,
clear both OpenSubtitles account fields or, for SubDL or SubSource, choose
another supplier,
and Save. This does not delete an account or records held by the supplier. Changing
supplier requires enabling for the new supplier. You can change languages
without repeating setup. When you have not chosen subtitle languages, the current
interface language is used. Explicit subtitle choices remain independent.
Privacy requests concerning supplier-held information
follow the identified supplier's privacy contact.

## Facts required before publication

- Publisher/controller identity, privacy contact, effective date, and markets.
- Actual supplier legal identity, policy and terms URLs, API recipients and
  retention; any applicable international-transfer arrangements.
- Verified HTTPS routing through the configured torrent proxy/adapter for all
  subtitle requests, including setup checks, with no direct fallback. This is
  the selected behavior; implementation evidence is still required.
- Local retention/deletion periods or criteria and implemented account-clearing
  behavior, including any relevant diagnostics or backups.
- Applicable processing basis and user-rights/contact information, including
  access, deletion, withdrawal and complaint routes where applicable.

These facts belong in the final public policy. A draft, dead link, or the
supplier's policy alone does not substitute for TinyTorrent's policy. Release
evidence is tracked in [the subtitle assessment](subtitles-release.md).
