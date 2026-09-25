# Wavee privacy

Wavee is an independent Spotify desktop client. It is **not affiliated with, endorsed by, or sponsored by
Spotify AB**. Spotify and the Spotify logo are trademarks of Spotify AB.

Wavee has **no analytics and no telemetry of its own**. It has one optional exception: an **opt-in,
off-by-default crash report** you can choose to send when Wavee crashes — covered in full under
"Crash reports (opt-in)" below. Outside of that one feature, the author of Wavee receives nothing from
your installation — not a ping, not usage data, not an error report you haven't chosen to send.

## What stays on your machine

Everything Wavee stores lives under `%LOCALAPPDATA%\Wavee` and never leaves the device unless you copy it
out yourself:

| What | Where | Notes |
|---|---|---|
| Your Spotify credential | `store.json` | Protected at rest with Windows DPAPI (per-user, per-machine). It is the reusable credential Spotify issues at login — never your password. |
| Library database | `library.db` | Albums, artists, playlists, sync state, and the metadata cache. |
| Encrypted audio cache | `Wavee\Cache` (relocatable in Settings › Storage) | Encrypted CDN chunks and saved license keys, so you do not re-download tracks you already streamed. |
| Image cache | `cache\images` | Album art and other artwork, keyed by URL hash. |
| Logs | `logs\wavee.log` | Rolled at 10 MB, 7 files kept. Written to disk only, never uploaded. |
| Crash reports | `logs\crash\<stamp>-<kind>\` | A folder per crash: the error and a redacted log excerpt, optionally a memory snapshot. Sending it anywhere is off by default and always your choice — see "Crash reports (opt-in)" below. |
| Playback session & navigation history | `session.json` | So Wavee reopens where you left off. |
| Local play log | under `%LOCALAPPDATA%\Wavee` | Powers the local "recently played" surfaces. |
| Settings | Windows registry, `HKCU` | Typed keys only. |
| Dealer WebSocket archive | off by default | A debugging capture of Spotify's push frames, enabled only in Settings › Diagnostics. Local file. |

## What leaves your machine

Only these, and only to the parties named:

- **Spotify.** Everything you would expect a Spotify client to send: login, catalog and playlist requests,
  playlist edits, Connect device state, and audio streaming. This includes **play telemetry to Spotify's
  event receiver ("gabo")** — the same events the official client sends. This is not optional: it is what
  makes Recently Played, play counts, and your listening history work at all. If Wavee did not send it,
  your Spotify account would simply stop recording what you played.
- **Lyrics providers**, when a track has no Spotify lyrics and you have lyrics enabled. Wavee queries them
  with the track title, artist, and duration — not your account. The providers are: `lrclib.net`,
  the AMLL TTML database on `raw.githubusercontent.com`, `apic-desktop.musixmatch.com` (Musixmatch),
  `lyrics.kugou.com` (KuGou), `music.163.com` (NetEase), and `y.qq.com` / `c.y.qq.com` (QQ Music).
- **GitHub**, for update checks and release notes. Wavee fetches a static `.appinstaller` file from the
  `wavee-stable` release at `github.com/christosk92/WaveeMusic/releases/download/wavee-stable/`, reads the
  version number in it, and — when you choose to update — asks Windows to download and install that release's
  `.msix` package. It also fetches the "What's new" notes (and the public issue titles they reference) from the
  same repository. It sends no identifiers; GitHub sees ordinary anonymous file downloads (your IP and user
  agent, as with any download).
- **Spotify's image CDN**, to fetch album art.
- **Wavee's own crash-reporting service** (below), and only when you have turned crash reporting on, or
  when you press Send on a specific report by hand. Off by default; nothing is sent until you opt in.

Nothing else. In particular: there is no third-party analytics, advertising, or crash SDK anywhere in the
app — the crash-reporting service above is code in this repository (`ops/crash/`), not a vendor's SDK,
and it never runs unless you've turned it on.

## Crash reports (opt-in)

Wavee can send a report when it crashes or stops responding, to help find and fix bugs. **This is off by
default.** You are asked once, in the setup wizard, and again the first time Wavee actually crashes; you
can change your mind at any time in **Settings › Privacy & diagnostics**, where the options are "No" (the
default), "Ask me first" (review and send each report by hand), or "Always send automatically."

A report contains, and only contains:

- The error and where it happened — the exception type and message, and the crashing call stack
  (resolved to method names against that exact build's symbols on our server; nothing about your code or
  data, only Wavee's own).
- Wavee's version and Windows build, and your GPU model and driver tier.
- The last 300 lines of that session's log, with personal details removed before anything leaves your
  machine: file paths, account and device identifiers, emails, tokens, and IP addresses are all stripped.
  Track, album, and playlist ids from the last few minutes are kept (they help reproduce the crash) — never
  your Spotify username, email, or password.
- A random **install id** — a GUID generated on this PC and stored with your local settings. It identifies
  this *installation*, not you: it carries no account or hardware information, and it is wiped by
  **Settings › Storage › Factory reset**.
- Optionally, a small memory snapshot (a Windows minidump) — only when you have explicitly ticked "Include
  a memory snapshot," or for a single report you send by hand and choose to include one. It holds thread
  stacks and loaded modules, never the process heap, so it cannot contain your Spotify credential or other
  live secrets.

**Never sent:** your Spotify account, password, or display name; any file on disk other than the
redacted log excerpt above; and your IP address is never stored by the service (see below) even though,
like any web request, it is visible to the network in transit.

**Where it goes:** our own crash-reporting service, source code in `ops/crash/` of this repository,
running on Cloudflare's infrastructure (a Cloudflare Worker, with Cloudflare R2 and D1 for storage) — not
a third-party crash SDK or analytics vendor. Cloudflare is a global network; which of its facilities
physically handles a given request is not something Wavee controls or guarantees, so no specific
jurisdiction (EU or otherwise) is promised. The service does not read, log, or store the connection
address a report arrives from — it sees your IP the way any web server briefly does while handling the
request, and never writes it to a database row, a file, or a log line.

**Retention:** crash reports are deleted automatically **90 days** after upload (a Cloudflare R2 lifecycle
rule enforces this — see `ops/crash/README.md`). Grouped issue statistics (counts, affected versions — no
report contents) may be kept longer to track whether a bug has been fixed.

**Seeing, deleting, or sending a report by hand:** every crash report Wavee has saved is listed in
**Settings › Logs › Reports**, whether or not you have crash reporting turned on — you can open, copy, or
manually send any individual report from there regardless of your general setting, and delete it from
your PC (`View` opens it in Notepad; `Send` is a single explicit action). The underlying files live in
your `logs\crash` folder (see the table above) if you would rather inspect or delete them directly on
disk.

### Your rights

Every report the crash service holds is tied to that random install id and nothing else, so deleting it
is straightforward:

- **In the app:** **Settings › Privacy & diagnostics** shows your install id and a **"Delete my data…"**
  button. Confirming it deletes every report the service has ever received from this install — the report
  contents, the memory snapshot when one was sent, all of it — usually within moments, and Wavee then
  starts using a brand-new install id, so anything sent afterward (if reporting stays on) can't be tied to
  what was deleted.
- **After you've uninstalled Wavee**, or if you'd rather ask a person: the install id was shown in that
  same Settings row while Wavee was installed — if you copied it down (or still have it in an old crash
  report on disk), open an issue at <https://github.com/christosk92/WaveeMusic/issues> or use the address
  under "Contact" below with that id, and it will be deleted by hand on the same terms as the in-app
  button.
- **Automatically, either way:** every report is deleted **90 days** after it was sent, whether or not you
  ever ask — see "Retention" above.

## Your Spotify account

Your relationship with Spotify — including what Spotify does with the requests above — is governed by
[Spotify's own privacy policy](https://www.spotify.com/legal/privacy-policy/). Wavee is a client; it does
not change what Spotify collects.

## How to erase everything

**Settings › Storage › Factory reset** signs you out and permanently deletes all local Wavee data on the
PC — login, library, metadata, settings, caches, and history — and restarts Wavee on the first-launch
screen. The app itself stays installed. Individual caches can also be cleared on their own from the same
page, and **Settings › About › Open data folder** shows you exactly what is there.

## Contact

Questions or a privacy problem: open an issue at
<https://github.com/christosk92/WaveeMusic/issues>.
