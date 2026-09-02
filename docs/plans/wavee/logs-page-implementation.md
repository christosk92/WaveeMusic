# Logs page — full-height CommandBar log viewer

Approved 2026-09-02 (session plan: onboarding v3 / settings regroup / logs page / dialog fade). Context and the cross-workstream sequencing live in the sibling docs of the same date; this file carries the workstream's real code shapes, component trees and wireframes.

## Workstream C — Logs page rebuild (`Settings › Logs`)

### C.0 Decisions
- **Session picker** stays a `ComboBox` (title + description rows) but is remounted with `Key = "logs:session:" + _sessionsRev` — the engine's `ComboBox.Create` (`ComboBox.cs:142-158`) re-pushes only `EnabledProps`, so a Key remount is the documented cure. **No engine change.** Same for the category combo and the `CommandBar` (its command lists are frozen fields — remount on `BarKey()` = toggle/level/live state; `Invoke` closures read component fields at click time, never captured row arrays).
- **Single-click expand**: `ListOptions { SelectionMode = None, IsItemInvokedEnabled = true, Selector = SelectorVisual.None }` — with `None`, Tap invokes (`ItemsView.cs:1416-1423`); hover via `.Interactive(Interaction.ListRow)` on the row. Chevron = `Icons.ChevronRight` with `Rotation = expanded ? 90 : 0` + `Transition = MotionTok.DisclosureChevron` (fallback: glyph swap).
- **Verbose** = a primary `AppBarToggleButton` ("Verbose", `Icons.Code`) → `MinLevel = Trace` / build default, persisted as `-1`/`0`. Capture level / File log level radio flyouts live in the overflow.
- **Env overrides deleted** (`WAVEE_LOG_LEVEL`, `WAVEE_LOG_FILE_LEVEL`, `WAVEE_LOG_RING`) — CLAUDE.md forbids env switches.
- **Pure model** in `Diagnostics/` (already source-included by `Wavee.Tests.csproj:92` glob — no csproj edit).
- Categories derived from the loaded entries (the hard-coded `s_categories` list is stale).
- The 750 ms live poll + list remount-on-visible-set-change idiom (`DiagnosticsPanel.cs:481-489`) stays.

### C.1 Wireframe
```
│ General  Appearance  Playback  Notifications  Storage  [Logs]  About                                    │
│ ┌ This session               ▾ ┐  ⟳ Refresh  ⎘ Copy  ⤓ Export  ▭ Folder  ✕ Clear │ ⇅ Newest ▤ Group ⟨⟩ Verbose  … │
│ │ pid 31544 · running 2 h 05 m │                                                                                  │
│ └──────────────────────────────┘                                                                                  │
│ 🔍 Filter logs ………………………………………  [ All │ Info+ │ Warnings │ Errors ]  (⚠ 3) (⛔ 1)     ┌ All categories ▾ ┐         │
│ ┌────────────────────────────────────────────────────────────────────────────────────────────────────────────┐   │
│ │ ›  18:24:27.445  INFO   connect    dealer connected                                                        │   │
│ │ ›  18:24:28.102  DEBUG  playback   prepared next uid=… (×12)                                               │   │
│ │ ⌄  18:24:29.000  WARN   lyrics     no synced lyrics for track                                              │   │
│ │    Fields     ┌ uri=spotify:track:…  provider=musixmatch  elapsed=41ms ─────────────────────── Copy ┐     │   │
│ │    Exception  ┌ System.Net.Http.HttpRequestException: … ─────────────────────────────────────── Copy ┐     │   │
│ │    #812 · lyrics.fetch · tid 4 · op 7f2a · 41 ms                                                           │   │
│ │ ›  18:24:31.774  ERROR  audio      playback failed                     (virtualized, all remaining height) │   │
│ ├────────────────────────────────────────────────────────────────────────────────────────────────────────────┤   │
│ │ Showing 500 of 3 214 events · live ring        Load more                     Capturing Trace+ · file Info+ │   │
│ └────────────────────────────────────────────────────────────────────────────────────────────────────────────┘   │
Overflow (…): ☐ Wrap long lines │ Report this session… │ Capture level ▸ (Trace…Error radios) │ File log level ▸
```

