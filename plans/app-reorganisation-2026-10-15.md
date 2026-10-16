# Organization of app/

16 October 2026 · Implemented · Owner's 15 October decisions retained · Normal Debug x64 build passed

## Decision and scope

Use seven source areas: Shell, Torrents, Settings, Library, Subtitles, Shared and Controls. Each area starts flat and uses one namespace. Only the supporting folders listed below create nested namespaces; partial-type folders keep their owner's namespace.

This revision retains the owner's 15 October decisions, including withdrawal of the earlier non-flat-tree requirement, and reconciles the proposal with the 16 October working tree at ec8979d. The owner subsequently authorized execution. Source, direct consumers and naming policy now follow this layout; the relocation map retains the pre-move paths as the implementation record.

Keep one WinUI project, direct concrete dependencies and the existing behavioral owners. Add no interfaces, duplicate presentation implementations or forwarding layers. The changes are file placement, namespaces and their direct consumers, the Settings → Editor rename, and moving the existing RelayCommand declaration into its own shared file.

On 5 October 2026, commit 0f3ff542 justified role folders by familiarity across the owner's other WinUI applications: the same role lived in the same place in each app. The owner now accepts the seven-area split and gives up those identical role-folder locations in exchange for locality within each area. This is a deliberate tradeoff; the other applications are outside this task.

## Changes since the previous inventory

Six files were added and one asset was replaced since the abb170ff snapshot. Their destinations follow the accepted owners; the seven-area design and the Settings → Editor rename are unchanged.

| Current change | Destination and treatment |
| --- | --- |
| Controls/Speedometer.xaml and Speedometer.xaml.cs added | Settings/; ConnectionPage is their sole consumer. Keep the Speedometer name and update its XAML class and imports with its code-behind. |
| Controls/PeerMap/Distribution.cs added | Torrents/PeerMap/Distribution.cs; keep it a PeerMap partial in Syno.TinyTorrent.Torrents, alongside Navigation.cs. |
| MainWindow/Connections.cs added | Shell/MainWindow/Connections.cs; preserve window placement, map scope and return coordination. |
| MainWindow/CaptureConnections.cs added | Shell/MainWindow/CaptureConnections.cs; retain the MainWindow partial and coverage by the moved Capture*.cs exclusion. |
| tests/DistributionChecks.ps1 added | Retain its path and behavior; update its three reflected type names below. |
| Assets/world.json replaced by Assets/world.bin | Retain world.bin and its embedded resource name. Geography reads this binary; do not restore the removed JSON asset. |

Existing files also changed for connection setup, peer rendering and navigation, and Library's retained file catalogue. Move their current contents, including InspectorPane's InspectorLayout and PeerView declarations, without restoring content from the earlier snapshot. The current enum members and values, database schema, resource keys and feature behavior stay as they are.

## Areas and namespaces

All namespaces below have at most four segments. The source root retains App, TinyTorrent.csproj and app.manifest, and receives Enums.cs in Syno.TinyTorrent. Assets, Resources and Properties keep their existing paths.

| Folder under app/src | Namespace | Ownership |
| --- | --- | --- |
| Shell/ | Syno.TinyTorrent.Shell | MainWindow, MainViewModel, Navigation, AboutPage, CreditsPage and the complete InspectorPane family. |
| Torrents/ | Syno.TinyTorrent.Torrents | Torrent state, Inspector, Peer, Tracker, Pieces, torrent-specific controls, addition, file operations and speed-limit editing. |
| Settings/ | Syno.TinyTorrent.Settings | Editor, SettingsPage, connection/proxy setup, scheduling, limits, theme and Windows integration. |
| Library/ | Syno.TinyTorrent.Library | Existing Library presentation, state, lookup, catalogue and persistence operations. |
| Library/Imdb/, Tmdb/, Wikidata/, Omdb/, MdbList/ | Syno.TinyTorrent.Library.<provider> | The provider folders exactly as they are today. |
| Subtitles/ | Syno.TinyTorrent.Subtitles | Acquisition and its parts, Supplier, Settings, Automatic, Batch, ChoiceDialog, LanguageDetection and SubtitleFile. |
| Subtitles/Suppliers/ | Syno.TinyTorrent.Subtitles.Suppliers | OpenSubtitles, SubDL and SubSource, keeping their names and language parts. |
| Shared/ | Syno.TinyTorrent.Shared | Common dialog, account, file, dataset, work, text, geography and command facilities listed below. |
| Shared/Engine/ | Syno.TinyTorrent.Shared.Engine | PipeClient and its supporting declarations and parts. |
| Shared/Storage/ | Syno.TinyTorrent.Shared.Storage | Database and its supporting declarations and parts. |
| Shared/Network/ | Syno.TinyTorrent.Shared.Network | ProviderHttp, RequestPolicy and their supporting declarations and parts. |
| Shared/Desktop/ | Syno.TinyTorrent.Shared.Desktop | DiskSpace, RecycleBin and ShellAssociation. |
| Shared/Diagnostics/ | Syno.TinyTorrent.Shared.Diagnostics | CrashDialog and ExceptionLog. |
| Controls/ | Syno.TinyTorrent.Controls | Reusable UI controls and framework mechanics. |

Shared's root contains Dialog, DialogFooter, DialogPreference, IDraft, AccountDialog, Credentials, Disclosure, FileName, FileSources, TorrentSource, DataDownload, DatasetRow, IDataset, BackgroundWork, WorkObservation, WorkStatus, Strings, Geolocation, Geography and RelayCommand. Related declarations already in those files stay together.

Torrents, Settings and Controls have no additional responsibility folders. Library keeps Browser/ and Store/ as parts folders alongside its existing provider folders. Subtitles changes Supplier/ to Suppliers/; the Supplier base stays at the Subtitles root. OpenSubtitles/Languages.cs, SubDL/Languages.cs and SubSource/Languages.cs remain partial files in the Suppliers namespace.

MainWindow/, MainViewModel/, Inspector/, InspectorPane/, PeerMap/, SettingsPage/, Store/, Browser/, Acquisition/, PipeClient/, Database/, ProviderHttp/ and RequestPolicy/ remain parts folders. They create no namespace and keep their current partial owner. A part's physical depth does not add namespace segments.

## Inspector, controls and behavioral ownership

InspectorPane moves permanently to Shell with its XAML, code-behind and Header, Library and Peers partials intact. It composes the torrent and Library tabs for its one consumer, MainWindow. Its existing supporting declarations stay with it. Only namespace and direct-reference adjustments are required.

Inspector.cs and Inspector/Peers.cs move to Torrents with Peer, Tracker, Pieces, PeerMap and its Navigation and Distribution partials, PiecesMap and SpeedGraph. These remain the torrent-specific state, projections and visual controls. Library's existing Browser and Details retain Library selection, commands and information/file presentation.

Pane remains one control in Controls. InspectorPane contains Pane; it does not inherit from it. Filters use the same Pane implementation. MainWindow retains placement, split height, collapse/maximize and focus coordination. Shell composition does not give it ownership of feature rules.

| Existing authority | Destination and preserved responsibility |
| --- | --- |
| MainWindow and MainViewModel | Shell; retain window/native interaction, feature lifetime, selection and command coordination. Their partials move together, including Connections and every Capture partial. |
| Settings (renamed Editor), Schedule and Proxy | Settings; retain confirmed settings, drafts and submission rules. MainViewModel.Settings keeps its property name. |
| VideoLookup, Store and IProvider | Library; lookup/identity/cache policy remains with Library, and providers keep only their specific behavior. |
| Acquisition and Supplier | Subtitles; scheduling, cancellation, matching and storage remain with the existing host, with concrete suppliers under Suppliers. |
| Database | Shared/Storage; one connection, schema and transaction authority. Feature SQL stays with its current feature owner. |
| ProviderHttp and RequestPolicy | Shared/Network; one route and request-protection implementation. |
| FileSources and TorrentSource | Shared root; retain the existing shared membership operations. |
| FileBrowser and FileSelection | Controls root; one visual implementation shared by existing workflows. |
| RelayCommand | Shared root; move the existing declaration unchanged out of MainViewModel.cs. SnapshotAppliedEventArgs and TransferTotals stay with MainViewModel. |
| TableView and Lucide | Their existing library projects; no copies or replacement libraries. |

Feature-specific controls follow their existing consumers. PeerMap, PiecesMap and SpeedGraph go to Torrents. ThemePicker, WindowsIntegration, LimitSelector, SpeedLimits, Speedometer, PeriodEditor, Scheduler and Week go to Settings. DatasetRow, WorkStatus and Disclosure go to Shared. CrashDialog goes to Shared/Diagnostics. The remaining reusable controls stay flat in Controls; Motion and TextEditor join them.

## Dialog ownership

Shared contains the one common Dialog implementation, its footer, draft contract and preference support. The current Dialog is sealed and inherits WinUI ContentDialog. Feature dialog content is usually a UserControl placed inside it; the feature content does not inherit from Dialog.

