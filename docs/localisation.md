# Localisation

This is part of the [architecture](architecture.md), not an
implemented feature. English alone must provide a complete working application.
Adding another language changes catalogue data, not application behavior.

Use the same text path from the first product screen. TableView already reads
its English text this way; the product projects follow the same pattern. There
is no need for a web i18n framework or translation service in this native
application.

## One catalogue per project

Each project that shows text owns an `en.json` in the `Resources/` folder beside
its project file: the canonical message keys and English text for that project's
surfaces. Another shipped language is a `<language-tag>.json` with the same keys
in the same folder. These
files are the project's catalogues. The project embeds them in its own binary,
so a library or the engine works without another project's text.
JSON has a concrete role here as editable translation data; IPC remains binary.

Group keys by surface in one level of objects, with snake_case names that do not
repeat their group: `column_menu.hide`, not `column_menu.hide_column`.
Placeholders are numbered, `{0}`, and a translation may reorder them.

Cover every application-authored surface: windows, dialogs, menus, tray text,
tooltips, status and error messages, empty states, notifications, keyboard hints,
and accessibility names/announcements, including those inside TableView controls.
Use stable semantic keys, not English sentences as identifiers. Keep whole
messages together; translators control word order through the placeholders. Text is
plain data, never executable markup. Preserve user text, torrent names, paths,
URLs, hashes, and peer/tracker messages as data rather than translation keys.
Windows-owned dialogs and shell surfaces retain platform-controlled language
behavior; TinyTorrent cannot promise to relabel an already-open system dialog.

Move hard-coded text into its project's `en.json` as surfaces are implemented.
Keep one English source for each message rather than copies in XAML, C++, C#,
and `.resw`. WinUI projects read their embedded catalogues directly; they do not
use `.resw` or MRT Core for application text. The engine reads its own embedded
`en.json` for its native surfaces. Exact loader code belongs to the first
implementation in each project. Shipping another language is separate from this
architecture requirement; expose only installed, validated catalogues in the
language selector.

## TableView

TableView owns the generic keys for its menus, placeholders, and accessibility
text in its own `en.json`, so the reusable control does not depend on the
torrent application. Standalone samples and other hosts receive complete English
without loading product messages, starting the engine, or reading its
preferences.

The product's process-local localisation owner supplies the selected language
and refresh notification. TableView selects its own catalogue for that language
and uses a domain-neutral refresh mechanism; it must not reference engine types
or own a second saved language preference. Keep its text access internal to the
control and use the same refresh path for standalone and product hosts. Choose the smallest concrete interface when
implementing this path; no new localisation package or provider framework is
prescribed.

The [TableView contract](../lib/TableView/docs/tableview-contract.md) owns what a switch
preserves: structural schema and interaction state stay intact while presentation
text changes. Its [implementation map](../lib/TableView/docs/tableview-implementation.md)
records the current gap, so embedded English alone cannot be reported as live
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

Resolve a missing message from a shipped parent language, then English. Every
project's loader applies this same rule; fallback is a complete message,
including all its plural forms. Preserve the language of the resolved message so
inherited English uses English plural rules.

Reject invalid catalogue data before packaging: missing English keys, duplicate
keys, incompatible placeholders, and incomplete plural sets. A failed language load
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
Microsoft documents [layout and RTL requirements](https://learn.microsoft.com/en-us/windows/apps/design/globalizing/adjust-layout-and-fonts--and-support-rtl).

Use the owned refresh path for product text. Microsoft's
[language override documentation](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.windows.globalization.applicationlanguages.primarylanguageoverride)
notes that already-loaded UI resources may not update immediately and that the
override persists for packaged apps. If platform controls require an override,
derive and reapply it from the engine's language. Never read it as a second preference
authority or rely on it to refresh existing bindings.

## Cost and proportionate evidence

Embedded catalogues per project are the implementation choice: each binary
carries only its own text. The engine must not load .NET, WinUI, or the UI's
catalogues for localisation.
Release obsolete language state after a switch and list languages without
loading every translation. Load platform formatting support only where needed.

Confirm this path with the first localised screen and tray message. Revisit the
representation if that implementation finds a simpler arrangement; preserve one
editable source for each message, one lookup policy per process, and the same
fallback results. No translation server, network fetch, watcher, or plugin framework is needed.

Follow [the testing policy](testing.md). A quick catalogue integrity check covers
keys, placeholders, and required forms when catalogues change; it does not assert
the wording of screen strings. One focused live-switch exercise during
implementation should cover an open draft, an owned control, tray state, fallback,
and switching back while downloads continue. Use temporary long-text/RTL data to
review layout when that path changes, not a maintained screenshot suite. Check
that the visible switch has no perceptible pause on this machine; investigate an
observed delay instead of imposing a benchmark run on every translation edit.