### C.2 Component tree
```
SettingsPage (tab == TabLogs → the existing unconstrained no-ScrollView lane, SettingsPage.cs:132-138)
└─ LogsPanel(settings)                                  NEW Features/Shell/LogsPanel.cs   BoxEl{Grow,Shrink,MinHeight=0,Dir=1,Gap=S}
   ├─ HeaderRow {Dir=0,Align=Center,MinHeight=48,Shrink=0}
   │  ├─ ComboBox.Create(labels,_session,width:320,itemDescriptions:subs)  with{Key="logs:session:"+_sessionsRev}
   │  └─ CommandBar.Create(primary, secondary)                             with{Key="logs:bar:"+BarKey()}
   │       primary : Refresh · Copy visible · Export… · Open log folder · Clear view(live) · Sep · [T]Newest · [T]Group · [T]Verbose
   │       secondary: [T]Wrap long lines · Sep · Report this session… · Sep · Capture level ▸ radios · File log level ▸ radios
   ├─ FilterRow {Dir=0,Align=Center,MinHeight=40,Shrink=0,Wrap}
   │  ├─ AutoSuggestBox.Create(placeholder, grow:1, text:_search)
   │  ├─ Segmented.Create(s_levelItems, _level)  + ClickableBadge(InfoBadge.Count(warn,Caution)) + (err,Critical)
   │  └─ ComboBox.Create(["All categories",…categories], _category, width:180) with{Key="logs:cat:"+session+":"+categories.Length}
   └─ Card {Grow,Shrink,MinHeight=0,Corners=Card,Fill=FillCardSecondary,Border,ClipToBounds}
      ├─ LogBody: loading → ProgressRing; empty → Icon(Search)+EmptyFilter; else
      │   BoxEl{Key="logs:list:"+LogView.RemountKey(...)} ─ ItemsView.Create(rows.Length, i=>LogRow(rows[i]), RepeatLayout.Measured(MeasuredStackVirtualLayout 36f),
      │       new ListOptions{SelectionMode=None, IsItemInvokedEnabled=true, OnInvoked=ToggleExpand, Selector=SelectorVisual.None, Controller=_listCtrl, Grow=1, Scroll=new{ScrollKey}})
      │     LogRow = {Dir=1}
      │       ├─ Line {Dir=0,MinHeight=36,Pad M}.Interactive(Interaction.ListRow)  Fill = expanded ? FillSubtleSecondary : transparent
      │       │   Chevron(Rotation) · SeverityDot · Time(mono,92) · LevelPill(58×22) · Category(mono,96) · Message{Grow, Wrap=(expanded||_wrap)} · RepeatBadge
      │       └─ expanded ? Detail{Pad=(44,0,M,S)}: Section("Fields", CodeBlock.Create(fieldText, copyable:true, fontSize:12)) · Section("Exception", CodeBlock…) · MetaLine(mono,tertiary)
      ├─ Divider()
      └─ Footer: TextEl(FooterLive/FooterPast(shown,total)){Grow} · truncated ? HyperlinkButton(LoadMore, +500) · TextEl(CaptureCaption(min, effectiveFile))
```

