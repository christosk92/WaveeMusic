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

### 6. Distribution channels, in order — and why the Microsoft Store is NOT one of them

**Store verdict (2026-08-30): do not list the streaming client.** No Store policy bans unofficial clients as such,
but three things line up against it:
- **Policy 11.2** ("content ... must be originally created, appropriately licensed, used as permitted by the rights
  holder, or otherwise permitted by law") plus the one-click IP infringement form is exactly how Spotify had
  *Spotimo* — a client on the official APIs — pulled: Spotify filed a claim citing its trademarks/logos and
  Microsoft removed the app without adjudication. Wavee reimplements Spotify's protocols and derives its content
  keys; it is far more exposed than Spotimo was.
- **Spotify's terms**: the Terms of Use forbid reverse engineering and "circumventing any technology used by
  Spotify"; the Developer Terms (IV.2.a.ii) forbid decompiling/reverse-engineering the platform and license playback
  only through the official platform. Spotify's 2025 GitHub DMCA notices asserted copyright, derivative-work AND
  anti-circumvention ("encryption and ... transfer keys") claims and took down whole fork networks (520 repos).
- **Certification itself** (10.3.1) requires handing Microsoft a working Premium account and describing how the
  product works — documenting the derivation to a party that must act on infringement complaints. A Store listing
  also raises the profile of the private PlayPlay repo and the fork network behind it.
The librespot precedent ("probably forbidden by them, use at your own risk") shows a protocol reimplementation can
live on GitHub for years; the Store is a different venue with a formal removal channel. Positioning follows from
this: Wavee is a **third-party client for your own Premium account**, never a "Spotify alternative"; no Spotify marks
in the icon, name, screenshots or tiles beyond nominative "for Spotify"; the not-affiliated line everywhere.

Channels, in order:
1. **GitHub + the `.appinstaller` feed** stay the primary install path (signed MSIX, silent updates) — the buttons
   on the README and the landing page.
2. **winget** manifest (`christosk92.Wavee`) — also a Microsoft-run catalog with an infringement process, but
   community-reviewed and far lighter than Store certification; acceptable risk, revisit if a complaint ever lands.
3. **Show HN** with the engine story ("a from-scratch GPU UI engine for .NET, and the client built on it"), posted
   early in the week, benchmark linked, author in the thread.
4. **Reddit**: r/Windows11, r/opensource, r/software, r/dotnet (engine); r/spotify carefully — lead with "for your
   own Premium account". One post per sub, screenshots + a 30 s clip, first comment introduces yourself.
5. **AlternativeTo** ("Spotify clients", not "alternatives") and the Windows app roundups that syndicate from it.
6. **Product Hunt** only once the landing page and demo exist.

A Store-safe *companion* (official Web API + Spotify Connect control only, own icon/name) is the only shape that
could ever be listed — and Spotimo shows even that draws a trademark claim if the branding leans on Spotify.

### 7. Trust signals to keep visible
- The privacy statement, the "not affiliated" line and "Premium required" in every listing.
- Signed MSIX + symbols zips per release, the CHANGELOG, the What's new page — the app already does the "we ship
  carefully" part; say so in one line on the README and the site.

### Order of work
README polish (1) → social preview (2) → screenshot pipeline (3) → landing page (4) → winget (6.2) →
demo clip (5) → HN/Reddit/AlternativeTo (6.3–6.5) → Product Hunt (6.6). No Microsoft Store listing (see 6).
