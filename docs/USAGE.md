# LyllyPlayer — usage guide

This document describes the **current** behavior of the Windows desktop app (main window, playlist, YouTube tab, lyrics, options, and persistence). Update it whenever behavior or labels change.

---

## Requirements (external tools)

**Playback** does **not** use **ffmpeg**. The app decodes and outputs audio with **LibVLC** (Windows native libraries are pulled in via the project’s LibVLC packages).

**YouTube** features (load playlist by URL/ID, search, refresh) need **yt-dlp** on your system or the app’s **internal** yt-dlp (see **Options → Tools**). Configure it by:

- Putting **yt-dlp** on your system `PATH`, or  
- Browsing to an executable under **Options → Tools**, or  
- Using **Use internal** when available.

There is **no ffmpeg field** in Options anymore; ignore older screenshots or third-party guides that mention it.

**Node.js** (under **Options → Tools**) is **not** required for basic use: normal playback, local files, and typical YouTube playlists only need **yt-dlp** (or internal). Node is for advanced YouTube options (e.g. **cookies from your browser**). Leave Node unset unless you need that.

When **Read metadata on load** is enabled for folders/M3U, the app probes **duration** (and related parse data) through **LibVLC**, not ffprobe.

---

## Main window (player)


| Control / area                 | What it does                                                                                                                                                                                                                                                                                                                                                                                   |
| ------------------------------ | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **options…**                   | Opens **Options** if it is not open. If **Options** is already open, **hides** it (position/state are saved). Same toggle pattern as **playlist…**.                                                                                                                                                                                                                                            |
| **playlist…**                  | Opens the **Playlist** window if it is not open. If it is already open, **hides** it (position/state are saved).                                                                                                                                                                                                                                                                               |
| **lyrics…**                    | Opens the **Lyrics** window when lyrics are enabled in Options, or toggles visibility if the window already exists. In **Ultra compact** layout, a compact **lyrics** control also appears on the transport row.                                                                                                                                                                                |
| **Playlist title** line        | Shows the playlist name. If the current playlist is **compound** (appended sources), it shows the **origin** for the **currently playing** item (best-effort). **Right-click** it to **Open origin** (browser for YouTube origins; File Explorer for local paths).                                                                                                                                                                                    |
| **Now playing** line           | Shows status and title/short messages. Common statuses: `STOPPED`, `FETCHING` (resolving media), `BUFFERING` (decoder running, not yet audible), `PLAYING` (PCM reaching the output device), `PAUSED`. You may also see `PREMIUM`, `AGE`, `UNAVAILABLE`, `COOKIE`, `ERROR`, etc. When the app shows an informational or error message, it can temporarily replace the normal now-playing line. |
| **Elapsed / duration**         | Current position and track length when known.                                                                                                                                                                                                                                                                                                                                                  |
| **Seek slider**                | Drag to seek within the current track (when enabled). In **Ultra compact** mode, the visualizer strip can act as a seek surface (see CHANGELOG 1.6.0).                                                                                                                                                                                                                                         |
| **VU meters / spectrum / off** | Lightweight level visualization. **Click** the visualizer area to **cycle**: **VU** → **spectrum** → **off** (same bar height; off keeps the empty slot so the layout does not jump). Spectrum is driven from the same PCM samples sent to the audio device (not the pre-buffer queue), so it tracks what you hear more closely.                                                               |
| **<< / > / >>**                | **Previous** track, **play/pause** (or resume / start), **next** track.                                                                                                                                                                                                                                                                                                                        |
| **■** (Stop)                   | Stops playback (transport row).                                                                                                                                                                                                                                                                                                                                                                |
| **Shuffle OFF / Shuffle ON**   | Toggles shuffle for the **play order** (the playlist list stays in original order).                                                                                                                                                                                                                                                                                                            |
| **Repeat: …**                  | Cycles **Repeat: None** → **Repeat: Single** → **Repeat: All** (loop queue) → back to **None**.                                                                                                                                                                                                                                                                                                |
| **Vol** slider                 | Output volume.                                                                                                                                                                                                                                                                                                                                                                                 |
| **[-]** (compact)              | Toggles **compact** layout: narrower chrome and a denser transport area. **Compact layout** is either **Normal compact** or **Ultra compact** (see **Options → System**). When **Compact mode hides Playlist and Options** is enabled, entering compact **hides** the Playlist, Options, and Lyrics windows (if open); they can be restored when you leave compact, or reopened from the main window while compact (see below). State is saved in `settings.json`. |
| **TOP** (always on top)        | Toggles **Always on top** for the **main window**. When enabled, the app keeps the **active** title bar colors even while unfocused (no “passive/inactive” title bar tint). Auxiliary windows can each opt in under **Options → System** (“Also keep … on top **when TOP is enabled**”).                                                                                                     |
| **Title bar**                  | Drag to move the window. **[X]** closes the app.                                                                                                                                                                                                                                                                                                                                               |

