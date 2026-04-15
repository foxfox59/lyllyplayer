# LyllyPlayer — release / portable build

## Supported (this repo, .NET 8 + WPF)

- Windows 10 and Windows 11, self-contained publish for `win-x64`, `win-x86`, and optionally `win-arm64`.

### Portable scripts vs single-file `publish-win10-selfcontained.cmd`

They are **not** the same; the batch file is **not** obsolete if you want a **single-file** x64 build.


|        | `scripts\publish-portable.ps1` / `publish-artifacts.ps1` | `publish-win10-selfcontained.cmd` + `Win10-SelfContained.pubxml`          |
| ------ | -------------------------------------------------------- | ------------------------------------------------------------------------- |
| Layout | Many files in a folder (then ZIP)                        | **One** `LyllyPlayer.exe` (compressed single-file self-extract)           |
| RIDs   | `win-x64`, `win-x86`, `win-arm64`                        | **win-x64** only                                                          |
| Output | `artifacts\publish\<rid>\` (default)                     | `LyllyPlayer\LyllyPlayer.App\bin\Release\net8.0-windows\win-x64\publish\` |


Use the **PowerShell** flow for standard portable ZIPs and multiple architectures; use `**publish-win10-selfcontained.cmd`** (or `dotnet publish ... -p:PublishProfile=Win10-SelfContained`) when you specifically want the **single-file** x64 layout.

## Release artifacts (ZIPs for multiple RIDs)

From the repository root (SDK required only on the machine that builds):

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\publish-artifacts.ps1
```

This runs a self-contained publish and ZIP for `**win-x64**` and `**win-x86**`. Optional ARM64:

```powershell
powershell -File .\scripts\publish-artifacts.ps1 -IncludeArm64
```

**Output** (folder `artifacts\` is gitignored)

- `artifacts\publish\win-x64\` and `artifacts\LyllyPlayer-portable-win-x64.zip`
- `artifacts\publish\win-x86\` and `artifacts\LyllyPlayer-portable-win-x86.zip`
- With `-IncludeArm64`: same for `win-arm64`

Optional: use another folder **under the repository** (still `publish\<rid>\` + ZIPs inside that base):

```powershell
powershell -File .\scripts\publish-artifacts.ps1 -ArtifactRoot artifacts\staging
```

Same `-ArtifactRoot` works with `publish-portable.ps1`. Relative paths are resolved from the repo root; the default base is `artifacts\`.

## Single RID (manual)

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\publish-portable.ps1
powershell -File .\scripts\publish-portable.ps1 -Runtime win-x86
```

## Maintainer: regenerate `.ico` from PNGs

After editing `LyllyPlayer.App\Assets\icon-*.png`:

```powershell
powershell -File .\LyllyPlayer\tools\make-ico.ps1
```

## External tools (not bundled; user installs separately)

- [yt-dlp](https://github.com/yt-dlp/yt-dlp)
- [ffmpeg](https://ffmpeg.org/)

## Older Windows versions

.NET 8 does not support Windows 7, Vista, or older. A separate codebase targeting .NET Framework (e.g. 4.x) or another stack would be required to support those OS versions; this project does not provide that build.