### C.3 Pure model — NEW `Diagnostics/LogView.cs` (System + `IWaveeLog.cs` types only)
```csharp
public enum LogLevelBucket : byte { All, InfoPlus, Warnings, Errors }          // == Segmented index
public readonly record struct LogViewRow(WaveeLogEntry Entry, int Repeat);
public sealed record LogViewQuery(LogLevelBucket Level = All, string? Category = null, string Search = "",
                                  bool NewestFirst = true, bool GroupRepeats = true, int Cap = LogView.PageRows);
public sealed record LogViewResult(LogViewRow[] Rows, int Total, int WarningCount, int ErrorCount, bool Truncated) { int Shown => Rows.Length; static Empty; }
static class LogView
{
    public const int PageRows = 500, MaxRows = 2000;
    public static readonly string[] LevelNames = ["Trace","Debug","Info","Warning","Error"];
    public static LogViewResult Build(ReadOnlySpan<WaveeLogEntry> entries, LogViewQuery q);   // order → level → category → search → adjacent-repeat grouping → Cap (Truncated); counts over ALL entries
    public static bool PassesLevel(WaveeLogLevel level, LogLevelBucket bucket);               // moves DiagnosticsPanel.cs:652-658
    public static bool Matches(in WaveeLogEntry e, string query);                             // :660-671, OrdinalIgnoreCase over category/eventId/message/op/fields
    public static bool IsRepeatOf(in WaveeLogEntry a, in WaveeLogEntry b);                    // level+category+eventId+message
    public static string[] Categories(ReadOnlySpan<WaveeLogEntry> entries);                   // distinct, case-insensitive, sorted
    public static int CategoryIndex(string[] categories, string? category);                  // 0 = All
    public static string FieldText(WaveeLogField[]? fields);                                  // one field per line
    public static string CopyText(ReadOnlySpan<LogViewRow> rows);                             // :685-698
    public static string FormatTime(long unixMs, TimeSpan utcOffset);                         // "HH:mm:ss.fff", "—" for 0
    public static string SessionLabel(long startUnixMs, int pid, int entryCount, TimeSpan utcOffset);
    public static string Uptime(TimeSpan up);                                                 // "2 h 5 m" / "12 min" / "just now"
    public static string MetaLine(in WaveeLogEntry e);                                        // "#812 · lyrics.fetch · tid 4 · op 7f2a · 41 ms", omits empties
    public static int IndexOfSequence(LogViewRow[] rows, long seq);
    public static string RemountKey(int session, LogViewQuery q, int shown);                  // moves :481-486
}
```
NEW `Diagnostics/LogCapturePolicy.cs` (System + `IAppSettings`):
```csharp
static class LogCapturePolicy
{
    public static WaveeLogLevel BuildDefaultMinLevel => /* #if DEBUG Debug #else Info */;
    public const WaveeLogLevel BuildDefaultFileLevel = WaveeLogLevel.Info;
    public static WaveeLogLevel Resolve(int setting, WaveeLogLevel buildDefault);      // -1 → default; clamp 0..4
    public static int ToSetting(WaveeLogLevel level, WaveeLogLevel buildDefault);      // == default → -1
    public static bool IsVerbose(WaveeLogLevel min) => min <= WaveeLogLevel.Debug;
    public static WaveeLogLevel VerboseTarget(bool on, WaveeLogLevel buildDefault) => on ? Trace : buildDefault;
    public static WaveeLogLevel EffectiveFileLevel(WaveeLogLevel min, WaveeLogLevel file) => file < min ? min : file;   // WaveeLog.cs:56-58 upward-only
    public static void SetMinLevel(WaveeLog log, IAppSettings? s, WaveeLogLevel level, WaveeLogLevel buildDefault);   // apply + persist (the panel's only writer)
    public static void SetFileLevel(WaveeLog log, IAppSettings? s, WaveeLogLevel level);
    public static void SetVerbose(WaveeLog log, IAppSettings? s, bool on, WaveeLogLevel buildDefault);
}
```

