# Wavee marketing: conclusion and plan (2026-08-30)

Research inputs: the pre-rewrite README (hero + dark/light installer buttons, `git show d75439e9:README.md`),
Files (`files-community/Files`: hero image → badge row → Store badge + classic-installer badge → screenshots),
Spotube (install table with platform badges, emoji feature list, community/donate links, credits), shields.io's
release/download badges, GitHub's social-preview guidance (1280×640, 1.91:1, ≤1 MB, keep text in the centre
1000×500), Microsoft's Store badge guidelines, and launch write-ups for Show HN / Product Hunt / Reddit.

## Conclusion

Wavee has a genuinely differentiated story — a native, NativeAOT, GPU-rendered Spotify client with a measured
performance edge over WinUI 3 — and none of it is visible from the repo front page. The README that shipped with the
split was a build guide. The 0.2.1 README fixes the first three seconds (hero, badges, two installer buttons); the
rest of the funnel does not exist yet: no social preview, no landing page, no screenshots of the current build, no
demo video, no place where a non-developer would discover the app. Marketing for a project like this is not ads —
it is **making the thing legible**: one image that explains it, one page that lets people install it, one story
(the engine + the benchmark) that people who write about Windows apps will repeat.

## Plan

### 1. README (done in 0.2.1, iterate)
- Hero = a real capture of the current build (`ops/release/wavee/0.2.0/media/redesigned.jpg` for now; replace with
  a composed hero — see 3).
- Badge row: release (filtered to `wavee-v*`), total downloads, .NET 10, FluentGpu, MIT. Keep it to five.
- Dark/light installer buttons (x64, ARM64) → the `wavee-stable` `.appinstaller` URLs. These are the restored
  0.1-era assets (`assets/readme/DownloadInstaller-*.png`, 608×168, 64 px tall in the page).
- Add next: a 2×2 screenshot table under "A look at Wavee" (home, artist page with lyrics, playlist facts, library),
  a short feature list (10 lines, no emoji wall), and "Why FluentGpu" with one benchmark image + link.

### 2. Social preview (30 min)
- 1280×640 PNG, ≤1 MB: app icon + "Wavee — a native Spotify client for Windows 11" + one cropped screenshot,
  text inside the centre 1000×500. Upload at Settings › Social preview. This is what every shared link shows.

### 3. Screenshot + hero pipeline (already exists, extend)
- `ops/release/tools/Capture-WaveeWindow.ps1` + `New-ReleaseImage.ps1` capture the real app at native DPI and frame
  it (single, detail crop, two-up). Produce the README set from them so screenshots never go stale, and add a
  "hero" variant (icon + tagline + framed window, the old `ReadmeHero.png` composition) as a script, not a PSD.
- Keep every still ≤150 KB (the release-notes budget) so the same files serve README, What's new and the site.

### 4. Landing page (GitHub Pages, one weekend)
- `christosk92.github.io/WaveeMusic` (or `wavee.app` later): hero, the two install buttons, four screenshots,
  "Why it's fast" (benchmark), FAQ (Premium required, not affiliated, privacy), changelog link. Static HTML from the
  same assets; the appinstaller links are the only CTA. Files' site is the model.

### 5. Demo media
- A 20–30 s screen recording (navigation speed, lyrics, video, queue) exported as MP4 ≤600 KB + poster for
  What's new, and a GIF-free (MP4/WebM) embed on the landing page. Motion is what sells "GPU-rendered".

### 6. Distribution channels, in order
1. **Microsoft Store** — the biggest lever for a Windows app: discoverability, trust, and a "Get it from Microsoft"
   badge in the README next to the installer buttons. The MSIX is already signed; the listing needs the screenshots
   from 3 and a 300×300 tile. Decide whether the Store build is the same package identity (`cproducts.Wavee`) or a
   Store-signed one.
2. **winget** manifest (`christosk92.Wavee`) pointing at the release MSIX — developers install everything this way.
3. **Show HN** with the engine story ("a from-scratch GPU UI engine for .NET, and the Spotify client built on it"),
   posted early in the week, benchmark linked, author available in the thread.
4. **Reddit**: r/Windows11, r/opensource, r/software, r/dotnet (engine), r/spotify (carefully — it is a third-party
   client; lead with "for your own Premium account"). One post per sub, screenshots + a 30 s clip, first comment
   introduces yourself and answers "why not the official app".
5. **AlternativeTo** listing (Spotify alternatives / clients) and the Windows app roundup sites that syndicate
   from it (Windows Central, Beebom, XDA "best open-source apps" lists).
6. **Product Hunt** only once the Store listing and landing page exist — it rewards a finished funnel.

### 7. Trust signals to keep visible
- The privacy statement, the "not affiliated" line and "Premium required" in every listing.
- Signed MSIX + symbols zips per release, the CHANGELOG, the What's new page — the app already does the "we ship
  carefully" part; say so in one line on the README and the site.

### Order of work
README polish (1) → social preview (2) → screenshot pipeline (3) → landing page (4) → Store + winget (6.1–6.2) →
demo clip (5) → HN/Reddit/AlternativeTo (6.3–6.5) → Product Hunt (6.6).
