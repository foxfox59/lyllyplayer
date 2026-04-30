# LyllyPlayer

© MuiluFox 2026  
**LyllyPlayer should always be completely free to download and use. If you are getting asked to pay for it (other than [donations](https://www.paypal.me/MuiluFox) here), someone is ripping you off.**  

**[1.1.0 in action (YouTube link)](https://www.youtube.com/watch?v=qnoZVa0sWO4)**  
**[Using the Theme Designer in 1.2.0 (YouTube link)](https://www.youtube.com/watch?v=Z1lniBzXm9Q)**  
**[Compound playlists - song queue - M3U export in 1.4.1 (YouTube link)](https://www.youtube.com/watch?v=K855yiYLzeQ)**

[CHANGELOG.md](CHANGELOG.md)

---

## Known issues

- DisplayFusion has a slight conflict/race situation with how LyllyPlayer handles "always on top" - for now, use the Compatibility settings in DF. I'm going to fix this at some point, just not right now.

---

A multi-function desktop audio player with minimal external requirements: **ffmpeg** for any audio output, and **yt-dlp** for Youtube functionality.
Supports login cookies from browser if you have **node.js**, but not required for playback in most cases.  
Supports either pasting a direct Youtube playlist ID/URL or searching from Youtube.  

Imports .m3u playlists. Generates playlists from supported local audio file folders (.mp3, .wav, .flac, .m4a, .aac, .ogg, .opus, .wma, .aiff, .aif, .aifc). Can save and load playlists for later use both in internal format and .M3U  
Tested also with simple icecast/shoutcast streams either via .m3u playlist or direct URL.  
Support for basic custom theming with a default, automatic, custom or Windows themed color tint. Can be switched between light/dark modes (although the feature is crude)  
Simple visualizer: off, VU bars, or a frequency spectrum  

**and so much more!** (not really that much, those were the main features :D)  

I am a software developer, so I have some qualifications to supervise and suggest solutions, but unless otherwise specified:  

**99.9% of the actual code is AI slop.**  
All I touch manually is the documentation (recently I had to start doing some fixes manually). This is partially an experiment in how far can I push things before the codebase gets too complicated for the AI to track. Also a test of patience - many things would have been faster fixed by hand but I deliberately chose not to.  

**Exit now or accept that.**

## Some screenshots of the app in action (outdated)

**[docs/SCREENSHOTS.md](docs/SCREENSHOTS.md)**

## Quick preface before the machine overlords take over

Hi!

I was frustrated with the lack of a simple player that works with Youtube playlists out of the box, so I vibe coded this.
Before long, the functionality grew to encompass local file playing, streams and .m3u playlists.

It's basically for my own usage, but as it's a really useful little player, I thought why not share it.

In case you find it useful and want to give a little something, feel free to, but
**do not feel obligated to pay anything, this software is supposed to be free and open source.**

**[Donate with PayPal](https://www.paypal.me/MuiluFox)**

### KISS

- You really do need **ffmpeg** (and **yt-dlp** for any Youtube stuff). Get those yourself, I can't bundle them reliably and keep them up to date.
  - node.js for Youtube cookies/login. Also use this at your own risk - don't want you to get banned (should not be, but you never know).
- Any updates and additional features will be **best effort and not guaranteed**, I am working on this on my free time and personal budget.
- Bugs are definitely there. I am only one man against a thousand enemies.

### Q&A

- Why?
  - Got fed up with trying to find an easy to use Youtube playlist app that doesn't require finding obscure plugins, fiddling with Youtube tokens and sacrificing 16 specifically colored chickens to Santa on a partially cloudy January night at exactly 22:42. Now you open LyllyPlayer, paste the playlist url and hit play. Boom.
- Does this work with Youtube Music Premium?
  - I have no idea. Try at your own risk. No responsibility is taken as I have zero idea how Youtube takes to third party agents using their API without a developer token.

## TODO (planned, no timeline, best effort)

Ideas for later — **not** commitments and nothing to depend on for now:

- Optimize memory usage (or change whole implementation away from WPF)
- Improve window snapping (currently occasionally feels jittery)
- Improve lyrics quality / matching (best-effort)
- Linux builds — desktop player on Linux (would require significant changes)
- Android release — mobile variant (requested feature by testers, would require very significant changes)
- I18N - currently only in English, add support for multiple languages and make sure themes don't break
- External visualization support (at least MilkDrop)
- Custom layouts, even wild shapes like old Media Player
  - Custom border width was a thing, but it's too much of a headache to fix with AI so it's disabled for now (and it's not really a very useful option anyway)

I'll let the AI do its thing now. Hope you enjoy this little player. If you feel like it and/or find any bugs, especially ones that crash or make you have a bad time, please feel free to report them, I'll see what I can do.

---

Desktop media player (currently) for **Windows 10 and later** (64-bit), built with **.NET 8** and **WPF**. It plays **YouTube playlists and searches** (via [yt-dlp](https://github.com/yt-dlp/yt-dlp)), **local folders** and **M3U/M3U8** playlists, optional **metadata** via **ffmpeg**, and saves/restores playlist state between sessions.

External tools (**ffmpeg**, **yt-dlp**) are not bundled; install them separately and configure paths in the app or ensure they are on `PATH`.

**End-user behavior** (windows, buttons, options, overlays, file locations) is documented in **[docs/USAGE.md](docs/USAGE.md)** — review and edit that file as the product evolves.

## Requirements


|               |                                                                             |
| ------------- | --------------------------------------------------------------------------- |
| OS            | Windows 10 / 11 (x64 for default portable build)                            |
| Build         | [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)              |
| Runtime (dev) | .NET 8 Desktop Runtime (included with SDK for `dotnet run`)                 |
| Mandatory     | ffmpeg (for any audio playback)                                             |
| Optional      | yt-dlp (for any Youtube functionality), node.js (for Youtube cookies/login) |


## License

This project is licensed under the **GNU General Public License v3.0 only** — see [LICENSE](LICENSE) in the repository root. SPDX identifier: `GPL-3.0-only`.

## Repository layout

```
├── LyllyPlayer/              # Visual Studio solution and WPF application
│   ├── LyllyPlayer.sln
│   └── LyllyPlayer.App/      # Main project (entry assembly LyllyPlayer.exe)
├── scripts/                  # Packaging (portable ZIP)
│   ├── publish-portable.ps1
│   └── publish-artifacts.ps1
├── docs/
│   ├── USAGE.md              # Feature / UI guide (for review)
│   ├── GITHUB.md             # Branch vs Releases, tags, Actions
│   └── RELEASING.md          # Portable / self-contained builds
├── nuget.config              # NuGet feed configuration
├── publish-win10-selfcontained.cmd
└── README.md
```

## Build from source

From the repository root (PowerShell):

```powershell
dotnet build .\LyllyPlayer\LyllyPlayer.sln -c Release
```

Run the app (framework-dependent build):

```powershell
dotnet run --project .\LyllyPlayer\LyllyPlayer.App\LyllyPlayer.App.csproj -c Release
```

Or start the built executable under `LyllyPlayer\LyllyPlayer.App\bin\Release\net8.0-windows\LyllyPlayer.exe`. **Debug** builds use a separate output folder: `LyllyPlayer\LyllyPlayer.App\bin\DebugDev\net8.0-windows\` (avoids file locks when switching configurations).

## Portable / self-contained release

To produce a **self-contained** folder and ZIP (no .NET install on the target PC), use the script from the **repository root**:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\publish-portable.ps1
```

Optional runtime (default is `win-x64`):

```powershell
powershell -File .\scripts\publish-portable.ps1 -Runtime win-x86
powershell -File .\scripts\publish-portable.ps1 -Runtime win-arm64
```

Alternatively, from the repo root:

```cmd
publish-win10-selfcontained.cmd
```

Details, supported platforms, and what is bundled vs external are documented in **[docs/RELEASING.md](docs/RELEASING.md)**.

## GitHub: code on the branch, binaries on Releases

Keep the **default branch** (`main` or `master`) as **source only** — no committed `bin/` or ZIPs (`.gitignore` already excludes build output).

Put **downloadable builds** on **GitHub Releases** (one release per version, attach one or more ZIPs for different CPUs). That is separate from the branch file tree.

Step-by-step (manual releases + optional automation) is in **[docs/GITHUB.md](docs/GITHUB.md)**. Pushing a tag like `v1.0.0` runs **Actions → Release portable builds** and publishes three portable ZIPs (`win-x64`, `win-x86`, `win-arm64`) to that release.
