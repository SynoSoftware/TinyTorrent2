# Engine

Follow the [root instructions](../AGENTS.md). The engine's contract is the
[engine contract](../docs/contracts/engine.md): read its shared rules and the
references its change index selects. Its words and catalogue follow
[localisation](../docs/contracts/localisation.md).

## Engine UI ownership

The engine draws its own popup, the custom Win32 acrylic window for toasts and
prompts. Each behaviour has one owner, because a second copy in a caller drifts
and the popups stop behaving alike:

- `Popup` owns window behaviour, rendering, focus, keyboard, accessibility,
  button states and dismissal, for both toasts and prompts. It adds Dismiss,
  last, to every toast, so no caller adds it. A caller passes
  content and answers to `Show` or `Ask`, and ends a prompt through `Close`.
- `Toasts` owns which toasts show, which share a popup, and the buttons that
  failures and prompts reuse (`Retry`, `OpenFolder`, `ForceExit`).
- Callers, such as `ExitOther`, own their business logic: what each answer
  does, when the prompt changes, and whether a failure answers the person's
  own action and so takes the focus.

## Engine button rules

**Owner ruling.** These rules apply to every button the engine draws. In the
acrylic popup, Fluent 2 principles apply within these rules, with no
accent-coloured primary buttons, outlined buttons or other variants.

- **Labels:** one or two familiar words. Buttons share one row in equal widths.
- **Icons:** every engine-authored button has a leading Lucide icon, as the
  window's buttons do. Platform-owned control glyphs stay: the popup's Close
  and More, native menus and Windows task dialogs.
- **Appearance:** one filled, borderless style for every button, with no
  primary, secondary, destructive, accent, outlined or raised variant. No
  button looks preferred, so the focus ring alone marks the button Enter runs.
  High contrast is the exception: there each button has an edge in the
  button text colour (`COLOR_BTNTEXT`), because the subtle fill leaves the
  buttons invisible in a high contrast theme.
- **Interaction:** hover and pressed states change the fill. Keyboard focus is
  always visible while a prompt or an interactive toast has the focus. When
  no button has the focus, the default answer takes it as soon as it is
  enabled, so the ring always shows the button Enter runs. A disabled button
  looks disabled and takes no hover, press, focus or invocation, so the person
  never presses a button that cannot act.
- **Consistency:** keep the established sizing, spacing, hit targets and
  accessibility semantics, so every engine button reads and works alike.
- **Accessibility:** every actionable control has an accessible name and a
  stable automation identifier through the native Win32 accessibility APIs, so
  screen readers and UI Automation can identify and drive it.