Content and draft files live with the operation they serve, even when another area opens them.

| Content | Home | Reason |
| --- | --- | --- |
| AddDialog and AddDraft | Torrents | Torrent addition owns the edit state and submission. |
| FileDialog and FileDraft | Torrents | Torrent move/removal remains the owner even when Library starts the operation. |
| ProxyDialog and Proxy | Settings | Proxy editing and checking share one draft owner. |
| MetadataDialog and BatchDialog | Library | They select Library metadata work. |
| ChoiceDialog | Subtitles | It selects subtitle work. |
| AccountDialog | Shared | Providers and suppliers use the same account-content implementation. |

Dialog owns common presentation, buttons, footer, sizing and initial focus. MainWindow's existing Interact, ShowDialog and ShowEditor methods remain the single coordination path for one-at-a-time display, XamlRoot/theme, text refresh and draft submission. Feature content supplies fields and outcomes without copying those rules.

The existing native CrashDialog remains the failure-path fallback in Shared/Diagnostics.

## Exact naming policy edits

The implementation updates docs/naming.md in place. Its scope-repetition rule, “start flat within an owner” rule, four-segment cap and partial-folder convention remain unchanged.

Replace the App folders section with the seven accepted areas and the supporting-folder/ownership rules above. Remove the obsolete role-folder statement in the Files section so it points to area ownership under App folders. Record the 5 October tradeoff in the replacement section.

Record these specific owner-accepted repetitions in the app section: SettingsPage because Page collides with WinUI, and LibraryTable and SubtitleFile because they are established names. These named decisions do not relax the repository's general scope-repetition rule.

Replace the managed enum-placement sentence with one rule for every managed project:

> Managed projects keep Enums.cs beside their project file, in the project's root namespace.

Keep the existing one-file, one-member-per-line enum rules and the native engine's Enums.h rule. The app's Enums.cs therefore moves to app/src/Enums.cs in Syno.TinyTorrent; consumers in its child namespaces need no enum-specific using directive. Enum declarations and values stay unchanged.

Rename only the settings editor type and its file from Settings to Editor. Preserve its related Setting declarations in that file. Geography retains its name at Shared's root. OpenSubtitles, SubDL and SubSource retain their names in the single Suppliers namespace. RelayCommand keeps its existing implementation and name.

## Namespace and member-name audit

The 15 October review checked the requested namespaces against MainViewModel, MainWindow, Supplier and Browser, including their partial files and MainWindow's XAML names. The 16 October source refresh rechecked those names and the new partials. Actual hiding members exist, but ordinary type imports avoid introducing ambiguous namespace expressions. No new namespace aliases are required by this relocation.

| New namespace name | Existing member or finding | Required treatment |
| --- | --- | --- |
| Shell | No same-named member in the four checked types. | Import needed types normally. |
| Torrents | MainViewModel.Torrents and MainWindow's generated Torrents field. | Import Syno.TinyTorrent.Torrents and use Torrent, Inspector and other type names directly. Do not introduce Torrents.Inspector expressions inside these classes. |
| Settings | MainViewModel.Settings. | Import Syno.TinyTorrent.Settings and use Editor directly. Keep the Settings property and Model.Settings bindings. |
| Shared | MainViewModel.Shared(Func<Torrent, bool>) in its Actions part. | Import the relevant Shared namespace and use Strings, RelayCommand and other type names directly. Do not introduce Shared.Strings expressions inside MainViewModel. |
| Suppliers | No same-named member in the four requested types. Acquisition.Suppliers is an additional existing case. | Supplier imports Syno.TinyTorrent.Subtitles.Suppliers and continues using the concrete class names. Acquisition keeps its existing SupplierType alias and avoids new Suppliers.Type expressions. |

Supplier and Browser have no members named Shell, Torrents, Settings, Shared or Suppliers. Browser's ShowSettings and SettingsRequested are different identifiers.

Retain the existing, specific aliases below; their targets remain as shown. These already disambiguate real types, and the folder move does not justify replacing them with a general alias scheme.

| Alias | Target | Consumers |
| --- | --- | --- |
| LibraryBrowser | Syno.TinyTorrent.Library.Browser | MainViewModel, MainWindow/Library, InspectorPane/Library. |
| ImdbProvider, TmdbProvider, WikidataProvider, OmdbProvider, MdbListProvider | Syno.TinyTorrent.Library.<matching provider>.Provider | MainViewModel/Features. |
| ImdbDataset | Syno.TinyTorrent.Library.Imdb.Dataset | MainViewModel/Features. |
| SubtitleAcquisition | Syno.TinyTorrent.Subtitles.Acquisition | MainViewModel/Features. |
| SupplierType | Syno.TinyTorrent.Subtitles.Supplier | Acquisition and Acquisition/Acquire. |
| Supplier | Syno.TinyTorrent.Subtitles.Supplier | App. |
| SubtitleSettings | Syno.TinyTorrent.Subtitles.Settings | SettingsPage and SettingsPage/Subtitles. |
| ProviderOption | Syno.TinyTorrent.Library.ProviderOption | SettingsPage. |
| Selection | Syno.TableView.Selection | Library/Browser. |

Update existing qualified expressions as well as using directives: Services.Strings becomes imported Strings in MainWindow/Actions and SettingsPage; Services.Database becomes imported Database in MainWindow/Capture; Services.CommandException becomes imported CommandException in CaptureEdits; Browser's Services.RequestBlock becomes imported RequestBlock. Controls.Dialog becomes imported Dialog in MainWindow/Library, Datasets and CaptureSettings; Views.ConnectionPage and Views.SettingsPage in CaptureSettings become imported type names.

PeerMap and its Distribution partial now use Models.Peer explicitly; replace those type references with Peer in their shared Torrents namespace. MainViewModel/About uses Models.WindowPage, which becomes WindowPage at the project root. Preserve qualification where a member hides the type: Library/Imdb/Dataset's Models.FileName.Normalize becomes Syno.TinyTorrent.Shared.FileName.Normalize because Dataset already has a FileName constant. Pane's Models.Dock references can use Syno.TinyTorrent.Dock beside its Dock property. None of these changes calls for another alias or a renamed member.

The Editor rename must cover constructors, types, static calls and nameof. Source checks identified Settings.TryInteger in Proxy and SettingsPage; Settings.RateInput and Settings.TryRate in SpeedLimit; nameof(Settings.IsPending) and Settings.TryNumber in ConnectionSetup; and Settings.RateInput/TryNumber in the existing Settings.cs. Change these type references to Editor while preserving the MainViewModel.Settings property.

During implementation, update XAML x:Class, xmlns mappings, typed resources and x:DataType/type references with their owning C# namespaces. Multiple areas may need separate prefixes where the former Views or Controls prefix covered them together. Preserve resource keys and template-part names.

## Paths and direct consumers

| Consumer | Required change |
| --- | --- |
| app/src/TinyTorrent.csproj | Change the conditional Compile Remove from MainWindow\Capture*.cs to Shell\MainWindow\Capture*.cs, including CaptureConnections.cs. Keep the project path, root namespace, project references and resource/copy/publish configuration, including Assets\world.bin with LogicalName world.bin. |
| CopyEngine.ps1 and CountryData.ps1 | Keep both at app/. Keep their existing ..\CopyEngine.ps1 and ..\CountryData.ps1 UpToDateCheckInput paths and Exec paths unchanged. |
| app/tests | Keep every script and Store fixture at its present path. Preserve script-relative paths and default binary locations. Update only type-name references affected by the namespace/type changes. |
| app/prototypes | Move all 19 existing HTML/Markdown prototype files here with no subfolders. Preserve basenames and tracked/ignored status; adjust live documentation links and relative URLs as needed. |
| .gitignore, prototype entry | Change /app/video-information-prototype.html to /app/prototypes/video-information-prototype.html. |
| .gitignore, Store entry | Keep /app/tests/Store/{ECE5E9CC-3FBB-452B-96ED-7F8E895285EE}.resume unchanged. |
| app/AGENTS.md | Change the dialog authority reference from Controls.Dialog to Shared.Dialog and update any affected paths, preserving its behavior rules. |
| Documentation | Update current source links/maps in docs/architecture.md, docs/architecture-current.md and other live references found by searching old paths; update prototype links. Keep historical prose clearly historical. |
| App and consumers outside moved files | Update imports and explicit type/XAML references to moved owners; no compatibility namespaces. |

Both anchored .gitignore entries are accounted for. The accepted scope keeps the Store path and its ignore line fixed, so only the prototype line changes.

AddSourceChecks.ps1 contains the following reflected names. Each must follow the declaration, including supporting types that share a file:

