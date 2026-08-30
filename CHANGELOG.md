# Changelog

All notable changes to **Wavee** are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Releases are cut from the `wavee-v*` tag prefix — see `docs/guide/releasing-wavee.md`. (The FluentGpu engine/gallery
versions separately under `v*` and is not tracked in this file.)

## [0.2.0] - 2026-08-30

### Added

- **Developer mode** — an explicit Settings toggle that gates the diagnostic surfaces. Off by default, so a normal
  install no longer exposes developer-only tooling.
- Updates: **What's new** — a page that shows the release highlights, the full changelog, and the current GitHub
  state of every issue a release references. The first launch after an update opens a short summary of what changed;
  a checkbox turns that off for good.
- Updates: **Release channel and update timing** — Settings › About names the channel this build follows. Updates
  apply on the next launch; optionally Wavee installs a waiting update as it closes (“Install a waiting update when
  I quit Wavee”, off by default) so the new version is simply there next time. Metered connections are left alone
  unless you opt in.
- **In-app update check** — Settings › About checks for a newer published release and reports what it finds, alongside
  the installed version.
- **Privacy policy and third-party notices** — reachable from Settings › About. `THIRD-PARTY-NOTICES.txt` is generated
  at packaging time and ships both inside the package and as a release asset.
- **Start on login** — an opt-in setting to launch Wavee when Windows starts.
- **`wavee://` protocol and toast activation declared in the MSIX manifest** — deep links and notification clicks now
  activate the packaged app through its registered identity/AUMID instead of the unpackaged HKCU fallback.
- **Crash notice on next launch** — if the previous run died, the next launch says so and points at the crash report
  and log, rather than failing silently.

### Changed

- Updates: **Updates download in the background and apply on the next launch.** “Update now” downloads, stages and
  restarts with real progress; the app no longer hands you to App Installer and hopes.
- **Dealer archive is off by default.** The raw dealer-message archive is a debugging aid; it now writes nothing unless
  it is turned on.
- **Lossless removed from the audio-quality picker** until it actually ships. Offering an option that silently did not
  apply was worse than not offering it.
- **API console, lyrics inspector, and test notifications moved behind developer mode.** They remain fully available —
  just not on the default Settings surface.
- **Image cache moved under `%LOCALAPPDATA%\Wavee\cache`**, joining the rest of the app's per-user state in one place
  that a factory reset can clear.

### Fixed

- Updates: **no more blank "App Installer" window at every launch.** The feed's on-launch check now runs silently in
  the background; a new version is staged while you keep using Wavee and applied the next time it starts.
- App icon: **no more white corners on the taskbar.** The icon's rounded corners are now genuinely transparent
  instead of white pixels left over from the artwork's export.
- Updates: **The "What's new" plate no longer opens on top of the setup wizard.** After a sign-in or a setup rerun it
  waits until setup is done, then appears in the same session.
- Setup: **The Terms step's full agreement opens in place and stays in its column.** The summary card grows into the
  scrollable agreement with a proper Close button (and Escape), instead of a separate panel that could paint over the
  page's own heading at some window widths.
- Updates: **A background update check that cannot reach the feed no longer interrupts you.** It is still shown in
  Settings › About (with Retry); only a check you started yourself, or a failed install, raises a toast.
- Updates: **Installing an update on quit no longer trips Windows' hang detector.** Wavee kept answering Windows while
  the update installed, instead of going quiet and being closed as an unresponsive app on the way out.
- Updates: **“Check for updates” could never find a release.** The check pointed at the repository's global latest
  release — the FluentGpu gallery's — so a published Wavee build was invisible to it. It now reads Wavee's own feed.
- **Fabricated listening history on fresh installs.** A brand-new profile showed recently-played entries that had never
  been played; a fresh install now starts empty.
- **The first-run wizard could be dismissed with `Esc`,** dropping the user into an unconfigured app.
- **"Cancel" while signing in quit the app** instead of returning to the previous step.
- **Setup › Local playback showed a stray chip row** with nothing behind it.
- **Hard-coded Premium pre-launch gate.** The account-tier check no longer depends on a compiled-in assumption about
  the signed-in account.

### Removed

- **Environment-variable switches.** `WAVEE_SETUP_START_PAGE`, `WAVEE_FPS`, and `WAVEE_DEALER_ARCHIVE` are gone, as are
  the `--free` flag and `WAVEE_FORCE_FREE`. Behaviour is configured in Settings (or reported by the diagnostics pages),
  never by an env var a shipped build would have to honour.
- Updates: **The `ms-appinstaller:` hand-off.** Windows disables that protocol by default on consumer installs, so the
  button opened nothing and then reported success. Wavee downloads and stages the update itself.

### Known limitations

- No dedicated **episode** or **profile** pages.
- **`nl` and `ko-KR`** localizations are partial and therefore hidden from the language picker.
- No **system tray** integration.
- No **podcast playback-speed** control.
- **FLAC seek** is not implemented.
- The **UI Automation tree** is incomplete — screen-reader coverage is partial.

[0.2.0]: https://github.com/christosk92/WaveeMusic/releases/tag/wavee-v0.2.0
