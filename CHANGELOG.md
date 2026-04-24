## Changelog

### 1.4.0 (queue)
  - Added: song queue
  - Added: playlist export as M3U(8) with optional rich comments that allow cleaner reimport
  - Added: stop button :D
  - Fixed: internal handling of playlist is now less resource intensive
  - Fixed: yt-dlp would get stuck on certain types of errors

### 1.3.1 (playlist bugfix)

  - Fixed: a small bug with the playlist window getting stuck on the wrong display on multi-monitor setups

### 1.3.0 (playlist and search improvements)

 - Added: Ability to import YT playlists directly from your account
 - Added: YouTube functionality moved under new window (Youtube...) in Playlist
 - Added: Playlist sorting
 - Fixed: Various playlist fixes

### 1.2.1 (bugfix)

 - Fixed: Rare issue that only happened when using DisplayFusion (apps were fighting for window focus).

### 1.2.0 (UI updates)

- Added: Ultra-compact mode, theme designer

### 1.1.0 (first public release)

- Added: Notification area (system tray) context menu: **Open**, transport controls (**Previous / Play-Pause / Next / Stop**), and **Exit**.
- Improved: App now declares **Per-Monitor DPI awareness (V2)** via application manifest for more consistent scaling behavior.

### 1.0.1 (internal)

- Fixed: Options → Apply no longer shows the “Not on PATH: Node.js …” info dialog repeatedly when Node.js is **unset** and not found on `PATH`.

### 1.0.0-RELEASE (internal)

- Initial internal release
