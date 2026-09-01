# Wavee in-app issue reporting — implementation plan (browser hand-off)

## Context

Reporters used to get a sticky toast ("Wavee crashed last time → Open report folder") and a bare link to the
issues page. The GitHub side is organised (issue forms `crash_report.yml` / `bug_report.yml` /
`feature_request.yml` with labels, Discussions Q&A + Ideas, the release-linkage gate), so the app now feeds it
directly: after a crash, a modal prompts on relaunch with a prefilled report; from Settings, a reporter files a
bug / feature / question in two clicks; everything the app sends is **redacted** (user-profile paths, user +
machine names, Spotify identity, secrets, IPs/MACs — track names survive) and lands in the right GitHub channel
with the right labels. Decisions: **browser hand-off** (no token, no server) — the app opens a prefilled
issue-form URL for the small fields, puts the full redacted report on the clipboard, saves it as
`wavee-report-<stamp>.txt` beside the logs, and the toast/dialog names the box to paste into. Crash prompt =
modal each time, with "Don't ask again". Log size: crash 300 lines, manual 200 lines; bundle ≤ 60 KB.

This document describes what shipped, cross-referenced against the approved plan; §8 lists every deviation.

## 1. System map

```
Program.cs ─► CrashDumpProbe.LogPendingCrashDump(settings, log) → string? newDumpPath
           ─► RunMarker.Begin(settings) → RunOutcome (previous "running" ⇒ Unclean)
           ─► versionChanged = --relaunched-after-update OR LastRunVersion != AppVersion.Info.LastRunKey
           ─► CrashPromptPolicy.ThisLaunch = Decide(pendingReport, newDump, prevRun, optOut, versionChanged)
           ─► settings.Set(PendingCrashReport, "")   (one-shot: consumed into ThisLaunch, never reread)
           ─► AfterUpdateDialog.CrashNoticeThisLaunch = (ThisLaunch.Mode != None)   (defers the What's-new plate)
           ─► FluentAppHarness.Run(...) returns ─► RunMarker.End(settings)   (also ProcessExit; UnhandledException/app-loop catch → MarkCrashed)

WaveeShell ZStack (inside OverlayHost, before AfterUpdateChrome) ─► Embed.Comp(ReportChrome) (0×0)  — see §3 for its
    two effects (ReportRequests.Requested → open; CrashPromptPolicy.ThisLaunch → open/toast) and the ReportDialog tree.

Entry points ─► ReportRequests.Open(kind, prefill): About "Report a problem…"(Bug)/"Suggest a feature…"(Feature) ·
    Diagnostics overflow "Report this session…"(Bug, selected past session) · Diagnostics "Crash reports" card
    row "Report…"(Crash, that file) · WaveeShell.GoDeepLinkOpen("report", arg) for
    wavee://open?route=report&arg=bug|feature|crash|question|idea
```

## 2. Engine-free core — code

`src/apps/Wavee/Diagnostics/{ReportKinds,ReportIdentity,ReportRedactor,ReportBundle,IssueFormUrl,RunMarker,
CrashPromptPolicy,CrashReportFiles,CrashProbe}.cs` — `System.*`/`Wavee.Core` only, source-included into
`Wavee.Tests`, per the plan's engine-free-core rule.

### `ReportKinds.cs`

