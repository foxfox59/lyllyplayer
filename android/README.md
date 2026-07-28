# LyllyPlayer Android (v0.1)

Lightweight Android player that opens desktop-compatible `.m3u` / `.m3u8` / `.lyllylist` playlists, plays local files, HTTP streams, and YouTube watch URLs (audio via [NewPipe Extractor](https://github.com/TeamNewPipe/NewPipeExtractor)), with LyllyPlayer-style shuffle and next-track URL prefetch.

## Requirements

- Android Studio with SDK at e.g. `D:\AndroidSDK` (set in `local.properties` as `sdk.dir=...`)
- SDK Platform **35+**, Build-Tools, Platform-Tools
- `minSdk` **23** (Android 6.0)
- JDK 17 (Studio JBR is fine)

If Gradle cannot download from `services.gradle.org` (firewall), place `gradle-8.9-bin.zip` in `android/` and temporarily set `distributionUrl=file\:///D:/CursorProjects/lyllyplayer/android/gradle-8.9-bin.zip` in `gradle/wrapper/gradle-wrapper.properties`.

## Open / build

1. Open the `android/` folder in Android Studio (or open this repo and import the Gradle project under `android/`).
2. Let Gradle sync; accept SDK licenses if prompted.
3. Run on an emulator or device.

CLI (from `android/`):

```bat
gradlew.bat assembleDebug
```

## Usage

- Menu (top-left) → **Load playlist** — pick `.m3u`, `.m3u8`, `.lyllylist`, or `.json` via the system file picker (SAF).
- Transport: previous / play-pause / next; shuffle and repeat on the right.
- Seek bar on the second row; playlist fills the rest of the screen.
- **Quit** stops playback and closes the app.

## Playlist compatibility

- `.lyllylist` / JSON matches the desktop `SavedPlaylist` shape (`Id`, `Name`, `Entries[].VideoId/Title/Channel/Url`).
- `.m3u` supports `#EXTINF` titles, `http(s)` streams, YouTube URLs, and relative local paths resolved against the playlist’s parent when SAF allows.
- **Windows absolute paths** in an M3U will not resolve on the phone; prefer relative paths + URLs when sharing playlists across desktop and Android.

## YouTube

- Watch URLs / video IDs in playlists are resolved to audio streams with NewPipe Extractor (not yt-dlp).
- While a track plays, the next YouTube item’s stream URL is prefetched (desktop warm-path idea) so handoff is less cold than resolve-on-open clients.
- No YouTube search, playlist import UI, or cookies in this version. Age-restricted / private videos may fail.

## Out of scope (for now)

Song queue, lyrics, folder import, themes, visualizer, yt-dlp fallback.
