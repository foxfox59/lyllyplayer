# GitHub: source branch vs releases

This is a mental model that stays the same for almost every project.

## Two different places


| Place                                       | What lives there                                                                                                               |
| ------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------ |
| **The default branch** (`main` or `master`) | **Source code only** — text files, projects, scripts. Cloning the repo gives you this.                                         |
| **Releases** (and **Actions artifacts**)    | **Built binaries** — ZIPs, installers, optional extra builds. These are **uploaded files**, not normal commits on your branch. |


Your repository history stays small and readable: no `bin/`, no giant ZIPs in git. The `.gitignore` file already tells Git to ignore build folders (`bin/`, `obj/`, `artifacts/`, etc.), so those never get committed by mistake.

## What you want in practice

1. **Day to day:** commit and push **only source** to your default branch (GitHub often calls it `main`; older repos may still use `master` — you can rename in **Settings → General → Default branch**).
2. **When you want users to download the app:** create a **Release** and attach one or more ZIPs (e.g. `win-x64`, `win-x86`, `win-arm64`).

Binaries attached to a Release **do not** clutter your branch; they sit on the Release page as downloads.

## Option A — Release by hand (good to learn once)

1. On your PC, run the portable script (see [RELEASING.md](RELEASING.md) / [README](../README.md)).
2. On GitHub: open your repo → **Releases** → **Draft a new release**.
3. Create a new tag (e.g. `v1.0.0`), title, short description.
4. **Attach** the ZIP files from `artifacts\` (you can attach several, e.g. one per CPU architecture).
5. Publish the release.

## Option B — Automatic builds when you push a tag

This repo includes a workflow (`.github/workflows/release-portable.yml`) that:

- Runs when you push a **version tag** whose name starts with `v` (examples: `v1.0.0`, `v0.2.1`).
- Builds **three** portable ZIPs: `win-x64`, `win-x86`, `win-arm64`.
- Creates a **GitHub Release** for that tag and attaches all three ZIPs.

**Steps:**

1. Commit your source and push to GitHub as usual.
2. Create and push a tag (from your machine, in the repo root):
  ```powershell
   git tag v1.0.0
   git push origin v1.0.0
  ```
3. Open **Actions** on GitHub and wait for **Release portable builds** to finish.
4. Open **Releases** — you should see `v1.0.0` with the ZIP files attached.

You can also run the same workflow **manually** (**Actions** → workflow → **Run workflow**) without creating a release; it will upload ZIPs as a workflow **artifact** you can download from the run summary (useful for testing).

## Summary

- **Branch** = clean **code** only (with `.gitignore` enforcing that).
- **Release** = **binaries** for users (one or many files per version), separate from the branch file tree.

That is exactly the split you described.