```csharp
public enum ReportKind : byte { Crash, Bug, Feature, Question, Idea }
public sealed record ReportChannel(ReportKind Kind, string Path, string TitlePrefix, string? Template, string? Category,
    string[] FieldIds, string[] TruncationOrder, string PasteBox);

static class ReportChannels
{
    public const string Repo = "https://github.com/christosk92/WaveeMusic";
    public static readonly string[] InstallSources = ["Microsoft Store", "Sideloaded (.appinstaller or .msix from GitHub)", "Built from source"];
    public static readonly string[] Architectures  = ["x64", "ARM64", "Not sure"];
    public static readonly string[] When = ["On launch", "During playback", "When switching video on or off", "When navigating pages", "After an update", "Randomly", "Other"];
    public static readonly string[] Reproduces = ["Every time", "Sometimes", "Once so far"];
    public static readonly string[] Areas = [/* playback, video, lyrics, ... 22 area slugs, "Not sure" */];

    // Crash→issues/new?template=crash_report.yml [version,install-source,architecture,windows-version,when,reproduces,what-were-you-doing]
    // Bug→bug_report.yml [..what-happened,steps-to-reproduce,expected-behaviour] · Feature→feature_request.yml [problem,proposal,area,alternatives]
    // Question/Idea→discussions/new?category=q-a|ideas [body]
    public static readonly ReportChannel Crash, Bug, Feature, Question, Idea;
    public static ReportChannel For(ReportKind kind);
    public static bool TryParseKind(string? arg, out ReportKind kind);   // bug|feature|crash|question|idea, OrdinalIgnoreCase; false + Bug on miss
}
```

Option arrays are copied verbatim from `.github/ISSUE_TEMPLATE/*.yml`'s old dropdown text (the crash/bug forms'
dropdowns were later converted to inputs — §6); `ReportChannelsTests` pins these against hard-coded expected lists.

### `ReportIdentity.cs`

```csharp
public sealed record ReportIdentity(string VersionLine, string InstallSource, string Architecture, string WindowsVersion, string Quad, string Commit, string Channel)
{
    public static ReportIdentity From(WaveeVersionInfo me, bool isPackaged, string osArch, int osBuild);
        // install = me.IsStore ? "Microsoft Store" : isPackaged && !me.IsDev ? "Sideloaded (...)" : "Built from source"
        // arch = osArch switch { "X64" => "x64", "Arm64" => "ARM64", _ => "Not sure" }
        // win = (osBuild >= 22000 ? "Windows 11" : "Windows 10") + " (build " + osBuild + ")"
    public string ArchLabel    => Architecture switch { "x64" => "arch: x64", "ARM64" => "arch: arm64", _ => "" };
    public string InstallLabel => InstallSource == InstallSources[0] ? "install: store" : InstallSource == InstallSources[1] ? "install: sideload" : "";
}
```

### `ReportRedactor.cs` — the redaction rule table

`Redact(text, RedactionRules rules)` runs every built-in `[GeneratedRegex]` pattern in order, then caller-supplied
literals (`Environment.UserName`/`MachineName`, the Spotify user id/display name, Connect device names). Every
replacement is a placeholder none of the other patterns can match, so `Redact(Redact(x, r), r) == Redact(x, r)`.

| Rule | Matches | Replaced with | Kept alone |
|---|---|---|---|
| `ProfilePath`/`ProfileVar`/`MacProfile` | `C:\Users\bob\...`, `%USERPROFILE%`, `/Users/bob/Library/...` | `C:\Users\<user>\...`, `<user-profile>`, `/Users/<user>/...` | drive + `Users` segment |
| `UncHost` | `\\NAS01\share\x` | `\\<host>\share\x` | share + rest of path |
| injected literals | `UserName`/`MachineName`/`SpotifyUserId`/`DisplayName`/`DeviceNames` (≥ 3 chars, ordinal case-insensitive) | `<user>`/`<machine>`/`<spotify-user>`/`<display-name>`/`<device>` | shorter names skipped (would erase prose) |
| `SpotifyUser` | `spotify:user:abc123` | `spotify:user:<id>` | `spotify:track:...` etc. untouched |
| `Email` | any email address | `<email>` | — |
| `Bearer` | `Authorization: Bearer <token>` | `Bearer <token>` | the word "Bearer" |
| `KeyValueSecret`/`CountryProduct` | `access_token=`, `password:`, `secret=`, `cookie=`, `country=`, `tier=`, … | `key=<redacted>` | requires `=`/`:` right after the key word ("the key of C major" never fires); skips a `Bearer …` value |
| `IPv4`/`IPv6`/`Mac` | dotted-quad, compressed/uncompressed v6, colon-hex MAC | `<ip>`/`<ip6>`/`<mac>` | `version=`/`quad=`/`build=` prefixes and `(...)` release stamps survive; timestamps (`12:34:56.789`) and `seq:tid` never match v6 |
| `DeviceId` | `deviceId=<16+ hex>` | `deviceId=<device-id>` | — |