### Auxiliary windows (Playlist, Options, Lyrics)

- **Snap / dock:** When you move Playlist, Options, or Lyrics near a main-window edge, they can **snap** with a small gap. While snapped, moving or resizing the main window **keeps the auxiliary window aligned** (edge and offset are persisted where applicable).
- **Compact + “hide aux”:** On entering compact with the hide option on, auxiliary windows are hidden. **Playlist** can still be opened from the **PL** control shown in compact / ultra layouts; that counts as a user-opened playlist for the compact session so it is not immediately closed again by the same policy. **Lyrics** behaves similarly when opened via **lyrics…** while compact.


### Global media keys (optional)

In **Options → System**, **Enable global media keys** registers a low-level hook so **Play/Pause**, **Next**, and **Previous** media keys control LyllyPlayer even when it is not focused.

Note from the implementation: the hook consumes those keys (other apps may not see them). If another media tool “wins” instead, try restarting LyllyPlayer after that tool.

---

## Playlist window


| Control                | What it does                                                                                                                                                                                                                                                                                                                |
| ---------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **↻ (Refresh)**        | Re-loads the **current playlist source** while trying to keep the current track. **Disabled** for sources that don’t support refresh (e.g. non‑YouTube HTTP stream URLs) and **disabled for compound playlists** (when multiple sources have been appended and the app cannot reliably refresh them all).                           |
| **Window title**       | Includes the current **source** after the second em dash (URL, path, or `Search: …`).                                                                                                                                                                                                                                                                        |
| **Tabs**               | The playlist window uses three tabs: **(blank)** (main playlist view), **YouTube**, and **Files**. The main tab contains the playlist list, search, sort, refresh, and removal actions. The YouTube tab contains YouTube flows. The Files tab contains local file/folder import and save/load actions. |
| **YouTube tab**        | Sub-tabs (left to right): **Search videos**, **Open URL**, **Search playlists**, **Import playlist**, **My playlists**. **Open URL**: paste a **YouTube** playlist or video URL (resolved via yt-dlp) or a **direct HTTP(S) stream** URL (e.g. Icecast) for a **single-item** playlist. **Import playlist** supports **Replace** or **Append** (and optional **Remove duplicates**). The app remembers your last Replace/Append choice. |
| **Files tab**          | **New playlist…** clears the current playlist. **Save playlist…** saves to **JSON** (internal format) or **M3U/M3U8** (based on your Save dialog choice). **Load playlist…** loads **JSON**, **M3U**, or **M3U8**. Local import actions add from a **folder** and/or specific **files**. Replace/Append and dedupe behavior follows the import behavior controls. |
| **Sort** row           | Pick **Ascending / Descending** (toggle), a **sort mode** (**Title** or **Name**, **Source**, **Duration**, or **None** depending on playlist type), then **Sort** to apply. Sorting **reorders the real playlist** (playback order), not just the view.                                                                                                                                       |
| **Remove**             | **Duplicates** removes best-effort duplicates; **Missing** removes missing local files and YouTube rows marked **unavailable**, **premium-gated**, or **age-restricted** (same criteria as greyed rows). **Queued** references to removed entries are dropped; the app tries to keep the current track when it still exists. |
| **Queue** panel        | When the song **queue** is non-empty, a **Queue** list appears above the main list. **Double-click** a row to jump playback. **Right-click** context menu: **Open** / **Open file location** / **Open source** (label depends on item), **Add to queue**, **Remove from queue** (queue row only for remove).              |
| **Playlist list**      | Full playlist in **playlist order**. **Double-click** a row to jump playback. **Right-click**: same pattern as the queue—**Open file location** for local files, **Open source** for URLs/YouTube, plus **Add to queue** / **Remove from queue** when applicable. Rows that are **queued** for “play next” show distinct styling. |


