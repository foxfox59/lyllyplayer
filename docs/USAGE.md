# LyllyPlayer — usage guide

This document describes the **current** behavior of the Windows desktop app (main window, playlist window, options, and persistence). Use it as a checklist while you review the product; adjust wording or sections if you change the UI later.

---

## Requirements (external tools)

LyllyPlayer does **not** ship **ffmpeg** or **yt-dlp**. Install them separately, then either:

- Put them on your system `PATH`, or  
- Set explicit paths under **Options → Tools** (browse buttons for **yt-dlp** and **ffmpeg**).

**YouTube** features (load playlist by URL/ID, search, refresh) need **yt-dlp**. **ffmpeg** is used for media playback, optional **metadata when loading** local folders/M3U (via ffprobe), and related tooling.

**Node.js** (when shown under **Options → Tools**) is **not** required for basic use: normal playback, local files, and typical YouTube playlists only need **yt-dlp** and **ffmpeg**. Node is for additional YouTube options (e.g. **cookies from your browser** so playback can follow your signed-in session). You can leave Node unset unless you need that.

---

## Main window (player)


| Control / area                 | What it does                                                                                                                                                                                                                                                                                                                                                                                   |
| ------------------------------ | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **options…**                   | Opens **Options** (see below).                                                                                                                                                                                                                                                                                                                                                                 |
| **playlist…**                  | Opens the **Playlist** window if it is closed. If it is already open, closes it (window position is saved).                                                                                                                                                                                                                                                                                    |
| **Playlist title** line        | Shows the playlist name. If the current playlist is **compound** (appended sources), it shows the **origin** for the **currently playing** item (best-effort). **Right-click** it to **Open origin** (browser for YouTube origins; File Explorer for local paths).                                                                                                                                                                                    |
| **Now playing** line           | Shows status and title/short messages. Common statuses: `STOPPED`, `FETCHING` (resolving media), `BUFFERING` (decoder running, not yet audible), `PLAYING` (PCM reaching the output device), `PAUSED`. You may also see `PREMIUM`, `AGE`, `UNAVAILABLE`, `COOKIE`, `ERROR`, etc. When the app shows an informational or error message, it can temporarily replace the normal now-playing line. |
| **Elapsed / duration**         | Current position and track length when known.                                                                                                                                                                                                                                                                                                                                                  |
| **Seek slider**                | Drag to seek within the current track (when enabled).                                                                                                                                                                                                                                                                                                                                          |
| **VU meters / spectrum / off** | Lightweight level visualization. **Click** the visualizer area to **cycle**: **VU** → **spectrum** → **off** (same bar height; off keeps the empty slot so the layout does not jump). Spectrum is driven from the same PCM samples sent to the audio device (not the pre-buffer queue), so it tracks what you hear more closely.                                                               |
| **<< / > / >>**                | **Previous** track, **play/pause** (or resume / start), **next** track.                                                                                                                                                                                                                                                                                                                        |
| **Shuffle OFF / Shuffle ON**   | Toggles shuffle for the **play order** (the playlist list stays in original order).                                                                                                                                                                                                                                                                                                            |
| **Repeat: …**                  | Cycles **Repeat: None** → **Repeat: Single** → **Repeat: All** (loop queue) → back to **None**.                                                                                                                                                                                                                                                                                                |
| **Vol** slider                 | Output volume.                                                                                                                                                                                                                                                                                                                                                                                 |
| **[-]** (compact)              | Toggles **compact** layout: narrower main chrome (playlist/options row and some transport controls hidden); **playlist** and **options** windows close until you expand again. State is saved in `settings.json`.                                                                                                                                                                              |
| **TOP** (always on top)        | Toggles **Always on top** for the **main window**. When enabled, the app keeps the **active** title bar colors even while unfocused (no “passive/inactive” title bar tint). This does **not** change auxiliary windows’ behavior; they still follow their own “also keep on top” settings.                                                                                                     |
| **Title bar**                  | Drag to move the window. **[X]** closes the app.                                                                                                                                                                                                                                                                                                                                               |


### Global media keys (optional)

In **Options → System**, **Enable global media keys** registers a low-level hook so **Play/Pause**, **Next**, and **Previous** media keys control LyllyPlayer even when it is not focused.

Note from the implementation: the hook consumes those keys (other apps may not see them). If another media tool “wins” instead, try restarting LyllyPlayer after that tool.

---

## Playlist window


