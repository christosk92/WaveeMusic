# Setup / sign-in, What's New (release notes, highlight viewer, after-update dialog), feedback dialog - 0.3 visual fidelity contract

> 0.2.9 sources: `Features/Setup/*.cs` (13 files, 1 601 lines) · `Features/Auth/*.cs` (5 files, 775) ·
> `Features/ReleaseNotes/*.cs` (14 files, 2 156) · `Features/Feedback/*.cs` (6 files, 877) ·
> `App/{SetupGating 201, SetupCommands 110, SetupEntryPoint 20, SetupSignInPresentation 38, SetupRuntimePresentation 22,
> SetupBootstrap 103, ReleaseNotesStore 543, AppUpdateToasts 153, WaveeTips 188, WaveeTipsCore 100, AppVersion 104}.cs` ·
> `Diagnostics/{ReportKinds 90, ReportBundle 200, IssueFormUrl 95, ReportRedactor 177, CrashPromptPolicy 50}.cs` ·
> `../Wavee.Core/ReleaseNotes/*.cs` (12 files, 1 882) — **≈ 9 385 lines total**
> | 0.3 target: `Screens/Setup.cs`, `Screens/Setup.UI.cs`, `Screens/ReleaseNotes.cs`, `Screens/ReleaseNotes.UI.cs`,
> `Screens/ReleaseNotes.Host.cs`, `Screens/Feedback.UI.cs` (+ a proposed `Screens/Feedback.cs`, §9) | Wave 6 owner R

