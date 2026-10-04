# Localisation

This is part of the [architecture](architecture.md), not an
implemented feature. English alone must provide a complete working application.
Adding another language changes catalogue data, not application behavior.

Use the same text path from the first product screen. TableView already reads
its English text this way; the product projects follow the same pattern. There
is no need for a web i18n framework or translation service in this native
application.

## Languages during implementation

`en.json` owns canonical keys and English text. The owner requires live English
and Spanish throughout implementation, so maintain `es.json` for each implemented
surface alongside its settled English text. This makes the requested language
switch useful immediately, including new dialogs, without flags.

Other language files remain frozen until their translation task: they stay
unread and unedited. A message still being reworded makes repeated translation
wasted work. Those tasks translate the settled text once against `en.json` as it
stands then.

A key that `en.json` lacks shows as its dotted name, `column_menu.hide`, where
the text belongs. Lookup never throws for a missing key: a person sees the gap on
screen at once, and one forgotten key cannot break a window.

Finding missing keys (used by code, absent from `en.json`) and unused keys
(present in `en.json`, used by no code) happens only on request, by a compiler
step or an agent scan. No standing test or build step does it, because a missing
key already shows on screen and an unused key causes no fault a user can see.

## One catalogue per project

Each project that shows text owns an `en.json` in the `Resources/` folder beside
its project file: the canonical message keys and English text for that project's
surfaces. Another shipped language is a `<language-tag>.json` with the same keys
in the same folder. These
files are the project's catalogues. The project embeds them in its own binary,
so a library or the engine works without another project's text.
These catalogues own translation data; the [protocol](protocol.md#encoding-and-validation) owns IPC encoding.

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

The product's process-local localisation owner prepares TableView's immutable
`Strings` value with `Strings.Load(language)`, alongside its
own text and on the same background thread. It publishes `Table.Strings`, host bindings, language and flow direction
together on the UI thread. TableView keeps catalogue lookup internal and owns no
saved language preference. New views bind to the same current prepared value.
Standalone English uses the same presentation refresh path without host setup.

The [TableView contract](../lib/TableView/docs/tableview-contract.md) owns what a
switch preserves. Its [implementation map](../lib/TableView/docs/tableview-implementation.md)
records the implementation and the remaining runtime verification.

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
lost focus/selection, or discarded draft. Download work is unaffected. Native
text composition and IME candidate handling get first refusal on input:
application shortcuts must not consume composition keys or commit unfinished
text. Use standard text controls and their composition behavior, not another
input system. Keep blocking resource work off the UI thread. Publish a prepared language change
together and retain the current language if preparation fails. Coalesce rapid
choices so older work or an acknowledgement cannot overwrite the latest selection.

Language is chosen in Preferences or the title bar, and only one UI runs. The
title-bar control uses a language abbreviation, never a flag: language does not
identify a country. Both controls use the same preference owner and live refresh
path so switching stays immediate wherever the person makes the choice. The UI
sends the choice through the ordinary settings command, and the engine updates the tray
from it; nothing needs to flow back, so there is no language notification and no
other transport or event bus. Saving runs asynchronously under the [engine persistence contract](engine.md#persistence-and-file-safety):
distinguish the live language from a successfully saved preference, report save
failure, and never silently claim persistence. The engine selects the validated
language when accepting the command; storage completion updates only the saved
preference. A failed save keeps the current language until another selection or
restart. Snapshot settings carry that live selection and `language_saved`
states whether it matches the saved choice. Reconnecting reads the engine's
language again. Tray menus use the current catalogue when shown; an already-open
tray menu must be refreshed or safely reopened without executing a selection.

## Language rules and fallback

Resolve a missing message from a shipped parent language, then English. Every
project's loader applies this same rule; fallback is a complete message,
including all its plural forms. Preserve the language of the resolved message so
inherited English uses English plural rules.

Reject invalid catalogue data before packaging: translated keys that English
lacks, duplicate keys, incompatible placeholders, and incomplete plural sets. A failed language load
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
globalization facilities, including the [ICU C APIs](https://learn.microsoft.com/en-us/windows/win32/intl/international-components-for-unicode--icu-)
Windows supplies, which work on the projects' Windows 10 1809 minimum. Do not
bundle another ICU distribution, raise the minimum for plurals, or build a
general message-expression interpreter. The catalogue contract needs only the
message forms and substitutions actually used by the product.

The currently shipped English and Spanish use the same singular rule for integer
torrent counts: one at 1, other otherwise. The native tray selects its explicit
singular or plural message by that known rule, because loading another API to
produce identical results adds no user benefit. Revisit that selection when a
shipped language needs a different rule; this is not a general plural rule for
other languages.

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
duplicate keys, placeholders, and required forms when catalogues change; it does
not assert the wording of screen strings. One focused live-switch exercise should
cover an open draft, an owned control, tray state, fallback, and switching back
while downloads continue. It switches to a language generated from `en.json` at
test time, because every other language file is frozen. Use temporary long-text/RTL data to
review layout when that path changes, not a maintained screenshot suite. Check
that the visible switch has no perceptible pause on this machine; investigate an
observed delay instead of imposing a benchmark run on every translation edit.