| Existing full name | Replacement full name |
| --- | --- |
| Syno.TinyTorrent.Services.Strings | Syno.TinyTorrent.Shared.Strings |
| Syno.TinyTorrent.Views.AddSource | Syno.TinyTorrent.Torrents.AddSource |
| Syno.TinyTorrent.Views.Settings | Syno.TinyTorrent.Settings.Editor |
| Syno.TinyTorrent.Views.Setting | Syno.TinyTorrent.Settings.Setting |
| Syno.TinyTorrent.Views.DialogPreference | Syno.TinyTorrent.Shared.DialogPreference |
| Syno.TinyTorrent.Services.PipeClient | Syno.TinyTorrent.Shared.Engine.PipeClient |
| Syno.TinyTorrent.Models.SettingKind | Syno.TinyTorrent.SettingKind |
| Syno.TinyTorrent.Models.SettingsCategory | Syno.TinyTorrent.SettingsCategory |

DistributionChecks.ps1 adds three reflected names that must follow PeerMap and Geography, including Geography's nested Area and the GeoPoint declaration in the same file:

| Existing full name | Replacement full name |
| --- | --- |
| Syno.TinyTorrent.Controls.PeerMap | Syno.TinyTorrent.Torrents.PeerMap |
| Syno.TinyTorrent.Models.Geography+Area | Syno.TinyTorrent.Shared.Geography+Area |
| Syno.TinyTorrent.Models.GeoPoint | Syno.TinyTorrent.Shared.GeoPoint |

Apply the same declaration-based updates to reflection consumers in the other existing scripts: NavigationChecks references Navigation (now Shell.Navigation) and enums; ProviderChecks references network types and enums; LibraryTiming references Services.Database and LibraryConfiguration; SourceChecks references shared owners, enums and OpenSubtitles through both full names and suffix strings passed to Get-Type/New-Value. Its Subtitles.OpenSubtitles suffix must follow that declaration into Subtitles.Suppliers. Keep the scripts, fixtures and test behavior in place. Search literal type names across the repository, not only C# imports.

## Implementation sequence and bounded validation

The following sequence records the approved implementation scope. Execution and its bounded validation are complete; evidence is recorded below.

1. Reinventory the owner-prepared working tree and reconcile any changed paths with the map. Preserve all existing work; do not stash, reset or commit it. Register the implementation's build inputs under docs/compilation.md before editing them.
2. Apply the relocation map and the one RelayCommand declaration move. Update namespaces, Editor references, XAML references, reflection names, the conditional Capture exclusion, documentation, naming policy and the prototype ignore path in the same change.
3. Before any build, run the cheap source checks below and review the complete diff for accidental behavior changes or duplicate declarations.
4. After all edits and cheap checks, queue the ready revision through docs/compilation.md and use a shared Debug x64 build of app/src/TinyTorrent.csproj covering that revision. Preserve this proposal's original limit of one build and a second only to correct a failure, subject to the shared interval and exclusive ownership. Do not broaden this into a solution build or separate engine/third-party rebuild. Inspect the build summary and output containment; report remaining failures or unavailable prerequisites rather than spending additional builds.
5. Return the complete move diff and verification results. The intended eventual history is one commit for the moves and their required reference/policy updates. Creating that commit is not part of this execution.

Cheap checks, before the build:

- No app source declaration remains in Syno.TinyTorrent.Views, Models, Services or Helpers, including their descendants; remove stale imports and qualified references too.
- Every XAML x:Class matches a C# partial declaration in the same namespace, including all relocated parts. Check declarations rather than assuming a .xaml.cs filename: SpeedLimitDialog is declared in SpeedLimit.cs, and Scheduler, Week and MainWindow also use existing non-.xaml.cs code-behind names.
- Every ordinary using Syno.TinyTorrent.* resolves to an existing namespace; every type alias resolves to a declaration. Parts folders create no namespaces. Check XAML xmlns references against the same inventory.
- The project excludes Shell\MainWindow\Capture*.cs when EnableCapture is false, including CaptureConnections.cs. All Capture partials remain MainWindow parts.
- The eight reflected type names in AddSourceChecks.ps1 and three in DistributionChecks.ps1 match their replacement tables; all other affected reflection strings follow their moved declarations.
- Assets/world.bin remains the resource read by Geography under the unchanged world.bin manifest name; the removed world.json has no relocation entry.
- The final path set matches the agreed relocation map with no duplicate destinations or old copies. Scripts and fixtures remain at their specified paths; prototype links and both ignore entries are correct.

No app launch or test execution is included. The normal build excludes Capture partials; their moved references receive source checks. The successful normal build validates the relocated production source; capture-only code received source checks. Runtime behavior was not exercised.

## Inventory and implementation verification

The refreshed inventory contains 274 existing app paths, including 208 C#/XAML files, 19 prototype documents and the observed ignored local log. The map assigns every existing path exactly one destination: 159 paths change and 115 remain. One additional file receives the existing RelayCommand declaration, giving 275 final paths and 209 C#/XAML files in this snapshot.

The inventory was checked on 16 October against the working checkout at ec8979d, including ignored app paths, and compared with the previous abb170ff snapshot. Other work is active in that checkout; these counts describe this proposal snapshot and do not require a clean tree or authorize changing the owner's work.

The Markdown plan and HTML review copy use the same section text, target tree and relocation data. Checks cover inventory completeness, unique targets, allowed supporting folders, unchanged test/script paths, flat prototypes, reflected names and the namespace/member audit. Execution applied all 159 path moves, extracted the existing RelayCommand unchanged, and updated imports, XAML namespaces, reflection strings, Editor type references, the Capture exclusion, prototype URLs, ignore rules and live documentation. The final inventory contains 275 paths, 180 C# files and 29 XAML files. All 282 distinct top-level types and 17 existing aliases are accounted for, with no old copies, obsolete namespace declarations or unresolved app namespace/type references.

The focused review passes checked paths and indirect consumers, preserved behavioral ownership, and re-read the corrected result. They caught an overbroad Settings replacement in two MainViewModel partials; those references retain the Settings property. C# syntax parsing included CAPTURE code. All 29 XAML documents are equivalent after normalizing the approved type/namespace changes. Reflected type literals follow their declarations. Existing source comments, string literals and file encodings were preserved; the owner’s unrelated changes remain in place.

The normal Debug x64 app build succeeded on 16 October, 04:01:26–04:04:29 UTC, with exit code 0 and no reported errors or warnings. Shared record: artifacts/compilation/builds/app-reorganisation-1.md; log and runtime output hashes are recorded beside it. All 298 frozen build inputs were unchanged during compilation. Output containment and git diff --check passed. The build reused the existing native engine and compiled only the app and its managed dependencies.

No test execution, app launch, stash, reset or commit was performed. The normal build excludes Capture partials; their syntax and references were checked, but capture compilation and runtime validation remain outside this plan. The separate peer-pane-close task retains its own capture validation.

A second pass after execution rechecked the complete map, the Markdown/HTML match and the hashes of all compiled inputs. Normalizing only the approved namespace/type edits found no other code changes. The review corrected a prototype's moved WinUI-instructions link and remaining short source paths in the subtitle documentation. Production build inputs are unchanged, so the successful build still covers the final source.

## Complete target tree

The local ignored myeasylog.log is retained in the map but omitted from this source tree.