`ReportComposer.Rules(Services?)` supplies the injected literals; a null/logged-out `svc` still redacts the
OS-level names.

### `ReportBundle.cs` — the clipboard/file bundle

```csharp
static class ReportBundle
{
    public const int MaxBytes = 60 * 1024, CrashLogLines = 300, ManualLogLines = 200, PreviewChars = 12_000;
    public static string FileName(DateTimeOffset stamp);   // "wavee-report-yyyyMMdd-HHmmss.txt"
    public static string Build(ReportKind kind, ReportIdentity id, IReadOnlyList<(string Label, string Text)> answers,
        string diagnostics, string? crashHead, IReadOnlyList<string> logLines, string logSource, bool includeLogs, DateTimeOffset now);
        // header + version/install/arch/os lines + <label>:\n<text> per answer + "--- Crash report (redacted) ---"
        // (head capped at MaxBytes/2) + (includeLogs) "--- Diagnostics ---" + fenced "--- Log excerpt ... ---",
        // newest lines kept / oldest dropped to fit MaxBytes, with a "[truncated: N older lines dropped]" notice
    public static (string Head, string[] Tail) SplitCrashReport(string fileText);   // splits at "wavee.log tail"; Tail = last CrashLogLines
    public static string Preview(string bundle);   // bundle, or bundle[..PreviewChars] + "… (N KB more in the copied report)"
}
```

### `IssueFormUrl.cs` — the URL builder contract

```csharp
static class IssueFormUrl
{
    public const int Budget = 7000;
    public static string Build(ReportKind kind, ReportIdentity id, string title,
        IReadOnlyList<KeyValuePair<string, string>> fields, IReadOnlyList<string> labels);
        // repo + channel.Path + template=/category= + title= + [identity fields, never truncated] +
        // [labels=, best-effort] + &<fieldId>=<value> per non-empty non-identity field.
        // Over Budget: TruncationOrder fields cut with a trailing "…", longest/least-essential first, until it fits.
        // A dropdown value not in ReportChannels.* throws ArgumentException up front (Validate).
}
```
Discussions channels (`Question`/`Idea`) fold identity into the `body` field itself (§3's `BuildUrlFields` `default`
case) rather than as separate params — there is no issue-form to prefill.

### `RunMarker.cs`, `CrashPromptPolicy.cs`, `CrashReportFiles.cs`, `CrashProbe.cs`

```csharp
enum RunOutcome : byte { Unknown, Clean, Unclean }
static class RunMarker   // consts "running"/"clean"/"crashed"
{
    public static RunOutcome Begin(IAppSettings s);   // reads+overwrites RunMarker with "running"
    public static void End(IAppSettings s);           // "running" → "clean"; never stomps "crashed"
    public static void MarkCrashed(IAppSettings s);    // → "crashed"
}

enum CrashSource : byte { None, ManagedReport, WerDump, UncleanExit }
enum CrashPromptMode : byte { None, Dialog, Toast }
readonly record struct CrashPromptDecision(CrashPromptMode Mode, CrashSource Source, string? ReportPath, string? DumpPath);
static class CrashPromptPolicy
{
    public static CrashPromptDecision ThisLaunch;   // latched by Program.cs, consumed+reset by ReportChrome
    public static CrashPromptDecision Decide(string pendingReport, string? newDumpPath, RunOutcome previousRun, bool optOut, bool versionChanged);
        // priority: pendingReport(ManagedReport) > newDumpPath(WerDump) > (Unclean && !versionChanged)(UncleanExit) > None
        // Mode = optOut ? Toast : Dialog; ReportPath only set for ManagedReport
}

static class CrashReportFiles   // List(dir, max): newest FILE NAME first (matches CrashReport.Prune), never throws, [] on I/O failure
{
    public static (string Path, DateTime Stamp)[] List(string dir, int max = CrashReport.DefaultKeep);
    public static bool TryStamp(string fileName, out DateTime stamp);
}

static class CrashProbe { public static string? Mode; }   // "throw"|"failfast"|null — Program.cs's --crash-probe arm
```

