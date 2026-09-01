# Authoring a Wavee release folder

One folder per released version: `ops/release/wavee/<semver>/`. It holds the part of a release that
`CHANGELOG.md` cannot carry — the codename's tagline, the hero highlights, deep links, and the notices — plus any
media those highlights show.

```
ops/release/wavee/0.2.0/
  whatsnew.json      <- hand-authored (this file describes it)
  media/             <- optional; only if a highlight shows something
    docked-video.webp
    docked-video.mp4
```

`ops/release/wavee-release.ps1` runs `Wavee.ReleaseTool validate` over this folder (phase 2) and emits the
artefacts the release actually publishes. **Nothing here is published verbatim**: the tool merges the authored
document with the dated `CHANGELOG.md` entry and stamps the rest.

## What you write vs. what the tool writes

| Field | Who |
|---|---|
| `version`, `name`, `tagline`, `lang`, `minOs`, `arch`, `highlights`, `notices` | **you** |
| `packageVersion`, `date`, `channel` | the tool (from `--quad` / the CHANGELOG date / `--channel`) |
| `sections` | the tool — parsed from the `## [<semver>]` entry in `CHANGELOG.md`. **Leave it `[]`.** |
| `links`, `generatedAt`, `media` (hashes), issue/PR `title`/`state`/`stateReason`/`merged` | the tool |
| a section item's `commits`, the document's `unlinkedCommits`, an index entry's `issues` | the tool — derived from `--commits` (`<staging>\commits.json`, written by the release script from `git log <prevTag>..HEAD`) and the closing-keyword / squash-suffix parse. Nothing here to author |

So the changelog stays the single human source of the itemised list, and `whatsnew.json` is only the editorial
layer on top of it. Writing bullets into `sections` by hand is silently overwritten.

## Rules the validator enforces

- **The CHANGELOG entry must exist and be dated.** `## [0.2.0] - unreleased` is fine while you work — the release
  script replaces `unreleased` with today's date before it calls the tool. A hand run against an undated entry
  fails with exit code 2.
- **`version` must equal `--semver` and `name` must equal `--codename`** (both come from `Wavee.Version.props`).
- **Deep links must be in-app routes** — `wavee://open?route=<route>`. The validator checks the *shape* only: the
  `wavee://open?route=` prefix must be there and a route must follow it. It does **not** know which routes exist, so
  a typo (`setttings`) passes validation and then lands the reader on the not-found page. Check the route by hand
  against the list below, or by opening the link on an installed build.

  Routes the shell renders today (`ShellRoutes`; a route not on this list is not one):

  | Exact | `home` `browse` `search` `albums` `artists` `liked` `podcasts` `local` `history` `recents` `settings` `api-console` `playback-diagnostics` `whatsnew` `sidebar-customize` `home-customize` |
  |---|---|
  | Prefixed (`<prefix><entity uri>`, never a bare prefix) | `album:` `pl:` `artist:` `show:` `prerelease:` `disco:` `module:` `browse:` `home-section:` `browse-section:` plus the concert family |

  A release note should almost always deep-link to `whatsnew`, `settings` or a stable top-level page — an entity
  URI baked into a release document goes stale the moment that playlist or show does.
- **Media budgets:**
  - no GIF, ever; allowed extensions are `.webp`, `.png`, `.jpg`, `.jpeg`, `.mp4`
  - ≤ 150 KB per still (`.webp` / `.png` / `.jpg`)
  - ≤ 600 KB per motion file (`.mp4`, or a `.webp` whose highlight declares `"kind": "video"`)
  - ≤ 1.5 MB for every media file of the release added together
  - no two different files sharing a basename — release assets are flat, so `a/hero.webp` and `b/hero.webp` collide
  - a `video` highlight should carry a `poster` still; the What's new page shows the poster until it plays
- **Every `#n` / `!n` referenced by the changelog must exist** in the repo. The tool fetches each one and snapshots
  `title` / `state` / `state_reason` / whether it is a PR into the emitted document, so the page can show the state
  the release shipped with even offline. A 404 is an error (the changelog names something that does not exist).
