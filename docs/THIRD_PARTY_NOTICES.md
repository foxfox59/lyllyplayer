# Third-party notices

This application bundles or relies on the components below. **Binary releases** (ZIP/installer) should ship this file next to the executable (or otherwise make equivalent notices available), especially when **libmp3lame** is included.

---

## LAME — `libmp3lame` (native MP3 encoder)

**Used for:** MP3 encoding in LyllyPlayer (via the managed wrapper below).

**Upstream project:** [LAME MP3 Encoder](https://lame.sourceforge.io/)

**License:** [GNU Lesser General Public License, version 2](https://www.gnu.org/licenses/old-licenses/lgpl-2.0.html) (LGPL). The LAME project describes its software as LGPL-licensed; see also the `COPYING`, `LICENSE`, and documentation files inside the official **source** tarball.

**Corresponding source code (official):**

- Project files and release tarballs: [SourceForge — LAME files](https://sourceforge.net/projects/lame/files/lame/)
- Project home and download information: [lame.sourceforge.io — Download](https://lame.sourceforge.io/download.php)

LAME is distributed in **source** form by its authors. The `libmp3lame` **Windows DLLs** that ship with this app are produced from that codebase (supplied through the **NAudio.Lame** NuGet package’s native payloads). To satisfy LGPL redistribution expectations for the shared library, retain this notice and offer users a clear way to obtain the **same or compatible** LAME source (links above).

Patents may apply to MP3 encoding/decoding in some jurisdictions; consult upstream LAME documentation.

---

## NAudio.Lame (managed wrapper for `libmp3lame`)

**Used for:** .NET interop to the LAME **native** DLLs.

**Project / package:** [NAudio.Lame on NuGet](https://www.nuget.org/packages/NAudio.Lame/)

**License:** **MIT License**

```
The MIT License (MIT)

Copyright (c) 2013-2019 Corey Murtagh

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

The **native** `libmp3lame` binaries are **not** MIT-licensed; they remain under **LAME’s LGPL** as described in the previous section.