## 3. App glue — component tree and code

`src/apps/Wavee/Features/Feedback/{ReportComposer,ReportRequests,ReportChrome,ReportDialog,CrashReportsCard}.cs`.

```
WaveeShell ZStack (OverlayHost child, before AfterUpdateChrome)
├── … SidebarOnboardingChrome · SetupChrome
├── Embed.Comp(ReportChrome) (0×0)
│     effect A (ReportRequests.Requested) ──► ReportDialog.Open(overlay, svc, hooks, settings, kind, prefill, default)
│     effect B (SetupSession.MarkerEpoch, CrashPromptPolicy.ThisLaunch) ──► Toast (opted out) | ReportDialog.Open(..., Crash, null, decision)
│     CrashProbe.Mode ──► UseTimeout(2000ms): throw / Environment.FailFast
│     ReportDialog.Open ──► ContentDialog.Show(overlay, d => { Title="Report a problem"; DialogWidth=548f;
│         Content = Embed.Comp(ReportDialogBody {...}); PrimaryText = isCrash?"Report on GitHub":"Open GitHub";
│         CloseText = isCrash?"Not now":"Cancel"; PrimaryButtonClick = args => { if (!session.Submit()) args.Cancel = true; } })
│       ReportDialogBody : Component   (Width = 500 DIP = 548 - 2×24 padding)
│         ├── TextEl subtitle · [not crash] Segmented [Bug|Feature|Question|Idea] (kindIndex:Signal<int>, seeded once)
│         └── Embed.Comp(ReportDialogCard {...}) with { Key = "report-card:" + (int)kind }   ← remounts per kind
│               ReportDialogCard : Component
│                 ├── [crash only] InfoBar(Error) "Wavee closed unexpectedly · <time> · <exception line>" | crashNoReport
│                 ├── TextBox Title
│                 ├── kind rows: Crash→When+Reproduces(Combo)+WhatWereYouDoing · Bug→WhatHappened+Steps+Expected+Area
│                 │   Feature→Problem+Proposal+Area+Alternatives · Question/Idea→Body(TextBox, 140 DIP)
│                 ├── CheckBox "Include diagnostics and the last {N} log lines" (default true for Crash/Bug)
│                 ├── row: "Report preview" · Copy report (Subtle) · Save as… (Subtle)
│                 ├── BoxEl{Height=160,Clip,bordered}→ScrollEl→TextEl(preview) mono 11 — per-signal recompute
│                 ├── TextEl previewNote ("Personal paths, account details and secrets are removed. Track names kept.")
│                 └── [crash only] CheckBox "Don't ask again after a crash" → WaveeSettings.CrashPromptOptOut
└── Embed.Comp(AfterUpdateChrome)   (deferred this launch when CrashNoticeThisLaunch)
```

Entry-point prefills (see §1 for the entry points themselves): `SettingsPage.About.cs` passes no prefill;
`DiagnosticsPanel.cs`'s overflow passes `ReportPrefill(PastSession: SelectedPastSession())`; `CrashReportsCard.cs`'s
row passes `ReportPrefill(CrashReportPath: path)`; the deep link is handled in `GoDeepLinkOpen` before
`ShellRoutes.IsKnown` — a report is a dialog, never a tab/history entry.

