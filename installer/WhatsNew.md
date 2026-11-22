## What's new

### Highlights

- **Library finds the media on your drives.** In **Settings**, under **Plugins**, turn on **Show media from this PC in Library**, or select **Discover** in an empty Library. Library then lists the videos, music and subtitles on your fixed drives. It finds them through Gozler, which TinyTorrent offers to install, and never walks your folders itself. A file that you rename or move on the same drive keeps its entry and its matches.
- **Missing files are a question, not a new download.** When a torrent's files are deleted or moved outside TinyTorrent, the torrent no longer downloads them again by itself. It shows **Files missing**, and the rest of the torrent keeps running. One dialog answers for every torrent at once: **Redownload** or **Skip**. You can also select **Cancel** to decide later.
- **TinyTorrent finds moved files.** If you move a torrent's files to another folder on the same drive, the torrent follows them without a check. With Gozler installed, it also finds them on another drive and tells you where it found them.
- **Instant feedback when you add from Explorer.** Open a torrent file or a magnet link, and **Adding torrent…** appears at once with its name. The same popup then shows the result. An addition no longer waits behind seeding on a busy network drive.

### Setup

- Setup can turn features on or off: media discovery, Gozler, IMDb ratings, city data for the peer map, and automatic subtitles. Choose **Custom installation**, or **Update TinyTorrent and change features** when you update. Each box shows what TinyTorrent has now, and the sizes count only what still has to download. A box you leave alone changes nothing.
- Setup starts faster, and it says what it is doing while it checks the required components and closes TinyTorrent.
- After an update, select **View what's new** on the last page to read these notes.

### Library

- **Refresh files** (F5) asks for the latest files again.
- When the installed Gozler is too old for Library, **Settings** and Library's message bar offer **Update**.
- **Library folders** in **Settings** > **Plugins** chooses which folders' media Library shows: clear a folder's check box to leave it out, or clear a drive and select only the folders you want. It changes only Library; Gozler still indexes those folders. **Leave folder out of Library** in a file's menu does the same in one click and offers **Undo**. A left-out folder keeps the matches you made, so its files come back with them.
- A video choice you saved is found again when another copy writes the title differently, for example *Ocean's Eleven* and *Oceans.Eleven*.
- The Library menus show the metadata commands only when the selection holds a video.

### Missing files

- The message bar, the torrent's row, the details pane and the **Files** tab show the question. **Review missing files…** opens the dialog.
- **Open** looks for a moved payload before it opens the folder.

### Subtitles

- When a settings, network or engine change interrupts **Find**, TinyTorrent tells you that Find stopped, and you can start it again.
- **Stop** also ends a Find that is still preparing, and nothing downloads after Stop.
- Copies of the same movie share one search limit, even when their file names write the title differently.
- A film whose title starts with "Forced", such as *Forced Vengeance*, keeps its full subtitles.
- `.ssa` and `.vtt` files count as subtitles everywhere.

### Settings

- Values that change, such as the speed limit mode, stay on one line, so rows never change height under you. Help text wraps, as in Windows Settings.
- Unit labels line up across a card.
- The schedule legend wraps instead of being cut off. Where another mode covers the selected period, its outline is dashed.

### Tables

- A column fits again when a longer value would cut it off, for example an upload speed that grows from "7 B/s" to "144.5 KB/s".
- **Trackers** and **Files** fit their columns to the new torrent's rows when you switch torrents.

### Peer map

- After a zoom, country borders sharpen first, and the routes follow.

### Bug fixes

- When a storage error was followed by a second error, clearing the second one no longer hides the first. Before, the **Retry** notice disappeared and the torrent started again without your answer.
- A move to a folder that already held a torrent's missing files no longer downloads them a second time.
- Errors that come from Windows show in your language.
- **Delete data** for IMDb also removes files an earlier version made.
- The Credits page names the location data providers in every language.
- A restored window is never narrower than its title bar.
- The installed engine is no longer packed, so antivirus programs are less likely to flag the installer.