### Long-running loads (overlay)

When loading or refreshing takes time, a **themed overlay** (same **Surface** / **Foreground** palette as the rest of the app) appears with **Cancel** (stops the operation and **restores the playlist** as it was before that action, unless **Options → Playlist → Keep incomplete playlist on cancel** is enabled — see below).

A **progress bar** under the overlay message reflects **metadata** loads (per-file LibVLC parse / duration probes) and **YouTube search** (staged search batches). **YouTube playlist refresh** shows an **indeterminate** bar while yt-dlp runs.

**Taking too long? [Skip metadata]** appears when **Read metadata on load** is on and you load/refresh a **folder** or **M3U** source: it cancels the slow metadata pass and **reloads the same path** with metadata reading turned off for that operation only (you keep the new playlist; this does not change your Options toggle). The “no metadata” reload runs in the background so the UI stays responsive.

**Taking too long? [Stop search]** can appear during long **YouTube video search** batches; it stops the staged search and leaves whatever results were already merged (subject to **Keep incomplete playlist on cancel** under **Options → Playlist**).

### Drag & drop (playlist list)

You can drag and drop onto the playlist list:

- **Local files / folders** (folders import recursively)
- **Browser tabs / URLs** (http/https). Dropping a YouTube playlist URL (or a watch URL containing a `list=...` parameter) uses the same URL import flow as **Open URL**.

Unsupported drops are ignored.

---

## Options window

Tabs (left to right): **Tools**, **Playlist**, **System**, **Audio**, **Theme**, **Lyrics**, **Log**. Changes in most tabs are held as a **draft** until you click **Apply**. **Cancel** closes without applying the draft.

### Tools

- **yt-dlp** — shows the **effective** binary (saved path, **internal** copy, or `PATH`), **Browse…**, **Use PATH**, and **Use internal** when supported. Optional **yt-dlp updates** expander (weekly check / check now) applies to the internal copy.
- **Node.js** (optional) — same pattern (**Browse…**, **Use PATH**). Needed only for **cookies from browser** / advanced YouTube flows gated below. Basic YouTube playback only needs yt-dlp.

Under Tools you may also see:

- **YouTube EJS solvers** — **Prefer from GitHub** (default; same as older app behavior) or **bundled only** (no `--remote-components ejs:github`).
- **Use cookies from browser for YouTube** — when enabled, passes your text as the value for yt-dlp’s `--cookies-from-browser` (see `yt-dlp --help` for syntax). Requires a non-empty value when enabled.

If you don’t care about those, you can ignore Node completely.

### Playlist

- **Cache MB** — upper bound used for cache behavior (YouTube stream cache, resolve cache, and related disk usage).  
- **Playlist auto-refresh** — **Disabled**, or **1 / 5 / 30 minutes** for sources that support refresh.  
- **Keep incomplete playlist on cancel** — when **on**, cancelling a **YouTube search** (Cancel) or a **playlist refresh** from the playlist window keeps whatever partial results had already arrived. When **off**, Cancel restores the playlist from before that operation (default).
- **Playlist export (M3U)** — controls how **Save playlist…** behaves when you pick M3U/M3U8:
  - Include YouTube entries (as webpage URLs)
  - Prefer relative paths (for local files)
  - Include LyllyPlayer rich comment metadata (`#LYLLY:...`)
- **Search defaults** — default result count and minimum length for YouTube video search.
- **Local import defaults** — include subfolders by default and whether to read metadata on load (can be slow).

### System

- **Enable global media keys** — see above.
- **Window**
  - **Also keep Playlist / Options / Lyrics on top** — only applies when main **TOP** is on; each auxiliary window can follow suit.
  - **Compact mode hides Playlist and Options** — when enabled, switching the main window to **compact** hides those windows (and **Lyrics** if it was open). Windows that were open are restored when you leave compact unless you chose to keep them closed. You can still open **Playlist** or **Lyrics** from the main window while compact (see **Main window** above).
  - **Compact layout** — **Normal compact** vs **Ultra compact** (denser two-row layout; playlist shortcut and lyrics control on the transport row).
- **App icon** visibility (taskbar / notification area / both).

### Audio