```csharp
static class ReportComposer
{
    public static RedactionRules Rules(Services? svc);   // OS names + Spotify identity + Connect device names
    public static ReportIdentity Identity();
    public static ComposedReport Compose(ReportKind kind, ReportPrefill? prefill, CrashPromptDecision crash, Services? svc);
        // diagnostics = Redact(SettingsPage.DiagInfoText(svc)); log source priority: (1) a crash-report file
        // (prefill.CrashReportPath ?? crash.ReportPath) → SplitCrashReport (2) prefill.PastSession, or the newest
        // past session on disk for WerDump/UncleanExit (3) this session's own WaveeLog.Instance.Snapshot() tail —
        // every line/head redacted with the SAME RedactionRules; returns ComposedReport
}
```

`SettingsPage.DiagInfoText(Services? svc)` — hoisted from the `DiagInfo()` local closure in
`SettingsPage.About.cs`'s `AboutTab` to `internal static` so `ReportComposer.Compose` can call it off the UI
thread. `CrashReportsCard.cs` is a `SettingsExpander` "Crash reports": `CrashReportFiles.List(...
10)` (`UseMemo`, snapshot-on-mount), each row = timestamp + "Report…" + "Open" (`ShellOpen.RevealInExplorer`),
empty state "No crash reports" — mounted at the end of `DiagnosticsPanel`'s `DiagnosticSwitches()`, not the log card.

## 4. Program.cs — run marker + crash decision

```csharp
// UnhandledException handler, after the existing report-write logic; and the ProcessExit handler (belt-and-braces
// — the orderly path below already calls RunMarker.End):
try { RunMarker.MarkCrashed(settings); } catch { }   /* in UnhandledException */
try { RunMarker.End(settings); } catch { }            /* in ProcessExit */

// CLI arms, alongside --perf-bench / --startup-bench:
int crashProbeIdx = Array.IndexOf(args, "--crash-probe");
if (crashProbeIdx >= 0)
    CrashProbe.Mode = crashProbeIdx + 1 < args.Length && !args[crashProbeIdx + 1].StartsWith("--") ? args[crashProbeIdx + 1] : "throw";

// Just before FluentApp.Run (was a bare LogPendingCrashDump call — now captures its return):
string? newDump = CrashDumpProbe.LogPendingCrashDump(settings, WaveeLog.Instance);
RunOutcome prevRun = RunMarker.Begin(settings);
bool versionChanged = Array.IndexOf(args, AppRelaunch.RelaunchedAfterUpdateFlag) >= 0
    || settings.Get(WaveeSettings.LastRunVersion) != AppVersion.Info.LastRunKey;
CrashPromptPolicy.ThisLaunch = CrashPromptPolicy.Decide(settings.Get(WaveeSettings.PendingCrashReport), newDump,
    prevRun, settings.Get(WaveeSettings.CrashPromptOptOut), versionChanged);
settings.Set(WaveeSettings.PendingCrashReport, "");
AfterUpdateDialog.CrashNoticeThisLaunch = CrashPromptPolicy.ThisLaunch.Mode != CrashPromptMode.None;

RunMarker.End(settings);   // after FluentAppHarness.Run(...) returns (orderly shutdown)
try { RunMarker.MarkCrashed(settings); } catch { }   // app-loop catch block, alongside the existing PendingCrashReport write
```

`WaveeShell.cs`'s old crash-notice effect (a sticky toast reading `PendingCrashReport` off settings) was deleted
outright — Program.cs now owns the decision, `ReportChrome` owns the surfacing.

## 5. Loc keys

New top-level `report` namespace in `en-US.json` (with a `$comment`): `subtitle titleRequired title kindBug
kindFeature kindQuestion kindIdea titleLabel titlePlaceholder whatHappened steps expected area problem proposal
alternatives when reproduces whatWereYouDoing body includeLogs({count}) preview previewNote preparing openGithub
reportOnGithub notNow copy saveAs copied copiedPaste({field}) saved crashTitle crashAt({time}) crashNoReport
dontAskAgain thisSession crashReports crashReportsSub crashReportsEmpty reportButton openButton
aboutReportProblem aboutSuggestFeature aboutAllIssues`. `SettingsPage.About.cs` drops the old single
`SendFeedback` hyperlink for three: "Report a problem…" (Bug), "Suggest a feature…" (Feature), "All issues on
GitHub" (still `FeedbackUrl`, a plain link).

