# Onboarding v3 — three screens (welcome & terms, sign in, local playback)

Approved 2026-09-02 (session plan: onboarding v3 / settings regroup / logs page / dialog fade). Context and the cross-workstream sequencing live in the sibling docs of the same date; this file carries the workstream's real code shapes, component trees and wireframes.

## Workstream A — Onboarding: 3 screens

### A.0 Flow

```
FirstRun:   Welcome(+terms) ──Continue──▶ Sign in ──authenticated & premium──▶ Local playback ──Open Wavee──▶ [close, MarkCompleted]
                 │ Quit                       │ Quit                             │ Not now → close (runtime declined, MarkCompleted)
Reauth:     (completed install, signed out)  Sign in ──▶ Local playback only if runtime NOT ready, else close
TermsRearm: (completed install, TermsVersion bumped) Welcome in "updated terms" mode ──Continue──▶ close
Rerun:      DELETED ("Run setup again" row removed from Settings — there is nothing left to re-run)
```

Footer: `Step 1 of 3 / 2 of 3 / 3 of 3`, progress `n/3`. No roadmap rail, no "~2 min · Premium · One download" meta cells, no "Decide for me", no "Is this you?" interstitial, no Done page.

### A.1 Wireframes (896×576 plate, Wide tier; the existing stage/decision split stays)

**Welcome (+terms)**
```
┌──────────────────────────────────────────────────────────────────────────────────────┐
│ ┌────────── stage 344 ──────────┐  Wavee for Windows                                 │
│ │   (HeroWelcome art)           │  Let's get you listening.                          │
│ │                               │  Wavee is an independent Spotify client for your   │
│ │                               │  own Premium account. Sign in, one small download, │
│ │                               │  and you're in.                                    │
│ │  Independent, on purpose      │                                                    │
│ │  Your account, Spotify's own  │  ┌ Terms (summary card, 4 rows, click → grows into ┐│
│ │  API — nothing else.          │  │ the full agreement in place, Esc closes)        ││
│ └───────────────────────────────┘  └─────────────────────────────────────────────────┘│
│                                    By continuing you agree to the terms · Privacy    │
├──────────────────────────────────────────────────────────────────────────────────────┤
│ Step 1 of 3 ▬▬▬░░░░░░░         [        Quit        ] [        Continue         ]    │
└──────────────────────────────────────────────────────────────────────────────────────┘
```
"Updated terms" mode (TermsRearm): eyebrow "Before you continue", title "We've updated the terms", same card, primary "Continue", secondary "Quit". Footer label "Terms" (no step count).

**Sign in** — unchanged layout (browser card recommended, QR on stage). Changes: the Authenticated phase no longer shows "Is this you?"; the wizard auto-advances (A.3). Fake backend keeps working.

**Local playback**
```
┌──────────────────────────────────────────────────────────────────────────────────────┐
│ ┌────────── stage ──────────────┐  Step 3 · Local playback                           │
│ │   (HeroPatch art)             │  One small download                                │
│ │                               │  Wavee plays audio through a component from        │
│ │ ┌ Signed in ─────────────────┐│  Spotify's own CDN. It's verified before it runs   │
│ │ │ (avatar) Christos          ││  and stored once per PC.                           │
│ │ │          Spotify Premium   ││                                                    │
│ │ │          Not you? Switch   ││  [ phase panel: Offer = one facts row              │
│ │ └────────────────────────────┘│    "Spotify 1.2.93 · Arm64 · ~38 MB · signed";     │
│ │                               │    Downloading/Verifying = progress + fact box;    │
│ │  Stored once on this PC       │    Ready = ReadyBadge + ReadyDetailBox;            │
│ │  Every Wavee update reuses it │    Failed = Failed(); Untrusted = VerifyDetailBox ]│
│ └───────────────────────────────┘  Advanced ▸  (HyperlinkButton → reveals the 3 chips)│
├──────────────────────────────────────────────────────────────────────────────────────┤
│ Step 3 of 3 ▬▬▬▬▬▬▬▬▬▬     [      Not now       ] [   Download & set up (38 MB)  ]   │
│                              Ready:                [        Open Wavee          ]    │
└──────────────────────────────────────────────────────────────────────────────────────┘
```
The three "Download / Verify / Ready" step cards and the pre-click "Advanced" chip row are gone from the Offer state; the chips appear only behind the `Advanced ▸` disclosure (and on Failed, as today).

