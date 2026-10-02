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
| Logs | `logs\wavee-<date>.log` | A new file each day or at 10 MB; kept 7 days, up to 250 MB. Written to disk only, never uploaded. |
| Crash reports | `logs\crash\<stamp>-<kind>\` | A folder per crash: the error and a redacted log excerpt, optionally a memory snapshot. Sending it anywhere is off by default and always your choice — see "Crash reports (opt-in)" below. |
| Playback session & navigation history | `session.json` | So Wavee reopens where you left off. |
| Local play log | under `%LOCALAPPDATA%\Wavee` | Powers the local "recently played" surfaces. |
| Settings | Windows registry, `HKCU` | Typed keys only. |
| Dealer WebSocket archive | off by default | A debugging capture of Spotify's push frames, enabled only in Settings › Privacy & diagnostics › Tools › Realtime capture. Local file. |

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
- **Wavee's own crash-reporting service** at `https://crash.cproducts.dev` (below), and only when you have
  set crash reporting to Automatic, or when you press Send on a specific report by hand. Off by default;
  nothing is sent until you opt in.

Nothing else. In particular: there is no third-party analytics, advertising, or crash SDK anywhere in the
app — the crash-reporting service above is code in this repository (`ops/crash/`), not a vendor's SDK,
and nothing reaches it unless you've chosen Automatic or pressed Send.

## Crash reports (opt-in)

Wavee can send a report when it crashes or stops responding, to help find and fix bugs. **This is off by
default.** You are asked once, in the setup wizard, and can change your mind at any time in **Settings ›
Privacy & diagnostics**. Both offer the same choices:

- **Off** (the default) and **Ask each time** — every time Wavee crashes or stops responding, it shows you a
  prompt on this PC where you can read the full report and choose to send it. Nothing is uploaded unless you
  click Send.
- **Automatic** — reports are sent without asking. If you're offline, the report waits on your PC and goes
  out once you're back online. Automatic is only offered by builds that can send reports at all; a build
  without the crash service's address (for example one you compiled yourself) offers only Off and Ask each
  time.

A report contains, and only contains:

- The error and where it happened — the exception type and message, and the crashing call stack
  (resolved to method names against that exact build's symbols on our server; nothing about your code or
  data, only Wavee's own).
- For a crash inside native code that isn't Wavee's own (a Windows library, or a driver loaded into Wavee):
  the Windows exception code, the **name of the program file** the fault happened in — for example a
  graphics driver's DLL; the file name only, never its folder — and the offset inside that file. Because
  that file can belong to other software, its name may reveal a piece of software installed on your PC
  (most often your graphics driver).
- Wavee's version, build commit, update channel, processor architecture (x64 or ARM64) and whether it is the
  installed package; your Windows build and display language; your GPU model and driver tier, and whether
  Windows fell back to software rendering.
- How that run went: how long Wavee had been running, whether its window had appeared yet, the Wavee page
  you were on (its address inside Wavee — for example an artist page and that artist's Spotify id), the
  process exit code, and a random id for that app session (a new one every launch).
- Technical identifiers of the Wavee program file itself — where Windows loaded it, its size and its build
  id — so the call stack can be matched to that exact build.
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

**Where it goes:** our own crash-reporting service at `https://crash.cproducts.dev`, source code in
`ops/crash/` of this repository, running on Cloudflare's infrastructure (a Cloudflare Worker, with
Cloudflare D1 and R2 for storage) — not a third-party crash SDK or analytics vendor. Cloudflare is a
global network; which of its facilities physically handles a given request is not something Wavee
controls or guarantees, so no specific jurisdiction (EU or otherwise) is promised. The service does not
read, log, or store the connection address a report arrives from — it sees your IP the way any web
server briefly does while handling the request, and never writes it to a database row, a file, or a log
line.

**Alerts:** when the service sees a new kind of crash, or a bug that was marked fixed comes back in a newer
Wavee version, it posts a short notice to the maintainer's private Discord channel. The notice holds the
crash type and the Wavee code location it happened in (for a native crash, also the program file named
above), the Wavee version, architecture and release channel, and how many reports and installs that issue
has — never the exception message, any report or install id, or the log.

**Retention:** every crash report — its database row and its files, including any memory snapshot — is
deleted **90 days** after upload by a daily clean-up job on the service, with a Cloudflare R2 lifecycle
rule on the files as a backstop (see `ops/crash/README.md`). What is kept after that are the grouped
statistics of each issue (an issue collects every report of the same bug): how many reports and installs
it has had, which Wavee versions, when it was first and last seen, its crash type, and the latest Wavee
code location it crashed in — method names inside Wavee itself (for a native crash, also the program file
it happened in). These statistics never hold report contents: no exception message, log, install id, or
memory snapshot. They are kept to track whether a bug has been fixed.

**Seeing, copying, deleting, or sending a report by hand:** every crash report Wavee has saved is listed
in **Settings › Privacy & diagnostics › Crash reports › Saved reports**, whether or not you have crash
reporting turned on, each with its short report id. Each report's **…** menu offers four actions, which work
regardless of your general setting: **View** opens it in Notepad; **Copy** puts the redacted report — exactly
what would be sent — on your clipboard; **Send** shows you the report first and uploads it only when you
confirm — a single explicit action; **Delete** removes it from your PC, and a report deleted before it was
sent is never uploaded. Deleting a report from your PC does not remove a copy that was already sent: that copy is
deleted after 90 days (see "Retention"), or straight away with "Delete my data…" below. The underlying files live
in your `logs\crash` folder (see the table above) if you would rather inspect or delete them directly on disk.

### Your rights

Every report the crash service holds is tied to that random install id and nothing else, so deleting it
is straightforward:

- **In the app:** **Settings › Privacy & diagnostics** shows your install id and a **"Delete my data…"**
  button. Confirming it first drops any report still waiting to send on this PC, then deletes every report
  the service has ever received from this install — the report contents, the memory snapshot when one was
  sent, all of it — usually within moments. The service keeps only a note that the old install id was
  erased (the id and when), so a report still carrying that id can never be stored again: it is refused
  instead. Wavee then starts using a brand-new install id, so anything sent afterward (if reporting stays
  on) can't be tied to what was deleted.
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