### C.4 Edits
| File | Change |
|---|---|
| NEW `Features/Shell/LogsPanel.cs` | The component in C.2. State signals: `_search, _level, _category, _session, _newestFirst, _groupRepeats, _wrap, _refresh, _visibleLimit, _sessionsRev, _levelsRev, _expandedSeq`; fields `_sessions`, `_sessionEntries`, `_rows`, `_categories`, `ItemsViewController _listCtrl`. Moves from `DiagnosticsPanel.cs`: `RefreshSessions` `:214-235` (bumps `_sessionsRev`), `EnsureSessionLoaded` `:237-256`, `SelectedPastSession` `:422-427`, `ExportSession` `:429-448`, `LevelPill` `:604-620`, `SeverityDot` `:571-576`, `RepeatBadge` `:564-569`, `ClickableBadge` `:378-379`, the Clear-view `SettingsShared.Confirm` `:410-415`, Report → `ReportRequests.Open(ReportKind.Bug, new ReportPrefill(PastSession: …))` `:403-404`, Open folder `:405-406`. Every toggle/level `Invoke` runs through `UsePost` (the overlay click finishes before the Key-remount tears the bar down); the category index clamp lives in a `UseEffect` (no signal writes in render). |
| DELETE `Features/Shell/DiagnosticsPanel.cs` | `DiagnosticsMoreMenu`/`DiagToolbarToggle` die with it. `SettingsShared.cs:12` cref → `LogsPanel`. The three switches + Simulate update → `SettingsPage.General.cs` "Developer" section; `CrashReportsCard` → `SettingsPage.About.cs` "Reports" (Workstream B). `SettingsPage` gains `_nc = UseContext(NotificationCenterBridge.Slot)` for Simulate update. |
| `Features/Shell/SettingsPage.cs:17,22,38-46,118-138` | `TabLogs` replaces `TabDiagnostics` (index per B.2); slug `"logs"`; `TabLogs => BoxEl{Grow,Shrink,MinHeight=0, Children=[Embed.Comp(() => new LogsPanel(svc?.Settings))]}` in the unconstrained lane. |
| `Diagnostics/WaveeLog.cs` | Delete `:67-69` env flags, `:445-457` `EnvLevel`/`EnvInt`; `:91-92` → `minLevel ?? Info` / `fileMinLevel ?? Info`; `:96` → `if (ringCapacity is int rc) ReallocRing(rc)`; doc `:78-79`. |
| `Program.cs:94-111` | Use `LogCapturePolicy.BuildDefault*` + `Resolve(settings.Get(LogMinLevel), …)`; drop the env-var comment. `Platform/AppSettings.cs:290-291`, `Features/Detail/DetailShell.cs:65` comments → "Settings › Logs › Verbose". |
| `assets/loc/en-US.json` | `settings.tabs.diagnostics` → `logs` "Logs". `settings.diagnostics.*` add: `refresh, clearView, clearViewBody, wrapLines, verbose, verboseSub, allCategories, levelAll, levelInfo, levelWarnings, levelErrors, fields, exception, captureCaption "Capturing {capture}+ · file {file}+", sessionEvents "{count} events", runningFor "pid {pid} · running {uptime}"`. Remove: `refreshSessions, clearRing, clearRingBody, sortNewestTip, sortOldestTip, groupRepeatsTip, groupRepeatsOffTip, captureLevelSub, fileLevelSub, levelOverriddenMin, levelOverriddenFile, category`. Keep the rest. (`nl.json`/`ko-KR.json` have no diagnostics section.) |

### C.5 Tests
- NEW `Wavee.Tests/LogViewTests.cs`: `Build_LevelBuckets`, `Build_CategoryFilter_IsCaseInsensitive`, `Build_Search_MatchesCategoryEventIdMessageOpAndFields`, `Build_NewestFirst_ReversesOrder`, `Build_GroupRepeats_CollapsesAdjacentIdenticalOnly` (A,A,B,A → A×2,B,A), `Build_GroupRepeats_Off_KeepsEveryRow`, `Build_Cap_SetsTruncated_AndCountsAreOverAllEntries`, `Categories_DistinctSortedIgnoreCase`, `CategoryIndex_NullIsZero_UnknownIsZero`, `FieldText_OnePerLine_EmptyWhenNone`, `FormatTime_UsesInjectedOffset_AndDashForZero`, `SessionLabel_WithTimestamp_AndPidFallback`, `Uptime_Tiers`, `MetaLine_OmitsEmptyParts`, `IndexOfSequence_FindsGroupedRow`, `CopyText_RepeatSuffix`.
- NEW `Wavee.Tests/LogCapturePolicyTests.cs`: `Resolve_MinusOneIsBuildDefault_ClampsToError`, `ToSetting_DefaultRoundTripsToMinusOne`, `SetVerbose_On_WritesTraceAndPersists`, `SetVerbose_Off_RestoresDefaultAndPersistsMinusOne`, `EffectiveFileLevel_IsUpwardOnly`.
- EDIT `WaveeLogSessionsTests.cs`: add `ListPastSessions_DiscoversDailyRolledFiles_FromBasePath` (`wavee-20260901.log` A, `wavee-20260902-120000.log` B-start, `wavee-20260902.log` B-cont + current sid; live path = non-existent base `wavee.log`; expect 2 sessions newest-first, B spanning two files, current excluded) and `ListPastSessions_DailyFileOrdering_SizeRollSortsBeforeItsDay` (pins the ordinal `'-' < '.'` at `WaveeLogSessions.cs:37`).
- EDIT `WaveeLogTests.cs:157-169`: replace `EnvPrecedence_EnvBeatsExplicitConfigureArg` with `Configure_ExplicitArgWins_NoEnvOverride` (env set, ignored).