## 6. GitHub side

`.github/ISSUE_TEMPLATE/crash_report.yml` and `bug_report.yml`: **`install-source` and `architecture` were
converted from `type: dropdown` to `type: input`** (option list moved into the field `description` + a
`placeholder`) — the single largest deviation from the plan, driven by a verified GitHub behaviour, not style:

**GitHub's current issue-creation UI prefills `input`/`textarea` fields from `?field-id=` query params but never
prefills `dropdown` fields — verified 2026-09-02.** A `&install-source=Microsoft%20Store` param is silently
ignored by a dropdown field but shows up ready-to-submit in an `input` field of the same id. Install-source and
architecture became inputs so the app's URL can actually fill them; the app still validates its own values against
`ReportChannels.InstallSources`/`Architectures` (`IssueFormUrl.Validate`) before building the URL, so Wavee itself
never sends a malformed value.

The remaining dropdowns (`when`, `reproduces` on the crash form; `area` on bug/feature) **stay dropdowns** — useful
for a human filing directly — but since the app's prefill can't reach them, it mirrors each answer into the first
line of the neighbouring textarea it CAN prefill: `"When: <x> · Reproduces: <y>\n\n<text>"` for the crash form,
`"Area: <x>\n\n<text>"` for bug/feature (`ReportDialogCard.BuildUrlFields`/`AreaLine`). The dropdown query params
are still sent too — harmless today, free if GitHub ever adds dropdown prefill.

The `labels=` param only applies for reporters with triage rights; `ReportIdentity.ArchLabel`/`InstallLabel` and
`BuildLabels` are therefore best-effort — the same facts are also in the prefilled form fields, visible and
submittable regardless of permissions.

`crash_report.yml`'s `crash-report` field description now points at Settings › About › Report a problem… and
`wavee-report-<date>.txt`, and correctly names the raw-report location (`crash-report-<date>.txt` via Settings ›
Diagnostics › Crash reports › Open) — dropping the old text's false claim that the report was "also saved in
`logs\wavee.log`". `bug_report.yml`'s `diagnostics`/`log-lines` descriptions gained a pointer to the same path.

## 7. Tests

`src/apps/Wavee.Tests/Feedback/` (xUnit, no source-text tests — every one exercises the pure classes directly):

| File | Coverage |
|---|---|
| `ReportRedactorTests.cs` | before/after per built-in pattern; literal case-insensitivity + `< 3 chars` skip; false positives that must survive (`spotify:track:...`, `version=0.2.5.6`, `12:34:56.789`, `seq=1 tid=5`, "the key of C major") |
| `IssueFormUrlTests.cs` | exact URL for Crash/Bug/Discussions channels, label composition, over-budget truncation (longest/least-essential field first), `Validate` throwing on a bad dropdown value |
| `ReportChannelsTests.cs` | option arrays match hard-coded expected lists; `TryParseKind` round-trips, falls back to `Bug` |
| `ReportBundleTests.cs` | byte-budget truncation (newest kept, notice line), `includeLogs=false` omits the fence, `SplitCrashReport` head/tail, `FileName`, `Preview` cutoff |
| `ReportIdentityTests.cs` | dev/store/sideload install strings; arch mapping; OS-build → Windows 10/11; empty labels for "Not sure"/"Built from source" |
| `CrashPromptPolicyTests.cs` | priority matrix `ManagedReport > WerDump > UncleanExit > None`; `versionChanged` suppresses only `UncleanExit`; `optOut → Toast` |
| `RunMarkerTests.cs` | `Unknown`→writes `running`; `Begin;End;Begin`→`Clean`; `Begin;Begin`→`Unclean`; `MarkCrashed` survives `End`; `Begin` after `crashed`→`Clean` |
| `CrashReportFilesTests.cs` | temp dir, stamped + stray files → newest-`max` by file name, stray excluded; `TryStamp` round-trip |