- **Every reference must actually be resolved.** If a lookup is throttled or fails, the authored `title`/`state`
  would ship under a `generatedAt` stamp claiming it was verified — a blank title, or an issue shown "open" that
  closed weeks ago. That is exit 2: `N issue(s) unresolved`. Fix it with a token (`--github-token`, or `GITHUB_TOKEN`
  in the environment, which is how the release script passes it); `--allow-unresolved` is the deliberate escape
  hatch for "GitHub is down and this release has to go out anyway".
- **Cross-checked against `--commits` when `--previous-tag` is given.** Every issue the CHANGELOG cites with `(#n)`
  must be closed by a commit in `<prevTag>..HEAD` (a `Fixes #n` / `Closes #n` / … trailer, or the same `#n` on the
  squash suffix), and every commit that closes an issue must be cited by the CHANGELOG. A mismatch is exit 2, one
  line per problem; `--allow-unlinked` ships anyway with the mismatches as warnings. This is the same rule the
  release script's `issue refs` preflight gate checks before the version bump — see
  `.claude/skills/github-triage/SKILL.md` for the contract every fix has to leave behind, and §4 of
  `docs/guide/releasing-wavee.md` for the gate. `commits`, `unlinkedCommits` and an index entry's `issues` (see the
  table above) are how the result reaches the emitted document — none of that is authored here.

## Media

Keep it small and keep it still where you can. A `.webp` still that the page can show instantly beats an `.mp4`
that has to buffer, and the whole folder ships **inside the MSIX** as well as as release assets — every byte here
is a byte in every install.

```jsonc
"media": {
  "kind": "video",                 // image | video
  "src": "media/docked-video.mp4",
  "poster": "media/docked-video.webp",
  "alt": "Video docked beside the queue",
  "width": 1200, "height": 675,
  "bytes": 512000
}
```

Omit `media` entirely when a highlight has nothing to show — that is the normal case, and it is what `0.2.0` does.

## Making the images

The highlight posters are made on the dev box, from the real app, with `ops/release/tools/`. Three steps, no
design tool, no network:

```powershell
# 1. capture - Wavee must be installed, running and signed in. Sizes the window, deep-links it, and
#    grabs the shadow-free DWM rectangle at native DPI.
ops\release\tools\Capture-WaveeWindow.ps1 -Route home -Out artifacts\media-src\home.png

# 2. frame + encode - headless Edge renders ops\release\tools\frame.html at 2x, ffmpeg downscales once
#    (lanczos) and steps the JPEG quality until the file fits the 150 KB budget.
ops\release\tools\New-ReleaseImage.ps1 -Shot artifacts\media-src\home.png `
    -Out ops\release\wavee\0.2.0\media\redesigned.jpg

# 3. paste the media block into whatsnew.json (below), then validate as usual.
```

Two more framings for when a full window says nothing:

```powershell
# a zoomed crop of one detail (760x400 viewport; -Cx/-Cy are the crop origin in SOURCE pixels)
ops\release\tools\New-ReleaseImage.ps1 -Shot artifacts\media-src\settings.png -Variant detail `
    -Zoom 1.4 -Cx 300 -Cy 200 -Out ops\release\wavee\0.2.0\media\update-toggle.jpg

# two shots, one behind the other (-Shot2 is required)
ops\release\tools\New-ReleaseImage.ps1 -Shot artifacts\media-src\home.png `
    -Shot2 artifacts\media-src\settings.png -Variant twoup `
    -Out ops\release\wavee\0.2.0\media\page-and-settings.jpg
```

Useful switches: `-TintA`/`-TintB` (the accent pair), `-Scale` (how much of the frame the shot fills, default
`0.88`), `-MaxBytes` (default `150000`), `-Keep2x` (keep the 2x PNG next to the output for inspection).
The tools need Microsoft Edge (ships with Windows) and `ffmpeg` on `PATH`; both fail with a clear message
if either is missing.

### The rules

- **1200x675 (16:9) JPEG, ≤ 150 KB.** That is the size the card, the What's new page and the after-update dialog
  are all laid out for, and the validator's per-still budget. The script enforces both.
- **One tint pair per release.** Pick `-TintA`/`-TintB` once (the 0.2.0 pair is the default `#3d5a8f` / `#6b3a63`)
  and use it for every image of that release, so the three cards read as one set. The next release picks a new pair.