### A.2 Pure model changes — `src/apps/Wavee/App/SetupGating.cs` (+ `SetupGatingTests.cs`)

```csharp
public enum SetupPage { Welcome = 0, SignIn = 1, LocalPlayback = 2 }   // Terms merged into Welcome; Appearance/Sidebar/Sound/Notifications/Done deleted

public const int StepTotal = 3;
public static (int Step, int Total)? StepNumber(SetupPage page, SetupSession.EntryPoint entry)
    => entry == TermsRearm ? null : ((int)page + 1, StepTotal);
public static float Progress(SetupPage page) => ((int)page + 1) / (float)StepTotal;
public static string? StepLabelKey(SetupPage page, EntryPoint entry) => entry == TermsRearm ? Strings.Setup.Terms.Title : null;

public static SetupPage NextPage(SetupPage page, bool skipSignIn)   // unchanged shape, clamps at LocalPlayback
public static SetupPage PrevPage(SetupPage page, bool skipSignIn)

/// FirstRun/Reauth: Authenticated + Premium ⇒ leave SignIn without a confirmation page.
public static bool AutoAdvancesAfterSignIn(SetupSignInPhase phase) => phase == SetupSignInPhase.Done;
/// Reauth on an install whose runtime is already Ready ⇒ nothing left to do after SignIn.
public static bool SkipsLocalPlayback(EntryPoint entry, ProvisioningOutcome outcome) => entry == Reauth && outcome == Ready;
/// The last page's primary closes the wizard (no Done page): Ready ⇒ "Open Wavee" + MarkCompleted.
public static bool IsLastPage(SetupPage page) => page == SetupPage.LocalPlayback;
/// While the wizard is open, the runtime toast/banner and "runtime ready" toast stay silent.
public static bool SuppressesRuntimePrompts(bool pending, bool sessionOpen) => pending || sessionOpen;
```
Delete: `RoadmapPages`, `RoadmapLabelKey`, `RoadmapIndexFor`, the 7-total constants. `SetupSession.EntryPoint` gains `TermsRearm` (built by `WaveeApp`/`SetupChrome` when `SetupGating.NeedsTermsRearm` armed the wizard on a *completed* install — detect: `IsPending && IsCompleted`).

`App/SetupCommands.cs`: `Resolve` arms for Welcome (`Continue`/`Quit` — new key `setup.quit`), SignIn (unchanged rows, but `Done` row is never shown post-auto-advance; keep for the fake backend / a Premium-required stall), LocalPlayback (`Ready` ⇒ primary `Strings.Setup.OpenWavee`, secondary null; `Offer` secondary `NotNow`). `ShowBack` = page is LocalPlayback only. Delete Appearance/Sidebar/Sound/Notifications/Done arms + `DoneRow`. Tests: `SetupCommandsTests.cs` updated.