### C.6 CHANGELOG
`Added`: "Settings › Logs — a full-height log viewer with a command bar (refresh, copy, export, open folder, clear; newest-first, group repeats, Verbose), search + level + category filters, one-click expandable rows with fields and exceptions, and a session picker that lists past runs (#n)". `Removed`: "`WAVEE_LOG_LEVEL` / `WAVEE_LOG_FILE_LEVEL` / `WAVEE_LOG_RING` environment overrides — use Settings › Logs (#n)".

---


## Dialog edge fade (engine + app)

**Engine** `C:\wavee\fluent-gpu\src\FluentGpu.Controls\ContentDialog.cs:301-303`
```csharp
? new ScrollEl { Content = Content, ContentSized = true, MaxHeight = MaxH - 200f,
                 AutoEdgeFade = true, EdgeCues = ScrollEdgeCues.None }   // alpha mask: the content region is an overlay fill, never opaque in dark
```
Doc-comment the why (the `DetailTracks.cs:965` rationale). Gates: `dotnet build src/FluentGpu.slnx` Debug + Release, VerticalSlice per engine CLAUDE.md.

**App**
- `Features/Sidebar/RootlistFolderPicker.cs:138-141`: inner `ScrollEl` gets `AutoEdgeFade = true, EdgeCues = ScrollEdgeCues.None` (same latent band).
- `Features/Feedback/ReportDialog.cs:18-21`: fix the stale doc comment (it *is* a ContentDialog at 548).
- CHANGELOG `Fixed`: "Report a problem: a dark band no longer covers the form under the title while scrolling (#n)".

---


## Sequencing (disjoint file groups → parallel Sonnet subagents; orchestrator builds/tests/launches)

| Wave | Group | Files |
|---|---|---|
| 1 | **Engine** (one agent, in `..\fluent-gpu`) | `ContentDialog.cs` fade; `glyphs.json` +5 glyphs. Gates: `dotnet build src/FluentGpu.slnx` Debug + Release. Must land before wave 2 (app references the new `Icons.*`). |
| 2a | **Onboarding** (A) | `App/Setup*.cs`, `Features/Setup/**`, `WaveeApp.cs:336-395`, `LiveSessionHost.cs:581-596`, `PlaybackRuntimeBanner.cs`, `PlaybackRuntimeSetupCard.cs:463` + Phase file, `loc setup.*`, `Wavee.Tests/Setup*Tests.cs`, `Wavee.Tests.csproj:255-267` |
| 2b | **Settings** (B) | `Features/Shell/SettingsPage*.cs`, `ProfileMenu.cs`, `Design/WaveeTheme.cs`, `Program.cs:376,512`, `WaveeApp.cs:53`, `WaveeShell.cs:1283,2074`, `Features/Detail/DetailShell.cs:191,460-468,521`, `DetailVerticalHero.cs:513-520`, `DetailVerticalLayout.cs`, the 6 marquee/wash readers, `Platform/AppSettings.cs` keys, `App/SettingsCatalog.cs` + tests, `loc settings.*`, `LightModeOverhaulTests.cs` |
| 2c | **Logs** (C) | `Diagnostics/LogView.cs`, `LogCapturePolicy.cs`, `WaveeLog.cs`, `Program.cs:94-111`, `Features/Shell/LogsPanel.cs`, delete `DiagnosticsPanel.cs`, `loc settings.diagnostics.*`, `Wavee.Tests/Log*Tests.cs`, `WaveeLogSessionsTests.cs`, `WaveeLogTests.cs` |
| 2d | **Fade app-side** (D) | `RootlistFolderPicker.cs:138-141`, `ReportDialog.cs:18-21` |
| 3 | Orchestrator | `CHANGELOG.md`, `docs/plans/wavee/{onboarding-v3,settings-regroup,logs-page}-implementation.md` (this plan split per workstream, per CLAUDE.md), issues filed via `github-triage` (each `gh` call user-approved), build/test/run. |

