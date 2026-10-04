# Localisation

This is part of the [architecture](architecture.md), not an
implemented feature. English alone must provide a complete working application.
Adding another language changes catalogue data, not application behavior.

Use the same text path from the first product screen. The catalogues and build
generation described here do not exist yet; existing Synapse resources are the
starting material, not a second permanent English authority. There is no need
for a web i18n framework or translation service in this native application.

## One source for application text

Use `resources/locales/en.json` as the canonical source of message keys and
English fallback text. Other shipped languages use the same keys in
`resources/locales/<language-tag>.json`. These are shared product resources,
available to the engine build without WinUI.
JSON has a concrete role here as editable translation data; IPC remains binary.

Cover every application-authored surface: windows, dialogs, menus, tray text,
tooltips, status and error messages, empty states, notifications, keyboard hints,
and accessibility names/announcements, including those inside Synapse controls.
Use stable semantic keys, not English sentences as identifiers. Keep whole
messages together with named arguments; translators control word order. Text is
plain data, never executable markup. Preserve user text, torrent names, paths,
URLs, hashes, and peer/tracker messages as data rather than translation keys.
Windows-owned dialogs and shell surfaces retain platform-controlled language
behavior; TinyTorrent cannot promise to relabel an already-open system dialog.

Move hard-coded WinUI/Synapse text into this authority as desktop consumers are
implemented. Keep one English source rather than hand-maintained duplicates
in XAML, C++, C#, and `.resw`. Start by generating Windows resources from the JSON:
`.resw` for all WinUI text, including Synapse and packaging, and a native subset
for the engine. WinUI uses [MRT Core](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/mrtcore/mrtcore-overview)
for lookup; it does not also load a parallel JSON catalogue at runtime. Exact
generated layouts and build tooling belong to the first implementation.
Shipping another language is separate from this architecture requirement; expose
only installed, validated catalogues in the language selector.

## Synapse

Shared authoring does not make the reusable control depend on the torrent
application. Synapse owns the generic keys for its menus, placeholders, and
accessibility text. Keep those keys in a distinct group of the canonical
catalogue; the library build generates and ships only that resource subset.
Generated `.resw` or native resources are outputs, never edited translations.
Standalone samples and other hosts receive a complete English fallback without
loading product messages, starting the engine, or reading its preferences.

The product's process-local localisation owner supplies the selected language,
resource context, and refresh notification. Synapse uses its resource subset
through that same lookup policy and a domain-neutral refresh mechanism; it must
not reference engine types or own a second saved language preference. Keep its
resource access internal to the control and use the same refresh path for
standalone and product hosts. Choose the smallest concrete interface when
implementing this path; no new localisation package or provider framework is
prescribed.

The [TableView contract](../winui3/docs/tableview-contract.md) owns what a switch
preserves: structural schema and interaction state stay intact while presentation
text changes. Its [implementation map](../winui3/docs/tableview-implementation.md)
records the current gap, so resource generation alone cannot be reported as live
language support.

## Ownership and live behavior

The engine owns the selected BCP-47 language tag in its preferences,
because tray text must work with WinUI closed. First use matches the Windows
language against shipped catalogues, falling back to English. Preserve an explicit
selection across UI and engine restarts. Show languages by their own names so a
user can recover from an unfamiliar selection.

One localisation component in the UI process supplies text to both application
views and owned controls. Selecting a language updates existing visible content
on the next dispatcher/render update after the local catalogue is ready, without
waiting for a torrent refresh. A language revision
invalidates translated bindings and formatted display values, including open
application dialogs, flyouts, column headers, and accessibility metadata. Changing
a culture property or resource qualifier alone is not the refresh mechanism.
Hidden or virtualized content uses the current language when it appears.

Switch in place: no process restart, page recreation, reconnect, torrent reload,
lost focus/selection, or discarded draft. Keep numeric edits under the culture in
which editing began until they are committed or cancelled; reformat settled
values without reinterpreting the user's input. Download work is unaffected.
Native text composition and IME candidate handling get first refusal on input:
application shortcuts must not consume composition keys or commit unfinished
text. Preserve the composing editor's text, caret, selection, and focus during
language changes. Defer only editor updates that would interrupt composition
until it ends; surrounding translated content still updates immediately. Use
standard text controls and their composition behavior, not another input system.
Keep blocking resource work off the UI thread. Publish a prepared language change
together and retain the current language if preparation fails. Coalesce rapid
choices so older work or an acknowledgement cannot overwrite the latest selection.

