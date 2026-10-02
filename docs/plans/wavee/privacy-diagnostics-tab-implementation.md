> Approved 2026-10-02 (Concept A of the concepts artifact https://claude.ai/artifact/LfUvrayESpgsmF9ubKJnvw). Implementation plan + the logging audit; the owner's copy lives in the planning session, this file is the repo record. Implemented the same day (see CHANGELOG 0.3.0 unreleased); the Logs page head uses the breadcrumb idiom per the owner's note in §3.2.

# Privacy & diagnostics tab — rework the Logs tab, relocate the scattered rows, fix the session picker and file logging

## Context

The Settings › Logs tab is unusable: twenty-one controls share two rows before a log line is visible, two of them
("Capture level", "File log level") are persisted settings masquerading as filters, three toggles are wordless icons
("Newest first", "Group repeats", `{ }` = "Verbose"), and the crash-report list sits under the viewer with three full
buttons per row. The privacy rows (crash-report consent, memory snapshot, send queue, saved reports, policy link,
"Your data on the crash service") and the whole Developer section (developer mode, FPS overlay, the realtime flight
recorder, capture folder/viewer, simulate update, test crash report) live in **General**, although PRIVACY.md and the
CHANGELOG already name a "Settings › Privacy & diagnostics" tab that does not exist. The "This session" combobox never
offers anything else, and the owner asked for an audit of the on-disk log writing.

Outcome: one **Privacy & diagnostics** tab that replaces Logs and absorbs those rows; the log viewer becomes a clean
full-page route; the session picker lists past runs for real; the file writer's rules match what PRIVACY.md promises.

**Concepts artifact** (three organising ideas drawn inside the real Wavee shell, light/dark, two window sizes, with a
recommendation, and the logging audit at the end): https://claude.ai/artifact/LfUvrayESpgsmF9ubKJnvw

- **A · settings page + the log as a page of its own** — recommended, planned below.
- **B · hub with sub-pages** (Windows Settings model, breadcrumbs, three more routes).
- **C · status-first dashboard** (receipts + "Recent problems", custom tiles, a second mini log viewer).

If the owner picks B or C, §1 (relocation), §2 (audit fixes), the catalog/strings/docs work and the Logs page are
identical; only the hosting (hub cards + breadcrumb pages, or the tile strip + "Recent problems") changes.

## 1. What moves where (all concepts)

| Today | Where | New home |
|---|---|---|
| Crash reports mode combo | General › Privacy & diagnostics | Privacy › **Crash reports** expander: value tag in the header, three radio items inside |
| Include a memory snapshot | General | inside the Crash reports expander, enabled when mode ≠ Off |
| Waiting to send · Send now · Discard | General | InfoBar item at the top of **Saved reports**, only while the outbox is non-empty |
| Saved reports · Open folder · Delete all + the Reports list under Logs | General + Logs | one **Saved reports** expander: rows inside, one "…" MenuFlyout per row (View · Copy · Send… · Delete), Open folder / Delete all in `ItemsFooter` |
| Privacy policy (header + link, same label) | General | clickable SettingsCard with the open-external action icon |
| Your data on the crash service | General | Privacy group, same controls (Copy id · Delete my data…) |
| Capture level · File log level · Verbose | viewer filter row / command bar | Logs › **Detail level** expander (two combos); Verbose stays a checkable item in the viewer's "…" |
| Newest first · Group repeats · Wrap | command-bar toggles | sortable **Time** column header; checkable "…" items |
| Refresh · Open log folder · Clear view | command bar | sessions re-list on page activation; Open folder in the page header; Clear view in "…" |
| Record realtime traffic · capture folder · capture viewer | General › Developer | Tools › **Realtime capture** expander, toggle in the header |
| Developer mode · FPS overlay · Simulate update · Test crash report | General › Developer | Developer › **Developer mode** expander; items greyed while off; toggles apply live (today they only apply after a restart) |
| Playback runtime · Connect diagnostics | Playback tab links | also clickable cards under Tools (Playback keeps its row) |
| Report a problem | About + viewer overflow | Logs › **Report a problem** card (About keeps its button; the viewer's "…" keeps "Report this session…") |

## 2. Audit — "This session" picker and the file writer (read-only, Fable 5.1; re-read by me)

### 2.1 Verdict on the empty picker

The code path is sound. Mount-once effect (`Diagnostics.UI.cs:299`, `DepKey.Empty`) → off-thread walk with
`Log.BasePath` = `…\logs\wavee.log` (`Platform.Host.cs:115, 643`) → `Directory.GetFiles(dir, "wavee-*.log")` →
`Split` by `sid=` dropping the live run → `UsePost` → keyed `ComboBox` remount (`:360-361`). Every link was verified
against engine source; the parse helpers cannot throw (`ReadLong` wraps instead of overflowing, `Expect` is bounded),
and the catch filters cover the IO cases. **On this machine today the folder holds 8 dated files (10/1–10/2, ~65 MB),
30 contiguous `sid=` runs, the last being the live process → the picker should list 29 past sessions.**

Why it reads "This session" only in practice, in the order the evidence supports:

1. **Retention is 7 files × 10 MB including the live file, and the app writes ~60 MB/day** (`Platform.Host.cs:618,
   783-786, 816-827`; 42.7 MB on 10/1). That is ~1.3 days of history. One long session (`4ee19f77`: 47,172 records
   ≈ 25 MB over three files) eats half the window. For anyone who leaves the player open, every earlier session is
   pruned and the picker is empty **by construction**. Today's 29 exist only because today had 20+ short crash-verify
   launches. Warning-level noise is a main volume driver (one 10 MB roll: `mem.sample` ×2,232, `[render.pace]`
   ×1,216, `[wake]` ×423).
2. **Storage › "Delete old logs" deletes the LIVE daily file.** `Settings.Host.cs:218` globs `wavee-*.log`, which
   matches `wavee-20261002.log`; its doc comment and the confirm text ("The live wavee.log file is kept") describe the
   0.2.9 world. The writer keeps appending to the unlinked handle (`FileShare.Delete`) until the next 10 MB check or
   midnight → folder empty, picker empty, crash `log-tail.txt` empty, up to 10 MB silently lost.
3. **Zero observability.** "This session" is rendered identically for "still walking", "none on disk" and "the walk
   faulted"; `_ = Task.Run(...)` discards a fault and a non-IO fault latches `_sessionsBusy = true` forever
   (`Diagnostics.UI.cs:479-489`); no log line, no footer count.
4. **Packaged runs.** The package LocalCache `logs\` folder is empty (last write 9/16) while its sibling `library.db`
   was written 10/1 → a packaged launch may leave no log file at all. Unresolvable read-only; needs one packaged
   launch and its `logResolved=` line.

**First implementation step (10 minutes, before any fix):** a headless probe `--log-sessions` that runs
`WaveeLogSessions.ListPastSessions` on the real folder and prints files/lines/sessions/elapsed or the exception, run
against `%LOCALAPPDATA%\Wavee\logs` and against a packaged LocalCache. It settles (3) and (4) with evidence.

### 2.2 Ranked findings

| # | Sev | Finding | Where |
|---|---|---|---|
| 1 | High | "Delete old logs" deletes the live daily file; confirm text and doc say it is kept | `Settings.Host.cs:207-218`, `en-US.json:1314`, `Platform.Host.cs:735,787` |
| 2 | High | Retention counts files (7, live included) not days/bytes → ~1.3 days at ~60 MB/day; the comment promises "about a week deep" | `Platform.Host.cs:618,783-786,816-827` |
| 3 | High | Crash tail reads the **boot day's** file: `s_datedLogPath` is frozen at install and passed to the child as argv; after midnight every bundle tails yesterday's file | `Shell.Host.cs:206`, `Crash.Host.cs:70-77,212,259`, `Crash.Handler.cs:97-101,481,512` |
| 4 | High | Session picker has no diagnostics or failure state; faults discarded and latch `_sessionsBusy` | `Diagnostics.UI.cs:477-496` |
| 5 | Medium | Managed-crash tail is written before the queue is flushed and before Shell logs the Critical line → the tail misses the crash's own lines and the last ≤512-entry batch (the native path flushes first) | `Crash.Host.cs:203-212`, `Platform.Host.cs:149-160`, `Shell.Host.cs:644-648`, `Platform.cs:1123` |
| 6 | Medium | Queue-drop marker is written without the `seq=/t=/sid=` prefix (unparseable later), never pushed to the ring, count zeroed before the write | `Platform.Host.cs:686,703-704,726-727`, `Platform.Settings.cs:451-454` |
| 7 | Medium | Warning-level noise (`mem.sample` every 5 s once WS > 400 MB, `[render.pace]`, `[wake]`, `[d3d12.*]`) dominates the Warnings badge and the volume | `Diagnostics.Host.cs:535`, `Platform.Host.cs:909-935` |
| 8 | Medium | Past-session parse is lossy: event id/op/elapsed/fields/exception folded into Message, continuation lines (stack traces, 40 % of one file) dropped → no Fields/Exception for past sessions | `Platform.Settings.cs:441-499` |
| 9 | Medium | Loader caps a past session to its last 4,096 entries while the label advertises the full count | `Platform.Settings.cs:431-439`, `Diagnostics.cs:165-171` |
| 10 | Medium | "Export session" on the live ring exports only the visible rows, without `t=`/`tid=`/`sid=`; a past session exports raw lines — same button, two products | `Diagnostics.UI.cs:557-558`, `Diagnostics.cs:147-158` |
| 11 | Medium | No test pins the writer's real roll name, `DatedPath`, `PruneRolledFiles` counting the live file, the drop marker, interleaved sids or `ListPastSessions`; the two tests the logs plan promised were never written | `Wavee.Tests` (`PlatformWave6Tests.cs:447-556`) |
| 12 | Low | Roll name is double-dated with a UTC stamp (`wavee-20261002-20261002-143142.log`); `File.Move(overwrite: true)` destroys a same-second collision | `Platform.Host.cs:755-758,806-810` |
| 13 | Low | PRIVACY.md and the Storage strings say `logs\wavee.log`; the Storage count counts every file in `logs\` (CSV captures, legacy crash txt → "23 files") | `PRIVACY.md:22`, `en-US.json:1322-1324`, `Settings.Host.cs:172` |
| 14 | Low | Interleaved processes fragment a run into three sessions; `" events"` literal unlocalised | `Platform.Settings.cs:366`, `Diagnostics.cs:170` |
| 15 | Low | Category combo remount key uses `_categories.Length` (same count, different names → stale items) | `Diagnostics.UI.cs:438-439` |
| 16 | Low | A deep link arrives with its NUL terminator (`wavee://open?route=home\0`) and the NUL rides into route ids and log lines | `Shell.Host.cs:786` |
| 17 | Low | Legacy `crash-report-*.txt`, `wavee-report-*.txt`, `scroll-*.csv` in `logs\` are never retained or offered for deletion | `logs\` |

What is fine (leave alone): UTF-8 without BOM; `FileShare.ReadWrite|Delete` consistent across writer, reader and
crash tail; the sid split, drop-the-current-run and newest-first rules; ordinal `Chronological`; the midnight switch
(verified on disk) and the size roll (observed live); `FileMinLevel` upward-only + `-1` resolution + immediate effect;
shutdown order (`Diagnostics.Shutdown` → `Platform.Shutdown` → `Log.Flush`), `ProcessExit` and both
`UnhandledException` flushes; no drops and no concurrent-writer corruption in the current files.

### 2.3 Fixes in this plan (Wave G) — design level, each with its pure class and test

| Fix | Change | Pure class · tests |
|---|---|---|
| G1 naming authority (#12, #13) | `LogFileNames` in `Platform.Settings.cs`: `Dated(base, localDay)` → `wavee-20261002.log`; `Rolled(base, localNow)` → `wavee-20261002-163142.log` (local time, single date, `overwrite: false` + `-2` suffix on collision); `Glob(base)`; `IsAppLog(name)`; `IsActive(name, today)`. Writer (`DatedPath`, `RollIfNeeded`), lister, deleter and the Storage census all call it. | `Rolled_SortsBeforeItsDay`, `Rolled_UsesLocalTime`, `Census_CountsOnlyAppLogs` |
| G2 never delete the active file (#1) | `DeleteOldLogs` deletes `IsAppLog && !IsActive`; fix the `:207` doc and the confirm body ("The file Wavee is writing to is kept."). | `DeleteOldLogsPlan_ExcludesTheActiveDatedFile` |
| G3 retention by age and bytes (#2) | `LogRetentionPolicy.Select(files:(path, bytes, lastWriteUtc), nowUtc, activePath, maxDays = 7, maxBytes = 250 MB)` → paths to delete; the writer applies it after each roll; the active file is excluded by rule. PRIVACY.md `:22`, the Storage `logsSub*` strings and the new "Log files" card read: "logs\wavee-<date>.log · a new file each day or at 10 MB · kept 7 days, up to 250 MB". | `KeepsSevenDaysNotSevenFiles`, `NeverSelectsTheActiveFile`, `BytesCapWins` |
| G4 observable picker (#4) | `ListPastSessions` returns `WalkResult(Sessions, FilesSeen, LinesSeen, FilesSkipped, Error)`; `RefreshSessions` wraps the walk in a catch-all, clears busy in `finally`, logs one `log.sessions.listed files= lines= sessions= ms=` (Warn with the exception on failure); the Logs page shows a caption under the session combo. | `SessionPickerState.Describe(busy, count, failed, retentionDays)` → "Reading past sessions…" / "No past sessions on disk — log files are kept 7 days" / "Couldn't read the log folder"; three tests |
| G5 crash tail follows the day and the queue (#3, #5) | In-process bundles use `Log.FilePath` at crash time; the child receives the BASE path and derives the dated name at bundle time via `LogFileNames.Dated`; `OnManagedCrash` calls `Log.Flush()` before `WriteTail`; `WriteTail` merges a snapshot of still-queued lines. | `TailAssembler.Merge(fileTail, pending, max)`; `CrashTailPath_FollowsTheDay`, `Merge_AppendsPendingAfterFile` |
| G6 drop marker as a real entry (#6) | Synthesize a `WaveeLogEntry(category "log")` written through `FormatFileLine` and pushed ring-only; re-add the count when a batch throws. | `FileQueuePolicy.Admit(count, cap)`; `TryParseLine_ParsesTheDropMarker` |
| G7 level hygiene (#7) | `MemorySamplePolicy.LevelFor(ws, prevPeak)`: Warning only on a new working-set peak or a growth edge; route `[render.pace]`, `[wake]`, `[d3d12.present]`, `[d3d12.display]` to Info via a `DiagRoute.Forensic` arm in `RouteFor`. | extend the existing `RouteFor` tests |
| G8 honest export and footer (#10, #9) | Live "Export session" writes the full ring snapshot in `FormatFileLine` shape (with `t=`/`tid=`), independent of filters; "Copy visible" stays the filtered one. Past-session footer reads "last 4,096 of 17,936 events · log file". | `LogView.ExportText(entries)` asserts the `t=` token; `LogView.PastFooter(loaded, total)` |
| G9 tests the logs plan promised (#11) | `ListPastSessions_DiscoversDailyRolledFiles_FromBasePath`, `ListPastSessions_DailyFileOrdering_SizeRollSortsBeforeItsDay` over a temp directory built with `LogFileNames`; `Configure_RollsAt10MB_AndPrunes` against a temp base path. | — |
| G10 deep-link NUL (#16) | Trim the WM_COPYDATA payload at its first `\0` before `DeepLink.Parse`. | parse test with a NUL-terminated `wavee://open?route=home` |

Follow-ups, not in this plan (owner's call): #8 faithful past-session parse (round-trip `Format()`, join continuation
lines into `Exception`, key sessions by sid → also fixes #14), #15 category combo key by content, #17 legacy file
clean-up in `logs\`.

## 3. Target design (Concept A)

Decisions (each also an open question in §9):

| # | Decision |
|---|---|
| D1 | Section glyphs Shield / Warning / Document / Repair / Code. `glyphs.json` lacks Shield, Warning, Repair → add `"Shield":"EA18"`, `"Warning":"E7BA"`, `"Repair":"E90F"` in `C:\wavee\fluent-gpu\src\FluentGpu.Controls\glyphs.json` (engine gates). Fallback without an engine change: Info / Attention / ThisPc, `crashContents` → List. |
| D2 | Developer items (FPS overlay, Simulate an update, Send a test crash report) are present and **greyed** while developer mode is off; they stop being `DeveloperOnly` catalog rows (only the two Appearance rows remain). |
| D3 | The crash-mode list is three left-aligned expander items, each `RadioButton.Create` + an indented caption (not one `RadioButtons` group: it has only a group-wide `isEnabled`, and Automatic alone must be disabled with its reason). |
| D4 | `wavee://open?route=settings&arg=<slug>` goes through a new `Shell.OnSettingsTabRequested` seam (mirrors `OnReportRequested`), never committed as a route with an Arg (`Shell.Dest` would show the Arg as the tab title; `SameSlot` would swallow a re-open). |
| D5 | The Log viewer card's live description is a `Signal<string>` bound into the card's description `TextEl` via `SettingsCard.Options.Parts` (`PartDescription`), fed by a 750 ms `UseInterval` that writes only when `Log.Version` or the minute moved, reading a new zero-copy `Log.Tally()`. One text node re-renders; no card remount. |
| D6 | `RouteKind.Logs` is a normal (not developer-only) route after `CaptureDiagnostics`, glyph `Icons.List`, registered from `Diagnostics.Install`; the page is a fresh `LogsPage` type (`Screens/LogsPage.cs` + `LogsPage.UI.cs`). |
| D7 | New loc namespaces `settings.privacy.*` (tab) and top-level `logs.*` (page); `settings.diag.*`, most of `settings.diagnostics.*`, `settings.logs.*` and the tab/list half of `crash.*` are deleted (the generated `Strings.*` consts vanish, so the compiler finds every straggler). `settings.diagnostics.refresh` / `openLogFolder` stay (used by the two diagnostics pages). |
| D8 | The viewer's Refresh command is dropped; sessions re-list on every page activation (`UseActivation`). |

### 3.1 The tab (ordinary scrolling tab; N8 "the one unscrolled tab" is retired)

```
SettingsPageView
└ ScrollView{ScrollKey="settings:privacy", Key="settings:scroll:privacy"} › ContentColumn(1000) › TabStack(gap 4)
  ├ SectionHeader("Privacy", Glyph("Shield"), "What leaves this PC, and the one thing you can opt into")
  ├ SettingsExpander Key="privacy.facts"  Header "What leaves this PC" · HeaderIcon RowGlyph("whatLeaves"=Globe) · Content ValueTag("No telemetry")
  │   Items = Item(Spotify, sub) · Item(Lyrics providers, sub) · Item(GitHub, sub) · Item(Wavee's crash service, sub)   (no controls)
  │   ItemsFooter = ExpanderPanel(Caption "Wavee has no analytics and no telemetry of its own.")
  ├ SettingsExpander Key="privacy.crash-reports"  Header "Crash reports" · HeaderIcon RowGlyph("crashReports"=StatusWarning) · Content ValueTag(mode)
  │   Items = ModeItem(Off) · ModeItem(Ask) · ModeItem(Auto, enabled: Uploader.Configured, else "Needs a Wavee build that can send reports")
  │         · Item("Include a memory snapshot", Toggle(Keys.CrashIncludeDump), isEnabled: mode!=Off, icon Camera)
  │         · Item("What a report contains", sub, icon Info)
  ├ Embed.Comp(CrashServiceCard) Key="privacy.crash-service"  SettingsCard "Your data on the crash service" · desc "… · 3f9c…2b1a" · [Copy id (subtle)] [Delete my data… (enabled: Configured; confirmed)]
  ├ SettingsCard "Privacy policy"  IsClickEnabled · ActionIcon Icons.OpenInNewWindow · OnClick OpenUri(Crash.PrivacyUrl)
  │
  ├ SectionHeader("Crash reports", Glyph("Warning"), "Saved on this PC; sending is always your call")
  ├ Embed.Comp(SavedReportsGroup) Key="privacy.reports"  [subscribes Crash.Host.ReportsVersion + Crash.Uploader.OutboxVersion]
  │   SettingsExpander Header "Saved reports" · Desc "3 sent · 1 not sent · 2 closed unexpectedly" | "No reports on this PC" · Content ValueTag("10 reports · 2 MB")
  │     Items[0] (QueuedCount()>0 only) = ExpanderPanel(InfoBar(Informational, "N reports waiting to send", actionButton: [Send now (accent)] [Discard]))
  │     Items[1..] = ReportRow(bundle) Key="crash:"+ReportId — Padding (58,8,16,8), MinHeight 44:
  │                  [dot 8] [stamp "g" / "Report xxxx-xxxx" 150w] [kind 56w] [what grow·ellipsis] [state 110w] [IconButton(Icons.More, Small) OnRealized→_anchors[id]]
  │                  "…" → overlay.Open(anchor, MenuFlyout[View · Copy · (Send…) · ─ · Delete], BottomEdgeAlignedRight)
  │     ItemsFooter = Caption "Reports older than the newest 10 are removed automatically." grow · [Open folder] · [Delete all (confirmed, enabled: Total>0)]
  │
  ├ SectionHeader("Logs", Glyph("Document"), "What Wavee writes while it runs")
  ├ Embed.Comp(LogViewerCard) Key="privacy.log-viewer"  SettingsCard "Log viewer" IsClickEnabled → GoTo(Route(Logs)) · Desc(bound) "This session: 4,096 events · 368 warnings · 4 errors · running 2 h 5 m"
  ├ Embed.Comp(DetailLevelGroup) Key="privacy.detail-level"  [subscribes Platform.SettingsChanged]
  │   SettingsExpander "Detail level" · Content ValueTag("Info · file Info") · Item("In the viewer", ComboBox(LogView.LevelNames,160) Key="privacy.level.viewer:"+(int)min) · Item("In the log file", ComboBox Key="privacy.level.file:"+(int)effectiveFile)
  ├ SettingsCard "Log files"  Desc from the writer's verified rule (fed Log.MaxFileBytes / Log.RetainedFiles) · [Open folder]
  ├ SettingsCard "Report a problem"  IsClickEnabled → Feedback.Open(ReportKind.Bug)
  │
  ├ SectionHeader("Tools", Glyph("Repair"), "Inspect the running app")
  ├ SettingsCard "Playback runtime" IsClickEnabled → GoTo(PlaybackDiagnostics) · SettingsCard "Spotify Connect" → GoTo(ConnectDiagnostics)
  ├ SettingsExpander Key="privacy.capture"  "Realtime capture" · Content Toggle(Keys.DealerArchiveEnabled, afterWrite RealtimeCaptureHost.OnSettingsChanged)
  │     Item("Open capture viewer", isClickEnabled → GoTo(CaptureDiagnostics)) · Item("Open capture folder", [Open folder])
  │
  ├ SectionHeader("Developer", Glyph("Code"), "For people working on Wavee itself")
  └ Embed.Comp(DeveloperGroup) Key="privacy.developer"  [reads Platform.Developer.Enabled/FpsOverlay signals + s_testCrashBusy]
      SettingsExpander "Developer mode" · Desc "Lyrics inspector, test notifications, FPS overlay" · Content ToggleSwitch(onChange: Platform.Developer.Set + Bump)
        Item("FPS overlay", ToggleSwitch → Platform.Developer.SetFpsOverlay, isEnabled dev) · Item("Simulate an update", [Run simulation], isEnabled dev)
        · Item("Send a test crash report", [Send test report], isEnabled dev && Configured && !busy)
```

### 3.2 The Logs page (route `logs`)

Owner decision 2026-10-02 (during verification): the page head follows the app's child-page idiom — a `BreadcrumbBar`
"Settings › Privacy & diagnostics › Logs" over `Design.Type.PageHero("Logs")` (Profile.Lists.Page.cs:246-264), no back
arrow and no eyebrow; the first two crumbs navigate, Esc still goes back. The tree below is updated accordingly.

```
LogsPageView : Component (Screens/LogsPage.UI.cs)
│ signals: _search _level _category _session _newestFirst _groupRepeats _wrap _refresh _visibleLimit _sessionsRev _expandedSeq   (LogView + LogCapturePolicy kept verbatim)
│ hooks:   UseContext(InputHooks.Current) · UseContext(Overlay.Service) · UsePost · UseEffect(RefreshSessions) · UseActivation(onActivated: RefreshSessions(force))
│          UseSignalEffect(session → reset + EnsureSessionLoaded) · UseInterval(750 ms, enabled: _session==0; bumps _refresh only when Log.Version moved) · UseMemo(MeasuredStackVirtualLayout 36)
└ BoxEl Key="logs-page" Grow Shrink MinWidth0 MinHeight0 Direction=1 ClipToBounds OnKeyDown=Esc→GoBack
  ├ HeaderBar Padding (36,16,36,8)  (Profile.Lists.Page.cs head shape)
  │   [BreadcrumbBar "Settings › Privacy & diagnostics › Logs" over PageHero "Logs"] … [picker caption] [ComboBox session, width 320, Key="logs:session:"+_sessionsRev] [Button "Open folder"]
  ├ Divider
  ├ Toolbar Padding (36,16,36,8) · Gap M · Wrap=true
  │   AutoSuggestBox(filter, grow 1, maxFillWidth 360, minHeight 34) · Segmented(LevelLabels with counts: "Warnings · 368") · ComboBox categories (180, Key="logs:cat:"+session+":"+n) · spacer
  │   · IconButton Copy · IconButton Export · IconButton More (OnRealized→_moreAnchor) → MenuFlyout[✓Newest first ✓Group repeats  Wrap long lines  Verbose | Clear view (live only) · Report this session…]
  ├ List card Margin (36,0,36,16) · Grow · FillCardSecondary · StrokeCardDefault · Radii.Card · Clip
  │   ColumnHeader Height 32: [12 chevron][6 dot][Time 92w: label + Chevron up/down · Role Button · OnClick flips _newestFirst, _expandedSeq=-1][Level 58][Category 96][Message grow]
  │   Divider · Body Key="logs:list:"+LogView.RemountKey(...)  ItemsView.Create(rows, RepeatLayout.Measured, ListOptions{SelectionMode None, IsItemInvokedEnabled, OnInvoked ToggleExpand, Scroll{ScrollKey="logs:scroll:"+session}})
  │          (loading: ProgressRing + "Reading session…"; empty: Search glyph + "No matching logs"; LogRow anatomy + expanded Fields/Exception CodeBlocks + meta line kept verbatim)
  │   Divider · Footer Padding (16,8,12,8): Caption "500 of 4,096 events · live" grow · HyperlinkButton "Load more" (Truncated) · MicroMeta "Capturing Info+ · file Info+"
```

Removed from the viewer: Refresh / Open-log-folder / Clear-view primary commands, the two InfoBadges, both level combos,
the `CommandBar`, and the Reports list beneath it.

## 4. File-by-file

### 4.1 New files

| File | Contents |
|---|---|
| `src/apps/Wavee/Screens/Settings.UI.Privacy.cs` | `partial class Settings`: `PrivacyDiagnosticsTab()` + the five groups; components `CrashServiceCard`, `SavedReportsGroup`, `LogViewerCard`, `DetailLevelGroup`, `DeveloperGroup`; `SendTestCrashReport()` + `static readonly Signal<bool> s_testCrashBusy`; helpers `ViewReport`, `CopyReport`, `ReportRow`, `OpenRowMenu`, `ModeItem`, `SetCrashMode`. |
| `src/apps/Wavee/Screens/Settings.Privacy.cs` | `Settings.PrivacyRules` (engine-free): `ReportTally Tally(...)`, `CanSend`, `HangSeconds`, `SentTimeLocal`, `ShortInstallId`, `Mb`, `KindKey`, `StateKey`, `ModeKey`. |
| `src/apps/Wavee/Screens/LogsPage.UI.cs` | `static partial class LogsPage { Page() => Embed.Comp(() => new LogsPageView()); sealed class LogsPageView … }` — §3.2. |
| `src/apps/Wavee/Screens/LogsPage.cs` | `LogsPage.Rules` (engine-free): `LevelLabels`, `WithCount`, `Thousands`, `IsLive`. |
| `src/apps/Wavee.Tests/PrivacyDiagnosticsRulesTests.cs`, `LogsPageRulesTests.cs` | §6. |
| `docs/plans/wavee/privacy-diagnostics-tab-implementation.md` | this plan, verbatim (CLAUDE.md "plans with real code"). |

### 4.2 Edited files (regions verified against the current source)

**Engine `C:\wavee\fluent-gpu\src\FluentGpu.Controls\glyphs.json`** (D1): `"Shield":"EA18"`, `"Warning":"E7BA"`, `"Repair":"E90F"`; `GlyphTableGenerator` regenerates `Icons.*`. Gate: `dotnet build src/FluentGpu.slnx` Debug + Release.

**`Screens/Settings.cs`**: `:33` enum `{ General, Appearance, Playback, Notifications, Storage, PrivacyDiagnostics, About }`; `:36` slugs `[…,"storage","privacy","about"]`; `:49-54` comment (five table-driven tabs, About has no rows); `:73-75` delete General's "Privacy & diagnostics" and "Developer" sections and `:106-122` their 13 rows; after `:175` append:

```csharp
new(Tab.PrivacyDiagnostics, "Privacy",       "Shield"),
new(Tab.PrivacyDiagnostics, "Crash reports", "Warning"),
new(Tab.PrivacyDiagnostics, "Logs",          "Document"),
new(Tab.PrivacyDiagnostics, "Tools",         "Repair"),
new(Tab.PrivacyDiagnostics, "Developer",     "Code"),
// Rows: only what paints a glyph (mode radios, the queue InfoBar, bundle rows, Detail-level and capture items don't).
new(Tab.PrivacyDiagnostics, "Privacy", "whatLeaves", "Globe"),  new(Tab.PrivacyDiagnostics, "Privacy", "crashReports", "StatusWarning"),
new(Tab.PrivacyDiagnostics, "Privacy", "crashDump", "Camera"),  new(Tab.PrivacyDiagnostics, "Privacy", "crashContents", "Info"),
new(Tab.PrivacyDiagnostics, "Privacy", "crashService", "Delete"), new(Tab.PrivacyDiagnostics, "Privacy", "privacyPolicy", "OpenInNewWindow"),
new(Tab.PrivacyDiagnostics, "Crash reports", "savedReports", "Folder"),
new(Tab.PrivacyDiagnostics, "Logs", "logViewer", "ViewList"), new(Tab.PrivacyDiagnostics, "Logs", "detailLevel", "Filter"),
new(Tab.PrivacyDiagnostics, "Logs", "logFiles", "Folder"),    new(Tab.PrivacyDiagnostics, "Logs", "reportProblem", "Attention"),
new(Tab.PrivacyDiagnostics, "Tools", "playbackRuntime", "MusicNote"), new(Tab.PrivacyDiagnostics, "Tools", "connectDiagnostics", "Devices"),
new(Tab.PrivacyDiagnostics, "Tools", "realtimeCapture", "RadioTower"),
new(Tab.PrivacyDiagnostics, "Developer", "developerMode", "Settings"),   // one of the two deliberate gears
new(Tab.PrivacyDiagnostics, "Developer", "fpsOverlay", "Clock"), new(Tab.PrivacyDiagnostics, "Developer", "simulateUpdate", "Refresh"),
new(Tab.PrivacyDiagnostics, "Developer", "sendTestCrashReport", "StatusWarning"),
```
Per-section uniqueness holds; cross-section repeats (Folder, StatusWarning, Devices, Clock, Refresh) are allowed by N3.

**`Screens/Settings.UI.cs`**: header `:2-4, :19-22`; `:46-52` `InstallScreens` adds `Shell.OnSettingsTabRequested = static slug => Open(TabFromSlug(slug));`; `:64-65` delete `LogsPanelBody`; `:103-106` add `private static partial Element PrivacyDiagnosticsTab();`; `:146-155` dispatch `Tab.PrivacyDiagnostics => PrivacyDiagnosticsTab()`; `:158-172` collapse to the single `ScrollView` branch; `:199` `Strings.Settings.Tabs.Privacy`; `:257-271` `Row(...)` gains `string? actionIcon = null`; `:329-346` `Glyph()` adds Shield/Warning/Repair; `:389-395` delete the two sections from `GeneralTab()`; `:482-560` delete `AddDeveloperRows`, `s_testCrashBusy`, `SendTestCrashReport`; `:810-825` delete `LogsTab()`.

**`Screens/Crash.UI.cs`**: header `:1-7`; add `internal static void OpenSendPrompt(IOverlayService? overlay, BundleInfo b) => OpenPrompt(overlay, b, fromList: true);`; delete `ShortInstallId` (:143, moves to `PrivacyRules`); `ComposePreview` (:149) → `internal`; `PrivacyUrl` (:373) → `internal const`; delete `:375-667` (`PrivacyRows` incl. `RowGap`, `ReportsList`).

**`Screens/Diagnostics.UI.cs`**: header; `:68` add `Shell.SetPage(Shell.RouteKind.Logs, static (in Shell.Route _) => LogsPage.Page());`; `:75` delete the `Settings.LogsPanelBody` assignment; `:133-135` delete `LogsPanel()`; `:263-705` delete `LogsPanelView` (+ `s_levelItems`, `ClickableBadge`); drop `using FluentGpu.WindowsApi.Dialogs;` (its only user was `ExportSession`). `PageFrame`/`Card`/`Row`/`OpenFolder`, `RuntimePageView`, `ConnectPageView`, lyrics inspector untouched. **`Diagnostics.Capture.UI.cs:219`** → `Strings.Settings.Privacy.Tools.OpenFolder`.

**`Shell/Shell.cs`**: `:45-49, :77-79` enum: insert `Logs,` after `CaptureDiagnostics`; `:197` after the Capture row `new(RouteKind.Logs, "logs", false, Strings.Nav.Logs, Icons.List, false, false, false),`; `:355` `CarriesDisplayName` excludes `RouteKind.Settings` (its Arg is a tab slug).

**`Shell/Shell.Host.cs`**: `:802` `public static Action<string>? OnSettingsTabRequested;`; `:763-765`:
```csharp
case DeepLinkKind.Open:
    // `route=settings&arg=<tab>`: the arg is a TAB SLUG, never a place of its own (D4).
    if (verb.Route.Kind == RouteKind.Settings && ArgOf(verb.Route) is { } slug && OnSettingsTabRequested is { } openTab)
    { openTab(slug); break; }
    GoTo(verb.Route);
    break;
```

**`Platform/Platform.cs`** (Log core, beside `Snapshot()` :721): zero-copy tally for the card (N12: no per-frame work, no allocation):
```csharp
public static void Tally(out int total, out int warnings, out int errors)
{
    total = warnings = errors = 0;
    lock (s_ringGate)
    {
        total = s_count;
        int start = (s_head - s_count + s_ring.Length) % s_ring.Length;   // mirror SnapshotLocked's walk exactly
        for (int i = 0; i < s_count; i++)
        {
            var level = s_ring[(start + i) % s_ring.Length].Level;
            if (level == WaveeLogLevel.Warning) warnings++; else if (level >= WaveeLogLevel.Error) errors++;
        }
    }
}
```
**`Platform/Platform.Host.cs`** (Wave G owns this file): the writer changes from §2.3 (G1 names via `LogFileNames`, G3 `LogRetentionPolicy` applied after each roll, G6 drop marker) plus `public static long MaxFileBytes`, `public static int RetentionDays`, `public static long RetentionBytes` getters so the Log files card and the Storage row never hardcode the rule. Also G-owned: `Platform.Settings.cs` (`WaveeLogSessions` → `WalkResult`, the new pure classes), `Diagnostics.Host.cs`, `Settings.Host.cs` (`DeleteOldLogs`, census), `Crash.Host.cs` / `Crash.Handler.cs` / `Crash.Bundles.cs` (G5), `Shell.Host.cs:206` (base path to the child) — the `:786` NUL trim (G10) is done by D, which owns `Shell.Host.cs`.

### 4.3 Deleted members
`Settings.LogsPanelBody`, `Settings.LogsTab`, `Settings.AddDeveloperRows`, `Settings.s_testCrashBusy` (field → Signal in the new file), `Settings.SendTestCrashReport` (recreated), `Crash.PrivacyRows`, `Crash.ReportsList`, `Crash.ShortInstallId`, `Diagnostics.LogsPanel`, `Diagnostics.LogsPanelView`, `Settings.Tab.Logs`, General catalog rows `crashMode…crashErase`, `developerMode…sendTestCrashReport`.

## 5. Code shapes (the contract for the implementing agents)

### 5.1 Per-row "…" menu (SavedReportsGroup)
```csharp
sealed class SavedReportsGroup : Component
{
    readonly Dictionary<string, NodeHandle> _anchors = new(StringComparer.Ordinal);
    OverlayHandle? _menu;

    public override Element Render()
    {
        var overlay = UseContext(Overlay.Service); var hooks = UseContext(InputHooks.Current); var post = UsePost();
        _ = Crash.Host.ReportsVersion.Value; _ = Crash.Uploader.OutboxVersion.Value;
        var bundles = Crash.Host.Bundles();                       // ≤ Crash.Files.KeepBundles (10); event-driven cold path
        var tally = PrivacyRules.Tally(bundles, BundleBytes);
        int queued = Crash.Uploader.QueuedCount(); bool configured = Crash.Uploader.Configured;

        var items = new List<Element>(bundles.Count + 1);
        if (queued > 0)
            items.Add(ExpanderPanel(InfoBar.Create(InfoBarSeverity.Informational, "",
                queued == 1 ? Loc.Get(R.QueuedOne) : Strings.Settings.Privacy.Reports.Queued(queued), isClosable: false,
                actionButton: HStack(Spacing.S,
                    Button.Accent(Loc.Get(R.SendNow), static () => Crash.Uploader.Drain(), isEnabled: configured),
                    Button.Standard(Loc.Get(R.Discard), static () => Crash.Uploader.DiscardQueue())))));
        foreach (var b in bundles) items.Add(ReportRow(b, overlay, hooks, post, configured) with { Key = "crash:" + b.Summary.ReportId });
        if (_anchors.Count > bundles.Count) _anchors.Clear();      // a deleted bundle; the rest re-capture on realize

        return SettingsExpander.Create(new SettingsExpander.Options
        {
            Header = Loc.Get(R.Saved),
            Description = tally.Total == 0 ? Loc.Get(R.SavedNone) : Strings.Settings.Privacy.Reports.SavedSummary(tally.Sent, tally.NotSent, tally.Closed),
            HeaderIcon = RowGlyph(Tab.PrivacyDiagnostics, "savedReports"),
            Content = ValueTag(tally.Total == 1 ? Strings.Settings.Privacy.Reports.SavedCountOne(PrivacyRules.Mb(tally.Bytes))
                                                : Strings.Settings.Privacy.Reports.SavedCount(tally.Total, PrivacyRules.Mb(tally.Bytes))),
            Items = items,
            ItemsFooter = new BoxEl
            {
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Padding = new Edges4(58f, Spacing.S, Spacing.L, Spacing.S),
                Children =
                [
                    Caption(Strings.Settings.Privacy.Reports.Retention(Crash.Files.KeepBundles)) with { Color = Tok.TextTertiary, Grow = 1f, MinWidth = 0f, Wrap = TextWrap.Wrap },
                    Button.Standard(Loc.Get(R.OpenFolder), static () => Diagnostics.OpenFolder(Crash.Files.Root(Crash.Host.LogFolder))),
                    Button.Standard(Loc.Get(R.DeleteAll), () => Controls.Confirm(overlay, Loc.Get(R.DeleteAllConfirmTitle), Loc.Get(R.DeleteAllConfirmBody),
                        Loc.Get(R.DeleteAll), static () => Crash.Host.DeleteAll()), isEnabled: tally.Total > 0),
                ],
            },
        }) with { Key = "privacy.reports" };
    }

    Element ReportRow(Crash.BundleInfo b, IOverlayService? overlay, InputHooks hooks, Action<Action> post, bool configured)
    {
        var s = b.Summary; string id = s.ReportId;
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, AlignSelf = FlexAlign.Stretch, MinHeight = 44f, Padding = new Edges4(58f, Spacing.S, Spacing.L, Spacing.S),
            Children =
            [
                new BoxEl { Width = 8f, Height = 8f, Corners = Radii.FullAll, Fill = DotFor(s.Kind), Shrink = 0f },
                new BoxEl { Direction = 1, Gap = 2f, Width = 150f, Shrink = 0f, Children =
                [
                    new TextEl(b.StampLocal.ToString("g", CultureInfo.CurrentCulture)) { Size = 12f, Color = Tok.TextSecondary },
                    new TextEl(Strings.Settings.Privacy.Reports.ReportId(Crash.ShortId(id))) { Size = 11f, Color = Tok.TextTertiary },
                ]},
                new TextEl(Loc.Get(PrivacyRules.KindKey(s.Kind))) { Size = 13f, Weight = 600, Width = 56f, Shrink = 0f },
                new TextEl(WhatText(s)) { Size = 13f, Color = Tok.TextSecondary, Grow = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                new TextEl(StateText(b)) { Size = 12f, Color = Tok.TextTertiary, Width = 110f, Shrink = 0f },
                ToolTip.Wrap(IconButton.Create(Icons.More, () => OpenRowMenu(b, overlay, hooks, post, configured), size: ControlSize.Small)
                    with { Shrink = 0f, OnRealized = h => _anchors[id] = h }, Loc.Get(R.RowMenu)),
            ],
        };
    }

    void OpenRowMenu(Crash.BundleInfo b, IOverlayService? overlay, InputHooks hooks, Action<Action> post, bool configured)
    {
        if (overlay is null) return;
        if (_menu is { IsOpen: true } open) { open.Close(); return; }
        string id = b.Summary.ReportId;
        var items = new List<MenuFlyoutItem>(5)
        {
            new(Loc.Get(R.View), Icons.OpenInNewWindow, true, () => ViewReport(b.ReportTxt)),
            new(Loc.Get(R.Copy), Icons.Copy, true, () => CopyReport(b, hooks, post)),
        };
        if (PrivacyRules.CanSend(b.Summary.Kind, b.Send.State, configured))
            items.Add(new(Loc.Get(R.Send), Icons.Forward, true, () => Crash.OpenSendPrompt(overlay, b)));
        items.Add(MenuFlyoutItem.Separator);
        items.Add(new(Loc.Get(R.Delete), Icons.Delete, true, () => Controls.Confirm(overlay, Loc.Get(R.DeleteConfirmTitle), Loc.Get(R.DeleteConfirmBody), Loc.Get(R.Delete), () => Crash.Host.Delete(b.Dir))));
        // Same anchored-flyout shape as Shell.UI.cs:1363 (history menu) and Settings.UI.Playback.cs:515 (manager).
        var h = overlay.Open(() => _anchors.TryGetValue(id, out var a) ? a : NodeHandle.Null, () => MenuFlyout.Create(items, () => _menu?.Close()),
            FlyoutPlacement.BottomEdgeAlignedRight, new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
        _menu = h; h.ClosedAction = () => { if (ReferenceEquals(_menu, h)) _menu = null; };
    }
}
```
`CopyReport` = today's `Crash.ReportsList.CopyReport` body (`Crash.Scrubber.RulesNow()` on the UI thread → `Crash.ComposePreview` off-thread → clipboard + `Notify.Say(Copied, dedupeKey "crash-copied:"+id)`); `ViewReport` = the notepad start.

### 5.2 Segmented labels with counts (LogsPage.Rules)
```csharp
public static string[] LevelLabels(string all, string info, string warnings, string errors, int warningCount, int errorCount)
    => [all, info, WithCount(warnings, warningCount), WithCount(errors, errorCount)];
public static string WithCount(string label, int count) => count > 0 ? label + " · " + count.ToString("N0", CultureInfo.InvariantCulture) : label;
// UI: SegmentedItem[] rebuilt every render — Segmented re-pushes Props, so labels follow the counts live; no Key remount, no badges.
```

### 5.3 Live description on the Log viewer card (D5)
```csharp
sealed class LogViewerCard : Component
{
    readonly Signal<string> _line = new(Compose());        // seeded at construction: no signal write in Render
    readonly TemplateParts _parts = new();
    long _seenVersion = -1, _seenMinute = -1;
    public LogViewerCard() => _parts.Set<TextEl>(SettingsCard.PartDescription, t => t with { Text = _line });
    public override Element Render()
    {
        UseInterval(() =>            // N12: every 750 ms, never per frame; write (and alloc) only when the log or the minute moved
        {
            long v = Log.Version, minute = Log.SinceStartMs / 60_000;
            if (v == _seenVersion && minute == _seenMinute) return;
            _seenVersion = v; _seenMinute = minute; _line.Value = Compose();
        }, 750f);
        return SettingsCard.Create(new SettingsCard.Options
        {
            Header = Loc.Get(Strings.Settings.Privacy.Logs.Viewer), Description = _line.Peek(), HeaderIcon = RowGlyph(Tab.PrivacyDiagnostics, "logViewer"),
            IsClickEnabled = true, IsActionIconVisible = true, OnClick = static () => Shell.GoTo(new Shell.Route(Shell.RouteKind.Logs)), Parts = _parts,
        });
    }
    static string Compose()
    {
        Log.Tally(out int total, out int warnings, out int errors);
        return Strings.Settings.Privacy.Logs.ViewerSub(Rules.Thousands(total), Rules.Thousands(warnings), Rules.Thousands(errors), Diagnostics.LogView.Uptime(TimeSpan.FromMilliseconds(Log.SinceStartMs)));
    }
}
```

### 5.4 Developer mode, live
```csharp
Content = ToggleSwitch.Create(new Signal<bool>(dev), onChange: static on => { Platform.Developer.Set(on); Bump(); }, style: SettingsCard.CompactToggleStyle()),
Items = [ Item(FpsOverlay…, ToggleSwitch.Create(new Signal<bool>(fps), onChange: static on => Platform.Developer.SetFpsOverlay(on), isEnabled: dev, …), isEnabled: dev, …),
          Item(SimulateUpdate…, Button.Standard(…, static () => Update.Host.SimulateUpdate(), isEnabled: dev), isEnabled: dev, …),
          Item(SendTestCrashReport…, Button.Standard(…, static () => SendTestCrashReport(), isEnabled: dev && canSend && !busy), isEnabled: dev && canSend, …) ]
// dev/fps read from Platform.Developer.Enabled/FpsOverlay signals (Platform.Settings.cs:85-105) — the ONE persist-then-publish setter;
// today's Toggle(key) wrote the store and left the signals stale until the next launch.
```

### 5.5 Crash-mode radio items
```csharp
static Element ModeItem(Crash.Reporting m, Crash.Reporting current, string label, string sub, bool enabled)
    => Item("", null, new BoxEl { Direction = 1, Gap = Spacing.XXS, MinWidth = 0f, Children =
       [ RadioButton.Create(label, isSelected: current == m, onChange: () => SetCrashMode(m), isEnabled: enabled),
         Caption(sub) with { Color = enabled ? Tok.TextSecondary : Tok.TextDisabled, Margin = new Edges4(28f, 0f, 0f, 0f), Wrap = TextWrap.Wrap, MaxLines = 2 } ] },
       SettingsCard.ContentAlignment.Left, isEnabled: enabled);   // Left = the card renders ONLY the content (SettingsCard.cs:143-144, 304-314)
static void SetCrashMode(Crash.Reporting m) { if ((Crash.Reporting)Platform.Settings.Get(Platform.Keys.CrashReporting) == m) return; Platform.Settings.Set(Platform.Keys.CrashReporting, (int)m); Bump(); }
// effective = Crash.ConsentPolicy.Effective(stored, Crash.Uploader.Configured) → ValueTag(Loc.Get(PrivacyRules.ModeKey(effective)))
```

### 5.6 Detail level (combos leave the viewer)
```csharp
_ = Platform.SettingsChanged.Value;   // the Logs page's Verbose toggle writes the same keys → the tag follows
Item(InViewer, …, ComboBox.Create(Diagnostics.LogView.LevelNames, new Signal<int>(Math.Clamp((int)min, 0, 4)), width: 160f,
     onChange: i => post(() => LogCapturePolicy.SetMinLevel(Platform.Settings, (WaveeLogLevel)Math.Clamp(i, 0, 4)))) with { Key = "privacy.level.viewer:" + (int)min }),
Item(InFile, …, ComboBox.Create(…, new Signal<int>(Math.Clamp((int)file, 0, 4)), …SetFileLevel…) with { Key = "privacy.level.file:" + (int)file }),
// ComboBox items/selection freeze at mount → keyed by the level VALUE; the write is posted so the click finishes before the remount.
```

### 5.7 Time header, Back/Esc, overflow
```csharp
Element TimeHeader(bool newestFirst, Action<Action> post) => new BoxEl
{
    Direction = 0, AlignItems = FlexAlign.Center, Gap = 4f, Width = 92f, Shrink = 0f, Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
    OnClick = () => post(() => { _newestFirst.Value = newestFirst ? 0 : 1; _expandedSeq.Value = -1; }),
    Children = [ Design.Type.MicroMeta(Loc.Get(L.ColTime)) with { Weight = 600, Color = Tok.TextTertiary }, Icon(newestFirst ? Icons.ChevronDown : Icons.ChevronUp, 10f, Tok.TextTertiary) ],
};
void OnPageKey(KeyEventArgs e) { if (e.Handled || e.KeyCode != Keys.Escape || !IsLive) return; e.Handled = true; GoBack(); }
static void GoBack() { if (Shell.CanBack.Peek()) Shell.GoBack(); else Settings.Open(Settings.Tab.PrivacyDiagnostics); }

IReadOnlyList<MenuFlyoutItem> OverflowItems(Action<Action> post, IOverlayService? overlay) =>   // built at open time: always live state
[
    MenuFlyoutItem.Toggle(Loc.Get(L.NewestFirst), newest, () => post(() => { _newestFirst.Value = newest ? 0 : 1; _expandedSeq.Value = -1; })),
    MenuFlyoutItem.Toggle(Loc.Get(L.GroupRepeats), group, () => post(() => { _groupRepeats.Value = group ? 0 : 1; _expandedSeq.Value = -1; })),
    MenuFlyoutItem.Toggle(Loc.Get(L.WrapLines), wrap, () => post(() => _wrap.Value = wrap ? 0 : 1)),
    MenuFlyoutItem.Toggle(Loc.Get(L.Verbose), verbose, () => post(() => { LogCapturePolicy.SetVerbose(Platform.Settings, !verbose); _refresh.Value++; })),
    MenuFlyoutItem.Separator,
    new(Loc.Get(L.ClearView), Icons.ClearText, live, () => Controls.Confirm(overlay, Loc.Get(L.ClearView), Loc.Get(L.ClearViewBody), Loc.Get(L.ClearView), () => { Log.ClearRing(); _refresh.Value++; })),
    new(Loc.Get(Strings.Report.ThisSession), Icons.Attention, true, () => Feedback.Open(Feedback.ReportKind.Bug, new Feedback.ReportPrefill(PastSessionId: SelectedPastSession() is { } s ? WaveeLogSessions.KeyOf(s) : null))),
];
```

### 5.8 `PrivacyRules`
```csharp
public static class PrivacyRules
{
    public readonly record struct ReportTally(int Total, int Sent, int NotSent, int Closed, long Bytes);
    public static ReportTally Tally(IReadOnlyList<Crash.BundleInfo> bundles, Func<Crash.BundleInfo, long> bytesOf);   // sent · not sent (NotSent/Queued/Failed on evidence) · closed (UncleanExit)
    public static bool CanSend(Crash.Kind kind, Crash.SendState state, bool configured) => configured && kind != Crash.Kind.UncleanExit && state is Crash.SendState.NotSent or Crash.SendState.Failed;
    public static int? HangSeconds(string exceptionMessage);                 // "no UI heartbeat for 23 s" → 23
    public static string SentTimeLocal(string? sentAtUtc, TimeSpan utcOffset); // ISO → "HH:mm"; unparsable → raw; null → ""
    public static string ShortInstallId(string id) => id.Length > 10 ? id[..4] + "…" + id[^4..] : id;
    public static string Mb(long bytes) => (bytes / 1_048_576.0).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
    public static string KindKey(Crash.Kind k); public static string? StateKey(Crash.Kind k, Crash.SendState s); public static string ModeKey(Crash.Reporting effective);
}
```

## 6. Strings (`assets/loc/en-US.json`)

Removed: `settings.tabs.logs`, `settings.logs.unavailable`, all `settings.diag.*`, all `settings.diagnostics.*` except `refresh`/`openLogFolder`, and these `crash.*`: `settingsSection, settingsSectionSub, mode, modeSub, modeOff, modeAsk, modeAuto, modeAutoNeedsService, includeDump, includeDumpSub, queued, queuedCount, sendNow, discard, saved, savedCount, openFolder, deleteAll, deleteAllConfirmTitle, deleteAllConfirmBody, privacy, eraseRow, eraseSub, eraseInstallId, eraseCopy, eraseButton, eraseConfirmTitle, eraseConfirmBody, eraseDone, eraseFailed, reports, reportsEmpty, kindCrash, kindExit, kindClosed, closedSub, hangWhat, stateSent, stateQueued, stateNotSent, stateFailed, sendButton, rowId, rowCopy, rowCopied, rowDelete, rowDeleteConfirmTitle, rowDeleteConfirmBody` (keep `kindHang`, `view` and every prompt/toast/recovery key; fix `crash.$comment`).
Added: `settings.tabs.privacy` "Privacy & diagnostics"; `nav.logs` "Logs"; the `settings.privacy` block (`facts`, `reports`, `logs`, `tools`, `developer`) and the top-level `logs` block — full key list with English text:

```json
"settings.privacy": {
  "facts": { "title":"Privacy", "subtitle":"What leaves this PC, and the one thing you can opt into",
    "whatLeaves":"What leaves this PC", "whatLeavesSub":"Spotify, lyrics providers and GitHub. Nothing else unless you send a report.", "noTelemetry":"No telemetry",
    "spotify":"Spotify", "spotifySub":"Sign-in, catalogue, playlists, Connect and streaming — including the play events the official client sends, which keep Recently Played and your listening history working",
    "lyrics":"Lyrics providers", "lyricsSub":"Only when a track has no Spotify lyrics: the title, artist and duration — never your account",
    "github":"GitHub", "githubSub":"Update checks and release notes, as plain anonymous downloads",
    "crashService":"Wavee's crash service", "crashServiceSub":"crash.cproducts.dev — only when reports are on, or when you press Send on one",
    "footer":"Wavee has no analytics and no telemetry of its own.",
    "crashReports":"Crash reports", "crashReportsSub":"Off by default. Ask each time shows you the full report before anything is sent.",
    "modeOff":"Off", "modeOffSub":"Reports are saved on this PC and nothing is sent", "modeAsk":"Ask each time", "modeAskSub":"After a crash, Wavee shows you the report and you decide",
    "modeAuto":"Automatic", "modeAutoSub":"Reports are sent without asking; offline, they wait until you're back online", "modeAutoNeedsService":"Needs a Wavee build that can send reports",
    "includeDump":"Include a memory snapshot", "includeDumpSub":"Helps with hard crashes. May contain small pieces of what was on screen.",
    "contents":"What a report contains", "contentsSub":"The error and where in Wavee it happened; Wavee, Windows and graphics-card versions; the last 300 log lines with personal details removed; a random install id. Never your Spotify account, password, name or files.",
    "yourData":"Your data on the crash service", "yourDataSub":"Reports are tied to a random install id, never to you · {id}", "copyId":"Copy id",
    "deleteMyData":"Delete my data…", "deleteMyDataConfirmTitle":"Delete my data?", "deleteMyDataConfirmBody":"Deletes every report this PC ever sent to the crash service. Reporting stays as it is.",
    "deleteMyDataDone":"Deleted {count} reports from the crash service", "deleteMyDataFailed":"Couldn't reach the crash service — try again later",
    "policy":"Privacy policy", "policySub":"What Wavee stores, what leaves this PC and your rights, in full" },
  "reports": { "title":"Crash reports", "subtitle":"Saved on this PC; sending is always your call", "saved":"Saved reports",
    "savedSummary":"{sent} sent · {unsent} not sent · {closed} closed unexpectedly", "savedNone":"No reports on this PC", "savedCount":"{count} reports · {size}", "savedCountOne":"1 report · {size}",
    "queued":"{count} reports waiting to send", "queuedOne":"1 report waiting to send", "sendNow":"Send now", "discard":"Discard", "reportId":"Report {id}",
    "kindCrash":"Crash", "kindHang":"Hang", "kindExit":"Exit", "kindClosed":"Closed", "closedWhat":"Windows or Task Manager ended Wavee", "hangWhat":"Stopped responding for {seconds} s",
    "stateSent":"Sent {time}", "stateQueued":"Waiting to send", "stateNotSent":"Not sent", "stateFailed":"Couldn't send",
    "rowMenu":"More actions", "view":"View", "copy":"Copy", "copied":"Report copied", "send":"Send…", "delete":"Delete",
    "deleteConfirmTitle":"Delete this report?", "deleteConfirmBody":"Deletes it from this PC. If it was sent, the crash service keeps its copy until it's 90 days old — Delete my data removes that too.",
    "retention":"Reports older than the newest {count} are removed automatically.", "openFolder":"Open folder", "deleteAll":"Delete all",
    "deleteAllConfirmTitle":"Delete all saved reports?", "deleteAllConfirmBody":"Deletes every crash report saved on this PC. Reports already sent stay on the crash service until they're 90 days old — Delete my data removes them sooner." },
  "logs": { "title":"Logs", "subtitle":"What Wavee writes while it runs", "viewer":"Log viewer", "viewerSub":"This session: {events} events · {warnings} warnings · {errors} errors · running {uptime}",
    "detailLevel":"Detail level", "detailLevelSub":"How much Wavee records. The file never records more than the viewer captures.", "detailLevelValue":"{viewer} · file {file}",
    "inViewer":"In the viewer", "inViewerSub":"What the in-app log viewer captures for this session", "inFile":"In the log file", "inFileSub":"What is written to disk; Warning or higher keeps the file small",
    "files":"Log files", "filesSub":"logs\\wavee-<date>.log · a new file each day or at {mb} MB · kept {days} days, up to {cap} MB", "openFolder":"Open folder",
    "sessionsReading":"Reading past sessions…", "sessionsNone":"No past sessions on disk — log files are kept {days} days", "sessionsFailed":"Couldn't read the log folder",
    "exportSessionLive":"The whole session, with timestamps", "footerPastCapped":"last {loaded} of {total} events · log file",
    "report":"Report a problem", "reportSub":"Opens a prefilled GitHub form and copies a redacted report to your clipboard" },
  "tools": { "title":"Tools", "subtitle":"Inspect the running app", "playbackRuntime":"Playback runtime", "playbackRuntimeSub":"Why local playback is or isn't ready: the runtime, its signature and the installed modules",
    "connect":"Spotify Connect", "connectSub":"Who owns playback right now, the last cluster and the last state round trip",
    "capture":"Realtime capture", "captureSub":"A redacted flight recorder of actions, playback, HTTP and dealer frames · logs\\capture\\ · up to 2 GB, 90 days",
    "openCaptureViewer":"Open capture viewer", "openCaptureViewerSub":"Recent causal roots and anomalies, without leaving the app",
    "openCaptureFolder":"Open capture folder", "openCaptureFolderSub":"The capture segments (.idx/.blob) in Explorer", "openFolder":"Open folder" },
  "developer": { "title":"Developer", "subtitle":"For people working on Wavee itself", "mode":"Developer mode", "modeSub":"Lyrics inspector, test notifications, FPS overlay",
    "fpsOverlay":"FPS overlay", "fpsOverlaySub":"Frame timing in the corner of the window",
    "simulateUpdate":"Simulate an update", "simulateUpdateSub":"Walks the update state machine locally — toast, notification centre and About — with no network and nothing installed", "simulateUpdateButton":"Run simulation",
    "sendTestCrashReport":"Send a test crash report", "sendTestCrashReportSub":"Builds a real crash report from a caught test exception and sends it to the crash service. Wavee keeps running.",
    "sendTestCrashReportUnavailable":"This build can't send crash reports — it has no crash service set up.", "sendTestCrashReportButton":"Send test report" }
},
"logs": { "eyebrow":"Privacy & diagnostics", "title":"Logs", "back":"Back", "thisSession":"This session · pid {pid} · running {uptime}", "openFolder":"Open folder",
  "filterPlaceholder":"Filter logs", "levelAll":"All", "levelInfo":"Info+", "levelWarnings":"Warnings", "levelErrors":"Errors", "allCategories":"All categories",
  "copyVisible":"Copy visible", "exportSession":"Export session", "exportLogText":"Log text", "exportAllFiles":"All files", "more":"More",
  "newestFirst":"Newest first", "groupRepeats":"Group repeats", "wrapLines":"Wrap long lines", "verbose":"Verbose",
  "clearView":"Clear view", "clearViewBody":"Clear the in-memory log ring for this session? The log file on disk is not affected.",
  "colTime":"Time", "colLevel":"Level", "colCategory":"Category", "colMessage":"Message", "fields":"Fields", "exception":"Exception",
  "footerLive":"{shown} of {total} events · live", "footerPast":"{shown} of {total} events · log file", "loadMore":"Load more", "captureCaption":"Capturing {capture}+ · file {file}+",
  "loadingSession":"Reading session from the log file…", "emptyFilter":"No matching logs" }
```
"Report this session…" reuses `report.thisSession`. `nl.json` / `ko-KR.json`: replace the stale `settings.tabs.diagnostics` with `"privacy": "Privacy en diagnostiek"` / `"개인정보 및 진단"` and add `nav.logs` `"Logboeken"` / `"로그"`; nothing else (both languages are disabled in the picker and fall back to en-US).

## 7. Tests

| File | Change |
|---|---|
| `SettingsCatalogTests.cs` | `Scope_IsTheFourTableDrivenTabs` → five tabs (+PrivacyDiagnostics); `Only_the_four_declared_rows_are_developer_only` → the two Appearance rows; drop `[InlineData(General,"simulateUpdate")]`; the two deliberate gears → `[(Appearance,"npvStyle"), (PrivacyDiagnostics,"developerMode")]`; `TabSlugs_AreOneToOneWithTheTabs` adds `TabFromSlug("privacy") == PrivacyDiagnostics` and `TabFromSlug("logs") == General`; new `ThePrivacyTab_HasItsFiveGroupsInOrder_AndGeneralLostItsTwo`, `ThePrivacyTab_SectionGlyphs_AreAllDistinct`. |
| `CrashTestReportTests.cs:113-114` | `RowVisible(PrivacyDiagnostics,"sendTestCrashReport", dev:false/true)` both true (present, greyed — D2). |
| `ShellRoutesTests.cs` | row count 36; `[InlineData("logs", RouteKind.Logs)]`; new `Logs_is_a_user_route_with_its_own_label`; `DeepLinkTests`: `A_logs_deep_link_opens_without_developer_mode`, `A_settings_deep_link_keeps_its_tab_slug_in_the_arg_and_never_as_a_title`. |
| `PrivacyDiagnosticsRulesTests.cs` (new) | `Tally_counts_sent_unsent_and_closed_and_sums_bytes`, `CanSend_needs_a_configured_build_evidence_and_an_unsent_or_failed_state` (theory), `HangSeconds_reads_the_handlers_wording_and_rejects_anything_else`, `SentTimeLocal_renders_in_the_callers_offset_and_degrades_to_raw_text`, `ShortInstallId_keeps_four_and_four`, `Mb_formats_binary_megabytes_invariant`, `Kind_state_and_mode_keys_cover_the_matrix`. |
| `LogsPageRulesTests.cs` (new) | `Level_labels_carry_a_count_only_when_nonzero_with_thousands_separators`, `Thousands_is_invariant`. |
| `PlatformWave6Tests.cs › DeveloperModeTests` | `SetFpsOverlay_persists_then_publishes`. |
| `DiagnosticsCoreTests.cs › LogViewTests` | unchanged; `Tally_matches_snapshot_counts` inside the `PlatformCollection` (a few `Log.Warn/Error` then compare with `Log.Snapshot()`). |
| Audit tests | from §2 when it reports (sessions discovery, retention/delete-old-logs rules, flush-on-exit). |

## 8. Docs and CHANGELOG

- **CHANGELOG.md `[0.3.0] - unreleased`** (edit the existing bullets in place; no `(#n)` unless the owner files an issue — never invent one): `:49-50` "… Settings › Privacy & diagnostics › Crash reports lists every saved report; each row's menu offers View, Copy, Send and Delete."; `:64` "Settings › Privacy & diagnostics › Developer can now build a real crash report …"; **Added** "A Privacy & diagnostics tab." (what leaves this PC; the three-way crash-report list with the snapshot switch beneath; one "…" menu per saved report; the session's counts; the two detail levels; Playback runtime, Spotify Connect and Realtime capture under Tools; the developer switches together; Delete my data… and the policy beside the install id); **Added** "The log viewer is a page of its own." (opens from the tab or `wavee://open?route=logs`, Back/Esc, session picker and Open folder in the header, one toolbar with counts in the level labels, a "…" menu, sortable Time header, footer); **Changed** "Developer mode and the FPS overlay apply at once."; **Changed** "The capture and file log levels left the viewer."
  Audit bullets (Wave G): **Fixed** "Storage › Delete old logs no longer deletes the file Wavee is writing to." · **Changed** "Log files are kept for 7 days (up to 250 MB) instead of the newest 7 files, and a size roll is named `wavee-yyyyMMdd-HHmmss.log` in local time." · **Fixed** "A crash report's log tail is the day's file at the moment of the crash, flushed first, so it holds the lines that led up to it — not yesterday's file." · **Fixed** "The log viewer's session picker says when it is reading, when there are no past sessions on disk, and when the log folder could not be read; every walk logs one `log.sessions.listed` line." · **Changed** "Memory samples and the engine's pacing lines are no longer logged as warnings." · **Fixed** "Export session on the live log writes the whole session with timestamps; the past-session footer says how much of it is shown." · **Fixed** "A deep link that arrives with a trailing NUL is trimmed before it is parsed."
- **PRIVACY.md**: `:22` logs row → the audited writer rule; `:27` "Settings › Diagnostics" → "Settings › Privacy & diagnostics › Tools › Realtime capture"; `:130-134` "Settings › Logs › Reports" → "Settings › Privacy & diagnostics › Crash reports › Saved reports", the four buttons → "Each report's … menu offers View, Copy, Send, Delete".
- **docs/guide/crash-diagnostics.md** `:152, :161, :297, :469`; **.github/ISSUE_TEMPLATE/crash_report.yml** `:73, :82`; **bug_report.yml** `:73, :81` (already stale; Copy diagnostics info stays in About — flag to the owner); **.claude/skills/wavee/deep-linking.md** `:29` (`settings` arg = tab slug; add `logs`); **SKILL.md:25**, **audio-handoff.md:51**; **.claude/skills/github-triage/SKILL.md:93**; **docs/guide/releasing-wavee.md** `:138, :243, :706`; **docs/plans/wavee/wavee-0.3-ui/27-settings-and-diagnostics.md** §0: dated drift notes under N8 (retired; the viewer is route `logs`) and N3 (five table-driven tabs).

## 9. Sequencing and verification

**Wave 0 (orchestrator)**: engine `glyphs.json` +3 names → `dotnet build src/FluentGpu.slnx` Debug + Release in `C:\wavee\fluent-gpu` (or choose the fallback glyphs).
**Wave 0b (orchestrator, before any fix)**: the `--log-sessions` headless probe (§2.1) against the real folder and a packaged LocalCache; record files/lines/sessions/ms or the exception in the repo plan file.
**Wave 1 (parallel sonnet subagents, disjoint files; this plan is the contract)**: **A** `Settings.cs` + `Settings.UI.cs` + `Platform.cs` `Log.Tally` · **B** new `Settings.UI.Privacy.cs` + `Settings.Privacy.cs` + `Crash.UI.cs` deletions/doors · **C** new `LogsPage.UI.cs` + `LogsPage.cs` + `Diagnostics.UI.cs` + `Diagnostics.Capture.UI.cs:219` (consumes G's `WalkResult` / `SessionPickerState` / `LogView.ExportText` / `PastFooter` — signatures fixed in §2.3) · **D** `Shell.cs` + `Shell.Host.cs` (+ the `:786` NUL trim) + the three loc files · **E** tests (§7 + §2.3) · **F** docs/CHANGELOG/templates/skills + the repo plan file · **G** the audit fixes (§2.3): `Platform.Host.cs`, `Platform.Settings.cs`, `Diagnostics.Host.cs`, `Settings.Host.cs`, `Crash.Host.cs` / `Crash.Handler.cs` / `Crash.Bundles.cs`, PRIVACY.md `:22`.
**Wave 2 (orchestrator only)**: `dotnet build Wavee.slnx` and `-c Release`; `dotnet test src/apps/Wavee.Tests/Wavee.Tests.csproj` Debug and Release; `dotnet run --project src/apps/Wavee -- --fake`; then a real run for the writer checks and the probe again.

Manual checks at 1717×1150 @150 % with the right panel open, and at 1440×900; dark and light:
1. Seven tabs; Privacy & diagnostics scrolls, 1000-DIP left column, 4/32-DIP rhythm; every section glyph paints (no `settings.glyph.unmapped`).
2. Expanders collapsed; headers answer: "No telemetry", the mode, "N reports · X MB", "Info · file Info".
3. Crash reports: each mode → tag follows; on a `dotnet run` build Automatic is greyed with its reason; the snapshot toggle greys when Off.
4. Saved reports: empty state; after `--crash-probe throw` rows appear with the "…" menu; Send… opens "Send this report?"; Delete / Delete all confirm with Close default (N11); the queued InfoBar shows while `outbox` is non-empty.
5. Log viewer card: counts and uptime move without opening the page; idle frame time flat with the FPS overlay on (N12).
6. Logs page: tab title "Logs"; Back and Esc return to Settings › Privacy & diagnostics with its scroll offset (N7); a deep-linked `wavee://open?route=logs` with no back stack lands on the tab; session combo lists the previous run after a second launch; filter; segment counts; category combo keyed per session; Copy/Export; every overflow toggle; Time header flips order; Load more; footer; Clear view disabled on a past session; Report this session… carries the past-session key.
7. At 1145×767 with the panel open the toolbar wraps and nothing clips.
8. `wavee://open?route=settings&arg=privacy` selects the tab; the strip still says "Settings".
9. Developer mode on → the rail's lyrics-inspector glyph and Notifications' "Send a test event" rows appear immediately; FPS overlay toggles live; off → items grey.
10. Realtime capture toggle logs started/stopped; Open capture viewer navigates.
11. Writer checks from §2: the probe lists the sessions; a second launch shows the first in the picker with the status caption gone; force a 10 MB roll (Verbose on, scroll a big playlist) → `wavee-yyyyMMdd-HHmmss.log` in local time; Storage › Delete old logs keeps today's file and the writer keeps logging into it; `--crash-probe throw` after a `Log.Warn` burst → `log-tail.txt` ends with those lines; `mem.sample` no longer fills the Warnings count; Export on the live session writes `t=` lines.

## 10. Open questions (recommended default first)

1. **Concept** A / B / C from the artifact — plan assumes A.
2. **Engine glyphs** Shield / Warning / Repair: add them (default) or use the Info / Attention / ThisPc fallback.
3. **Issue refs**: none on the new bullets unless the owner files a "Privacy & diagnostics tab" issue (then `(#n)` + `Fixes #n`).
4. **Crash-mode list** as three radio items (default) vs one `RadioButtons` group (roving focus, but no per-item disable).
5. **Developer items greyed, not composed away** (default, D2) — the catalog's developer-only set shrinks to the two Appearance rows.
6. **Deep-link seam** `Shell.OnSettingsTabRequested` + `Settings` excluded from `CarriesDisplayName` (default both).
7. **`Log.Tally()`** in the logger core (default) vs `Log.Snapshot()` per changed version.
8. **Uptime wording** "2 h 5 m" (tested `LogView.Uptime`, default) vs "2 h 05 m".
9. **Discard** (drop queued sends) unconfirmed as today (it deletes nothing).
10. **Esc on the Logs page** only with focus inside the page (same as the customizer); Back is the first tab stop.
11. "Report a problem" stays in About as well (default yes).
12. **Retention rule** 7 days + 250 MB cap, active file always kept (default) — or keep "newest 7 files" and only fix the live-file deletion.
13. **Audit scope**: Wave G as listed (G1–G10) in this change, with #8 (faithful past-session parse), #15 and #17 as follow-ups (default) — or pull #8 in now so past sessions show Fields/Exception like the live ring.