Shared-file conflicts to sequence inside wave 2: `SettingsPage.cs`/`SettingsPage.General.cs`/`SettingsPage.About.cs` are B's; C only *adds* the `TabLogs` arm and deletes `DiagnosticsPanel.cs` — C's agent hands B's agent the three-switch + CrashReports blocks to place (or B runs after C on those two files). `Program.cs` is touched at three disjoint line ranges (A none, B `:376,:512`, C `:94-111`). `WaveeApp.cs` A `:336-395` vs B `:53`. `en-US.json` is edited by A, B, C in different key families — merge is mechanical.

## Verification (end to end)
```powershell
# engine
cd ..\fluent-gpu; dotnet build src/FluentGpu.slnx; dotnet build src/FluentGpu.slnx -c Release   # + VerticalSlice per engine CLAUDE.md
# app
dotnet build Wavee.slnx; dotnet build Wavee.slnx -c Release                                     # TreatWarningsAsErrors, both configs
dotnet test src/apps/Wavee.Tests/Wavee.Tests.csproj                                             # baseline in docs/guide/releasing-wavee.md §gates, minus deleted Setup*/AppearanceStage tests, plus LogView/LogCapturePolicy/SettingsCatalog/SetupGating updates
dotnet run --project src/apps/Wavee -- --fake
```
Manual (`--fake`, then a real fresh-install run by wiping `%LOCALAPPDATA%\Wavee` — unpackaged only, never against a packaged build):
- **Onboarding**: Welcome shows terms card + "By continuing…"; agreement grows in place, Esc closes it; footer "Step 1 of 3"; Continue → Sign in; fake sign-in → auto-lands on Local playback with the account card showing the *display name* (real backend: the name resolves live, no id); no toast/banner appears while the wizard is up; Offer state = one facts line + `Advanced ▸`; Not now → app opens, `setup.completed=true`; relaunch → no wizard. Sign out → Reauth opens on Sign in; after auth with runtime Ready → wizard closes without Local playback. Bump `TermsVersion` locally → Welcome in "updated terms" mode, Continue → closes. Settings has no "Run setup again".
- **Settings**: 7 tabs; every row glyph distinct within its section (`SettingsCatalogTests` pins it); no Palette anywhere (Settings, profile menu); no Mica Alt; Appearance shows "Marquee text"/"Color washes" as ON toggles and flipping them changes the shell live; no "Track page layout"/"Limit page color" rows; detail pages always Automatic + full-page tone.
- **Logs**: card fills the window height; session combo lists past runs after ~1 s (launch twice); pick one → rows load, footer "log file"; single click expands with chevron rotation and `CodeBlock` Fields/Exception with Copy; Warnings/Errors segments + badge counts agree; Verbose ON → Debug/Trace rows appear live, footer caption "Capturing Trace+ · file Info+"; overflow Capture level ▸ Error narrows capture; Copy visible pastes; Clear view confirms; `set WAVEE_LOG_LEVEL=Error` before launch has no effect.
- **Fade**: Report a problem → scroll the form → the top/bottom edges feather to transparent, no solid band; Rootlist folder picker with >360 DIP of folders likewise.

## Decisions made on your behalf (redirect if wrong)
1. Terms consent is "By continuing you agree" on the Welcome primary (clickwrap; the full agreement is one click away and Esc-closable) — no separate Terms screen and no checkbox.
2. No "Is this you?" interstitial: FirstRun/Reauth auto-advance on Authenticated+Premium; the account card (name, Premium, "Not you? Switch account") lives on the Local playback stage instead.
3. "Run setup again" is deleted with the settings-tour pages (nothing left to re-run); the sidebar chooser popup stays suppressed on fresh installs.
4. "remove these settings in general" (image #25) = the whole **Track page layout** group (Automatic/Hero + Limit page color) — detail pages become always-Automatic.
5. `DisableMarquee`/`DisableColorWashes` are renamed to `MarqueeEnabled`/`ColorWashesEnabled` (default on) with **no** migration of the old keys.
6. The Diagnostics tab is renamed **Logs** (log viewer only); Developer switches → General › Developer; Crash reports → About › Reports. 7 tabs, not 8.
7. Env log-level overrides are deleted (CLAUDE.md rule), replaced by the Verbose toggle.

---