```text
└── app/
    ├── prototypes/
    │   ├── about-prototype.html
    │   ├── connection-prototype.html
    │   ├── delete-prototype.html
    │   ├── fetch-metadata-prototype.html
    │   ├── general-prototype.html
    │   ├── library-prototype.html
    │   ├── page-switch-prototype.html
    │   ├── peers-prototype.html
    │   ├── pieces-prototype.html
    │   ├── prototype.html
    │   ├── settings-options-prototype.html
    │   ├── settings-options-prototype.md
    │   ├── settings-prototype.html
    │   ├── settings-windows-prototype.html
    │   ├── settings-windows-prototype.md
    │   ├── speed-prototype.html
    │   ├── status-bar-prototype.html
    │   ├── transfers-layout-prototype.html
    │   └── video-information-prototype.html
    ├── src/
    │   ├── Assets/
    │   │   ├── Settings/
    │   │   │   ├── advanced.png
    │   │   │   ├── advanced.svg
    │   │   │   ├── appearance.png
    │   │   │   ├── appearance.svg
    │   │   │   ├── general.png
    │   │   │   ├── general.svg
    │   │   │   ├── home.svg
    │   │   │   ├── library.svg
    │   │   │   ├── LICENSE
    │   │   │   ├── limits.svg
    │   │   │   ├── network.png
    │   │   │   ├── network.svg
    │   │   │   ├── plugins.png
    │   │   │   ├── README.md
    │   │   │   ├── schedule.png
    │   │   │   ├── schedule.svg
    │   │   │   ├── setup.png
    │   │   │   ├── speed.png
    │   │   │   ├── transfers.png
    │   │   │   └── transfers.svg
    │   │   ├── Attribution.md
    │   │   ├── ThemeDark.jpg
    │   │   ├── ThemeLight.jpg
    │   │   ├── tmdb.svg
    │   │   └── world.bin
    │   ├── Controls/
    │   │   ├── ActionButton.cs
    │   │   ├── CheckStatus.xaml
    │   │   ├── CheckStatus.xaml.cs
    │   │   ├── Field.cs
    │   │   ├── FieldColumns.cs
    │   │   ├── FieldHeader.xaml
    │   │   ├── FieldHeader.xaml.cs
    │   │   ├── FileBrowser.xaml
    │   │   ├── FileBrowser.xaml.cs
    │   │   ├── FileSelection.cs
    │   │   ├── MenuItems.cs
    │   │   ├── MessageBarPanel.cs
    │   │   ├── Motion.cs
    │   │   ├── Overlay.cs
    │   │   ├── Pane.cs
    │   │   ├── Progress.cs
    │   │   ├── Section.cs
    │   │   ├── SettingsExpander.cs
    │   │   ├── SettingsLink.cs
    │   │   ├── SettingsRow.cs
    │   │   ├── SettingsSection.cs
    │   │   ├── Splitter.xaml
    │   │   ├── Splitter.xaml.cs
    │   │   ├── Strip.cs
    │   │   ├── TextEditor.cs
    │   │   └── WrapPanel.cs
    │   ├── Library/
    │   │   ├── Browser/
    │   │   │   └── Information.cs
    │   │   ├── Imdb/
    │   │   │   ├── Dataset.cs
    │   │   │   └── Provider.cs
    │   │   ├── MdbList/
    │   │   │   └── Provider.cs
    │   │   ├── Omdb/
    │   │   │   └── Provider.cs
    │   │   ├── Store/
    │   │   │   ├── Identification.cs
    │   │   │   ├── Settings.cs
    │   │   │   └── Videos.cs
    │   │   ├── Tmdb/
    │   │   │   └── Provider.cs
    │   │   ├── Wikidata/
    │   │   │   └── Provider.cs
    │   │   ├── BatchDialog.xaml
    │   │   ├── BatchDialog.xaml.cs
    │   │   ├── Browser.cs
    │   │   ├── Details.cs
    │   │   ├── EmptyState.xaml
    │   │   ├── EmptyState.xaml.cs
    │   │   ├── Entry.cs
    │   │   ├── FileFacts.cs
    │   │   ├── IProvider.cs
    │   │   ├── LibraryTable.xaml
    │   │   ├── LibraryTable.xaml.cs
    │   │   ├── MetadataActions.cs
    │   │   ├── MetadataDialog.cs
    │   │   ├── OriginActions.cs
    │   │   ├── ProviderJson.cs
    │   │   ├── Store.cs
    │   │   └── VideoLookup.cs
    │   ├── Properties/
    │   │   └── launchSettings.json
    │   ├── Resources/
    │   │   ├── en.json
    │   │   ├── es.json
    │   │   ├── fr.json
    │   │   ├── nl.json
    │   │   ├── pt.json
    │   │   └── zh.json
    │   ├── Settings/
    │   │   ├── SettingsPage/
    │   │   │   ├── Destinations.cs
    │   │   │   ├── Header.cs
    │   │   │   ├── Home.cs
    │   │   │   └── Subtitles.cs
    │   │   ├── ConnectionPage.xaml
    │   │   ├── ConnectionPage.xaml.cs
    │   │   ├── ConnectionSetup.cs
    │   │   ├── Editor.cs
    │   │   ├── LimitSelector.cs
    │   │   ├── PeriodEditor.xaml
    │   │   ├── PeriodEditor.xaml.cs
    │   │   ├── PeriodSpan.cs
    │   │   ├── Proxy.cs
    │   │   ├── ProxyDialog.xaml
    │   │   ├── ProxyDialog.xaml.cs
    │   │   ├── Schedule.cs
    │   │   ├── Scheduler.cs
    │   │   ├── Scheduler.xaml
    │   │   ├── SettingsPage.xaml
    │   │   ├── SettingsPage.xaml.cs
    │   │   ├── SpeedLimits.xaml
    │   │   ├── SpeedLimits.xaml.cs
    │   │   ├── Speedometer.xaml
    │   │   ├── Speedometer.xaml.cs
    │   │   ├── ThemePicker.xaml
    │   │   ├── ThemePicker.xaml.cs
    │   │   ├── Week.cs
    │   │   ├── Week.xaml
    │   │   ├── WindowsIntegration.xaml
    │   │   └── WindowsIntegration.xaml.cs
    │   ├── Shared/
    │   │   ├── Desktop/
    │   │   │   ├── DiskSpace.cs
    │   │   │   ├── RecycleBin.cs
    │   │   │   └── ShellAssociation.cs
    │   │   ├── Diagnostics/
    │   │   │   ├── CrashDialog.cs
    │   │   │   └── ExceptionLog.cs
    │   │   ├── Engine/
    │   │   │   ├── PipeClient/
    │   │   │   │   └── Files.cs
    │   │   │   └── PipeClient.cs
    │   │   ├── Network/
    │   │   │   ├── ProviderHttp/
    │   │   │   │   └── Dns.cs
    │   │   │   ├── RequestPolicy/
    │   │   │   │   └── Access.cs
    │   │   │   ├── ProviderHttp.cs
    │   │   │   └── RequestPolicy.cs
    │   │   ├── Storage/
    │   │   │   ├── Database/
    │   │   │   │   ├── Files.cs
    │   │   │   │   └── Schema.cs
    │   │   │   └── Database.cs
    │   │   ├── AccountDialog.cs
    │   │   ├── BackgroundWork.cs
    │   │   ├── Credentials.cs
    │   │   ├── DataDownload.cs
    │   │   ├── DatasetRow.cs
    │   │   ├── Dialog.cs
    │   │   ├── DialogFooter.cs
    │   │   ├── DialogPreference.cs
    │   │   ├── Disclosure.cs
    │   │   ├── FileName.cs
    │   │   ├── FileSources.cs
    │   │   ├── Geography.cs
    │   │   ├── Geolocation.cs
    │   │   ├── IDataset.cs
    │   │   ├── IDraft.cs
    │   │   ├── RelayCommand.cs
    │   │   ├── Strings.cs
    │   │   ├── TorrentSource.cs
    │   │   ├── WorkObservation.cs
    │   │   ├── WorkStatus.xaml
    │   │   └── WorkStatus.xaml.cs
    │   ├── Shell/
    │   │   ├── InspectorPane/
    │   │   │   ├── Header.cs
    │   │   │   ├── Library.cs
    │   │   │   └── Peers.cs
    │   │   ├── MainViewModel/
    │   │   │   ├── About.cs
    │   │   │   ├── Actions.cs
    │   │   │   ├── Features.cs
    │   │   │   ├── Finding.cs
    │   │   │   ├── Limits.cs
    │   │   │   ├── Notifications.cs
    │   │   │   ├── Selection.cs
    │   │   │   └── Updates.cs
    │   │   ├── MainWindow/
    │   │   │   ├── Actions.cs
    │   │   │   ├── Associations.cs
    │   │   │   ├── Capture.cs
    │   │   │   ├── CaptureConnections.cs
    │   │   │   ├── CaptureEdits.cs
    │   │   │   ├── CaptureFiles.cs
    │   │   │   ├── CaptureLibrary.cs
    │   │   │   ├── CaptureLibraryFiles.cs
    │   │   │   ├── CapturePeers.cs
    │   │   │   ├── CapturePieces.cs
    │   │   │   ├── CaptureSearch.cs
    │   │   │   ├── CaptureSettings.cs
    │   │   │   ├── CaptureTraffic.cs
    │   │   │   ├── Chrome.cs
    │   │   │   ├── Connections.cs
    │   │   │   ├── Datasets.cs
    │   │   │   ├── Dialogs.cs
    │   │   │   ├── Library.cs
    │   │   │   ├── Notifications.cs
    │   │   │   ├── Opening.cs
    │   │   │   ├── Placement.cs
    │   │   │   ├── Subtitles.cs
    │   │   │   ├── Work.cs
    │   │   │   └── Workspace.cs
    │   │   ├── AboutPage.xaml
    │   │   ├── AboutPage.xaml.cs
    │   │   ├── CreditsPage.xaml
    │   │   ├── CreditsPage.xaml.cs
    │   │   ├── InspectorPane.xaml
    │   │   ├── InspectorPane.xaml.cs
    │   │   ├── MainViewModel.cs
    │   │   ├── MainWindow.cs
    │   │   ├── MainWindow.xaml
    │   │   └── Navigation.cs
    │   ├── Subtitles/
    │   │   ├── Acquisition/
    │   │   │   ├── Acquire.cs
    │   │   │   ├── Choose.cs
    │   │   │   ├── Discovery.cs
    │   │   │   ├── Files.cs
    │   │   │   ├── Jobs.cs
    │   │   │   ├── Storage.cs
    │   │   │   └── Work.cs
    │   │   ├── Suppliers/
    │   │   │   ├── OpenSubtitles/
    │   │   │   │   └── Languages.cs
    │   │   │   ├── SubDL/
    │   │   │   │   └── Languages.cs
    │   │   │   ├── SubSource/
    │   │   │   │   └── Languages.cs
    │   │   │   ├── OpenSubtitles.cs
    │   │   │   ├── SubDL.cs
    │   │   │   └── SubSource.cs
    │   │   ├── Acquisition.cs
    │   │   ├── Automatic.cs
    │   │   ├── Batch.cs
    │   │   ├── ChoiceDialog.cs
    │   │   ├── LanguageDetection.cs
    │   │   ├── Settings.cs
    │   │   ├── SubtitleFile.cs
    │   │   └── Supplier.cs
    │   ├── Torrents/
    │   │   ├── Inspector/
    │   │   │   └── Peers.cs
    │   │   ├── PeerMap/
    │   │   │   ├── Distribution.cs
    │   │   │   └── Navigation.cs
    │   │   ├── AddDialog.xaml
    │   │   ├── AddDialog.xaml.cs
    │   │   ├── AddDraft.cs
    │   │   ├── FileDialog.xaml
    │   │   ├── FileDialog.xaml.cs
    │   │   ├── FileDraft.cs
    │   │   ├── Inspector.cs
    │   │   ├── Peer.cs
    │   │   ├── PeerMap.xaml
    │   │   ├── PeerMap.xaml.cs
    │   │   ├── Pieces.cs
    │   │   ├── PiecesMap.xaml
    │   │   ├── PiecesMap.xaml.cs
    │   │   ├── SpeedGraph.xaml
    │   │   ├── SpeedGraph.xaml.cs
    │   │   ├── SpeedLimit.cs
    │   │   ├── SpeedLimitDialog.xaml
    │   │   ├── Torrent.cs
    │   │   └── Tracker.cs
    │   ├── App.cs
    │   ├── app.manifest
    │   ├── App.xaml
    │   ├── Enums.cs
    │   └── TinyTorrent.csproj
    ├── tests/
    │   ├── Store/
    │   │   ├── {ECE5E9CC-3FBB-452B-96ED-7F8E895285EE}.resume
    │   │   └── settings.json
    │   ├── AddSourceChecks.ps1
    │   ├── Capture.ps1
    │   ├── DistributionChecks.ps1
    │   ├── LibraryTiming.ps1
    │   ├── NavigationChecks.ps1
    │   ├── ProviderChecks.ps1
    │   └── SourceChecks.ps1
    ├── AGENTS.md
    ├── CopyEngine.ps1
    └── CountryData.ps1
```