- **Output device** — pick **Default** or a specific **WaveOut** device. Changing device and clicking **Apply** hot-swaps output while playing when the selection actually changes (same device does nothing).
- **YouTube stream quality** — **Auto** (best available), **High** (prefers WebM/Opus-style streams), **Medium** / **Low** (bitrate caps for slower links). Passed to yt-dlp as the format selector; takes effect on the **next** track (not mid-track).
- **AGC** — **Automatic gain control** (real-time). Reduces loudness jumps without needing the full file cached. This is **not** “peak normalize”; it continuously adjusts gain while playing.

### Theme

Background image mode (**None** / **Default (Lylly)** / **Default (Meow Cat)** / **Custom**), optional **custom image** path, **background colors** (default / derived from image / custom + color picker), **opacity**, **scrim**, **window title** mode (default / custom), **window border** thickness, **UI scale** (50%–200%). **Save theme…** exports theme-related settings. **Load theme…** imports them and applies immediately if the file is valid.

With **Background: None**, **From image** under background colors is disabled (no wallpaper to sample), and **Background scrim** is disabled. If your saved theme had **From image** with **None**, opening Options coerces colors to **Default**.

If you pick the wrong file (e.g. a playlist JSON), **Load theme…** warns that the JSON does not look like a theme file (or contains no theme keys) instead of failing silently.

### Lyrics

- **Display lyrics** enables lyric resolving and highlighting (Lyrics window + optional title bar line).
- **Try to get lyrics for local files** enables LRCLIB lookups for local tracks too (best-effort).

Lyrics come from either yt-dlp (when available on the YouTube item) or LRCLIB (one query per track, cached on disk).

The **Lyrics** window uses the same themed chrome as other auxiliary windows and persists size, position, snap state, and “open” preference like Playlist and Options.

### Log

- Shows the tail of `app.log` and lets you **Pause** (freeze view) for diagnostics.
- **Open in separate window…** pops out the log window. While the popout is open, the embedded view suspends itself to avoid duplicate file tailing.

---

## Where settings and data are stored

Under `%AppData%\LyllyPlayer\` (i.e. `Environment.SpecialFolder.ApplicationData\LyllyPlayer`):


| File                  | Purpose                                                                                                            |
| --------------------- | ------------------------------------------------------------------------------------------------------------------ |
| `settings.json`       | Options (including tools, audio quality, output device), window layout, volume, repeat/shuffle, paths, theme, etc. |
| `last-playlist.json`  | Last non-empty playlist snapshot for **startup restore** (not a full refresh of network sources).                  |
| `playlist-cache.json` | Cached **YouTube** playlist entries to avoid hitting yt-dlp on every launch when the playlist ID matches.          |
| `app.log`             | Append-only log file (also viewable in the Log window).                                                            |


---

## Tips

1. **First YouTube load:** ensure yt-dlp is found (Options → Tools, or PATH). The app may use **playlist-cache.json** after a successful resolve.
2. Use **Youtube… → Open URL** for quick paste loads; use **Import playlist** when you need Replace/Append and duplicate handling.
3. **Refresh** after changing files on disk for **folder** or **M3U** playlists.
4. **Repeat: Single** is useful for practicing one track; **Repeat: All** loops the whole queue.
5. If **global media keys** conflict with another app, turn the option off under **Options → System**.
6. **Playlist items** can show **[PREMIUM]** when YouTube reports Music Premium–only content; those rows are greyed like unavailable items. **Clean invalid items** removes them (and broken locals) in one step.
7. Use **Add to queue** (right-click) to build a **Queue** panel of “play next” items without reordering the whole playlist.

---

## Out of scope / limitations (current code)

- Targets **.NET 8**, **Windows 10+**, WPF.  
- **YouTube “Search”** playlists cannot use the same **Refresh** behavior as a normal playlist URL.

If you find a mismatch between this guide and the app, update **this file** or the code so they stay aligned.

---

## Contributor note (code organization)

Playback and timers still live largely in the WPF layer, but playlist **file** I/O (internal JSON + M3U export) is implemented in **`PlaylistFileService`** and composed on **`AppShell`** (`PlaylistFiles`) so the same logic can be reused from other hosts later. The playlist window receives backend operations through **`PlaylistWindowOps`** rather than a long list of unrelated delegates.