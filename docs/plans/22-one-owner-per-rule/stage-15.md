# Stage 15 gate record: engine windows

The gate of [stage 15](../22-one-owner-per-rule.md#15-engine-windows). Line
numbers are those of commit 7e0ee84; no engine file changed between that
commit and the start of this stage. This record replaces the stage's text for
the session that implements it.

## Owners and interfaces

The owners are those the [engine instructions](../../../engine/AGENTS.md)
name. Each member below is the whole interface its callers use.

**`Acrylic`** (`engine/inc/Desktop/Acrylic.h`) owns what every acrylic window
shares, besides the surface it already owned:

| Member | Rule |
| --- | --- |
| `static constexpr DWORD frame` | The window styles of an acrylic window. The frame keeps DWM's rounded corners and shadow; `Handle` removes its chrome. |
| `static void Register(wchar_t const* name, WNDPROC procedure)` | The window class: product icon, arrow pointer. |
| `std::optional<LRESULT> Handle(HWND window, UINT message, Theme theme)` | `WM_NCCALCSIZE` 0, `WM_NCHITTEST` `HTCLIENT`, `WM_ERASEBKGND` 0, and `WM_SETTINGCHANGE`, `WM_SYSCOLORCHANGE`, `WM_THEMECHANGED`, `WM_DWMCOMPOSITIONCHANGED` apply the theme. Empty for every other message. A window procedure calls it after its own cases, in place of `DefWindowProc`. |
| `static LOGFONTW MessageFont(UINT dpi)` | Windows' message font at a DPI. |
| `int Scale(int value, UINT dpi)` | A length in effective pixels, in physical pixels at a DPI. A free function in `Acrylic.h`, because about 60 lines of `Popup.cpp` call it and `Acrylic::Scale` would rewrap many of them. |
| `Canvas::Icon(POINT at, int size, HICON icon = nullptr)` | Draws an icon, or else the product icon loaded at that size. A null `HICON` means the product icon, as `Popup::Content::icon` already says. |
| `Blend` (file-local) | One channel blend that rounds; `Mix` (colours) and the pixel `Blend` both use it. |

**`Popup`** (`engine/inc/Desktop/Popup.h`):

| Member | Rule |
| --- | --- |
| `static void Wait(std::function<bool()> const& waiting, std::function<void(MSG const&)> const& thread = {})` | Runs this thread's messages while `waiting` returns true. A message without a window, such as a thread timer's, goes to `thread` instead of a window procedure. A `WM_QUIT` ends the wait and is posted again. |
| `Refresh()` (private) | Changed content: lay it out and name the window by its title. |
| `LineHeight(HDC device)` (private) | The height of one diagnostic line, in the caption font. |
| `MenuBounds(item)` (existing) | The one menu row rectangle; `MenuItemAt` tests the point against it. |

**`Toasts`** (`engine/inc/Desktop/Toasts.h`):

| Member | Rule |
| --- | --- |
| `static Popup::Action Exit(Strings const&, std::wstring tooltip, std::function<void()> run)` | The Exit button of a prompt. |
| `static Popup::Action OpenFolder(Strings const&, std::function<std::filesystem::path()> folder, std::wstring tooltip)` | Open folder. `folder` runs at the click; an empty path shows nothing. |
| `Message` (existing) | Pushes the stopped toast's Retry once. |

**`Window`** (`engine/src/Desktop/Window.cpp`): a file-local
`AllowForeground(DWORD processId)` and a private `AwaitReady()`.

## Callers and the member each calls

| Caller | Today | Calls |
| --- | --- | --- |
| `Main.cpp` 206 `ShowRefusal` | Own loop | `Popup::Wait` while its popup is open |
| `Application.cpp` 504 `ConfirmExit` | Own loop and `WM_QUIT` repost | `Popup::Wait` while the toasts are open and Exit is confirming |
| `Instance.cpp` 168 `Conflict::Resolve` | Own loop with the thread timer | `Popup::Wait` while its popup is open, timer messages to `Check` |
| `Application.cpp` 501, `Instance.cpp` 208 | Exit action by hand | `Toasts::Exit`; Conflict then sets `closes` and `enabled`, as it does for Force exit |
| `Toasts.cpp` 438 | Open folder by hand | `Toasts::OpenFolder` with a folder found at the click |
| `Main.cpp` 203, `Toasts.cpp` 365, 452, `Window.cpp` 705 | `OpenFolder` with a path | `OpenFolder` with a function that returns the path |
| `Toasts.cpp` 433, 457 | Retry twice | One Retry after both branches |
| `Splash.cpp` 30, `Popup.cpp` 631 | `WNDCLASSW` by hand | `Acrylic::Register` |
| `Splash.cpp` 66, `Popup.cpp` 810, 1836 | Frame styles by hand | `Acrylic::frame` |
| `Splash.cpp` 229, `Popup.cpp` 1691, 2032 | Message cases | `Acrylic::Handle` |
| `Splash.cpp` 22, `Popup.cpp` 92, `Tray.cpp` 272, 288 | `MulDiv` by 96 | `Scale` |
| `Splash.cpp` 167, `Popup.cpp` 912 | `SPI_GETNONCLIENTMETRICS` | `Acrylic::MessageFont` |
| `Acrylic.cpp` 71, 77, 102 | Truncating and rounding blends | One rounding `Blend` |
| `Splash.cpp` 193, `Popup.cpp` 1075 | `LoadImageW` and `DrawIconEx` | `Canvas::Icon` |
| `Popup.cpp` 693, 781, 848 | Layout and window text | `Refresh` |
| `Popup.cpp` 977, 1110 | Caption metrics | `LineHeight` |
| `Popup.cpp` 1900, 1919 | Two row computations | `MenuItemAt` tests `MenuBounds` |
| `Window.cpp` 137, 230, 278, 392 | Foreground grant | `AllowForeground` |
| `Window.cpp` 167, 176 | Ready wait, launch kind, splash | `AwaitReady` |

## Findings to re-read

- Content-change steps (`Popup.cpp` 693, 781, 848): **kept**. The rule: when
  the content changes, the popup lays it out again and takes its title as its
  window text, which names a prompt on the taskbar and in Alt+Tab. Settling the
  buttons stays a separate step of the two in-place changes. The text is not
  set in `Layout`, because `Layout` also runs on a DPI or settings change, and
  setting the text again there raises a name change for nothing.
- Caption line height (977, 1110): **kept**. The rule: a diagnostic line is one
  line of the caption font tall, in the layout and in the paint.
- Icon paint (`Splash.cpp` 193; `Popup.cpp` 1075): **kept**. The rule: an
  engine window draws an icon at its exact pixel size, the product icon loaded
  at that size unless the content gives another.

## Decisions

The plan leaves these open; each is decided by the engine contract, the Owner
rulings and usability.

1. **The loop's condition comes from the caller.** `ConfirmExit` waits while
   the opt-out save is pending after its popup closed, and stops when Windows
   ends the session, so a loop that ran only while the popup is open could not
   serve it. `Wait` is static for the same reason: `Application` holds `Toasts`,
   not the popup.
2. **Messages without a window go to the caller.** Only `Conflict` needs it:
   its thread timer must run `Check` outside every window procedure, because
   `Check` can throw. Without a handler, the loop dispatches such messages as
   before. Dispatching a windowless message without a timer procedure does
   nothing, and the engine sets no timer procedure, so handing them all to the
   handler changes nothing.
3. **Settled as: a prompt loop posts a quit message on again.** `Wait` posts the
   `WM_QUIT` it takes with the same exit code. Before, only `ConfirmExit` did.
   In `ShowRefusal` the process ends next, and nothing posts a quit message
   while `Conflict` asks, so no person sees a difference.
4. **The two loops that stay.** `Application::Run` is the thread's own loop,
   which ends on `WM_QUIT` and returns its code. `EndSession` waits until a
   deadline and drops `WM_QUIT`, because it posts its own at the end. Neither
   waits for a popup.
5. **Settled as: blended colours round.** One channel blend rounds to the
   nearest level. The background's premultiplied fill (`Acrylic.cpp` 171) is
   the background laid over transparent black, so it uses the same blend and
   rounds too; a channel moves by at most one level of 255. The disabled text
   colour is unchanged in both themes (112 and 155 before and after).
6. **The frame styles join the window messages.** `WM_NCCALCSIZE` returns 0
   only because the windows keep the frame styles for DWM's corners and shadow,
   and the three comments say so. Moving one half of that rule to `Acrylic`
   and leaving the other in three callers would split it, so `Acrylic::frame`
   holds the styles.
7. **`Popup`'s `WM_DPICHANGED` lays out without `Apply`.** `Apply` sets the DWM
   attributes, the blur and the colours, none of which depend on the DPI, and
   `Layout` repaints. `WM_SETTINGCHANGE` lays out, then `Handle` applies, in
   the order it had.
8. **The tray uses `Scale`.** `Tray.cpp` 272 and 288 compute the same length in
   effective pixels. The tray's menu font is `lfMenuFont`, a different font, so
   it stays.
9. **`OpenFolder` takes a function for every caller.** Two overloads would be a
   second entry for one button. The fixed callers pass a function that returns
   their path; none of their paths can be empty, so the empty check changes
   nothing for them.
10. **The Exit tooltip stays a parameter.** `ConfirmExit`'s Exit has no tooltip
    today and passes none, so its prompt does not change.
11. **The foreground grant includes `Window.cpp` 230.** It grants the same
    right to the window process it just started.

## Seen and left

- The three `WM_NCCREATE` trampolines (`Splash::Procedure`,
  `Popup::Procedure`, `Popup::MenuProcedure`) each store a different member.
  A shared one needs a template over member pointers, which costs more to read
  than the copies. The stage does not list them.
- `Popup::Show` 673 and `Popup::Ask` 775 both focus the answer, take the
  foreground and take the focus. The stage does not list the pair.
- Two different rules that the searches for the absorbed copies also find:
  `Application.cpp` 117 and 130 register the hidden owner and broadcast
  windows, which are not acrylic windows and have no pointer; `Pipe.cpp` 496
  and `Main.cpp` 191 grant the foreground to the engine that takes a forwarded
  request and to the launch that Retry starts, not to the product window;
  `Toasts.cpp` 514 shows a finished torrent's folder and opens the window when
  it has none, where Open folder shows nothing.

## Consumers of each changed name

Searched in `engine/`, `app/src` with the capture journeys, `app/tests`,
`engine/tests`, `installer/` and `lib/*/tests`:

- `Popup::Wait`, `Toasts::Exit`, `Acrylic::Register`, `Acrylic::Handle`,
  `Acrylic::frame`, `Acrylic::MessageFont`, `Canvas::Icon`, `AllowForeground`,
  `AwaitReady`, `Popup::Refresh` and `Popup::LineHeight` are new; their callers
  are the table above.
- `Toasts::OpenFolder` changes its parameter; its consumers are `Main.cpp`
  203, `Toasts.cpp` 365, 438, 452 and `Window.cpp` 705.
- `Scale` moves from `Popup.cpp` and `Splash.cpp` to `Acrylic.h`; its consumers
  are those two files and `Tray.cpp`.
- No test, check script or capture journey names an engine window class, these
  members or the automation identifiers `Exit`, `OpenFolder`, `Retry`,
  `ForceExit` and `Dismiss`. The identifiers do not change.

## Files the change may edit

`engine/inc/Desktop/Acrylic.h`, `Popup.h`, `Toasts.h`, `Window.h`;
`engine/src/Desktop/Acrylic.cpp`, `Application.cpp`, `Instance.cpp`,
`Popup.cpp`, `Splash.cpp`, `Toasts.cpp`, `Tray.cpp`, `Window.cpp`;
`engine/src/Main.cpp`; `engine/AGENTS.md`.

## Proposed contract text

For the lead to apply to `docs/contracts/engine.md`, at the end of the
paragraph that starts "Callbacks and worker completions wake the owner", once
the file is free:

> A loop that waits for the person's answer to a popup posts the `WM_QUIT` it
> takes again, so the thread's own loop ends too.

Rounded blending needs no contract text: no contract describes the popup's
colours to the level of one channel.

## Cost

No build in this session. Batch H: one engine build, then one look at the toast
popup, a prompt, the More menu and the splash, each in light and dark. About
two hours of agent work.