Use the settings command and the engine's control notifications to synchronize
the live language and update tray surfaces; do not add a transport or event bus.
UI feedback can preview the pending choice immediately, then reconcile with the
engine. Saving runs asynchronously under the [engine persistence contract](engine.md#persistence-and-file-safety):
distinguish the live language from a successfully saved preference, report save
failure, and never silently claim persistence. Reconnecting reads the engine's
language again. Tray menus use the current catalogue when shown; an already-open
tray menu must be refreshed or safely reopened without executing a selection.

## Language rules and fallback

Resolve missing messages from a shipped parent language, then English, when
generating each shipped catalogue. Share that rule between native and WinUI
outputs; fallback is a complete message, including all its plural forms. Preserve
the language of the resolved message so inherited English uses English plural
rules. This avoids independent runtime fallback implementations. Windows
[language matching](https://learn.microsoft.com/en-us/windows/uwp/app-resources/how-rms-matches-lang-tags)
can select compatible regional variants; an explicit resource context alone does
not implement this catalogue inheritance rule.

Reject invalid catalogue data before packaging: missing English keys, duplicate
keys, incompatible placeholders, incomplete plural sets, and identifiers that
collide in generated Windows resources. MRT identifiers are case-insensitive,
and a base identifier can conflict with a property identifier; check the
[emitted names](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/mrtcore/localize-strings#store-strings-in-a-resources-file)
as well as the source keys. A failed language load
cannot prevent startup; embedded English remains available. Keep unknown engine
error codes usable through a translated generic message with optional diagnostic
detail.

Send stable error/status codes and typed arguments from the engine, not English
sentences that WinUI must parse. Localize at the surface displaying the message;
retain codes/arguments for operation status and outcomes so they can be rendered
again after a switch. Raw operating-system or library diagnostics may accompany
that message without being treated as translated product copy.

Use locale-correct plural forms for counted messages, not an English singular/
plural rule for every language. Select forms using the message's resolved language,
including when it falls back to English. Keep regional number/date/unit formatting
consistent with Windows regional preferences; UI language and region are separate
choices, without adding a second settings panel. Selecting a UI language must not
assign that language to the regional formatting/parsing culture. Use platform
globalization facilities; Windows supplies [ICU C APIs](https://learn.microsoft.com/en-us/windows/win32/intl/international-components-for-unicode--icu-)
when plural selection needs them. Do not bundle another ICU distribution or build
a general message-expression interpreter. The catalogue contract needs only the
message forms and substitutions actually used by the product. The retained
projects target Windows 10 1809: verify the chosen plural APIs and import libraries
against that floor. The consolidated `icu.dll` requires 1903; the existing floor
uses the older system ICU libraries. Do not raise the minimum merely to avoid
checking those imports.

Allow longer translations, Unicode, appropriate font fallback, and right-to-left
layout. Direction changes with the UI language; paths and identifiers retain
readable direction. Focus, keyboard hints, and accessibility remain coherent.
Microsoft documents [layout and RTL requirements](https://learn.microsoft.com/en-us/windows/apps/design/globalizing/adjust-layout-and-fonts--and-support-rtl)
and [WinUI resource behavior](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/mrtcore/localize-strings).

Use an explicit resource context and owned refresh for product text. Microsoft's
[language override documentation](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.windows.globalization.applicationlanguages.primarylanguageoverride)
notes that already-loaded UI resources may not update immediately and that the
override persists for packaged apps. If platform controls require an override,
derive and reapply it from the engine's language, and verify its interaction with
explicit contexts in the chosen SDK. Never read it as a second preference
authority or rely on it to refresh existing bindings.

## Cost and proportionate evidence

Generated resources are the initial implementation choice: they reuse WinUI's
existing text path and give the engine only text for its native surfaces. The
engine must not load .NET, WinUI, or the full UI catalogue for localisation.
Release obsolete language state after a switch and list languages without
loading every translation. Load platform formatting support only where needed.

Confirm this small build path with the first localised screen and tray message.
Revisit the representation if that implementation finds a simpler arrangement;
preserve one editable authority, one lookup policy per process, and the same
fallback results. Generated outputs never become hand-edited translations. No
translation server, network fetch, watcher, or plugin framework is needed.

Follow [the testing policy](testing.md). A quick catalogue integrity check covers
keys, placeholders, and required forms when catalogues change; it does not assert
the wording of screen strings. One focused live-switch exercise during
implementation should cover an open draft, an owned control, tray state, fallback,
and switching back while downloads continue. Use temporary long-text/RTL data to
review layout when that path changes, not a maintained screenshot suite. Check
that the visible switch has no perceptible pause on this machine; investigate an
observed delay instead of imposing a benchmark run on every translation edit.