Shared vocabulary lives in `00-design-system.md` (tokens, type ramp, motion curves, CTA shapes, materials). This chapter
specifies only how **this** surface configures it. Cross-references: `18-shell-frame.md` (where the three zero-size
chromes mount), `19-shell-overlays.md` (toasts, the runtime banner, teaching tips — and, with `14-os-surfaces.md`,
the ONE notification stack `Platform/Notify.cs` + `Platform/Notify.Host.cs`, owner I in Wave 4, which ports
`AppUpdateToasts`; this chapter consults both and owns neither), `27-settings-and-diagnostics.md`
(the crash-reports card, About's report links, the diagnostics text the composer quotes).

Cite 0.2.9 paths as they are today (`src/apps/Wavee/...`); after Wave 0 they live under `src/apps/_old/Wavee/` with the
same relative path. Line numbers are HEAD `b3f6647a`.

---

## 0. The non-negotiables

1. **The wizard plate is 762 × 490, never the engine's `ContentDialog`.** `ContentDialog` hard-clamps its card at
   `MaxW = 548 / MaxH = 756` (`..\fluent-gpu\src\FluentGpu.Controls\ContentDialog.cs:110`), so `SetupPlate` reproduces
   that card's chrome by hand: `Tok.FillSolidBase` plate, `Tok.FillLayerAlt` content region, a 1-px
   `Tok.StrokeCardDefault` separator, an 80-tall `Tok.FillSolidBase` footer, `Radii.OverlayAll`, a 1-px
   `Tok.StrokeSurfaceDefault` hairline and `Elevation.Dialog` (`SetupDialog.cs:129-162`). A wizard that reads as a
   different dialog family from the rest of the app is the first thing a user notices.
2. **The Lottie hero column is 192 DIP and it recolours to the live accent.** Three Windows-OOBE scenes
   (`eula`/`connect`/`patch`) play the first half of their timeline once and hold (`LottieOptions.RiseSetup`), zoomed
   1.2×, with `#0078D4 → Tok.AccentDefault`, `#002B67 → Tok.AccentTextPrimary` and the H 195-285° / S ≥ .35 blue-violet
   band hue-rotated by the accent's own delta (`WaveeLottie.cs:47`, `WaveeLottieRecolor.cs:47-57`). A static PNG, a
   looping animation, or an un-recoloured Windows-blue scene are each a visible regression.
3. **The footer is primary LEFT, secondary RIGHT, with a 210-wide progress column that collapses with the icon
   column.** Rise's `ControlGrid` order, not WinUI's (`SetupDialog.cs:241-254`). At 762 wide the two buttons are
   246 each; below the 770-DIP viewport breakpoint they are 351 each and the label/bar disappear.
4. **The sign-in Idle body fits the 325-DIP lane with no scrolling** — lead (2 lines, 40) + 20 + browser card (68) +
   20 + scan card (82-DIP QR plate + 32 padding = 114) + 20 + the one-row "needs Premium · Sign up" (32) = **314**
   (`SetupLayout.cs:80-84`, `SetupLayoutTests.cs:64-66`). The body `ScrollView` still carries `AlwaysShowScrollbar`
   so any overflow *says* it scrolls (`SetupPageHost.cs:77`).
   **The 314 holds only while the card is ≥ 476 DIP wide.** `SettingsCard` wraps its `Content` slot BELOW the header
   (plus an 8-DIP `VerticalHeaderContentSpacing`, left-justified instead of right-aligned) whenever the card measures
   `< SettingsCard.WrapThreshold = 476` (`..\fluent-gpu\src\FluentGpu.Controls\SettingsCard.cs:31, 129, 135-138,
   267-278`). In the wizard that is two viewport bands — see W3b. `SignInIdleBodyHeight` does **not** model the wrap;
   the persistent scrollbar is what covers it, and that is why it is `AlwaysShowScrollbar` rather than `Auto`.
   There is a **second** `SettingsCard` threshold below it: at `< WrapNoIconThreshold = 286` the card drops its
   **header icon** entirely (`SettingsCard.cs:32, 130` — `hideIcon`), so at the wizard's minimum plate the Globe /
   Camera / Download glyphs disappear from every card. See W3b.
5. **The QR is pure black on a pure white `Radii.Card` plate, coalesced run-per-row, quiet zone 4 modules.**
   No logo, no tint, no rounded modules (`QrGrid.cs:46-66`). The plate size is `(modules + 8) × max(2, size/(modules+8))`
   — a v4 symbol asked for at 80 DIP paints at **82** (`QrPlate.cs:20-22`).
6. **The highlight card's body is a fixed 68-DIP slot with a true alpha `EdgeFade`, conditional on measured overflow.**
   Four lines at `LineHeight 17`, `MaxLines = 0` inside a natural-height wrapper whose `OnBoundsChanged` flips a
   per-card signal at `> 68.5` (`HighlightCard.cs:273-314`, `HighlightCardMetrics.cs:35-40`). It is `BoxEl.EdgeFade`
   (one offscreen alpha layer), **not** a painted gradient — which is exactly what lets the card stay the standard
   translucent `Tok.FillCardDefault` Mica card instead of the opaque plate the design doc first specified.
7. **The highlight viewer never bounces when you page.** Its text half is a `ZStack` of one invisible, inert **sizer**
   per highlight plus the live keyed slide on top, so the plate's height is `band + pager + tallest text` from the
   first frame (`HighlightViewer.cs:405-431`). A flex column here re-folds the exit orphan's height into the parent for
   the whole 160-ms exit and the centred plate jumps twice per step.
8. **The viewer's chevrons and close never move.** Both chevrons are rendered on every slide at a fixed position
   (12 DIP inset, 36-DIP circle); at an end the button **dims in place** — `Opacity 0.3`, `Fill = MediaScrim @ A .40`,
   not hit-testable, out of the tab order — over an 83-ms ease (`HighlightViewer.cs:290-310`). Removing it reads as the
   chrome jumping rather than the slide moving.
9. **The pager is a hand-rolled dot strip, not `PipsPager`.** 6-DIP dots, gap 8; the selected one stretches to a
   **14-DIP capsule** on a `SizeMode.Reflow` width transition (120 ms `SmoothOut`) and brightens to `Tok.TextPrimary`
   on the same 120 ms brush ramp; the cluster is centred inside a full-width 24-tall strip so the dots never drift
   sideways (`HighlightViewer.cs:338-393`). The strip shows *where* you are, not just how many.
10. **The What's-new page publishes one whole view or nothing.** `ReleaseNotesView` is assembled off the UI thread and
    written in a single signal write; until then the page is a centred `ProgressRing(28)`, and a failed load is the
    empty state with the GitHub link — never a hero with no sections (`ReleaseNotesPage.cs:23-29, 79-80, 264-268`).
    The one deliberate second publish is the budgeted issue-state refresh, which re-publishes the whole view.
11. **Reading the page is what marks the notes seen, and `lastSeen` is captured BEFORE the load starts.**
    `ReleaseNotesPage.cs:62-77` — read `ReleaseNotesLastSeen`, carry it into the loader and out again on the view, then
    advance the setting. Advancing first makes `ReleaseNotesRange.Between(me, me)` empty, and the "since you last
    looked" banner plus every unread dot become unreachable by construction.
12. **Everything visible is a loc key — with four named exceptions and three unnamed ones.** `setup.*` / `auth.*` /
    `whatsNew.*` / `report.*` in `assets/loc/en-US.json` (+ `ko-KR.json`, `nl.json`). No literal user-facing string in
    an element; the four hard-coded URLs (`spotify.com/signup`, `spotify.com/premium`, `PRIVACY.md`, the GitHub repo)
    are named constants. The three that are **not** loc keys and must be ported as they are:
    (a) the report dialog's three dropdowns — `ReportChannels.When` (7), `.Reproduces` (3), `.Areas` (24) — are
    **verbatim English** strings copied from `.github/ISSUE_TEMPLATE/*.yml` and pinned to it by `ReportChannelsTests`
    (`Diagnostics/ReportKinds.cs:38-48`); translating them would break the GitHub prefill;
    (b) the preview's truncation tail `"… (N KB more in the copied report)"` (`ReportBundle.cs:188-190`);
    (c) the download card's `" MB"` unit (`SetupPage.LocalPlayback.cs:140-142`).
    A locale sweep (parity 62) must expect all three to stay English.
13. **The report preview is monospace, 220 DIP tall, bordered, and it recomputes per keystroke** — the reporter must
    see what leaves the machine, redacted, before pressing anything (`ReportDialog.cs:240-274`). It is a **preview**,
    not the bundle: `ReportBundle.Preview` cuts at `PreviewChars = 12 000` and appends
    `"
… (N KB more in the copied report)"` (`ReportBundle.cs:24, 188-190`). Keep the cap AND the tail — a preview
    that silently stops mid-log reads as a truncation bug.
14. **The three deferral gates hold — and a FOURTH one, owned elsewhere, behaves differently.** The after-update
    plate, the crash prompt and the runtime toast all stand down while the setup wizard is pending or live, and
    re-evaluate on `SetupSession.MarkerEpoch` (`AfterUpdateDialog.cs:195-204`, `ReportChrome.cs:45-67`,
    `SetupGating.cs:200`). A modal stack over a mandatory modal wizard is the worst first-run bug this surface can
    ship. **`SidebarOnboardingChrome` is the fourth**, mounted in the same ZStack immediately BEFORE `SetupChrome`
    (`WaveeShell.cs:1482`): it reads the same `SetupGating.IsPending` gate but from a `DepKey.Empty` effect
    (`SidebarOnboardingChrome.cs:35-51`), so unlike the other three it does **not** re-evaluate on `MarkerEpoch` and
    the design chooser waits for the NEXT launch. `26-*` owns that chrome; this chapter owns the fact that the
    ordering and the epoch asymmetry are deliberate, and a 0.3 port must not "fix" it into a fourth same-launch modal.
15. **Escape cannot strand a user.** Only a `TermsRearm` run may be dismissed, and only while nothing is in flight
    (`SetupGating.cs:93-101`); a programmatic close always goes through (`SetupDialog.cs:59-60`). The honest exit from
    FirstRun/Reauth is "Decline" → quit.

---

## 1. Anatomy

### 1.1 — 0.2.9 tree: the setup wizard

```
WaveeApp.Render                                              WaveeApp.cs:349-405   login gate; reads SetupSession.MarkerEpoch
├─ [needsSignIn] SetupPreAuthRoot(session, settings)          SetupPreAuthRoot.cs:27   the WHOLE window pre-auth
│  ├─ UseEffect → WaveeLottie.Warm()                          SetupPreAuthRoot.cs:37   parse+compile 3 scenes off-thread
│  ├─ TitleBar.Create(ShowCaptionButtons: true)               SetupPreAuthRoot.cs:39   only min/max/close a logged-out window gets
│  ├─ BoxEl { Grow, Fill = ColorF.Transparent }               SetupPreAuthRoot.cs:55   transparent → live DWM Mica reads through
│  │  └─ SetupPreAuthOpener                                   SetupPreAuthRoot.cs:69   0-size; ONE post → SetupDialog.Open(bare: true)
│  └─ OverlayHost.Create(body)                                SetupPreAuthRoot.cs:58   the only overlay service below the gate
└─ [authed] WaveeShell … shellWithOverlays ZStack             WaveeShell.cs:1482-1494
   ├─ SidebarOnboardingChrome(settings)                       WaveeShell.cs:1482    0-size; NOT this chapter's — but it
   │                                                                                defers on IsPending too, DepKey.Empty (§0.14)
   ├─ SetupChrome(settings)                                   SetupChrome.cs:26     0-size; DOUBLE post → Open(bare: false)
   ├─ ReportChrome(settings)                                  ReportChrome.cs:23    0-size; two effects
   ├─ AfterUpdateChrome(settings)                             AfterUpdateDialog.cs:185  0-size; one effect
   └─ SetupCoverScrim                                         WaveeShell.cs:2273    Tok.FillSmoke cross-fade behind the plate

SetupDialog.Open(overlay, post, settings, session, bare)      SetupDialog.cs:31
├─ PopupOptions(FocusTrap, Modal, PopupChrome.Modal)          SetupDialog.cs:46-47  ScrimVisual = bare
├─ ClosingAction  = Programmatic || EscapeClosesPlate(...)    SetupDialog.cs:59-60
├─ ClosedAction   = Covering→None · session clear · BumpMarker SetupDialog.cs:69-83  the ONE close funnel
└─ SetupPlate(session)                                        SetupDialog.cs:101
   ├─ contentRegion BoxEl Fill=FillLayerAlt Pad=24            SetupDialog.cs:129-135
   │  └─ PagesHost BoxEl Grow/Shrink/Min 0 Clip               SetupDialog.cs:181-195
   │     └─ Flow.KeepAlive(page, MaxEntries 3,                SetupDialog.cs:186-193 TransitionFor = PageNavMotion.RecipeFor(Dir.Peek())
   │        │                SuppressLayoutTransitionsOnActivation)
   │        └─ SetupPagePlaceholders.For(page)                SetupPage.Placeholders.cs:22
   │           └─ SetupPageCapture(page)                      SetupPage.Placeholders.cs:39  attaches settings/bridge/runtime
   │              └─ SetupTermsPage | SetupSignInPage | SetupLocalPlaybackPage
   │                 └─ SetupPageHost.Frame(page, header, body, backAutoPadding)  SetupPageHost.cs:29
   │                    └─ SetupPageFrame (props pushed)      SetupPageHost.cs:45
   │                       ├─ [wide] iconColumn 192           SetupPageHost.cs:93-98  LottieView.Create(WaveeLottie.For(page), 192, Options)
   │                       └─ content column                  SetupPageHost.cs:80-84
   │                          ├─ header row (spacer 42?, Title) SetupPageHost.cs:56-64, 108-109
   │                          └─ ScrollView(body) AlwaysShowScrollbar SetupPageHost.cs:66-78
   ├─ separator BoxEl Height 1 Fill=StrokeCardDefault         SetupDialog.cs:143
   ├─ SetupWizardFooter(session)                              SetupDialog.cs:205
   │  ├─ [large] progress column 210×32 pad-right 48          SetupDialog.cs:224-239
   │  │  ├─ TextEl "Step N of 2" | "Pre-setup" 14/600 secondary SetupDialog.cs:236
   │  │  └─ ProgressBar.Determinate(Progress(page), 162)      SetupDialog.cs:237
   │  ├─ PrimaryButton  (Accent, Grow 1 Basis 0, H 32)        SetupDialog.cs:263-269
   │  └─ SecondaryButton (Standard, same box; absent on Ready) SetupDialog.cs:257-259
   └─ [row.ShowBack] BackOverlay                              SetupDialog.cs:167-173  IconButton 30/12 over the 24-pad corner
```

Page bodies:

```
SetupTermsPage                                     SetupPage.Terms.cs:20
│  header = rearm ? terms.updatedTitle : terms.header   :24-25   "We've updated the terms" on a TermsRearm run —
│                                                                the ONLY difference; the body is the same agreement
│  Frame(..., backAutoPadding: false)                   :43      Terms declares it explicitly; SignIn does too (:86).
│                                                                Only LocalPlayback takes Frame's `true` default.
└─ SetupText.Stack(gap 20)                          SetupText.cs:17
   ├─ Lead(terms.start)                            SetupPage.Terms.cs:29   BodyStrong 14/600 wrap
   ├─ Body(terms.lastUpdated)                      :30                     Body 14/400 wrap
   ├─ Body(welcome.lead)                           :31
   ├─ ×4 [ Lead(sectionNTitle), Body(sectionNBody) ] :33-37
   └─ Group(gap 12)                                 :38-40
      ├─ Secondary(terms.fine)                                             Body 14 secondary wrap
      └─ HyperlinkButton(terms.privacyLink, Small) → PRIVACY.md

SetupSignInPage                                    SetupPage.SignIn.cs:30
├─ UseEffect announce(phase)                        :46-62   UIA live region; code SPELLED OUT
├─ UseEffect needsChallenge → session.RestartCode    :65-69
└─ body = facet switch … with Key "signin:<facet>", Enter Dy 6/α0, Exit Dy -4/α0, MotionTok.StandardEnter  :73-84
   ├─ Idle / Failed / Expired / Premium             :90-102
   │  ├─ [Idle] Lead(signIn.lead)                   :94
   │  ├─ [else] InfoBar(Error, …) not closable      :118-125
   │  ├─ BrowserCard  → SettingsCard(Globe, click)  :127-129
   │  ├─ ScanCardSlot                               :134-137
   │  │  ├─ [challenge] SetupScanCard (Key = code)  :228-261   1 Hz tick, own component; the QR text is
   │  │  │                                                     `Challenge.VerificationUriComplete ?? .VerificationUri` (:135)
   │  │  │  └─ SettingsCard(Camera, desc = "CODE · spotify.com/pair  ·  Expires in mm:ss",
   │  │  │                  Content = QrGrid(uri, 80))          :253-259
   │  │  └─ [none] PendingScanCard                  :139-152   ProgressRing(20) + "Getting your code…" 12.5 tertiary
   │  └─ PremiumRow (wrap, gap 4)                   :108-116   Body secondary + HyperlinkButton(signup)
   ├─ Busy                                          :155-183
   │  ├─ Lead(signIn.waiting) · InfoBar(Informational, auth.signingIn, msg)   msg SWITCHES (:157-158):
   │  │     RequestingCode | LoggedOut → auth.gettingCode ("Getting your code…")
   │  │     anything else              → auth.waitingApproval ("Waiting for you to authorize…")
   │  ├─ LoginStepBar(bridge.Login)                 LoginView.cs:113   Determinate step/4, 220 wide, Fill reflow-eased
   │  └─ 4 × LoginStepRow(width 300), Wrap, Gap 16, Stagger 40  LoginView.cs:48
   └─ Done "Is this you?"                           :186-219
      ├─ Lead(signIn.confirmLead)
      ├─ AccountRow: PersonPicture 40 · BodyStrong name · Caption Premium/Free · HyperlinkButton "Not me"  :197-219
      └─ Secondary(signIn.confirmHint)

SetupLocalPlaybackPage                             SetupPage.LocalPlayback.cs:25
│  [model is null] header = localPlayback.header, body = Lead + Body(runtime.notActive), NO Key/Enter/Exit, NO fine print  :39-45
│  header = HeaderFor(phase)                                 :67-73  Untrusted → runtime.signatureInvalid ·
│                                                                    Ready → runtime.ready · Advanced → runtime.chooseVersion ·
│                                                                    everything else → setup.localPlayback.header
└─ body Key "runtime:<phase>", same Enter/Exit as SignIn    :56-62   (the key carries the MODEL phase name, e.g.
   │                                                                 "runtime:FetchingCatalog", not the footer facet)
   ├─ Lead(LeadFor(phase))                                   :51  Ready → readyLead, everything else → lead
   ├─ PhaseContent(phase)                                    :78-89
   │  Offer       → Group[ Card(Download, cardTitle/cardSub(arch)), SettingsExpander "Advanced" ×3 items ]  :92-112
   │  Catalog     → Card(checkingSupport, reachingCatalog·arch, Download, ProgressBar.Indeterminate 162)    :115-119
   │  Downloading → Card(DownloadLabel ?? runtime.downloading, bytes, Download, Determinate|Indeterminate 162) :121-132
   │                 bytes = Total > 0 ? "12.3 / 38.4 MB" : "12.3 MB" (no total yet)                        :140-142
   │  Verifying   → Card(verifying, verifyingCaption, Download, Indeterminate 162)                          :134-138
   │  Untrusted   → Group[ InfoBar(Warning), Card(version·arch), Card(sha 4…4) ]  ActiveEntry null → "—"    :146-156
   │  Ready       → Group[ (upToDate Body, only when UpToDate), Card ×4 (version / arch / signature / location) ] :159-173
   │                 signature's "View" link ONLY when status.SignatureInfo is not null; every unknown value is "—"
   │  Failed      → Group[ InfoBar(Error, runtime.missing, model.Error ?? runtime.noPack), DownloadCard ]    :176-179
   │                 title = "Local playback needs a one-time setup"; the body falls back to
   │                 "Playback support isn't available for your Spotify version yet." when model.Error is null
   │  Advanced    → SetupBody.VersionPicker (PlaybackRuntimeSetupCard.cs)                                    :87
   └─ [phase != Advanced] Secondary(localPlayback.finePrint) Key "runtime:fineprint"  :54
```

### 1.2 — 0.2.9 tree: What's New

```
ContentHost.PageFor "whatsnew"                     ContentHost.cs:235-237   Key = "page:whatsnew:" + arg
└─ ReleaseNotesPage(versionArg)                    ReleaseNotesPage.cs:44
   ├─ signals: _view (ReleaseNotesView?), _loaded, _onlyLatest                :46-48
   ├─ UseEffect DepKey.Empty                                                  :62-77  lastSeen → LoadAsync → advance setting
   └─ Frame(body, rail)                                                       :145-167
      ├─ header row pad (36,16,36,12) gap 12, AlignItems Center: Icon(Tag,22) + PageHero("What's new")  :150-159
      └─ content row pad (36,0,36,0) gap 14
         ├─ body column gap 12                                                :137
         │  ├─ [stacked] SinceBanner                                          :176-192
         │  └─ ScrollView(ScrollKey "whatsnew:<version>")  gap 16, pad-right 6 :129-134
         │     ├─ ReleaseNotesHero.Create(doc, isLatest, openUrl, copy)       ReleaseNotesHero.cs:17
         │     │  isLatest = IsLatest(view, doc) — TRUE when the index is null OR empty (:169-174), so an OFFLINE
         │     │  load (no index ⇒ no rail) still shows the [Latest] pill on whatever document it managed to load
         │     │  ├─ pills row (Wrap): [Latest accent — only when isLatest] [Stable|Beta — always]
         │     │  │                    [quad mono — only when doc.PackageVersion is non-empty]   :24-27, 81-95
         │     │  ├─ headline 30/600 · tagline 14 secondary MaxW 640          :49-52
         │     │  │   headline = whatsNew.headline(version, name), or whatsNew.headlineBare(version) with no codename  :76-79
         │     │  ├─ meta row gap 14 (Wrap, margin-top 4): [Released — only when Date parses]
         │     │  │                    [Requires — only when MinOs] [Tag — always]               :29-34, 97-105
         │     │  └─ right column gap 8: Button.Standard(OpenOnGitHub) / Button.Subtle(CopyLink), Small  :56-66
         │     ├─ HighlightStrip.Create(merged, open)                         HighlightStrip.cs:21
         │     │  ├─ [no highlights] BoxEl Height 0, HitTestVisible false — the whole strip disappears  :23-24
         │     │  ├─ Eyebrow("Highlights") tertiary, column gap 6
         │     │  └─ row gap 10, AlignItems Stretch, ≤ 3 × HighlightCard.Create(item, open)
         │     │     Key = "hl:<version>:<highlight.Id>", falling back to the LOOP INDEX when Id is empty (:33)
         │     │     └─ HighlightCardView(item, open, compact: false)         HighlightCard.cs:179
         │     │        ├─ Media band (16:9, ZStack, Clip)                    :63-100
         │     │        │  ├─ ImageEl poster Cover / DecodePx 1200 / Corners (8,8,0,0)  :78-84
         │     │        │  └─ overlay row pad 8: KindPill · Spacer · PlayGlyph(video)   :86-91
         │     │        ├─ Title 13.5/18 MaxLines 2                           :240-244
         │     │        ├─ BodySlot 68 + EdgeFade(Bottom,24) when overflows   :273-314
         │     │        └─ ReadMoreRow (regular) | Button.Accent footer (store) :318-337 / :232-236
         │     ├─ per release: [divider] · InfoBar per notice · ChangelogSection per section  :100-120
         │     │  └─ ChangelogSection(props re-pushed, Key "sec:<ver>:<kind>") ChangelogSection.cs:24
         │     │     ├─ header: badge 22 (glyph/ink/wash) · title 14/600 · count 12 tertiary · [Show all N]  :56-67
         │     │     └─ card: rows separated by 1-px dividers                  :76-81
         │     │        └─ ChangelogItem.Create(item, states, openUrl)        ChangelogItem.cs:19
         │     │           ├─ RichTextBlock.Paragraph(scope eyebrow + markdown-lite spans) 13  :35-38
         │     │           ├─ refs row wrap gap 6 → IssueChip.Create / IssueChip.Pr  :29-31
         │     │           └─ Avatars.Create(contributors)                    Avatars.cs:51
         │     ├─ "Issue states as of <date>" 11.5 tertiary — only when doc.GeneratedAt is set  :122-124
         │     └─ spacer 24                                                   :125
         └─ ReleaseRail.Create(index, selected, running, lastSeen, go)        ReleaseRail.cs:27
            ├─ [index empty / failed] BoxEl Width 0, HitTestVisible false — the whole column disappears  :31
            ├─ Eyebrow("Releases") tertiary, margin (4,6,4,2)
            ├─ ScrollView(ScrollKey "whatsnew:rail") gap 2 → Flow.For keyed by version → Row  :60-103
            │  Row = Role NavigationItem, Focusable, Key "rail:<version>"; markers are EXCLUSIVE:
            │  isYou ? [YOU] : unread ? [•] : nothing, and `unread` is force-false on the SELECTED row  :62-73
            │  [BETA] is independent and can accompany either; the title row wraps at gap 6
            │  subtitle (11 tertiary, 1 line, char-ellipsis) = `name  ·  date` · `name` · `date` · `""` — whichever
            │  of the two that index entry carries (`ReleaseRail.cs:105-111`); with neither it prints an empty line
            └─ RailFoot 11.5 tertiary wrap
```

### 1.3 — 0.2.9 tree: the highlight viewer and the after-update dialog

```
HighlightViewer.Open(overlay, items, initial, nav, closeHost)   HighlightViewer.cs:28
│  PopupOptions(FocusTrap, LightDismiss, PopupChrome.Modal) { ScrimVisual = false }   :36-37
└─ HighlightViewerView                                          :44
   ├─ _index Signal<int> · _pip Signal<int> (mirrored from an effect) · _dir plain FIELD  :59-65
   └─ root BoxEl W=vp.W H=vp.H ZStack Center Focusable OnKeyDown   :100-129
      ├─ veil BoxEl stretch · Fill/Hover/Pressed = rgba(0,0,0,184) · OnClick Close  :113-126
      └─ Plate BoxEl W=w MaxH=vpH-64 FillSolidBase r8 border 1 Shadow(90/40/#99)  :182-196
         ├─ Band H=imgH ZStack Clip Fill=(store?AccentSubtle:FillSubtleSecondary) BrushMs 250  :208-232
         │  ├─ KEYED "hv:<id>:img" Animate=For(_dir) → ImageEl (Fade 140 reveal)  :218-227, 234-242
         │  ├─ TopRow (STABLE): KindPill @ margin (8,8,0,0) · SpaceBetween · close Circle @ (0,12,12,0)  :247-258
         │  └─ CentreRow (STABLE): Prev · [WatchPill if video] · Next, pad-x 12, SpaceBetween  :263-284
         ├─ [count ≥ 2] PagerRow H 24 margin-top 8, centred cluster gap 8      :343-393
         └─ TextStack ZStack Grow                                              :405-419
            ├─ N × TextSizer (Key "hv:<id>:size", Opacity 0, inert, same padding)  :425-431
            └─ TextSlide (Key "hv:<id>:txt", Animate=For(_dir))                :480-515
               └─ ScrollView(TextColumn, ContentSized: true, Padding=TextPadding(hasPager))
                  └─ TextColumn MaxW 720: title 20/28 · paragraph 14/20 (+8) · [actions +16]  :441-472

AfterUpdateDialog.Open(overlay, settings, fromQuad, me, store, nav)   AfterUpdateDialog.cs:36
│  settings.Set(ReleaseNotesPendingFrom, "") BEFORE the plate renders  :39   ← one-shot, whatever happens next
│  PopupOptions(FocusTrap, Modal, PopupChrome.Modal) { ScrimVisual = true }  :48-49
└─ Plate : Component                                              :53
   ├─ _loaded Signal<(Doc, HighlightItem[])?> · _dontShow Signal<bool>  :59-60
   ├─ hero pad (26,22,26,18) gap 8                                :89-109
   │  ├─ pills row: Pill("Updated", accent) · Pill("0.2.8 → 0.2.9.10", mono)  :95-103
   │  ├─ welcome 26/600 wrap = whatsNew.dialog.welcome(AppVersionDisplay.Of(me))   :104-105
   │  │     THREE shapes (`AppVersion.cs:95-103`): `Wavee 0.2.9 "Breaker"` · `Wavee 0.2.9` (no codename) ·
   │  │     `Wavee 0.2.9 "Breaker" · Beta 3` (a beta). A DEV build prints `me.SemVer`, not `me.Core`.
   │  └─ tagline 14 secondary wrap MaxW 560                       :106-107
   ├─ [cards] row gap 10 AlignItems Stretch pad (26,6,26,14)      :112-121   ABSENT when cards.Count == 0 — which
   │                                                                          covers BOTH "still loading" AND every
   │                                                                          failure (no store / doc null / threw:
   │                                                                          :149-153, 167-168). See W23.
   │  └─ HighlightCard.Compact(item, () => HighlightViewer.Open(overlay, highlights, idx, nav, close))  :80-82
   ├─ footer pad (26,14,26,14) Fill=FillLayerAlt border 1 StrokeDividerDefault  :123-136
   │  └─ CheckBox("Don't show…") · Spacer · Button.Standard("Full release notes") · Button.Accent("Got it")
   └─ plate box W 720 MaxH 620 r8 Clip FillSolidBase              :138-144
```

### 1.4 — 0.2.9 tree: the report dialog

```
ReportChrome(settings)                                ReportChrome.cs:23   0-size, inside OverlayHost
├─ effect A: ReportRequests.Requested.Seq changed  → ReportDialog.Open(kind, prefill, default)  :37-43
├─ effect B: SetupSession.MarkerEpoch → CrashPromptPolicy.ThisLaunch → Toast | Open(Crash)      :45-67
│  deferred (and left ARMED) while SetupGating.IsPending || SetupSession.Current is not null    :50
│  Mode == Toast (the opt-out path): Toast.Show(common.crashLastRun, Severity Warning,
│    DurationMs 0 = STICKY, ActionLabel report.reportOnGithub → ReportRequests.Open(Crash, that path),
│    DedupeKey "crash.pendingReport")                                                           :53-64
│  Mode == Dialog: ReportDialog.Open(…, Crash, null, d)                                         :66
└─ CrashProbe.Mode → UseTimeout(2000) throw/FailFast :69-76

CrashReportsCard                                      CrashReportsCard.cs:16   (Settings › Diagnostics; ch. 27 owns the tab)
└─ SettingsExpander(crashReports / crashReportsSub, Icons.StatusWarning, InitiallyExpanded false)  :26-33
   ├─ [none] Item("", null, TextEl(crashReportsEmpty) 12 tertiary)  Key "crash-reports:empty"      :36-40
   └─ ≤ 10 newest × Item(stamp "g", null, HStack(8)[ Button.Standard("Report…"), Button.Standard("Open") ])  :42-57
      Key "crash-reports:<path>"; listing is UseMemo(DepKey.Empty) — snapshot on mount, never live

ReportDialog.Open(...)                                ReportDialog.cs:42
└─ ContentDialog.Show(overlay, d => …)                :58-81
   ├─ d.Title = report.title · d.DialogWidth = 548    :60-61
   ├─ d.PrimaryText = crash ? reportOnGithub : openGithub · d.CloseText = crash ? notNow : cancel  :75-76
   ├─ d.PrimaryButtonClick → session.Submit() ?? args.Cancel = true  :80
   └─ d.Content = ReportDialogBody, Key "report-body:<kind>"  :65-74
      └─ ReportDialogBody : Component  (Width 500 = 548 − 2×24)  :95
         ├─ TextEl subtitle 14 secondary wrap MaxW 500           :113-114
         ├─ [!crash] Segmented[Bug | Feature | Question | Idea] bound to kindIndex  :118-125
         └─ ReportDialogCard, Key "report-card:<kind>"           :127-136
            └─ ReportDialogCard : Component  gap 12, Width 500   :149
               ├─ UseEffect → Task.Run(ReportComposer.Compose)   :183-195  off-thread compose + redaction
               ├─ UseEffect → Session.Submit installed/cleared   :200-219
               ├─ [crash] InfoBar (3 honest states)              :226, 328-344
               ├─ TextBox Title (header + placeholder, W 500)    :228-233
               ├─ AddKindFields                                  :289-326
               ├─ CheckBox includeLogs(300 crash | 200 manual)   :237-238
               ├─ preview row: label · Spacer · Copy · Save as…  :240-255
               ├─ preview box 500×220 bordered → ScrollEl → mono 11  :256-272
               ├─ previewNote 12 tertiary                        :273-274
               └─ [crash] CheckBox "Don't ask again after a crash"  :276-278
```

### 1.5 — 0.2.9 tree: the `--qr-dump` probe (no window, no element)

`Features/Auth/QrDump.cs` (93) is the fifth file in `Features/Auth`. It paints nothing inside Wavee, but it is the only
instrument that decides **which half** of the sign-in QR is at fault, and it re-declares in a second place the encode
parameters and the ink that §4.2 pins for `QrGrid`. Engine-free: BCL only (`System.IO.Compression` plus a hand-rolled
CRC32) plus the app's `WaveeLogger` (`QrDump.cs:1-4, 12`).

```
Program.cs:232-238        --qr-dump [text] [outpath.png]
  |                        text  default "https://spotify.com/pair"   (:235 — an arg starting with "--" is NOT taken)
  |                        path  default "qr.png"                     (:236)
  +-> CliRun.cs:15         "--qr-dump" is a ProbeFlags entry -> IsHeadless -> no window; Program.cs:126 then points the
  |                        log's echo at the console AttachParentConsole() attached (Program.cs:35-40)
  +-> QrDump.Run                                        QrDump.cs:15
        |-- Qr.Encode(text, Qr.Ecc.M)                   :18     the SAME call QrGrid.Render makes (QrGrid.cs:24)
        |     encode throws -> log "QR encode failed: ...", exit 1    :19
        |-- ASCII structural dump -> Console.Out         :22-30  margin 2; dark = two full blocks, light = two spaces
        |-- grayscale PNG -> path                        :32-39  quiet 4, scale 14 px/module
        |-- log "QR for \"{text}\": {n}x{n} modules -> {px}x{px}px PNG at {full path}"   :40
        +-- exit 0                                       :41
      WritePng  :44-66   8-bit grayscale (colour type 0), a 0 filter byte per scanline, ZLibStream Optimal, IHDR/IDAT/IEND
      Crc32     :79-92   table-driven 0xEDB88320, hand-rolled so the probe needs no NuGet (:12)

  BOTH exits leave through ExitCli -> WaveeLog.Flush + DealerArchive.Flush -> Environment.Exit (Program.cs:22-27), so
  no CLI arm below :238 ever runs: `--qr-dump --spotify-login` dumps the DEFAULT url and exits without logging in.
```

One encode, two artefacts, two different quiet zones: the ASCII block is structural (finders / timing / alignment by
eye, margin **2**), the PNG is the scannable one (margin **4**). They are not interchangeable — W27.

### 1.6 — the 0.3 tree

Props freeze at mount (`..\fluent-gpu\docs\design\subsystems\component-props-contract.md`). The column below says how
each node gets *changed* data: **ctor** (frozen, safe — the value never changes for that mount), **props** (re-pushed
through `Embed.Comp(props, factory)` + `UseProps<T>`), **signal** (read inside `Render`), **context**, or
**Key remount**.

| 0.3 node | file · kind | inputs | how data reaches it |
|---|---|---|---|
| `Setup.Pages` / `Setup.Gate` / `Setup.Commands` / `Setup.Layout` / `Setup.SignInFacet` / `Setup.RuntimeFacet` / `Setup.Recolor` / `Qr.Encode` / `Qr.Plate` | `Screens/Setup.cs` · **CORE** | plain values | pure statics — no components, no signals |
| `Setup.Session` | `Screens/Setup.UI.cs` · plain class outside the tree | `Signal<SetupPage> Page`, `Signal<NavTransitionKind> Dir`, `static Signal<SetupCover> Covering`, `static Signal<int> MarkerEpoch` | signals; the object itself survives the pre-auth→post-auth remount |
| `Setup.PreAuthRoot` | `Setup.UI.cs` · `Component` | ctor `(Session, IAppSettings)` | ctor (both are stable for the window's life) |
| `Setup.PreAuthOpener` / `Setup.Chrome` | `Setup.UI.cs` · `Component` | ctor `settings`; `UseContext(Overlay.Service)` | context + one-shot `UseEffect(DepKey.Empty)` |
| `Setup.Plate` | `Setup.UI.cs` · `Component` | ctor `Session`; `UseContextSignal(Viewport.Size)` | **signal** — viewport drives `plateW/plateH`; `session.Page.Value` drives the KeepAlive |
| `Setup.Footer` | `Setup.UI.cs` · `Component` | ctor `Session`; `Viewport.Size` | signal (`Page`, viewport) → `Setup.Commands.Resolve(session.BuildCtx())` |
| `Setup.PageFrame` | `Setup.UI.cs` · `Component` | **props** `(SetupPage, string Header, Element Body, bool BackAutoPadding)` | **props** — the body is rebuilt when a page-local signal moves, so a ctor arg would freeze the first tree forever (`SetupPageHost.cs:40-47`) |
| `Setup.TermsPage` / `Setup.SignInPage` / `Setup.LocalPlaybackPage` | `Setup.UI.cs` · `Component` | `UseContext(PlaybackBridge)` / `Services` / `Overlay`; `bridge.Login.Value`, `bridge.Auth.Value`, `model.PhaseSig.Value` | **context + signal** |
| `Setup.ScanCard` | `Setup.UI.cs` · `Component` | **props** `(Uri, Code, Expiry)`; `Key = "signin:scan:" + code` | props + **Key remount** on a new code; owns its own 1 Hz tick signal |
| `Setup.QrGrid` | `Setup.UI.cs` · `Component` | ctor `(text, size)` | ctor — remounted by the scan card's Key |
| `Setup.LottieHero` | `Setup.UI.cs` · static | `Setup.LottieFor(page)`, `Setup.LottieOptions` | frozen per page (each KeepAlive slot mounts its own) |
| `ReleaseNotes.Document/Index/Highlight/...`, `ReleaseNotes.CardMetrics`, `ReleaseNotes.ViewerLayout`, `ReleaseNotes.Visibility`, `ReleaseNotes.Markdown`, `ReleaseNotes.Range`, `ReleaseNotes.Parser`, `ReleaseNotes.Validation`, `ReleaseNotes.Links` | `Screens/ReleaseNotes.cs` · **CORE** (also `<Compile Include>`d by `Wavee.ReleaseTool`, plan §3.3) | plain values | pure |
| `ReleaseNotes.Store` | `Screens/ReleaseNotes.Host.cs` · **SHELL** | `HttpClient`, app-data root, feed release, log | plain object; `IndexSnapshot()` is a `Volatile.Read`, never a signal (written off-thread) |
| `ReleaseNotes.Page` | `ReleaseNotes.UI.cs` · `sealed class Page : Component` | ctor `versionArg`; own `Signal<View?>`, `Signal<bool> loaded`, `Signal<bool> onlyLatest` | ctor (the route arg is the page's identity — `ContentHost` keys the slot by it) + **signals** |
| `ReleaseNotes.Hero` / `.Strip` / `.Rail` / `.Item` / `.Chip` / `.Avatars` / `.Divider` / `.SinceBanner` / `.Empty` | `ReleaseNotes.UI.cs` · static builders | immutable records + callbacks | rebuilt every render of the page |
| `ReleaseNotes.Section` | `ReleaseNotes.UI.cs` · `Component` | **props** `(Section, IssueStateCache?, Action<string>)`; `Key = "sec:<ver>:<kind>"` | **props** — the issue states land *after* the document, so a frozen section would show the release tool's snapshot forever (`ChangelogSection.cs:22-23`) |
| `ReleaseNotes.CardView` | `ReleaseNotes.UI.cs` · `Component` | ctor `(HighlightItem, Action open, bool compact)`; own `Signal<bool> overflows` | ctor is safe (`HighlightItem` immutable, `open` captures the index); the signal is why it is a component at all |
| `ReleaseNotes.ViewerView` | `ReleaseNotes.UI.cs` · `Component` | ctor `(items, initial, nav, closeHost, handleThunk)`; `UseContext(Viewport.Size)`; `Signal<int> _index`, `Signal<int> _pip`, plain field `_dir` | ctor + signals. `_dir` **must stay a plain field** — a motion-only value must not trigger a render of its own (`HighlightViewer.cs:63-65`) |
| `ReleaseNotes.AfterUpdatePlate` | `ReleaseNotes.UI.cs` · `Component` | ctor `(overlay, fromQuad, me, store, nav, close)`; `Signal<(Doc, Cards)?>` | ctor + one signal published in ONE write |
| `ReleaseNotes.AfterUpdateChrome` | `ReleaseNotes.UI.cs` · `Component` | ctor `settings`; reads `Setup.Session.MarkerEpoch.Value` (subscribing) | **signal** — the wizard deferral is re-evaluated on every marker bump |
| `Feedback.Kinds` / `.Channels` / `.Bundle` / `.IssueFormUrl` / `.Redactor` / `.Identity` / `.KindIndex` / `.CrashPromptPolicy` / `.CrashReportFiles` | **proposed** `Screens/Feedback.cs` · **CORE** (§9) | plain values | pure |
| `Feedback.Chrome` | `Screens/Feedback.UI.cs` · `Component` | ctor `settings`; `ReportRequests.Requested.Value`, `Setup.Session.MarkerEpoch.Value` | **signals**; a `static int s_lastOpenedSeq`, never a `UseRef` baseline (`ReportChrome.cs:26-29`) |
| `Feedback.DialogBody` | `Feedback.UI.cs` · `Component` | required init props; `Key = "report-body:" + (int)effectiveKind` | **Key remount** on kind |
| `Feedback.DialogCard` | `Feedback.UI.cs` · `Component` | required init props; `Key = "report-card:" + (int)kind` | **Key remount** — every signal inside is seeded once from an open-time constant, so the kind switch must rebuild the whole form |

---

## 2. Wireframes

Scale ≈ **8 DIP per monospace column**. Heights are annotated at the right edge in DIP.
Layout breakpoint for the whole wizard: **`SetupLayout.ShowsIcon(viewportW) = viewportW >= 770`** — a single on/off
switch on the **window** width, **no hysteresis band** (`SetupLayout.cs:28, 55`; `SetupLayoutTests.cs:26-31`). The plate
itself is `Clamp(762, 320, viewportW - 64)` x `Clamp(490, 184, viewportH - 64)` (`SetupLayout.cs:50-51`), so between
viewport 770 and 826 the icon column is present on a **shrunken** plate (770 -> plate 706, text column 442, footer
buttons 218 each).

### W1 - Setup / Terms @ viewport >= 826 (plate 762 x 490, wide)

```
 <- 24 ->  <---- icon column 192 ---->  <24>  <------------ content column 498 ------------->  <- 24 ->
+-----------------------------------------------------------------------------------------------+
|                                                                                               | 24  content-region pad
|                              .--------------.       Licence agreement & terms                 | 36  Ui.Title 28/36, margin 0,-4,0,4
|                              |   LOTTIE     |                                                 |  4
|                              |   "eula"     |       Wavee - licence agreement & terms of use  | 20  Lead  BodyStrong 14/20/600
|                              |  192 x 192   |                                               ^ | 20  <- Stack gap (SetupText.cs:18)
|                              |  RiseSetup   |       Last updated September 2026 - MIT licence| | 20  Body 14/20/400
|                              |  zoom 1.2x   |                                               | | 20
|                              |  recoloured  |       Wavee is an independent desktop client   # | 40  Body, wraps 2 lines
|                              '--------------'       for your own Spotify Premium account...  # |
|                               centred in the                                                 # | 20
|                               192 column,            Base software & copyright               # | 20  Lead
|                               AlignSelf Stretch      Wavee's own source is licensed to you.. # | 40
|                               Justify Center                                                 # | 20
|                                                      Your account & your library             # | 20  x4 sections, Lead+Body
|                                                      Signing in lets Wavee read and modify.. # | 40
|                                                            ...                               # |
|                                                      Spotify(R) and the Spotify logo are...  # | 40  Secondary (Group gap 12)
|                                                      Privacy policy                          v | 32  HyperlinkButton Small
|                                                      ^ ScrollView: margin-right -24, pad-right 24
|                                                        AlwaysShowScrollbar = true (the rail rides in the plate's pad)
|                                                                                               | 24
+-----------------------------------------------------------------------------------------------+  1  StrokeCardDefault
|      Pre-setup                    +-----------------------+ +-----------------------+         | 80  footer, pad 24
|      ========================0%   |        Accept         | |        Decline        |         |     H 32 buttons
|      <-- 210 (48 right pad, bar 162) --> 6 <---- 246 ----> 6 <---- 246 ---->                  |
+-----------------------------------------------------------------------------------------------+
  plate: Fill FillSolidBase - Corners Radii.OverlayAll (8) - Border 1 StrokeSurfaceDefault - Shadow Elevation.Dialog
  content region: Fill Tok.FillLayerAlt - Padding Edges4.All(24)      (SetupDialog.cs:129-162)
```

### W2 - Setup / Terms @ viewport 700 (plate 636 x 490, compact)

```
 <- 24 ->  <---------------------- content column 588 ---------------------------->  <- 24 ->
+-------------------------------------------------------------------------------------+
|                                                                                     | 24
|   Licence agreement & terms                                                         | 36  no 42-DIP back spacer here:
|                                                                                     |  4  Terms never shows Back
|   Wavee - licence agreement & terms of use                                          | 20  (SetupGating.cs:176, 182)
|   ...                                                                               |
|   (identical body, wider measure; the ScrollView rail still rides the plate's pad)   |
|                                                                                     | 24
+-------------------------------------------------------------------------------------+  1
|  +---------------------------------+ +---------------------------------+            | 80
|  |             Accept              | |            Decline              |            |
|  <----------- 288 --------------> 6 <----------- 288 -------------->                |
+-------------------------------------------------------------------------------------+
  ICON COLUMN GONE and PROGRESS COLUMN GONE together (ProgressColumnFor(false) = 0, SetupLayout.cs:59).
  FooterButtonWidth(636, false) = (636 - 48 - 0 - 12)/2 = 288.   At plate 762 compact: 351.
```

### W3 - Setup / Sign in / Idle, wide (the 314-of-325 lane)

```
+-----------------------------------------------------------------------------------------------+
|                                                                                               | 24
|                              .--------------.       Sign in to Spotify                        | 36  Ui.Title
|                              |   LOTTIE     |                                                 |  4
|                              |  "connect"   |       Sign in with your Spotify(R) account on   | 40  Lead, 2 lines
|                              |  192 x 192   |       the Spotify(R) website - or scan the...   |
|                              |              |                                                 | 20  Stack gap
|                              '--------------'    +----------------------------------------+   | 68  SettingsCard MinHeight 68
|                                                  | (globe) Continue in your browser       |   |     HeaderIcon Globe (<=20)
|                                                  |         Opens accounts.spotify.com ... |   |     IsClickEnabled -> StartBrowser
|                                                  +----------------------------------------+   |
|                                                                                               | 20
|                                                  +----------------------------------------+   | 114 = 82 QR + 2x16 pad
|                                                  | (cam) Scan the code with your phone    |   |
|                                                  |       WZY5-Q6TX - spotify.com/pair     |   |  description, one line
|                                                  |       -  Expires in 04:37  <- 1 Hz     |   |
|                                                  |                     +--------------+   |   |
|                                                  |                     | ### ## # ### |   |   |  QrGrid 82 x 82
|                                                  |                     | # # ### # #  |   |   |  white r8 plate
|                                                  |                     | ### # ## ### |   |   |  pure-black modules
|                                                  |                     +--------------+   |   |  quiet zone 4 x cell = 8
|                                                  +----------------------------------------+   |
|                                                                                               | 20
|                                                  Wavee needs Spotify Premium.  Don't have a   | 32  ONE wrapping row:
|                                                  Spotify account? Sign up                     |     Body.Secondary + HyperlinkButton
|                                                                                               | 24
+-----------------------------------------------------------------------------------------------+  1
|      Step 1 of 2                  +-----------------------+ +-----------------------+         | 80
|      ============........50%      |        Log in         | |         Close         |         |
+-----------------------------------------------------------------------------------------------+
  body sum = 40 + 20 + 68 + 20 + 114 + 20 + 32 = 314 <= BodyLaneHeight(490) = 325   (SetupLayout.cs:80-84)
```

### W3b - Setup / Sign in / Idle, the SettingsCard WRAP band (the state SignInIdleBodyHeight does not model)

```
 card width < SettingsCard.WrapThreshold (476)  ->  the Content slot moves BELOW the header,
 + 8 DIP VerticalHeaderContentSpacing, and is LEFT-justified instead of right-aligned
 (FluentGpu.Controls/SettingsCard.cs:31, 129, 135-138, 267-278)

+----------------------------------------+        the two bands, from SetupLayout's own arithmetic:
| (cam) Scan the code with your phone    |
|       WZY5-Q6TX - spotify.com/pair     |          WIDE   (icon column shown, plate = min(762, vpW-64)):
|       -  Expires in 04:37              |            card = plate - 48 - 192 - 24
|                                        | 8          wraps while vpW is 770 .. 803   (card 442 .. 475)
| +--------------+                       |            does NOT wrap at vpW >= 804     (card 476 .. 498)
| | ### ## # ### |  <- left, not right   | 82
| | # # ### # #  |                       |          COMPACT (no icon column, plate = min(762, vpW-64)):
| +--------------+                       |            card = plate - 48
+----------------------------------------+            wraps while vpW < 588          (card < 476)
  scan card ~ 158 tall instead of 114, so the Idle body      does NOT wrap at vpW 588 .. 769
  runs ~358 against a 325 lane -> IT SCROLLS, and the
  AlwaysShowScrollbar rail is what says so (S 0.4).
  The browser card (no Content slot) is unaffected at any width; only the scan card grows.

  A THIRD band, below both: card width < SettingsCard.WrapNoIconThreshold (286) drops the HEADER ICON
  (SettingsCard.cs:32, 130 - `hideIcon`, Right-aligned content only).  Compact card = plate - 48, and the
  plate floors at MinPlateWidth 320, so:
      vpW <  398  ->  card <= 286  ->  NO (globe) / (cam) / (dl) glyph on ANY wizard card
      vpW >= 398  ->  the glyph is back
  It never fires in WIDE mode (at vpW 770 the card is already 442).  A 0.3 port that hard-codes the icon
  into its own card shape will not reproduce this, and the 320-DIP floor is a real, reachable window size.

  The PREMIUM ROW wraps on its own too (Wrap = true, SetupPage.SignIn.cs:108-116): the secondary sentence and
  the "Sign up" HyperlinkButton fall onto two lines once the lane is narrow, +32 DIP the budget does not model.
  So does a THREE-LINE lead in a longer locale - SetupLayoutTests pins that case as deliberately overflowing
  (`SignInIdleBody_ThreeLineLeadOverflows_SoTheScrollbarMustShow`, SetupLayoutTests.cs:68-70).
```

### W4 - Setup / Sign in / Idle, challenge not minted yet (the ONLY skeleton on this page)

```
                                                  +----------------------------------------+   | 68
                                                  | (cam) Scan the code with your phone    |   |  Key "signin:scan:pending"
                                                  |                                        |   |
                                                  |                      (o) Getting your  |   |  ProgressRing.Indeterminate(20)
                                                  |                          code...       |   |  + TextEl 12.5 Tok.TextTertiary
                                                  +----------------------------------------+   |
  The code is minted the moment this page MOUNTS, not when the user reaches it, so it cannot expire while
  they read Terms (SetupPage.SignIn.cs:65-69, 131-137).  A fresh code remounts: Key = "signin:scan:" + UserCode.
```

### W5 - Setup / Sign in / Busy

```
|                              .--------------.       Sign in to Spotify                        | 36
|                              |   LOTTIE     |                                                 |  4
|                              |  "connect"   |       Waiting for you to authorize...           | 20  Lead
|                              |              |                                                 | 20
|                              '--------------'    +----------------------------------------+   |     InfoBar Informational
|                                                  | (i)  Signing you in...                 |   |     not closable
|                                                  |      Waiting for you to authorize...   |   |
|                                                  +----------------------------------------+   |
|                                                                                               | 20
|                                                  =============............  step/4            |     LoginStepBar 220 wide,
|                                                  (AlignSelf Start; Fill part reflow-eased)    |     Determinate; Error on Failed
|                                                                                               | 20
|                                                  (o) Connecting to Spotify   v Preparing your | 26  LoginStepRow x4, width 300,
|                                                                                library        |     Wrap -> 2 x 2, Gap 16,
|                                                  .  Starting audio           .  Almost there  | 26  Stagger 40 ms
|                                                  ^ 18x18 mark box: ProgressRing(16) current - Accept 15 AccentDefault done -
|                                                    RadioBullet 11 TextTertiary pending - Cancel 15 SystemFillCritical failed
|                                                    label 12/16: current 600/TextPrimary, done 400/TextSecondary, pending TextTertiary
+-----------------------------------------------------------------------------------------------+
|      Step 1 of 2  =======.....  |  Signing you in... (DISABLED) | |      Cancel       |        |  PrimaryEnabled = false
```

### W6 - Setup / Sign in / Done - "Is this you?"

```
|                              .--------------.       Is this you?                              | 36  header swaps
|                              |  "connect"   |                                                 |  4
|                              '--------------'       You're signed in on this PC. Continue as  | 40  Lead
|                                                     this account, or switch to another.       |
|                                                                                               | 20
|                                                  +----------------------------------------+   | 68  hand-rolled card:
|                                                  | (O)  Christos                  Not me  |   |  MinHeight SettingsCard.MinHeight
|                                                  |  40  Spotify Premium                   |   |  Padding All(16), Fill FillCardDefault
|                                                  +----------------------------------------+   |  Border 1 StrokeCardDefault, r 4
|                                                    ^ PersonPicture 40: the real avatar photo when one exists,
|                                                      else initials from displayName (imageSourcePath, NOT initials)
|                                                      BodyStrong name (1 line, char-ellipsis) over Caption.Secondary
|                                                      "Not me" HyperlinkButton (Small), pushed right by a Grow spacer
|                                                                                               | 20
|                                                  Not you? "Not me" signs this PC out so a     | 40  Secondary
|                                                  different account can sign in.               |
+-----------------------------------------------------------------------------------------------+
|      Step 1 of 2  ======.....  |    Yes, continue    | |        Not me        |               |
    NO auto-advance and NO green pill: this is a real, user-clicked confirmation (SetupCommands.cs:86-87).
```

### W7 - Setup / Sign in / Failed, Expired, Premium (retry in place)

```
|                                                  +----------------------------------------+   |     InfoBar Error, not closable,
|                                                  | X  Couldn't sign in                    |   |     REPLACES the Lead line
|                                                  |    We couldn't reach Spotify. Check... |   |
|                                                  +----------------------------------------+   |
|                                                     v the SAME two Idle cards + Premium row stay below
|                                                  + (globe) Continue in your browser ------+   | 68
|                                                  + (cam)   Scan the code with your phone -+   | 114
|                                                  Wavee needs Spotify Premium.  ...Sign up     | 32
+-----------------------------------------------------------------------------------------------+
   Failed   -> "Couldn't sign in" / snap.Error ?? auth.networkError   primary "Try again"      secondary "Close"
   Expired  -> "That code expired" / auth.codeExpiredBody             primary "Get a new code"  secondary "Close"
   Premium  -> "Wavee needs Spotify Premium" / auth.premiumBody       primary "Go to Premium"   secondary "Use a different account"
   SetupSignInPresentation.ShowsIdleCards(phase) is the ONE fact that keeps the cards (App/SetupSignInPresentation.cs:22-23).
```

### W8 - Setup / Local playback / Offer (+ the back button)

```
+-----------------------------------------------------------------------------------------------+
| (<) 30x30                    .--------------.       Set up local playback                     | 36
| IconButton over the 24-pad   |   LOTTIE     |                                                 |  4
| corner, glyph 12 (Icons.Back)|   "patch"    |       Wavee plays audio through a component     | 40  Lead
| in a HitTestPassThrough box  |              |       from Spotify's own CDN...                 |
|                              '--------------'                                                 | 20
|                                                  +----------------------------------------+   | 68
|                                                  | (dl) Download from Spotify's CDN       |   |
|                                                  |      Verified by fingerprint... - Arm64|   |
|                                                  +----------------------------------------+   |
|                                                                                               | 12  Group gap (BodyInnerSpacing)
|                                                  +----------------------------------------+   |     SettingsExpander,
|                                                  | (gear) Advanced                      v |   |     collapsed by default
|                                                  +----------------------------------------+   |     3 click-enabled items:
|                                                    - Choose a Spotify.dll...                  |       PickFolder
|                                                    - Use installed Spotify                    |       UseInstalled
|                                                    - Choose a version                         |       ShowAdvanced
|                                                                                               | 20
|                                                  Wavee recognizes supported builds by         | 40  Secondary, Key "runtime:fineprint"
|                                                  fingerprint and configures the rest...       |
+-----------------------------------------------------------------------------------------------+
|      Step 2 of 2   ====================100%   | Download & set up | |      Not now      |      |
```

### W9 - Setup / Local playback / Downloading, Checking, Verifying

```
|                                                  +----------------------------------------+   | 68+
|                                                  | (dl) Downloading Spotify 1.2.93        |   |  header = model.DownloadLabel
|                                                  |      12.3 / 38.4 MB                    |   |  "{r:0.0} / {t:0.0} MB" (:141)
|                                                  |      (Total <= 0 -> just "12.3 MB")    |   |  header falls back to runtime.downloading
|                                                  |                  =======.........      |   |  Content slot: ProgressBar 162
|                                                  +----------------------------------------+   |  Determinate when Total > 0,
|                                                                                               |  Indeterminate otherwise
+-----------------------------------------------------------------------------------------------+
|   Step 2 of 2  ============  |  Downloading... (DISABLED) | |       Cancel       |             |
   Catalog   header runtime.checkingSupport - desc "Reaching the catalog  -  Arm64" - Indeterminate
             primary "Checking..." disabled - secondary Cancel
   Verifying header runtime.verifying - desc verifyingCaption - Indeterminate
             primary "Verifying..." disabled - secondary **absent** (SecondaryKey null, SetupCommands.cs:103)
```

### W10 - Setup / Local playback / Ready (the wizard's terminal page)

```
| (<)                          .--------------.       Local playback is ready                   | 36  header = runtime.ready
|                              |   "patch"    |                                                 |  4
|                              '--------------'       Downloaded, verified and stored. Every    | 40  ReadyLead
|                                                     Wavee checkout on this PC reuses it.      |
|                                                                                               | 20
|                                                  [ up-to-date Body line, ONLY when UpToDate ]  | 20
|                                                  + Version        1.2.93.394 -------------+   | 68  SetupText.Card x4,
|                                                  + Architecture   Arm64 ------------------+   | 68  Group gap 12
|                                                  + Signature      Valid - Spotify AB  View+   | 68  Content slot: "View" link,
|                                                  + Location       C:\...\runtime  Open folder+ | 68  ONLY when SignatureInfo != null
|                                                    ^ every unknown value prints an em dash "-" (U+2014): status.SpotifyVersion
|                                                      ?? "-", Arch?.ToString() ?? "-", RuntimePath ?? "-"   (:164-171)
|                                                                                               | 20
|                                                  Wavee recognizes supported builds by...      | 40
+-----------------------------------------------------------------------------------------------+
|  Step 2 of 2  ======== 100%   +---------------------------------------------------------+     | 80
|                               |                     Open Wavee                          |     |
|                               <----------------------- 498 ----------------------------->     |
   SecondaryKey is NULL on Ready (SetupCommands.cs:106), so the flex row is [210 progress | 6 | primary]
   and the primary spans 714 - 210 - 6 = 498 - NOT FooterButtonWidth's 246.  Compact: 714 - 0 = 714.
```

### W11 - Setup / Local playback / Untrusted and Failed

```
Untrusted:  header swaps to runtime.signatureInvalid  +--------------------------------------+
                                                   | !  Signature could not be verified     |     InfoBar Warning
                                                   +----------------------------------------+
                                                   + Version   1.2.93  -  Arm64 ------------+     68   ActiveEntry null -> "-"
                                                   + SHA-256   a91f...3d02 ----------------+     68   ShortHash: 4 + "..." + 4
   footer: primary "Load anyway" (ConfirmUntrusted)  -  secondary "Back" = model.CancelUntrusted(), NOT a page walk
Failed:                                            +----------------------------------------+
                                                   | X  Local playback needs a one-time     |     InfoBar Error, title =
                                                   |    setup                               |     runtime.missing; body =
                                                   |    Playback support isn't available... |     model.Error ?? runtime.noPack
                                                   +----------------------------------------+
                                                   + (dl) Download from Spotify's CDN ------+     68   the Offer card again
   footer: primary "Try again"  -  secondary "Not now" -> DismissSetting + DeclineRuntime + MarkCompleted + Close
```

### W12 - Setup / footer anatomy (exact geometry, both breakpoints)

```
large (viewport >= 770)                                           plate 762
+- pad 24 -+-------- 210 --------+6+------ 246 ------+6+------ 246 ------+- pad 24 -+
|          | Step 1 of 2         | | +-------------+ | | +-------------+ |          |  Height 80
|          | (14/600 secondary,  | | |   PRIMARY   | | | |  SECONDARY  | |          |  AlignItems Center
|          |  1 line, char-elps) | | |  Accent, 32 | | | | Standard,32 | |          |  Fill FillSolidBase
|          | =====... 162 wide   | | +-------------+ | | +-------------+ |          |
|          +- 48 right pad ------+ |                 | |                 |          |
|            column box 210 x 32, Justify Center, AlignItems Start, AlignSelf Center, row Gap 6
+-----------------------------------------------------------------------------------+
compact (viewport < 770)
+- pad 24 -+-------------- 351 --------------+6+-------------- 351 --------------+- pad 24 -+
   The 48 is a RIGHT pad - Edges4 is (Left, Top, Right, Bottom). Passing it as the 2nd argument put the
   label and bar 48 DIP BELOW the lane and the bar past the plate's bottom edge (onboarding-v3 v3.2).
   Progress fraction: Terms 0 - SignIn .5 - LocalPlayback 1.0  (SetupGating.cs:173).
```

### W13 - Setup / the pre-auth window (what a cold install actually sees)

```
#################################################################################################
#  Wavee                                                                        -   []   X      #  TitleBar(ShowCaptionButtons: true)
#-----------------------------------------------------------------------------------------------#
#                                                                                               #
#          ........  live DWM Mica (the body paints ColorF.Transparent)  ........               #
#                                                                                               #
#                 ::::::: the engine's own popup scrim (ScrimVisual = bare = TRUE) ::::::       #
#                 :                                                              :              #
#                 :          +--- the 762 x 490 plate, centred ---+               :              #
#                 :          |                                    |               :              #
#                 :          +------------------------------------+               :              #
#                 ::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::               #
#################################################################################################
  ONE post (not the double post) - deferring a second frame cost a visible empty-window flash on launch AND an
  un-screenshottable first frame (SetupPreAuthRoot.cs:84-93).  No splash: the dialog IS the first thing shown.
  POST-AUTH the engine scrim is OFF (ScrimVisual = !bare = false) and the SHELL paints Tok.FillSmoke instead
  (SetupCoverScrim, WaveeShell.cs:2273-2289), cross-faded over WaveeMotion.Standard = 250 ms, Easing.Linear.
```

### W14 - What's new / fully loaded, one release @ content host 1136 (scale ~12 DIP/char here)

```
<--36--><----------------------- body 842 ---------------------------><14><-- rail 208 --><--36-->
+------------------------------------------------------------------------------------------------+
| (tag22) What's new                                                                             | 16+36+12  header row,
|                                                                                                |           gap 12, PageHero
+------------------------------------------------------------------------------------------------+
| +-------------------------------------------------------------+   | RELEASES            |      | Eyebrow tertiary
| | [Latest] [Stable] [0.2.9.10]                 +-------------+ |   | (o) 0.2.9      YOU  |      | pad 8, r6, gap 8
| |                                              |Open on GitHub| |   |     Breaker - 11 Sep|      | bullet 9 r4.5 bw1.5
| | Wavee 0.2.9 "Breaker"            30/600      +-------------+ |   |                     |      |
| |                                              +-------------+ |   | ( ) 0.2.8        .  |      | unread dot 6 r3 accent
| | One Connect ownership authority, a frame-time |  Copy link  | |   |     Crest - 29 Aug  |      |
| | lyrics clock, visual continuity.   14 sec    +-------------+ |   |                     |      |
| |                                                             |   | ( ) 0.2.7   [BETA]  |      | BetaPill 9.5/700
| | Released 5 Sep 2026   Requires 10.0.19041.0   Tag wavee-v0.2.9 |   |     Drift - 14 Aug  |      |
| +-------------------------------------------------------------+   |         ...         |      | hero: pad 24/22/24/20
|   ^ Fill FillCardSecondary, border 1 StrokeCardDefault, r12         |                     |      |     AlignItems End
|                                                                    | Notes are fetched   |      |
| HIGHLIGHTS                                              Eyebrow    | once per release... |      | RailFoot 11.5 tertiary
| +------------------+ +------------------+ +------------------+     +---------------------+      |
| | [Rebuilt]        | | [New]            | | [New]        (>) |              ^ ScrollView       |
| |  poster 16:9     | |  poster 16:9     | |  poster 16:9     |              ScrollKey          |
| +------------------+ +------------------+ +------------------+              "whatsnew:rail"    |
| | Title, 2 lines   | | Title            | | Title            |                                  | gap 10, AlignItems
| | body 4 lines,    | | body...          | | body...          |                                  | Stretch, card widths
| | last one faded   | |                  | |                  |                                  | Grow 1 Basis 0 MaxW 420
| | Read more >      | | Read more >      | | Read more >      |                                  |
| +------------------+ +------------------+ +------------------+                                  |
|                                                                                                 | gap 16 (Spacing.L)
| (+) Added                       7                                                               | badge 22 r6 wash+ink
| +---------------------------------------------------------------+                               | card r8, FillCardDefault,
| | PLAYER   Docked video now follows the pop-out...  (#118) closed  (C)(J)                       | | border 1 StrokeCardDefault
| |----------------------------------------------------------------|                             | 1-px divider between rows
| | Lyrics ink is sampled from the cover palette...  (!430) merged  (C)                           | | row pad 12/9/12/9, gap 12
| +---------------------------------------------------------------+                               |
|                                                                                                 |
| (v) Fixed                      14        [ Show all 14 ]                                        | Button.Subtle Small
| +---------------------------------------------------------------+                               | fold = 8 rows
| | ...8 rows...                                                    |                             |
| +---------------------------------------------------------------+                               |
|                                                                                                 |
| Issue states as of 5 Sep 2026                            11.5 tertiary, margin-top 6            |
|                                                          + 24-DIP tail spacer                   |
+-------------------------------------------------------------------------------------------------+
  Frame: header pad (36,16,36,12) gap 12 - content row pad (36,0,36,0) gap 14 - body column gap 12
  ScrollView ScrollKey "whatsnew:<selectedVersion>", inner gap 16, pad-right 6   (ReleaseNotesPage.cs:129-167)
  Section order per release: notices (InfoBar) -> Added -> Changed -> Fixed -> Removed -> Deprecated -> Security -> Known
  (document order, not sorted - ReleaseNotesPage.cs:114-120; empty sections are skipped entirely, :116)
  Conditionals in the hero: the [Latest] pill only when this IS the index's newest release; the mono quad pill only
  when doc.PackageVersion is set; "Released" whenever ReleaseNotesText.Date(doc.Date) is non-empty — which is any
  non-blank Date, NOT only a parseable one: Date() ECHOES an unparseable string verbatim rather than blanking it
  (ReleaseNotesLinks.cs:58-64), so a hand-authored "Q3 2026" prints as "Released Q3 2026". The same rule governs the
  stacked-release divider and the rail subtitle. "Requires" only when doc.MinOs is set; "Tag"
  is unconditional. A release with no codename prints whatsNew.headlineBare ("Wavee 0.2.9", no empty quotes).
  [Latest] shows when the index names this as its newest release — AND whenever the index is null or empty
  (IsLatest's bare `return true`, ReleaseNotesPage.cs:172), i.e. on every offline load.
  A release with NO highlights drops the eyebrow AND the row - HighlightStrip returns a 0-height, hit-invisible box.
```

### W15 - What's new / loading and W16 / empty and W17 / stacked

```
LOADING  (view == null && !loaded)                    EMPTY  (loaded && view == null)
+------------------------------------------+          +------------------------------------------+
| (tag) What's new                         |          | (tag) What's new                         |
+------------------------------------------+          +------------------------------------------+
|                                          |          |                                          |
|                   (o)                    |          |                 (tag 32)                 |  Icon 32 TextTertiary
|         ProgressRing.Create(28)          |          |   No release notes were found for this   |  13 TextSecondary
|         centred, Grow, no rail           |          |                version.                  |  wrap MaxWidth 360
|                                          |          |                                          |  gap 12
|                                          |          |      +---------------------------+       |
|                                          |          |      |     Open on GitHub        |       |  Button.Standard
+------------------------------------------+          +------------------------------------------+
  rail is NULL in both states (Frame(body, rail: null), ReleaseNotesPage.cs:80) - the whole 208 column + 14 gap is gone.

STACKED  (releases.Length > 1 && !onlyLatest)  - the banner sits ABOVE the ScrollView, never inside it
+----------------------------------------------------------------------------------------------+
| (sparkle14) Since you last looked: 3 releases - 0.2.7 -> 0.2.9. Showing  [ Only the latest ]  |  pad (12,8,12,8), r6
+----------------------------------------------------------------------------------------------+  Fill AccentSubtle
   everything new.                                12.5 TextPrimary, Grow, wrap                     Border 1 AccentDefault
   Between releases, a divider:                                                                    Button.Subtle Small, Shrink 0
   0.2.8  Crest  -  29 Aug 2026  ------------------------------------------------------------- 1px StrokeDividerDefault
   ^ 12/600 TextTertiary Shrink 0, gap 8, margin-top 8                            (ReleaseNotesPage.cs:176-205)
   Highlights MERGE across the stack, newest first, capped at 3 - a reader who skipped four releases wants the
   four best things, not four strips. Only VISIBLE highlights count against the cap (:292-308).
```

### W18 - Highlight card / regular @ 420 (page max) and @ 216 (dialog three-up)

```
 420 wide (page cap CardMaxW)                                216 wide (dialog three-up)
+----------------------------------------------------------+ +---------------------------+
| [New]                                            (play)  | | [New]                     |   KindPill: H 20, pad (8,0,8,0),
|                                                          | |                           |   r Radii.Full, Fill FillSolidTertiary
|              POSTER 420 x 236, Cover, r(8,8,0,0)         | |   POSTER 216 x 122        |   (store: AccentDefault), Shadow
|              AspectRatio 16:9, DecodePx 1200             | |                           |   (0,1,4, #00000066), text 11/600
|              Placeholder FillSubtleSecondary             | |                           |   overlay row: pad 8, HitTest false
|                                                          | +---------------------------+   PlayGlyph 32 r16 MediaScrim +
+----------------------------------------------------------+ | A real Logs tab           | 18   Icon(Play,14,OnMediaPrimary)
| A real Logs tab                                     13.5 | |                           | 18   only when media.kind == "video"
|                                                      /18 | | Settings > Logs is a      | 17
| Settings > Logs is a full-height log viewer: a command    | | full-height log viewer:   | 17   BODY SLOT: Height 68 fixed,
| bar to refresh, copy, export and open the log folder, a   | | a command bar to refresh, | 17   ClipToBounds, ZStack,
| search box with level and category filters, rows that     | | copy, export and open ... | 17   12.5 / LineHeight 17,
| ..... expand on a click to show fields and exception .... | | ..... the log folder .... |      MaxLines 0, TextSecondary
| Read more >                                          16  | | Read more >               | 16   EdgeFade(Bottom, 24) only when
+----------------------------------------------------------+ +---------------------------+      measured height > 68.5
  frame: Fill Tok.FillCardDefault (hover FillCardSecondary), Border 1 Tok.StrokeCardDefault, r Radii.Card(8),
         ClipToBounds, .Interactive(Interaction.Card) -> PressScale 0.985 on MotionTok.StandardSpring
  text block padding 12 / 10 / 12 / 12, column gap 4     card height = band + 150 (2-line title) or + 132 (1-line)
  "Read more" 12/600 TextSecondary -> HoverColor/FocusedColor Tok.AccentTextPrimary over 83 ms, + ChevronRight 10
  KIND PILL LABELS (HighlightCard.cs:143-148, loc whatsNew.kind.*): store -> "Microsoft Store" (accent) ·
  "rebuilt" -> "Rebuilt" · "improved" -> "Improved" · EVERYTHING ELSE, including an unknown/absent kind -> "New".
  (An unknown kind is NOT title-cased, whatever HighlightVisibility's own comment says - it falls into the New arm.)
```

### W19 - Highlight card / store variant @ 329 (dialog two-up)

```
+----------------------------------------------------------+  <- Border 1 Tok.AccentDefault
| [Microsoft Store]                                        |     KindPill Fill AccentDefault, text TextOnAccentPrimary
|                                                          |
|        tinted plate 329 x 185 - Fill Tok.AccentSubtle    | 185   no poster: the BAND still exists (a card that
|                                                          |       loses its top third reads as broken)
+----------------------------------------------------------+
| Wavee is on the Microsoft Store                          | 18   \
|                                                          | 18    |  HIT REGION (Role Button, Focusable, Hand,
| Install it from the Store and updates arrive the way     | 17    |  WhilePressed Scale .985, StandardSpring)
| every Store app's do - quietly, in the background,       | 17    |  padding 12 / 10 / 12 / **4**
| with no installer to run. Your library, settings and     | 17    |  NO "Read more" row
| ..... sign-in carry over unchanged ..................... | 17   /
| +----------------------------------------+               | 32   <- FOOTER, a SIBLING of the hit region:
| |    Get it from the Microsoft Store      |               |         pad (12, 4, 12, 12), Button.Accent
| +----------------------------------------+               |         so a card NEVER nests a button in a button
+----------------------------------------------------------+       card = 185 + 170 = 355
```

### W20 - Highlight viewer @ 1440 x 900 (plate 960, band 540)

```
 full-window veil rgba(0,0,0,184) - Fill == HoverFill == PressedFill (a dismiss surface must not tint under the cursor)
 root BoxEl Width = vp.W, Height = vp.H, ZStack, Justify/AlignItems Center, Focusable, OnKeyDown
::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::
::            <------------------------------ plate 960 ----------------------------->                    ::
::            +-----------------------------------------------------------------------+                   ::
::            | [Rebuilt]  8,8 inset                                     (X) 12,12     |                   ::  close: 36 circle,
::            |                                                                       |                   ::  ChromeClose @ 12
::            |                                                                       |                   ::
::            | (<)                POSTER 960 x 540 (Cover, DecodePx 1200,       (>)   | 540               ::  chevrons: 36 circles,
::            |  12                 Corners (8,8,0,0), Fade(140) reveal)          12   |                   ::  glyph 16, inset 12,
::            |                                                                       |                   ::  vertically centred
::            |                     [ (play) Watch the video on GitHub ]  <- video only|                   ::
::            |                                                                       |                   ::
::            +-----------------------------------------------------------------------+                   ::
::            |                         .  ====  .                                    | 24 (+8 margin-top) ::  pager: dots 6, selected
::            |                         <-8-><-8->                                    |                   ::  capsule 14, gap 8,
::            +-----------------------------------------------------------------------+                   ::  centred cluster
::            |  Setup is three screens                          20/600 LH 28          | 24 pad-l/r,       ::
::            |                                                                       |  8 pad-top (pager)::
::            |  Terms, sign in, local playback - that is the whole wizard now, in a   | 14/20 TextSecondary::
::            |  plain WinUI dialog with an animated hero beside it. The full          | +8 margin-top     ::
::            |  agreement prints inline; the pairing code is minted the moment the    | selectable        ::
::            |  sign-in page mounts, so it cannot expire while you read.              |                   ::
::            |                                                                       | +16 margin-top    ::
::            |  +-------------+                                                      | 32                ::
::            |  |  Try it ->  |   Button.Accent, only when the highlight has an       |                   ::
::            |  +-------------+   Open deep link (or the store CTA - mutually exclusive) 24 pad-bottom    ::
::            +-----------------------------------------------------------------------+                   ::
::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::
  plate: Fill FillSolidBase, Corners Radii.Overlay(8), Border 1 StrokeSurfaceDefault, ClipToBounds,
         Shadow(Blur 90, OffsetY 40, #00000099), MaxHeight = vpH - 64      (HighlightViewer.cs:182-196)
  W = round(min(max(320, min(960, vpW-96, (vpH-360)*16/9)), vpW-96)):
      1440x900 -> 960 | 1100x700 -> 604 | 900x600 -> 427 | 500x420 -> 320 | 320x600 -> 224 (the window edge WINS over the floor)
  Band height = round(W * 9/16) ALWAYS - poster or not (L4, issue #89). 960 -> 540, 427 -> 240, 224 -> 126.
```

### W21 - Highlight viewer @ 900 x 600 (plate 427, band 240) and the no-poster / single-slide cases

```
 plate 427, MaxHeight = 536                     no poster (e.g. the store slide)     single highlight (count == 1)
+-------------------------------+              +-------------------------------+    +-------------------------------+
| [New]                (X)      |              | [Microsoft Store]      (X)    |    | [New]                 (X)     |
|(<)   POSTER 427x240      (>)  | 240          |(<)  tinted band 427x240  (>)  |    |(<)  POSTER            (>)     |
+-------------------------------+              |     Fill Tok.AccentSubtle     |    |  DIMMED     ...      DIMMED   |  both ends:
|          .  ====  .           | 24+8         +-------------------------------+    +-------------------------------+  Opacity 0.3,
+-------------------------------+              |          .  ====  .           |    |   NO PAGER ROW AT ALL         |  MediaScrim
| Title 20/28                   |              +-------------------------------+    +-------------------------------+  @ A .40,
| body 14/20, SCROLLS when the  |              | (the tint lives on the STABLE |    | Title / body / CTA            |  not hit-
| plate would exceed vpH-64     |              |  band, never on the keyed     |    +-------------------------------+  testable
| - the IMAGE never scrolls     |              |  layer: two stacked translucent                                       Focusable
| +-----------+                 |              |  fills flashed ~8 levels      |     hasPager = count >= 2 only
| | Try it -> |                 |              |  brighter for ~100 ms on every                                        false
| +-----------+                 |              |  step, HighlightViewer.cs:203-207)  TextPadding: (24, 16, 24, 24)
+-------------------------------+                                                    with a pager: (24, **8**, 24, 24)
```

### W22 - After-update dialog / three cards (720 x 505) and / lone store card (720 x 603, the worst case)

```
+----------------------------------------------------------------------------------------------+
|                                                                                              | 22   pad (26, 22, 26, 18)
|   [Updated] [0.2.8 -> 0.2.9.10]                                                              | 22   Pill accent + Pill mono
|                                                                                              |  8   (Cascadia Code). LEFT side is
|                                                                                              |      AppUpdateVersion.ReleaseTagVersion(fromQuad)
|                                                                                              |      = the first THREE parts of the stored
|                                                                                              |      app.lastRunVersion quad; RIGHT is me.Quad
|                                                                                              |      verbatim (AfterUpdateDialog.cs:101)
|   Welcome to Wavee 0.2.9 "Breaker"                                      26/600 TextPrimary   | 35
|                                                                                              |  8
|   One Connect ownership authority, a frame-time lyrics clock, visual     14 TextSecondary     | 19   MaxWidth 560
|   continuity, pop-out drag.                                             wrap                 |      (19 per line)
|                                                                                              | 18
|                                                                                              |  6   RowPadTop
|   +------------------------+  +------------------------+  +------------------------+         |
|   | [Rebuilt]              |  | [New]                  |  | [New]                  |         |
|   |    POSTER 216 x 122    |  |    POSTER 216 x 122    |  |    POSTER 216 x 122    |  122    |
|   +------------------------+  +------------------------+  +------------------------+         |
|   | Title (<= 2 lines)     |  | Title                  |  | Title                  |   36    |  compact card:
|   | body 4 lines, last one |  | body...                |  | body...                |   68    |  MaxWidth 356
|   | ... faded ...........  |  |                        |  |                        |         |  (CompactCardMaxW)
|   | Read more >            |  | Read more >            |  | Read more >            |   16    |
|   +------------------------+  +------------------------+  +------------------------+  =272   |
|   <---------- 216 ---------->10<--------- 216 -------->10<--------- 216 ---------->           |
|                                                                                              | 14   RowPadBottom
+----------------------------------------------------------------------------------------------+
| [x] Don't show this after updates       +--------------------+ +----------------+             | 62   Fill Tok.FillLayerAlt
|                                         | Full release notes | |    Got it      |             |      Border 1 StrokeDividerDefault
+----------------------------------------------------------------------------------------------+      pad (26,14,26,14), gap 8
  plate: Width 720, MaxHeight 620, Corners Radii.Overlay(8), ClipToBounds, Fill FillSolidBase - no border, no own shadow
  (PopupChrome.Modal + ScrimVisual = TRUE supply the smoke and the scale/fade)      (AfterUpdateDialog.cs:138-144)
  Height table (HighlightCardMetrics.DialogHeight, pinned by HighlightCardMetricsTests):
      3-up regular 505 - 3-up w/ store 525 - 2-up 568 / 588 - lone regular 583 - LONE STORE 603 <= 620 cap
      3-up with a ONE-line tagline: 486
  Card widths: DialogCardWidth(n) = min((668 - (n-1)*10)/n, 356)   ->  3-up 216 - 2-up 329 - lone 356
```

### W23 - After-update dialog / still loading (the one place 0.2.9 POPS - see section 9)

```
+----------------------------------------------------------------------------------------------+
|   [Updated] [0.2.8 -> 0.2.9.10]                                                              |  hero renders IMMEDIATELY
|   Welcome to Wavee 0.2.9 "Breaker"                                                           |
|                                                        <- tagline is "" until the doc lands  |  loaded?.Doc.Tagline ?? ""
+----------------------------------------------------------------------------------------------+
| [ ] Don't show this after updates        | Full release notes | |    Got it    |              |  no card row at all
+----------------------------------------------------------------------------------------------+  (cards.Count == 0)
  When LoadAsync publishes, the tagline line fills in AND a ~292-DIP card row appears in one frame,
  growing the plate from ~213 to 505. There is no skeleton and no reserved height. (AfterUpdateDialog.cs:75-121)

  THIS IS ALSO THE FAILURE STATE, PERMANENTLY. LoadAsync returns early on `store is null` (:149), on a null
  document (:153) and on any exception (:167-168) WITHOUT publishing anything, so an offline first launch after an
  update shows exactly this frame — pills, welcome line, an empty tagline line, no cards — and never changes.
  Nothing says "couldn't load the notes"; "Full release notes" is the only way out. §9.4's fix (a) (open only once
  the load resolves) has to decide what happens when it resolves to NOTHING — the honest answer is "do not open the
  plate at all, and leave ReleaseNotesPendingFrom armed", which is a behaviour change to flag to the owner.
```

### W24 - Report dialog / Bug (ContentDialog 548, content 500)

```
+--------------------------------------------------------------+   <- ContentDialog card, MaxW 548, Pad 24,
| Report a problem                            20/600 (Title)   |      Fill/stroke/separator/footer = the stock card
+--------------------------------------------------------------+
| Opens a prefilled GitHub form in your browser and copies the  | 14 TextSecondary, wrap, MaxWidth 500
| redacted report to your clipboard. Nothing is sent until you  |
| click.                                                        |
|                                                               | gap 12 (Spacing.M) throughout
| +------+ +---------+ +----------+ +------+                    |    Segmented, bound to kindIndex
| | Bug  | | Feature | | Question | | Idea |                    |    (absent entirely in crash mode)
| +------+ +---------+ +----------+ +------+                    |
|                                                               |
| Title                                                         |    TextBox header
| +-----------------------------------------------------------+ |    Width 500
| | One line that says what's wrong                           | |    placeholder
| +-----------------------------------------------------------+ |
| What happened                                                 |
| +-----------------------------------------------------------+ |    AcceptsReturn, Height 72
| |                                                           | |
| +-----------------------------------------------------------+ |
| Steps to reproduce            [ ...72... ]                    |
| Expected behaviour            [ ...72... ]                    |
| Area                                                          |
| +------------------------------+                              |    ComboBox width 260,
| | Not sure                  v  |                              |    24 options, default = last ("Not sure").
|                                                               |    RAW ENGLISH SLUGS, never localized:
|                                                               |    playback, video, lyrics, player, connect,
|                                                               |    library, playlists, search, home, browse,
|                                                               |    concerts, detail-pages, sidebar, shell, auth,
|                                                               |    setup, updates, store, release-tooling,
|                                                               |    diagnostics, modules, i18n, engine, Not sure
|                                                               |    (ReportKinds.cs:43-48 — pinned to the issue
|                                                               |     form YAML by ReportChannelsTests; §0.12)
| +------------------------------+                              |
| [x] Include diagnostics and the last 200 log lines             |    CheckBox; default true for Crash/Bug
|                                                               |
| Report preview            [ Copy report ] [ Save as... ]      |    12/600 TextSecondary + Spacer + 2 Button.Subtle
| +-----------------------------------------------------------+ |    box 500 x 220, Fill FillSolidBase,
| | Wavee 0.2.9.10 - ARM64 - packaged - Windows 10.0.26340    | |    Border 1 StrokeDividerDefault,
| | ---------------------------------------------------------| |    Corners Radii.ControlAll (4), ClipToBounds
| | Title: Docked video flickers on resize                    | |    ScrollEl ScrollKey "report-preview",
| | What happened: ...                                        | |    EdgeCues None, content pad 10
| | [diagnostics block, already redacted once, off-thread]    | |    TextEl 11 "Cascadia Code" TextSecondary
| | [200 log lines]                                           | |    wrap, MaxWidth 480
| | … (37 KB more in the copied report)   <- PreviewChars cap  | |    cut at 12 000 chars, ENGLISH LITERAL tail
| +-----------------------------------------------------------+ |    (ReportBundle.cs:24, 188-190)
| Personal paths, account details and secrets are removed.      | 12 TextTertiary wrap
| Track names are kept.                                         |
+--------------------------------------------------------------+
|                       +------------------+ +---------------+  |    equal-width command buttons (the stock card)
|                       |   Open GitHub    | |    Cancel     |  |    DefaultButton = Primary
+--------------------------------------------------------------+
  Feature: Problem(72) / Proposal(72) / Area(combo 260) / Alternatives(72).   Question+Idea: Details(TextBox 140).
```

### W25 - Report dialog / Crash (fixed kind, no Segmented)

```
+--------------------------------------------------------------+
| Report a problem                                             |
+--------------------------------------------------------------+
| Opens a prefilled GitHub form... (same subtitle)              |
|                                          <- NO Segmented row |
| +-----------------------------------------------------------+ |   InfoBar, availableWidth 500, not closable.
| | X  Wavee closed unexpectedly                              | |   THREE honest states:
| |    Last run ended 14:07 - System.InvalidOperationException| |    c == null      -> Informational "Reading the last
| +-----------------------------------------------------------+ |                      crash report..."
| Title              [ prefilled from ReportPrefill.Title ]     |    CrashSummary>0 -> Error, "Last run ended {t} - {line}"
| When does it happen        [ On launch                  v ]   |    else           -> Error, "No crash report was written"
| Does it reproduce          [ Every time                 v ]   |
| What were you doing        [ ...72... ]                       |
| [x] Include diagnostics and the last 300 log lines             |   CrashLogLines = 300 (vs ManualLogLines = 200)
| Report preview  [ Copy report ] [ Save as... ]                 |
| [ 500 x 220 mono preview ]                                     |
| Personal paths, account details and secrets are removed...     |
| [ ] Don't ask again after a crash        -> WaveeSettings.CrashPromptOptOut
+--------------------------------------------------------------+
|                    +---------------------+ +--------------+   |
|                    |  Report on GitHub   | |   Not now    |   |
+--------------------------------------------------------------+
  Submit is VETOABLE: no composed report yet -> Toast(report.preparing, Informational) and args.Cancel = true;
  an empty title on a non-crash kind -> Toast(report.titleRequired, Warning) and Cancel.  (ReportDialog.cs:80, 202-217)
```

### W26 - Hover / pressed / focused (all three surfaces)

```
HIGHLIGHT CARD (REGULAR - the frame IS the hit region, .Interactive(Interaction.Card))
                                        rest                       hover                       pressed
  frame Fill                            Tok.FillCardDefault    ->   Tok.FillCardSecondary       (same as hover)
  frame Border                          StrokeCardDefault      ->   StrokeCardDefault (FLAT - HoverBorderColor is set
                                                                    to the SAME token deliberately)
  brush cross-fade                      WaveeMotion.Faster = 83 ms
  geometry                              -                      ->   -                      ->   Scale 0.985, StandardSpring

HIGHLIGHT CARD (STORE - frame + hit-region + footer are THREE nodes; the frame is NOT .Interactive)
  frame Fill                            Tok.FillCardDefault    ->   NO CHANGE (no hover ramp: the store frame never
                                                                    takes Interaction.Card, HighlightCard.cs:215-237)
  frame Border                          Tok.AccentDefault      ->   Tok.AccentDefault (HoverBorderColor is the same)
  hit region (band + title + slot)      -                      ->   -                      ->   WhilePressed Scale 0.985,
                                                                                                MotionTok.StandardSpring
                                                                                                (authored on the hit region
                                                                                                itself, not inherited)
  footer Button.Accent                  the stock accent-button ramp; a sibling, so pressing it never scales the card
  "Read more" label + chevron           TextSecondary          ->   AccentTextPrimary (83 ms; also on FOCUS)
  focus                                 engine ring (Tok.FocusOuter / FocusInner / FocusThickness) on the hit region

VIEWER CHROME CIRCLE                    enabled rest               enabled hover               disabled (end of list)
  Fill                                  Tok.MediaScrim         ->   MediaScrim @ A .70          MediaScrim @ A .40
  Opacity                               1                                                       0.3   (83 ms ease, not a pop)
  Scale                                 1                      ->   1.04 / press 0.96           1 (HoverIf/PressIf collapse)
  hit-test / focus / cursor             yes / yes / Hand                                        no / no / default

PAGER PIP                               unselected                 unselected hover            selected
  Width x Height                        6 x 6                      6 x 6                       14 x 6 (Reflow, 120 ms SmoothOut)
  Fill                                  rgba(255,255,255,82)   ->   rgba(255,255,255,140)       Tok.TextPrimary (120 ms)

RAIL ROW                                unselected                 selected
  Interaction preset                    Interaction.Subtle         Interaction.ListRow
  bullet                                9 x 9, r4.5, bw 1.5 TextTertiary, transparent  ->  AccentDefault border + AccentDefault fill
  version text                          12.5/600 TextSecondary                         ->  12.5/600 TextPrimary

SETTINGS CARD (browser / scan / detail) - the engine control's own WinUI ramp; a click-enabled card shows the Hand cursor
  and the chevron affordance. Never restyled by this surface (SetupText.Card is a thin pass-through, SetupText.cs:38-47).
```

### W27 - The two `--qr-dump` artefacts, to scale (headless; the one wireframe here with no window)

```
stdout (QrDump.cs:22-30)                                 file (QrDump.cs:32-39)
  y = -2 .. n+1, x = -2 .. n+1                             px = (n + 8) * 14
  every cell prints TWO characters wide, so a v3            v2 (25 mod) -> 33*14 = 462 x 462
  symbol is 33 rows x 66 columns                            v3 (29 mod) -> 37*14 = 518 x 518
                                                            v4 (33 mod) -> 41*14 = 574 x 574
  +-- 2-module margin (NOT the ISO 4) --+          +------------- quiet = 4 modules = 56 px -------------+
  |  ############  ##  ##  ############ |          |                                                     |
  |  ##        ##  ####    ##        ## |          |     +---------- n modules, 14 px each ----------+   |
  |  ##  ####  ##    ##    ##  ####  ## |          |     |  finder      timing        finder         |   |
  |  ##        ##  ##  ##  ##        ## |          |     |                                           |   |
  |  ############  ##  ##  ############ |          |     |        gray 0 = dark, gray 255 = light    |   |
  |                ##                   |          |     +-------------------------------------------+   |
  +-------------------------------------+          +-----------------------------------------------------+
   eyeball finders / timing / alignment             scannable FROM THE FILE; 14 px/module is 7x the 2-DIP cell
   NOT a scan target (margin is 2, ISO wants 4)      the sign-in card paints at 80 DIP (W3), so a PASS here
                                                     says nothing about the app's raster
```

**What each arm indicts.** A scannable `qr.png` proves the *bitstream* — `Qr.Encode`, the mask, the RS codewords. The
82-DIP plate (W3, parity 11) proves the *raster*. Keep both and say which one a failure points at (`QrDump.cs:8-12`):
a clean dump beside an unscannable in-app QR means the renderer, not the encoder.

---

## 3. Tokens

Token *definitions* live in `00-design-system.md`; this table is the per-element **usage** contract.
`-` = not set (inherits / not applicable).

### 3.1 Setup wizard

| element | size (DIP) | padding / gap | radius | type style | colour / brush | material / elevation | source |
|---|---|---|---|---|---|---|---|
| plate | 762 x 490, min 320 x 184 | pad 0 | `Radii.OverlayAll` = 8 | - | `Tok.FillSolidBase`, border 1 `Tok.StrokeSurfaceDefault` | `Elevation.Dialog` (dark 64/16/`#00000066`, light 48/12/`#00000030`) | `SetupDialog.cs:151-162` |
| content region | Grow | pad `Edges4.All(24)` | - | - | `Tok.FillLayerAlt` (ContentDialogTopOverlay) | - | `SetupDialog.cs:129-135` |
| separator | H 1, AlignSelf Stretch | - | - | - | `Tok.StrokeCardDefault` | - | `SetupDialog.cs:143` |
| footer | H 80 | pad `All(24)`, gap 6 | - | - | `Tok.FillSolidBase` | - | `SetupDialog.cs:247-254` |
| progress column | 210 x 32 | pad-right 48, stack gap 6 | - | - | - | - | `SetupDialog.cs:231-233` |
| step label | 14 / 600 | - | - | (inline) | `Tok.TextSecondary`, MaxLines 1, CharacterEllipsis | - | `SetupDialog.cs:236` |
| progress bar | W 162 | - | - | - | `ProgressBar.Determinate(Progress(page))` | - | `SetupDialog.cs:237`, `SetupLayout.cs:35` |
| primary button | Grow 1 Basis 0, H 32 | Justify Center | control | - | `Button.Accent` (stock WinUI AccentButtonStyle) | - | `SetupDialog.cs:263-269` |
| secondary button | Grow 1 Basis 0, H 32 | Justify Center | control | - | `Button.Standard` | - | `SetupDialog.cs:257-259` |
| back button | 30 x 30, glyph 12 | over the 24 pad corner | - | `Icons.Back` | `IconButton.DefaultStyle` | - | `SetupDialog.cs:167-173`, `SetupLayout.cs:36` |
| icon column | W 192, Shrink 0, gap 24 to content | Justify/AlignItems Center, AlignSelf Stretch | - | - | recoloured Lottie (S 4) | - | `SetupPageHost.cs:93-98` |
| page header | LineHeight 36 | margin `(0, -4, 0, 4)` | - | `Ui.Title` 28/36/600 | `Tok.TextPrimary`, wrap, MaxLines 2, WordEllipsis | - | `SetupPageHost.cs:60-64, 108-109` |
| back spacer | W 42, Shrink 0 | - | - | - | - | only when `!iconShown && page == LocalPlayback` | `SetupLayout.cs:29`, `SetupGating.cs:182` |
| body scroller | Grow/Shrink, MinH 0 | margin-right **-24**, pad-right **+24** | - | - | `EdgeCues.None`, `AlwaysShowScrollbar = true` | - | `SetupPageHost.cs:66-78` |
| body stack | - | gap 20 (`BodySpacing`) | - | - | - | - | `SetupText.cs:17-18` |
| inner group | - | gap 12 (`BodyInnerSpacing`) | - | - | - | - | `SetupText.cs:22-23` |
| `Lead` | 14 / 20 / 600 | - | - | `Ui.BodyStrong` | `Tok.TextPrimary`, wrap, MinWidth 0 | - | `SetupText.cs:27` |
| `Body` | 14 / 20 / 400 | - | - | `Ui.Body` | `Tok.TextPrimary`, wrap | - | `SetupText.cs:30` |
| `Secondary` | 14 / 20 / 400 | - | - | `Ui.Body().Secondary()` | `Tok.TextSecondary`, wrap | - | `SetupText.cs:34` |
| `Card` | MinHeight 68 | pad 16 | control | engine `SettingsCard` | header icon <= 20; Content slot wraps below the header (+8) at width < **476**, and the header icon is dropped entirely at width < **286** | `BrushTransitionMs` 83 | `SetupText.cs:38-47`, `SettingsCard.cs:24-32, 129-130` |
| account row | MinHeight 68 | pad `All(16)`, gap 16 | `Radii.Control` = 4 | - | `Tok.FillCardDefault`, border 1 `Tok.StrokeCardDefault` | - | `SetupPage.SignIn.cs:197-219` |
| account avatar | `PersonPicture` 40 | Shrink 0 | circle | - | photo or initials | - | `SetupPage.SignIn.cs:206` |
| account name / tier | 14/20/600 ; 12/16 | column gap 2 | - | `BodyStrong` / `Caption` | `Tok.TextPrimary` / `.Secondary()` | - | `:212-213` |
| premium row | **no authored Height** — 32 is the *budget's* model (`SetupLayout.LinkRowHeight`); the row measures its HyperlinkButton's 32-DIP lane, and **64 once it wraps** | gap 4 (`Spacing.XS`), `Wrap = true`, AlignItems Center | - | `Ui.Body().Secondary()` + `HyperlinkButton` | `Tok.TextSecondary` | - | `:108-116` |
| "Getting your code" | ring 20, text 12.5 | gap 8 (`Spacing.S`) | - | - | `Tok.TextTertiary` | - | `:143-151` |
| login step row | H 26, W 300 (in the wizard) | gap 8, mark box 18 x 18 | - | label 12 / 16 | current 600 `TextPrimary` / done 400 `TextSecondary` / pending `TextTertiary` | - | `LoginView.cs:84-105` |
| login step mark | ring 16 ; glyph 15 (done/failed) / 11 (pending) | - | - | `Theme.IconFont` | done `Tok.AccentDefault`, failed `Tok.SystemFillCritical`, pending `Tok.TextTertiary` | - | `LoginView.cs:67-74` |
| login step bar | W 220 | - | - | - | `ProgressBar.Determinate(step/4)`, `ProgressBarState.Error` on Failed | Fill part gets a `Size/Reflow/Leading/Width` layout transition | `LoginView.cs:113-138` |
| QR plate | `(modules + 8) x cell`, cell = `max(2, 80/(modules+8))` -> **82** for v4 | modules box pad `4 x cell` = 8 | `Radii.Card` = 8 | - | plate `#FFFFFF`, modules `#000000` | AlignSelf Center, ClipToBounds | `QrGrid.cs:51-66`, `QrPlate.cs:20-22` |
| QR fallback | `size x size` | centred | `Radii.Card` | `Icons.MusicNote` 28 | plate `#FFFFFF`, glyph `#1DB954` | - | `QrGrid.cs:68-74` |
| `LoginView.CodeFont` — **DEAD in 0.2.9**: declared, referenced by nothing | - | - | - | `"Consolas"`, the monospace face it was reserved for | - | - | `LoginView.cs:35` |
| countdown, compact — **DEAD in 0.2.9**: `LoginCountdown` is never mounted anywhere | 11.5 | - | - | - | `Tok.TextTertiary` | - | `LoginView.cs:203` |
| countdown, pill — **DEAD in 0.2.9** (same component) | 12 | pad `(10,4,11,5)`, gap 4 | 11 | `Icons.Clock` 12 | `Tok.FillSubtleSecondary`, glyph `TextTertiary`, text `TextSecondary` | - | `LoginView.cs:205-214` |
| scan-card description (**the live one**) | 12 (`SettingsCard.DescriptionFontSize`) — NOT monospace | - | - | `setup.signIn.pairLine(code)` = `"{code} · spotify.com/pair"`, then `"  ·  "`, then `auth.expiresIn(mm:ss)` | the card's own description ink | - | `SetupPage.SignIn.cs:250-251`, `SettingsCard.cs:69` |
| shell cover scrim | Grow | - | - | - | `Tok.FillSmoke` | cross-fade 250 ms Linear | `WaveeShell.cs:2273-2289` |

### 3.2 What's-new page

| element | size | padding / gap | radius | type style | colour / brush | material | source |
|---|---|---|---|---|---|---|---|
| page header row | - | pad `(36, 16, 36, 12)`, gap 12 | - | `WaveeType.PageHero` = `Ui.Title` 28/36/600 | icon `Icons.Tag` 22 `Tok.TextPrimary` | - | `ReleaseNotesPage.cs:150-159` |
| content row | Grow | pad `(36, 0, 36, 0)`, gap 14 | - | - | - | - | `:160-166` |
| body column | Grow | gap 12 (`Spacing.M`) | - | - | - | - | `:137` |
| scroll content | - | gap 16 (`Spacing.L`), pad-right 6 | - | - | `ScrollKey "whatsnew:<version>"` | - | `:129-134` |
| hero | AlignItems **End** | pad `(24, 22, 24, 20)`, gap 12 | 12 | - | `Tok.FillCardSecondary`, border 1 `Tok.StrokeCardDefault` | - | `ReleaseNotesHero.cs:36-40` |
| hero pills row | - | gap 8, Wrap | - | - | - | - | `:48` |
| pill | MinHeight 22 | pad `(9, 2, 9, 2)` | `Radii.Pill` = 16 | 11.5 / 600, MaxLines 1 | accent: `Tok.AccentDefault` + `Tok.TextOnAccentPrimary`; else `Tok.FillSubtleSecondary` + `Tok.TextSecondary`; mono: `"Cascadia Code"` | - | `:81-95` |
| headline | 30 / 600 | - | - | - | `Tok.TextPrimary`, wrap | - | `:49-50` |
| tagline | 14 | MaxWidth 640 | - | - | `Tok.TextSecondary`, wrap | - | `:51-52` |
| meta row / item | 12 | gap 14 (row), gap 5 (item), margin-top 4 | - | - | label 12/600 `TextSecondary`, value 12 `TextTertiary` | - | `:53, 97-105` |
| hero buttons | Small | column gap 8, AlignItems End | control | - | `Button.Standard` / `Button.Subtle` | - | `:56-66` |
| highlights eyebrow | 12 / 16 / 600, tracking 30/1000 em | column gap 6 | - | `WaveeType.Eyebrow` | `Tok.TextTertiary` | - | `HighlightStrip.cs:41`, `WaveeType.cs:38, 54` |
| card row | AlignItems **Stretch** | gap 10 | - | - | - | - | `HighlightStrip.cs:43-46` |
| since banner | AlignSelf Stretch | pad `(12, 8, 12, 8)`, gap 10 | 6 | text 12.5 | `Tok.AccentSubtle`, border 1 `Tok.AccentDefault`, icon `Icons.RefineSparkle` 14 `Tok.AccentTextPrimary` | - | `ReleaseNotesPage.cs:176-192` |
| release divider | - | gap 8, margin-top 8 | - | 12 / 600 | `Tok.TextTertiary` + a 1-px `Tok.StrokeDividerDefault` rule | - | `:194-205` |
| "as of" line | 11.5 | margin-top 6 | - | - | `Tok.TextTertiary`, wrap | - | `:122-124` |
| tail spacer | H 24 | - | - | - | HitTestVisible false | - | `:125` |
| empty state | icon 32, text 13 | gap 12, MaxWidth 360 | - | - | icon + text `TextTertiary`/`TextSecondary` | - | `:207-218` |
| loading | ring 28 | centred, Grow | - | - | `ProgressRing.Create(28)` | - | `SettingsShared.cs:46-51` |
| section column (header + card) | AlignSelf Stretch | gap 6 | - | - | - | - | `ChangelogSection.cs:71` |
| section header | MinHeight 28 | gap 8 | - | title 14/600, count 12 | title `TextPrimary`, count `TextTertiary` (count `Grow 1`, so "Show all" is pushed right) | - | `ChangelogSection.cs:74` |
| section badge | 22 x 22, glyph 12/700 | centred | 6 | plain characters `+ ~ v - !` (not the icon font) | see S 4.4 | - | `ChangelogSection.cs:56-60, 88-97` |
| section card | AlignSelf Stretch | - | `Radii.Card` = 8 | - | `Tok.FillCardDefault`, border 1 `Tok.StrokeCardDefault`, ClipToBounds | - | `:76-81` |
| row divider | H 1 | - | - | - | `Tok.StrokeDividerDefault` | - | `:49` |
| changelog row | - | pad `(12, 9, 12, 9)`, gap 12, AlignItems **Start** | - | paragraph 13 | `Tok.TextPrimary` spans | - | `ChangelogItem.cs:47-56` |
| changelog row inner column (paragraph + refs) | Grow 1 Basis 0 | gap 6 | - | - | - | - | `ChangelogItem.cs:53` |
| scope eyebrow span | 11 / 700 | trailing `"   "` | - | inline span, UPPERCASED | `Tok.TextTertiary` | - | `ChangelogItem.cs:26` |
| inline code span | 12 | - | - | `"Cascadia Code"` | `Tok.TextSecondary` | - | `ReleaseNotesText.cs:36` |
| issue chip | H 18, dot 7 | pad `(6, 0, 8, 0)`, gap 4 | 9 (`Height/2`) | number 11/600 `"Cascadia Code"`, word 11 | number `TextPrimary`, word `TextTertiary`; dot open `SystemFillSuccess` / not-planned `TextTertiary` / closed+merged `AccentDefault` | `.Interactive(Interaction.Control)` | `IssueChip.cs:20, 49-69` |
| avatar disc | 18 x 18, initials 9/700 | overlap margin-left -5 from #2 | 9 | - | deterministic tint (S 4.3), border 1.5 `Tok.FillSolidBase`, text `Tok.TextOnAccentPrimary` | - | `Avatars.cs:18, 62-70` |
| avatar overflow | 10 / 600 | margin-left 4 | - | `"+N"` | `Tok.TextTertiary` | max 4 shown | `Avatars.cs:73-75` |
| rail | W 208, Shrink 0 | gap 6 | - | - | - | - | `ReleaseRail.cs:25, 41-43` |
| rail eyebrow / foot | eyebrow 12/16/600, foot 11.5 | margins `(4,6,4,2)` / `(4,6,4,6)` | - | `WaveeType.Eyebrow` | `Tok.TextTertiary` | - | `:46-47, 54-55` |
| rail row | - | pad `All(8)`, gap 8, column gap 1 | 6 | version 12.5/600, subtitle 11 | selected `TextPrimary` else `TextSecondary`; subtitle `TextTertiary` MaxLines 1 | `Interaction.ListRow` (selected) / `Interaction.Subtle` | `:75-102` |
| rail bullet | 9 x 9, bw 1.5 | margin `(2, 4, 0, 0)` | 4.5 | - | selected `Tok.AccentDefault` fill+border; else transparent + `Tok.TextTertiary` border | - | `:84-90` |
| unread dot | 6 x 6 | - | 3 | - | `Tok.AccentDefault`; shown **only** when `!isYou && !isSelected && IsNewer(version, lastSeen)` | - | `:65, 71-72` |
| YOU / BETA pill | 9.5 / 700 | pad `(5, 1, 5, 1)` | 6 | - | YOU `AccentDefault` + `TextOnAccentPrimary`; BETA `FillSubtleSecondary` + `TextTertiary` | - | `:113-123` |

### 3.3 Highlight card, viewer, after-update dialog

| element | size | padding / gap | radius | type style | colour / brush | material | source |
|---|---|---|---|---|---|---|---|
| card frame (regular) | Grow 1 Basis 0, MaxWidth 420 (page) / 356 (dialog) | - | `Radii.Card` = 8, ClipToBounds | - | `Tok.FillCardDefault` -> hover `Tok.FillCardSecondary`, border 1 `Tok.StrokeCardDefault` (flat) | `.Interactive(Interaction.Card)` + `BrushTransitionMs = 83` | `HighlightCard.cs:202-221` |
| card frame (**store**) | same box | - | same | - | `Tok.FillCardDefault` with **no hover ramp**, border 1 `Tok.AccentDefault` = `HoverBorderColor` | **not** `.Interactive` — the press spring lives on the hit-region sibling (`WhilePressed Scale .985`, `MotionTok.StandardSpring`) | `HighlightCard.cs:215-237` |
| poster band | AspectRatio 16/9, AlignSelf Stretch | - | ClipToBounds; image corners `(8, 8, 0, 0)` | - | band fill `Tok.FillSubtleSecondary` (store `Tok.AccentSubtle`); `ImageEl` `Fit=Cover`, `DecodePx = 1200`, `Placeholder = Tok.FillSubtleSecondary` | - | `HighlightCard.cs:63-99, 43` |
| band overlay row | Grow | pad `All(8)`, AlignItems Start | - | - | HitTestVisible false | - | `:86-91` |
| kind pill | H 20 | pad `(8, 0, 8, 0)` | `Radii.Full` = 999 (clamped to H/2) | 11 / 600 | `Tok.FillSolidTertiary` + `Tok.TextPrimary` (store `Tok.AccentDefault` + `Tok.TextOnAccentPrimary`) | `ShadowSpec(Blur 4, OffsetY 1, #00000066)` | `:141-158` |
| play glyph | 32 x 32, icon 14 | centred | 16 | `Icons.Play` | `Tok.MediaScrim` + `Tok.OnMediaPrimary` | - | `:164-173` |
| card title | 13.5 / LineHeight 18 / 600 | column gap 4 | - | MaxLines 2, CharacterEllipsis | `Tok.TextPrimary`, wrap | - | `:240-244`, `HighlightCardMetrics.cs:21-26` |
| body slot | **H 68** fixed | - | ClipToBounds, ZStack | paragraph 12.5 / LineHeight 17, MaxLines **0** | `Tok.TextSecondary` | `EdgeFade(EdgeMask.Bottom, 24)` when measured H > 68.5 | `:273-314`, `HighlightCardMetrics.cs:28-40` |
| text block padding | - | `(12, 10, 12, 12)`; store hit region bottom **4** | - | - | - | - | `HighlightCardMetrics.cs:45, 50` |
| read-more row | H 16, chevron 10 | gap 4 | - | 12 / 600, NoWrap | `Tok.TextSecondary` -> Hover/Focused `Tok.AccentTextPrimary`, 83 ms | HitTestVisible false | `:318-337` |
| store footer | button H 32 | pad `(12, 4, 12, 12)` | - | - | `Button.Accent` | sibling of the hit region | `:232-236` |
| viewer veil | vp.W x vp.H | - | - | - | `ColorF.FromRgba(0, 0, 0, 184)` = rgba(0,0,0,.72); Fill == HoverFill == PressedFill | `ScrimVisual = false` (no second smoke) | `HighlightViewer.cs:48, 113-126` |
| viewer plate | W = `PlateWidth(vpW, vpH)`, MaxHeight `vpH - 64` | - | `Radii.Overlay` = 8, ClipToBounds | - | `Tok.FillSolidBase`, border 1 `Tok.StrokeSurfaceDefault` | `ShadowSpec(Blur 90, OffsetY 40, #00000099)` | `:182-196` |
| viewer band | H = `round(W * 9/16)` | - | ClipToBounds | - | `Tok.FillSubtleSecondary` / store `Tok.AccentSubtle`, `BrushTransitionMs = 250` | poster `RevealTransition = ImageTransition.Fade(140)` | `:208-242` |
| chrome circle | 36 x 36; glyph 16 (chevrons) / 12 (close) | inset 12 from the band edge | 18 | `Icons.ChevronLeft/Right/ChromeClose` | `Tok.MediaScrim`; hover `MediaScrim @ A .70`; disabled `MediaScrim @ A .40` + Opacity 0.3 | `BrushTransitionMs 83`, `MotionTok.ControlFaster` | `:290-310`, `HighlightViewerLayout.cs:28-29` |
| watch pill | icon 14, text 12.5/600 | pad `(14, 8, 16, 8)`, gap 6 | `Radii.Pill` = 16 | - | `Tok.MediaScrim` + `Tok.OnMediaPrimary`, hover `@ A .70` | `Role = Hyperlink` | `:316-329` |
| pager strip | H 24, margin-top 8 | Justify Center | - | - | - | tooltip = `whatsNew.viewer.position` on the CLUSTER, not the strip | `:341, 378-392` |
| pip | 6 x 6; selected **14** x 6 | gap 8 | `Radii.Full` | - | off `rgba(255,255,255,82)` / hover `rgba(255,255,255,140)`; on `Tok.TextPrimary` | `Size/Reflow/Width` tween 120 ms `SmoothOut`; `BrushTransitionMs 120` | `:338, 350-369` |
| viewer text padding | - | `(24, hasPager ? 8 : 16, 24, 24)` | - | - | - | shared by the live slide AND every sizer | `:436` |
| viewer title | 20 / LineHeight 28 / 600 | - | - | - | `Tok.TextPrimary`, wrap | - | `:460` |
| viewer body | 14 / LineHeight 20 | margin-top 8 | - | markdown-lite spans, selectable | `Tok.TextSecondary` | - | `:464-466` |
| viewer actions | button H 32 | gap 8, margin-top 16 | - | - | `Button.Accent` | column MaxWidth 720, AlignSelf Start | `:468-471` |
| dialog plate | W 720, MaxHeight 620 | - | `Radii.Overlay` = 8, ClipToBounds | - | `Tok.FillSolidBase` | Modal chrome + `ScrimVisual = true` | `AfterUpdateDialog.cs:138-144` |
| dialog hero | - | pad `(26, 22, 26, 18)`, gap 8 | - | welcome 26/600, tagline 14 MaxWidth 560 | `Tok.TextPrimary` / `Tok.TextSecondary` | - | `:89-109` |
| dialog card row | AlignItems Stretch | pad `(26, 6, 26, 14)`, gap 10 | - | - | - | - | `:112-121` |
| dialog footer | - | pad `(26, 14, 26, 14)`, gap 8 | - | - | `Tok.FillLayerAlt`, border 1 `Tok.StrokeDividerDefault` | - | `:123-136` |

### 3.4 Report dialog

| element | size | padding / gap | radius | type style | colour / brush | source |
|---|---|---|---|---|---|---|
| dialog card | W 548 (`ContentDialog` MaxW), content 500 | pad 24 | overlay | title 20/600 | stock `ContentDialog` chrome | `ReportDialog.cs:35-36, 61`, `ContentDialog.cs:110-117` |
| body column | W 500 | gap 12 (`Spacing.M`) | - | - | - | `:138-142` |
| subtitle | 14, MaxWidth 500 | - | - | - | `Tok.TextSecondary`, wrap | `:113-114` |
| kind switch | - | - | - | `Segmented` 4 items | stock | `:118-125` |
| single-line field | W 500 | - | control | `TextBox` header + placeholder | stock | `:228-233` |
| multi-line field | W 500, **H 72** | - | control | `AcceptsReturn` | stock | `:159-160, 297-318` |
| Question/Idea body | W 500, **H 140** | - | control | - | stock | `:322-323` |
| combo | **W 260** | - | control | `ComboBox` + header | stock | `:162, 295-296` |
| preview header row | W 500, Shrink 0 | gap 8 | - | 12 / 600 | `Tok.TextSecondary` + 2 x `Button.Subtle` | `:240-255` |
| preview box | W 500, **H 220** | inner pad 10 | `Radii.ControlAll` = 4 | 11 `"Cascadia Code"` | `Tok.FillSolidBase`, border 1 `Tok.StrokeDividerDefault`, ClipToBounds; text `Tok.TextSecondary`, wrap MaxWidth 480; content is `ReportBundle.Preview` — cut at `PreviewChars` 12 000 with an English `"… (N KB more in the copied report)"` tail; `report.preparing` until the compose lands | `:159-162, 256-272, 391-397`, `ReportBundle.cs:24, 188-190` |
| preview note | 12, MaxWidth 500 | - | - | - | `Tok.TextTertiary`, wrap | `:273-274` |
| crash InfoBar | availableWidth 500 | - | - | - | `InfoBarSeverity.Error` (or `Informational` while composing) | `:328-344` |

### 3.5 `--qr-dump`'s raster constants (§1.5)

Not theme tokens, and none of them may become one.

| | value | source | must match |
|---|---|---|---|
| ECC level | `Qr.Ecc.M` | `QrDump.cs:18` | `QrGrid.cs:24` — same level, or the dump is a different symbol |
| PNG quiet zone | `4` modules | `QrDump.cs:32` | `QrGrid.cs:28` (`const int quiet = 4`) |
| ASCII margin | `2` modules | `QrDump.cs:24` | **deliberately below ISO's 4** — structural only, never a scan target |
| module scale (PNG) | `14` px, fixed | `QrDump.cs:32` | vs the app's `max(2, size/(modules+8))` = **2** at the wizard's 80-DIP ask (`QrPlate.cs:20`) |
| dark / light | gray `0` / `255` | `QrDump.cs:37` | the `#000000` / `#FFFFFF` literals in `QrGrid.cs:61, 66` (§4.2) |
| matrix indexing | `m[x, y]` | `QrDump.cs:27, 37` | `QrGrid.cs:43` — both column-major; a transposed read still looks plausible |

§4.2's rule (a tinted or inverted QR loses binarization margin on a phone camera) governs the probe's output for the
same reason: gray 0 and gray 255 only, no grid line, no logo.

---

## 4. Colour & material

### 4.1 The Lottie hero recolour (`WaveeLottieRecolor.cs`) - the only derived palette in the setup wizard

```
input: every fill / stroke / gradient mid-stop colour in eula.json | connect.json | patch.json
       (Bodymovin 5.6.5 exports of the Windows 11 OOBE scenes, as redistributed by Rise Media Player)

  1. |c - #0078D4| <= 1/64 per channel   ->  Tok.AccentDefault    with { A = c.A }        (:49)
  2. |c - #002B67| <= 1/64 per channel   ->  Tok.AccentTextPrimary with { A = c.A }       (:50)
  3. (h, s, l) = WaveePalette.ToHsl(c)
     s >= 0.35  AND  195 <= h <= 285     ->  WaveePalette.FromHsl(h + (H(accent) - H(#0078D4)), s, l, c.A)   (:52-56)
  4. otherwise                           ->  c unchanged      (the neutrals #FFFFFF #EEEEEE #F1F0EF #E0DEDC and the teal)
```

- **Alpha is ALWAYS the shape's own**, never the token's — a fill's opacity belongs to the artwork (`:45-46`).
- Applied **once per mount**, not per frame (`LottieView` calls `Recolor` at mount), so re-reading the live `Tok` state
  is cheap and correctness (today's theme/accent) wins (`WaveeLottie.cs:38`-comment / `WaveeLottieRecolor.cs:36-39`).
- **A theme or accent change does not repaint a mounted hero.** The wizard remounts the hero per page (each KeepAlive
  slot owns its own `LottieView`), so the next page picks up the new accent; the page you are on does not. This is the
  0.2.9 behaviour — keep it, do not "fix" it with a per-frame recolour.
- `LottieOptions.RiseSetup with { Recolor = ..., Zoom = 1.2f }`: `Loop = false`, `To = 0.5f` (the first half of the
  timeline, once, then hold), `AutoPlay = true`, `ReducedMotion = SnapEnd`. The 1.2x zoom is Wavee's one deliberate
  deviation from a literal Rise readout (the OOBE scenes carry ~25 % empty margin at their authored fit);
  it is still centred and clipped to the 192 box (`WaveeLottie.cs:47`).
- **Reduced motion** is resolved at mount inside the control (`SnapEnd` -> the static pose at `To = 0.5`), never by an
  author-side `if`.

### 4.2 The QR (`QrGrid.cs`)

`#FFFFFF` plate, `#000000` modules — literals, deliberately **not** tokens and **not** theme-aware: a tinted or
dark-mode-inverted QR loses binarization margin on a phone camera (`QrGrid.cs:61, 66`). Light modules are
`ColorF.Transparent` so the plate shows through, and consecutive dark modules in a row coalesce into ONE `BoxEl`
(a few hundred nodes instead of `version^2`, `:39-49`). No centre logo. The fallback plate (unencodable text) keeps the
same white plate and draws `Icons.MusicNote` at 28 in `#1DB954` — **the only Spotify green left anywhere in this
surface** (`:73`).

**The ink is declared twice.** `--qr-dump` re-states it as gray `0` / `255` in its own PNG writer (`QrDump.cs:37`,
§3.5). However 0.3 consolidates the QR ink, both sites move together — or the bisector stops bisecting.

### 4.3 Contributor avatar tint (`Avatars.cs:24-37`)

```
h = 0; foreach ch in login: h = (h * 31 + ch) & 0x7fffffff
tint = h % 6 -> #4A7AC0 | #C07A4A | #4AA87A | #8A6AC0 | #C05A7A | #5A8A8A   (all A = 0xFF)
```

Deterministic and fixed — the same login always gets the same tint, so a contributor is recognisable down a page with
nobody storing a colour. Initials: `"christosk92" -> "C"`, `"jane-doe" -> "JD"` (first letter, plus the letter after the
first `-`/`_`/`.`), two letters maximum; the login is the tooltip (`:39-47`). A 1.5-px `Tok.FillSolidBase` ring cuts the
disc out of the one behind it at the -5 overlap.

### 4.4 Changelog kind badge (`ChangelogSection.cs:88-97`)

| kind | glyph | ink | wash |
|---|---|---|---|
| `added` | `+` | `Tok.SystemFillSuccess` | `Tok.SystemFillSuccessBackground` |
| `changed` | `~` | `Tok.AccentTextPrimary` | `Tok.AccentSubtle` |
| `fixed` | `v` (U+2713) | `Tok.SystemFillCaution` | `Tok.SystemFillCautionBackground` |
| `removed` | `-` (U+2212) | `Tok.SystemFillCritical` | `Tok.SystemFillCriticalBackground` |
| `deprecated` | `!` | `Tok.SystemFillCaution` | `Tok.SystemFillCautionBackground` |
| `security` | `!` | `Tok.SystemFillCritical` | `Tok.SystemFillCriticalBackground` |
| anything else (`known`) | `!` | `Tok.TextTertiary` | `Tok.FillSubtleSecondary` |

Plain characters, not the icon font: "+ ~ v - !" is what a changelog reads as, and the icon font has no arithmetic
glyphs at this weight.

### 4.5 Notices (`ReleaseNotesPage.cs:107-112`)

`notice.Kind` -> `InfoBarSeverity`: `"breaking"` and `"warning"` (both, case-insensitive) -> `Warning`; everything else
-> `Informational`. Title = the notice text, body = `""`, `isClosable: false`.

### 4.6 Scrims, veils and materials — who paints what

| surface | who paints the dim | value | why | source |
|---|---|---|---|---|
| setup wizard, **pre-auth** (`bare: true`) | the engine popup chrome (`ScrimVisual = true`) | the chrome's own smoke | nothing behind the plate but bare DWM Mica; the built-in scrim tints exactly that | `SetupDialog.cs:46-47` |
| setup wizard, **post-auth** (`bare: false`) | the **shell** (`SetupCoverScrim`) | `Tok.FillSmoke`, cross-faded 250 ms Linear | the shell, not the popup host, is what is actually behind the plate; only the shell knows whether the page wants a dim | `WaveeShell.cs:2273-2289`, `SetupSession.Covering` |
| after-update dialog | the engine popup chrome (`ScrimVisual = true`) | the chrome's own smoke | one plate over the shell | `AfterUpdateDialog.cs:48-49` |
| **highlight viewer** | the **view itself** (`ScrimVisual = false`) | `rgba(0,0,0,184)` local literal | it can open ON TOP of the after-update dialog, where the chrome's smoke would stack; `Tok.MediaScrim` (.55) reads too thin there | `HighlightViewer.cs:36-37, 48` |
| report dialog | stock `ContentDialog` | the control's own | ordinary dialog | `ReportDialog.cs:58` |

The **highlight card's** "there is more" cue is a true alpha `EdgeFade` of the slot's content — one offscreen layer —
**not** a painted gradient. That is precisely what lets the card be the standard translucent `Tok.FillCardDefault` Mica
card: a painted gradient needs a far stop that is actually there, which forced the design doc's opaque
`Tok.FillSolidTertiary` plate. The alpha feather dissolves into whatever is behind it — Mica-over-wallpaper on the page,
the `FillSolidBase` plate in the dialog (`HighlightCard.cs:20-29, 262-272`).

**Light / dark.** Every colour above is a token except: the QR (`#FFFFFF`/`#000000`/`#1DB954`), the avatar palette, the
viewer veil `rgba(0,0,0,184)`, the chrome hover/dim `MediaScrim @ .70 / .40`, the pip off/hover
`rgba(255,255,255,82) / (…,140)`, the kind-pill shadow `#00000066`, and the viewer plate shadow `#00000099`. All eight
are deliberate, commented literals and must survive the port verbatim. `Tok.FillSolidTertiary` (the kind pill) is dark
theme ~`#2D2D2D` against the plate's `#202020` and light theme `#F9F9F9` — it is a deliberately *lighter* opaque rung so
the pill can never be mistaken for the plate behind it where the poster does not paint (issue #89 L3,
`HighlightCard.cs:130-140`).

---

## 5. Motion

Every animation below is compositor-driven (`LayoutTransition` / `Animate` / `While*` / `anim.Keyframes`) and therefore
samples the engine frame clock. The only per-second re-render is the pairing countdown's 1 Hz signal write, which is a
**clock read**, not an animation: `DateTimeOffset.UtcNow` against the challenge's `Expiry`
(`SetupPage.SignIn.cs:236-250`, `LoginView.cs:188-201`) — a `Task.Delay(1000)` off-thread posted to the UI thread.
**No `Environment.TickCount64` anywhere in this surface.**

| trigger | target | property | from -> to | duration | easing | delay / stagger | reduced motion | source |
|---|---|---|---|---|---|---|---|---|
| wizard page forward | KeepAlive page subtree | Position + Opacity | enter `Dx +8`, `α 0 -> 1`; exit `α 1 -> 0` in place | enter 250, exit 120 | enter `SmoothOut`, exit `EaseOut` | enter delay **90 ms** | tracks skipped -> cut | `PageNavMotion.cs:52-59`, `SetupDialog.cs:192` |
| wizard page back | same | same | enter `Dx -8` | 250 / 120 | same | 90 ms | cut | `PageNavMotion.cs:61-68` |
| wizard page neutral | same | Opacity only | `MotionRecipes.PageFade` | - | - | - | cut | `PageNavMotion.cs:43` |
| sign-in facet change (`Key = "signin:<facet>"`) | the body subtree | Position + Opacity | enter `Dy +6, α 0`; exit `Dy -4, α 0` | `MotionTok.StandardEnter` = 300 ms | `Easing.FluentDecelerate` | - | `KeepFade` | `SetupPage.SignIn.cs:79-84` |
| runtime phase change (`Key = "runtime:<phase>"`) | the body subtree | same | same | same | same | - | `KeepFade` | `SetupPage.LocalPlayback.cs:56-62` |
| login step advances | step mark (`Key = "login-step-mark:<state>"`) | Scale + Opacity | enter `S 0.72, α 0 -> 1`; exit `S 0.88, α 1 -> 0` | `MotionTok.ControlNormal` = 250 ms | `Easing.FluentStandard` | - | `KeepFade` | `LoginView.cs:76-82` |
| login step row mounts | the row | Position + Opacity | `Dx -6, α 0 -> 1` | `ControlNormal` 250 | `FluentStandard` | **40 ms** per row (`WaveeMotion.StaggerMs`), 4 rows | `Stagger = 0` when `Motion.ReducedMotion` | `LoginView.cs:88`, `SetupPage.SignIn.cs:173` |
| login progress value changes | `ProgressBar.PartFill` | Width | old -> new | `ControlNormal` 250 | `FluentStandard` | - | `KeepFade` | `LoginView.cs:115-125` |
| waiting dots (**DEAD** — `WaitingDots` is never mounted anywhere in 0.2.9; kept) | 3 x 6-DIP dot, gap 5, `Tok.AccentDefault` | TranslateY | `0 -> -5 -> 0` | 1100 ms loop, `Cadence.Display` | `EaseInOut` | peaks at u = .16 / .28 / .40 | `return` before wiring — dots stay static | `LoginView.cs:144-174` |
| wizard plate opens / closes | the plate | Scale + Opacity | `1.05 -> 1.0` + fade (the WinUI ContentDialog motion) | engine-owned | engine-owned | - | engine-owned | `PopupChrome.Modal`, `SetupDialog.cs:46` |
| shell cover appears / clears | `SetupCoverScrim` | Opacity | `0 <-> 1` | `WaveeMotion.Standard` = 250 ms | `Easing.Linear` | - | **0 ms** (`Motion.ReducedMotion` -> `ms = 0`) | `WaveeShell.cs:2277-2284` |
| Lottie hero mounts | every tracked node | the compiled tracks | `From 0 -> To 0.5`, once, then HOLD | the scene's own (half its authored duration) | the scene's own beziers (+ `Easing.Hold` for step keys) | - | `ReducedMotionPolicy.SnapEnd` -> the static pose at `To` | `WaveeLottie.cs:47` |
| highlight card hover (**regular only**) | frame | Fill | `FillCardDefault -> FillCardSecondary` | `WaveeMotion.Faster` = 83 ms | brush cross-fade | - | brush only, always runs | `HighlightCard.cs:210, 216`, `Interaction.cs:146-153` |
| highlight card press | regular: the frame (via `Interaction.Card.PressScale`); store: the hit-region sibling (`WhilePressed`) | Scale | `1 -> 0.985` | `MotionTok.StandardSpring` | spring | - | engine-gated | `HighlightCard.cs:216, 228-230`, `Interaction.cs:151-152` |
| any `SettingsCard` (browser / scan / runtime detail) hover, press | the card root | Fill + Border | the control's own WinUI ramp | `Style.BrushTransitionMs` = **83** | brush cross-fade | - | brush only | `SettingsCard.cs:70, 150-158` |
| card body overflow flips | body slot | `EdgeFade` presence | none <-> `EdgeFade(Bottom, 24)` | - | - | - | n/a | `HighlightCard.cs:277, 294-298` |
| "Read more" hover / focus | label + chevron | Color | `TextSecondary -> AccentTextPrimary` | 83 ms | brush cross-fade | - | brush only | `HighlightCard.cs:328-334` |
| viewer slide FORWARD | `"hv:<id>:img"` and `"hv:<id>:txt"` | Position + Opacity | enter `Dx +24, α 0 -> 1` | `Expressive.Fast` = **250 ms** | `Easing.SmoothOut` | - | Enter/Exit tracks skipped -> instant swap | `HighlightViewerMotion.cs:22-27` |
| viewer slide BACK | same | same | enter `Dx -24, α 0 -> 1` | 250 | `SmoothOut` | - | instant | `:29-30` |
| viewer slide EXIT (either direction) | same | Opacity only | `Dx 0`, `α 1 -> 0` | **120 ms** | `Easing.FluentAccelerate` | - | instant | `:20, 27` |
| viewer FIRST slide | same | - | **no entrance** (`Enter = default`); the Modal chrome already scaled the plate in — but the same exit | - | - | - | - | `:34` |
| viewer poster loads | `ImageEl` | Opacity | reveal | **140 ms** | `ImageTransition.Fade` | - | still runs — this is orientation, not motion | `HighlightViewer.cs:240` |
| viewer band tint changes (store <-> regular) | the STABLE band | Fill | `FillSubtleSecondary <-> AccentSubtle` | `WaveeMotion.Standard` = 250 ms | brush cross-fade | - | brush only | `:215` |
| pip selection moves | the two affected pips | Width (`SizeMode.Reflow`, `SizeAxes.Width`) | `6 <-> 14` | **120 ms** | `Easing.SmoothOut` | - | tween skipped -> snap | `:338, 356-359` |
| pip selection moves | the two affected pips | Fill | `rgba(255,255,255,82) <-> Tok.TextPrimary` | `BrushTransitionMs = 120` | brush cross-fade | - | brush only | `:362-364` |
| chevron reaches / leaves an end | the circle (SAME node) | Opacity + Fill | `1 <-> 0.3`, `MediaScrim <-> MediaScrim @ .40` | `MotionTok.ControlFaster` = **83 ms** | `Easing.FluentStandard` | - | `KeepFade` | `:299-301` |
| chevron / close / watch-pill hover, press | the circle / pill | Scale | hover `1.04`, press `0.96` (`WaveeMotion.ScaleStandard`) | the `While*` default tier | spring | - | collapses to 1 (and to 1 for a dead end button, `HoverIf/PressIf`) | `:304-305, 322`, `WaveeMotion.cs:43` |
| viewer opens / closes | the plate | Scale + Opacity | the ContentDialog scale/fade | engine-owned | engine-owned | - | engine-owned | `PopupChrome.Modal` |
| after-update dialog opens | the plate | same | same | engine-owned | - | - | - | `AfterUpdateDialog.cs:48` |
| report dialog opens | the `ContentDialog` card | same | same | engine-owned | - | - | - | `ReportDialog.cs:58` |
| pairing countdown | the scan card's description | text (`mm:ss`) | 1 Hz signal write | - | - | - | unaffected (not an animation) | `SetupPage.SignIn.cs:236-250` |
| teaching tip (if this surface ever raises one) | `TeachingTip` | Scale | expand `min(.01, 20/W) -> 1` / contract `1 -> 20/W` | 300 / 200 ms | `cubic-bezier(.1,.9,.2,1)` / `(.7,0,1,.5)` | double-post so it rises over a PAINTED frame | engine-owned | `WaveeTips.cs:43-45` |

**Reveal choreography.** There is none on the What's-new page: the whole view lands in one signal write, so the hero,
strip and sections appear together in one frame, and the only per-element motion is the poster fade
(`ImageEl.RevealTransition` is **not** set on the card — only in the viewer). Do not add a staggered section reveal
"for polish": the page is a document, and 0.2.9 deliberately does not cascade it.

**The two KeepAlive knobs on the wizard's page host are load-bearing:** `MaxEntries: 3` (all three pages stay live, so
returning to a page keeps its scroll and its Lottie hold state) and `SuppressLayoutTransitionsOnActivation: true`
(re-activating a parked page must not replay every child's layout transition). `TransitionFor` **Peeks** `Dir`, never
subscribes — a motion-only write must not re-run the KeepAlive boundary (`SetupDialog.cs:186-193`,
`SetupSession.cs:11-16, 156-165`).

**The `--qr-dump` probe has no motion at all** (§1.5) — one shot, headless, no window and no frame loop. It is stated
here only so its absence from the table above does not read as an omission.

---

## 6. Interaction

### 6.1 Setup wizard

**Keyboard (plate root, `OnKeyDown`)** — `SetupDialog.cs:123-127`:

| key | effect | condition |
|---|---|---|
| `Enter` | `session.Primary()` | only when `row.PrimaryEnabled` (never during Busy / Catalog / Downloading / Verifying) |
| `Backspace` | `session.Back()` | only when `row.ShowBack` (LocalPlayback only) |
| `Escape` | overlay dismiss, **vetoed** by `ClosingAction` | passes only for `TermsRearm` and only when `!IsBusy` (`SetupGating.cs:93-101`) |
| `Tab` | trapped inside the plate | `FocusTrap: true` |

A programmatic close (`session.RequestClose`) **always** goes through — "Open Wavee", "Decline" on a rearm run and the
diagnostics hand-off are the session's own closes, and vetoing them left a finished wizard on screen with a dead button
(`SetupDialog.cs:56-60`).

**Entry point → start page** (`WaveeApp.cs:366-369`, `SetupChrome.cs:70-77`): **FirstRun** starts on Terms;
**Reauth** starts on `SetupPage.SignIn` (re-walking terms someone already accepted is nonsense); **TermsRearm** is
built post-auth by `SetupChrome` and starts on Terms. `SetupPage` is never persisted, so the numbering is free.

**Command table** — `SetupCommands.Resolve(ctx)` (`App/SetupCommands.cs:66-109`). `null` = no such button;
a disabled button is a non-null key with its `*Enabled` flag false, so a caller can always tell "not offered" from
"offered but not right now". `PrimaryKind` is `Accent` on **every** row today — `SetupButtonKind.Standard` exists in the
vocabulary and `SetupWizardFooter.PrimaryButton` honours it (`SetupDialog.cs:263-269`), but nothing selects it, so a
port that drops the switch is a silent API narrowing, not a simplification. `BlocksDismiss` is set on
Catalog/Downloading/Verifying but is **read by nothing** any more (`Resolve` overwrites `ShowBack` from
`SetupGating.ShowsBack(page)` alone) — port it, do not wire new behaviour onto it without saying so.

| page / facet | primary (loc key) | enabled | secondary (loc key) | enabled | Back | what the primary does |
|---|---|---|---|---|---|---|
| Terms | `setup.accept` | yes | `setup.decline` | yes | no | write `TermsAcceptedVersion = 1`, then advance (or, `TermsRearm`: `MarkCompleted` + close) |
| SignIn / Idle | `auth.logIn` | yes | `auth.close` | yes | no | `StartBrowser` |
| SignIn / Busy | `auth.signingIn` | **no** | `auth.cancel` | yes | no | - (secondary = `CancelSignIn`, **never** quit: the label promises to stop the sign-in, not Wavee) |
| SignIn / Done | `setup.signIn.yesContinue` | yes | `setup.signIn.notMe` | yes | no | `FinishSignIn(SkipsLocalPlayback)` |
| SignIn / Failed | `auth.tryAgain` | yes | `auth.close` | yes | no | `RestartCode` |
| SignIn / Expired | `auth.getNewCode` | yes | `auth.close` | yes | no | `RestartCode` |
| SignIn / Premium | `auth.upgrade` | yes | `auth.useAnotherAccount` | yes | no | open `spotify.com/premium` (secondary re-mints the code) |
| LocalPlayback / Offer | `playback.runtime.downloadSetup` | yes | `playback.runtime.notNow` | yes | **yes** | `StartDownload` |
| LocalPlayback / Catalog | `playback.runtime.checking` | **no** | `auth.cancel` | yes | yes | - |
| LocalPlayback / Downloading | `playback.runtime.downloading` | **no** | `auth.cancel` | yes | yes | - |
| LocalPlayback / Verifying | `playback.runtime.verifying` | **no** | **null** | - | yes | - (no Cancel: a signature check is fast enough) |
| LocalPlayback / Versions | `playback.runtime.install` | yes | `playback.runtime.back` | yes | yes | `InstallSelected` |
| LocalPlayback / Untrusted | `playback.runtime.loadAnyway` | yes | `playback.runtime.back` | yes | yes | `ConfirmUntrusted` |
| LocalPlayback / Ready | `setup.openWavee` | yes | **null** | - | yes | `MarkCompleted` + close (the primary spans the whole action lane) |
| LocalPlayback / Failed | `playback.runtime.tryAgain` | yes | `playback.runtime.notNow` | yes | yes | `Retry` |

**What each SECONDARY actually does** (`SetupSession.cs:252-323`) — the table above gives its label; this gives its
verb, and three of them are not what the label suggests:

| page / facet | secondary label | what it does |
|---|---|---|
| Terms (FirstRun / Reauth) | `setup.decline` | **`QuitApp`** — the marker stays armed, so the next launch resumes here (`:269-272`, the `else` arm) |
| Terms (TermsRearm) | `setup.decline` | `RequestClose` — a live shell is behind it (`:271`) |
| SignIn / **Idle, Failed, Expired** | `auth.close` | **`QuitApp`** — same reason as Decline: pre-auth there is nothing behind the plate (`:286-295`, `default:` arm) |
| SignIn / Busy | `auth.cancel` | `CancelSignIn` — back to Idle **with the cards**; never quits (`:290`) |
| SignIn / Premium | `auth.useAnotherAccount` | `RestartCode` — re-mints the pairing code (`:291`) |
| SignIn / Done | `setup.signIn.notMe` | `SwitchAccount` — signs this PC out; the page drops to Idle with a fresh code (`:292`) |
| LocalPlayback / Offer, Failed | `playback.runtime.notNow` | `DismissSetting` + `DeclineRuntime` + `MarkCompleted` + `RequestClose` — finishes the wizard outright (`:306-312`) |
| LocalPlayback / Versions | `playback.runtime.back` | `model.Back()` — a MODEL step, not a page walk (`:313`) |
| LocalPlayback / Untrusted | `playback.runtime.back` | `model.CancelUntrusted()` — also a model step (`:314`) |
| LocalPlayback / Catalog, Downloading | `auth.cancel` | `model.Cancel()` (`:315-316`) |
| LocalPlayback / Verifying, Ready | *(null)* | no button to invoke (`:317`) |

The plate's own **Back** affordance (the 30×30 icon button and `Backspace`) is always the bare page walk
`Advance(PrevPage(...))` (`SetupSession.cs:323`) — it is a different verb from the footer's "Back" label above.

**Click targets.** The browser card is a click-enabled `SettingsCard` (whole card, `IsClickEnabled` + `OnClick`); the
scan card is **not** clickable (the QR is the affordance). "Sign up", "Privacy policy", "Not me", "View", "Open folder"
are `HyperlinkButton`s. The three Advanced rows are click-enabled `SettingsExpander.Item`s. No context menus, no
drag-and-drop, no double-click, no right-click anywhere in the wizard.

**Focus visuals** are the engine's standard ring. Initial focus is the plate root (`FocusTrap`'s first stop).

**Accessibility.** The sign-in page drives a UIA live region through `InputHooks.Current.Default.Announce`
(`SetupPage.SignIn.cs:43-62`), keyed on the raw `LoginPhase` (not the folded facet), because Failed and ChallengeExpired
carry different copy and `AwaitingApproval` must re-announce when a fresh code replaces an expired one:

- `AwaitingApproval` -> `auth.scanToLogIn + ". " + auth.orGoTo + " spotify.com/pair, " + auth.enterCodeColon + " " +`
  **the code spelled out one character at a time, space-separated** (`"W Z Y 5 Q 6 T X"`, hyphens stripped) — `"WZY5-Q6TX"`
  is unreadable by any synthesizer. Non-assertive.
- `Failed` -> `snap.Error` or `auth.networkError`. **Assertive.**
- `ChallengeExpired` -> `auth.codeExpired`. **Assertive.**

A null announcer (non-Windows backends) is a silent no-op.

### 6.2 What's-new page

| gesture | target | effect |
|---|---|---|
| click | hero "Open on GitHub" | `LoginView.OpenUrl(doc.Links.Release ?? ReleaseTagUrl(version))` |
| click | hero "Copy link" | clipboard + `Toast.Show(whatsNew.linkCopied, Severity = Success)` (`ReleaseNotesPage.cs:83-87`) |
| click | highlight card (the whole hit region) | `HighlightViewer.Open(overlay, view.MergedHighlights, i, go, closeHost: null)` |
| click | store card's accent footer button | `HighlightCard.OpenStoreListing()` -> `StoreLinks.ProductPage(AppVersion.Info.StoreId ?? "9NJPVWTQPT9H")` |
| click | a link/`@mention`/`#123`/`!430` **inside a changelog sentence** | `openUrl` -> the browser. Inside a **card** body the handler is a deliberate no-op (`static _ => { }`) because the card is itself one button; links are live in the **viewer**, where the body is not a button (`HighlightCard.cs:302`, `HighlightViewer.cs:457, 476`) |
| click | issue / PR chip | opens `github.com/<repo>/issues/<n>` (or `/pull/<n>` for a PR); tooltip = the live issue title, else the baked-in snapshot title, and **no tooltip at all** when neither carries one (`IssueChip.cs:35, 68`) |
| hover | contributor disc | tooltip = `login`, or `whatsNew.firstContribution(login)` when `FirstTime` |
| click | "Show all {n}" | `_all.Value = true` — expands in place and **never collapses again while the page is open** (fold = 8 rows, `ChangelogSection.cs:30, 65-67`) |
| click | "Only the latest" | `_onlyLatest.Value = true` — drops the stack to `releases[..1]` (`ReleaseNotesPage.cs:189-190`) |
| click | a rail row | `go("whatsnew", version)` — a NEW `ContentHost` keep-alive slot, keyed by the arg |
| click | empty-state "Open on GitHub" | `RepoUrl + "/releases"` |
| text selection | changelog paragraphs | `RichTextBlock.Paragraph` default (selectable); the card's paragraph opts **out** (`isTextSelectionEnabled: false`) |

No context menus, no drag-and-drop, no keyboard shortcuts of the page's own — arrows and Home/End belong to the scroller.
Scroll position is preserved per release by `ScrollKey = "whatsnew:" + view.SelectedVersion`, and the rail has its own
`ScrollKey = "whatsnew:rail"`.

### 6.3 Highlight viewer

**Keyboard (root `OnKeyDown`, `HighlightViewer.cs:145-159`).** Every recognised key is marked `Handled` **even when it
is a no-op**, so a single-highlight viewer never leaks arrows to the page under the veil. A key that arrives
**already handled** came from a focused pip and is left alone.

| key | `HighlightNavKey` | behaviour |
|---|---|---|
| `Left` | `Previous` | step, **clamped — no wrap** (the WinUI FlipView rule) |
| `Right` | `Next` | step, clamped |
| `Home` | `First` | jump to 0 |
| `End` | `Last` | jump to `count - 1` |
| `Escape` | - | the overlay's (Modal chrome) |

`HighlightViewerLayout.Step` returns `HighlightSlideDirection.None` when the index would not move (already at an end,
or `count <= 1`), and `Go` returns immediately on `None` — so a Right press on the last slide changes nothing, animates
nothing and re-renders nothing (`HighlightViewerLayout.cs:54-67`, `HighlightViewer.cs:134-139`).

**Pointer.** The veil is the ONE light-dismiss surface (`OnClick = Close`); the plate paints over it in z-order, so a
click on the plate never reaches it — no `HitTestPassThrough` gymnastics. Clicking the image does nothing. The chevrons
step; a pip jumps (`StepTo`, direction = the sign of the move); the close circle closes; the watch pill opens the
release page in the browser **and leaves the viewer open**. "Try it" navigates, then closes the host dialog (if any),
then closes the viewer — **in that order**, so the shell has navigated before either plate starts its exit and focus
lands somewhere (`HighlightViewer.cs:163-170`).

**Tooltips** (the only automation names these glyph-only controls carry — `AutomationRole` is the sole a11y property an
`Element` has today): close `whatsNew.viewer.close`, prev `whatsNew.viewer.previous`, next `whatsNew.viewer.next`,
pager cluster `whatsNew.viewer.position` = `"{index} of {count}"` (1-based). A **dimmed** end chevron is not
hit-testable, so it simply never raises one. The watch pill's own visible label IS its name.

**Tab order** root -> close -> prev -> [watch pill] -> next -> pips -> CTA. A dimmed chevron leaves the tab order
(`TabStop = false`) but keeps its slot in the layout, so the order is otherwise identical on every slide. The sizers'
buttons are **disabled** (`isEnabled: live`), so the focus trap never collects them. The watch pill sits BETWEEN the
two chevrons in the DOM (`CentreRow` is `[Prev, WatchPill, Next]`, `HighlightViewer.cs:268-270`) and — unlike the
three chrome circles — never sets `Focusable = true` (`:316-329`): it carries `Role = Hyperlink`, `Cursor = Hand` and
an `OnClick` only. **Verify on the candidate build whether it is keyboard-reachable**; if 0.2.9 leaves it out of the
trap, that is a bug worth fixing in 0.3, not parity worth preserving.

### 6.4 Report dialog

| control | behaviour |
|---|---|
| Segmented (Bug / Feature / Question / Idea) | writes `kindIndex`; the form **remounts** (`Key = "report-card:" + (int)kind`) because every signal in it is seeded once from an open-time constant. Never shown in crash mode. |
| "Copy report" | builds the bundle at click time, clipboard, `Toast(report.copied, Success)`. Disabled until the compose finishes. |
| "Save as…" | `FilePicker.SaveFile(FluentApp.WindowHandle, report.saveAs, ReportBundle.FileName(now), ("Text files","*.txt"), ("All files","*.*"))`; writes, `Toast(report.saved(path), Success)`; an exception becomes `Toast(ex.Message, Error)`. Disabled until compose. |
| primary (`Open GitHub` / `Report on GitHub`) | `session.Submit()`: no composed report -> `Toast(report.preparing, Informational)` + `args.Cancel = true`; empty title on a non-crash kind -> `Toast(report.titleRequired, Warning)` + Cancel; else clipboard + save a `wavee-report-<stamp>.txt` into `CrashReport.DefaultDirectory` + `ShellOpen.OpenUrl(IssueFormUrl.Build(...))` + `Toast(report.copiedPaste(channel.PasteBox), Success, DurationMs 8000)` |
| close (`Cancel` / `Not now`) | dismisses; `Session.Submit` is nulled by the effect cleanup |
| "Include diagnostics and the last {N} log lines" | default **true** for Crash and Bug, false for Feature/Question/Idea; N = 300 (crash) / 200 (manual) |
| "Don't ask again after a crash" | writes `WaveeSettings.CrashPromptOptOut` immediately (crash mode only); it is **seeded from the current setting**, so a reporter who already opted out sees it ticked (`ReportDialog.cs:176`) |
| `When does it happen` | 7 options, default index 0 = "On launch" (`ReportChannels.When`). **English, never localized** (§0.12) |
| `Does it reproduce` | 3 options, default index 0 = "Every time" (`ReportChannels.Reproduces`). **English, never localized** |
| `Area` | 24 options, RAW SLUGS (`playback`, `video`, … `engine`) plus `"Not sure"`; default = the **last** — `area = Areas.Length - 1` (`ReportDialog.cs:174`). **English, never localized**; `AreaLine`/`BuildLabels` compare against the literal `"Not sure"` to decide whether to emit an `Area:` line and an `area: <slug>` GitHub label (`:445-464`) |
| `Enter` | the `ContentDialog` default button = Primary |

The **preview recomputes on every keystroke** — `PreviewText` is called with `title.Value`, `f1.Value`… (subscribing
reads), so the card re-renders per character; `Submit` reads the same signals with `Peek()` so it never goes stale
(`ReportDialog.cs:202-217, 240`). Only the small free-text answers are re-redacted per keystroke; the diagnostics block,
the crash head and the log excerpt were redacted once, off-thread (`ReportComposer.cs:29-32`).

**Entry points** (all through `ReportRequests.Open(kind, prefill)`, a monotonic `Seq` so two opens in one flush cannot
collapse): Settings › About "Report a problem…" (Bug) / "Suggest a feature…" (Feature)
(`SettingsPage.About.cs:148-149`); the Logs panel's "Report this session…" (Bug + `PastSession`)
(`LogsPanel.cs:224`); the Diagnostics crash-reports card's per-row "Report…" (Crash + that file path)
(`CrashReportsCard.cs:50-52`); the deep link `wavee://open?route=report&arg=bug|feature|crash|question|idea`
(`WaveeShell.cs:1808`); and the automatic post-crash prompt (`CrashPromptPolicy.ThisLaunch`).

**The post-crash prompt has TWO shapes, and only one is this dialog** (`ReportChrome.cs:46-67`).
`CrashPromptPolicy.Mode` is `Dialog` normally and `Toast` once the user has opted out. The Toast arm is owned by this
surface, not by `19-shell-overlays.md`'s generic toast table, and its options are load-bearing:
`Severity = Warning`, **`DurationMs = 0` (sticky — it waits for the user)**, `ActionLabel = report.reportOnGithub`
whose `OnAction` re-enters through `ReportRequests.Open(Crash, prefill: that report path)`, and
`DedupeKey = "crash.pendingReport"` so a second evaluation cannot stack two. Either arm consumes
`CrashPromptPolicy.ThisLaunch` (`= default`) the moment it fires — one shot per launch — but **only after** the wizard
deferral has passed, so a deferred prompt is still armed when `MarkerEpoch` bumps.

### 6.5 After-update dialog

| control | behaviour |
|---|---|
| the plate | `DismissBehavior.Modal` + `PopupChrome.Modal`, `ScrimVisual = true`, `FocusTrap` — no light dismiss; there is no `ClosingAction` veto, so `Escape` closes it (`AfterUpdateDialog.cs:44-49`) |
| a card | `HighlightCard.Compact(item, …)` → `HighlightViewer.Open(overlay, highlights, idx, nav, close)` — the viewer opens **over** this plate and is handed this plate's `close` as its `closeHost` (`:80-82`) |
| CheckBox "Don't show this after updates" | writes `WaveeSettings.ReleaseNotesAutoShow = !v` **immediately**, not on close (`:130-131`) |
| Button.Standard "Full release notes" | `close()` **then** `nav("whatsnew", null)` — the OPPOSITE order from the viewer's "Try it" (§6.3), and deliberate: the host plate here is the thing being replaced, not a plate the navigation has to survive (`:133`) |
| Button.Accent "Got it" | `close` and nothing else (`:134`) |
| re-showing | impossible: `ReleaseNotesPendingFrom` was cleared at `Open()` before the first render (§0.9). Neither button writes it back |

No keyboard shortcuts of its own, no context menus. Tab order is hero → cards (each card is one button) → checkbox →
"Full release notes" → "Got it".

### 6.6 The `--qr-dump` probe (CLI, no window)

`Wavee.exe --qr-dump "<text>" <out.png>` — both positional args are optional, and an arg that starts with `--` is taken
as the **next flag** rather than as the text or the path (`Program.cs:235-236`), so `--qr-dump --spotify-login` dumps the
default `https://spotify.com/pair` to `qr.png` and exits before the login arm is reached (`Program.cs:22-27, 238`).
A relative path resolves against the process CWD; the summary line prints the FULL path so the file can be found
(`QrDump.cs:40`).

The **exit code is the whole contract**: `0` = encoded, the ASCII block written to stdout and the PNG written to disk;
`1` = the encoder threw (text too long for v1-10) and **nothing was written** (`QrDump.cs:19, 41`). There is no other
channel — no prompt, no window, nothing to click. The ASCII block goes straight to `Console.Out` (`:30`); the summary
is a *log* line and reaches the terminal only because `--qr-dump` is a `ProbeFlags` entry, which is what arms
`WaveeLog.SetEcho` in Release as well as Debug (`CliRun.cs:12-15`, `Program.cs:126`).

---

## 7. Data & readiness in 0.3 terms

| visual element | 0.2.9 data source | 0.3 read | readiness predicate |
|---|---|---|---|
| wizard entry point / start page | `SetupGating.IsPending/IsCompleted(settings)`, `NeedsTermsRearm` | `Platform.Settings` keys `setup.pending`, `setup.completed`, `setup.terms.acceptedVersion` | synchronous registry read; never async, never a skeleton |
| which page is showing | `SetupSession.Page` `Signal<SetupPage>` | `Setup.Session.Page` (unchanged) | always known |
| footer row | `SetupCommands.Resolve(session.BuildCtx())` | `Setup.Commands.Resolve` over `(Page, SignInFacet, RuntimeFacet)` | always resolvable — both sub-states fold to `Idle` / `Offer` before their pages have ever rendered (`SetupSession.cs:144-154`) |
| sign-in facet | `bridge.Login.Value.Phase/.Step` + `bridge.Auth.Value` -> `SetupCommands.Project` | **DATA GAP D1** — `Spotify.Session.cs` has no `LoginSnapshot` signal in the plan | the default `LoggedOut` -> `Idle` is a real state, not a skeleton |
| pairing QR + code + countdown | `snap.Challenge.{VerificationUriComplete ?? VerificationUri, UserCode, Expiry}` — **two** URI fields, the complete one preferred (`SetupPage.SignIn.cs:135`) | **DATA GAP D1** | `Challenge is null` -> the "Getting your code…" placeholder card (the ONE skeleton on this page) |
| "Is this you?" name / avatar / tier | `bridge.User.Value` (live) with `LoginSnapshot.User` fallback, folded by `SetupSignInPresentation.DisplayNameFor` | **DATA GAP D2** — `User` has library edges but no profile columns | show the account row as soon as `auth == Authenticated`; the *name* may arrive one tick later (that is exactly what `DisplayNameFor` is for: prefer whichever source carries a name that differs from the id) |
| local-playback phase, bytes, label, version/arch/signature/path | `PlaybackRuntimeSetupModel.{PhaseSig, Received, Total, DownloadLabel, ActiveEntry, Status, UpToDate, Error}` | **DATA GAP D3** — no home in the plan | `model is null` -> the "Local playback isn't active" body (`SetupPage.LocalPlayback.cs:39-45`); otherwise the phase IS the readiness |
| Lottie hero | `assets/lottie/{eula,connect,patch}.json` beside the exe | **DATA GAP D7** (assets) | `Warm()` is a nicety; a cold `For()` costs one frame, never a placeholder |
| whole What's-new page | `ReleaseNotesView(Releases, MergedHighlights, Index, SelectedVersion, LastSeen)` — assembled off-thread, ONE write | `ReleaseNotes.Host.Store` -> a `Signal<ReleaseNotes.View?>` owned by the page | **`view is null && !loaded` -> `ProgressRing(28)`; `view is null && loaded` -> the empty state; otherwise the COMPLETE page.** A partially built view is never observable (`ReleaseNotesPage.cs:23-29`) |
| release document (hero, notices, sections, highlights) | `ReleaseNotesStore.GetAsync(semver)`: embedded -> cache -> release asset | `ReleaseNotes.Host.GetAsync` (unchanged ladder) | part of the one view write |
| the release rail | `ReleaseNotesStore.RefreshIndexAsync` / `IndexSnapshot()` | `ReleaseNotes.Host.Index` (a `Volatile.Read` field, **not** a signal — it is written on whatever thread the HTTP continuation finished on) | the index is a **nicety**: on failure the rail renders as a zero-width, hit-invisible box and the document still loads (`:235-237`, `ReleaseRail.cs:31`) |
| issue chip state word + dot | baked-in `ReleaseIssue.State/StateReason`, overlaid by `RefreshIssueStatesAsync` (budgeted, newest document only) | `ReleaseNotes.Host.RefreshIssueStates` -> a SECOND whole-view publish | the chip **always** shows a state — the live one when the fetch landed, the tool's snapshot otherwise. Never blank, never a spinner (`IssueChip.cs:11-16, 25-26`) |
| highlight posters | `HighlightCard.ResolvePoster` (`store.MediaPath` + `File.Exists`) run in the LOADER | `ReleaseNotes.Host.ResolvePoster`, still off the UI thread | resolved before the view is published; `null` -> a tinted band, never a blank third |
| "since you last looked" banner + unread dots | `ReleaseNotesLastSeen` read BEFORE the load, carried on the view | `Platform.Settings` key `app.whatsnew.lastSeenVersion` | the read must precede the write (§0.11) |
| after-update plate | `ReleaseNotesPendingFrom` + `ReleaseNotesAutoShow` + `AppVersion.Info` + `SetupGating.IsPending` / `SetupSession.Current` + `AfterUpdateDialog.CrashNoticeThisLaunch` | `Platform.Settings` keys `app.whatsnew.pendingFrom`, `app.whatsnew.autoShow` | **FOUR gates, all of which leave the key armed** (`AfterUpdateDialog.cs:198-201`): (1) no pending version → nothing to say; (2) `autoShow` off → the user said never; (3) the wizard is pending **or** a session is live → re-evaluated on `MarkerEpoch`, so it still appears THIS launch; (4) a crash notice opened this launch → defers to the NEXT launch. Past all four, the pending key is cleared at `Open()` **before the plate renders** — one shot whatever happens next |
| after-update tagline + cards | `store.GetAsync(me.Core)` -> `_loaded` | same | **today: the hero renders with an empty tagline and no cards until the load lands** (W23) — and that same frame is the PERMANENT state when the load fails or the store is null (`AfterUpdateDialog.cs:149, 153, 167-168`): no error, no retry, no distinction from "still loading". See §9.4. |
| report identity block | `ReportIdentity.From(AppVersion.Info, PackageIdentity.IsPackaged, OSArchitecture, OSVersion.Build)` | `Platform.cs` | synchronous |
| report diagnostics / logs / crash head | `SettingsPage.DiagInfoText(svc)`, `WaveeLog.Instance`, `WaveeLogSessions`, the crash-report file | `Diagnostics.*` | `composed is null` -> the preview reads `report.preparing` and the primary vetoes with a toast |
| crash InfoBar summary | first line after the `"\nException\n---------\n"` marker in the report head | `Diagnostics.*` | three honest states (composing / summary / no report) |

**Pages demand their whole model on mount.** The What's-new page asks for the whole skipped range in one loader
(`ReleaseNotesRange.Between(lastSeen, me.Core, index, channel)`), then the merged highlights, then one budgeted issue
sweep over the newest document only — there is no visible-window fetching anywhere and nothing re-fetches on scroll.
**Derived facts live on the model:** `ReleaseEntry.IsYou`/`.IsUnread`, `HighlightItem.Poster`, `ReleaseNotesView.LastSeen`
and `SetupCommandRow` are all computed once, off the UI thread or in a pure function, and handed to the UI —
no element probes a store, a file system or a service.

**One structural requirement with no signal behind it.** `--qr-dump` (§1.5) has no model, no readiness and no signals,
but it pins the encoder's dependencies: **`Qr` must stay engine-free.** It is source-included into the lean
`Wavee.Tests` project (`Wavee.Tests.csproj:103`, the property `Qr.cs:9` asserts), and the probe reaches it with nothing
but the BCL and `WaveeLogger`. If 0.3 moves QR *encoding* behind an engine type, a `Signal` or a component, both the
probe and `QrTests` die with it — which is why §1.6's first row keeps `Qr.Encode` and `Qr.Plate` pure statics in
`Screens/Setup.cs` CORE, and only `Setup.QrGrid` is a component.

### DATA GAPS

| # | what this surface shows | 0.2.9 source | the plan's data model holds | proposed column / edge / home |
|---|---|---|---|---|
| **D1** | login phase, step, error text, and the pairing challenge (uri, user code, expiry) | `PlaybackBridge.Login : Signal<LoginSnapshot>` + `.Auth : Signal<AuthStatus>`, fed by `SpotifyLiveLogin` / `Switchable` | nothing — §4.10/§4.6 cover the AP socket and decode, never the login *UI state machine* | `Spotify/Spotify.Session.cs` (SHELL): `public static readonly Signal<LoginSnapshot> Login` and `Signal<AuthStatus> Auth`, with `readonly record struct LoginSnapshot(LoginPhase Phase, LoginStep Step, PairingChallenge? Challenge, string? Error, UserRef User)` and `readonly record struct PairingChallenge(StringId Uri, StringId UriComplete, StringId UserCode, DateTimeOffset Expiry)` — **both** URI fields, since the page prefers `UriComplete` and falls back — declared in `Spotify.cs` (CORE, so `Setup.Commands.Project` stays pure and testable) |
| **D2** | the account row: display name, avatar URL, Premium vs Free | `WaveeUser.{Id, DisplayName, AvatarUrl, IsPremium}` off `bridge.User` | `User.cs` is *only* library edges (`Liked`, `SavedAlbums`, `Rootlist`, …) — no profile columns at all | `UserTable`: `Column<StringId> DisplayName, Avatar; Column<uint> Flags` with `UserFlags.Premium`; `[Flags] UserFields { Profile = 1<<0, … }` so `User.Me.Knows(UserFields.Profile)` is the readiness predicate. Authority `Full` from the profile fetch, `Thin` from the login payload. Also needed by `20-player-bar.md` / `19-shell-overlays.md` (the profile menu). |
| **D3** | every local-playback fact: phase, received/total bytes, download label, catalog entries, `SpotifyVersion`, `Arch`, `DllSha256`, `SignatureInfo`, `RuntimePath`, `UpToDate`, `Error` | `PlaybackRuntimeSetupModel` (`Features/Shell/PlaybackRuntimeSetupCard.cs`) + `PlaybackBridge.RuntimeStatus` | the MODEL: nothing, §2 lists no provisioning file. **The card's UI BODY is A18, CONFIRMED (plan §9.6 Q9, 2026-09-12): `+Screens/Setup.UI.Runtime.cs` (1,100), written by owner I in Wave 4 for the Wave-4 shell-banner gate, mounted a second time — not re-implemented — by this chapter's LocalPlayback page (`SetupBody.VersionPicker`, line 222 above) in Wave 6.** | `Platform/Platform.cs` (CORE: `RuntimePhase`, `RuntimeStatus`, `ProvisioningOutcome`, the pure `ProgressFraction`/`ShortHash`) + `Platform/Platform.Host.cs` (SHELL: catalog fetch, download, verify, install, the `Signal<RuntimePhase>` the page and footer both read). It is NOT entity data and must not be forced into a table. |
| **D4** | the release-notes document, index and issue states | `whatsnew.json` / `whatsnew-index.json` (embedded -> cache -> release asset) and `api.github.com` | nothing — it is file/HTTP data, correctly outside the entity graph | `Screens/ReleaseNotes.cs` (CORE: the document/index records + parser + validation, also `<Compile Include>`d by `Wavee.ReleaseTool` per plan §3.3) + `Screens/ReleaseNotes.Host.cs` (SHELL: the three-rung ladder, the cache root, `IssueStateBudget`). Keep `Index` a `Volatile` field, never a `Signal` — it is written from an HTTP continuation with no poster behind it. |
| **D5** | highlight posters on disk | `store.MediaPath(doc, src)` under `%LOCALAPPDATA%\Wavee\cache\whatsnew\<semver>\` + `Assets\whatsnew\` beside the exe | not modelled | `ReleaseNotes.Host.MediaPath` (unchanged). Resolution stays in the LOADER: a `File.Exists` per card per render turns every hover, scroll and re-theme into synchronous UI-thread I/O for an answer that cannot change while the page is open. |
| **D6** | the crash-report corpus, the log ring, past sessions, the diagnostics text | `CrashReportFiles`, `WaveeLog`, `WaveeLogSessions`, `SettingsPage.DiagInfoText` | `Diagnostics.*` exists in §2 but its contract is unstated | `Diagnostics/Diagnostics.cs` (CORE: `CrashPromptPolicy`, `CrashReportFiles`, the redaction rule table) + `Diagnostics.Host.cs` (SHELL: file I/O). Cross-ref `27-settings-and-diagnostics.md`. |
| **D7** | **assets**: `assets/lottie/{eula,connect,patch}.json` (3 x ~47 KB) and `Assets/whatsnew/{whatsnew.json, media/*}` | `Wavee.csproj` `Content` items, read via `AppContext.BaseDirectory` | plan §2's `assets/` line names only "fonts, loc/*.json, deck media" | extend the `assets/` line to **"fonts, loc/*.json, deck media, lottie/*.json, whatsnew/ (notes + media, laid down by `pack-wavee-msix.ps1 -NotesDir`)"**. The MSIX pack script and the release tool both write into `Assets\whatsnew`; silently dropping it from the tree ships a build whose own release notes are unreachable offline. |
| **D8** | the settings keys this surface owns | `WaveeSettings.{SetupPending, SetupCompleted, TermsAcceptedVersion, ReleaseNotesAutoShow, ReleaseNotesLastSeen, ReleaseNotesPendingFrom, ReleaseNotesPreviousVersion, CrashPromptOptOut, TipsSeen}` | `Platform.Boot()` is named; no key schema | keep the exact key STRINGS (`setup.pending`, `setup.completed`, `setup.terms.acceptedVersion`, `app.whatsnew.autoShow`, `app.whatsnew.lastSeenVersion`, `app.whatsnew.pendingFrom`, `app.whatsnew.previousVersion`, `crash.promptOptOut`, `tips.seen`) — they live in `HKCU\Software\Wavee\Wavee\Settings` and a renamed key silently re-runs a one-time wizard on every existing install. |
| **D9** | `AppVersion.Info` — `Core`, `Quad`, `Channel`, `IsStore`, `StoreId`, `UpdateBaseUrl` | build-time stamped statics | not named | `Platform/Platform.cs`. `IsStore` gates the Store highlight (`HighlightVisibility`), `StoreId` the Store listing link, `UpdateBaseUrl` the notes fetch root (a loopback feed for the E2E test must reach the same server). |

---

## 8. Pure rules to port verbatim

These are **ported, never re-derived**. Every one is engine-free by construction today (System + BCL + the generated
`Strings` consts + app enums) and source-included into `Wavee.Tests`; in 0.3 the test project references
`Wavee.csproj` outright (plan §3.2), so they just need to sit in the CORE section of the named file.

| name | 0.2.9 file | what it decides | tests | 0.3 destination |
|---|---|---|---|---|
| `SetupGating` | `App/SetupGating.cs` (201) | armed / completed / deferred markers and their idempotence; `TermsVersion = 1`; `NeedsTermsRearm`, `GrandfathersTerms`, `NeedsFreshInstallReset`, `CarriesAcrossAuthGate`; `NextPage`/`PrevPage` with the sign-in skip; `StepTotal = 2`, `StepNumber`, `Progress`; `ShowsBack`, `BackSpacerApplies`; `CanDismiss`/`EscapeClosesPlate`; `SkipSignIn`, `SkipsLocalPlayback`, `IsLastPage`, `SuppressesRuntimePrompts` | `Wavee.Tests/SetupGatingTests.cs` | **`Screens/Setup.cs` CORE** |
| `SetupCommands` + `SetupSignInPhase` / `SetupRuntimeFacet` / `SetupButtonKind` / `SetupCtx` / `SetupCommandRow` | `App/SetupCommands.cs` (110) | the whole footer label table (loc KEYS, never text), `PrimaryEnabled`/`SecondaryEnabled`, `NeedsPairingChallenge`, and `Project(LoginPhase, LoginStep, AuthStatus)` folding ten phases into six facets | `Wavee.Tests/SetupCommandsTests.cs`, `SetupSignInProjectionTests.cs` | **`Screens/Setup.cs` CORE** |
| `SetupLayout` | `Features/Setup/SetupLayout.cs` (85) | every wizard number: `762 x 490`, min `320 x 184`, viewport margin 32, pad 24, icon column 192 + gap 24 at the **770** breakpoint, footer 80 / pad 24 / gap 6, progress column 210 (+48 right pad, bar 162), back 30/12, body spacing 20/12, scroll gutter 24, `QrPlateBudget`, `Width`/`Height`/`ShowsIcon`/`ProgressColumnFor`/`FooterButtonWidth`/`CoverFor`/`BodyLaneHeight`/`SignInIdleBodyHeight` | `Wavee.Tests/SetupLayoutTests.cs` | **`Screens/Setup.cs` CORE** |
| `SetupEntryPoint`, `SetupPage`, `SetupCover` | `App/SetupEntryPoint.cs` (20), `App/SetupGating.cs:14`, `Features/Setup/SetupLayout.cs:11` | the three enums `SetupGating`/`SetupCommands` switch on | (driven by the above) | **`Screens/Setup.cs` CORE** |
| `PlaybackRuntimeSetupModel.Phase` | `Features/Shell/PlaybackRuntimeSetupModel.Phase.cs` | the eight provisioning phases the LocalPlayback page's `HeaderFor`/`LeadFor`/`PhaseContent` and `SetupSession.RuntimeFacetFor` all switch on — `Offer, FetchingCatalog, Downloading, Verifying, Untrusted, Ready, Failed, Advanced`. **Already split into its own engine-free partial file and `<Compile Include>`d by `Wavee.Tests`** (`Wavee.Tests.csproj:301`) precisely so `SetupCommandsTests` can drive the real fold | (via `SetupCommandsTests`) | **`Platform/Platform.cs` CORE** (with DATA GAP D3's `RuntimePhase`) — it is the same enum, do not mint a second one in `Screens/Setup.cs` |
| `ReleaseNotesStore` | `App/ReleaseNotesStore.cs` (543) | the embedded → cache → release-asset ladder, `MediaPath`, `RefreshIndexAsync`/`IndexSnapshot`, `RefreshIssueStatesAsync`. **Not a "pure rule" but already engine-free by construction and source-included** (`Wavee.Tests.csproj:589`) — `ReleaseNotesStoreTests` drives the REAL service over `ScriptedHttpHandler`. The 0.3 `ReleaseNotes.Host.cs` must keep that property or the test dies with it | `Wavee.Tests/ReleaseNotesStoreTests.cs` | **`Screens/ReleaseNotes.Host.cs`** |
| `SetupSignInPresentation` | `App/SetupSignInPresentation.cs` (38) | `ShowsIdleCards(phase)` — which facets keep the two option cards under an error bar; `DisplayNameFor(live, snapshot)` — the "raw Spotify id" fix | `Wavee.Tests/SetupSignInPresentationTests.cs` | **`Screens/Setup.cs` CORE** |
| `SetupRuntimePresentation` | `App/SetupRuntimePresentation.cs` (22) | `ProgressFraction(received, total)` (clamped, 0 when total <= 0); `ShortHash` = 4 + `…` + 4 | `Wavee.Tests/SetupRuntimePresentationTests.cs` | **`Screens/Setup.cs` CORE** |
| `SetupBootstrap` | `App/SetupBootstrap.cs` (103) | arming a fresh install, the terms re-arm and the fresh-install reset at boot | (via `SetupGatingTests`) | **`Screens/Setup.cs` CORE** (called from `Platform.Boot`) |
| `QrPlate` | `Features/Auth/QrPlate.cs` (23) | `CellFor(size, modules) = max(2, size/(modules+8))`, `PlateFor = (modules+8) * cell` — **the floor is 2, not 3** (a 3 floor made an 80-DIP ask for a v3 symbol paint 111) | `Wavee.Tests/QrGridTests.cs` | **`Screens/Setup.cs` CORE** |
| `Qr` (the encoder) | `Features/Auth/Qr.cs` (368) | the whole ISO/IEC 18004 encode: version pick, ECC M, mask selection, matrix | `Wavee.Tests/QrTests.cs` (round-trip decode) | **`Screens/Setup.cs` CORE** |
| `QrDump` (the `--qr-dump` probe) | `Features/Auth/QrDump.cs` (93) | the bisector (§1.5, §6.6): one `Qr.Encode` → an ASCII structural block (margin 2) **plus** a grayscale PNG (quiet 4, 14 px/module, gray 0/255), written by its own minimal encoder — IHDR/IDAT/IEND, a 0 filter byte per scanline, `ZLibStream` Optimal, table-driven CRC32 (`:44-92`) | **none, and it is NOT source-included today** — everything the probe adds on top of `Qr` is untested, and a wrong CRC or a missed filter byte yields a file no decoder opens while the probe still exits 0. **Add one:** parse the chunks back, expand the scanlines, downsample by `scale`, strip the 4-module quiet zone and feed the recovered matrix to the `QrReader.Decode` that already exists (`Wavee.Tests/QrTests.cs:29`), plus `px == (n + 8) * 14` and gray values drawn only from {0, 255}. It is **not writable against 0.2.9 as it stands**: `WritePng` is private and takes a path and `Run` takes a `WaveeLogger`, so 0.3 must first expose an engine-free `WritePng(Stream, size, px)` seam | with the other headless probe arms (`CliRun`, `Diagnostics.Host.cs` — D6) — **not** `Screens/Setup.cs`: it is a CLI arm, not part of the setup screen (§9.5) |
| `WaveeLottieRecolor` | `Features/Setup/WaveeLottieRecolor.cs` (61) | the two exact source colours, the H 195-285 / S >= .35 hue-rotation band, the 1/64 tolerance, alpha preservation | `Wavee.Tests/WaveeLottieRecolorTests.cs` | **`Screens/Setup.cs` CORE** (the testable `Apply(c, accent, accentDeep)` overload stays; `Apply(c)` reading live `Tok` is the UI seam) |
| `HighlightCardMetrics` | `../Wavee.Core/ReleaseNotes/HighlightCardMetrics.cs` (99) | the card's whole geometry AND the dialog's height budget: 16:9, `CardMaxW 420`, `CompactCardMaxW 356`, title 13.5/18/2, body 12.5/17/4 = 68, fade 24, `OverflowThreshold 68.5`, paddings, `TextBlockHeight`, `DialogCardWidth`, `BandHeight`, `CardHeight`, `HeroHeight`, `DialogHeight`, `PlateWidth 720`, `PlatePadX 26`, `PlateMaxHeight 620`, `CardGap 10`, `RowPadTop/Bottom 6/14`, `FooterHeight 62` | `Wavee.Tests/ReleaseNotes/HighlightCardMetricsTests.cs` (incl. the every-shape <= 620 sweep) | **`Screens/ReleaseNotes.cs` CORE** |
| `HighlightViewerLayout` + `HighlightNavKey` / `HighlightSlideDirection` / `HighlightStep` | `../Wavee.Core/ReleaseNotes/HighlightViewerLayout.cs` (81) | `PlateWidth(vpW, vpH)` (the clamp ladder, window edge beats the floor), `ImageHeight` (always `W*9/16`), `PlateMaxHeight`, `ChromeCircle 36` / `ChromeInset 12`, and `Step`/`StepTo` — **clamped, never wrapping** | `Wavee.Tests/ReleaseNotes/HighlightViewerLayoutTests.cs` | **`Screens/ReleaseNotes.cs` CORE** |
| `HighlightViewerMotion` | `Features/ReleaseNotes/HighlightViewerMotion.cs` (42) | `SlideDistance 24`, `ExitMs 120`, the forward/back/first recipes and the **direction-free exit** | `Wavee.Tests/ReleaseNotes/HighlightViewerMotionTests.cs` (it source-includes cleanly despite the engine types) | **`Screens/ReleaseNotes.UI.cs`** (engine-typed) — keep it a separate named region so the test keeps working |
| `HighlightVisibility` | `../Wavee.Core/ReleaseNotes/HighlightVisibility.cs` (43) | `IsStore`, `IsVisible(h, isStoreInstall)`, `SelectVisible(list, isStoreInstall, max)` — the cap counts what actually RENDERS | `Wavee.Tests/ReleaseNotes/HighlightVisibilityTests.cs` | **`Screens/ReleaseNotes.cs` CORE** |
| `MarkdownLite` | `../Wavee.Core/ReleaseNotes/MarkdownLite.cs` (194) | the inline tokenizer: bold, code, links, bare URLs, `#123`, `!430`, `@mention`, `owner/repo#n` | `Wavee.Tests/ReleaseNotes/MarkdownLiteTests.cs` | **`Screens/ReleaseNotes.cs` CORE** |
| `ReleaseNotesRange` | `../Wavee.Core/ReleaseNotes/ReleaseNotesRange.cs` (108) | which releases sit between `lastSeen` and the running build on a channel — the "since you last looked" stack | `Wavee.Tests/ReleaseNotes/ReleaseNotesRangeTests.cs` | **`Screens/ReleaseNotes.cs` CORE** |
| `ReleaseNotesDocument` + `Normalize()` and the whole record family | `../Wavee.Core/ReleaseNotes/ReleaseNotesDocument.cs` (290) | the wire schema AND the ONE null-repair chokepoint every consumer depends on | `Wavee.Tests/ReleaseNotes/ReleaseNotesNormalizeTests.cs`, `ReleaseNotesJsonTests.cs` | **`Screens/ReleaseNotes.cs` CORE** (required by `Wavee.ReleaseTool`, plan §3.3) |
| `ChangelogParser` | `../Wavee.Core/ReleaseNotes/ChangelogParser.cs` (224) | `CHANGELOG.md` -> sections/items/refs, and the stable `"{kind}-{index}"` item ids | `Wavee.Tests/ReleaseNotes/ChangelogParserTests.cs` | **`Screens/ReleaseNotes.cs` CORE** (tool) |
| `ReleaseNotesValidation` | `../Wavee.Core/ReleaseNotes/ReleaseNotesValidation.cs` (568) | what `Wavee.ReleaseTool validate` enforces on a document | `Wavee.Tests/ReleaseNotes/ReleaseNotesValidationTests.cs` | **`Screens/ReleaseNotes.cs` CORE** (tool) |
| `ReleaseCommits` | `../Wavee.Core/ReleaseNotes/ReleaseCommits.cs` (146) | commit -> issue/PR association for the appendix | `Wavee.Tests/ReleaseNotes/ReleaseCommitsTests.cs` | **`Screens/ReleaseNotes.cs` CORE** (tool) |
| `IssueStateBudget` / `IssueStateCache` | `../Wavee.Core/ReleaseNotes/{IssueStateBudget,IssueStateCache}.cs` (54 / 55) | which keys to fetch and when to stop (20 per open, 24 h TTL, stop on 403) — **the single owner of the numbers** | `Wavee.Tests/ReleaseNotes/IssueStateBudgetTests.cs` | **`Screens/ReleaseNotes.cs` CORE** |
| `ReleaseNotesText` (the engine-free half) | `Features/ReleaseNotes/ReleaseNotesLinks.cs` (65) | `Repo`, `RepoUrl`, `IssueUrl`, `ReleaseTagUrl`, `ReleasePageUrl(snapshot)`, `Date(iso)` via the `whatsNew.dateFormat` loc key — the **single owner** of "which GitHub page does this open?" | (exercised by `AppUpdateToastsTests`, `ReleaseNotesStoreTests`) | **`Screens/ReleaseNotes.cs` CORE** |
| `AppUpdateToasts` | `App/AppUpdateToasts.cs` (153) | the `(previous, next)` snapshot -> `ToastPlan` table, action labels, `FailureText`, `ReleaseName` | `Wavee.Tests/AppUpdateToastsTests.cs` | **`Platform/Notify.cs` CORE** — owner I, Wave 4 (arbitration 2026-09-12, A8). **Not** `Screens/ReleaseNotes.cs`: the same table also feeds the OS banner (`14-os-surfaces.md`) and the in-app update card (`19-shell-overlays.md`), and `ReleaseName`/`FailureText` are what Settings › About’s `StateSentence` calls — three consumers, one file. This chapter READS it (the after-update plate’s `from -> to` pill, W23) and does not port it |
| `AppUpdateVersion` | `../Wavee.Core/Notifications/AppUpdateVersion.cs` | `IsNewer(a, b)` (the unread-dot rule) and `ReleaseTagVersion(quad)` (the dialog's `from -> to` pill) | (via the above) | `Platform/Platform.cs` CORE |
| `AppVersionDisplay.Of` | `App/AppVersion.cs:93-104` | the after-update plate's welcome line and About's version line: bare / `"codename"` / `"codename" · Beta n`, and `SemVer` instead of `Core` on a dev build | (none today — **add one**; it is three branches feeding a 26-DIP headline) | `Platform/Platform.cs` CORE |
| `ReportKindIndex` | `Features/Feedback/ReportKindIndex.cs` (33) | the `Segmented` index <-> `ReportKind` round trip; `Crash` is never a segment | `Wavee.Tests/Feedback/ReportKindIndexTests.cs` | **`Screens/Feedback.cs` CORE** (proposed, §9) |
| `ReportKinds` / `ReportChannel` / `ReportChannels` | `Diagnostics/ReportKinds.cs` (90) | the routing table: paths, title prefixes, templates, categories, **field ids in URL order**, truncation order, paste-box labels, and the dropdown option strings copied VERBATIM from the issue-form YAML | `Wavee.Tests/Feedback/ReportChannelsTests.cs` (pins them against `.github/ISSUE_TEMPLATE/*.yml`) | **`Screens/Feedback.cs` CORE** |
| `ReportBundle` | `Diagnostics/ReportBundle.cs` (200) | `MaxBytes 60 KB`, `CrashLogLines 300`, `ManualLogLines 200`, `PreviewChars 12 000`, `Build`, `Preview`, `FileName`, `SplitCrashReport` | `Wavee.Tests/Feedback/ReportBundleTests.cs` | **`Screens/Feedback.cs` CORE** |
| `IssueFormUrl` | `Diagnostics/IssueFormUrl.cs` (95) | URL assembly + the over-budget truncation order | `Wavee.Tests/Feedback/IssueFormUrlTests.cs` | **`Screens/Feedback.cs` CORE** |
| `ReportRedactor` / `RedactionRules` | `Diagnostics/ReportRedactor.cs` (177) | the redaction rule table (OS account + machine, the signed-in id/display name, Connect device names) | `Wavee.Tests/Feedback/ReportRedactorTests.cs` | **`Screens/Feedback.cs` CORE** |
| `ReportIdentity` | (with `ReportKinds`) | version line, architecture, Windows build, `ArchLabel`/`InstallLabel` | `Wavee.Tests/Feedback/ReportIdentityTests.cs` | **`Screens/Feedback.cs` CORE** |
| `CrashPromptPolicy` / `CrashPromptDecision` / `CrashPromptMode` | `Diagnostics/CrashPromptPolicy.cs` (50) | `Decide(pendingReport, dumpPath, previousRun, optOut, versionChanged)` and its priority ladder; `Mode = optOut ? Toast : Dialog` | `Wavee.Tests/Feedback/CrashPromptPolicyTests.cs` | `Diagnostics/Diagnostics.cs` CORE |
| `CrashReportFiles` | `Diagnostics/CrashReportFiles.cs` | newest-first listing, `TryStamp`, never throws | `Wavee.Tests/Feedback/CrashReportFilesTests.cs` | `Diagnostics/Diagnostics.cs` CORE |
| `WaveeTipsCore` | `App/WaveeTipsCore.cs` (100) | `ShouldShow`, `Parse`/`Add`/`Contains` over the one `tips.seen` set | `Wavee.Tests/WaveeTipsCoreTests.cs` | **`Shell/Shell.cs` CORE** (with the presenter `WaveeTips`, 188, in `Shell/Shell.Host.cs` SHELL) — owner I, Wave 4, and **`19-shell-overlays.md` owns both**. Arbitration 2026-09-12 (A10): this chapter drops the claim; the earlier "`Platform/Design.cs` or …" alternative is gone. The one shipped tip id is anchored on the detail page (`DetailTracks.cs:3801`), not on any surface in this chapter |
| `PageNavMotion` | `Features/Shell/PageNavMotion.cs` (122) | `RecipeFor(NavTransitionKind)` — the wizard's KeepAlive reuses the shell's page-swap recipes verbatim | (chapter 18) | `Shell/Shell.UI.cs` — this surface only **calls** it |

---

## 9. Re-author notes

### 9.1 What must not be simplified

1. **The `TextStack` sizer trick** (`HighlightViewer.cs:395-431`). It looks like dead weight — N invisible copies of
   the text — and it is the only reason the viewer does not bounce. `FlexLayout.AddOrphanMain` folds an **exit orphan's**
   height into its parent column's measure for the whole 160 ms exit, so a keyed slide inside a flex column makes the
   plate `band + pager + old text + new text` for that window, and a centred plate jumps up by half of it, then snaps
   to the new text's height when the orphan is reclaimed. `MeasureZStack` has no orphan fold; the sizers pin the height
   to the tallest highlight from frame one. Keep the sizers **inert**: `Opacity 0` + `HitTestVisible = false` (stops
   hit-testing the whole subtree) + `isEnabled: live = false` on their buttons (a disabled node is never collected by
   the focus trap).
2. **Two keyed subtrees, not one** (`:220, 484`). The design doc wanted one keyed slide covering image + pager + text.
   Remounting the pager destroys the focused pip, and the chevrons' dim is an opacity transition on a **persistent**
   node. The band's tint likewise lives on the **stable** band, never on the keyed layer — on the keyed layer the
   outgoing and incoming translucent fills stacked and the band flashed ~8 levels brighter for ~100 ms on every step
   (`:203-207`).
3. **`_dir` is a plain field, not a signal** (`:63-65`). A motion-only value must not trigger a render of its own; it
   is read by the render that `_index.Value = …` already triggers on the next line. The same discipline governs
   `SetupSession.Dir`, which `SetupDialog`'s KeepAlive `TransitionFor` **`Peek()`s** (`SetupDialog.cs:192`).
4. **The `_pip` mirror is written from an effect, never during render** (`:84`). The pips write back only through
   their own `onClick`, so `Go()` has already set `_dir` before the index moves.
5. **The body slot measures overflow; it does not estimate it** (`HighlightCard.cs:262-298`). `MaxLines = 0` inside a
   `Shrink = 0` child of a **flex column** (not a bare ZStack layer): `ArrangeZStack` clamps an auto-sized layer to the
   stack, so no child of a 68-DIP ZStack can ever report "there is more". And the signal flips only when the **answer**
   changes, never on every bounds change.
6. **`EdgeFade`, not a painted gradient** (§0.6, §4.6). This is what keeps the card on the standard translucent Mica
   surface.
7. **The pairing code is minted on page MOUNT** (`SetupPage.SignIn.cs:64-69`), not when the user arrives, so it cannot
   expire while they read Terms; `NeedsPairingChallenge` returns false in requesting and terminal phases so a reactive
   re-render cannot start a second request.
8. **`lastSeen` is read before the load** (§0.11). This is one line and the entire "since you last looked" feature.
9. **`ReleaseNotesPendingFrom` is cleared at `Open()`, before the plate renders** (`AfterUpdateDialog.cs:39`). A crash,
   a force-quit or a dismissal must never resurrect a one-shot notice.
10. **`ReportChrome`'s `s_lastOpenedSeq` is a STATIC, not a `UseRef(-1)`** (`ReportChrome.cs:26-29`). A `UseRef`
    baseline treats "whatever Seq is current when I first mount" as already-served and swallows the first request after
    an `OverlayHost` subtree remount.
11. **The scan card is its own component** (`SetupPage.SignIn.cs:224-261`). Its 1 Hz tick must re-render a small card,
    never the whole sign-in page — which also hosts a mounted Lottie.
12. **`SetupText` is five primitives and a pass-through card.** Do not re-introduce a stage/decision split, a hero rail,
    chips or rows; every page is one content column.
13. **The footer's `Edges4(0, 0, 48, 0)`** is a RIGHT pad. Getting the argument order wrong is a shipped bug with a
    named engine gate (`gate.layout.footer-band`).
14. **`PagesHost` gives the KeepAlive a DEFINITE box** (`SetupDialog.cs:175-195`) and both `SetupPagePlaceholders.For`
    and `SetupPageHost.Frame` wrap their `Embed.Comp` in a box that claims `Grow/Shrink/MinWidth/MinHeight`. A
    `ComponentEl` carries no layout columns of its own; without these three boxes the page's `ScrollView` shrink-wraps
    to content, nothing scrolls, and the last row clips instead of scrolling into view.

### 9.2 Traps

- **Props freeze at mount.** Three components in this surface *must* take pushed props: `SetupPageFrame`
  (its body is rebuilt whenever a page-local signal moves), `ChangelogSection` (issue states land after the document),
  `SetupScanCard` (a new code is also a `Key` remount). Everything else is legitimately ctor-frozen because the value
  genuinely cannot change for that mount — `HighlightItem` is immutable, `open` captures its index, and the report
  dialog rebuilds the whole card on a kind change.
- **`Key` remounts to respect:** `"setup:page:<n>"` (the KeepAlive slot, numeric) **and** the inner
  `"setup:page:terms|sign-in|local-playback"` (the string form on `SetupPagePlaceholders.BodyFor`'s `Embed.Comp` —
  a *different* key from the slot's, `SetupPage.Placeholders.cs:30-32`), `"setup:capture:<n>"`, `"setup:frame:<n>"`,
  `"setup:layout:wide|compact"`, `"signin:<facet>"`, `"signin:scan:<code>"` / `"signin:scan:pending"`,
  `"runtime:<phase>"` (the MODEL phase name, e.g. `runtime:FetchingCatalog`), `"runtime:fineprint"`,
  `"hl:<version>:<id>"`, `"dlg-hl:<id>"`, `"hv:<version>:<id>:img|:txt|:size"`,
  `"pip:<i>"`, `"hv:close|prev|next"`, `"rail:<version>"`, `"sec:<version>:<kind>"`, `"report-body:<kind>"`,
  `"report-card:<kind>"`, `"crash-reports:<path>"` / `"crash-reports:empty"`, `"page:whatsnew:<arg>"`,
  `"login-step-mark:<state>"` (failed|done|current|pending — the mark's scale-pop, `LoginView.cs:78`).
  The chrome circles' keys are **caller-supplied and stable across slides** — that is what makes the dim an in-place
  ease instead of a remount.
- **`ReuseGuard`** will fire if the highlight strip is keyed by index instead of `doc.Version + ":" + highlight.Id`
  (`HighlightStrip.cs:33`), if the rail's `Flow.For` sees a duplicate version (a rehearsal feed with two quads of one
  semver — deduped at `ReleaseRail.cs:37-39`), or if the viewer's `SlideId` recipe drifts from the strip's
  (`HighlightViewer.cs:520-521`).
- **Zero-allocation scroll frames vs per-row richness.** This surface reconciles the two differently from a track
  list, and that is correct: the What's-new page is a **document**, not a virtualized list. It renders at most 3 cards,
  <= 8 + expansion rows per section, and <= ~20 rail rows; the expensive work is pushed *out of render* instead —
  poster paths resolved in the loader, issue states fetched once per open, `MarkdownLite.Tokenize` run per render but
  over one sentence. The two places that WOULD have been per-frame work are both fixed the same way: a 1 Hz signal on
  a small component (the countdown) and an `OnBoundsChanged` that writes only when the answer changes (the fade). Do
  not virtualize any of this; do not move poster resolution back into `Render`.
- **The store card must never nest a button in a button** — hit region and footer are siblings under the frame
  (`HighlightCard.cs:222-237`).
- **`HighlightViewerLayout.PlateWidth` re-applies the window-edge clamp after the floor** (`:36-40`): `320` is a floor
  for the *ladder*, not a promise; a 320-wide window yields 224.
- **`SettingsCard` has TWO responsive thresholds, not one.** `WrapThreshold 476` moves the Content slot below the
  header; `WrapNoIconThreshold 286` drops the header ICON (`SettingsCard.cs:31-32, 129-130`). Both are reachable inside
  the wizard's own clamp ladder (W3b). A 0.3 port that hand-rolls a card shape instead of using the engine control will
  silently lose both, and neither is modelled by `SignInIdleBodyHeight`.
- **`ReleaseNotesText.Date` echoes, it does not blank.** Every `is { Length: > 0 }` guard around it therefore means
  "the document carried *something*", not "the document carried a date" (`ReleaseNotesLinks.cs:58-64`).
- **Two quiet zones in one file.** `--qr-dump`'s PNG uses the ISO 4 modules; its ASCII block uses 2 (`QrDump.cs:32`
  vs `:24`). Anyone who points a phone at the terminal fails for a reason that is not the encoder. In 0.3 either
  label the ASCII arm "structural, not scannable" in the probe's own output or raise it to 4 — do **not** reconcile
  them by narrowing the PNG's, which is the only scannable artefact (§3.5, W27).

### 9.3 Where plan §2 / §4.12 / §4.13 are wrong or too thin for this surface

1. **`Features/Auth/**` has no home in the §2 tree at all.** 775 lines, and **five** files, not four: the QR encoder
   (`Qr.cs`, 368, unit-tested), `QrPlate` (23), `QrGrid` (75), `LoginView` (216 — its four surviving live
   sub-components `LoginStepRow`, `LoginStepBar`, `WaitingDots`, `LoginCountdown`, plus `OpenUrl`, which is called from
   Home, the notification panel, the profile menu, Settings and the wizard) and **`QrDump` (93, the `--qr-dump` probe,
   §1.5)** — 368 + 23 + 75 + 216 + 93 is where the 775 comes from. §2 lists `Screens/Setup.cs` + `Setup.UI.cs` and
   nothing else. **Fix:** fold the first four into those two files and say so in §2; `OpenUrl` is really a platform
   shim and belongs in `Platform/Platform.cs`, and `QrDump` belongs with the headless probe arms rather than in a
   screen file (§9.5).
2. **There is no `Screens/Feedback.cs` (CORE) — only `Feedback.UI.cs`.** The feedback surface has ~700 lines of
   engine-free, unit-tested core (`ReportKinds`/`ReportChannels`, `ReportBundle`, `IssueFormUrl`, `ReportRedactor`,
   `ReportIdentity`, `ReportKindIndex`) with **nine** test files under `Wavee.Tests/Feedback/` (`CrashPromptPolicyTests`,
   `CrashReportFilesTests`, `IssueFormUrlTests`, `ReportBundleTests`, `ReportChannelsTests`, `ReportIdentityTests`,
   `ReportKindIndexTests`, `ReportRedactorTests`, `RunMarkerTests`). Putting them in a `.UI`
   file violates G7 and D17. **Fix:** add `Screens/Feedback.cs` to §2 (~700 lines).
3. **`Setup.Host.cs` / a session home is unstated — RESOLVED, plan §9.6 Q8, 2026-09-12.** `SetupSession` is a live
   model that must live **outside** the element tree so the pre-auth -> post-auth remount can hand the same
   instance to a second mount site. **Answer: `Screens/Setup.cs` (CORE)** — the orchestrator's call, under the
   plan's own rule that the plan decides where code lives; no chapter named a home, so this one didn't get to.
   `Setup.cs`'s §2 budget grows 950 → 1,150 (+200) to carry `SetupSession`/`SetupSession.MarkerEpoch` (324 lines of
   0.2.9) alongside `SetupGating`/`SetupCommands`/`SetupEntryPoint`/`SetupLayout`.
4. **§2's `assets/` line drops `lottie/` and `whatsnew/`** — see DATA GAP D7. Two shipped asset trees silently
   disappear.
5. **§4.12/§4.13 describe entity rows and entity pages only.** Nothing in the plan's UI examples covers a **modal
   overlay**, a **keyed slide with an exit orphan**, a **zero-size chrome component** (three of which this surface
   owns, all mounted inside `OverlayHost`), or a **page whose model is a file/HTTP document rather than a handle**.
   The `UseEffect(() => Entities.Ensure(...))` pattern in §4.13 does not transfer: this surface's readiness is a single
   `Signal<View?>` published off-thread. Add a short "screens are not entity pages" note to §4.13 so Wave 6 owner R
   does not try to force `Entities.Ensure` onto a release-notes document.
6. **Plan §3.3 says `Wavee.ReleaseTool` gets `<Compile Include="..\Wavee\Screens\ReleaseNotes.cs" />`.** That forces
   the document model + `ChangelogParser` + `ReleaseNotesValidation` + `ReleaseCommits` + the JSON context
   (1 248 lines today) into ONE file that must not reference the engine — and `HighlightCardMetrics`,
   `HighlightViewerLayout`, `HighlightVisibility`, `MarkdownLite`, `ReleaseNotesRange`, `IssueStateBudget` and
   `ReleaseNotesText`'s links half all want to be there too (another ~650; `AppUpdateToasts`’ 153 is **no longer in
   this pile** — arbitration 2026-09-12 sends it to `Platform/Notify.cs`). That is a **~1 900-line
   CORE file** before a single UI line. Either accept a named partial (`ReleaseNotes.Model.cs` + `ReleaseNotes.cs`,
   which the plan's own "30 % over budget gets a named partial" rule already permits) or give the tool two includes.
7. **`AppUpdateToasts` and `WaveeTips`/`WaveeTipsCore` were homeless; they are not any more.** Both are consulted by
   this surface and **owned elsewhere** — arbitration 2026-09-12: `AppUpdateToasts` → `Platform/Notify.cs` CORE
   (A8, with the rest of the one notification stack, owner I in Wave 4, `14-os-surfaces.md` §9 and
   `19-shell-overlays.md` §8); `WaveeTips` + `WaveeTipsCore` → `Shell/Shell.cs` CORE + `Shell/Shell.Host.cs` SHELL
   (A10, `19-shell-overlays.md` §9.5). §2 of the plan still has to gain all four rows — that is the live defect —
   but the *destination* is settled and no longer contested by this chapter.
8. **The three zero-size chromes need a named mount point in `Shell.UI.cs`, in order.** `SetupChrome` ->
   `ReportChrome` -> `AfterUpdateChrome`, and the order is load-bearing: `ReportChrome` must mount before
   `AfterUpdateChrome` because `AfterUpdateDialog.CrashNoticeThisLaunch` has to be set before the plate's effect reads
   it (`WaveeShell.cs:1483-1494`).

### 9.4 The one 0.2.9 wart to fix (not a simplification)

The after-update plate renders its hero **before** the document lands: the tagline is `""` and the card row is absent,
then both appear in one frame and the plate grows from ~213 to 505 DIP (W23). Every other surface in this chapter
already obeys "publish the whole model or nothing". Two parity-safe options, in order of preference:

- **(a)** open the overlay only after `LoadAsync` completes — move the load into `AfterUpdateChrome`'s effect and pass
  the `(Doc, Cards)` into `Open()`. The plate then mounts at its final height and the Modal chrome scales in once.
- **(b)** reserve the row: render a `HighlightCardMetrics.CardHeight(DialogCardWidth(3), 2, false)` = 272-DIP
  placeholder row and a one-line tagline spacer while `_loaded` is null.

Do **not** ship the pop. Flag it to the owner as a deliberate change and note it in the CHANGELOG with its issue ref.

### 9.5 Line budget

| file | 0.2.9 lines it absorbs | plan §2 implied target | honest estimate |
|---|---|---|---|
| `Screens/Setup.cs` (CORE) | `SetupGating` 201 + `SetupCommands` 110 + `SetupEntryPoint` 20 + `SetupLayout` 85 + `SetupSignInPresentation` 38 + `SetupRuntimePresentation` 22 + `SetupBootstrap` 103 + `QrPlate` 23 + `Qr` 368 + `WaveeLottieRecolor` 61 = **1 031** | (unstated) | **~950** |
| `Screens/Setup.UI.cs` | the rest of `Features/Setup` (1 032) + `QrGrid` 75 + `LoginView` 216 = **1 323** | (unstated) | **~1 250** |
| `Screens/ReleaseNotes.cs` (CORE) | `Wavee.Core/ReleaseNotes` 1 882 + `ReleaseNotesLinks` 65 = **1 947** (`AppUpdateToasts`’ 153 left this row on 2026-09-12 for `Platform/Notify.cs` — A8) | (unstated) | **~1 850** (still needs a named partial — §9.3.6) |
| `Screens/ReleaseNotes.UI.cs` | `Features/ReleaseNotes` minus the links half (2 091) | (unstated) | **~1 950** |
| `Screens/ReleaseNotes.Host.cs` | `App/ReleaseNotesStore.cs` 543 | (unstated) | **~520** |
| `Screens/Feedback.cs` (CORE, **proposed**) | `ReportKinds` 90 + `ReportBundle` 200 + `IssueFormUrl` 95 + `ReportRedactor` 177 + `ReportKindIndex` 33 + `ReportIdentity` (~60) = **655** | **absent from §2** | **~650** |
| `Screens/Feedback.UI.cs` | `ReportDialog` 521 + `ReportComposer` 151 + `ReportChrome` 80 + `ReportRequests` 33 + `CrashReportsCard` 59 = **844** | (unstated) | **~800** |
| **total for this chapter's surface** | **≈ 8 650** (of the 9 385 read; the rest is shared with Diagnostics/Platform, and `AppUpdateToasts` 153 + `WaveeTips`/`WaveeTipsCore` 288 are read here but ported by owner I — §9.3.7) | §2 gives `Screens/` **13 files / ~13 700** total, of which `Settings.*` and `Diagnostics.*` plausibly take ~10 000 -> **~3 700 implied for Setup + ReleaseNotes + Feedback** | **~7 950 across 7 files** |

**`Features/Auth`'s 775 lines are only 682 accounted for above** — `Qr` 368 + `QrPlate` 23 in the CORE row, `QrGrid` 75
+ `LoginView` 216 in the UI row. The missing **93** are `QrDump.cs` (§1.5, §6.6), and they belong in neither: budget
them beside `Diagnostics/CliRun.cs` with the other probe arms, or the file is one no wave owns.

**The §2 Screens budget is roughly 2.2x too small for this surface.** The overrun is not fat: 368 lines of it is a QR
encoder, 568 is release-notes validation the release tool needs, 1 882 is the document model + parsers, and ~700 is
report plumbing with seven test files. Either raise `Screens/` to ~19 000 or move `ReleaseNotes.cs` (CORE) and
`Feedback.cs` (CORE) out of the `Screens/` count. Wave 6 owner R should be told the real number before starting.

### 9.6 Files / pages missing from the §2 tree

`Features/Auth/*` (5 files) · `Screens/Feedback.cs` · ~~a named home for `SetupSession`~~ — **RESOLVED 2026-09-12,
plan §9.6 Q8: `Screens/Setup.cs`** · `assets/lottie/*` ·
`assets/whatsnew/*` · `WaveeTips`/`WaveeTipsCore` (missing from §2, but **owned**: `Shell/Shell.cs` +
`Shell/Shell.Host.cs`, ch. 19 — A10) · `AppUpdateToasts` (likewise: `Platform/Notify.cs`, ch. 14/19 — A8) · the
three chrome mount points in `Shell.UI.cs`.

---

## 10. Parity checklist

**Reference build (0.2.9, kept):**
`C:\WAVEE\wavee-0.2.9\src\apps\Wavee\bin\Release\net10.0\win-arm64\publish\Wavee.exe`
**Candidate build (0.3):** publish to a separate `-o` verify folder and run it from there (never the owner's running
instance).

**Harness rules.**

- Both builds are **unpackaged**, so they share `HKCU\Software\Wavee\Wavee\Settings` and `%LOCALAPPDATA%\Wavee`.
  Snapshot the key before starting and restore it after: `reg export HKCU\Software\Wavee\Wavee\Settings wavee.reg`.
- Run each build as `Wavee.exe --fake` unless an item says otherwise. `--fake` starts **LoggedOut**, so the pre-auth
  wizard is the first thing on screen; the entry point is `Reauth` when `setup.completed = 1` and `FirstRun` otherwise.
- To force **FirstRun**: set `setup.completed = 0`, `setup.pending = 1`, `setup.terms.acceptedVersion = 0`.
  To force **Reauth**: `setup.completed = 1`. To force **TermsRearm**: `setup.completed = 1`,
  `setup.terms.acceptedVersion = 0` and sign in first (fake auth), then relaunch.
- To reach the **shell** in `--fake`: Terms -> Accept -> "Log in" (the fake backend authenticates immediately) ->
  "Yes, continue" -> Local playback -> "Not now".
- To arm the **after-update plate**: set `app.whatsnew.pendingFrom = "0.2.8.4"` and `app.whatsnew.autoShow = 1`, then
  relaunch (it is consumed on open, so re-set it before every run).
- **Screenshots:** the `--screenshot` harness capture is black under async render — use an OS-level capture of the
  window (or a frame recording) and compare the two images side by side. For motion items, record at >= 60 fps and
  frame-diff; for hover items, park the cursor and capture, then move away and capture again.
- Every item is **binary**: pass only if the candidate matches the reference exactly (or matches the stated number).

| # | route / state | window | what to compare | how |
|---|---|---|---|---|
| 1 | pre-auth, FirstRun, first frame after launch | 1440 x 900 | The titlebar + Mica window and the plate appear **together**; no empty-window flash, no splash. | frame recording of the first 500 ms |
| 2 | Terms | 1440 x 900 | Plate is **762 x 490**, centred, `Radii.OverlayAll` 8, 1-px hairline, dialog shadow. | static capture, measure in pixels / DIP |
| 3 | Terms | 1440 x 900 | Icon column present, **192** wide, 24 gap, Lottie "eula" centred; the scene plays the FIRST HALF once then HOLDS. | frame recording of the first 3 s |
| 4 | Terms | 1440 x 900 | The hero's accent-family colours match the **live system accent**, not Windows blue. Change the accent in Windows Settings, relaunch, recheck. | two static captures |
| 5 | Terms | 1440 x 900 | Header is `Ui.Title` 28/36 with the `0,-4,0,4` margin; the four agreement sections are printed **inline** (no summary card, no disclosure). | static capture |
| 6 | Terms | 1440 x 900 | Footer reads **"Pre-setup"** (not "Step 0 of 2"), bar at **0 %**, 162 wide; buttons **Accept (accent, left)** / **Decline (standard, right)**, 246 each. | static capture + measure |
| 7 | Terms | resize 826 -> 769 | At **769** the icon column AND the progress column disappear **in the same frame**; buttons become equal halves of `(plateW - 60)/2`. No hysteresis: 770 shows them, 769 does not. | frame recording while dragging the edge |
| 8 | Terms | 1440 x 900 | Press `Escape` on a **FirstRun** run: nothing happens (vetoed). Press `Escape` on a **TermsRearm** run: the plate closes to the shell. | live, two runs |
| 9 | Terms | 1440 x 900 | Body scrollbar rail is **visible without hovering** whenever the content overflows, and rides in the plate's own 24-DIP pad (text measure unchanged when it appears). | static capture, two window heights |
| 10 | Sign in / Idle | 1440 x 900 | Body sums to **314 DIP** and does not scroll: lead (2 lines) 40, 20, browser card 68, 20, scan card 114, 20, premium row 32. | static capture + measure |
| 11 | Sign in / Idle | 1440 x 900 | The QR plate is **82 x 82**, pure black on pure white, `Radii.Card` 8 corners, 8-DIP quiet zone, no logo, centred in the card's Content slot. | static capture, zoom to pixels |
| 12 | Sign in / Idle | 1440 x 900 | The scan card's description is one line: `"<CODE> · spotify.com/pair  ·  Expires in mm:ss"`, and the **mm:ss ticks once per second** while the rest of the page does not re-render. | 10 s frame recording + frame-diff (only the card's text region changes) |
| 13 | Sign in / Idle, before the code exists | 1440 x 900 | The scan card shows a 20-DIP indeterminate ring + "Getting your code…" at 12.5 tertiary — **not** an empty card or a blank QR box. | capture the first frame of the Sign-in page |
| 14 | Sign in / Idle | 1440 x 900 | "Wavee needs Spotify Premium.  Don't have a Spotify account? Sign up" is **ONE** 32-DIP wrapping row, not two stacked rows. | static capture |
| 15 | Sign in / Idle -> Busy | 1440 x 900 | The body cross-fades with `Dy +6 -> 0` enter / `Dy -4` exit over 300 ms; the header, icon column and footer do not move. | frame recording, frame-diff |
| 16 | Sign in / Busy | 1440 x 900 | Four step rows wrap to **2 x 2** at width 300, gap 16, staggered **40 ms** apart; the current row's mark is a 16-DIP indeterminate ring, done rows an accent check, pending rows a tertiary bullet. | frame recording |
| 17 | Sign in / Busy | 1440 x 900 | Primary reads "Signing you in…" and is **disabled**; secondary reads "Cancel" and **cancels the attempt** (drops back to Idle with the cards) — it must NOT quit the app. | live |
| 18 | Sign in / Done | 1440 x 900 | Header swaps to **"Is this you?"**; the account row is a plain 68-DIP card (40-DIP `PersonPicture`, `BodyStrong` name, `Caption` tier, "Not me" link) — **no green pill**, and no auto-advance. | static capture |
| 19 | Local playback / Offer | 1440 x 900 | The 30 x 30 back button sits over the content region's **top-left 24-DIP pad corner** with a 12-DIP glyph, and `Backspace` walks back. | static capture + live |
| 20 | Local playback / Downloading | 1440 x 900 | One progress `SettingsCard`: header = the download label, description = `"12.3 / 38.4 MB"` (one decimal), a **162**-wide bar in the Content slot. | static capture |
| 21 | Local playback / Ready | 1440 x 900 | Four detail cards (Version / Architecture / Signature+View / Location+Open folder), and the footer's primary **spans the whole action lane (498 at plate 762)** because there is no secondary. | static capture + measure |
| 22 | any wizard page, post-auth (TermsRearm) | 1440 x 900 | The shell behind the plate is dimmed by `Tok.FillSmoke`, cross-faded over 250 ms on open and on close. | frame recording |
| 23 | `whatsnew` (no arg) | 1440 x 900 | Loading state is a centred 28-DIP `ProgressRing` with **no rail column** at all; the rail's 208 + 14 gap are absent, not empty. | capture the first frame after navigation |
| 24 | `whatsnew` | 1440 x 900 | The page appears **all at once** — hero, highlight strip and every section in the same frame. No section pops in later. | frame recording from navigation to steady state, frame-diff |
| 25 | `whatsnew` | 1440 x 900 | Hero: pills row (`Latest` accent / `Stable` / the quad in `Cascadia Code`), 30/600 headline `Wavee X.Y.Z "Name"`, 14 tagline (MaxWidth 640), meta row `Released / Requires / Tag`, right column `Open on GitHub` + `Copy link` — bottom-aligned (`AlignItems End`). | static capture |
| 26 | `whatsnew` | 1440 x 900 | Highlight strip: eyebrow "HIGHLIGHTS" in `Tok.TextTertiary` with 30/1000-em tracking, then **at most 3** cards, gap 10, equal heights (`AlignItems Stretch`), each capped at 420. | static capture + measure |
| 27 | `whatsnew` | 1440 x 900 | A card whose body overflows shows a **24-DIP bottom alpha fade**; a card whose body fits shows **none**. Resize the window until a card's body reflows from 5 lines to 4 and confirm the fade eases out. | two static captures + a slow resize recording |
| 28 | `whatsnew` | 1440 x 900 | The body slot is exactly **68 DIP** on every card regardless of body length, and the "Read more ›" row is present on every non-store card. | static capture + measure |
| 29 | `whatsnew`, hover a card | 1440 x 900 | Fill goes `FillCardDefault -> FillCardSecondary` over 83 ms, the **border does not change**, "Read more ›" and its chevron turn `AccentTextPrimary`, and there is **no hover scale**. | hover capture + frame-diff |
| 30 | `whatsnew`, press a card | 1440 x 900 | Press scales the card to **0.985** on a spring and releases. | frame recording |
| 31 | `whatsnew` (a store highlight present, non-Store build) | 1440 x 900 | The store card has an **accent border and accent pill**, a tinted `AccentSubtle` band, **no "Read more" row**, and an accent `Get it from the Microsoft Store` button as a **sibling footer**. | static capture |
| 32 | `whatsnew` | 1440 x 900 | A `fixed` section with > 8 items shows 8 rows + a `Show all {n}` subtle button; clicking expands in place and the button disappears permanently for that page visit. | live + two captures |
| 33 | `whatsnew` | 1440 x 900 | Kind badges: `+` success, `~` accent, `✓` caution, `−` critical, `!` caution/critical/tertiary — 22 x 22, r6, wash + ink per §4.4. | static capture |
| 34 | `whatsnew` | 1440 x 900 | Issue chips: 18 tall, r9, 7-DIP dot (open = success green, closed/merged = accent, not-planned = tertiary), number in `Cascadia Code` 11/600, state word 11 tertiary; hovering shows the issue **title** as a tooltip. | hover capture |
| 35 | `whatsnew` | 1440 x 900 | Contributor discs: 18 DIP, overlapping by **5**, 1.5-px `FillSolidBase` ring, initials 9/700; the **same login gets the same tint on both builds**; a 5th contributor collapses to `+N`. | static capture, compare tints |
| 36 | `whatsnew` | 1440 x 900 | Rail: 208 wide; the running version carries a `YOU` pill, unread versions a 6-DIP accent dot, beta versions a `BETA` pill; the selected row has a filled accent bullet and `TextPrimary` version text. | static capture |
| 37 | `whatsnew` with `app.whatsnew.lastSeenVersion` set two releases back | 1440 x 900 | The "Since you last looked: N releases — X → Y" banner appears **above** the scroller with an `AccentSubtle` fill and `AccentDefault` border, a 14-DIP sparkle icon and an `Only the latest` subtle button; release dividers separate the stacked releases; highlights are **merged across the stack, newest first, capped at 3**. | static capture |
| 38 | `whatsnew` for a version with no notes (`whatsnew` + a bogus arg) | 1440 x 900 | Empty state: 32-DIP tertiary tag icon, 13 secondary sentence at MaxWidth 360, an `Open on GitHub` standard button — centred, no rail. | static capture |
| 39 | highlight viewer, opened from the page | 1440 x 900 | Plate width **960**, band **540**, veil `rgba(0,0,0,.72)` covering the **whole window**, plate shadow `blur 90 / y 40`. | static capture + measure |
| 40 | highlight viewer | 1100 x 700 then 900 x 600 then 500 x 420 | Plate widths **604 / 427 / 320** and bands **340 / 240 / 180**. | three static captures |
| 41 | highlight viewer, step forward then back | 1440 x 900 | **The plate does not change height or move vertically at any point during either step.** Only the poster and the text cross-fade/slide. | frame recording, track the plate's top edge per frame |
| 42 | highlight viewer, step | 1440 x 900 | Enter slides `±24` DIP with a 250 ms `SmoothOut`; exit is a **direction-free fade over 120 ms** (a Back step's outgoing slide must not travel forward). | frame recording, frame-diff |
| 43 | highlight viewer, first slide | 1440 x 900 | The first slide has **no entrance slide** (only the Modal plate scale/fade), but it still fades out when you step off it. | frame recording |
| 44 | highlight viewer, pager | 1440 x 900 | Dots are 6 DIP, gap 8; the selected one is a **14-DIP capsule** that GROWS over 120 ms (never jumps) and brightens to `TextPrimary` on the same ramp; the cluster stays horizontally centred and the dots do not drift sideways. | frame recording at 60 fps |
| 45 | highlight viewer, at the first / last slide | 1440 x 900 | The dead chevron **stays in place**, dims to opacity 0.3 with `MediaScrim @ .40` over 83 ms, is not hit-testable, shows no tooltip and no hover scale, and is skipped by `Tab`. | hover capture + keyboard walk |
| 46 | highlight viewer, keyboard | 1440 x 900 | `Left`/`Right` step clamped (no wrap), `Home`/`End` jump, `Escape` closes; on a **single-highlight** viewer arrows do nothing **and do not scroll the page underneath**. | live |
| 47 | highlight viewer, veil | 1440 x 900 | Hovering and pressing the veil produces **no tint and no flash** — the dismiss surface must not behave like a giant button; clicking it closes. | hover capture + frame recording of a press |
| 48 | highlight viewer, video highlight | 1440 x 900 | A labelled on-media pill (`▶ Watch the video on GitHub`, 12.5/600, `Radii.Pill`, `MediaScrim`) sits **centred between the two chevrons**; the card shows a 32-DIP play glyph in the band's top-right. | static capture |
| 49 | highlight viewer, no-poster slide | 1440 x 900 | The band is still **W x 9/16** (not a short 120-DIP strip) and tinted; stepping between two no-poster slides produces **no brightness flash**. | frame recording, sample the band's colour per frame |
| 50 | highlight viewer over the after-update dialog, "Try it" | 1440 x 900 | The shell navigates **first**, then the dialog closes, then the viewer closes — focus never lands nowhere. | frame recording |
| 51 | after-update dialog (armed) | 1440 x 900 | Plate **720** wide, `MaxHeight 620`; the three-up row is 216-wide cards with 122-tall bands and a **505**-DIP plate; the footer is `FillLayerAlt` with a divider border, 62 tall. | static capture + measure |
| 52 | after-update dialog | 1440 x 900 | Hero: `Updated` accent pill + `0.2.8 → 0.2.9.10` mono pill, 26/600 welcome line, 14 tagline at MaxWidth 560. | static capture |
| 53 | after-update dialog | 1440 x 900 | Ticking "Don't show this after updates" writes `app.whatsnew.autoShow = 0`; `Full release notes` closes the plate and navigates to `whatsnew`; `Got it` just closes. Relaunching with the key armed does **not** re-show it (one shot). | live + registry check |
| 54 | after-update dialog while the wizard is pending | 1440 x 900 | The plate does **not** appear over the wizard; it appears in the **same launch** as soon as the wizard closes. | live, watch the frame after the wizard closes |
| 55 | report dialog (`wavee://open?route=report&arg=bug`) | 1440 x 900 | Card width **548**, content **500**; subtitle, a 4-item `Segmented`, Title box, three 72-DIP multi-line boxes, a 260-wide Area combo defaulting to **"Not sure"**, the include-logs checkbox (checked, "last 200 log lines"). | static capture + measure |
| 56 | report dialog | 1440 x 900 | The preview box is **500 x 220**, bordered, `Radii.Control`, mono 11, scrollable, and it **updates as you type** in any field. | frame recording while typing |
| 57 | report dialog | 1440 x 900 | Switching the `Segmented` kind **rebuilds the form** (fields change; typed text in the previous kind's fields is gone) and the field set matches §W24's table for each kind. | live, four captures |
| 58 | report dialog | 1440 x 900 | Pressing the primary with an empty Title raises a **warning toast** and the dialog **stays open**; pressing it before the compose finishes raises the "Preparing the report…" toast and stays open. | live |
| 59 | report dialog, crash mode (`arg=crash`) | 1440 x 900 | No `Segmented`; an error `InfoBar` titled "Wavee closed unexpectedly"; When/Reproduces combos; the include-logs label says **300**; a "Don't ask again after a crash" checkbox; buttons read `Report on GitHub` / `Not now`. | static capture |
| 60 | all three surfaces | 1440 x 900, dark **and** light | Every colour flips with the theme **except** the QR (white plate / black modules), the avatar tints, the viewer veil, the chrome scrims and the two literal shadows. | two static captures per surface |
| 61 | all three surfaces | 1440 x 900, Windows "Animation effects" **off** | Every slide/stagger/scale becomes an instant swap; the poster's 140 ms reveal fade still runs; the Lottie hero shows its static end pose; nothing is stuck mid-transition. | static captures + a short recording |
| 62 | all three surfaces | 1440 x 900 | Locale switch to `nl` (Settings › General): every visible string changes **except** the four URLs, the version/quad numbers, and the three documented non-loc literals of §0.12 — the report dialog's `When` / `Does it reproduce` / `Area` option lists (34 strings, verbatim English), the preview's `"… (N KB more in the copied report)"` tail, and the download card's `" MB"`. Those three MUST stay English; anything else in English is a regression. | two static captures per surface |
| 63 | Sign in / Idle | resize 826 → 804 → 803 | At **804** the browser and scan cards still lay out header-left / QR-right; at **803** the scan card's QR drops BELOW the description with an 8-DIP gap, left-aligned, and the body starts scrolling (W3b). Same flip in the compact band at 588 → 587. | slow resize recording, watch the QR's origin |
| 64 | Terms, TermsRearm run | 1440 x 900 | The header reads **"We've updated the terms"**, not "Licence agreement & terms"; the body is byte-identical to a FirstRun Terms page. | two static captures |
| 65 | Sign in / Idle, pre-auth FirstRun | 1440 x 900 | Pressing the secondary (**"Close"**) **quits Wavee** — it is `QuitApp`, exactly like Terms's "Decline". Same on Failed and Expired. Busy's "Cancel" must NOT quit (item 17). | live, four runs |
| 66 | Local playback / Untrusted, Versions | 1440 x 900 | The footer's "Back" is a MODEL step (`CancelUntrusted` / `Back`) — the page stays on Local playback. The plate's own 30×30 Back button and `Backspace` are the page walk. | live |
| 67 | Local playback / Downloading with an unknown Content-Length | 1440 x 900 | The description reads `"12.3 MB"` (no `/ total`) and the bar is **indeterminate**. | live against a feed with no Content-Length, or a static capture of that state |
| 68 | Local playback / Ready on an unsigned or unknown runtime | 1440 x 900 | Missing values print an em dash `—`, and the Signature card shows **no "View" link** when `SignatureInfo` is null. | static capture |
| 69 | `whatsnew` for a release with zero highlights | 1440 x 900 | The "HIGHLIGHTS" eyebrow AND the card row are **both absent** — no empty strip, no reserved gap (the strip is a 0-height box). | static capture |
| 70 | `whatsnew` for a release with no codename / no `minOs` / no `packageVersion` | 1440 x 900 | Headline prints bare (`Wavee 0.2.9`, no empty quotes); the meta row drops "Requires"; the pills row drops the mono quad pill. | static capture against a hand-edited `whatsnew.json` |
| 71 | `whatsnew`, the SELECTED rail row when it is also unread | 1440 x 900 | The selected row shows **no** unread dot; the running build shows `YOU` and **never** a dot; `BETA` may sit beside either. | static capture with `lastSeenVersion` set back |
| 72 | `whatsnew`, hover the STORE card | 1440 x 900 | The frame fill does **not** change on hover (only the regular cards ramp `FillCardDefault → FillCardSecondary`); the accent border does not move; pressing the band/title area scales to 0.985 while pressing the footer button does not scale the card. | hover + press recording, frame-diff |
| 73 | highlight card, an unknown `kind` string | 1440 x 900 | The pill reads **"New"** (not the title-cased raw kind). `store` → "Microsoft Store", `rebuilt` → "Rebuilt", `improved` → "Improved". | static capture against a hand-edited document |
| 74 | after-update dialog | 1440 x 900 | The mono pill reads `0.2.8 → 0.2.9.10`: the LEFT side is the previous run's quad with its fourth part **dropped**, the right side is the full running quad. | static capture, set `app.lastRunVersion = 0.2.8.4` |
| 75 | after-update dialog, a launch that also opened a crash notice | 1440 x 900 | The plate does **not** appear at all this launch, and `app.whatsnew.pendingFrom` is still set afterwards — it appears on the NEXT launch. | `--crash-probe` run, then relaunch; registry check between |
| 76 | crash prompt with `crash.promptOptOut = 1` | 1440 x 900 | No dialog: a **sticky** (never auto-dismissing) Warning toast with a "Report on GitHub" action that opens the crash dialog for that exact report file. | live, after a `--crash-probe` run |
| 77 | Settings › Diagnostics › Crash reports, with no reports on disk | 1440 x 900 | The expander shows one row of 12-DIP tertiary "no crash reports" text, not an empty expander. | static capture on a clean profile |
| 78 | Sign in / Idle | resize to **397 → 398** wide | At 398 the browser / scan / download cards still show their `(globe)` / `(cam)` / `(dl)` header glyph; at 397 (card 286) **every wizard card loses its icon** — `SettingsCard.WrapNoIconThreshold`. The header text shifts left to fill the gap. | frame recording while dragging the edge |
| 79 | Sign in / Idle, `nl` or `ko-KR` | 1440 x 900 | If the localized lead wraps to **three** lines the body overflows the 325 lane and the persistent scrollbar appears — that is correct, not a bug (`SetupLayoutTests.cs:68-70`). The lane must never clip silently. | static capture per locale |
| 80 | Sign in / Idle, narrow | 500 x 900 | The premium row **wraps** to two lines (secondary sentence, then the "Sign up" link), +32 DIP the budget does not model, and the body scrolls. | static capture |
| 81 | `whatsnew` with the network blocked (no index reachable) | 1440 x 900 | The rail is absent (0-wide), the embedded document still renders, and the hero **still shows the `[Latest]` pill** — `IsLatest` returns true on an empty index. Same on both builds. | static capture, host blocked |
| 82 | `whatsnew` for a release whose `date` is not an ISO date (e.g. `"Q3 2026"`) | 1440 x 900 | The meta row prints `Released Q3 2026` — the raw string, echoed, not blanked and not a parse error. | static capture against a hand-edited notes file |
| 83 | after-update dialog (armed) with the network blocked | 1440 x 900 | The plate opens at ~213 DIP: pills + welcome line, an **empty tagline line**, no card row, and it never changes. `app.whatsnew.pendingFrom` is cleared anyway. Confirm the 0.3 candidate's chosen §9.4 behaviour here explicitly and note it in the CHANGELOG. | live, host blocked |
| 84 | after-update dialog on a **beta** build | 1440 x 900 | The welcome line reads `Welcome to Wavee 0.2.9 "Breaker" · Beta 3` (`AppVersionDisplay.Of`), not the bare two-part form. | static capture on a beta quad |
| 85 | report dialog | 1440 x 900 | With logs included, the preview ends in `… (N KB more in the copied report)` at the 12 000-char cut — and **"Copy report" / "Save as…" produce the full, untruncated bundle**. Diff the clipboard against the preview. | live, paste into an editor |
| 86 | Local playback / Failed | 1440 x 900 | The InfoBar title is `Local playback needs a one-time setup`; with no `model.Error` the body reads `Playback support isn't available for your Spotify version yet.` | static capture, force a failure |
| 87 | first launch on a re-armed install (setup pending) that ALSO has the sidebar chooser pending | 1440 x 900 | The sidebar design chooser does **not** appear this launch, even after the wizard closes — unlike the after-update plate and the crash prompt, it is gated once at mount (`DepKey.Empty`) and waits for the NEXT launch. Both builds must behave identically. | live, two launches |
| 88 | `--qr-dump` (§6.6) | **headless — no window** | `Wavee.exe --qr-dump "https://spotify.com/pair?code=WZY5Q6TX" out.png` on each build: exit **0**, a **518 x 518** PNG (v3 = 29 modules → (29+8) x 14) that a phone scans back to the exact URL, and the two files **byte-identical**. This proves the **bitstream** only — 14 px/module is 7x the 2-DIP cell the app paints, so item 11 still owns the raster and neither check substitutes for the other. | run both from a real terminal (the ASCII arm needs an attached console), then `certutil -hashfile out.png SHA256` |
| 89 | `--qr-dump` with unencodable text | **headless** | Pass a string too long for v1-10 (e.g. 1 000 chars): the probe logs `QR encode failed: …`, exits **1**, and writes **no file**. The in-app path deliberately does something else — `QrGrid`'s fallback plate (white, `Radii.Card`, `Icons.MusicNote` 28 in `#1DB954`) — and a 0.3 change to either must not assume the other mirrors it. | run both, check the exit code and that `out.png` was not created |
| 90 | the `--qr-dump` PNG's ink and quiet zone | **headless** | The file contains **only** gray 0 and gray 255 (no anti-aliasing, no third value) and its border is exactly **4 modules = 56 px** of gray 255 on every edge — the same ink and quiet zone `QrGrid` paints as `#000000` / `#FFFFFF` with `quiet = 4` (§3.5, §4.2). The ASCII block's margin stays **2** and is not a scan target. | open both PNGs in an image editor: sample a dark and a light module, measure the border |

---

*Written 2026-09-12 against 0.2.9 HEAD `b3f6647a`. Where a design doc under `docs/plans/wavee/` disagrees with the code,
the code won and the drift is noted inline: `onboarding-v3-implementation.md` §A still describes a three-step wizard
with a stage/decision split (superseded by its own §v3.1/v3.2); its §v3.2 quotes `SignInIdleBodyHeight(2) = 312`, which
is now **314** because the budget charges the painted 82-DIP QR plate instead of the requested 80
(`SetupLayout.cs:44-48`). `whatsnew-highlight-viewer-design.md` specifies an opaque `Tok.FillSolidTertiary` card with a
painted gradient, a stock `PipsPager`, `WaveeMotion.ScaleEmphatic` chrome, a mirrored directional exit and a flat
120-DIP no-poster band — the shipped code uses a translucent `FillCardDefault` card with `BoxEl.EdgeFade`, a hand-rolled
capsule pager, `ScaleStandard`, a direction-free exit and `W x 9/16` always (issue #89 L1-L4 and
`whatsnew-highlight-viewer-implementation.md` §0). `issue-reporting-implementation.md` §3 shows a 160-DIP preview box;
the code is **220**.*

---

## 11. Audit log

Adversarial re-read against 0.2.9 HEAD `b3f6647a`, 2026-09-12. Every entry was re-derived from source, not from the
design docs. Sections 2, 3 and 5's arithmetic was recomputed end-to-end and **all of it held** —
`SignInIdleBodyHeight(2) = 314`, `BodyLaneHeight(490) = 325`, `QrPlate.PlateFor(80, 33) = 82`,
`FooterButtonWidth` at 762/706/636, the Ready primary's 498, every `HighlightCardMetrics.DialogHeight`
(486 / 505 / 525 / 568 / 583 / 588 / 603), every `HighlightViewerLayout.PlateWidth`
(960 / 604 / 427 / 320 / 224) and band (540 / 340 / 240 / 180 / 126), `Expressive.Fast = 250`,
`WaveeMotion.{Faster 83, Standard 250, StaggerMs 40}`, `ScaleStandard = (1.04, 0.96)`,
`Radii.{Control 4, Card 8, Overlay 8, Pill 16, Full 999}`, `Spacing.{XS 4, S 8, M 12, L 16, PageWide 36}`,
`ContentDialog MaxW 548 / MaxH 756`, `SettingsCard MinHeight 68 / Padding 16 / HeaderIcon 20`, and the 24-option
`Areas` list. Line citations spot-checked across ~20 files: accurate.

| # | kind | section | correction |
|---|---|---|---|
| 1 | **wrong** | W22, W23 | The after-update mono pill read `0.2.8.4 → 0.2.9.10`. The left side is `AppUpdateVersion.ReleaseTagVersion(fromQuad)` — the first **three** parts only (`AppUpdateVersion.cs:48-53`, `AfterUpdateDialog.cs:101`). Corrected to `0.2.8 → 0.2.9.10`, which is what §1.3 and parity item 52 already said; the two wireframes contradicted the rest of the chapter. |
| 2 | **wrong** | §3.3, §5, W26 | "card frame … hover `FillCardSecondary` … `Interaction.Card`" was stated for the card generally. The **store** card's frame is a plain `frame with { Children = … }` and never takes `.Interactive(Interaction.Card)` (`HighlightCard.cs:215-237`): no hover fill ramp at all, and its 0.985 press is authored on the hit-region sibling via `WhilePressed` + `MotionTok.StandardSpring`. Split into two rows in §3.3, two blocks in W26, and two clauses in §5. |
| 3 | **overclaim** | §3.1 | Three table rows described parts nothing mounts. `LoginCountdown` (both the compact and pill forms) and `LoginView.CodeFont` are **dead in 0.2.9** — grepped app-wide, referenced only by their own file's comments. The live scan card formats `mm:ss` into the `SettingsCard` description at the stock 12, not monospace. Rows marked DEAD and a real `scan-card description` row added. |
| 4 | **overclaim** | §5 | The "waiting dots" row said "unused by the wizard today"; `WaitingDots` is unused by **anything**. Marked DEAD, plus its gap 5 / `AccentDefault` fill which were missing. |
| 5 | **overclaim** | §9.3.2 | "**seven** test files under `Wavee.Tests/Feedback/`" — there are **nine**; all nine now named. |
| 6 | **missing** | §0.4, new W3b, parity 63 | The biggest gap in the chapter. `SettingsCard` wraps its `Content` slot below the header (+8 DIP, left-justified) at `width < WrapThreshold = 476` (`SettingsCard.cs:31, 129, 135-138, 267-278`). In the wizard that is viewport **770–803** (icon column on a shrunken plate → card 442–475) and viewport **< 588** (compact → card < 476). In that band the scan card grows ~114 → ~158 and the Idle body runs ~358 against a 325 lane, so §0.4's "no scrolling" holds only at viewport ≥ 804 / 588–769. `SignInIdleBodyHeight` does not model it. |
| 7 | **missing** | §1.1 (Terms), parity 64 | A `TermsRearm` run swaps the page header to `setup.terms.updatedTitle` = "We've updated the terms" (`SetupPage.Terms.cs:24-25`). Nothing in the chapter said the header ever changed on Terms. |
| 8 | **missing** | §1.1 (LocalPlayback), W10, W11 | `HeaderFor(phase)` swaps the page header per phase: Untrusted → `runtime.signatureInvalid`, Ready → `runtime.ready`, Advanced → `runtime.chooseVersion`, else `setup.localPlayback.header` (`:67-73`). Only `LeadFor` was documented. |
| 9 | **missing** | §1.1, W9, parity 67 | `DownloadBytesText` falls back to `"12.3 MB"` (no `/ total`) when `Total <= 0`, and the card header falls back to `runtime.downloading` when `DownloadLabel` is null (`:128, 140-142`). |
| 10 | **missing** | §1.1, W10, W11, parity 68 | Every unknown runtime fact prints an em dash `—`; the Ready "Signature" card's "View" link exists **only** when `status.SignatureInfo is not null`; the up-to-date line only when `UpToDate` (`:149-150, 163-171`). |
| 11 | **missing** | W18, parity 73 | The four kind-pill labels were never listed: `store` → "Microsoft Store", `"rebuilt"` → "Rebuilt", `"improved"` → "Improved", **everything else including an unknown kind** → "New" (`HighlightCard.cs:143-148`). Noted that `HighlightVisibility`'s own comment claims unknown kinds are title-cased — the code disagrees; the code wins. |
| 12 | **missing** | §1.2, W14, parity 69 | A release with **no highlights** renders a 0-height, hit-invisible box — the eyebrow and the whole row disappear (`HighlightStrip.cs:23-24`). |
| 13 | **missing** | §1.2, W14, parity 70 | Hero conditionals: the `Latest` pill only when latest, the mono quad pill only when `PackageVersion` is set, `Released` only when `Date` parses, `Requires` only when `MinOs` is set; a codename-less release prints `whatsNew.headlineBare` (`ReleaseNotesHero.cs:24-34, 76-79`). Also the "as of" line is conditional on `GeneratedAt`. |
| 14 | **missing** | §1.2, §3.2, parity 71 | Rail markers are mutually exclusive and selection-aware: `isYou ? [YOU] : unread ? [•] : nothing`, and `unread` is forced false on the selected row (`ReleaseRail.cs:65, 71-73`). `BETA` is independent. Rows are `Role = NavigationItem`, `Focusable`. |
| 15 | **missing** | §6.1 (new secondary table), parity 65, 66 | The command table gave labels but never verbs. Three are surprising: SignIn / **Idle, Failed, Expired** "Close" is `QuitApp` (`SetupSession.cs:286-295`); LocalPlayback's "Back" is `model.Back()` / `model.CancelUntrusted()`, **not** a page walk (`:313-314`); "Not now" on Offer/Failed does `DismissSetting` + `DeclineRuntime` + `MarkCompleted` + close (`:306-312`). The plate's own Back button is the only page walk. |
| 16 | **missing** | §6.1 | Entry point → start page: FirstRun → Terms, **Reauth → `SetupPage.SignIn`** (`WaveeApp.cs:366-369`), TermsRearm → Terms via `SetupChrome`. Also: `PrimaryKind` is `Accent` on every row but `SetupButtonKind.Standard` is live in the footer switch, and `BlocksDismiss` is now read by nothing. |
| 17 | **missing** | §1.4, §6.4, parity 76 | The post-crash prompt's **Toast** arm (the opt-out path) was undocumented: `Warning`, `DurationMs = 0` (sticky), `ActionLabel = report.reportOnGithub`, `DedupeKey = "crash.pendingReport"`, `OnAction` re-enters via `ReportRequests.Open` (`ReportChrome.cs:53-64`). Also its wizard deferral at `:50`. |
| 18 | **missing** | §1.4, parity 77 | `CrashReportsCard`'s anatomy and its **empty state** (one 12-DIP tertiary row, `Key "crash-reports:empty"`) — the file is in this chapter's assigned sources but appeared only as an entry-point citation. |
| 19 | **missing** | new §6.5 | There was **no interaction section for the after-update dialog at all**. Added: Modal dismiss + no `ClosingAction` veto (so Escape closes), the checkbox writing `autoShow` immediately, and "Full release notes" doing `close()` **then** `nav(...)` — the reverse of the viewer's "Try it" order that §6.3 and §9.1 make a point of. |
| 20 | **missing** | §7, parity 75 | The after-update readiness row named two settings keys. There are **four** gates, all leaving the key armed: no pending version, `autoShow` off, wizard pending / session live, and `CrashNoticeThisLaunch` (`AfterUpdateDialog.cs:198-201`). |
| 21 | **missing** | §8 | Two rows absent. `PlaybackRuntimeSetupModel.Phase` is already a split-out engine-free partial `<Compile Include>`d by `Wavee.Tests` (`Wavee.Tests.csproj:301`) and is the enum this surface's page, lead, header and facet fold all switch on — it must land once, in `Platform.cs` with DATA GAP D3's `RuntimePhase`, not twice. `ReleaseNotesStore` is engine-free by construction and source-included (`:589`) with `ReleaseNotesStoreTests` driving it over `ScriptedHttpHandler`; `ReleaseNotes.Host.cs` has to keep that property. |
| 22 | **missing** | §6.3 | The watch pill was absent from the tab order. It sits between the two chevrons in the DOM (`:268-270`) and, unlike the chrome circles, never sets `Focusable` (`:316-329`) — flagged as something to verify rather than assumed. |
| 23 | **missing** | §9.2 | Four keys absent: the inner `"setup:page:terms"` / `"…:sign-in"` / `"…:local-playback"` (distinct from the numeric KeepAlive slot key, `SetupPage.Placeholders.cs:30-32`), `"crash-reports:empty"`, `"login-step-mark:<state>"`, and the note that `"runtime:<phase>"` carries the MODEL phase name (`runtime:FetchingCatalog`), not the footer facet. |
| 24 | **missing** | §1.1 | The `model is null` LocalPlayback body renders with no `Key`, no Enter/Exit and no fine print (`:39-45`) — §7 named the state, the anatomy did not. |
| 25 | **missing** | §6.4 | The three combos' option counts and defaults (When 7 / index 0, Reproduces 3 / index 0, Area 24 / **last**), and that the "Don't ask again" checkbox is seeded from the live `CrashPromptOptOut` setting rather than always unticked. |
| 26 | **missing** | §5 | Every `SettingsCard` in the wizard cross-fades its own fill/border at `Style.BrushTransitionMs = 83` (`SettingsCard.cs:70`) — W26 said "the engine control's own ramp" without the number. |

**Not changed, deliberately.** The `ReducedMotion = SnapEnd` claim in §4.1/§5 is correct but *implicit*:
`LottieOptions.RiseSetup` never sets the property and `ReducedMotionPolicy.SnapEnd` is the enum's zero
(`MotionTok.cs:21`). A 0.3 port that reorders that enum silently changes the hero's reduced-motion behaviour — left as
written because the observable behaviour is what the chapter promises, but worth an explicit `ReducedMotion = SnapEnd`
in the new `Setup.LottieOptions`.

**Residual risks the chapter still cannot settle from source.** (a) Whether the watch pill is keyboard-reachable
(item 22) needs the running app. (b) Whether the viewer plate's `Shadow(90/40)` is clipped near a window edge — the
code comment itself asks for a live check (`HighlightViewer.cs:187-193`). (c) The W23 "~213 DIP" pre-load plate height
is an estimate; `HeroHeight(1) + FooterHeight = 194` by the metrics class, and the difference is whatever the empty
tagline's line box actually measures — capture it rather than trusting either number.

### Second adversarial pass — 2026-09-12, independent re-read against the same HEAD

Sections 2, 3 and 5 were recomputed a second time from source and **every number held again**, including the ones the
first pass did not reach: `Expressive.Fast = 250` / `DistBase = 8` (`fluent-gpu/.../Dsl/Expressive.cs:17, 25`),
`MotionTok.{StandardEnter 300/FluentDecelerate, ControlNormal 250/FluentStandard, ControlFaster 83/FluentStandard,
StandardSpring response .35 damping .85}` (`Animation/MotionTok.cs:164-173`), `WaveeMotion.ScaleStandard = (1.04, 0.96)`
(`Design/WaveeMotion.cs:43`), `Interaction.Card` (`FillCardDefault -> FillCardSecondary`, flat `StrokeCardDefault`,
`PressScale 0.985`, `StandardSpring` — `Interaction.cs:146-153`), `SettingsCard.{MinHeight 68, Padding 16,
HeaderIconMaxSize 20, VerticalHeaderContentSpacing 8, WrapThreshold 476, DescriptionFontSize 12, BrushTransitionMs 83}`,
every `HighlightCardMetrics` height (486 / 505 / 525 / 568 / 583 / 588 / 603, and `TextBlockHeight` 132 / 150 / 152 / 170),
every `HighlightViewerLayout.PlateWidth`/`ImageHeight` pair, `SetupLayout`'s whole ladder (314 / 325 / 82 / 288 / 351 /
218 / 498), and the four `Wavee.Tests.csproj` include lines section 8 cites (`:106` `QrPlate`, `:301`
`PlaybackRuntimeSetupModel.Phase`, `:302` `SetupLayout`, `:589` `ReleaseNotesStore`). All nine `Wavee.Tests/Feedback/*`
and all eleven `Wavee.Tests/ReleaseNotes/*` files exist as named.

| # | kind | section | correction |
|---|---|---|---|
| 27 | **missing** | §0.4, W3b, §9.2, parity 78 | **`SettingsCard` has a SECOND responsive threshold.** Below `WrapNoIconThreshold = 286` the card drops its **header icon** outright (`SettingsCard.cs:32, 130`). Compact card = plate − 48 and the plate floors at 320, so at viewport < 398 every wizard card loses its Globe / Camera / Download glyph. The chapter documented only the 476 wrap. |
| 28 | **missing** | W3b, parity 79, 80 | Two more un-modelled overflows of the 325-DIP lane: the premium row's own `Wrap = true` (+32 when it folds, `SetupPage.SignIn.cs:108-116`) and a **three-line localized lead**, which `SetupLayoutTests.cs:68-70` pins as deliberately overflowing. §0.4 named only the scan card's wrap. |
| 29 | **wrong** | W14, parity 82, §9.2 | "`Released` only when `doc.Date` **parses**" is wrong. `ReleaseNotesText.Date` **echoes an unparseable string verbatim** and returns empty only for null/whitespace (`ReleaseNotesLinks.cs:58-64`), so `Released Q3 2026` renders. The same `is { Length: > 0 }` guard governs the stacked-release divider and the rail subtitle. |
| 30 | **missing** | §1.2, W14, parity 81 | `IsLatest` returns a bare `true` when the index is null **or empty** (`ReleaseNotesPage.cs:169-174`), so every offline load shows the `[Latest]` pill beside a hidden rail. The chapter said "only when this IS the index's newest release". |
| 31 | **missing** | W23, §7, §9.4, parity 83 | The after-update plate's "still loading" frame is **also its permanent failure frame**: `LoadAsync` returns without publishing on `store is null`, a null document, or any exception (`AfterUpdateDialog.cs:149, 153, 167-168`). An offline first-launch-after-update sits on pills + welcome + an empty tagline line forever, with `pendingFrom` already cleared. §9.4's option (a) must state what it does when the load resolves to nothing. |
| 32 | **overclaim** | §0.12, §6.4, W24, parity 62 | "No literal user-facing string" has **three** more exceptions than the four URLs: the report dialog's `When` (7) / `Reproduces` (3) / `Areas` (24) option lists are verbatim English pinned to the issue-form YAML (`ReportKinds.cs:38-48`); `ReportBundle.Preview`'s truncation tail; and `DownloadBytesText`'s `" MB"`. Parity item 62 demanded they all change under `nl` — it now demands the opposite. |
| 33 | **overclaim** | §0.13, §3.4, W24, parity 85 | The preview is not "exactly what leaves the machine": `ReportBundle.Preview` cuts at `PreviewChars = 12 000` and appends a tail (`ReportBundle.cs:24, 188-190`). Copy / Save as… emit the full bundle; only the box is cut. |
| 34 | **missing** | §0.14, §1.1, parity 87 | There is a **fourth** one-shot modal in the same `shellWithOverlays` ZStack, mounted immediately BEFORE `SetupChrome`: `SidebarOnboardingChrome` (`WaveeShell.cs:1482`). It reads the same `SetupGating.IsPending` gate but from a `DepKey.Empty` effect (`SidebarOnboardingChrome.cs:35-51`), so — unlike the three this chapter owns — it does **not** re-evaluate on `MarkerEpoch` and defers to the NEXT launch. The asymmetry is deliberate and must survive the port. |
| 35 | **wrong** | §6.1 | Three `SetupSession.cs` citations in the secondary-verb table were off by ~25 lines: Terms/`QuitApp` is `:269-272` (not `:295-303`), Terms/TermsRearm/`RequestClose` is `:271` (not `:300-302`), `Back()` is `:323` (not `:319-321`), and the table's header range is `:252-323` (not `:249-318`). |
| 36 | **missing** | §1.1, W22, §8, parity 84 | The after-update welcome line is `whatsNew.dialog.welcome(AppVersionDisplay.Of(me))`, which has **three** shapes — bare, with a codename, and codename + `· Beta n` — and prints `me.SemVer` rather than `me.Core` on a dev build (`AppVersion.cs:95-103`). W22 showed only the codename form. Added an `AppVersionDisplay` row to section 8 and flagged that it has **no test**. |
| 37 | **missing** | §1.1, W11, parity 86 | Local playback / **Failed**: the InfoBar's title is `playback.runtime.missing` = "Local playback needs a one-time setup" (the wireframe had invented "Local playback component missing"), and the body falls back to `playback.runtime.noPack` = "Playback support isn't available for your Spotify version yet." when `model.Error` is null (`:176-179`). |
| 38 | **missing** | §1.1, W5 | Sign in / **Busy**: the InfoBar's MESSAGE switches — `RequestingCode`/`LoggedOut` -> `auth.gettingCode`, everything else -> `auth.waitingApproval` (`SetupPage.SignIn.cs:157-158`). The chapter wrote "msg" and the wireframe hard-coded one of the two. |
| 39 | **missing** | §1.1, §7, D1 | The scan card's QR encodes `Challenge.VerificationUriComplete ?? Challenge.VerificationUri` (`:135`) — **two** fields. D1's proposed `PairingChallenge` record carried only one, which would silently drop the fallback. |
| 40 | **missing** | §1.2 | `HighlightStrip`'s key falls back to the **loop index** when `Highlight.Id` is empty (`HighlightStrip.cs:33`), and `HighlightViewer.SlideId` mirrors that fallback (`:517-521`) — §9.2's ReuseGuard note assumed an id always exists. Also added the rail subtitle's four shapes (`name · date` / `name` / `date` / empty, `ReleaseRail.cs:105-111`). |
| 41 | **missing** | §1.1 | Terms and SignIn pass `backAutoPadding: false` **explicitly** (`SetupPage.Terms.cs:43`, `SetupPage.SignIn.cs:86`); only LocalPlayback takes `Frame`'s `true` default. The chapter attributed the missing spacer to `ShowsBack` alone — true, but not the whole mechanism a port has to reproduce. |
| 42 | **missing** | §3.1, §3.2, §3.4 | Five token rows were thin or wrong: the premium row has **no authored Height** (32 is `SetupLayout.LinkRowHeight`, the budget's model, and it doubles on wrap); the `Card` row never named the two `SettingsCard` thresholds or its 83-ms brush; §3.2 was missing the section column's own `gap 6` (`ChangelogSection.cs:71`), the count's `Grow 1` (what actually pushes "Show all" right), the changelog row's `AlignItems Start` and its inner column's `gap 6`; §3.4's preview-box row said nothing about the 12 000-char cut or the `report.preparing` placeholder. |

**Still unsettled after two passes.** (a) the watch pill's keyboard reachability (first pass, item 22) — unchanged,
needs the running app; (b) the viewer plate's shadow near a window edge — unchanged; (c) the W23 "~213 DIP" estimate —
unchanged, and now doubly worth capturing because it is the offline **steady** state, not just a transient;
(d) `Interaction.Card`'s **disabled** stroke is unreachable today (no highlight card is ever disabled), so the flat
stroke is untested by observation; (e) whether any shipped locale actually wraps `setup.signIn.lead` to three lines at
the reference plate (item 28) — `nl` and `ko-KR` are in the tree but the answer needs the shaper, not the JSON.

**token-reconcile (2026-09-12):** `Tok.FillLayerAlt`, named here but missing from the first build of `00-design-system.md §12.1`, is now indexed there (`#FFFFFF` OPAQUE light / `#FFFFFF0D` dark — unlike `FillLayerDefault`'s translucent `#FFFFFF80`). No value in this chapter changed.

**coverage-sweep merge (2026-09-12):** `90-coverage-gaps-1.md` (sweep 1, filed for this chapter) was folded in here and tombstoned — the number is not a lost chapter. It covered exactly one 0.2.9 file no chapter draft had opened, `Features/Auth/QrDump.cs` (93), the `--qr-dump` encoder/renderer bisector, which arrives as **§1.5** (its anatomy and CLI call path; the old §1.5 "the 0.3 tree" is now **§1.6**), **W27** (the two artefacts to scale), **§3.5** (six raster constants and what each must match in `QrGrid`/`QrPlate`), a sentence in **§4.2** (the QR ink is declared in two places), one line in **§5** (no motion — stated so the absence is not read as an omission), **§6.6** (the CLI contract: two optional positional args, the `--`-swallowing parse, and the exit code as the whole result), a paragraph in **§7** (the encoder must stay engine-free or the probe and `QrTests` die together), a **§8** row, a **§9.2** trap (two quiet zones in one file), corrections to **§9.3.1** and **§9.5** (`Features/Auth` is five files, and its 775 lines were only 682 accounted for — the missing 93 are a CLI arm and must be budgeted with the probes, not with `Screens/Setup.cs`), and **parity 88-90**. Every citation in the memo was re-checked against HEAD `b3f6647a` and held — `Qr.Ecc.M` at `:18` against `QrGrid.cs:24`, quiet 4 / scale 14 at `:32`, the 2-module ASCII margin at `:24`, gray 0/255 at `:37`, column-major `m[x, y]`, the defaults and the `--` guard at `Program.cs:235-236`, `ProbeFlags` at `CliRun.cs:15`, and `PlateFor(80, 33) = 82` — with three sharpenings the code forced: the source-inclusion of `Qr.cs` is pinned by `Wavee.Tests.csproj:103` (`Qr.cs:9` only asserts it), `QrDump` is BCL **plus `WaveeLogger`** rather than BCL alone, and the memo's proposed PNG round-trip test cannot be written against 0.2.9 as it stands (`WritePng` is private and takes a path, `Run` takes a logger) — 0.3 has to expose an engine-free `WritePng(Stream, …)` seam first. The memo's fifth block, a budget gap, was filed as a §9.5 correction rather than a parity item: it is an accounting error in this chapter, not something a side-by-side can observe.

**arbitration 2026-09-12:** this chapter releases two claims it should never have held. **A8** — `AppUpdateToasts`
(153) is **not** `Screens/ReleaseNotes.cs` CORE; it is `Platform/Notify.cs` CORE with the rest of the one
notification stack (`Platform/Notify.cs` + `Platform/Notify.Host.cs`, owner **I**, **Wave 4** — A9/A11, chs. 14 and
19), because the same `(previous, next) -> ToastPlan` table feeds the OS banner, the in-app update card and Settings
› About’s `StateSentence`. §8, §9.3.6, §9.3.7, §9.5 and §9.6 were rewritten to it and the arithmetic followed:
`ReleaseNotes.cs` CORE 2 100 -> 1 947 absorbed / ~2 000 -> ~1 850 honest, the ~800 “also wants to be there” pile ->
~650, the CORE-file warning 2 050 -> 1 900 lines, and the chapter totals ≈ 8 800 -> ≈ 8 650 absorbed / ~8 100 ->
~7 950 honest. **A10** — `WaveeTips` + `WaveeTipsCore` go to `Shell/Shell.cs` CORE + `Shell/Shell.Host.cs` SHELL
(ch. 19), and the “`Platform/Design.cs` or `Shell/Shell.cs`” hedge in §8 is deleted: verified against 0.2.9, the one
shipped tip id is raised from `Features/Detail/DetailTracks.cs:3801,3806,3864` and no surface in this chapter shows
a tip at all. Nothing this chapter renders, measures or animates changed.

**answers 2026-09-12: Q8 and Q9 (plan §9.6) both land in this chapter.** Q8 — this chapter's own §9.6 item 3 ("a
session home is unstated") is resolved: `SetupSession`/`SetupSession.MarkerEpoch` folds into `Screens/Setup.cs`
(orchestrator's call), which grows 950 → 1,150; the §9.6 missing-files list is updated. Q9 — A18 is CONFIRMED: the
runtime provisioning card's UI body is `+Screens/Setup.UI.Runtime.cs`, written by owner I in Wave 4, mounted a
second time (not re-implemented) by this chapter's LocalPlayback page in Wave 6; DATA GAP D3 is updated to point at
it rather than say "nothing".