## One declaration moved to an additional file

| Existing declaration | Destination | Change |
| --- | --- | --- |
| RelayCommand in app/src/MainViewModel.cs | app/src/Shared/RelayCommand.cs | Remove that declaration from the relocated MainViewModel.cs and place it here unchanged, in Syno.TinyTorrent.Shared. Update direct consumers; retain one implementation. |

All other existing files have one destination in the map below. Supporting declarations remain with their file unless the explicit Editor type rename applies.

## Complete relocation map

Paths are relative to the repository root. A retained path is listed explicitly; this table does not imply deleting or recreating an ignored or untracked file.

| Existing path | Destination | Required treatment |
| --- | --- | --- |
| `app/about-prototype.html` | `app/prototypes/about-prototype.html` | Move to flat prototypes folder; preserve filename and tracked/ignored status; repair references. |
| `app/AGENTS.md` | `app/AGENTS.md` | Retain path. |
| `app/connection-prototype.html` | `app/prototypes/connection-prototype.html` | Move to flat prototypes folder; preserve filename and tracked/ignored status; repair references. |
| `app/CopyEngine.ps1` | `app/CopyEngine.ps1` | Retain path and existing project Exec/UpToDateCheckInput paths. |
| `app/CountryData.ps1` | `app/CountryData.ps1` | Retain path and existing project Exec/UpToDateCheckInput paths. |
| `app/delete-prototype.html` | `app/prototypes/delete-prototype.html` | Move to flat prototypes folder; preserve filename and tracked/ignored status; repair references. |
| `app/fetch-metadata-prototype.html` | `app/prototypes/fetch-metadata-prototype.html` | Move to flat prototypes folder; preserve filename and tracked/ignored status; repair references. |
| `app/general-prototype.html` | `app/prototypes/general-prototype.html` | Move to flat prototypes folder; preserve filename and tracked/ignored status; repair references. |
| `app/library-prototype.html` | `app/prototypes/library-prototype.html` | Move to flat prototypes folder; preserve filename and tracked/ignored status; repair references. |
| `app/page-switch-prototype.html` | `app/prototypes/page-switch-prototype.html` | Move to flat prototypes folder; preserve filename and tracked/ignored status; repair references. |
| `app/peers-prototype.html` | `app/prototypes/peers-prototype.html` | Move to flat prototypes folder; preserve filename and tracked/ignored status; repair references. |
| `app/pieces-prototype.html` | `app/prototypes/pieces-prototype.html` | Move to flat prototypes folder; preserve filename and tracked/ignored status; repair references. |
| `app/prototype.html` | `app/prototypes/prototype.html` | Move to flat prototypes folder; preserve filename and tracked/ignored status; repair references. |
| `app/settings-options-prototype.html` | `app/prototypes/settings-options-prototype.html` | Move to flat prototypes folder; preserve filename and tracked/ignored status; repair references. |
| `app/settings-options-prototype.md` | `app/prototypes/settings-options-prototype.md` | Move to flat prototypes folder; preserve filename and tracked/ignored status; repair references. |
| `app/settings-prototype.html` | `app/prototypes/settings-prototype.html` | Move to flat prototypes folder; preserve filename and tracked/ignored status; repair references. |
| `app/settings-windows-prototype.html` | `app/prototypes/settings-windows-prototype.html` | Move to flat prototypes folder; preserve filename and tracked/ignored status; repair references. |
| `app/settings-windows-prototype.md` | `app/prototypes/settings-windows-prototype.md` | Move to flat prototypes folder; preserve filename and tracked/ignored status; repair references. |
| `app/speed-prototype.html` | `app/prototypes/speed-prototype.html` | Move to flat prototypes folder; preserve filename and tracked/ignored status; repair references. |
| `app/src/App.cs` | `app/src/App.cs` | Retain path. |
| `app/src/app.manifest` | `app/src/app.manifest` | Retain path. |
| `app/src/App.xaml` | `app/src/App.xaml` | Retain path. |
| `app/src/Assets/Attribution.md` | `app/src/Assets/Attribution.md` | Retain path. |
| `app/src/Assets/Settings/advanced.png` | `app/src/Assets/Settings/advanced.png` | Retain path. |
| `app/src/Assets/Settings/advanced.svg` | `app/src/Assets/Settings/advanced.svg` | Retain path. |
| `app/src/Assets/Settings/appearance.png` | `app/src/Assets/Settings/appearance.png` | Retain path. |
| `app/src/Assets/Settings/appearance.svg` | `app/src/Assets/Settings/appearance.svg` | Retain path. |
| `app/src/Assets/Settings/general.png` | `app/src/Assets/Settings/general.png` | Retain path. |
| `app/src/Assets/Settings/general.svg` | `app/src/Assets/Settings/general.svg` | Retain path. |
| `app/src/Assets/Settings/home.svg` | `app/src/Assets/Settings/home.svg` | Retain path. |
| `app/src/Assets/Settings/library.svg` | `app/src/Assets/Settings/library.svg` | Retain path. |
| `app/src/Assets/Settings/LICENSE` | `app/src/Assets/Settings/LICENSE` | Retain path. |
| `app/src/Assets/Settings/limits.svg` | `app/src/Assets/Settings/limits.svg` | Retain path. |
| `app/src/Assets/Settings/network.png` | `app/src/Assets/Settings/network.png` | Retain path. |
| `app/src/Assets/Settings/network.svg` | `app/src/Assets/Settings/network.svg` | Retain path. |
| `app/src/Assets/Settings/plugins.png` | `app/src/Assets/Settings/plugins.png` | Retain path. |
| `app/src/Assets/Settings/README.md` | `app/src/Assets/Settings/README.md` | Retain path. |
| `app/src/Assets/Settings/schedule.png` | `app/src/Assets/Settings/schedule.png` | Retain path. |
| `app/src/Assets/Settings/schedule.svg` | `app/src/Assets/Settings/schedule.svg` | Retain path. |
| `app/src/Assets/Settings/setup.png` | `app/src/Assets/Settings/setup.png` | Retain path. |
| `app/src/Assets/Settings/speed.png` | `app/src/Assets/Settings/speed.png` | Retain path. |
| `app/src/Assets/Settings/transfers.png` | `app/src/Assets/Settings/transfers.png` | Retain path. |
| `app/src/Assets/Settings/transfers.svg` | `app/src/Assets/Settings/transfers.svg` | Retain path. |
| `app/src/Assets/ThemeDark.jpg` | `app/src/Assets/ThemeDark.jpg` | Retain path. |
| `app/src/Assets/ThemeLight.jpg` | `app/src/Assets/ThemeLight.jpg` | Retain path. |
| `app/src/Assets/tmdb.svg` | `app/src/Assets/tmdb.svg` | Retain path. |
| `app/src/Assets/world.bin` | `app/src/Assets/world.bin` | Retain path and world.bin embedded resource name; replaces the removed world.json. |
| `app/src/Controls/ActionButton.cs` | `app/src/Controls/ActionButton.cs` | Retain path. |
| `app/src/Controls/CheckStatus.xaml` | `app/src/Controls/CheckStatus.xaml` | Retain path. |
| `app/src/Controls/CheckStatus.xaml.cs` | `app/src/Controls/CheckStatus.xaml.cs` | Retain path. |
| `app/src/Controls/CrashDialog.cs` | `app/src/Shared/Diagnostics/CrashDialog.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Controls/DatasetRow.cs` | `app/src/Shared/DatasetRow.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Controls/Dialog.cs` | `app/src/Shared/Dialog.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Controls/DialogFooter.cs` | `app/src/Shared/DialogFooter.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Controls/Disclosure.cs` | `app/src/Shared/Disclosure.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Controls/Field.cs` | `app/src/Controls/Field.cs` | Retain path. |
| `app/src/Controls/FieldColumns.cs` | `app/src/Controls/FieldColumns.cs` | Retain path. |
| `app/src/Controls/FieldHeader.xaml` | `app/src/Controls/FieldHeader.xaml` | Retain path. |
| `app/src/Controls/FieldHeader.xaml.cs` | `app/src/Controls/FieldHeader.xaml.cs` | Retain path. |
| `app/src/Controls/FileBrowser.xaml` | `app/src/Controls/FileBrowser.xaml` | Retain path. |
| `app/src/Controls/FileBrowser.xaml.cs` | `app/src/Controls/FileBrowser.xaml.cs` | Retain path. |
| `app/src/Controls/FileSelection.cs` | `app/src/Controls/FileSelection.cs` | Retain path. |
| `app/src/Controls/LimitSelector.cs` | `app/src/Settings/LimitSelector.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Controls/MenuItems.cs` | `app/src/Controls/MenuItems.cs` | Retain path. |
| `app/src/Controls/MessageBarPanel.cs` | `app/src/Controls/MessageBarPanel.cs` | Retain path. |
| `app/src/Controls/Overlay.cs` | `app/src/Controls/Overlay.cs` | Retain path. |
| `app/src/Controls/Pane.cs` | `app/src/Controls/Pane.cs` | Retain path. |
| `app/src/Controls/PeerMap.xaml` | `app/src/Torrents/PeerMap.xaml` | Move; update namespace and direct consumers where applicable. |
| `app/src/Controls/PeerMap.xaml.cs` | `app/src/Torrents/PeerMap.xaml.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Controls/PeerMap/Distribution.cs` | `app/src/Torrents/PeerMap/Distribution.cs` | Move with PeerMap; keep its partial namespace and update Models.Peer references and reflected consumers. |
| `app/src/Controls/PeerMap/Navigation.cs` | `app/src/Torrents/PeerMap/Navigation.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Controls/PeriodEditor.xaml` | `app/src/Settings/PeriodEditor.xaml` | Move; update namespace and direct consumers where applicable. |
| `app/src/Controls/PeriodEditor.xaml.cs` | `app/src/Settings/PeriodEditor.xaml.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Controls/PiecesMap.xaml` | `app/src/Torrents/PiecesMap.xaml` | Move; update namespace and direct consumers where applicable. |
| `app/src/Controls/PiecesMap.xaml.cs` | `app/src/Torrents/PiecesMap.xaml.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Controls/Progress.cs` | `app/src/Controls/Progress.cs` | Retain path. |
| `app/src/Controls/Scheduler.cs` | `app/src/Settings/Scheduler.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Controls/Scheduler.xaml` | `app/src/Settings/Scheduler.xaml` | Move; update namespace and direct consumers where applicable. |
| `app/src/Controls/Section.cs` | `app/src/Controls/Section.cs` | Retain path. |
| `app/src/Controls/SettingsExpander.cs` | `app/src/Controls/SettingsExpander.cs` | Retain path. |
| `app/src/Controls/SettingsLink.cs` | `app/src/Controls/SettingsLink.cs` | Retain path. |
| `app/src/Controls/SettingsRow.cs` | `app/src/Controls/SettingsRow.cs` | Retain path. |
| `app/src/Controls/SettingsSection.cs` | `app/src/Controls/SettingsSection.cs` | Retain path. |
| `app/src/Controls/SpeedGraph.xaml` | `app/src/Torrents/SpeedGraph.xaml` | Move; update namespace and direct consumers where applicable. |
| `app/src/Controls/SpeedGraph.xaml.cs` | `app/src/Torrents/SpeedGraph.xaml.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Controls/SpeedLimits.xaml` | `app/src/Settings/SpeedLimits.xaml` | Move; update namespace and direct consumers where applicable. |
| `app/src/Controls/SpeedLimits.xaml.cs` | `app/src/Settings/SpeedLimits.xaml.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Controls/Speedometer.xaml` | `app/src/Settings/Speedometer.xaml` | Move with ConnectionPage; update x:Class to Syno.TinyTorrent.Settings.Speedometer. |
| `app/src/Controls/Speedometer.xaml.cs` | `app/src/Settings/Speedometer.xaml.cs` | Move with ConnectionPage; update namespace and direct consumers where applicable. |
| `app/src/Controls/Splitter.xaml` | `app/src/Controls/Splitter.xaml` | Retain path. |
| `app/src/Controls/Splitter.xaml.cs` | `app/src/Controls/Splitter.xaml.cs` | Retain path. |
| `app/src/Controls/Strip.cs` | `app/src/Controls/Strip.cs` | Retain path. |
| `app/src/Controls/ThemePicker.xaml` | `app/src/Settings/ThemePicker.xaml` | Move; update namespace and direct consumers where applicable. |
| `app/src/Controls/ThemePicker.xaml.cs` | `app/src/Settings/ThemePicker.xaml.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Controls/Week.cs` | `app/src/Settings/Week.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Controls/Week.xaml` | `app/src/Settings/Week.xaml` | Move; update namespace and direct consumers where applicable. |
| `app/src/Controls/WindowsIntegration.xaml` | `app/src/Settings/WindowsIntegration.xaml` | Move; update namespace and direct consumers where applicable. |
| `app/src/Controls/WindowsIntegration.xaml.cs` | `app/src/Settings/WindowsIntegration.xaml.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Controls/WorkStatus.xaml` | `app/src/Shared/WorkStatus.xaml` | Move; update namespace and direct consumers where applicable. |
| `app/src/Controls/WorkStatus.xaml.cs` | `app/src/Shared/WorkStatus.xaml.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Controls/WrapPanel.cs` | `app/src/Controls/WrapPanel.cs` | Retain path. |
| `app/src/Helpers/Motion.cs` | `app/src/Controls/Motion.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Helpers/RecycleBin.cs` | `app/src/Shared/Desktop/RecycleBin.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Helpers/ShellAssociation.cs` | `app/src/Shared/Desktop/ShellAssociation.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Helpers/TextEditor.cs` | `app/src/Controls/TextEditor.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Library/BatchDialog.xaml` | `app/src/Library/BatchDialog.xaml` | Retain path. |
| `app/src/Library/BatchDialog.xaml.cs` | `app/src/Library/BatchDialog.xaml.cs` | Retain path. |
| `app/src/Library/Browser.cs` | `app/src/Library/Browser.cs` | Retain path. |
| `app/src/Library/Browser/Information.cs` | `app/src/Library/Browser/Information.cs` | Retain path. |
| `app/src/Library/Details.cs` | `app/src/Library/Details.cs` | Retain path. |
| `app/src/Library/EmptyState.xaml` | `app/src/Library/EmptyState.xaml` | Retain path. |
| `app/src/Library/EmptyState.xaml.cs` | `app/src/Library/EmptyState.xaml.cs` | Retain path. |
| `app/src/Library/Entry.cs` | `app/src/Library/Entry.cs` | Retain path. |
| `app/src/Library/FileFacts.cs` | `app/src/Library/FileFacts.cs` | Retain path. |
| `app/src/Library/Imdb/Dataset.cs` | `app/src/Library/Imdb/Dataset.cs` | Retain path. |
| `app/src/Library/Imdb/Provider.cs` | `app/src/Library/Imdb/Provider.cs` | Retain path. |
| `app/src/Library/IProvider.cs` | `app/src/Library/IProvider.cs` | Retain path. |
| `app/src/Library/LibraryTable.xaml` | `app/src/Library/LibraryTable.xaml` | Retain path. |
| `app/src/Library/LibraryTable.xaml.cs` | `app/src/Library/LibraryTable.xaml.cs` | Retain path. |
| `app/src/Library/MdbList/Provider.cs` | `app/src/Library/MdbList/Provider.cs` | Retain path. |
| `app/src/Library/MetadataActions.cs` | `app/src/Library/MetadataActions.cs` | Retain path. |
| `app/src/Library/MetadataDialog.cs` | `app/src/Library/MetadataDialog.cs` | Retain path. |
| `app/src/Library/Omdb/Provider.cs` | `app/src/Library/Omdb/Provider.cs` | Retain path. |
| `app/src/Library/OriginActions.cs` | `app/src/Library/OriginActions.cs` | Retain path. |
| `app/src/Library/ProviderJson.cs` | `app/src/Library/ProviderJson.cs` | Retain path. |
| `app/src/Library/Store.cs` | `app/src/Library/Store.cs` | Retain path. |
| `app/src/Library/Store/Identification.cs` | `app/src/Library/Store/Identification.cs` | Retain path. |
| `app/src/Library/Store/Settings.cs` | `app/src/Library/Store/Settings.cs` | Retain path. |
| `app/src/Library/Store/Videos.cs` | `app/src/Library/Store/Videos.cs` | Retain path. |
| `app/src/Library/Tmdb/Provider.cs` | `app/src/Library/Tmdb/Provider.cs` | Retain path. |
| `app/src/Library/VideoLookup.cs` | `app/src/Library/VideoLookup.cs` | Retain path. |
| `app/src/Library/Wikidata/Provider.cs` | `app/src/Library/Wikidata/Provider.cs` | Retain path. |
| `app/src/MainViewModel.cs` | `app/src/Shell/MainViewModel.cs` | Move MainViewModel and its supporting declarations; move only RelayCommand to the additional file below. |
| `app/src/MainViewModel/About.cs` | `app/src/Shell/MainViewModel/About.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/MainViewModel/Actions.cs` | `app/src/Shell/MainViewModel/Actions.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/MainViewModel/Features.cs` | `app/src/Shell/MainViewModel/Features.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/MainViewModel/Finding.cs` | `app/src/Shell/MainViewModel/Finding.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/MainViewModel/Limits.cs` | `app/src/Shell/MainViewModel/Limits.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/MainViewModel/Notifications.cs` | `app/src/Shell/MainViewModel/Notifications.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/MainViewModel/Selection.cs` | `app/src/Shell/MainViewModel/Selection.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/MainViewModel/Updates.cs` | `app/src/Shell/MainViewModel/Updates.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/MainWindow.cs` | `app/src/Shell/MainWindow.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/MainWindow.xaml` | `app/src/Shell/MainWindow.xaml` | Move; update namespace and direct consumers where applicable. |
| `app/src/MainWindow/Actions.cs` | `app/src/Shell/MainWindow/Actions.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/MainWindow/Associations.cs` | `app/src/Shell/MainWindow/Associations.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/MainWindow/Capture.cs` | `app/src/Shell/MainWindow/Capture.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/MainWindow/CaptureConnections.cs` | `app/src/Shell/MainWindow/CaptureConnections.cs` | Move with MainWindow; remain covered by Shell\MainWindow\Capture*.cs exclusion. |
| `app/src/MainWindow/CaptureEdits.cs` | `app/src/Shell/MainWindow/CaptureEdits.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/MainWindow/CaptureFiles.cs` | `app/src/Shell/MainWindow/CaptureFiles.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/MainWindow/CaptureLibrary.cs` | `app/src/Shell/MainWindow/CaptureLibrary.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/MainWindow/CaptureLibraryFiles.cs` | `app/src/Shell/MainWindow/CaptureLibraryFiles.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/MainWindow/CapturePeers.cs` | `app/src/Shell/MainWindow/CapturePeers.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/MainWindow/CapturePieces.cs` | `app/src/Shell/MainWindow/CapturePieces.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/MainWindow/CaptureSearch.cs` | `app/src/Shell/MainWindow/CaptureSearch.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/MainWindow/CaptureSettings.cs` | `app/src/Shell/MainWindow/CaptureSettings.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/MainWindow/CaptureTraffic.cs` | `app/src/Shell/MainWindow/CaptureTraffic.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/MainWindow/Chrome.cs` | `app/src/Shell/MainWindow/Chrome.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/MainWindow/Connections.cs` | `app/src/Shell/MainWindow/Connections.cs` | Move with MainWindow; preserve map scope, placement and return coordination. |
| `app/src/MainWindow/Datasets.cs` | `app/src/Shell/MainWindow/Datasets.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/MainWindow/Dialogs.cs` | `app/src/Shell/MainWindow/Dialogs.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/MainWindow/Library.cs` | `app/src/Shell/MainWindow/Library.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/MainWindow/Notifications.cs` | `app/src/Shell/MainWindow/Notifications.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/MainWindow/Opening.cs` | `app/src/Shell/MainWindow/Opening.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/MainWindow/Placement.cs` | `app/src/Shell/MainWindow/Placement.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/MainWindow/Subtitles.cs` | `app/src/Shell/MainWindow/Subtitles.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/MainWindow/Work.cs` | `app/src/Shell/MainWindow/Work.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/MainWindow/Workspace.cs` | `app/src/Shell/MainWindow/Workspace.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Models/Enums.cs` | `app/src/Enums.cs` | Root namespace Syno.TinyTorrent; preserve enum members and values. |
| `app/src/Models/FileName.cs` | `app/src/Shared/FileName.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Models/Geography.cs` | `app/src/Shared/Geography.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Models/IDataset.cs` | `app/src/Shared/IDataset.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Models/Peer.cs` | `app/src/Torrents/Peer.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Models/PeriodSpan.cs` | `app/src/Settings/PeriodSpan.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Models/Pieces.cs` | `app/src/Torrents/Pieces.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Models/Torrent.cs` | `app/src/Torrents/Torrent.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Models/Tracker.cs` | `app/src/Torrents/Tracker.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Models/WorkObservation.cs` | `app/src/Shared/WorkObservation.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/myeasylog.log` | `app/src/myeasylog.log` | Observed ignored local log; leave untouched; not application source. |
| `app/src/Navigation.cs` | `app/src/Shell/Navigation.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Properties/launchSettings.json` | `app/src/Properties/launchSettings.json` | Retain path. |
| `app/src/Resources/en.json` | `app/src/Resources/en.json` | Retain path. |
| `app/src/Resources/es.json` | `app/src/Resources/es.json` | Retain path. |
| `app/src/Resources/fr.json` | `app/src/Resources/fr.json` | Retain path. |
| `app/src/Resources/nl.json` | `app/src/Resources/nl.json` | Retain path. |
| `app/src/Resources/pt.json` | `app/src/Resources/pt.json` | Retain path. |
| `app/src/Resources/zh.json` | `app/src/Resources/zh.json` | Retain path. |
| `app/src/Services/BackgroundWork.cs` | `app/src/Shared/BackgroundWork.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Services/Credentials.cs` | `app/src/Shared/Credentials.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Services/Database.cs` | `app/src/Shared/Storage/Database.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Services/Database/Files.cs` | `app/src/Shared/Storage/Database/Files.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Services/Database/Schema.cs` | `app/src/Shared/Storage/Database/Schema.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Services/DataDownload.cs` | `app/src/Shared/DataDownload.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Services/DiskSpace.cs` | `app/src/Shared/Desktop/DiskSpace.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Services/ExceptionLog.cs` | `app/src/Shared/Diagnostics/ExceptionLog.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Services/FileSources.cs` | `app/src/Shared/FileSources.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Services/Geolocation.cs` | `app/src/Shared/Geolocation.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Services/PipeClient.cs` | `app/src/Shared/Engine/PipeClient.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Services/PipeClient/Files.cs` | `app/src/Shared/Engine/PipeClient/Files.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Services/ProviderHttp.cs` | `app/src/Shared/Network/ProviderHttp.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Services/ProviderHttp/Dns.cs` | `app/src/Shared/Network/ProviderHttp/Dns.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Services/RequestPolicy.cs` | `app/src/Shared/Network/RequestPolicy.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Services/RequestPolicy/Access.cs` | `app/src/Shared/Network/RequestPolicy/Access.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Services/Strings.cs` | `app/src/Shared/Strings.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Services/TorrentSource.cs` | `app/src/Shared/TorrentSource.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Subtitles/Acquisition.cs` | `app/src/Subtitles/Acquisition.cs` | Retain path. |
| `app/src/Subtitles/Acquisition/Acquire.cs` | `app/src/Subtitles/Acquisition/Acquire.cs` | Retain path. |
| `app/src/Subtitles/Acquisition/Choose.cs` | `app/src/Subtitles/Acquisition/Choose.cs` | Retain path. |
| `app/src/Subtitles/Acquisition/Discovery.cs` | `app/src/Subtitles/Acquisition/Discovery.cs` | Retain path. |
| `app/src/Subtitles/Acquisition/Files.cs` | `app/src/Subtitles/Acquisition/Files.cs` | Retain path. |
| `app/src/Subtitles/Acquisition/Jobs.cs` | `app/src/Subtitles/Acquisition/Jobs.cs` | Retain path. |
| `app/src/Subtitles/Acquisition/Storage.cs` | `app/src/Subtitles/Acquisition/Storage.cs` | Retain path. |
| `app/src/Subtitles/Acquisition/Work.cs` | `app/src/Subtitles/Acquisition/Work.cs` | Retain path. |
| `app/src/Subtitles/Automatic.cs` | `app/src/Subtitles/Automatic.cs` | Retain path. |
| `app/src/Subtitles/Batch.cs` | `app/src/Subtitles/Batch.cs` | Retain path. |
| `app/src/Subtitles/ChoiceDialog.cs` | `app/src/Subtitles/ChoiceDialog.cs` | Retain path. |
| `app/src/Subtitles/LanguageDetection.cs` | `app/src/Subtitles/LanguageDetection.cs` | Retain path. |
| `app/src/Subtitles/Settings.cs` | `app/src/Subtitles/Settings.cs` | Retain path. |
| `app/src/Subtitles/SubtitleFile.cs` | `app/src/Subtitles/SubtitleFile.cs` | Retain path. |
| `app/src/Subtitles/Supplier.cs` | `app/src/Subtitles/Supplier.cs` | Retain path. |
| `app/src/Subtitles/Supplier/OpenSubtitles.cs` | `app/src/Subtitles/Suppliers/OpenSubtitles.cs` | Keep concrete supplier class name; namespace Syno.TinyTorrent.Subtitles.Suppliers. |
| `app/src/Subtitles/Supplier/OpenSubtitles/Languages.cs` | `app/src/Subtitles/Suppliers/OpenSubtitles/Languages.cs` | Keep partial owner and filename; namespace Syno.TinyTorrent.Subtitles.Suppliers. |
| `app/src/Subtitles/Supplier/SubDL.cs` | `app/src/Subtitles/Suppliers/SubDL.cs` | Keep concrete supplier class name; namespace Syno.TinyTorrent.Subtitles.Suppliers. |
| `app/src/Subtitles/Supplier/SubDL/Languages.cs` | `app/src/Subtitles/Suppliers/SubDL/Languages.cs` | Keep partial owner and filename; namespace Syno.TinyTorrent.Subtitles.Suppliers. |
| `app/src/Subtitles/Supplier/SubSource.cs` | `app/src/Subtitles/Suppliers/SubSource.cs` | Keep concrete supplier class name; namespace Syno.TinyTorrent.Subtitles.Suppliers. |
| `app/src/Subtitles/Supplier/SubSource/Languages.cs` | `app/src/Subtitles/Suppliers/SubSource/Languages.cs` | Keep partial owner and filename; namespace Syno.TinyTorrent.Subtitles.Suppliers. |
| `app/src/TinyTorrent.csproj` | `app/src/TinyTorrent.csproj` | Retain path. |
| `app/src/Views/AboutPage.xaml` | `app/src/Shell/AboutPage.xaml` | Move; update namespace and direct consumers where applicable. |
| `app/src/Views/AboutPage.xaml.cs` | `app/src/Shell/AboutPage.xaml.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Views/AccountDialog.cs` | `app/src/Shared/AccountDialog.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Views/AddDialog.xaml` | `app/src/Torrents/AddDialog.xaml` | Move; update namespace and direct consumers where applicable. |
| `app/src/Views/AddDialog.xaml.cs` | `app/src/Torrents/AddDialog.xaml.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Views/AddDraft.cs` | `app/src/Torrents/AddDraft.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Views/ConnectionPage.xaml` | `app/src/Settings/ConnectionPage.xaml` | Move; update namespace and direct consumers where applicable. |
| `app/src/Views/ConnectionPage.xaml.cs` | `app/src/Settings/ConnectionPage.xaml.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Views/ConnectionSetup.cs` | `app/src/Settings/ConnectionSetup.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Views/CreditsPage.xaml` | `app/src/Shell/CreditsPage.xaml` | Move; update namespace and direct consumers where applicable. |
| `app/src/Views/CreditsPage.xaml.cs` | `app/src/Shell/CreditsPage.xaml.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Views/DialogPreference.cs` | `app/src/Shared/DialogPreference.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Views/FileDialog.xaml` | `app/src/Torrents/FileDialog.xaml` | Move; update namespace and direct consumers where applicable. |
| `app/src/Views/FileDialog.xaml.cs` | `app/src/Torrents/FileDialog.xaml.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Views/FileDraft.cs` | `app/src/Torrents/FileDraft.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Views/IDraft.cs` | `app/src/Shared/IDraft.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Views/Inspector.cs` | `app/src/Torrents/Inspector.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Views/Inspector/Peers.cs` | `app/src/Torrents/Inspector/Peers.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Views/InspectorPane.xaml` | `app/src/Shell/InspectorPane.xaml` | Move intact with the same partial owner; namespace and references only. |
| `app/src/Views/InspectorPane.xaml.cs` | `app/src/Shell/InspectorPane.xaml.cs` | Move intact with the same partial owner; namespace and references only. |
| `app/src/Views/InspectorPane/Header.cs` | `app/src/Shell/InspectorPane/Header.cs` | Move intact with the same partial owner; namespace and references only. |
| `app/src/Views/InspectorPane/Library.cs` | `app/src/Shell/InspectorPane/Library.cs` | Move intact with the same partial owner; namespace and references only. |
| `app/src/Views/InspectorPane/Peers.cs` | `app/src/Shell/InspectorPane/Peers.cs` | Move intact with the same partial owner; namespace and references only. |
| `app/src/Views/Proxy.cs` | `app/src/Settings/Proxy.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Views/ProxyDialog.xaml` | `app/src/Settings/ProxyDialog.xaml` | Move; update namespace and direct consumers where applicable. |
| `app/src/Views/ProxyDialog.xaml.cs` | `app/src/Settings/ProxyDialog.xaml.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Views/Schedule.cs` | `app/src/Settings/Schedule.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Views/Settings.cs` | `app/src/Settings/Editor.cs` | Rename Settings to Editor; retain Setting and related declarations in this file. |
| `app/src/Views/SettingsPage.xaml` | `app/src/Settings/SettingsPage.xaml` | Move; update namespace and direct consumers where applicable. |
| `app/src/Views/SettingsPage.xaml.cs` | `app/src/Settings/SettingsPage.xaml.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Views/SettingsPage/Destinations.cs` | `app/src/Settings/SettingsPage/Destinations.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Views/SettingsPage/Header.cs` | `app/src/Settings/SettingsPage/Header.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Views/SettingsPage/Home.cs` | `app/src/Settings/SettingsPage/Home.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Views/SettingsPage/Subtitles.cs` | `app/src/Settings/SettingsPage/Subtitles.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Views/SpeedLimit.cs` | `app/src/Torrents/SpeedLimit.cs` | Move; update namespace and direct consumers where applicable. |
| `app/src/Views/SpeedLimitDialog.xaml` | `app/src/Torrents/SpeedLimitDialog.xaml` | Move; update namespace and direct consumers where applicable. |
| `app/status-bar-prototype.html` | `app/prototypes/status-bar-prototype.html` | Move to flat prototypes folder; preserve filename and tracked/ignored status; repair references. |
| `app/tests/AddSourceChecks.ps1` | `app/tests/AddSourceChecks.ps1` | Retain path, fixtures and script-relative paths; update affected reflection type names only. |
| `app/tests/Capture.ps1` | `app/tests/Capture.ps1` | Retain path, fixtures and script-relative paths; update affected reflection type names only. |
| `app/tests/DistributionChecks.ps1` | `app/tests/DistributionChecks.ps1` | Retain path and behavior; update reflected PeerMap, Geography+Area and GeoPoint names. |
| `app/tests/LibraryTiming.ps1` | `app/tests/LibraryTiming.ps1` | Retain path, fixtures and script-relative paths; update affected reflection type names only. |
| `app/tests/NavigationChecks.ps1` | `app/tests/NavigationChecks.ps1` | Retain path, fixtures and script-relative paths; update affected reflection type names only. |
| `app/tests/ProviderChecks.ps1` | `app/tests/ProviderChecks.ps1` | Retain path, fixtures and script-relative paths; update affected reflection type names only. |
| `app/tests/SourceChecks.ps1` | `app/tests/SourceChecks.ps1` | Retain path, fixtures and script-relative paths; update affected reflection type names only. |
| `app/tests/Store/{ECE5E9CC-3FBB-452B-96ED-7F8E895285EE}.resume` | `app/tests/Store/{ECE5E9CC-3FBB-452B-96ED-7F8E895285EE}.resume` | Retain path, fixtures and script-relative paths; update affected reflection type names only. |
| `app/tests/Store/settings.json` | `app/tests/Store/settings.json` | Retain path, fixtures and script-relative paths; update affected reflection type names only. |
| `app/transfers-layout-prototype.html` | `app/prototypes/transfers-layout-prototype.html` | Move to flat prototypes folder; preserve filename and tracked/ignored status; repair references. |
| `app/video-information-prototype.html` | `app/prototypes/video-information-prototype.html` | Move to flat prototypes folder; preserve filename and tracked/ignored status; repair references. |
