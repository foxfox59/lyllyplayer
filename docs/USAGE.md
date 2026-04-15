# LyllyPlayer — usage guide

This document describes the **current** behavior of the Windows desktop app (main window, playlist window, options, and persistence). Use it as a checklist while you review the product; adjust wording or sections if you change the UI later.

---

## Requirements (external tools)

LyllyPlayer does **not** ship **ffmpeg** or **yt-dlp**. Install them separately, then either:

- Put them on your system `PATH`, or  
- Set explicit paths under **Options → Tools** (browse buttons for **yt-dlp** and **ffmpeg**).

**YouTube** features (load playlist by URL/ID, search, refresh) need **yt-dlp**. **ffmpeg** is used for media playback, optional **metadata when loading** local folders/M3U (via ffprobe), and related tooling.

**Node.js** (when shown under **Options → Tools**) is **not** required for basic use: normal playback, local files, and typical YouTube playlists only need **yt-dlp** and **ffmpeg**. Node is for **Advanced** YouTube options (e.g. **cookies from your browser** so playback can follow your signed-in session). You can leave Node unset unless you need that.

---

## Main window (player)


| Control / area                 | What it does                                                                                                                                                                                                                                                                                                                                                                                   |
| ------------------------------ | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **options…**                   | Opens **Options** (see below).                                                                                                                                                                                                                                                                                                                                                                 |
| **playlist…**                  | Opens the **Playlist** window if it is closed. If it is already open, closes it (window position is saved).                                                                                                                                                                                                                                                                                    |
| **Playlist title** line        | Shows the current playlist name or source summary.                                                                                                                                                                                                                                                                                                                                             |
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
| **↻ (Refresh)**        | **YouTube:** re-fetches the playlist via yt-dlp while trying to keep the current track. **Local folder / M3U:** rescans the folder or re-reads the M3U (respects **Read metadata on load** from Options). **Not** used for the “Search YouTube Music” source. **Disabled** for plain HTTP stream URLs that are not YouTube. |
| **Source** text box    | Read-only display of the current source (URL, path, or `Search: …`).                                                                                                                                                                                                                                                        |
| **Search Youtube**     | Opens a dialog: **query**, **minimum length** (Any / 30 sec / 1 min / 2 min), **result count** (default from Options → Search). Builds a **search** playlist (not the same as loading a channel URL).                                                                                                                       |
| **Load URL**           | Opens a dialog. Pasting a **YouTube** playlist or video URL loads via yt-dlp. A **direct HTTP(S) stream** URL (e.g. Icecast) is turned into a **single-item** playlist.                                                                                                                                                     |
| **Load M3U**           | Pick an `.m3u` / `.m3u8` file. With **Read metadata on load** enabled, tags/duration are read with ffmpeg/ffprobe (slower).                                                                                                                                                                                                 |
| **Load folder**        | Pick a folder of audio files. Supported extensions include: **.mp3, .wav, .flac, .m4a, .aac, .ogg, .opus, .wma, .aiff, .aif, .aifc**. Optional subfolders and metadata follow Options (see below).                                                                                                                          |
| **Save playlist…**     | Saves the **current queue order as shown** to a **JSON** file (app’s own format), including source type/name metadata.                                                                                                                                                                                                      |
| **Load saved…**        | Loads a previously saved **JSON** playlist file.                                                                                                                                                                                                                                                                            |
| **Queue list**         | Shows tracks in **playlist order**. **Double-click** a row to jump playback to that track.                                                                                                                                                                                                                                  |
| **Right-click → Open** | For **local files**, opens **File Explorer** with the file selected. For **URLs**, opens the link in the **default browser**.                                                                                                                                                                                               |


### Long-running loads (overlay)

When loading or refreshing takes time, a **themed overlay** (same **Surface** / **Foreground** palette as the rest of the app) appears with **Cancel** (stops the operation and **restores the playlist** as it was before that action, unless **Options → Advanced → Keep incomplete playlist on cancel** is enabled — see below).

A **progress bar** under the overlay message reflects **metadata** loads (per-file ffprobe completion) and **YouTube search** (staged search batches). **YouTube playlist refresh** shows an **indeterminate** bar while yt-dlp runs.

**Taking too long? [Skip metadata]** appears when **Read metadata on load** is on and you **Load folder**, **Load M3U**, or **Refresh** a **folder** or **M3U** source: it cancels the slow ffprobe pass and **reloads the same path** with metadata reading turned off for that operation only (you keep the new playlist; this does not change your Options toggle). The “no metadata” reload runs in the background so the UI stays responsive.