- **No text baked into the image.** The words are the highlight's `title` and `body`; text inside a poster cannot be
  localised, cannot be read by a screen reader, and goes blurry at card size. (Store listing screenshots are the one
  place text is baked in - those are made separately.)
- **`alt` describes the feature, not the picture.** "The new Wavee home page", not "screenshot of an app window".
  It is what a screen reader announces and what shows if the image fails to decode.
- **JPEG, not WebP.** The validator accepts `.webp`, but Windows only *decodes* WebP when the Store's WebP Image
  Extension is installed - on a machine without it the card would show an empty band. `-Format webp` exists; don't
  use it for a poster.
- **Source PNGs stay out of git.** Captures live in `artifacts\media-src\` (gitignored). Only the encoded
  ≤150 KB JPEG belongs in `ops\release\wavee\<semver>\media\`, and it ships inside the MSIX - every byte counts.
- **`src` must be `media/<basename>`.** Release assets are flattened to basenames, so that is the only path shape
  the app can resolve.

### The block to paste

```jsonc
"media": {
  "kind": "image",
  "src": "media/redesigned.jpg",
  "alt": "The new Wavee home page",
  "width": 1200, "height": 675
}
```

`bytes` and the hash are stamped by the tool - leave them out.

### Close-ups (`-Variant detail`)

`-Zoom 1` shows the captured pixels 1:1 in the final 1200x675 image (the 760x400 viewport is a 760x400 region of the
capture, positioned with `-Cx/-Cy` in capture pixels). The capture carries the DISPLAY's DPI - 150% on this machine -
so that is already a 1.5x close-up of the UI; `-Zoom` up to ~1.5 is acceptable, beyond that the image is enlarged and
goes soft. To zoom further, capture on a 200%+ display (the app renders sharper, not the tool). The tool never
upscales for the `card` variant: a small capture simply sits smaller in the frame.


## Running the validator by hand

From the repo root:

```powershell
dotnet run --project src/apps/Wavee.ReleaseTool -- validate `
  --semver 0.2.0 --quad 0.2.0.17 --codename Breaker --channel stable `
  --changelog CHANGELOG.md --notes ops/release/wavee/0.2.0 `
  --out artifacts/release/0.2.0/notes --repo christosk92/WaveeMusic `
  --previous-tag wavee-v0.1.2 --github-token (gh auth token)
```

Exit `0` ok, `2` the release is not shippable (every problem is listed), `1` usage or I/O. On failure **nothing is
written**, so a rejected run cannot half-publish.

It emits, into `--out`:

| File | What it is |
|---|---|
| `whatsnew.json` | the merged document — shipped in the MSIX (`Assets/whatsnew/`) and as a release asset |
| `whatsnew-index.json` | the ≤12 newest releases, newest first; uploaded to the **rolling feed release**, not the version tag |
| `RELEASE_BODY.md` | the GitHub release body (`gh release create --notes-file`) |
| `store-listing.txt` | tagline + highlight titles, ≤1500 characters |
| `media/*` | every referenced media file, flat, by basename |

To iterate on wording without re-running validation (no network, no writes):

```powershell
dotnet run --project src/apps/Wavee.ReleaseTool -- render --notes artifacts/release/0.2.0/notes --markdown
dotnet run --project src/apps/Wavee.ReleaseTool -- render --notes artifacts/release/0.2.0/notes --store-listing
```

## Starting the next release

1. Copy the previous folder, delete the media you are not reusing.
2. Set `version` and `name` to the next semver + codename (the sea series: Abyss 0.1, Breaker 0.2, Crest 0.3,
   Drift 0.4, Ebb, Fetch, Groundswell, Harbor, Inlet, Jetty, Kelp, Lagoon …) and match `Wavee.Version.props`.
3. Rewrite `tagline` and the highlights. Three is the shape the page and the after-update dialog are built for;
   more than three is truncated in the dialog.
4. Clear `notices` unless something really is breaking.
5. Leave `sections`, `links`, `packageVersion`, `date`, `generatedAt` and `media` (the hash list) exactly as the
   template has them — empty.