`App/SetupBootstrap.cs`: keep `SidebarOnboardingSeen = true` on fresh install (the separate sidebar chooser must **not** come back now that the Sidebar step is gone — one fewer popup; Classic default, switchable from the sidebar's own layout menu). Update the comment.

Delete (and their `Wavee.Tests.csproj` `Compile Include` lines 262–265 + test files): `App/SetupSoundFacts.cs`, `App/SetupNotificationSummary.cs`, `App/SetupDoneSteps.cs`, `Features/Setup/AppearanceStageModel.cs`; tests `SetupSoundFactsTests`, `SetupNotificationSummaryTests`, `SetupDoneStepsTests`. `App/SetupStepState.cs` stays only if `SetupDecision.StepCard` is still used (Verify/Untrusted detail) — otherwise delete.

### A.3 Session — `Features/Setup/SetupSession.cs`

- `EntryPoint { FirstRun, Reauth, TermsRearm }` (Rerun deleted; `IsRerun` → `IsTermsRearm`; `CanDismiss(isRerun…)` in SetupGating becomes `CanDismiss(entry, busy)`: only TermsRearm may Escape — it has a shell behind it).
- `Primary()`:
  - Welcome ⇒ write `TermsAcceptedVersion = SetupGating.TermsVersion` (consent moves here, still before the advance), then `TermsRearm ? Close+MarkCompleted : Advance(SignIn)`.
  - SignIn ⇒ per phase as today; `Done` ⇒ advance (kept for fake backend).
  - LocalPlayback ⇒ as today, but `Ready ⇒ MarkCompleted + RequestClose` (was `m.Close()` → advance to Done).
- `Secondary()`: Welcome ⇒ `QuitApp` (TermsRearm ⇒ `RequestClose`); LocalPlayback Offer/Failed ⇒ `DismissSetting + DeclineRuntime + MarkCompleted + RequestClose`. Delete the four DecideFor arms and the Done arm.
- New: `ObserveAuth()` — an effect in `SetupSignInPage` (it already subscribes to `bridge.Login`) that, when `SetupGating.AutoAdvancesAfterSignIn(facet)` and `Page.Peek()==SignIn`, calls `session.Advance(SkipsLocalPlayback(entry, bridge.RuntimeStatus.Peek().Outcome) ? close : LocalPlayback)`. Post via `UsePost` so it never writes a signal during render. Guard with a `UseRef<bool>` so it fires once per mount.
- `SetupChrome.cs:86-99`: the post-auth remount lands on whatever page the carried session is on (LocalPlayback after the auto-advance); drop the "Is this you?" comment block. Delete the `OpenRequest`/`Bump` rerun path and `Settings → Run setup again` (`SettingsPage.General.cs:266-273`, loc `setup.runAgain`).
- `WaveeApp.cs:355-357`: `completed ? Reauth : FirstRun` unchanged; add the TermsRearm construction when `IsPending && IsCompleted` and authenticated (this case is currently mis-routed to a FirstRun-shaped Rerun via SetupChrome).

### A.4 Views — `Features/Setup/`

| Action | File |
|---|---|
| **Rewrite** `SetupPage.Welcome.cs` | Merge: keep kicker/headline/lead (new lead copy), drop `MetaRow`/`MetaCell`; body adds the Terms summary card + "By continuing…" fine print. Move `AgreementSummary`, `AgreementDoc`, `DocSection`, `SummaryRow`, `Sections()`, `Teaser`, `PrivacyUrl`, the Rise/Lift motion, the `EscapeConsumer` registration and `agreementOpen` signal **from** `SetupPage.Terms.cs` into a new `SetupTermsCard.cs` (static builder + one component holding the open signal) so Welcome composes it. Stage: `SetupStage.Rail(Welcome)` + `Caption("Independent, on purpose", …)`; roadmap removed. TermsRearm mode: eyebrow/title from `setup.terms.updatedTitle`/`updatedLead`. |
| **Delete** | `SetupPage.Terms.cs`, `SetupPage.Appearance.cs`, `AppearanceStageView.cs`, `SetupMiniChrome.cs`, `SetupPage.Sidebar.cs`, `SetupPage.Sound.cs`, `SetupPage.Notifications.cs`, `SetupPage.Done.cs`, `SetupStepList.cs` (if unused), `SetupWrites.cs` (nothing left writes settings from the wizard), heroes `HeroEula.cs` `HeroSettings.cs` `HeroSidebar.cs` `HeroSound.cs` `HeroBell.cs` `HeroDone.cs` (+ their `HeroMotion` helpers if orphaned; `docs/plans/wavee/onboarding-v2-assets/{Settings,Eula,Privacy}Lottie.json` and `onboarding*.html` stay as design history — they are docs). |
| **Edit** `SetupPage.Placeholders.cs` | `BodyFor` = 3 arms; `SetupPageCapture` drops `AttachRequestTheme`. |
| **Edit** `HeroView.cs` | `Art`: Welcome→HeroWelcome, SignIn→HeroConnect, LocalPlayback→HeroPatch. |
| **Edit** `SetupLayout.cs:210-212` | `CoverFor` ⇒ always `Dim` when a shell is behind (no live-preview pages remain). `SetupLayoutTests` updated. |
| **Edit** `SetupPage.SignIn.cs` | Name source: `bridge.User.Value` (subscribing) with `Login.Value.User` fallback — a tiny pure helper `SetupSignInPresentation.DisplayNameFor(WaveeUser? live, WaveeUser? snapshot)` (prefers the live profile's `DisplayName` when it differs from `Id`; tests in `SetupSignInPresentationTests`). Add the auto-advance effect (A.3). The `DoneLeft` card moves to `SetupAccountCard.cs` (shared with LocalPlayback's stage). |
| **Edit** `SetupPage.LocalPlayback.cs` | Offer state: lead + one `RuntimeDetailRow` facts line + `Advanced ▸` `HyperlinkButton` toggling a local `UseSignal<bool>` that reveals the existing chip row; drop the `StepCard` ladder (`SetupRuntimePresentation.ShowsStepCards` → delete, `StepStates` → delete; keep `ShowsAdvancedChips`/`ShowsLocalSourceChips`/`StagePanelFor`/`ProgressFraction`/`ShortHash`; `SetupRuntimePresentationTests` updated). Stage column: `SetupAccountCard` (avatar via `PersonPicture`, `DisplayNameFor`, Premium pill, "Not you? Switch account" → `session.SwitchAccount`) above the existing phase panel. Primary label for Offer: `Strings.Playback.Runtime.DownloadSetupSized(mb)` when `Total` is known from the catalog, else the plain key. |
| **Edit** `SetupDialog.cs` | Footer reads `StepNumber(page, entry)`/`StepLabelKey(page, entry)`; `ClosingAction` uses `CanDismiss(entry, busy)`; delete the `SetupWizardFooter` "Decide for me" width math if any; `KeepAlive MaxEntries: 3`. |
| **Edit** `SetupStage.cs` | Delete `Roadmap`. |
| **Edit** `SetupPreAuthRoot.cs` / `SetupChrome.cs` | Remount discipline unchanged; remove rerun. |

### A.5 Toasts/banner never over the wizard

- `SpotifyLive/LiveSessionHost.cs:581-596`: wrap in `if (!SetupGating.SuppressesRuntimePrompts(SetupGating.IsPending(svc.Settings), SetupSession.Current is not null))`. (The wizard's own LocalPlayback page *is* the prompt; a user who chose "Not now" already burned `PlaybackRuntimeSetupDismissed` via `DismissSetting`, so the toast correctly never returns for them either.)
- `Features/Shell/PlaybackRuntimeBanner.cs:71`: `showBanner &= SetupSession.Covering.Value == SetupCover.None && !SetupGating.IsPending(_settings)` (subscribes to `Covering` — reactive, disappears the frame the wizard opens).
- `Features/Shell/PlaybackRuntimeSetupCard.cs:463` ("Local playback is ready" toast): skip when `OnWizardExit is not null` (the model is wizard-hosted; the page shows Ready itself). Pure form: `PlaybackRuntimeSetupModel.ShowsReadyToast(bool wizardHosted)` next to the Phase enum file so `Wavee.Tests` can pin it.

### A.6 Loc — `assets/loc/en-US.json`

Add: `setup.quit` "Quit", `setup.welcome.lead` (new copy), `setup.welcome.consent` "By continuing you agree to the terms.", `setup.terms.updatedTitle` "We've updated the terms", `setup.terms.updatedLead`, `setup.localPlayback.title` "One small download", `setup.localPlayback.lead`, `setup.account.notYou` "Not you? Switch account", `setup.stepOf` stays. `setup.eyebrow.signIn` → "Step 2 · Your account", `setup.eyebrow.localPlayback` → "Step 3 · Local playback".
Delete: `setup.eyebrow.{appearance,sidebar,sound,notifications}`, `setup.roadmap.*`, `setup.welcome.meta*`, `setup.welcome.roadmapTotal`, `setup.decideForMe`, `setup.useThisLayout`, `setup.runAgain`, `setup.done.*`, `setup.appearance.*`, `setup.sidebar.*`, `setup.sound.*`, `setup.notifications.*`, `setup.terms.{needGroup,premium*,runtime*,data*,stageCaption*}` (the three "what Wavee needs" cards fold into the one Welcome lead), `setup.preSetup`, `setup.complete`. The generated `Strings` class drops them; the build catches any straggler.

### A.7 CHANGELOG / issue
CHANGELOG `Changed`: "Setup is three screens — welcome & terms, sign in, local playback. Appearance, sidebar, sound and notification choices live in Settings with the same defaults the wizard used to pick (#n)". `Fixed`: "Setup showed the account id instead of the display name (#n)"; "Local-playback toast and banner no longer appear over the setup wizard (#n)". Issues created via `github-triage` (user-approved `gh` calls) before the commit.

---

## Sources (onboarding research)
- Fluent 2 · Onboarding: https://fluent2.microsoft.design/onboarding
- SEM Nexus · App onboarding drop-off benchmarks 2026: https://semnexus.com/app-onboarding-flow-benchmarks-where-users-drop-off-2026
- Formbricks · Onboarding best practices: https://formbricks.com/blog/user-onboarding-best-practices
- scandiweb · Onboarding best practices 2026: https://scandiweb.com/blog/user-onboarding-best-practices/
- Spotify desktop has no wizard at all (Page Flows / UserOnboarding.academy teardowns): https://pageflows.com/post/ios/onboarding/spotify/ · https://useronboarding.academy/post/5-user-onboarding-experiences

---

## v3.1 — Rise 1:1 visual rework

Approved 2026-09-02, same session. The v3 workstream above shipped the RIGHT three pages but an incoherent visual
layer (a 344-DIP radial-gradient "stage" rail, duplicated account cards, a green pill, "Advanced ▸", 64-px display
type, bespoke chips/rows). This pass replaces the whole visual layer with Rise Media Player's own setup dialog,
1:1 — metrics, type ramp, controls — while keeping every logic seam (`SetupSession`, `SetupGating`, `SetupChrome`,
`SetupPreAuthRoot`'s mount discipline, `PlaybackRuntimeSetupModel` + `PlaybackRuntimeSetupCard.SetupBody`, `LoginView`
statics, `QrGrid`, `WaveeLottie`) untouched in substance.

### Layout — `SetupLayout.cs` (rewritten)

Rise reference (captured 2026-09-02 from its live XAML): `ContentDialog` 762×490, padding 24; `SetupPageContent` =
icon column 0→192 (+24 gap) at `MinWindowWidth ≥ 770` (one on/off breakpoint, no hysteresis ladder); header
`TitleTextBlockStyle` (28/600) `Margin 0,-4,0,4` with a 42-wide back-button spacer that collapses once the icon
column shows; footer `ControlGrid` height 80, padding 24, columns `[210 progress | 1* primary | 1* secondary]`,
gap 6 — **primary LEFT, secondary RIGHT** (WinUI's own default-button-first order); back button 30×30, glyph `E112`
(`Icons.Back`) @ 12, shown only on the last page. The old four-tier `SetupLayoutTier` ladder, the 344-DIP stage rail,
every row/chip/pairing-lane const are DELETED.

### Pages — `SetupPage { Terms, SignIn, LocalPlayback }` (renamed from `Welcome`)

The wizard's first page IS the terms page now (Rise's `TermsPage`) — the welcome/wordmark moment moved to a
pre-dialog splash, `SetupSplash.cs`, mounted by `SetupPreAuthRoot` for a cold `FirstRun` only (Reauth/TermsRearm keep
the original one-post auto-open). `SetupGating.StepNumber(page)` no longer takes an entry parameter — a TermsRearm
run only ever visits Terms, so `StepNumber(Terms) == null` ("Pre-setup") already covers it. `StepTotal` is 2 (SignIn
= 1, LocalPlayback = 2); `Progress(page) = (int)page / 2f`.

Every real page composes through `SetupPageHost.Frame(page, header, body, backAutoPadding)` — Rise's
`SetupPageContent`: a 192-wide Lottie column (dropped below the 770-DIP breakpoint) beside [a `Ui.Title` header row
over a `ScrollView` body]. Body vocabulary is the new `SetupText` static (`Stack`/`Group`/`Lead`/`Body`/`Secondary`/
`Card` — replaces `SetupStage`/`SetupDecision`/`SetupCompact`/`SetupRows`/`SetupType` wholesale, all deleted).

- **Terms** (`SetupPage.Terms.cs`, replacing `SetupPage.Welcome.cs` + `SetupTermsCard.cs`): the full four-section
  agreement printed inline and left to scroll — no summary card, no disclosure, no `EscapeConsumer`. Footer
  "Pre-setup" · **Accept** / **Decline**.
- **Sign in** (`SetupPage.SignIn.cs`, rewritten): Idle shows two `SettingsCard`s (continue in the browser; scan a
  QR — `QrGrid` mounts directly in the card's `Content` slot, and the live "expires in mm:ss" countdown is isolated
  in its own tiny `SetupScanCard` component so the 1 Hz tick re-renders only that card, never the whole page). Busy
  shows an `InfoBar` + the takeover's own `LoginStepBar`/`LoginStepRow` ladder. Done ("Is this you?") is a plain
  account row — avatar, name, "Spotify Premium"/"Spotify Free" caption, "Not me" — no green pill. Failed/Expired/
  Premium show an error `InfoBar` OVER the same two Idle cards so the user can retry in place
  (`SetupSignInPresentation.ShowsIdleCards`).
- **Local playback** (`SetupPage.LocalPlayback.cs`, rewritten): a download `SettingsCard` + a `SettingsExpander`
  "Advanced" while Offer; one progress `SettingsCard` (label/byte text + a 162-wide `ProgressBar` in `Content`) per
  network phase; a warning `InfoBar` + two fact cards for Untrusted; four detail cards (version/arch/signature/
  location) + fine print for Ready; an error `InfoBar` + the Offer card again for Failed; `SetupBody.VersionPicker`
  for Advanced. No account card (confirmed on the previous page now), no facts table, no chips.

### Deleted outright

`SetupStage.cs`, `SetupDecision.cs`, `SetupCompact.cs`, `SetupRows.cs`, `SetupType.cs`, `SetupTermsCard.cs`,
`SetupAccountCard.cs`, `SetupPage.Welcome.cs`, `HeroView.cs` (its one caller, `SetupPageHost`, now calls
`WaveeLottie.For`/`Options` directly). `Features/Auth/LoginView.cs` lost `CompactRightPane`/`CompactPairingLink`/
`OrDivider`/`BrowserLoginButton`/`GlyphBadge`/`SpotifyBrand`/`GoldTint` — all zero-caller once the SignIn page stopped
using the old takeover's pairing pane; `SpotifyGreen`/`OpenUrl`/the four live sub-components survive.

### Engine

`LottieOptions.Zoom` (new, default 1): `_fit *= Zoom`, still centred/clipped. `WaveeLottie.Options` is
`LottieOptions.RiseSetup with { Recolor = WaveeLottieRecolor.Apply, Zoom = 1.2f }` — the OOBE scenes carry ~25% empty
margin at their authored fit; 1.2× is the one deliberate deviation from a literal Rise readout.

### Tests

`SetupLayoutTests` rewritten for the Rise metrics (`Width`/`Height` clamp, `ShowsIcon`, `FooterButtonWidth`,
`CoverFor`). `SetupGatingTests` updated for the two-step ladder (`StepNumber`/`Progress`/`ShowsBack`/
`BackSpacerApplies`, `Terms` replacing `Welcome` throughout). `SetupCommandsTests` updated for the Terms Accept/
Decline row and page-based `ShowBack` (no more `BlocksDismiss` veto). `SetupSignInPresentationTests` rewritten for
`ShowsIdleCards` (the old `PaneOpacity`/`ShowsOptionCards`/`StageKind`/etc. are gone with the stage/decision split).
`SetupRuntimePresentationTests` trimmed to `ProgressFraction`/`ShortHash`/`ShowsReadyToast` (the old
`ShowsAdvancedChips`/`ShowsLocalSourceChips`/`StagePanelFor`/`SetupRuntimeStagePanel` are gone with the stage panel).

## v3.2 — first-run fixes (2026-09-02)

Field defects from the v3.1 rework, each with its root cause and the gate that pins it:

- **Footer label 48 DIP below the band, bar past the plate bottom** — `SetupWizardFooter`'s progress column passed Rise's
  `Padding="0,0,48,0"` as `new Edges4(0, 48, 0, 0)`: `Edges4` is `(Left, Top, Right, Bottom)`, so the 48 landed on TOP.
  The engine's FlexLayout measure-share change (two `Grow=1, Basis=0` siblings) was suspected and exonerated: the engine
  gate `gate.layout.footer-band` (FluentGpu.VerticalSlice `LayoutShellSuite`) lays out the exact plate → chrome column →
  footer shape with the real `Button`/`ProgressBar` controls and reproduced the +48 DIP label with the wrong argument
  order and passes with the right one — buttons 246/246 in source order, column pinned at `Height=32, AlignSelf=Center`.
- **Splash removed** — `SetupSplash.cs` deleted, `setup.splash.*` keys removed; `SetupPreAuthRoot` mounts the one-post
  `SetupPreAuthOpener` for every entry point (FirstRun included), so the dialog is the first thing a cold install shows.
  The "We / We're glad you're here. A" fragments over the dialog were a D3D12 backend defect (glyph batches replayed
  after a segment's fills — page text painted over the opaque plate under a partial-repaint scissor), fixed in the engine
  (`D3D12Device.CoverPendingText`) and pinned headlessly by `gate.overlay.modal-covers-text` /
  `gate.overlay.unmounted-text-leaves-no-glyphs` and on the real path by the gallery's `--dialog-scroll-probe`.
- **Sign-in page fits** — 80-DIP QR, one-line card descriptions, "Wavee needs Spotify Premium · Sign up" on one row
  (`SetupLayout.SignInIdleBodyHeight(2) = 312 ≤ BodyLaneHeight(490) = 325`, `SetupLayoutTests`); the body ScrollView
  carries `AlwaysShowScrollbar` so any overflow shows the WinUI rail.
- **Accent primary** — the Spotify-green `SetupButtonKind.Spotify` palette is gone (`LoginView.SpotifyGreen` with it);
  every primary is `Button.Accent`.
- **"Checking…"** — `playback.runtime.checking` is the Catalog-phase primary label; the long sentence stays as the
  progress card's header. Engine `Button` labels are one line, ellipsized, `MinWidth 0` (`gate.layout.button.label-ellipsis`).