| Control                | What it does                                                                                                                                                                                                                                                                                                                |
| ---------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **↻ (Refresh)**        | Re-loads the **current playlist source** while trying to keep the current track. **Disabled** for sources that don’t support refresh (e.g. non‑YouTube HTTP stream URLs) and **disabled for compound playlists** (when multiple sources have been appended and the app cannot reliably refresh them all).                           |
| **Source** text box    | Read-only display of the current source (URL, path, or `Search: …`).                                                                                                                                                                                                                                                        |
| **Youtube...**         | Opens the modeless **YouTube** window with tabs for **Search videos**, **Search playlists**, **Import playlist**, and **My playlists (best-effort)**. Import supports **Replace** or **Append** (and optional “Remove duplicates”). The app remembers your last Replace/Append choice.                                         |
| **Load URL**           | Opens a dialog. Pasting a **YouTube** playlist or video URL loads via yt-dlp. A **direct HTTP(S) stream** URL (e.g. Icecast) is turned into a **single-item** playlist.                                                                                                                                                     |
| **Load playlist…**     | Loads a playlist from file (**JSON**, **M3U**, **M3U8**). For M3U and folder-origin sources, optional metadata behavior follows Options (see **Options → Playlist**).                                                                                                                                                       |
| **Local files…**       | Opens the local file picker modal to import from a **folder** and/or specific **files**. Supports **Replace** or **Append** and optional best-effort **Remove duplicates** (defaults are remembered).                                                                                                                        |
| **Save playlist…**     | Saves the current playlist to **JSON** (app’s internal format) or **M3U/M3U8** (based on your Save dialog choice). M3U export options are under **Options → Playlist**.                                                                                                                                                    |
| **Queue list**         | Shows tracks in **playlist order**. **Double-click** a row to jump playback to that track.                                                                                                                                                                                                                                  |
| **Right-click → Open** | For **local files**, opens **File Explorer** with the file selected. For **URLs**, opens the link in the **default browser**.                                                                                                                                                                                               |


### Long-running loads (overlay)

When loading or refreshing takes time, a **themed overlay** (same **Surface** / **Foreground** palette as the rest of the app) appears with **Cancel** (stops the operation and **restores the playlist** as it was before that action, unless **Options → Playlist → Keep incomplete playlist on cancel** is enabled — see below).

A **progress bar** under the overlay message reflects **metadata** loads (per-file ffprobe completion) and **YouTube search** (staged search batches). **YouTube playlist refresh** shows an **indeterminate** bar while yt-dlp runs.

**Taking too long? [Skip metadata]** appears when **Read metadata on load** is on and you load/refresh a **folder** or **M3U** source: it cancels the slow ffprobe pass and **reloads the same path** with metadata reading turned off for that operation only (you keep the new playlist; this does not change your Options toggle). The “no metadata” reload runs in the background so the UI stays responsive.

---

## Options window

Tabs (left to right): **Tools**, **Playlist**, **System**, **Audio**, **Theme**, **Lyrics**, **Log**. Changes in most tabs are held as a **draft** until you click **Apply**. **Cancel** closes without applying the draft.

### Tools

- **yt-dlp**, **ffmpeg**, and optional **Node.js** — each row shows the **effective** binary (resolved from your saved path or from `PATH`), a **source** line (**explicit path** vs **PATH**), **Browse…** to set an override, and **Use PATH** to clear the saved path and rely on auto-detection. A notice at the top lists anything that was resolved from `PATH`.
- **Node.js** is optional: basic YouTube playback only needs yt-dlp and ffmpeg. Node unlocks additional YouTube options on this same tab (**EJS solvers** and **cookies from browser**).

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
- **Window** behavior (aux “always on top”, compact behavior, compact layout).
- **App icon** visibility (taskbar / notification area / both).

### Audio

- **Output device** — pick **Default** or a specific **WaveOut** device. Changing device and clicking **Apply** hot-swaps output while playing when the selection actually changes (same device does nothing).
- **YouTube stream quality** — **Auto** (best available), **High** (prefers WebM/Opus-style streams), **Medium** / **Low** (bitrate caps for slower links). Passed to yt-dlp as the format selector; takes effect on the **next** track (not mid-track).

### Theme

Background image mode (**None** / **Default (Lylly)** / **Default (Meow Cat)** / **Custom**), optional **custom image** path, **background colors** (default / derived from image / custom + color picker), **opacity**, **scrim**, **window title** mode (default / custom), **window border** thickness, **UI scale** (50%–200%). **Save theme…** exports theme-related settings. **Load theme…** imports them and applies immediately if the file is valid.

With **Background: None**, **From image** under background colors is disabled (no wallpaper to sample), and **Background scrim** is disabled. If your saved theme had **From image** with **None**, opening Options coerces colors to **Default**.

If you pick the wrong file (e.g. a playlist JSON), **Load theme…** warns that the JSON does not look like a theme file (or contains no theme keys) instead of failing silently.

### Lyrics

- **Display lyrics** enables lyric resolving and highlighting (Lyrics window + optional title bar line).
- **Try to get lyrics for local files** enables LRCLIB lookups for local tracks too (best-effort).

Lyrics come from either yt-dlp (when available on the YouTube item) or LRCLIB (one query per track, cached on disk).

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
2. **Refresh** after changing files on disk for **folder** or **M3U** playlists.
3. **Repeat: Single** is useful for practicing one track; **Repeat: All** loops the whole queue.
4. If **global media keys** conflict with another app, turn the option off under **Options → System**.
5. **Playlist items** can show **[PREMIUM]** when YouTube reports Music Premium–only content; those rows are greyed like unavailable items.

---

## Out of scope / limitations (current code)

- Targets **.NET 8**, **Windows 10+**, WPF.  
- **YouTube “Search”** playlists cannot use the same **Refresh** behavior as a normal playlist URL.

If you find a mismatch between this guide and the app, update **this file** or the code so they stay aligned.