## 8. Verification

1. `dotnet build Wavee.slnx` Debug **and** Release (Release exercises `[GeneratedRegex]` + the AOT analyzers).
2. `dotnet test src/apps/Wavee.Tests/Wavee.Tests.csproj --filter FullyQualifiedName~Feedback`, then the full suite.
3. `dotnet run --project src/apps/Wavee -- --fake` → About › "Report a problem…": dialog opens in `ContentDialog`,
   preview redacts the data-folder line; Copy report → paste into Notepad; Open GitHub → browser lands on the bug
   form with fields prefilled, toast names "Relevant log lines"; Segmented routes Feature/Question/Idea correctly.
4. `start "" "wavee://open?route=report&arg=feature"` (app running) → dialog on the Feature segment.
5. Crash rehearsal: `--fake --crash-probe throw` → report written, marker `crashed`; relaunch → modal, then no
   prompt on the next. `--crash-probe failfast` → no managed report; relaunch → modal says none was written,
   excerpt from the previous session. "Don't ask again" + crash again → sticky toast, no modal.
6. Diagnostics: "Crash reports" card lists the rehearsal files, "Report…"/"Open" work; What's-new is deferred.
7. `gh label list` (read-only) confirms `arch: x64|arm64`, `install: store|sideload`, `area: *` exist.
8. Packaged-run hygiene per CLAUDE.md: wipe `%LOCALAPPDATA%\Wavee` after the unpackaged rehearsals.

## As built (2026-09-02)

Deviations from the approved plan, found in the shipped code:

- **`ContentDialog` host, not a hand-drawn 640 DIP raw-overlay card.** The plan called for a raw-overlay modal
  (`AfterUpdateDialog`/`PlayLinkDialog` precedent); a card whose size settles only after mount was mis-centred by
  the raw overlay, so `ReportDialog.Open` uses the engine's own `ContentDialog`, which centres correctly regardless.
- **548 DIP dialog width / 500 DIP content width, not 640** — `DialogWidth = 548f` matches WinUI's
  `ContentDialog_themeresources.xaml` `MaxWidth`; everything sizes off `ContentWidth = 500f`. Preview box is 160
  DIP tall, not 180.
- **Dropdown facts mirrored into text; install-source/architecture became `input` fields.** §6 has the full
  reasoning (GitHub prefills inputs/textareas, never dropdowns — verified 2026-09-02). `BuildUrlFields`/`AreaLine`
  mirror `when`/`reproduces`/`area` into the neighbouring textarea's first line; the two identity dropdowns became
  YAML inputs.
- **`CrashProbe.Mode` is a bare static field** — `public static string? Mode;`, no enum; `ReportChrome`
  string-compares `"failfast"` directly, same intent as the plan with less ceremony.
- **`ReportDialogSession.Submit` handshake + a `ReportDialogBody`/`ReportDialogCard` split — not named in the
  plan.** The form lives inside a `ContentDialog` whose primary button belongs to the dialog, not the form, so the
  body tells `PrimaryButtonClick` whether submit succeeded via a mutable `Func<bool>? Submit` box
  (`ReportDialogSession`). The plan's one card became two components — chrome (`ReportDialogBody`) and the
  kind-specific form (`ReportDialogCard`, keyed by kind) — so switching kind remounts the whole answer set, per
  "component props freeze at mount".
- **`SettingsPage.DiagInfoText` hoist matches the plan** but sits right before `AboutTab`, not the plan's line numbers.
- **Everything else matches the plan closely**: `RunMarker`/`CrashPromptPolicy`/`CrashProbe` wiring in `Program.cs`,
  the `WaveeShell.cs` crash-notice-effect deletion + `ReportChrome` mount ordering, the deep-link arm, the
  `CrashReportsCard` placement, the settings-key additions (`CrashPromptOptOut`, `RunMarker`), and all eight
  planned test files.