---

## Options window

Tabs (left to right): **Tools**, **System**, **Audio**, **Theme**, **Search**, **Local**, **Advanced**. Changes in most tabs are held as a **draft** until you click **Apply**. **Cancel** closes without applying the draft.

### Tools

- **yt-dlp**, **ffmpeg**, and optional **Node.js** — each row shows the **effective** binary (resolved from your saved path or from `PATH`), a **source** line (**explicit path** vs **PATH**), **Browse…** to set an override, and **Use PATH** to clear the saved path and rely on auto-detection. A notice at the top lists anything that was resolved from `PATH`.
- **Node.js** is optional: basic YouTube playback only needs yt-dlp and ffmpeg. Node unlocks **Options → Advanced** (EJS solvers and **cookies from browser**).

### System

- **Cache MB** — upper bound used for **YouTube / yt-dlp** cache behavior (see app settings model).  
- **Playlist auto-refresh** — **Disabled**, or **1 / 5 / 30 minutes** for sources that support refresh (not local-only “search” playlists).  
- **Enable global media keys** — see above.

### Audio

- **Output device** — pick **Default** or a specific **WaveOut** device. Changing device and clicking **Apply** hot-swaps output while playing when the selection actually changes (same device does nothing).
- **YouTube stream quality** — **Auto** (best available), **High** (prefers WebM/Opus-style streams), **Medium** / **Low** (bitrate caps for slower links). Passed to yt-dlp as the format selector; takes effect on the **next** track (not mid-track).

### Theme

Background image mode (**None** / **Default** / **Custom**), optional **custom image** path, **background colors** (default / derived from image / custom + color picker), **opacity**, **scrim**, **window title** mode (default / custom), **window border** thickness, **UI scale** (50%–200%). **Save theme…** exports theme-related settings. **Load theme…** imports them and applies immediately if the file is valid.

With **Background: None**, **From image** under background colors is disabled (no wallpaper to sample), and **Background scrim** is disabled. If your saved theme had **From image** with **None**, opening Options coerces colors to **Default**.

If you pick the wrong file (e.g. a playlist JSON), **Load theme…** warns that the JSON does not look like a theme file (or contains no theme keys) instead of failing silently.

### Search

Default **result count** for the Search Youtube dialog and default **minimum length** (Any / 30 sec / 1 min / 2 min).

**Search is metadata-only:** results come from yt-dlp’s **flat** YouTube Music search (titles, ids, durations when the extractor provides them). The app does **not** run a full per-video extraction at search time, so a hit can still **fail at play** (format/CDN changes, signature/EJS issues, region, age, very long uploads, etc.). That is normal for any client that lists search hits quickly.

### Local

- **Include sub-folders by default** — used when **loading a folder** (and for refresh of a folder source).  
- **Read metadata on load (slow!)** — when enabled, **folder** and **M3U** loads (and local refresh) call ffprobe for tags/duration; large libraries can take a long time (use **Skip metadata** on the load overlay if needed).

### Advanced

- **YouTube EJS solvers** — **Prefer from GitHub** (default; same as older app behavior) or **bundled only** (no `--remote-components ejs:github`). This tab is **disabled until Node.js** is found (explicit path or on `PATH`).
- **Use cookies from browser for YouTube** — when enabled, passes your text as the value for yt-dlp’s `--cookies-from-browser` (see `yt-dlp --help` for syntax). Requires a non-empty value when enabled.
- **Keep incomplete playlist on cancel** — when **on**, cancelling a **YouTube search** (Cancel) or a **playlist refresh** from the playlist window **keeps** whatever results were already shown (search batches already applied, or unchanged playlist after a refresh that did not finish). When **off**, Cancel **restores** the playlist from before that operation (default). **Taking too long? [Stop search]** always keeps partial results when the search had produced at least one batch.
- **Logs…** — opens the **Log** window (in-memory ring + file tail of `app.log`).
- **App icon (taskbar / notification area)** — controls where the app is visible:
  - **Taskbar only**
  - **Taskbar and notification area**
  - **Notification area only**

When the notification area icon is enabled, you can **right-click** it for a menu with:
- **Open**
- Transport controls (**Previous**, **Play/Resume/Pause**, **Next**, **Stop**)
- **Exit**

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