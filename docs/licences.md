# Licence and attribution review

Reviewed 2026-10-13 against the checked-out source and public primary sources.
This records evidence and unresolved distribution questions; a Credits page alone
does not establish permission to distribute provider content or credentials.
No provider API was called with a key, no credentials were printed, and no build
or test was run for this review. Existing supplier evidence remains in
[the subtitle release assessment](subtitles-release.md).

## Provider notices and logos

Only TMDB explicitly requires its logo in the public terms verified here. Other
providers still receive a text credit and link, following the owner's decision.
The absence of a published logo requirement is not permission to use a trademark.

| Integration | Required notice or logo found | Current source and action |
| --- | --- | --- |
| TMDB API | An approved, unmodified logo and the exact notice: “This product uses the TMDB API but is not endorsed or certified by TMDB.” Attribution belongs in About/Credits; the logo must be less prominent than TinyTorrent's identity and must not imply endorsement. [Official FAQ](https://developer.themoviedb.org/docs/faq). | The native Credits page and prototype contain the logo and notice. Keep both inside the TMDB surface in the approved redesign. Preserve its colors and proportions. Asset provenance is in [Attribution.md](../app/src/Assets/Attribution.md). |
| IMDb downloadable datasets | Exact source acknowledgement: “Information courtesy of IMDb (https://www.imdb.com). Used with permission.” [Software-use rules](https://help.imdb.com/article/imdb/general-information/can-i-use-imdb-data-in-my-software/G5JTRESSHJBBHTGX). The [linking rules](https://help.imdb.com/article/imdb/general-information/how-do-i-link-to-a-specific-page-on-your-site/GTRTF6K8UA9JP9QQ) prohibit using an IMDb logo in a link without permission or a commercial content licence. | Required text is present in Credits and the Settings disclosure. Keep the logo out. This acknowledgement is the required formula, not evidence that the publisher has a commercial agreement. |
| Wikidata structured data | Structured data is CC0; text from other namespaces uses CC BY-SA 4.0. [Licensing policy](https://www.wikidata.org/wiki/Wikidata:Licensing). No required logo or prescribed credit found for this integration. | The provider reads structured SPARQL results, not wiki articles or media. Existing CC0 text credit is appropriate. A link to [CC0](https://creativecommons.org/publicdomain/zero/1.0/) makes its status clearer. |
| OMDb API | The [API key page](https://omdbapi.com/apikey.aspx) names Brian Fritz and licenses content under CC BY-NC 4.0. That licence requires reasonable attribution, a licence link, preservation of supplied notices, and identification of changes when applicable. [Licence section 3](https://creativecommons.org/licenses/by-nc/4.0/legalcode.en). No required logo found. | Credits includes OMDb API / Brian Fritz, the licence link and a display-formatting notice inside the OMDb row. Preserve any attribution notices supplied with content. Keep this content licence separate from TinyTorrent's MPL source licence. |
| MDBList | Published [combined terms and privacy policy](https://mdblist.com/static/legal.html) require compliance with third-party provider terms; they contain no prescribed credit or logo requirement. | A text credit is present, and the provider's privacy/terms disclosures use the verified combined policy URL. Public terms do not establish every upstream content right. |
| OpenSubtitles.com REST API | No explicit required logo or credit formula found in the verified [API overview](https://opensubtitles.tawk.help/article/about-the-api), [getting-started guide](https://opensubtitles.tawk.help/article/getting-started), or [website terms](https://opensubtitles.tawk.help/article/terms-of-service). | Existing text credit/link is appropriate as voluntary acknowledgement. Keep notices embedded in subtitle files. The applicable Consumer agreement may impose additional obligations; it was not available in this public review. Do not apply the unrelated MKV Player licence or old .org material to this REST integration. |
| SubDL | No explicit logo or credit requirement found in [terms](https://subdl.com/terms) or [developer docs](https://subdl.com/developers). Downloaded subtitles can have independent copyright. | Existing text credit/link is appropriate; preserve subtitle contents and any author notices. Do not interpret API access as ownership of subtitle files. |
| SubSource | No prescribed logo or app credit found. [Terms](https://subsource.net/terms) reserve translators' rights and prohibit altering subtitles; permitted re-synchronization/re-upload retains translator and sponsor names. | Existing text credit/link is appropriate. Keep downloaded SRT contents unchanged. The source downloads/extracts a selected file; it does not perform subtitle text editing or re-upload. |
| Cloudflare connection test | No required logo or attribution formula found in the reviewed [speed-test introduction](https://blog.cloudflare.com/test-your-home-network-performance/) or [official speedtest repository](https://github.com/cloudflare/speedtest). | Existing text credit is appropriate. Native code calls measurement endpoints directly; it does not ship the JavaScript module, so that module's MIT licence is not a shipped dependency merely because the service is used. |

Evidence for current notices: [native Credits](../app/src/Views/CreditsPage.xaml.cs),
[English catalogue](../app/src/Resources/en.json),
[Spanish catalogue](../app/src/Resources/es.json), and
[prototype Credits](../app/about-prototype.html).

The prescribed TMDB and IMDb statements retain their English wording in both
catalogues. Their legal quotations are source notices, not translated UI prose.
All provider credit rows remain, including voluntary acknowledgements, following
the owner's final decision. Only TMDB's required logo is included.

## Binary distribution notices

The inspected dependency graph is the restored application assets and existing
Debug output; the final release has not been built or verified in this review.
[installer/Build.ps1](../installer/Build.ps1) owns distribution notices. It uses
the published application's `.deps.json` to select runtime package notices,
which excludes unrelated build-tool packages. Credits provides licence-site
links and does not embed licence texts.

| Shipped component | Required material and source | Distribution owner |
| --- | --- | --- |
| libtorrent and bundled implementations | BSD and bundled-component notices in the pinned checkout's composite `LICENSE`. Its shorter `COPYING` omits those bundled notices. [Pinned source](https://github.com/SynoSoftware/libtorrent/blob/627695bf2026faebbc0c1ec8575e2a8700af7f27/LICENSE). | Installer copies the composite `LICENSE`. |
| OpenSSL | Apache-2.0 text in the pinned checkout's `LICENSE.txt`; no root `NOTICE` file exists there. [Licence](https://www.apache.org/licenses/LICENSE-2.0). | Installer reads the existing dependency owner. |
| nlohmann/json | MIT permission, copyright and disclaimer. [Upstream licence](https://github.com/nlohmann/json/blob/v3.12.0/LICENSE.MIT). | Installer reads the existing dependency owner. |
| Lucide, including Feather-derived icons | ISC and MIT copyright/permission notices. [Upstream licence](https://lucide.dev/license). | Installer reads `lib/Lucide/LICENSE`, which includes both sets of notices. |
| Fluent UI System Icons | MIT copyright/permission notice. [Upstream licence](https://github.com/microsoft/fluentui-system-icons/blob/main/LICENSE). | Existing artwork content includes `Assets/Settings/LICENSE`. |
| Microsoft.Data.Sqlite | MIT text and copyright; its restored package declares a licence expression without a root licence file. [Versioned source](https://github.com/dotnet/efcore/blob/v10.0.12/LICENSE.txt). | `installer/notices/Microsoft.Data.Sqlite.txt`, copied by the installer. |
| SQLitePCLRaw | Apache-2.0 text and package copyright; its restored packages declare expressions without root licence files. [Versioned source](https://github.com/ericsink/SQLitePCL.raw/blob/ed046114d5a30534e13294d94d78eb73de896ad4/LICENSE.TXT). | `installer/notices/SQLitePCLRaw.txt`, copied by the installer. |
| WebView2 SDK | BSD licence and supplied third-party notices. Core, Core.Projection and Loader binaries are present in the existing output. [Exact package licence](https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.3719.77/License). | Installer copies the restored runtime dependency's `LICENSE.txt` and `NOTICE.txt`. |
| Windows App SDK projections | Microsoft package terms and supplied notices. [Exact WinUI package licence](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.WinUI/2.3.6/License). | Installer copies licence/notice files for the packages represented in the published dependency graph. The separately installed Microsoft runtime retains its vendor distribution. |

SQLite is [public domain](https://sqlite.org/copyright.html), so its package's
public-domain declaration is not a required distribution notice. Boost's
[licence](https://www.boost.org/LICENSE_1_0.txt) exempts machine-executable object
code from notice inclusion. Both still receive voluntary credits and links.
The app uses a separately installed, framework-dependent .NET runtime
(`SelfContained=false`); the entire runtime's notice tree is not an application
payload. Perl, NASM, CMake and Ninja are build tools and are not shipped.

The root [LICENSE](../LICENSE) now contains Mozilla's
[official MPL 2.0 plain text](https://www.mozilla.org/media/MPL/2.0/index.txt),
as approved by the owner. The separate [branding notice](../resources/LICENSE)
continues to describe project artwork. Preserve source corresponding to each
released binary and tell recipients where to obtain it, as required by
[MPL section 3.2](https://www.mozilla.org/en-US/MPL/2.0/#distribution-of-executable-form).

### Microsoft distribution terms

Windows App SDK package terms section 3(b)(ii) require distributors and end
users to agree to terms protecting Microsoft's distributable code. The existing
[installer](../installer/Setup.iss) has no licence-acceptance step. Including
notices or linking to the terms does not establish that agreement.

The shipped Windows SDK projections (`Microsoft.Windows.SDK.NET.dll` and
`WinRT.Runtime.dll`) come from a targeting package whose metadata links to
[Windows SDK terms](https://aka.ms/WinSDKLicenseURL). It contains no root licence
file. The installed SDK terms were reviewed at
`C:/Program Files (x86)/Windows Kits/10/Licenses/10.0.26100.0/sdk_license.rtf`;
section 2(a) requires preservation of notices, protective end-user/distributor
terms and an eligible redistributable from the SDK's REDIST list. The installed
kit and targeting package contain no REDIST file, and the linked list could not
be retrieved here. Verify the exact projection redistribution grant and the
required recipient agreement before release; the CsWinRT source repository's
MIT licence does not alone establish the targeting package's distribution terms.

## Permission and distribution questions

These are distinct from missing credits. Resolve the applicable terms and the
publisher's actual use before release; do not buy plans, contact suppliers,
remove integrations or change the settled built-in-key policy without the
owner's decision. The owner permits built-in free application keys only where
the applicable free terms cover them, as stated in
[third-party provider access](architecture.md#third-party-provider-access).

### TMDB

The [FAQ](https://developer.themoviedb.org/docs/faq) permits free noncommercial
API use and describes a project whose primary purpose is revenue for its owner
as commercial. Free/open-source distribution by a company does not by itself
answer that purpose test. Record TinyTorrent's intended use and the terms
accepted for its application key.

An indexed copy of the official [API terms](https://www.themoviedb.org/api-terms-of-use?language=en-CA)
contains a six-month cache limit. The live terms page denied automated retrieval;
the indexed copy is older and cannot establish the current accepted agreement.
This is a concrete verification item, not a reason to invent a 180-day limit.
If the applicable terms retain it, the present persistent cache is insufficient:
[Store/Videos.cs](../app/src/Library/Store/Videos.cs) reads saved records and
derived information without an age limit or deletion, while
[Tmdb/Provider.cs](../app/src/Library/Tmdb/Provider.cs) stores retrieval timestamps.
Check search choices and derived saved information as well as raw responses.

The current TMDB adapter requests search, movie/series/episode information,
credits, keywords and recommendations, not watch-provider availability. The
separate [JustWatch attribution requirement](https://developer.themoviedb.org/reference/tv-series-watch-providers)
therefore does not apply to the inspected endpoint set.

### IMDb

The [dataset page](https://data.imdb.com/non-commercial-datasets/) permits local
copies for personal, noncommercial use. Its linked [software-use rules](https://help.imdb.com/article/imdb/general-information/can-i-use-imdb-data-in-my-software/G5JTRESSHJBBHTGX)
limit source acquisition to the datasets and forbid republishing, reselling or
repurposing them into a movie database except for individual personal use.
[Dataset.cs](../app/src/Library/Imdb/Dataset.cs) downloads the official basics,
ratings and episodes archives on request and imports them locally; it does not
scrape IMDb. That fits the stated personal-local-use route when the user's use
is personal and noncommercial. Do not bundle the imported database, mirror the
archives, export a shared catalogue, or describe unrestricted commercial data
rights. Confirm the distributed app's intended use stays within that scope.

### Wikidata

The [API usage guidelines](https://foundation.wikimedia.org/wiki/Policy:Wikimedia_Foundation_API_Usage_Guidelines)
require following rate-limit responses, correct identification and applicable
content licences. The [User-Agent policy](https://foundation.wikimedia.org/wiki/Policy:Wikimedia_Foundation_User-Agent_Policy/en)
requires descriptive identification with contact information for automated
clients. [App.cs](../app/src/App.cs) supplies `TinyTorrent/version` with the
project/contact URL; [ProviderHttp.cs](../app/src/Services/ProviderHttp.cs) applies
that shared value. CC0 permits commercial reuse of
structured data, but does not exempt the API's operating rules.

### OMDb

[CC BY-NC 4.0](https://creativecommons.org/licenses/by-nc/4.0/legalcode.en)
does not permit commercial content use. Attribution alone does not fix that.
The [site/service terms](https://www.omdbapi.com/legal.htm) also limit
Contributions to personal noncommercial use and restrict indexing and derivative
uses. Record which terms/grant cover a distributed application, local structured
caching and the release's built-in key; the public key signup does not expressly
authorize sharing one key across installations. A free application price does
not determine whether its content use is noncommercial. The site's dedicated
Poster API is patron-only; the inspected adapter does not call that API.

### MDBList

[API documentation](https://docs.mdblist.com/docs/api) offers free keys and
account-wide allowances shared by keys and apps. Its [authentication guide](https://mdblist.docs.apiary.io/reference/external-lists/list-items)
describes API keys for personal scripts/testing and OAuth for third-party apps.
This is guidance, not an express ban on app keys. Neither source nor the
[published terms](https://mdblist.com/static/legal.html) establishes a broad
grant to distribute one developer key or indefinitely retain/redistribute every
upstream provider's data. Verify those points for the registered application.
The inspected adapter uses metadata lookups, not user lists or OAuth-protected
account operations; do not introduce OAuth solely from general guidance.

### OpenSubtitles

The [API overview](https://opensubtitles.tawk.help/article/about-the-api)
offers free access and optional professional packages. The [getting-started guide](https://opensubtitles.tawk.help/article/getting-started)
requires an application key and app/version identification and documents limited
anonymous downloads. The [Pro guide](https://opensubtitles.tawk.help/article/pro-packages)
allows development/testing before plan selection. Those pages establish a free
development route, not the particular Consumer agreement TinyTorrent accepted.
Its [website terms](https://opensubtitles.tawk.help/article/terms-of-service)
prohibit commercial use while the API materials offer commercial packages; keep
those scopes separate and verify the actual agreement, key distribution,
content rights and required notices for the shipped free integration. Do not
conclude a paid package is mandatory for every noncommercial release.

### SubDL

Current [terms](https://subdl.com/terms), updated September 6, 2026, permit
commercial API use with a paid plan, prohibit a shared free key powering a
commercial service, and expressly permit each user bringing their own free or
paid key. No pooled keys or limit bypass is allowed. Terms do not expressly
address a shared free key in a noncommercial distributed app. Record whether
TinyTorrent's use is commercial and whether its shared free-key route is
covered; if commercial, the present built-in-free-key policy conflicts unless
separately authorized. Existing personal-key replacement is already supported.

### SubSource

[API docs](https://subsource.net/api-docs) tell developers to keep keys secure
and not share them publicly. [SubSource.cs](../app/src/Subtitles/Supplier/SubSource.cs)
can read a built-in key from assembly metadata, which is recoverable in a
distributed binary. The owner ruling to ship this key therefore needs an
applicable exception/authorization from SubSource; binary embedding is not
secret storage. The docs establish endpoints, per-key limits and profile keys,
but the [terms](https://subsource.net/terms) do not expressly settle distributed
application use or commercial API/content licensing. Treat that as unresolved,
not as either a blanket prohibition or permission.

### Cloudflare

The [official announcement](https://blog.cloudflare.com/aim-database-for-internet-quality/)
invites applications to use its open-source speed test; the public repository
documents requests against Cloudflare's edge network. Native
[ConnectionTest.cpp](../engine/src/Engine/ConnectionTest.cpp) independently calls
`/__down` and `/__up`. No separate native-client service grant or quota agreement
was located. Record the endpoint terms that cover the release's traffic rather
than treating a JavaScript MIT licence as a service contract. Cloudflare's
[privacy explanation](https://blog.cloudflare.com/test-your-home-network-performance/)
also makes it a recipient to disclose when the person runs the connection test;
do not infer the native client sends the JavaScript module's completed-result
telemetry, because the inspected native path does not do that.

## Release evidence still required

- The publisher asked this review to use published free terms only; no separate
  provider permissions or agreements were supplied.
- Microsoft's applicable redistribution grant and recipient agreement for
  shipped SDK components, as described above.
- Applicable key/account agreements and publisher use classification for the
  providers above. A working development key or a successful download is not
  evidence of redistribution rights.
- Current TMDB cache terms and, if applicable, cache retention implementation.
- An accurate published privacy policy covering actual recipients, request
  inputs, account handling, network routing and contact; the existing
  [subtitle privacy document](subtitle-privacy.md) remains a draft.
- Verification against the final shipped distribution: code/source licences,
  native and managed transitive dependencies, notices and any modified
  third-party files are separate from API/content permission.

This is an evidence review, not a replacement authority for feature behavior.
The Library, subtitle and architecture contracts retain those decisions.
