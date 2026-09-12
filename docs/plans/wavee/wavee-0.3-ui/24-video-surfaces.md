# Video surfaces (docked, in-window PiP, pop-out, fullscreen, placement) — 0.3 visual fidelity contract

> **0.2.9 sources** (all paths relative to `src/apps/Wavee/`; after Wave 0 the same relative paths under `src/apps/_old/Wavee/`):
> `Features/Video/DockedVideoSurface.cs` (521) · `Features/Video/InWindowVideoPip.cs` (538) ·
> `Features/Video/PopOutVideoWindow.cs` (259) · `Features/Video/VideoFullscreenSurface.cs` (336) ·
> `Features/Video/VideoPlacementHost.cs` (203) · `Features/Video/VideoPlacementMenu.cs` (77) ·
> `Features/Shell/VideoOverrideManagerFlyout.cs` (295) — **2,229 UI lines**.
> Pure rules: `App/PlacementCore.cs` (543) · `App/DockedVideoHosting.cs` (168) · `App/VideoOverrideUx.cs` (381) ·
> `App/RailVideoCoupling.cs` (83) · `App/VideoUpgradeGate.cs` (76) · `App/VideoAspectPersistence.cs` (35) ·
> `App/VideoStageInput.cs` (30) · `App/DetachedFullscreenRule.cs` (31) · `App/VideoSurfaceMount.cs` (13) — **1,360 rule lines**.
> Mount sites read but owned elsewhere: `Features/Player/RightRail.cs:170-340`, `Features/Modules/WatchPageView.cs:74-164`,
> `Features/Shell/WaveeShell.cs:690-830,1455-1500`, `Features/Shell/PlayerBar.cs:500-600`,
> `Features/Shell/ContentHost.cs:35-50`, `Features/Player/VideoRailPanel.cs:104-135`,
> `Features/Shell/SettingsPage.VideoOverrides.cs:170-330`, `Features/Shell/ShellResponsiveLayout.cs:126-162`.
>
> **0.3 target — settled by arbitration 2026-09-12 (A13).** §2's tree had only `Playback/Playback.Video.cs` —
> *"SHELL: native video host + load pump"*, 1,100 lines — which is the **host** (`FluentVideoMediaHost` +
> `VideoLoadPump`), not one pixel of the UI, and no file anywhere in §2 could hold four video surfaces, the placement
> menu, the placement state machine or the override manager. This chapter's proposal was taken whole and is now in the
> tree with an owner and a wave:
> - `Shell/Video.cs` — **CORE** (~900): `PlacementCore`, `DockedVideoHosting`, `RailVideoCoupling`, `VideoUpgradeGate`,
>   `VideoStageInput`, `DetachedFullscreenRule`, `VideoSurfaceMount`, `VideoAspectPersistence`, `PlacementPersistence`,
>   `VideoOverrideUx`. All nine are already `System`-only; they port **verbatim**.
> - `Shell/Video.UI.cs` — **UI** (**1,450**, plan §2/§9 — this chapter's own estimate was ~1,700, folding in the
>   override-manager flyout body): the docked cap surface, in-window PiP (8 resize zones), the fullscreen surface,
>   the watch stage, the placement menu. The override-manager flyout **body** is its own named partial,
>   `Screens/+Settings.UI.Video.cs` (280) — written by owner K in Wave 4, mounted by owner R's Playback tab in
>   Wave 6 — not part of `Video.UI.cs`.
> - `Shell/Video.Host.cs` — **SHELL** (~250): the detached-window owner (`VideoPlacementHost`) + `VideoWindowPrefs`.
>
> All three are **Wave 4 owner K** — K already owns `Rail.UI.cs` (which mounts the docked cap) and `Lyrics.UI.cs`
> (whose immersive surface is the structural twin of `VideoFullscreenSurface`, sharing the full-bleed-layer,
> focus-scope and Enter/Exit-terminal idioms). Splitting the fullscreen surface from the immersive lyrics surface
> across two owners is how the two drift.
> **`Playback/Playback.Video.cs` stays the decode/host only** — `FluentVideoMediaHost` + `VideoLoadPump`, **Wave 3
> owner H**, unchanged. And **the right rail's video panel body stays in `Rail.UI.cs`** (owner K, chapter 21): this
> chapter bills it as a mount site, not as a second copy of the surface.

Cross-references (do not re-specify here): tokens, type ramp, easing curves and the on-media ink ladder →
`00-design-system.md`. The global player bar that keeps rendering under the docked card and the PiP →
`20-player-bar.md`. The right rail that hosts the docked cap, its splitter and the `RailMode.Video` body →
`21-right-rail-npv-queue-stage.md`. The immersive lyrics surface whose layer pattern the fullscreen surface mirrors →
`22-lyrics.md`. The shell ZStack layer order and the chrome unmount under fullscreen → `18-shell-frame.md`. The watch
page that hosts the `PageStage` face → `09-show-episode-module.md`. Settings → Playback → Video overrides (the card
the manager flyout hangs off) → `27-settings-and-diagnostics.md`.

---

## 0. The non-negotiables

1. **No `LayoutTransition`, no `Opacity`, no `OpacityGroup`/blur/edge-fade on any ancestor of a video hole — ever.**
   `DrawOp.DrawVideo` is a `DestOut` erase against the real back buffer. An ancestor opacity channel multiplies into
   `DrawVideoCmd.Opacity` (washed-out, see-through video with the page bleeding through); an ancestor that pushes an
   offscreen RT makes the punch never reach the back buffer and **the hole vanishes entirely, silently**
   (`DockedVideoSurface.cs:70-80`, `VideoFullscreenSurface.cs:40-49`, `WatchPageView.cs:31-46`). This is why the docked
   card carries no transition, why the fullscreen terminals are **scale-only**, and why the watch stage sits outside
   `ModulePage.Section()`, outside `Skel.Region` and outside the page `ScrollView`.
2. **The docked card is the content's own shape, not a letterbox box.** Its height at rest is
   `ShellResponsiveLayout.FitDockedVideoHeight(railW, naturalW, naturalH)` (`ShellResponsiveLayout.cs:158-162`) — a 16:9
   stream fills a 340-wide rail at 191.25 DIP, a 4:3 one at 255, a 2.35:1 one at 143.44. Black bars above and below a
   docked music video are a regression, not a default.
3. **Nothing ever restarts playback.** No surface builds a `MediaPlayer`; every surface binds
   `PlaybackBridge.VideoPlayer` (`DockedVideoSurface.cs:55-57`, `InWindowVideoPip.cs:23-25`,
   `PopOutVideoWindow.cs:32-34`, `VideoFullscreenSurface.cs:34-38`). The stage key is **player identity only**
   (`"gen:" + binding.Generation`) so a video→video skip keeps the element mounted and pumping and the engine
   cross-fades the previous frame to the new source's first frame.
4. **Hover-reveal chrome costs no signal and no re-render.** Every chrome strip is `Opacity = 0, HoverOpacity = 1` with
   `HoverDurationMs = WaveeMotion.Fast (167)` and `HoverEasing = Easing.FluentDecelerate`; the card earns
   hover-container status with a no-op `OnPointerExit = static () => { }`. Three surfaces use the identical idiom
   (`DockedVideoSurface.cs:456-458`, `InWindowVideoPip.cs:274-275`, `VideoFullscreenSurface.cs:317-318`).
5. **Exactly one mounted video surface, derived from one value.** Every surface mounts iff
   `PlaybackBridge.VideoPlacementNow()` — `PlacementCore.Resolve(VideoSurface)` — equals its own placement; the two
   docked *faces* additionally go through `DockedVideoHosting.ShouldMount`. There is never a second visibility flag.
6. **Exactly one transport per window**, from one derived signal (`PlacementCore.TransportOwnerFor`,
   `PlacementCore.cs:451-457`): Docked/Floating → `TransportOwner.Docked` (the card draws its own auto-hiding overlay
   **and** the 72-DIP bar keeps rendering below it — they were never stacked), Detached → `PopOut`, Fullscreen →
   `Fullscreen` (and the shell **unmounts** the title bar and the player bar for the duration).
   `PlacementCore.SingleTransportInvariant()` asserts one claimant for every placement value.
7. **Closing is off, stickily.** Every in-app ✕ routes through `NotifyVideoSurfaceClosed(placement)` →
   `PlacementCore.HostClosed` (`PlacementCore.cs:405-412`) — never `TurnVideoOff` directly, because `HostClosed`
   carries the stale-close identity guard. Closing the **detached** window is the one exception: it falls to the mini
   player and keeps watching. The placement menu's own **"Turn off video"** row is the other, deliberate exception —
   it calls `b.TurnVideoOff` directly (`VideoPlacementMenu.cs:73`), because an explicitly chosen menu row is not a
   host close and has no stale-close race to guard against. ✕ ⇒ `NotifyVideoSurfaceClosed`; menu row ⇒ `TurnVideoOff`.
8. **A no-player state is never a black rectangle — on three of the four surfaces.** It is the current track's artwork
   at `Opacity = 0.4f` over `Tok.MediaLetterbox`, with a 20-DIP indeterminate `ProgressRing` and `player.loading`
   ("Loading…") at 12/600 (`DockedVideoSurface.cs:409-441`, `InWindowVideoPip.cs:333-343`,
   `VideoFullscreenSurface.cs:266-275`). A DRM licence round-trip takes real seconds on every track change.
   **The pop-out window is the exception and it is a 0.2.9 defect, not a design**: `PopOutVideoContent` renders
   `Array.Empty<Element>()` while no player exists (`PopOutVideoWindow.cs:149-155`), so a freshly opened pop-out is a
   bare `Tok.MediaLetterbox` rectangle for the whole manifest/DRM round-trip — the exact "reads as broken" picture the
   other three exist to avoid. **In 0.3 give it the same poster** (see W14b). Related: `PopOutVideoStage` itself
   returns a bare `BoxEl { Grow = 1 }` when its bound player vanishes (`PopOutVideoWindow.cs:204`) — that arm is
   correct (the owning surface unmounts on the same pass) and must stay.
9. **The idle→live handover is ONE cross-fade.** The watch page's idle layer is byte-identical
   `DockedVideoSurface.PosterGround` (`WatchPageView.cs:104`, `DockedVideoSurface.cs:423-426`), and the card's poster is
   `LivePosterArt` (a component that reads `CurrentTrack` inside *its own* render, so a frozen prop can never show the
   first track's art forever). The only visible transition is the element's own 150 ms `PosterCrossFade`.
10. **The PiP never covers the page while it sits at home.** Anchored ⇒ it publishes
    `FloatingSurfaceReserve = _h + 16` and `ContentHost` insets the page by exactly that
    (`InWindowVideoPip.cs:132-137`, `ContentHost.cs:45`). The first drag or resize releases the reservation — placing it
    deliberately is the user opting into a free-floating overlay.
11. **Fullscreen restores what it found.** Enter captures `hooks.IsWindowFullscreen()`; exit restores that remembered
    value, never an unconditional `false` (`VideoFullscreenSurface.cs:107-117`). Escape is handled at the fullscreen
    root — the common ancestor of everything focusable in the presentation — so no focused control can swallow the one
    guaranteed way out.
12. **The pop-out's fullscreen is its OWN window's, on its own monitor.** It toggles
    `PlaybackBridge.DetachedFullscreen` (`PopOutVideoContent.cs → PopOutVideoWindow.cs:112-115`), never
    `ShowVideoAt(Fullscreen)` — that would close the pop-out and fullscreen the *main* window on the *main* window's
    display, which is the picture visibly jumping screens off the second monitor it was dragged to.
13. **The placement ladder is reachable from the picture itself.** All four surfaces wire
    `MediaPlayerElement.MoreMenuItems = () => VideoPlacementMenu.Items(...)`, so the video can be moved from the surface
    the user is looking at, not only from the player bar across the window. Unavailable rungs are **disabled with their
    reason in the accelerator column**, never hidden (`VideoPlacementMenu.cs:43-58`).
14. **Docked is not an elevated card — but the PICTURE is never truly full-bleed.** The app's own wrapper carries no
    `Shadow`, no border and no corners: it is a content-layer rung and the rail / the page clips its own silhouette
    (`DockedVideoSurface.cs:290-296`). The PiP is the opposite: `Radii.Card (8)`, 1-DIP `Tok.StrokeCardDefault`,
    `Elevation.Flyout`. **What no surface escapes is `MediaPlayerElement`'s OWN frame.** `FrameCorners`
    (`MediaPlayerElement.cs:154-160`) is `Radii.OverlayAll (8)` for any element that is not `IsDecorative` and whose
    `CornerRadius` is 0 — which is every Wavee surface — and the frame paints a 1-DIP `Tok.StrokeFlyoutDefault`
    border (`:1057-1058`). Both tests read `IsFullscreenPresentation`, the element's **internal** own-overlay flag
    (`:247`), *not* the `PresentingFullscreen` that ORs in `IsHostFullscreen` (`:228`) — and Wavee is not an
    `InternalsVisibleTo` friend, so it can never set it. Consequence, visible in every capture: **the docked cap, the
    PiP stage, the pop-out stage and the app's own fullscreen surface all draw an 8-DIP rounded, hairline-bordered
    picture**, including the surface the chapter elsewhere calls full-bleed. Do not "fix" this app-side by inventing a
    radius — like the §6 label wart it needs a public knob on the element (engine change), and until then 0.3 must
    reproduce the frame rather than pretend it is absent.
15. **Persist where you like to work; never persist whether something is running.** `Preferred` round-trips as a *name*
    (`"docked"/"floating"/"detached"`), the PiP rect and the pop-out rect round-trip as `"x,y,w,h"`; `None` and
    `Fullscreen` deliberately do not (`PlacementCore.cs:164-217`). A pop-out is **never** persisted while borderless
    fullscreen (`VideoPlacementHost.cs:121-126`).

---

## 1. Anatomy

### 1a. 0.2.9 composition

```
WaveeShell.Render  ZStack (Features/Shell/WaveeShell.cs:1479-1503)              — layer order is load-bearing
├─ tinted (content + rail + player bar)                        WaveeShell.cs:1171
│   └─ RightRail                                               Features/Player/RightRail.cs
│       ├─ DockedCap(ui, docked, settings, railWidth)          RightRail.cs:214-263   — non-Details bodies
│       │   ├─ ZStack Height=ui.DockedVideoHeight Fill=Tok.MediaLetterbox  RightRail.cs:231-236
│       │   ├─ Embed.Comp(new DockedVideoSurface())            RightRail.cs:230      — Face = Cap (default)
│       │   └─ Splitter.Create(ui.DockedVideoHeight, Commit,   RightRail.cs:246-252  — 16 DIP, bottom edge,
│       │        Min=DockedVideoNaturalH(railW) Max=560          ShowIndicator=false)   HitTestPassThrough wrapper
│       └─ PinnedHero(ui, b, stageHosts, settings, railWidth)   RightRail.cs:288-305  — Details body: the SAME
│           ├─ docked ⇒ DockedCap(...)  (byte-identical)        RightRail.cs:296         cap, or the art tile +
│           └─ else   ⇒ ZStack[NowPlayingHeroTile, ArtVideoToggle]  RightRail.cs:299       the 2-state Art|Video toggle
├─ immersiveLyricsLayer                                        WaveeShell.cs:1437
├─ runtimeBannerLayer / fileDropLayer / palette / chromes       WaveeShell.cs:1387-1497
├─ InWindowVideoPip { Settings }                               WaveeShell.cs:1498
├─ VideoPlacementHost { Settings }                             WaveeShell.cs:1499    — controller leaf, renders empty
└─ videoFullscreenLayer = Flow.Show(VideoFullscreenActive, …)  WaveeShell.cs:1455-1473

DockedVideoSurface : Component                                 Features/Video/DockedVideoSurface.cs:100
├─ props  Face : DockedVideoFace (frozen)                      :111   — Cap (0) | PageStage (2)
│         OwnerStagePlayable : string? (frozen)                :124   — the parked-page discriminator
├─ field  _activeGate : Signal<bool> (STABLE instance)         :131   — the Activation.IsActive override
├─ effect height fit → ui.DockedVideoHeight                    :187-210  (Cap only; PageStage early-returns)
├─ effect ALWAYS-ON log `docked policy …`                      :215-225
├─ effect ALWAYS-ON log `docked host …`                        :234-246
├─ effect SetVideoSurfaceLive(Docked, mount)                   :267 + unmount disposer :270
├─ gate   DockedVideoHosting.ShouldMount(…) → empty BoxEl      :259-276
└─ card   BoxEl ZStack ClipToBounds Focusable OnKeyDown(Space) :292-311
    ├─ BuildVideoArea(b, EnterFullscreen, settings)            :341-403
    │   ├─ [player] Ctx.Provide(Activation.IsActive, _activeGate, …)   :394
    │   │     └─ Embed.Comp(MediaPlayerElement) Key="dockstage:gen:N:t0|t1"  :359-391
    │   │           PosterContent = LivePosterArt.Make()       :383
    │   │           MoreMenuItems = VideoPlacementMenu.Items(includeFullscreen:false)  :387
    │   └─ [no player] Poster(track) = ZStack[PosterGround(art) @0.4, LoadingOverlay]  :409-413
    └─ BuildChrome(b, enterFullscreen)  — pass-through positioner  :444-477
        └─ strip  H=30  Justify=End  Gap=2  Pad=(8,0,8,0)  Gradient=Tok.ScrimTop  Opacity 0→Hover 1
            ├─ Glyph(Icons.BackToWindow  E73F) → ShowVideoAt(Floating)        :461-465
            ├─ Glyph(Icons.FullScreen    E740) → ShowVideoAt(Fullscreen)      :466
            └─ Glyph(Icons.Cancel        E711) → NotifyVideoSurfaceClosed(Docked)  :467-473

LivePosterArt : Component                                      DockedVideoSurface.cs:506-521
└─ BoxEl Fill=Tok.MediaLetterbox [ PosterGround(CurrentTrack.Image) ]   Key="live-poster"

InWindowVideoPip : Component                                   Features/Video/InWindowVideoPip.cs:50
├─ prop   Settings : IAppSettings? (frozen)                    :99
├─ fields _x _y _w _h : Signal<float>; _placed : Signal<bool>; _sized : bool; _fitRatio : float  :75-86
│         _dragNode : NodeHandle; _bandNodes[8]; _vpSig; _startX/_startY/_startW/_startH/_startPx/_startPy
├─ seed   PlacementPersistence.TryLoadRect(VideoPipRect) once  :110-122
├─ effect SetVideoSurfaceLive(Floating) + FloatingSurfaceReserve  :132-137 + disposer :140
├─ effect content-aspect height fit                            :146-168
├─ gate   VideoPlacementNow() != Floating ⇒ empty BoxEl        :171
└─ layer  BoxEl Grow=1 HitTestPassThrough                      :216-220
    └─ surface  W=_w H=_h Transform=Translation(ClampX,ClampY) ZStack ClipToBounds
                Corners=Radii.Card(8) Border=1 Tok.StrokeCardDefault Shadow=Elevation.Flyout
                Fill=Transparent  Layout=SurfaceMotion  OnPointerExit=no-op          :175-212
        ├─ BuildVideoArea(b, settings)                         :298-344
        │   ├─ [player] Embed.Comp(PopOutVideoStage) Key="pipstage:gen:N"  :315-320
        │   │              Host = VideoStageHost(TransportOwner.Docked, TransportOwnerNow,
        │   │                                    () => ShowVideoAt(Fullscreen))
        │   └─ [no player] ZStack[art @0.4, LoadingOverlay(track)]   :333-343
        ├─ BuildChrome(b)  — pass-through positioner            :226-285
        │   └─ strip H=30 Pad=(14,6,14,0) Gradient=Tok.ScrimTop Corners=(8,8,0,0) Opacity 0→Hover 1
        │       ├─ dragSurface Grow=1 Cursor=SizeAll OnPointerDown/OnDrag/OnClick/OnDragCanceled  :228-237
        │       └─ close 24×24 Radii.Control → NotifyVideoSurfaceClosed(Floating)   :239-263
        └─ BuildResizeBands()  — 3-row skeleton, every non-band cell pass-through   :367-433
            row0 H=12 AlignStart : [0] NW 14×12 SizeNWSE · [1] N grow×6 SizeNS · [2] NE 14×12 SizeNESW
            row1 grow           : [3] W 6 SizeWE · (pass-through filler) · [4] E 6 SizeWE
            row2 H=12 AlignEnd  : [5] SW 14×12 SizeNESW · [6] S grow×6 SizeNS · [7] SE 14×12 SizeNWSE
                                   [7] additionally: Opacity 0→Hover 1, nub 8×8 Margin(0,0,2,2)
                                       Corners=(0,0,4,0) Fill=Tok.OnMediaTertiary HitTestVisible=false

VideoPlacementHost : Component (renders empty)                 Features/Video/VideoPlacementHost.cs:37
├─ prop   Settings (frozen)                                    :43
├─ ref    handle : IDetachedVideoWindow?                       :49
├─ effect reconcile PlacementCore.DecideOwned(resolved, Detached, alive)  :55-137
│          Open  → hooks.OpenDetachedWindow(DetachedWindowRequest(
│                    title = CurrentTrack.Title | player.nowPlaying,
│                    Size2(640,360), PopOutVideoWindow{Source,Player,Bridge,Settings},
│                    AlwaysOnTop = VideoWindowAlwaysOnTop (default true),
│                    InitialBoundsPx = restored VideoWindowRect,
│                    MinClientSizeDip = Size2(320,180)))                  :76-87
│          null  → NotifyVideoSurfaceClosed(Detached)   (the platform refused)   :90-95
│          OnClosed → identity guard, DetachedFullscreen=false, Live=false, HostClosed  :97-112
│          BoundsChanged → persist VideoWindowRect UNLESS DetachedFullscreen  :121-126
│          Close → OnClosed=null first (a state-driven close is not a user-close)  :129-136
├─ effect apply DetachedFullscreen → live.SetFullscreen(b)     :146-153
├─ effect SetTitle(CurrentTrack.Title) live                    :158-162
├─ effect VideoWindowPrefs.Epoch → live.SetTopmost(pref)       :168-173
└─ effect unmount: OnClosed=null, DetachedFullscreen=false, Close()  :178-191

PopOutVideoWindow : Component (root of the detached AppHost)   Features/Video/PopOutVideoWindow.cs:27
└─ OverlayHost.Create(Embed.Comp(PopOutVideoContent{…}))       :59-60  — MUST be a Component, never a tree
    └─ PopOutVideoContent : Component                          :90
        └─ BoxEl Direction=1 W/H = viewport, Fill = Tok.MediaLetterbox (ALWAYS opaque)  :134-156
            ├─ [live] BoxEl Grow=1 Shrink=1 MinW=0 MinH=0 ClipToBounds
        │         └─ Embed.Comp(PopOutVideoStage{ Host=VideoStageHost(PopOut,…,toggle DetachedFullscreen),
        │                                         IsHostFullscreen = DetachedFullscreen })
        │            Key = "stage:gen:N:f0|f1"                 :154
        └─ [no player] Array.Empty<Element>()  — NO poster, NO spinner: a bare letterbox rect :155  (0.2.9 defect, W14b)

PopOutVideoStage : Component  (THE shared stage: PiP, pop-out, fullscreen)  PopOutVideoWindow.cs:169
├─ props  Source (frozen), Player (signal), Bridge, Settings, Host : VideoStageHost?, IsHostFullscreen  :173-198
├─ guard  binding.Player is null ⇒ bare BoxEl{Grow=1,MinHeight=0}  (the surface unmounts on the same pass) :204
└─ Embed.Comp(MediaPlayerElement) Key = "player:" + N + ":t0|t1" + ":f0|f1"   :222-257
      (NO "gen:" segment here — that prefix belongs to the three SURFACE keys above, not to this one; :255-256)
      Stretch=Uniform · AspectMode/CustomAspectRatio/AspectModeChanged from the bridge
      MoreMenuItems = VideoPlacementMenu.Items(includeFullscreen:false)
      PosterContent = LivePosterArt.Make()
      SuppressTransport = Host.Owner.Value != Host.Identity
      FullscreenRequested = Host.FullscreenRequested
      DragMovesWindow = VideoStageInput.DragMovesWindow(Identity, IsHostFullscreen)
      CursorAutoHide  = VideoStageInput.HidesCursorWindowed(Identity) ? Always : FullscreenOnly

VideoFullscreenSurface : Component                             Features/Video/VideoFullscreenSurface.cs:51
├─ prop   UserInitiated : IReadSignal<bool>? (peeked ONCE at mount)  :83
├─ static EnterTerminal = EnterExit(Sx:1.03, Sy:1.03, Active:true)   :65-67   — SCALE ONLY, no Opacity
├─ static ExitTerminal  = EnterExit(Sx:1.02, Sy:1.02, Active:true)   :70-72
├─ effect SetVideoSurfaceLive(Fullscreen) + disposer            :99-100
├─ layout-effect REAL OS fullscreen: capture prior, set true; restore prior on unmount  :107-117
├─ layout-effect focus: PushFocusScope(root), focus FirstFocusableIn(_videoArea) when user-initiated;
│                       on unmount PopFocusScope + RestoreFocus(prior)  :128-148
└─ root BoxEl Grow=1 Focusable OnFocusChanged(re-park)  :150-204
    OnKeyDown: bails on e.Handled OR any of Ctrl|Alt|Shift (:162) — so Shift+Esc / Ctrl+F do NOT exit —
               then Escape|F → ExitVideoFullscreen (:168-170)
    └─ BoxEl Grow=1 ZStack ClipToBounds Fill=Tok.MediaLetterbox OnPointerExit=no-op  :182-202
        ├─ Shield(hooks)   Key="fs:shield"  FIRST child, childless, AlignSelf/JustifySelf=Stretch,
        │                  OnClick→focus root  :212-217
        ├─ VideoArea(b, realized)                              :224-276
        │   ├─ [player] Embed.Comp(PopOutVideoStage) Key="fsstage:gen:N"
        │   │              Host = VideoStageHost(Fullscreen, TransportOwnerNow, ExitVideoFullscreen)
        │   │              Settings = null (DELIBERATE :236 — "Always on top" is Detached-only, so the
        │   │                                fullscreen ⋯ menu never carries it even in principle)
        │   │              IsHostFullscreen = true (constant, never keyed)
        │   └─ [no player] ZStack[art @0.4, LoadingOverlay]     :266-275
        └─ ExitChrome(b)  — pass-through positioner             :306-335
            └─ band H=56 Pad=(20,12,12,0) Gap=12 Justify=SpaceBetween Gradient=Tok.ScrimTop
                        Opacity 0→Hover 1
                ├─ TextEl(Prop.Of(() => CurrentTrack.Title)) 15/600 Tok.OnMediaPrimary 1 line ellipsis
                └─ ToolTip.Wrap(StageChrome.ExitFab(Icons.BackToWindow), player.videoExitFullScreen)

WatchPageView.Stage (the PageStage host)                       Features/Modules/WatchPageView.cs:90-141
└─ BoxEl Key="module-stage" AspectRatio=16/9 MaxHeight=560 Fill=Tok.MediaLetterbox  (a COLUMN: hazard 2)
    └─ BoxEl Grow=1 ZStack                                      — AspectRatio never reaches a ZStack's measure
        ├─ DockedVideoSurface.PosterGround(model.PosterUrl)      — ALWAYS the bottom layer
        ├─ Flow.Show(() => !Live(), PlayCta)   — 64-DIP accent disc, WaveeCta.Icon + Radii.Full
        │     ADDED ONLY WHEN onPlay is not null (WatchPageView.cs:101): a document with no play action
        │     shows a bare poster and no affordance at all — absent rather than dead
        └─ Flow.Show(Live, Embed.Comp(DockedVideoSurface{Face=PageStage, OwnerStagePlayable=stagePlayable})
                           with Key="module-stage-video")

VideoPlacementMenu.Items(b, settings, includeFullscreen)       Features/Video/VideoPlacementMenu.cs:38-76
  1  RadioItem  player.dockInRail            Icons.SplitView    E8A0   reason: player.videoNeedsWiderWindow
  2  RadioItem  player.videoMiniPlayer       Icons.BackToWindow E73F   (no reason — always available)
  3  RadioItem  player.videoInSeparateWindow Icons.Movie        E8B2   reason: player.videoNoSecondWindow
  4  RadioItem  player.videoFullScreen       Icons.FullScreen   E740   accel "F11" | player.videoNoFullscreen
     (row 4 only when includeFullscreen — the player bar passes true, every surface passes false)
  —  Separator + Toggle player.videoAlwaysOnTop      (only while resolved == Detached)
  —  Separator + Item   player.turnOffVideo  Icons.Cancel E711  (only while PlacementCore.IsActive)

VideoOverrideManagerFlyout : Component                         Features/Shell/VideoOverrideManagerFlyout.cs:23
├─ props  Rows : Func<IReadOnlyList<VideoOverrideRow>> · Version : IReadSignal<int>
│         RowActions : Func<row,Element> · StatusChip : Func<status,Element>   (all frozen; Version drives re-read)
├─ state  _view (0 root | 1 leaf) · _query · _focus · _forward
└─ BoxEl W=420 Pad=12 ClipToBounds
    └─ body Key="vo-view:root"|"vo-view:all" Animate = PageSlideForward|PageSlideBack
        ├─ ROOT  (BuildRoot)                                    :68-115
        │   ├─ EditableText Key="vo-search" W=396 H=32  (stays mounted across Recent↔Results↔NoMatches;
        │   │     NOT rendered at all in the Empty section, :76 — an empty roster has nothing to search)
        │   ├─ Empty     → EmptyState()                         (videoOverride.settingsEmpty + …EmptySub)
        │   ├─ NoMatches → Hint(videoOverride.noMatches)
        │   ├─ Results   → SectionLabel(matchCount) + RowList(hits, compact:false, maxHeight:380)
        │   ├─ Recent    → SectionLabel(recentlyAdded) + RowList(RecentlyAdded(all,4), compact:true, maxHeight:280)
        │   └─ Divider + BrowseAllRow(total)   (ShowsBrowseAll = total>0 && !IsSearching)
        └─ LEAF  (BuildAll)                                     :138-161
            ├─ header row MinH=36 : BackButton 28×28 + BodyStrong(settingsHeader) + Caption(count)
            └─ RowList(all, compact:false, maxHeight:400)
```

### 1b. 0.3 composition (proposed)

Props still freeze at mount. The **how a change reaches a child** column is the contract.

| 0.2.9 node | 0.3 target | Kind | Inputs | Change reaches it by |
|---|---|---|---|---|
| `PlacementCore` + friends | `Shell/Video.cs` | CORE static | plain values | — (pure) |
| `DockedVideoSurface` | `Shell/Video.UI.cs` → `Video.DockedSurface : Component` | UI | `Face` (frozen enum), `OwnerStagePlayable` (frozen string) | placement/track/rail-width via **signals read in `Render`** (`Playback.VideoSurface`, `Playback.Current`, `Shell.ActiveStagePlayable`, `Shell.RailWidth`); face + owner never change without a **`Key` remount** (the route IS the key) |
| `LivePosterArt` | `Video.LivePoster : Component` | UI | none | reads `Playback.Current` **inside its own render** — this is the whole point; never a frozen `Poster(track)` |
| `InWindowVideoPip` | `Video.Pip : Component` | UI | `Settings` (frozen instance) | geometry through its own `Signal<float>` `_x/_y/_w/_h/_placed`; `Transform`/`Width`/`Height` are `Prop.Of` thunks so a drag never re-renders |
| `PopOutVideoStage` | `Video.Stage : Component` | UI | `Source` (frozen), `Player` (frozen **signal**), `Bridge`, `Settings`, `Host` (frozen struct), `IsHostFullscreen` (frozen bool) | `Player.Value` read in `Render` re-binds; `suppress` + `IsHostFullscreen` + generation are folded into the **element `Key`** (`"player:gen:N:t0|t1:f0|f1"`) because `MediaPlayerElement`'s props freeze |
| `PopOutVideoWindow` / `PopOutVideoContent` | `Video.PopOutRoot` / `Video.PopOutContent` | UI | frozen **signal instances** (context does not cross the AppHost boundary) | `Source.Value`/`Player.Value`/`DetachedFullscreen.Value` read in `Render`; the fullscreen bit is in the stage `Key` |
| `VideoPlacementHost` | `Shell/Video.Host.cs` → `Video.DetachedOwner : Component` (renders empty) | SHELL | `Settings` (frozen) | five `UseSignalEffect`s; the window handle is a `UseRef`, never reactive |
| `VideoFullscreenSurface` | `Video.FullscreenSurface : Component` | UI | `UserInitiated` (frozen signal, **peeked once**) | mount/unmount **is** the mode; the shell's `Flow.Show` remounts it every time |
| `VideoPlacementMenu` | `Video.PlacementMenu.Items(...)` static | UI | plain values + `Playback` handle | rebuilt per open (the lazy open-thunk convention) |
| `VideoOverrideManagerFlyout` | `Screens/+Settings.UI.Video.cs` (named partial, owner K writes it in Wave 4, owner R's `Settings.UI.cs` Playback tab mounts it in Wave 6) → `Settings.VideoOverrideManager : Component` | UI | `Rows` (Func), `Version` (signal), `RowActions`/`StatusChip` (Funcs) | **`Version.Value` read in `Render`** is what makes a frozen `Rows` thunk live |
| `DockedCap` / `PinnedHero` mounts | `Shell/Rail.UI.cs` | UI | `Shell.DockedVideoHeight` (FloatSignal, a **stable bind**, never a fresh `Prop.Of` per render) | — |
| `WatchPageView.Stage` | `Platform/Modules.UI.cs` | UI | `stagePlayable` (frozen string per route) | `Flow.Show(Live)` — a **node-bound effect**, never a C# `if`, because a parked/exit-frozen page cannot re-render |

---

## 2. Wireframes

Horizontal scale ≈ **8 DIP per character**. Vertical is compressed (~12 DIP per row) and every real height is annotated.

### W1 — Docked cap, Cap face, 16:9 source, at rest @ rail 340

```
   rail inner width = 340 DIP = 42 chars
   ┌──────────────────────────────────────────┐ ← card top; the RAIL clips its own rounded top-left
   │                                          │   BoxEl ZStack ClipToBounds Shrink=0 MinWidth=0
   │                                          │   Height = ShellUi.DockedVideoHeight (FloatSignal)
   │            [ live video hole ]           │   Fill = Tok.MediaLetterbox (#000 opaque)
   │         MediaPlayerElement, full-bleed   │   CornerRadius = 0, no border, NO shadow
   │         Stretch = Uniform (Fit)          │   ShowLetterboxBars = true
   │                                          │   H = FitDockedVideoHeight(340,1920,1080)
   │                                          │     = 340 × 1080/1920 = 191.25 DIP
   └──────────────────────────────────────────┘
                                           ↑ the bottom 16 DIP carry the rail's vertical Splitter
                                             (ShowIndicator=false, HitTestPassThrough wrapper)
```

### W2 — Docked cap, hover (chrome + transport both revealed) @ rail 340

```
   ┌──────────────────────────────────────────┐ ── strip: H=30, Justify=End, Gap=Spacing.XXS(2),
   │▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓[▣][⛶][✕]  ▓▓│    Padding=(8,0,8,0), Gradient=Tok.ScrimTop
   │  ScrimTop: #000 α153 → α51 @35% → 0      │    Opacity 0→1 over 167 ms FluentDecelerate
   │                                          │    each glyph 24×24, Radii.Control(4),
   │            [ live video hole ]           │    11 DIP Segoe Fluent, Tok.OnMediaSecondary
   │                                          │    →Hover Tok.OnMediaPrimary, fill α0.14/α0.22
   │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│ ── MediaPlayerElement's OWN transport, bottom-pinned,
   │ ●───────────────────────────────○        │    Gradient=Tok.ScrimBottom, Padding=(14,34,14,8)
   │ ▶  0:42 / 3:18              [⋯]          │    seek row 24 + gap 2 + control row 34 ⇒ 102 DIP
   └──────────────────────────────────────────┘    COMPACT (340 < CompactTransportWidth 420):
                                                   chips fold into ⋯; auto-hides after 3,000 ms
   3 glyphs = 24·3 + 2·2 = 76 DIP; right edge at cardW − 8.
   ▣ Icons.BackToWindow E73F → ShowVideoAt(Floating)      tip: player.videoMiniPlayer
   ⛶ Icons.FullScreen   E740 → ShowVideoAt(Fullscreen)    tip: player.videoFullScreen
   ✕ Icons.Cancel       E711 → NotifyVideoSurfaceClosed(Docked)  tip: player.turnOffVideo
```

### W3 — Docked cap, aspect ladder (same rail width, four sources)

```
 railW = 340                    H = Math.Clamp(340 × naturalH/naturalW, 120, 560)

 16:9  1920×1080  ┌────────────────────────────────────────┐  191.25 DIP   (== DockedVideoNaturalH(340))
                  └────────────────────────────────────────┘
 4:3    640×480   ┌────────────────────────────────────────┐  255.00 DIP
                  │                                        │
                  └────────────────────────────────────────┘
 21:9  2560×1080  ┌────────────────────────────────────────┐  143.44 DIP
                  └────────────────────────────────────────┘
 32:9  3840×1080  ┌────────────────────────────────────────┐  120.00 DIP   ← floored at DockedVideoFitMinH
                  └────────────────────────────────────────┘
 9:16  1080×1920  ┌────────────────────────────────────────┐  560.00 DIP   ← ceilinged at DockedVideoMaxH
                  │  (portrait: the ELEMENT pillarboxes    │              (604.44 would exceed the rail's
                  │   inside the box with its own bars)    │               remaining Grow=1 body)
                  └────────────────────────────────────────┘
 unknown (0×0)    16:9 fallback → 191.25 DIP; the manifest's own NaturalWidth/Height SEEDS this at
                  mount so the decoder's later NaturalSize report is a CONFIRM, never a second reflow.
```

### W4 — Docked cap, splitter drag in progress @ rail 340

```
   ┌──────────────────────────────────────────┐
   │            [ live video hole ]           │   the SAME FloatSignal the fit effect writes;
   │  (Stretch stays Uniform — the element    │   drag range = [DockedVideoNaturalH(340)=191.25, 560]
   │   pillarboxes/letterboxes into the       │   (the splitter's floor is 16:9; the FIT's floor is 120)
   │   grown box; Crop is a ⋯-menu click)     │
   │▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓│ ← Splitter strip, 16 DIP, SplitterAxis.Vertical,
   ╞══════════════════════════════════════════╡   ShowIndicator = false, cursor = SizeNS
   │  rail body (lyrics / queue / …) Grow=1   │   on COMMIT: ClampDockedVideoHeight(h, railW),
                                                  ShellUi.DockedVideoHeightPinned = true,
                                                  settings["shell.rail.docked-video.height"] = h
   The pin is cleared by the SURFACE on the next PopOutVideoSource.Key change (DockedVideoSurface.cs:194-198):
   a decision about one video is not a standing one.
```

### W5 — Docked cap, no player yet (resolving manifest / DRM licence) @ rail 340

```
   ┌──────────────────────────────────────────┐  Poster(track) — DockedVideoSurface.cs:409-413
   │░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░│  BoxEl Grow=1 ZStack ClipToBounds Fill=Tok.MediaLetterbox
   │░░░░ track artwork, Surfaces.ArtworkFill ░│  ├─ PosterGround: BoxEl Opacity = 0.4f, radius 0
   │░░░░ Opacity 0.4 over #000 ░░░░░░░░░░░░░░░│  └─ LoadingOverlay: Direction=1, centred,
   │░░░░░░░░░░░░ ◌ ░░░░░░░░░░░░░░░░░░░░░░░░░░░│       Gap=Spacing.S(8), HitTestPassThrough
   │░░░░░░░░░ Loading… ░░░░░░░░░░░░░░░░░░░░░░░│       ProgressRing.Indeterminate(20, Tok.TextOnAccentPrimary)
   └──────────────────────────────────────────┘       TextEl 12/600 Tok.TextOnAccentPrimary NoWrap 1 line
   A track with no art degrades to the bare letterbox fill — still never an empty rect.
```

### W6 — Docked cap, NOT mounted (video elsewhere / off / no video) @ rail 340

```
   ┌──────────────────────────────────────────┐  RightRail's wrapper Height binds to DockedVideoHeight and
   │  rail header (Lyrics ▾  ⤢  ✕)            │  the SURFACE returns a bare BoxEl (Shrink=0, zero size),
   ├──────────────────────────────────────────┤  so NOTHING reflows — that is the gate's whole design
   │  rail body                               │  (DockedVideoSurface.cs:276, RightRail.cs:190-196).
```

In `RailMode.Video` specifically, `VideoRailPanel` fills the vacated slot with the **B10 placeholder** — the same
composition MINUS the spinner (`VideoRailPanel.cs:111-135`): `AspectRatio 16/9`, `Corners = Radii.Card(8)`,
`Fill = Tok.MediaLetterbox`, art at `Opacity 0.4`, `player.noVideoForThisSong` at 12/600 `Tok.TextOnAccentPrimary`.

### W7 — Docked cap, PageStage face (watch page) @ content column 880

```
 ┌────────────────────────────────────────────────────────────────────────────────────────────────────┐
 │                                                                                                    │  Key="module-stage"
 │                                                                                                    │  AspectRatio = 16/9 (a COLUMN)
 │                              [ live video hole, FULL-BLEED ]                                       │  MaxHeight = 560
 │                     the PAGE owns the envelope: the 16:9 box, the                                  │  Fill = Tok.MediaLetterbox
 │                     rounded silhouette and the idle PosterGround                                   │  NO Enter/Exit/Layout/Opacity
 │                     under it, so idle→live is ONE cross-fade                                       │  h = 880 / (16/9) = 495 DIP
 │                                                                                                    │  card: Grow=1 MinHeight=0
 └────────────────────────────────────────────────────────────────────────────────────────────────────┘  Fill=Tok.MediaLetterbox
   idle instead:  same box, PosterGround(model.PosterUrl) @ 0.4, with a 64-DIP accent disc (Icons.Play,
                  WaveeCta.Icon + Radii.Full, tooltip detail.play) centred — Flow.Show(() => !Live(), …)
```

### W8 — In-window PiP, anchored home, at rest @ viewport 1440×900

```
  window 1440 × 900 (client), player bar 72 at the bottom
  ┌──────────────────────────────────────────────────────────────────────────────────┐
  │  page content — ContentHost insets its BOTTOM by FloatingSurfaceReserve          │
  │                 = _h + Margin = 202 + 16 = 218 DIP                               │
  │                                                    ┌──────────────────────────┐  │
  │                                                    │                          │  │  PiP surface
  │                                                    │   [ live video hole ]    │  │  W = 360, H = 202
  │                                                    │   pure video at rest     │  │  x = 1440−360−16 = 1064
  │                                                    │                          │  │  y = 900−202−72−16 = 610
  │                                                    └──────────────────────────┘  │  Corners = Radii.Card(8)
  ├──────────────────────────────────────────────────────────────────────────────────┤  Border 1 Tok.StrokeCardDefault
  │  player bar, 72 DIP — STILL RENDERING (TransportOwner.Docked is admitted by it)  │  Shadow = Elevation.Flyout
  └──────────────────────────────────────────────────────────────────────────────────┘  Fill = Transparent (the hole!)
   16 DIP gap from every window edge (Margin); the anchor is DERIVED each frame from the viewport,
   so a window resize moves the card with the corner — until the first drag sets _placed = true.
```

### W9 — In-window PiP, hover (chrome + resize nub + transport)

```
  ┌────────────────────────────────────────────┐  ← Corners (8,8,0,0) on the strip so it cannot square
  │▓▓▓▓ drag surface (Cursor = SizeAll) ▓▓[✕]▓▓│     off the card's rounded border
  │   H=30, Padding = (CornerW 14, EdgeBand 6, │  ✕ 24×24, Radii.Control(4), Icons.Cancel E711 @11 DIP,
  │   CornerW 14, 0) — insets so neither child │     Tok.OnMediaSecondary → Hover Tok.OnMediaPrimary,
  │   ever sits under a resize band            │     HoverFill α0.14 / PressedFill α0.22
  │                                            │     → NotifyVideoSurfaceClosed(Floating)
  │          [ live video hole ]               │
  │                                            │
  │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│  ← the SHARED stage's transport (suppress = false,
  │ ●──────────────────────○                   │     because TransportOwnerFor(Floating) == Docked)
  │ ▶ 0:42 / 3:18                     [⋯]   ◣ │  ← the SE nub: 8×8, Margin (0,0,2,2),
  └────────────────────────────────────────────┘     Corners (0,0,Radii.Control 4,0), Fill = Tok.OnMediaTertiary,
   360 wide < 420 ⇒ the transport is COMPACT here too.  HitTestVisible=false, Opacity 0→Hover 1 (same cascade)
```

### W10 — In-window PiP, the eight resize zones (hit map, never painted except the nub)

```
   ┌──┬──────────────────────────────────┬──┐   row0: Height = CornerH 12, AlignItems = Start
   │NW│              N  (H=6)            │NE│         [0] 14×12 SizeNWSE   [1] grow×6 SizeNS   [2] 14×12 SizeNESW
   ├──┼──────────────────────────────────┼──┤
   │W │                                  │ E│   row1: Grow = 1, the middle cell is HitTestPassThrough
   │6 │   (pass-through: the transport   │6 │         [3] W=6 SizeWE                          [4] W=6 SizeWE
   │  │    keeps its clicks, the wheel   │  │
   │  │    keeps falling through)        │  │
   ├──┼──────────────────────────────────┼──┤
   │SW│              S  (H=6)            │SE│   row2: Height = CornerH 12, AlignItems = End
   └──┴──────────────────────────────────┴──┘         [5] 14×12 SizeNESW  [6] grow×6 SizeNS   [7] 14×12 SizeNWSE + nub
   Corner rows are 12 tall while the edge bands stay 6 thin (Start/End pins them to the outer edge), so the
   bottom band grazes only the transport's 8-DIP bottom padding instead of covering its controls.
   The band layer is TOPMOST (layer 2) so a band always wins over the chrome beneath it.
```

### W11 — In-window PiP, drag in progress

```
   Visuals: NONE beyond the card moving. There is no ghost, no drop target, no snap chip, no flash.
   ┌────────────────────────────┐        The Transform thunk re-evaluates from _x/_y (compositor-only;
   │    [ video, still live ]   │ ◄─ ✛   layout never moves), so the hole tracks the card for free —
   └────────────────────────────┘        a translate composes on the AbsoluteRect the punch reads from.
   Constraints while dragging (InWindowVideoPip.cs:531-534):
     x ∈ [16, max(16, vpW − w − 16)]          y ∈ [16, max(16, vpH − h − 72 − 16)]
   On pointer-down: the drawn position is COMMITTED first (an unplaced card would otherwise snap to the
   origin on the first sample), _placed = true ⇒ FloatingSurfaceReserve drops to 0 and the page re-expands.
   On release (an OnDrag node's OnClick IS the commit edge): settings["video.pip.rect"] = "x,y,w,h" (ints).
```

### W12 — In-window PiP, resize in progress (NW corner shown)

```
       ┌ anchored edges are the ones NOT named by the PipResizeEdge flags
   ↖   ┌─────────────────────┐     Left  : w = clamp(startW − dx, 240, max(240, right − 16)); x = right − w
       │  [ video ]          │     Right : w = clamp(startW + dx, 240, max(240, vpW − 16 − startX))
       │                     │     Top   : h = clamp(startH − dy, 135, max(135, bottom − 16)); y = bottom − h
       └─────────────────────┘     Bottom: h = clamp(startH + dy, 135, max(135, vpH − 16 − 72 − startY))
   MinW 240 × MinH 135 (exactly 16:9). A resize sets _sized = true, which ENDS the content-aspect fit —
   until the CONTENT'S aspect changes by more than 0.01, at which point the height re-fits from the KEPT
   width (the user owns the width, the content owns the shape).  InWindowVideoPip.cs:164-167
```

### W13 — In-window PiP, no player (poster) — identical composition to W5 at 360×202

```
   ┌────────────────────────────────────────────┐  ZStack Fill = Tok.MediaLetterbox
   │░░░░ artwork @ 0.4 ░░░░░ ◌ ░░░░░░░░░░░░░░░░░│  ProgressRing 20 + player.loading 12/600
   │░░░░░░░░░░░░░ Loading… ░░░░░░░░░░░░░░░░░░░░░│  (InWindowVideoPip.cs:333-343 — byte-for-byte the
   └────────────────────────────────────────────┘   docked card's, and the fullscreen surface's)
```

### W14 — Pop-out window, windowed @ 640×360 (the open size)

```
  ╔══════════════════════════════════════════════════════════════╗  OS frame: CustomFrame — the OS keeps only
  ║                                                              ║  the RESIZE BORDERS. No title bar of ours.
  ║                                                              ║  Root BoxEl W/H = viewport,
  ║                    [ live video hole ]                       ║  Fill = Tok.MediaLetterbox — ALWAYS opaque,
  ║                                                              ║  including while live (the DestOut punch
  ║              drag the PICTURE to move the window             ║  removes it over the video rect; anything
  ║              (OS move loop ⇒ Aero Snap, monitor hops)        ║  NOT painted shows the DESKTOP through).
  ║▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒║  640 ≥ 420 ⇒ the transport is FULL here
  ║ ●─────────────────────────────────────○                      ║  (chips visible, not folded into ⋯).
  ║ ▶ ⏪ ⏩   0:42 / 3:18   🔊 ══   [CC] [1×] [HD] [⛶] [⋯]         ║  Cursor auto-hides with the chrome
  ╚══════════════════════════════════════════════════════════════╝  (CursorAutoHidePolicy.Always — mpv's
   Window title = the CURRENT TRACK (live, SetTitle per track change), else player.nowPlaying.        windowed default).
   AlwaysOnTop = settings["video.window.ontop"], default TRUE, applied LIVE (VideoWindowPrefs.Epoch).
   MinClientSizeDip = 320×180.  Reopens at settings["video.window.rect"], clamped into a visible monitor.
```

### W14b — Pop-out window, NO PLAYER yet (the 0.2.9 defect the other three surfaces do not have)

```
  ╔══════════════════════════════════════════════════════════════╗  PopOutVideoContent.Render (:149-155)
  ║                                                              ║  Children = live ? [stage] : Array.Empty
  ║                                                              ║  ⇒ for the whole manifest + DRM-licence
  ║                  (nothing — Tok.MediaLetterbox)              ║    round-trip, seconds long on every
  ║                                                              ║    track change, this window is a plain
  ║   no poster · no artwork · no spinner · no "Loading…"        ║    black rectangle with a title bar
  ║                                                              ║    entry and nothing else.
  ╚══════════════════════════════════════════════════════════════╝
   0.3 TARGET: the same `Poster(track)` the docked card, the PiP and the fullscreen surface already use —
   artwork @ 0.4 over Tok.MediaLetterbox + the 20-DIP ring + player.loading. It is the one helper §9 asks for
   anyway; wiring the pop-out's non-live arm to it is a two-line change and the only place this chapter
   knowingly diverges from 0.2.9. Everything else here is parity.
```

### W15 — Pop-out window, borderless fullscreen on its own monitor

```
  ┌───────────────────────────────────── monitor 2, edge to edge ─────────────────────────────────────┐
  │                                                                                                   │  DetachedFullscreen = true
  │                                   [ live video hole ]                                             │  → live.SetFullscreen(true)
  │                                                                                                   │  on THIS window's handle
  │                    DragMovesWindow = FALSE here (nowhere to go)                                   │  (NOT InputHooks —
  │                                                                                                   │   that would fullscreen
  │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│   the MAIN window on
  │ ▶ ⏪ ⏩  0:42 / 3:18   🔊 ══   [CC] [1×] [HD] [▣ exit] [⋯]                                          │   the MAIN monitor)
  └───────────────────────────────────────────────────────────────────────────────────────────────────┘
   The stage Key gains ":f1", which REMOUNTS MediaPlayerElement so IsHostFullscreen actually changes — the
   transport draws BackToWindow instead of FullScreen, the ⋯ row says "Exit full screen", and Escape leaves.
   The window rect is NOT persisted while this is true (VideoPlacementHost.cs:121-126).
```

### W16 — Fullscreen surface (main window), at rest @ 1920×1080

```
  ┌──────────────────────────────────────────────────────────────────────────────────────────────────┐
  │                                                                                                  │  The shell's title bar and
  │                                                                                                  │  the 72-DIP player bar are
  │                                                                                                  │  UNMOUNTED (not hidden) —
  │                              [ live video hole, full-bleed ]                                     │  extra layers over a
  │                                                                                                  │  full-screen video defeat
  │                          Fill = Tok.MediaLetterbox under it                                      │  the composition fast path.
  │                                                                                                  │  NO pass-through bands.
  │                                                                                                  │
  │                                                                                                  │
  └──────────────────────────────────────────────────────────────────────────────────────────────────┘
   Hit order inside the ZStack: [0] Shield (childless, full-bleed, click → focus the root) — MUST BE FIRST,
   because InputDispatcher.Hit keeps the LAST matching child; [1] VideoArea; [2] ExitChrome.
```

### W17 — Fullscreen surface, hover (exit band + the video's own transport)

```
  ┌──────────────────────────────────────────────────────────────────────────────────────────────────┐
  │▓▓ Wherever You Will Go                                                                    ( ▣ ) ▓│ ← band H = 56
  │   15/600 Tok.OnMediaPrimary, 1 line, ellipsis, Shrink=1     StageChrome.ExitFab 44×44           │   Padding (20,12,12,0)
  │   Gradient = Tok.ScrimTop, Opacity 0→1 @167 ms FluentDecelerate, Justify = SpaceBetween, Gap 12  │   ExitInset 12,
  │                                                                                                  │   left = 12 + Spacing.S 8
  │                              [ live video hole ]                                                 │
  │                                                                                                  │   ExitFab: Radii.Circle(44),
  │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│   Fill = StageInk.GlassPlate
  │ ▶ ⏪ ⏩  0:42 / 3:18   🔊 ══    [CC] [1×] [HD] [⛶] [⋯]        THE ONLY TRANSPORT ON SCREEN       │   (OnMediaPrimary α0.14),
  └──────────────────────────────────────────────────────────────────────────────────────────────────┘   Border 1 StageInk.Stroke,
   The title is bound REACTIVELY (Prop.Of over CurrentTrack) — the surface outlives a track change.       Shadow = Elevation.Card,
   Its tooltip names the DESTINATION (player.videoExitFullScreen), never the state.                       glyph 18 StageInk.Ink
```

### W18 — Fullscreen surface, no player (placement move: close-then-open)

```
  ┌──────────────────────────────────────────────────────────────────────────────────────────────────┐
  │░░░░░░░░░░░░░░░░░░░░░░░░░ artwork @ 0.4 over Tok.MediaLetterbox ░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░│
  │░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░ ◌ ░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░│
  │░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░ Loading… ░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░│
  └──────────────────────────────────────────────────────────────────────────────────────────────────┘
   B22: a placement MOVE is a close-then-open, so the fullscreen surface briefly has no player. It covers
   that with the same poster the other three use — never a black screen.
```

### W19 — The placement menu (from the player bar's chevron: `includeFullscreen: true`)

```
   ┌──────────────────────────────────────────────────┐  MenuFlyout, opened UPWARD out of the split
   │ ⊙  ▣  Dock in rail                               │  button (FlyoutPlacement.TopEdgeAlignedLeft,
   │ ⦿  ▣  Play in a mini player                      │  FocusTrap + LightDismiss)
   │ ⊙  ▣  Play in a separate window                  │  Radio-checked against the RESOLVED placement
   │ ⊙  ⛶  Full screen                           F11  │  (so it shows where the video IS; re-picking
   ├──────────────────────────────────────────────────┤  the checked row is a harmless no-op)
   │ ☑     Always on top                              │  ← only while resolved == Detached
   ├──────────────────────────────────────────────────┤
   │    ✕  Turn off video                             │  ← only while PlacementCore.IsActive
   └──────────────────────────────────────────────────┘
   Unavailable rung (e.g. the window is too narrow to dock):
   │ ⊙  ▣  Dock in rail             Needs a wider window │  ← DISABLED, reason in AcceleratorText.
                                                            Hidden is wrong; a disabled item cannot carry a
                                                            tooltip (IsEnabled=false removes hit-testing).
   From a SURFACE's ⋯ menu the Full screen row is omitted (includeFullscreen: false) — the element has its own.
```

### W20 — Override manager flyout, ROOT / Recent @ 420

```
   ┌────────────────────────────────────────────────────┐  W = 420, Padding = Spacing.M (12) all round,
   │ ┌────────────────────────────────────────────────┐ │  ClipToBounds; kids Gap = Spacing.S (8)
   │ │ Search by track, artist or file name           │ │  EditableText Key="vo-search", placeholder =
   │ └────────────────────────────────────────────────┘ │  videoOverride.searchPlaceholder (VERBATIM —
   │  Recently added                                    │  it is NOT "Search attached videos")
   │ ┌────────────────────────────────────────────────┐ │  W = 420 − 24 = 396, H = WaveeSize.ControlH (32)
   │ │ 🎬  Wherever You Will Go              [ OK ]   │ │  SectionLabel: Caption (12/16) Weight 600
   │ │     wherever.mp4                               │ │  Tok.TextSecondary, Margin (4, 2, 0, 0).
   │ ├────────────────────────────────────────────────┤ │  NO uppercase transform anywhere — the string is
   │ │ 🎬  Another Song                   [ Missing ] │ │  sentence-case "Recently added" as authored.
   │ │     another.mp4                                │ │  CompactRow: MinHeight 44, Gap 8,
   │ └────────────────────────────────────────────────┘ │  Padding (8,4,8,4), Radii.Control(4), Interaction.Subtle,
   │ ────────────────────────────────────────────────── │  Icons.Movie 16 TextSecondary; title Body/TextPrimary
   │ 📁  Browse all…             12 attachments     ›   │  over file Caption/TextSecondary in a column with Gap 1.
   └────────────────────────────────────────────────────┘  ScrollEl ContentSized, MaxHeight 280, list gap 2
                                                           (at most VideoOverrideUx.RecentCount = 4 rows).
   Chip texts are the loc strings verbatim: "OK" / "Missing" / "Drive offline" / "Unplayable" — "OK" is
   upper-case in the resource, not title-case.  Divider: 1 DIP Tok.StrokeSurfaceDefault.
   Browse-all row: MinHeight 40, Icons.Folder 16 / ChevronRight 14; its count is
   videoOverride.settingsCount = "{n, plural, one {# attachment} other {# attachments}}", i.e. "12 attachments",
   NOT a bare numeral.  → Drill(null): _forward = true, _view = 1.
   Anchoring (SettingsPage.VideoOverrides.cs:192-196): FlyoutPlacement.BottomEdgeAlignedLeft off the Manage
   button, PopupOptions(FocusTrap: true, LightDismiss, Chrome: PopupChrome.Popup) with ConstrainToRootBounds = true.
```

### W21 — Override manager flyout, ROOT / Results and NoMatches

```
   Results (IsSearching && matchCount > 0)              NoMatches (IsSearching && matchCount == 0)
   ┌────────────────────────────────────────────┐       ┌────────────────────────────────────────────┐
   │ [ another                              ]   │       │ [ zzzz                                 ]   │
   │  3 results                                 │       │  No attachments match that search.         │  Hint: MinHeight 44,
   │ ┌────────────────────────────────────────┐ │       └────────────────────────────────────────────┘  Padding (8,0,8,0),
   │ │ Another Song                 [ OK ]    │ │        Body Tok.TextSecondary, Wrap, MaxLines 2
   │ │ The Artist  ·  D:\Music\another.mp4    │ │       Empty (total == 0): no search box at all —
   │ │ Replace video file…                    │ │        BodyStrong(settingsEmpty "No videos attached
   │ │ Show in Explorer  [ Remove video ]     │ │        yet") + Caption(settingsEmptySub, Wrap,
   │ └────────────────────────────────────────┘ │        MaxLines 4), block Padding 8 all, Gap 4 —
   └────────────────────────────────────────────┘        TEACH the context-menu path, not an empty list.
   The section label is videoOverride.matchCount = "{n, plural, one {# result} other {# results}}" — "3 results",
   sentence case, NOT "3 MATCHES". Caption applies no uppercase transform anywhere in this panel.
   ACTION LABELS ARE THE FULL LOC STRINGS, never short verbs (SettingsPage.VideoOverrides.cs:226-235):
     "Replace video file…"  HyperlinkButton, always
     "Locate video file…"   HyperlinkButton, only when CanLocate (Status == Missing)
     "Show in Explorer"     HyperlinkButton, only when CanReveal (Status is Ok | Unplayable)
     "Remove video"         Button.Standard, always, last
   The actions row is Direction 0, AlignItems Center, Gap = Spacing.S (8), MinWidth 0, **Wrap = true**
   (`:252-256`) — at 420 − 24 (panel pad) − 16 (row pad) = 380 usable DIP those full labels routinely take TWO
   lines, and wrapping (not ellipsising) is the intended read. Do not shorten them in 0.3.
   FullRow: Direction 1, Gap 4, Padding 8 all, Radii.Control(4), Fill = Tok.FillSubtleSecondary
            (or Tok.AccentSubtle while highlighted by a drill focus).  Search results use the FULL row on
            purpose, so a repair never needs a drill.  ScrollEl MaxHeight 380 (root) / 400 (leaf).
   A RowList handed ZERO rows renders Hint(settingsEmpty) in place of the scroller (VideoOverrideManagerFlyout.cs:182).
```

### W22 — Override manager flyout, BROWSE-ALL leaf (drill, slide forward)

```
   ┌────────────────────────────────────────────────────┐  header row MinHeight 36, Gap 8
   ┌────────────────────────────────────────────────────┐  header row MinHeight 36, Gap 8
   │ ‹   Attached videos             12 attachments     │  BackButton 28×28 Radii.Control, ChevronLeft 14,
   │ ┌────────────────────────────────────────────────┐ │  Interaction.Subtle
   │ │ Wherever You Will Go                  [ OK ]   │ │  BodyStrong TextPrimary, Caption count TextSecondary
   │ │ The Calling  ·  D:\Music\wherever.mp4          │ │  path is the FULL path (2 lines, wrap, ellipsis) —
   │ │ Replace video file…   Show in Explorer         │ │  it answers "is this the file I meant?"
   │ │ [ Remove video ]                               │ │  actions: HyperlinkButtons + a Standard "Remove video",
   │ ├────────────────────────────────────────────────┤ │  Direction 0, Gap 8, Wrap = true
   │ │ Broken One                       [ Missing ]   │ │  Locate… appears only when CanLocate (Missing);
   │ │ D:\Gone\broken.mp4                             │ │  Show in Explorer only when CanReveal (Ok|Unplayable)
   │ │ Replace video file…   Locate video file…       │ │  The header count is settingsCount, so it reads
   │ │ [ Remove video ]                               │ │  "12 attachments" (plural-aware), not a bare "12".
   │ └────────────────────────────────────────────────┘ │  The subtitle is `artists + "  ·  " + FULL PATH`
   └────────────────────────────────────────────────────┘  (two spaces either side of the middot), or the bare
   Body Key = "vo-view:all", Animate = PageSlideBack.      path when the store has never seen the playable.
   Both views ride the app's standing page transition on ONE keyed child: drill → PageSlideForward, Back → mirror.
   The Locate… picker's TITLE is `videoOverride.locateTitle + " — " + NearestExistingAncestor(path)` — the deepest
   surviving folder is spelled into the dialog caption, not only used as its start directory
   (SettingsPage.VideoOverrides.cs:262-266).
```

### W23 — Rail Details body, video dockable but NOT docked (the Art|Video toggle)

```
   ┌──────────────────────────────────────────┐  NowPlayingHeroTile + ArtVideoToggle overlaid (ZStack, not a
   │ ▁▁▁▁ hero strip (Cover | Player) ▁▁▁▁▁▁▁ │  Margin trick on the tile: the tile's own S-inset is untouched)
   │  ┌────────────────────────────┐   ┌──┬──┐│  toggle H = 24, Corners = Radii.Control(4), ClipToBounds,
   │  │                            │   │🖼│🎬││  Fill = WaveeOnMedia.GlassHover (OnMediaPrimary α0.10)
   │  │        album art           │   └──┴──┘│  Margin (0, NowPlayingHeroTile.ArtTop + 4, 8 + 4, 0)
   │  │                            │          │  — DERIVED from ArtTop, never a literal
   │  └────────────────────────────┘          │  🖼 Icons.Picture E9BF selected, tip player.switchToAudio
   └──────────────────────────────────────────┘     → NotifyVideoSurfaceClosed(Docked)  (sticky off)
   Shown ONLY while PlacementCore.Allows(Available, Docked).   🎬 Icons.Movie E8B2, tip player.switchToVideo
   Never over the docked card: that card's own ✕ occupies the       → ShowVideoAt(Docked)
   very same top-right corner and does the very same thing.
   AlignSelf = Start, JustifySelf = End (that, not the Margin, is what parks it top-right).
   EACH HALF (RightRail.cs:347-359): 24 × 24, Corners = Radii.Control(4), Role = Button, Focusable = true,
   AllowFocusOnInteraction = false, Cursor = Hand, glyph 11 DIP Theme.IconFont, wrapped in ToolTip.Wrap.
     selected half   Fill = Tok.OnMediaPrimary α0.16, glyph Tok.OnMediaPrimary
     unselected half Fill = ColorF.Transparent,       glyph Tok.OnMediaSecondary
   There is no HoverFill/PressedFill on either half — the only state this control expresses is which side is lit,
   and the Art half is ALWAYS the lit one here (the control only exists in the un-docked state, so it has no
   `docked` parameter at all). Total toggle width = 48; the strip's ClipToBounds is what makes the two 4-DIP
   squares read as one 4-DIP capsule.
   THREE conditions gate it, all in PinnedHero (RightRail.cs:298-310), and the order matters:
     1. bridge is null OR **stageHosts** ⇒ the BARE art tile, no toggle. While a module watch page's stage owns the
        one surface the rail shows exactly what a video-less track shows — belt-and-braces against the hero
        outliving the card it stands in for.
     2. resolved == Docked ⇒ the DockedCap instead (byte-identical to the non-Details arm's).
     3. !Allows(Available, Docked) ⇒ the bare art tile (no video for this track, or the window is too narrow).
```

### W23b — Rail header, RailMode.Video (a SECOND placement affordance, not in the card)

```
   ┌──────────────────────────────────────────┐  RightRail.VideoHeaderKids (RightRail.cs:262-278)
   │  Video                    ▣    ⛶    ✕    │  TitleText(RailMode.Video)
   └──────────────────────────────────────────┘  ▣ Icons.BackToWindow  tip player.videoMiniPlayer
                                                    → Announcer.Say + ShowVideoAt(Floating)
   Only in the Video rail body, and only once the       ⛶ Icons.FullScreen tip player.videoFullScreen
   bridge is attached — with a null bridge the header       → Announcer.Say + ShowVideoAt(Fullscreen)
   degrades to bare TitleText + CloseButton.            ✕ CloseButton → ui.RailOpen = false — it closes the
                                                            RAIL, not the video. (WaveeShell's
   These two duplicate the card's own ▣ / ⛶ glyphs and       RailVideoCoupling.OnRailClosed is what then
   are a THIRD placement entry point beside the card         demotes a docked video to Floating.)
   strip and the ⋯ menu. 0.3 must keep all three: the
   card's strip is hover-only, and a user who has the rail header in view must not have to find the picture.
```

### W24 — LIVE source (a module broadcast): the transport is a DIFFERENT control

```
  Every wireframe above draws a FINITE transport ( ●────○  ▶ 0:42 / 3:18 ). A source with
  PopOutVideoSource.IsLive = true (SpotifyLive/PopOutVideoSource.cs:42-50 — carried on the SOURCE because MF
  reports a sliding DVR window as an ordinary finite GetDuration, which is how a six-hour broadcast once
  rendered as `0:03 / -3:22`) opens with SourceLiveness.Live and the engine never publishes a duration at all:

  ║▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒║  Same Tok.ScrimBottom ground, same
  ║ ●══════════════════════════════════╪═══════════════════════○ ║  ~102 DIP, same auto-hide timers.
  ║ ▶  ⏪ ⏩   ● LIVE   [Go live]   🔊 ══   [CC] [HD] [⛶] [⋯]      ║  The DVR rail is the seekable WINDOW,
  ╚══════════════════════════════════════════════════════════════╝  not the whole programme; "Go live"
                                                                    jumps to its right edge.
  The claimants are enumerated in PlacementCore.cs:441-444 ("play/pause · volume · LIVE chip · Go live ·
  fullscreen · the DVR rail when a live window exists"). This is an ENGINE-owned control surface; the app
  contributes only the IsLive bit. It is listed here because the 0.3 parity pass will otherwise capture only
  finite sources and never see it — and because the docked cap's content-aspect fit and the PiP's fit both run
  identically on a live source (the manifest still reports NaturalWidth/Height).
```

### W25 — The engine's own player frame (present on ALL FOUR surfaces, denied by §0.14's old wording)

```
   ┌── the app's wrapper: no corners, no border, Fill = Tok.MediaLetterbox ──────────────────┐
   │ ╭──────────────────────────────────────────────────────────────────────────────────╮   │
   │ │   MediaPlayerElement's own frame:  Corners = Radii.OverlayAll (8),                │   │
   │ │   BorderWidth 1, BorderColor = Tok.StrokeFlyoutDefault   (:1050-1058)             │   │
   │ │                      [ video hole ]                                               │   │
   │ ╰──────────────────────────────────────────────────────────────────────────────────╯   │
   └────────────────────────────────────────────────────────────────────────────────────────┘
   FrameCorners (MediaPlayerElement.cs:154-160):
     IsFullscreenPresentation → default (square)      ← INTERNAL to FluentGpu.Controls (:247); Wavee cannot set it
     CornerRadius > 0         → that radius            ← no Wavee surface passes one
     IsDecorative             → CornerRadius (0)       ← every Wavee surface passes IsDecorative = false
     otherwise                → Radii.OverlayAll (8)   ← THE ARM ALL FOUR SURFACES TAKE
   The border test on :1057-1058 is the same expression, so the hairline is drawn in all four too — including
   VideoFullscreenSurface, because IsHostFullscreen feeds PresentingFullscreen (:228) and NOT
   IsFullscreenPresentation. So the "full-bleed" fullscreen picture actually carries 8-DIP rounded corners and a
   1-DIP StrokeFlyoutDefault hairline against the monitor edge, and the docked cap is an 8-rounded, hairlined
   picture inside a square black card. Verify this before "fixing" any radius in 0.3 — see §0.14 and §6's wart.
```

---

## 3. Tokens

| Element | Size (DIP) | Padding / gap | Radius | Type style | Colour / brush | Material / elevation | Source |
|---|---|---|---|---|---|---|---|
| Docked card (Cap) | W = rail width; H = `DockedVideoHeight` | — | 0 (the rail clips) | — | `Fill = Tok.MediaLetterbox` (#000 α1) | **none** (content-layer rung) | `DockedVideoSurface.cs:330-335` |
| Docked card (PageStage) | `Grow = 1, MinHeight = 0` | — | 0 (the page clips) | — | `Fill = Tok.MediaLetterbox` | none | `DockedVideoSurface.cs:321` |
| Docked cap height, at rest | `clamp(railW × nH/nW, 120, 560)` | — | — | — | — | — | `ShellResponsiveLayout.cs:158-162` |
| Docked cap height, splitter range | `[railW × 9/16, 560]` | — | — | — | — | — | `ShellResponsiveLayout.cs:130-137` |
| Docked cap splitter | strip 16 | — | — | — | `ShowIndicator = false` | — | `Splitter.cs:22`, `RightRail.cs:246-252` |
| Docked chrome strip | H 30 | `Padding (8,0,8,0)`, `Gap = Spacing.XXS (2)` | 0 | — | `Gradient = Tok.ScrimTop` | — | `DockedVideoSurface.cs:449-459` |
| Docked chrome glyph | 24 × 24 | — | `Radii.Control (4)` | 11 DIP `Theme.IconFont` | `Tok.OnMediaSecondary` → hover `Tok.OnMediaPrimary`; `HoverFill = OnMediaPrimary α0.14`, `PressedFill = α0.22` | — | `DockedVideoSurface.cs:479-496` |
| Poster ground | `Grow = 1` | — | 0 | — | `Opacity = 0.4f` over `Surfaces.ArtworkFill(art, 0f)` | — | `DockedVideoSurface.cs:423-426` |
| Loading overlay | ring 20 | `Gap = Spacing.S (8)` | — | 12 / weight 600, NoWrap, 1 line | `Tok.TextOnAccentPrimary` (both ring and text) | `HitTestPassThrough` | `DockedVideoSurface.cs:428-441` |
| PiP surface | W 360 × H 202 default; min 240 × 135 | `Margin` 16 from every edge | `Radii.Card (8)` | — | `Fill = ColorF.Transparent`; `BorderWidth 1`, `BorderColor = Tok.StrokeCardDefault` | `Shadow = Elevation.Flyout` (dark 16/8/0 #00000042; light 16/8/0 #00000024) | `InWindowVideoPip.cs:53-59, 175-203` |
| PiP chrome strip | H 30 | `Padding (14, 6, 14, 0)` | `(8, 8, 0, 0)` | — | `Gradient = Tok.ScrimTop` | — | `InWindowVideoPip.cs:268-277` |
| PiP close | 24 × 24 | — | `Radii.Control (4)` | 11 DIP icon font | as docked glyph | — | `InWindowVideoPip.cs:239-263` |
| PiP resize bands | edge 6; corner 14 × 12 | — | — | — | unpainted | topmost layer, all non-band cells pass-through | `InWindowVideoPip.cs:58-59, 367-433` |
| PiP SE nub | 8 × 8 | `Margin (0,0,2,2)` | `(0, 0, 4, 0)` | — | `Tok.OnMediaTertiary` (white α0.60) | `HitTestVisible = false`, `Opacity 0 → Hover 1` | `InWindowVideoPip.cs:421-426` |
| PiP layout reserve | `_h + 16` | — | — | — | — | consumed by `ContentHost` bottom inset | `InWindowVideoPip.cs:136`, `ContentHost.cs:45` |
| Pop-out window | open 640 × 360; min client 320 × 180 | — | OS | — | root `Fill = Tok.MediaLetterbox` (always opaque) | `CustomFrame`; `AlwaysOnTop` default **true** | `VideoPlacementHost.cs:75-87` |
| Fullscreen exit band | H 56 | `Padding (20, 12, 12, 0)`, `Gap = Spacing.M (12)` | 0 | — | `Gradient = Tok.ScrimTop` | `HitTestPassThrough` positioner | `VideoFullscreenSurface.cs:311-318` |
| Fullscreen title | — | — | — | 15 / weight 600, 1 line, `CharacterEllipsis`, `Shrink = 1` | `Tok.OnMediaPrimary` | — | `VideoFullscreenSurface.cs:323-328` |
| Fullscreen exit FAB | 44 × 44 | — | `Radii.Circle(44)` | glyph 18 icon font | `Fill = StageInk.GlassPlate` (OnMediaPrimary α0.14) / hover α0.22 / pressed α0.28; `BorderColor = StageInk.Stroke`; glyph `StageInk.Ink` | `Shadow = Elevation.Card` (load-bearing: the one separation channel that survives an inverted ink ladder) | `StageChrome.cs:202-227` |
| **Engine player frame** (all four surfaces) | fills the surface | — | `Radii.OverlayAll (8)` — *not* 0 | — | `BorderWidth 1`, `BorderColor = Tok.StrokeFlyoutDefault` | none | `MediaPlayerElement.cs:154-160, 1050-1058` — see W25 |
| Engine transport overlay | ≈ 102 tall (14/34/14/8 pad + 24 seek + 2 gap + 34 controls; the two row heights are *estimated*, the padding and gap are verified) | `Padding (14,34,14,8)`, `Gap 2`; control row `Gap 6`, `Shrink 1`, `MinWidth 0` | — | — | `Gradient = Tok.ScrimBottom` | bottom-pinned ZStack overlay; reflows nothing | `MediaPlayerElement.cs:1372-1391` |
| Engine transport, compact | < 420 wide (exit hysteresis 460) | — | — | — | chips fold into ⋯ | — | `MediaPlayerElement.cs:96-98` |
| Engine caption lift | 28 above the video area's bottom with the chrome down | — | — | — | — | rises by the measured chrome height | `MediaPlayerElement.cs:91` |
| PiP content-fit ceiling | `h = clamp(w × ratio, 135, max(135, vpH − 72 − 32))` | — | — | — | — | the fit can never be taller than the free window height | `InWindowVideoPip.cs:158, 167` |
| Art\|Video toggle half | 24 × 24 | — | `Radii.Control (4)` | glyph 11 DIP `Theme.IconFont` | selected `Fill = OnMediaPrimary α0.16` + glyph `OnMediaPrimary`; unselected transparent + glyph `OnMediaSecondary` | no hover/press fill | `RightRail.cs:347-359` |
| Watch-page stage | `AspectRatio 16/9`, `MaxHeight 560` | — | page-owned | — | `Fill = Tok.MediaLetterbox` | **no** transition/opacity/stagger | `WatchPageView.cs:121-140` |
| Watch-page idle CTA | 64 | — | `Radii.Full` | — | `ButtonAppearance.Accent` | — | `WatchPageView.cs:67, 155-164` |
| B10 no-video placeholder | `AspectRatio 16/9` | `Padding = Spacing.S (8)` | `Radii.Card (8)` | 12 / 600, NoWrap, 1 line | art @0.4 over `Tok.MediaLetterbox`; text `Tok.TextOnAccentPrimary` | — | `VideoRailPanel.cs:111-135` |
| Manager flyout panel | W 420 | `Padding = Spacing.M (12)` all; kids `Gap = Spacing.S (8)` | — | — | popup chrome | `PopupChrome.Popup`, `FocusTrap`, `LightDismiss` | `VideoOverrideManagerFlyout.cs:37-64`, `SettingsPage.VideoOverrides.cs:192-196` |
| Manager search box | 396 × 32 | — | control | — | placeholder `videoOverride.searchPlaceholder` = "Search by track, artist or file name" | — | `VideoOverrideManagerFlyout.cs:78-84` |
| Manager compact row | MinH 44 | `Padding (8,4,8,4)`, `Gap 8`, list gap 2; title↔filename column `Gap 1` | `Radii.Control (4)` | Body / Caption | `Tok.TextPrimary` / `Tok.TextSecondary`; `Icons.Movie 16 TextSecondary` | `Interaction.Subtle` | `VideoOverrideManagerFlyout.cs:196-221` |
| Manager section label | — | `Margin (4,2,0,0)` | — | Caption (12 / lineHeight 16) weight 600, 1 line, ellipsis | `Tok.TextSecondary` | no uppercase transform — "Recently added" / "3 results" render sentence-case | `VideoOverrideManagerFlyout.cs:283-288`, `Typography.cs:39` |
| Manager empty state | — | `Padding 8` all, `Gap = Spacing.XS (4)` | — | BodyStrong + Caption (Wrap, MaxLines 4) | `Tok.TextPrimary` / `Tok.TextSecondary` | — | `VideoOverrideManagerFlyout.cs:262-274` |
| Manager row actions row | — | `Gap = Spacing.S (8)` | — | HyperlinkButton × 1-3 + `Button.Standard` "Remove video" | — | `Direction 0`, `AlignItems Center`, **`Wrap = true`** | `SettingsPage.VideoOverrides.cs:252-256` |
| Manager full row | — | `Padding 8` all, `Gap = Spacing.XS (4)`, list gap 4 | `Radii.Control (4)` | Body 600 + Caption (2 lines, wrap) | `Fill = Tok.FillSubtleSecondary`, highlighted `Tok.AccentSubtle` | — | `VideoOverrideManagerFlyout.cs:225-259` |
| Manager Browse-all row | MinH 40 | `Padding (8,4,8,4)`, `Gap 8` | `Radii.Control (4)` | Body + Caption | `Icons.Folder 16` / `Icons.ChevronRight 14`, `Tok.TextSecondary` | `Interaction.Subtle` | `VideoOverrideManagerFlyout.cs:117-135` |
| Manager scrollers | MaxHeight 280 (recent) / 380 (results) / 400 (leaf) | — | — | — | `ContentSized = true` | — | `VideoOverrideManagerFlyout.cs:99,104,159,187-191` |
| Manager divider | H 1 | — | — | — | `Tok.StrokeSurfaceDefault` | — | `VideoOverrideManagerFlyout.cs:290-294` |
| Status chip | — | `Padding (8,3,8,3)` | `Radii.Full` | 12 / weight 600 | Ok `TextSecondary`/`FillSubtleSecondary`; Missing & DriveOffline `SystemFillCaution`/`…CautionBackground`; Unplayable `SystemFillCritical`/`…CriticalBackground` | — | `SettingsPage.VideoOverrides.cs:303-316` |
| Manager flyout anchoring | — | — | — | — | `FlyoutPlacement.BottomEdgeAlignedLeft` off the Manage button, `ConstrainToRootBounds = true` | `FocusTrap`, `LightDismiss`, `PopupChrome.Popup`; re-clicking Manage closes it | `SettingsPage.VideoOverrides.cs:180-197` |
| Art\|Video toggle | H 24 | `Margin (0, ArtTop + 4, 12, 0)` | `Radii.Control (4)` | — | `Fill = WaveeOnMedia.GlassHover` (α0.10) | `ClipToBounds` | `RightRail.cs:325-340` |

---

## 4. Colour & material

Everything on a video surface is on the **on-media ink ladder**, not the theme ink ladder, and that is deliberate:
these pixels sit over a dark scrim over video in **both** themes, so a light-theme `Tok.TextSecondary` would be
invisible (`InWindowVideoPip.cs:243-245`). There is **no cover-palette derivation, no tint and no wash on any video
surface** — the video is its own colour.

| Input | Function (file:line) | Applied to | Transition |
|---|---|---|---|
| — | `Tok.MediaLetterbox` = opaque `#000000` (`fluent-gpu/src/FluentGpu.Engine/Dsl/Tokens.cs:372`) | the ground of every surface: the docked card, the PiP's poster state, the pop-out root, the fullscreen stage, the watch-page stage, the B10 placeholder | **static** — never animated. The `DestOut` punch erases it over the video rect, which is why an opaque root does not cause black video |
| — | `Tok.ScrimTop` = linear 90°, `#000 α153 → α51 @0.35 → transparent @1.0` (`Tokens.cs:385-390`) | the docked chrome strip, the PiP chrome strip, the fullscreen exit band | fades **with the band's own `Opacity`** (0 → 1 in 167 ms); never on an ancestor of a hole |
| — | `Tok.ScrimBottom` = `transparent → α76 @0.66 → α224 @1.0` (`Tokens.cs:377-383`) | the engine transport's own ground | engine-owned; reveal 150 ms, conceal 400 ms |
| `Tok.OnMediaPrimary` = white α1.0 | `Tokens.cs:361` | glyph hover colour, the fullscreen title, the FAB glyph | `BrushTransitionMs` on the FAB = `WaveeMotion.Faster (83)` |
| `Tok.OnMediaSecondary` = white α0.80 | `Tokens.cs:363` | chrome glyph rest colour (docked ✕/▣/⛶, PiP ✕) | hover swap, engine-timed |
| `Tok.OnMediaTertiary` = white α0.60 | `Tokens.cs:365` | the PiP's SE resize nub | rides the chrome's `HoverOpacity` cascade |
| `Tok.OnMediaPrimary × α0.14 / α0.22` | `DockedVideoSurface.cs:484-485`, `InWindowVideoPip.cs:246-247` | glyph hover / pressed fill | engine brush transition |
| `Tok.OnMediaPrimary × α0.10` | `WaveeOnMedia.GlassHover` (`Design/WaveeOnMedia.cs:83`) | the Art\|Video toggle's ground | static |
| `Tok.OnMediaPrimary × α0.16` | `RightRail.cs:349` | the Art\|Video toggle's **selected** half | static |
| `StageInk.GlassPlate / …Hover / …Pressed` — **theme-SPLIT**, not α-on-white | `Design/StageInk.cs:41-46` → `Design/StageArm.cs:60, 79-83, 96` (**not** `WaveeOnMedia.cs`) | the fullscreen exit FAB's ground, stroke and glyph | `BrushTransitionMs = 83` |
| track artwork | `Surfaces.ArtworkFill(art, 0f)` under `Opacity = 0.4f` | every poster ground | the element's own `PosterCrossFade` — 150 ms, `Easing.FluentStandard`, `ReducedMotionPolicy.KeepFade` (`MediaPlayerElement.cs:74-84`) |
| `Tok.StrokeCardDefault` | | the PiP's 1-DIP border | static (`Prop.Of`, so it follows a theme flip without a re-render) |
| `Elevation.Flyout` | `fluent-gpu/.../Elevation.cs:33-35` — dark `blur 16, y 8, #00000042`; light `blur 16, y 8, #00000024` | the PiP only | static |
| `Elevation.Card` | `Elevation.cs:18-21` — dark `blur 8, y 2, #00000033`; light `blur 4, y 2, #0000001A` | the fullscreen exit FAB only | static |
| roster status | `VideoOverrideUx.StatusOf` (`VideoOverrideUx.cs:148-157`) → `SettingsPage.VideoOverrides.cs:303-311` | the manager's chips | none |

**Light/dark differences — FIVE, not three.** The `Tok.OnMedia*` ladder and the two scrims really are theme-invariant
`static readonly`s (`Tokens.cs:355-390`), so the chrome strips, their glyphs, the PiP nub and the letterbox ground are
identical in both themes. But four more things move, and two of them were previously claimed not to:

1. `Elevation.Flyout` / `Elevation.Card` (both theme-split) — the PiP's shadow and the exit FAB's.
2. `Tok.StrokeCardDefault` — the PiP's 1-DIP border. Also `Tok.StrokeFlyoutDefault`, the engine player frame's
   hairline (W25), which is on every surface.
3. The roster's theme tokens (`TextPrimary`, `TextSecondary`, `FillSubtleSecondary`, `AccentSubtle`,
   `StrokeSurfaceDefault`, the `SystemFill*` pairs).
4. **`StageInk` is the app's one deliberate theme branch, and the fullscreen exit FAB is built entirely out of it.**
   `StageInk.X => StageArm.For(Tok.Theme).X` (`Design/StageInk.cs:16-19`). `StageArm.Ink` is
   `Dark ? WaveeOnMedia.Ink : Tok.MediaStage` (`StageArm.cs:60`), and `GlassPlate`/`Hover`/`Pressed` are that ink at
   α0.14 / 0.22 / 0.28 (`:79-83`), with `Stroke` likewise (`:96`). So in **dark** theme the FAB is the white plate the
   old text described; in **light** theme it inverts to a **near-black `#0A0A0A` plate with a near-black glyph and a
   near-black stroke** over undimmed video. That is a design decision (separation has to come from the polarity the
   stage is in), not a bug — but it means the fullscreen surface does **not** look identical in both themes, and a
   0.3 re-author who hard-codes `Tok.OnMediaPrimary α0.14` ships the dark arm into both.
5. **`Tok.TextOnAccentPrimary` — the loading overlay's ink — is theme-split, and its dark value is `#000000`.**
   `PaletteBuilder.BuildLight` sets it to `#FFFFFF` (`PaletteBuilder.cs:239`); `BuildDark` sets it to `#000000`
   (`:327`, the standard WinUI `TextOnAccentFillColorPrimary` pair, correct for text on a light-blue dark-theme accent
   fill). Wavee resolves `Tok.NeutralPalette` (`Design/WaveeTheme.cs:15`), so this is the live value.
   **Consequence: in Wavee's dark theme the 20-DIP `ProgressRing` and the "Loading…" label paint BLACK over
   40 %-dimmed artwork over `#000` letterbox — on all three poster surfaces and on the B10 no-video placeholder**
   (`DockedVideoSurface.cs:434-438`, `InWindowVideoPip.cs:352-356`, `VideoFullscreenSurface.cs:284-288`,
   `VideoRailPanel.cs:126-129`). This is a 0.2.9 legibility defect, not a style: the token means "ink that sits on an
   accent fill", and nothing here is an accent fill. **0.3 must use `Tok.OnMediaPrimary` (ring) and
   `Tok.OnMediaSecondary` or `OnMediaPrimary` (label)** — the theme-invariant on-media ladder everything else on these
   surfaces already uses. Fixing it is the second knowing divergence from 0.2.9 in this chapter (W14b is the first).

**Mica / acrylic: none.** No video surface participates in the shell material. The design intent documents
`docked-video-mica.html` / `docked-video-snap-mica.html` show the card sitting *on* the Mica-tinted window, which it
does, but the card itself paints opaque `#000` — anything translucent behind the video rect is either erased by the
punch or (worse, if painted after it) paints over the video.

---

## 5. Motion

Every animation below is engine-scheduled and therefore samples the engine present clock (`FrameClock.PresentQpc`);
none of this code reads `Environment.TickCount64`. The one clock-adjacent value is
`MediaPlayerElement`'s idle timer (`UseTimeout(_onWake, …)`, engine `HostTimerQueue`) — also frame-clock driven.

| Trigger | Target | Property | From → to | Duration | Easing | Delay / stagger | Reduced motion | Source |
|---|---|---|---|---|---|---|---|---|
| Pointer enters the docked card (anywhere in its subtree) | chrome strip | `Opacity` | 0 → 1 | `ChromeFadeMs = WaveeMotion.Fast = 167` | `Easing.FluentDecelerate` (cubic-bezier 0.1, 0.9, 0.2, 1.0) | none | engine hover cascade honours `ReducedSnap` | `DockedVideoSurface.cs:104, 456-458` |
| Pointer leaves | chrome strip | `Opacity` | 1 → 0 | 167 | FluentDecelerate | none | as above | same |
| Pointer enters the PiP card | chrome strip **and** the SE nub, together | `Opacity` | 0 → 1 | 167 | FluentDecelerate | none | as above | `InWindowVideoPip.cs:60, 274-275, 417-418` |
| Pointer enters the fullscreen stage | exit band | `Opacity` | 0 → 1 | `WaveeMotion.Fast = 167` | FluentDecelerate | none | as above | `VideoFullscreenSurface.cs:317-318` |
| PiP mount (placement resolves to Floating) | the surface node | `Opacity` **and** the Enter terminal's scale | `Sx/Sy 0.94, Opacity 0` → `1, 1` | 240 | `Easing.SmoothOut` (0.22, 1.0, 0.36, 1.0) | none | `AnimScheduler.ReducedSnap`: **the scale snaps, the fade still runs** | `InWindowVideoPip.cs:67-72` |
| PiP unmount | the surface node | same channels | `1` → `Sx/Sy 0.96, Opacity 0` | 140 | `Easing.EaseInOut` | none | as above | same |
| Fullscreen enter (`Flow.Show` mounts the layer) | the layer | **scale only — NO opacity** | `Sx/Sy 1.03` → `1.0` | engine `Flow.Show` terminal default | engine default | none | `Motion.ReducedMotion` ⇒ `default` terminal = a **hard cut**, not a same-value animation | `VideoFullscreenSurface.cs:65-67`, `WaveeShell.cs:1470` |
| Fullscreen exit | the layer | scale only | `1.0` → `Sx/Sy 1.02` | engine default | engine default | none | hard cut under reduced motion | `VideoFullscreenSurface.cs:70-72`, `WaveeShell.cs:1471` |
| First decoded frame / readiness flip | the element's `media-poster` node | `Opacity` | 1 → 0 (or 0 → 1) | `PosterCrossFadeMs = 150` | `Easing.FluentStandard` (0.8, 0, 0.2, 1.0) | seeded via `AnimEngine.SeedEased`; a **remounted** element never fades in over already-decoded video | `ReducedMotionPolicy.KeepFade` — this fade **keeps running**, because it is what tells the user whether they are looking at the poster or the live frame | `MediaPlayerElement.cs:74-84, 974-990` |
| Source switch (video→video skip) | the stage | **nothing app-side** | — | — | — | — | — | the stage stays mounted (`Key = "gen:N"`); the engine holds the previous frame and cross-fades to the new source's first frame under the poster (`DockedVideoSurface.cs:356-358, 395-398`) |
| Playback advancing + pointer idle | the engine transport | `Opacity` | 1 → 0 | `MediaChromeFadeOutMs = 400` | ease-out | after `MediaChromeIdleDelayMs = 3000` (mouse) / 4000 (touch, keyboard) | engine-owned | `fluent-gpu/.../MotionTok.cs:137-156` |
| Pointer move ≥ 3 DIP from rest / focus / key | the engine transport | `Opacity` | 0 → 1 | `MediaChromeFadeInMs = 150` | FluentDecelerate | none | engine-owned | `MotionTok.cs:145-159` |
| Pointer leaves the player | the engine transport | `Opacity` | 1 → 0 | 400 | ease-out | after `MediaChromeLeaveHideMs = 150` | engine-owned | `MotionTok.cs:143-146` |
| Rail slide (the rail opens/closes with a docked card in it) | the RAIL, not the card | `TranslateX` | — | 300 | rail-owned | none | rail-owned | the card rides it **for free** — a translate composes on the `AbsoluteRect` the punch already reads from; nobody animates the hole (`DockedVideoSurface.cs:77-80`) |
| Drag / resize of the PiP | `Transform` / `Width` / `Height` | translation + size | continuous | **none — direct, per pointer sample** | — | — | unaffected | `InWindowVideoPip.cs:179-187, 452-514` — `Channels = Opacity` **only** on `SurfaceMotion` so the node is never `BoundsAnimated` and a live gesture is never FLIP-chased (`:64-66`) |
| Docked splitter drag | the cap's `Height` signal | height | continuous | none | — | — | unaffected | `RightRail.cs:246-252` |
| Drill into the manager's browse-all leaf | the keyed body | `MotionRecipes.PageSlideForward` | engine recipe | engine | engine | none | engine | `VideoOverrideManagerFlyout.cs:52-58` |
| Back out of the leaf | the keyed body | `MotionRecipes.PageSlideBack` | engine recipe | engine | engine | none | engine | same |
| Player-bar video button appears/disappears | the split-button slot | `Opacity` only (the slot's **width is unconditional**) | 0 ↔ 1 | `ItemMotion` | engine | none | engine | `PlayerBar.cs:563-568` — gating the MOUNT on `hasVideo` slid every sibling and shrank the seek bar under the user's eye. The slot itself is reserved only while `active && showQueue`; below that the ladder moves into the narrow-layout overflow's `AppBarCommand.Flyout` (`:591-595`) |
| Hover / press the fullscreen exit FAB | the FAB node | `Scale` | 1 → `1.07` hover, `0.92` press | engine scale channel | engine | none | `ScaleTier.Hover/Press` return **1f** under `Motion.ReducedMotion` — the scale is dropped as a VALUE, no branch at the call site | `StageChrome.cs:214`, `WaveeMotion.cs:48, 179-182` |
| Hover / press the fullscreen exit FAB | the FAB's fills + glyph | `Fill` / `Color` | `GlassPlate → …Hover → …Pressed` | `BrushTransitionMs = WaveeMotion.Faster (83)` | engine brush transition | none | engine | `StageChrome.cs:208-213` |
| Stage mounted, first frame not yet decoded | the engine's own status overlay | nothing is drawn at all for the first **500 ms** (`StartupSpinnerDelayMs`); a DETERMINATE readout only past **10 000 ms** (`StartupDetailDelayMs`) | — | — | — | 500 / 10 000 ms thresholds | engine-owned | `MediaPlayerElement.cs:86-89` — "a spinner that flashes for 200 ms is worse than no spinner". The app's own `LoadingOverlay` (no player at all) has **no** such delay and appears on the first frame; the two must not be confused when frame-differencing a parity capture |
| Splitter thumb reveal | the splitter's 2-DIP indicator | `Opacity` | 0 → 1 | `Motion.ControlFast` | engine | none | engine | **not applicable here** — the docked cap passes `ShowIndicator = false`, so the thumb is never built (`Splitter.cs:26-30`, `RightRail.cs:246-252`) |

**Not present, on purpose:** a `LayoutTransition` on the docked card or the watch-page stage; any opacity, blur,
edge-fade or stagger on an ancestor of a hole; a corner-snap or drop-target animation for the PiP (see §9).

---

## 6. Interaction

### Docked card (both faces)

| Input | Result |
|---|---|
| Hover anywhere in the card's subtree | chrome strip fades in (167 ms); the engine transport also reveals on its own timer |
| Click the picture | the element's own click handling (play/pause per `MediaPlayerElement`) |
| Double-click the picture | fullscreen — the element's `ToggleFullscreen` prefers `FullscreenRequested`, which the card wires to `ShowVideoAt(Fullscreen)` (`DockedVideoSurface.cs:390`) |
| **Space** (card focused) | play/pause — mirrors `MediaPlayerElement.HandleKey`. **Escape is deliberately NOT mirrored**: that arm only fires `when IsFullscreenPresentation`, which this face never is (`DockedVideoSurface.cs:299-308`) |
| **F11 / F** (inside the player) | fullscreen, through `FullscreenRequested` |
| Right-click / Menu key on the picture | the element's More menu — the app's placement rows **first** (`VideoPlacementMenu.Items(includeFullscreen: false)`) then the element's own playback rows (Aspect ratio, playback speed, quality, CC) |
| Click ▣ (`Icons.BackToWindow`) | `Announcer.Say(player.videoMiniPlayer)` then `ShowVideoAt(Floating)`. Tooltip: `player.videoMiniPlayer` = "Play in a mini player" |
| Click ⛶ (`Icons.FullScreen`) | `Announcer.Say(player.videoFullScreen)` then `ShowVideoAt(Fullscreen)`. Tooltip: `player.videoFullScreen` = "Full screen" |
| Click ✕ (`Icons.Cancel`) | `Announcer.Say(player.turnOffVideo)` then `NotifyVideoSurfaceClosed(Docked)` — **never** `TurnVideoOff`. Tooltip: `player.turnOffVideo` = "Turn off video" |
| Drag the splitter on the cap's bottom 16 DIP | resize the cap between `railW × 9/16` and 560; commit clamps, **pins** (`DockedVideoHeightPinned = true`) and persists |
| Drag the picture | **nothing** — `DragMovesWindow` is false for every in-window surface (`VideoStageInput.cs:21-22`); moving the main window from a card inside a scroller would also steal touch pans |

Focus: the card is `Focusable = true`; each glyph is `Focusable = true, AllowFocusOnInteraction = false`
(`Role = AutomationRole.Button`, `Cursor = CursorId.Hand`), so a click never leaves a focus ring on a glyph but Tab
still reaches it. Accessibility names come from `ToolTip.Wrap` — the tooltip **is** the name of record, and it names
the control's *destination*, not a bare verb.

### In-window PiP

| Input | Result |
|---|---|
| Hover | chrome strip + SE nub fade in together |
| Press + drag the top strip (`Cursor.SizeAll`) | move. `OnPointerDown` commits the drawn position and sets `_placed = true` (⇒ the layout reservation is released); each move reconstructs the true window-space pointer as `local + scene.AbsoluteRect(_dragNode)`, because the grip moves **with** the surface. Eager pointer capture keeps the gesture alive outside the band |
| Release / cancel the drag | `PersistGeometry()` — an `OnDrag` node's `OnClick` **is** its release edge, so one settings write per gesture, not per sample. Only a `_placed` surface is written |
| Press + drag any of the eight bands | resize (cursors: `SizeNWSE`, `SizeNS`, `SizeNESW`, `SizeWE`). Sets `_placed` **and** `_sized`. The anchored edge is the one not named by the flags |
| Click ✕ | `NotifyVideoSurfaceClosed(Floating)` ⇒ sticky off (`HostClosed` on a non-Detached placement is `TurnOff`) |
| Right-click the picture | the shared stage's More menu — placement rows + element rows |
| Double-click the picture | fullscreen via `ShowVideoAt(Fullscreen)` |
| Click on the card anywhere | absorbed (the card is a hover container with a pointer handler), so clicks do **not** fall through the pass-through layer onto the page |
| Window resize while anchored | the card tracks the bottom-right corner live (the `Transform` thunk subscribes to `Viewport.Size`) |
| Window resize while placed | the card is re-clamped into the new bounds but keeps its position |

### Pop-out window

| Input | Result |
|---|---|
| Drag the **picture** | moves the window through the OS move loop — Aero Snap, the snap bar and monitor hops included (`VideoStageInput.DragMovesWindow(PopOut, hostFullscreen: false)`). A click is still a click, a double-click still fullscreen, a right-click still the menu |
| Pointer idles | the cursor hides with the chrome (`CursorAutoHidePolicy.Always` — mpv's windowed default) |
| Double-click / F11 / F / the transport's ⛶ / the ⋯ Full-screen row | toggles **`DetachedFullscreen`**, i.e. this window borderless on its own monitor. Never `ShowVideoAt(Fullscreen)` |
| Escape while fullscreen | leaves (the element's `case Keys.Escape when PresentingFullscreen`, reachable because `IsHostFullscreen` is in the stage key) |
| Right-click | placement rows + element rows, plus **Always on top** (a checkable item, applied live) |
| ⋯ → Turn off video / Alt+F4 / Alt+Space → Close / the OS ✕ | `IDetachedVideoWindow.OnClosed` → identity guard → `DetachedFullscreen = false` → `SetVideoSurfaceLive(false)` → `NotifyVideoSurfaceClosed(Detached)` ⇒ **falls back to the mini player**, not off |
| Drag / resize the window | the host debounces and reports settled geometry; persisted to `video.window.rect` **unless** fullscreen |

There is no window chrome of ours: `CustomFrame` keeps only the OS resize borders. The OS title (taskbar, Alt+Tab) is
the **current track's title**, updated live, falling back to `player.nowPlaying` = "Now playing".

### Fullscreen surface

| Input | Result |
|---|---|
| Mount | real OS borderless fullscreen on the monitor the window sits on; the shell **unmounts** the title bar and the player bar; a focus **scope** is pushed at the root (Tab cannot walk out into the now-unmounted chrome); focus parks on the first focusable **inside the video** (so Space / ←→ / F11 / F drive playback immediately) — but only when `UserInitiated` |
| **Escape** | `ExitVideoFullscreen()`. Handled at the fullscreen **root**, the common ancestor of everything focusable, so no focused control can swallow it. It never enters fullscreen and never quits the app |
| **F** | same (the media-player convention; this arm covers focus sitting on the chrome rather than inside the player) |
| **Shift+Escape / Ctrl+F / Alt+F / any modified Escape or F** | **nothing.** The root handler returns early on `e.Handled` or on any of `Ctrl \| Alt \| Shift` (`VideoFullscreenSurface.cs:162`) before it ever looks at the key code. Deliberate — a modified key belongs to whatever else claims it — but it means "Escape always exits" is true only for *unmodified* Escape, and a parity test that holds Shift will read as a regression |
| **F11** | handled inside the element, delegated to `ExitVideoFullscreen` via `FullscreenRequested` |
| Double-click the picture | exit (same delegate) |
| Click the exit FAB | `ExitVideoFullscreen()`. Tooltip: `player.videoExitFullScreen` = "Exit full screen" |
| Click anywhere else (letterbox bars, empty space) | the **Shield** takes the click and re-parks focus on the root — it never falls through to the page underneath. It is the FIRST child, because `InputDispatcher.Hit` keeps the LAST matching child |
| Focus lost to nothing | `OnFocusChanged` re-parks focus on the root — never leave focus null while the surface is up |
| Unmount (any route) | the remembered OS fullscreen state is restored; the focus scope is popped; focus is handed back to whatever invoked fullscreen |

### Placement menu (exact rows, order, icons, conditions)

```
1  Dock in rail             Icons.SplitView    E8A0   radio, ShowVideoAt(Docked)
                            disabled ⇒ AcceleratorText = player.videoNeedsWiderWindow ("Needs a wider window")
2  Play in a mini player    Icons.BackToWindow E73F   radio, ShowVideoAt(Floating)   — always available
3  Play in a separate window Icons.Movie       E8B2   radio, ShowVideoAt(Detached)
                            disabled ⇒ AcceleratorText = player.videoNoSecondWindow ("Not available on this display setup")
4  Full screen              Icons.FullScreen   E740   radio, ShowVideoAt(Fullscreen)  — ONLY when includeFullscreen
                            enabled  ⇒ AcceleratorText = "F11"
                            disabled ⇒ AcceleratorText = player.videoNoFullscreen ("Not available here")
—  Separator                                          only when resolved == Detached && settings != null
5  Always on top            (no icon)                 toggle, checked from video.window.ontop
—  Separator                                          only when PlacementCore.IsActive(state)
6  Turn off video           Icons.Cancel       E711   TurnVideoOff
```

Enabled-ness comes from `PlacementCore.Allows(state.Available, p)`; checked-ness from `now == p` where
`now = PlacementCore.Resolve(state)`. The three call sites (player-bar chevron, narrow-layout overflow
`AppBarCommand.Flyout`, every surface's ⋯) share this one builder so they cannot drift.

**Known label wart** (`VideoPlacementMenu.cs:33-37`): on the fullscreen surface the element's *own* Fullscreen row
exits correctly but still reads "Full screen" and draws the enter glyph, because
`MediaPlayerElement.IsFullscreenPresentation` is `internal` to `FluentGpu.Controls` and Wavee is not an
`InternalsVisibleTo` friend. Fixing it needs a public knob on the element. **In 0.3 this is an engine change, and it
should be made** — do not paper over it app-side.

### Override manager flyout

| Input | Result |
|---|---|
| Type in the search box | `_query` drives `VideoOverrideUx.Search` → `RootSection`. The box **stays mounted** across every section swap (same key, same slot) so retyping never steals focus or drops a keystroke |
| Click a compact "Recently added" row | `Drill(row.Uri)` — leaf, slide forward, that row highlighted (`Tok.AccentSubtle`) |
| Click "Browse all…" | `Drill(null)` — leaf, slide forward, nothing highlighted |
| Click ‹ | `_forward = false`, clear focus, back to root (slide back) |
| Row → **Replace video file…** | file picker titled `videoOverride.pickTitle`, filter `("MP4 video", "*.mp4")`, then `VideoOverrideUx.Validate(path, File.Exists)`; a rejection raises an **error toast** (`rejectedNotMp4` "Only .mp4 files can be attached." / `rejectedNotFound` "That file couldn’t be found.") and logs `override.attach.rejected`; success → `curation.Attach` + `videoOverride.replaced` success toast |
| Row → **Locate video file…** (only when `Status == Missing`) | picker starting at `VideoOverrideUx.NearestExistingAncestor(path, Directory.Exists)` — the deepest surviving ancestor, the Lightroom repair pattern. That folder is also **spelled into the dialog caption**: `locateTitle + " — " + start` (`SettingsPage.VideoOverrides.cs:262-266`) |
| Row → **Show in Explorer** (only when `Status` is Ok or Unplayable) | `ShellOpen.RevealInExplorer(path)` |
| Row → **Remove video** | applies **immediately** with a toast-undo (`videoOverride.removed` + `videoOverride.undo` → `curation.Attach(uri, path)`), never a dialog. A failed re-attach surfaces as an error toast |
| Settings card → Remove all | `ConfirmThen(clearAll, clearAllBody, clearAll, …)` → `ClearAllVideoOverrides`: every `Remove`, logged as `override.settings.clear_all` with a count, then the `clearedAll` success toast. **No undo for the bulk path** — unlike a single row (`SettingsPage.VideoOverrides.cs:156-160, 290-299`; the card itself is chapter 27) |
| Deep link → Manage | `PlaybackBridge.OpenVideoOverrides` (a missing/unplayable toast's action) lands on the Playback tab **and** opens the flyout. The open is deferred because the Manage button is not realized on the frame the tab flips — whichever of the posted retry or the button's `OnRealized` gets a live anchor first wins, once (`:200-216`). 0.3 needs the same two-way race, not a single `post()` |
| Light-dismiss / re-click Manage | closes (the toggle contract every anchored surface uses) |

`DriveOffline` deliberately offers **no** repair CTA: the volume heals by itself when the drive returns, so the copy
must never suggest removing the link (`VideoOverrideUx.cs:23-29`).

---

## 7. Data & readiness in 0.3 terms

| Visual element | 0.2.9 data source | 0.3 read | Readiness predicate |
|---|---|---|---|
| Card mounts at all | `PlaybackBridge.VideoPlacementNow()` | `Playback.VideoSurface` signal → `Video.Resolve(state)` (CORE) | none — a placement value is always defined; `None` ⇒ nothing mounts |
| Which docked face | `ShellUi.ActiveStagePlayable` + `CurrentTrack.Uri` | `Shell.ActiveStagePlayable : Signal<StringId>` + `Playback.Current` handle's `Uri.Full` — compare `StringId`s (ordinal by construction) | both present and equal; empty ⇒ the rail hosts |
| Cap height at rest | `PopOutVideoSource.NaturalWidth/Height`, then `MediaPlayer.NaturalSize` | **DATA GAP 1** (below) + the engine player's `NaturalSize` signal | never gated — 16:9 is the pre-report answer so the tile never flashes at a wrong shape |
| Poster art | `CurrentTrack.Image` | `Playback.Current.Image` (`StringId`, `TrackFields.Image`) | `t.Knows(TrackFields.Image)`; unknown ⇒ the bare letterbox fill, **never** a skeleton (a poster is already the fallback) |
| Is the source a LIVE broadcast (the LIVE chip / Go live / the DVR rail — W24) | `PopOutVideoSource.IsLive`, set by the resolving module, opened as `SourceLiveness.Live` | part of **DATA GAP 1**; propose `TrackFlags.LiveVideo` so a re-watch opens live-shaped before the manifest lands | never gated — a wrong guess here is what rendered a six-hour broadcast as `0:03 / -3:22`, so the default must be "finite" and the flip must come from the source, never from MF's `GetDuration` |
| Fullscreen title | `CurrentTrack.Title` | `Playback.Current.TitleId` (`TrackFields.Title`) | `t.Knows(TrackFields.Title)`; unknown ⇒ empty string (the band still shows the exit FAB) |
| Pop-out window title | `CurrentTrack.Title` | same | unknown ⇒ `player.nowPlaying` |
| "This track has a video" (the player-bar slot, the B10 placeholder, the availability set) | `VideoPresence.HasVideo(uri)` — three planes OR-ed: the store's kind-99 `VideoAssociation`, the user's override, the module's `form: video` | `Track.HasVideo` flag + `TrackFields.Video` (plan §4.2 already has both) — **but** see DATA GAP 2, the plan's flag cannot express all three planes | `t.Knows(TrackFields.Video)`. **Derived fact, lives on the model** — the UI must never probe a service. Until known: the player-bar slot reserves its width at `Opacity 0` (never a layout pop) |
| Resolved playable source | `PlaybackBridge.PopOutVideoSource : Signal<PopOutVideoSource?>` | **DATA GAP 1** | a `null` source **never** unmounts the stage (`VideoSurfaceMount.ShouldMountPlayerStage(playerPresent)`) |
| The player itself | `PlaybackBridge.VideoPlayer : Signal<VideoPlayerBinding>` | `Playback.Video.Player : Signal<(MediaPlayer?, long Generation)>` in `Playback/Playback.Video.cs` (Wave 3, owner H) | player present ⇒ stage; else poster + spinner |
| Transport ownership | `PlaybackBridge.TransportOwnerNow` (derived projection) | `Video.TransportOwnerFor(Video.Resolve(state))` — a derived read, never stored | — |
| Availability set | `VideoUpgradeGate.AvailabilityFor(hasVideo, HostPlacementCapability)` | same function; `hostCapable` from `Shell` (rail fit OR page stage) ∧ `Platform` (can a second window open? does the fullscreen hook exist?) | — |
| Rail width / docked height | `ShellUi.RailWidth`, `ShellUi.DockedVideoHeight`, `…Pinned` | `Shell.RailWidth`, `Shell.DockedVideoHeight`, `Shell.DockedVideoHeightPinned` — plain signals on the shell state object | — |
| Manager roster rows | `VideoOverrideService.All()` + `Decide(uri)` + `IStore.GetTrack(uri)` | **DATA GAP 3** | rows resolved **once per load**, never per frame; a cold load shows a spinner, a live re-load keeps the last roster (swapping it for a spinner would destroy the anchor the open flyout hangs off) |
| Manager row title / subtitle | `VideoOverrideUx.TitleFor/SubtitleFor(uri, Track?)` | `Track(uri).TitleId` / the artist line; **fall back to the raw uri** when the store has never seen the playable — a device-wide roster outlives any one account's catalog | unknown title ⇒ show the uri. Honest, not blank |

**Pages demand their whole model on mount.** None of these surfaces fetches anything: the docked card, the PiP, the
pop-out and the fullscreen surface are *presenters* of one player and one resolved source, both produced by the
playback host. The watch page demands its own model (chapter 09). Nothing here manages a visible window.

### DATA GAPS

| Element | What it needs | 0.2.9 source | Proposed column / edge |
|---|---|---|---|
| **1. The resolved video source** — decides *what plays*, seeds the card's aspect at mount, and is the stage's content identity | `Key` (string), `ClearUrl`, `FilePath`, `DrmDescriptor` (PlayReady DASH descriptor), `LicenseRelay` (a delegate), `LicenseServerUri`, `NaturalWidth`, `NaturalHeight`, `IsLive` | `SpotifyLive/PopOutVideoSource.cs:15-64`, published on `PlaybackBridge.PopOutVideoSource` by `SpotifyVideoManifestResolver` / `CompositeVideoResolver` | **Not a column.** It carries a live delegate and a parsed DRM descriptor — it is *session* state, not entity state. Keep it as a record on the playback host: `Playback.Video.Source : Signal<VideoSource?>` in `Playback/Playback.Video.cs`. **But** `NaturalWidth`/`NaturalHeight`/`IsLive` are per-*playable* facts worth caching: add `Column<ushort> VideoW, VideoH` and a `TrackFlags.LiveVideo` bit to `TrackTable`, filled under `TrackFields.Video`, so a re-watch sizes the card correctly before the manifest round-trip |
| **2. Video presence is THREE planes, the plan's flag is one** | Spotify's kind-99 association verdict (+ its `videoGidHex`, which the Connect publisher needs), the user's local attachment, and a module's own `form: video` resolve | `App/VideoOverrideUx.cs:341-381` (`VideoPresence`), `IStore.GetVideoAssociation(uri)`, `Backend/Modules/ModulePlayables.HasVideo`, `App/VideoUpgradeGate.cs:13-31` (`ConnectVideoFacts`) | §4.2 has `VideoCounterpart` (slot) + flag `HasVideo`. Keep both and add **`Column<byte> VideoOrigin`** (0 none, 1 association, 2 user override, 3 module) + **`Column<StringId> VideoGid`** under `TrackFields.Video`. `HasVideo` must be the OR, computed **at commit** (P11), never per frame: the row path is a single boolean read today and must stay one |
| **3. The override roster** — a device-wide, account-independent list of uri → local file | `VideoOverride { Uri, Path, AddedAtUnix, SourceKey }`, plus a per-session quarantine set and a `video-overrides` store sentinel | `Wavee.Backend.VideoOverride` + `VideoOverrideService` (roster, `Decide(uri)` tier walk, `Has`, `Attach`, `Remove`, `All`) | **Not entity state** (it survives scope switches and accounts, which `Scope` does not). Propose `Platform/Platform.cs` → `Platform.VideoOverrides`: a `Dictionary<StringId,(StringId Path,int AddedAt)>` + an epoch `Signal<int>`, persisted in the app-settings store, with the tier walk (`UseOverride` / `Broken` / `Quarantined`) as a CORE function in `Shell/Video.cs`. The **status** split (`Missing` vs `DriveOffline`) stays `VideoOverrideUx.StatusOf`, ported verbatim |
| **4. Placement persistence keys** | `video.placement` (a NAME), `video.pip.rect` (window DIP `"x,y,w,h"`), `video.window.rect` (screen px), `video.window.ontop` (bool, default **true**), `video.aspect.mode` (stable name), `video.aspect.customRatio` (double), `shell.rail.docked-video.height` (float) | `Platform/AppSettings.cs:152, 195-206` | Keep every key **byte-identical** so an in-place 0.3 upgrade does not lose where the user likes to watch. `PlacementPersistence` + `VideoAspectPersistence` port verbatim into `Shell/Video.cs` |
| **5. Host placement capability** | `railFits` (viewport ∧ sidebar ∧ rail width), `pageStageWouldHost`, `CanOpenDetachedWindow()`, `WindowSetFullscreen is not null` | `WaveeShell.cs:696-727` | `Shell.HostPlacementCapability : Signal<PlacementSet>`, written from exactly one effect in `Shell/Shell.Host.cs`. Conservative seed: `Docked | Floating` — an unwired host must degrade, never offer a placement it cannot honour |
| **6. `ActiveStagePlayable`** — the parked-page discriminator | the PLAYABLE uri the attached module page would stage, `""` when nothing | `ShellUi.cs:58`, written by `ModulePage` (gated on `UseIsActive`) and cleared by `ContentHost` on any non-module route | `Shell.ActiveStagePlayable : Signal<StringId>` with `StringId.Empty` as the resting value. **One id space, and it is the PLAYABLE uri** — the 0.2.9 bug where this carried the page *entity* uri cost a full debugging cycle and no pixel or test could show it |

---

## 8. Pure rules to port verbatim

These are `System`-only today (no `Signal<T>`, no FluentGpu type) and are **ported, never re-derived**.

| Name | 0.2.9 file | Decides | Tests | 0.3 destination |
|---|---|---|---|---|
| `PlacementCore` | `App/PlacementCore.cs:230-543` | the whole placement state machine: `Resolve`, `FirstAvailable` (ladder walk down-then-up; Fullscreen falls to the **cheapest**), `TogglePrimary` (symmetric ⇒ the toggle cannot stick), `OpenAt`, `TurnOff`, `Demote`, `HostClosed`, `Enter/ExitFullscreen`, `LiveAfterReport` (scoped: a surface may claim `Live` for itself and release it only if it still holds it), `TransportOwnerFor`, `SingleTransportInvariant`, `DecideMount`/`DecideOwned`, `IsCurrentGeneration`, and the `Apply`/`Invariant` data-driven driver | `src/apps/Wavee.Tests/PlacementCoreTests.cs` (915 lines, incl. arbitrary-command-sequence property tests over `Apply` + `Invariant`) | `Shell/Video.cs` — CORE section **`// ── placement state machine`** |
| `SurfacePlacement`, `PlacementSet`, `TransportOwner`, `PlacementPolicy`, `PlacementState`, `MountAction`, `PlacementCommand(Kind)` | `App/PlacementCore.cs:1-155` | the vocabulary — commitment order is load-bearing (the ladder walks it) | same | same file, same section |
| `PlacementPersistence` | `App/PlacementCore.cs:164-217` | `SavePlacement`/`LoadPlacement` (a **name**, not the enum number — a future reorder would silently reinterpret every saved preference), `SaveRect`/`TryLoadRect` (rounded ints; degenerate ⇒ false) | `PlacementCoreTests.cs` | `Shell/Video.cs` — CORE **`// ── persistence codecs`** |
| `DockedVideoHosting` + `DockedVideoFace` + `DockedVideoHost` | `App/DockedVideoHosting.cs:18-168` | `ShouldMount` (the ONE gate every docked mount site calls: ≤1 face true, exactly 1 iff Docked resolved), `PageStageHosts` (ordinal, playable-uri id space), `HostFor` (the rail is the resting owner), `HostOf`, `DockedHostAvailable` (rail-fits **OR** page-stage — two suppliers, one OR) | `src/apps/Wavee.Tests/DockedVideoHostingTests.cs` (298) | `Shell/Video.cs` — CORE **`// ── docked host arbitration`** |
| `RailVideoCoupling` + `RailMode` | `App/RailVideoCoupling.cs:8-83` | `ModeOnDock`, `OnRailClosed`, `ReDockOnRailOpen`, `CloseRailOnVideoLeft` (takes the host that **was**), `BodyModeFor` (a render-time substitution, never a write to the mode) | `src/apps/Wavee.Tests/RailVideoCouplingTests.cs` (137) | `Shell/Video.cs` — CORE **`// ── rail ↔ dock coupling`** (the `RailMode` enum itself belongs with `Rail.UI.cs`'s state; keep it `System`-only wherever it lands) |
| `VideoUpgradeGate` + `ConnectVideoFacts` | `App/VideoUpgradeGate.cs:13-76` | `AvailabilityFor`, `FoldAvailability`, `DeferUpgrade` (**no mid-track auto-swap** — the badge lights, playback stays put), `PrimaryClick` (the click COMMITS exactly what `DeferUpgrade` withheld, so the first click never reads as "turn it off"); `ConnectVideoFacts.Observe` folds a badge-only land into exactly one extra PutState | `PlacementCoreTests.cs` (`VideoUpgradeGate` section) | `Shell/Video.cs` — CORE **`// ── availability + the no-mid-track-swap rule`** |
| `VideoStageInput` | `App/VideoStageInput.cs:12-30` | `DragMovesWindow(identity, hostFullscreen)` = PopOut ∧ !fullscreen; `HidesCursorWindowed(identity)` = PopOut. Returns plain bools so the test assembly stays FluentGpu-free | `src/apps/Wavee.Tests/VideoStageInputTests.cs` (36) | `Shell/Video.cs` — CORE **`// ── stage input affordances`** |
| `DetachedFullscreenRule` | `App/DetachedFullscreenRule.cs:22-31` | `After(current, resolved)` = keep the pop-out's fullscreen bit iff still Detached — evaluated at the **single** placement write path, so "closed while fullscreen, reopened fullscreen" is unrepresentable rather than fixed | `src/apps/Wavee.Tests/DetachedFullscreenRuleTests.cs` | `Shell/Video.cs` — CORE **`// ── pop-out fullscreen`** |
| `VideoSurfaceMount` | `App/VideoSurfaceMount.cs:8-13` | `ShouldMountPlayerStage(playerPresent)` — a briefly-null source must **not** tear down the only MF pump | `src/apps/Wavee.Tests/VideoOverrideMutationCoreTests.cs` | `Shell/Video.cs` — CORE, same section as the arbitration |
| `VideoAspectPersistence` + `VideoAspectPreference` | `App/VideoAspectPersistence.cs:7-35` | stable **names** for the aspect policy (the persisted data must survive an engine enum reorder); `LoadRatio` bounds (0.01, 100) with `DefaultCustomRatio = 16/9` | `src/apps/Wavee.Tests/VideoAspectPersistenceTests.cs` (51) | `Shell/Video.cs` — CORE **`// ── persistence codecs`** |
| `VideoOverrideUx` + `VideoOverrideStatus` / `VideoAttachRejection` / `VideoMenuItems` / `VideoOverrideRow` / `VideoManagerSection` | `App/VideoOverrideUx.cs:17-332` | `RecentCount = 4`, `RecencyOrder` (newest first, ties by uri so the list can never shuffle), `Extension = ".mp4"`, `PickerFilter`/`PlayableFilter`/**`IsAudioFile`** (delegated to `LocalFileMediaProvider.IsSupportedAudioFile` so a surface can never accept what the resolver would refuse), `IsMp4`, `Validate`, `FirstMp4`, `StatusOf` (the Broken tier splits into Missing vs DriveOffline **here and only here**; a **rootless/relative** path has no volume to be offline and degrades to `Missing` — `:159-169`), `NearestExistingAncestor`, `MenuFor`, `BuildRoster`, `RecentlyAdded`, `IsSearching` (whitespace is not a query), `Matches` (title/subtitle/filename — **not** the path), `Search`, `RootSection`, `ShowsBrowseAll`, `TitleFor`, `SubtitleFor`, `FileNameOf`. `VideoOverrideRow.CanLocate`/`CanReveal` are the row's own derived verbs and must port with it | `src/apps/Wavee.Tests/VideoOverrideMutationCoreTests.cs` | `Shell/Video.cs` — CORE **`// ── local video attachments`** (the flyout that renders it goes to `Screens/Settings.UI.cs`) |
| `VideoPresence` | `App/VideoOverrideUx.cs:341-381` | the three-plane has-video answer + `HasOverride` + the diagnostic-only `Association`. **NOT pure** (it holds `VideoOverrideService` + `IStore` statics) and deliberately **not a signal** — a row must not subscribe per row. It is the seam DATA GAP 2 replaces; it is listed here so a re-author does not port it verbatim by mistake | — (exercised through the surfaces) | **DELETED** in 0.3 — replaced by `Track.HasVideo` computed at commit (DATA GAP 2) |
| `ShellResponsiveLayout` video geometry | `Features/Shell/ShellResponsiveLayout.cs:126-162` | `DockedVideoNaturalH(railW) = railW × 9/16`, `DockedVideoMaxH = 560`, `DockedVideoFitMinH = 120`, `ClampDockedVideoHeight`, `FitDockedVideoHeight` (**not** routed through the clamp, on purpose: the clamp's floor is 16:9 and flooring a content fit at 16:9 forces every wider-than-16:9 stream 32 %+ taller than its own aspect — guaranteed black bars) | `src/apps/Wavee.Tests/ShellResponsiveLayoutTests.cs:155-190` | `Shell/Shell.cs` — CORE, with the rest of the responsive layout (chapter 18) |

---

## 9. Re-author notes

### Must not be simplified

- **The three-way branch in every `BuildVideoArea`.** Live stage ⇒ stage only (the ENGINE element is the one loading
  affordance; stacking the app's own `LoadingOverlay` on top produced *two spinners at once*). No player ⇒ poster +
  spinner. A **null source with a live player** is still the stage — never a teardown.
- **`LivePosterArt` as a component.** `PosterContent` freezes at the element's mount and the stage key is now stable
  across source switches, so a frozen `Poster(track)` would show the FIRST track's art during every later switch.
- **The `_activeGate` `Ctx.Provide`.** `MediaPlayerElement` exposes no public "are you active" prop; its `_isActive`
  comes from `UseIsActive()`, which AND-folds ambient `Activation.IsActive` with the component's KeepAlive-parked
  state. Re-providing a *stable* signal instance (never reassigned — `Ctx.Provide` only re-notifies on a **value**
  change, and `UseIsActive` resolves the instance once) that re-derives window visibility AND "immersive lyrics is not
  covering the rail" is the **only** lever that exists. A parked, non-decorative element still calls `PumpVideo`, so MF
  keeps advancing and the video picks up mid-song instead of restarting when immersive lyrics closes.
- **`IsDecorative = false` on the docked card.** Decorative skips the pump while parked.
- **The always-on log lines.** `docked cap fit …` (railW, natural WxH, height, pinned, dim source, source key),
  `docked policy …` (aspect mode, custom ratio, transport owner, suppressed), `docked host …` (every **term** of the
  mount decision, not just its outcome). No env switch. The host line exists because "the rail kept it" looks
  identical whether the page never claimed it, claimed it with the wrong id, or was correctly outranked — and that
  ambiguity cost a full debugging cycle. `Show(uri)` keeps the last 24 characters, because a module playable is 60+
  characters of base64 that differs from its neighbours only in the tail.
- **The Shield as the first ZStack child** in the fullscreen surface. A full-bleed shield *after* the video area won
  every hit: the video never saw a hover move (its controls could not auto-show), its transport could not be clicked,
  a double-click never exited, and its idle cursor logic never learned where the pointer was.
- **The PiP's band inset arithmetic.** The chrome strip is inset by `CornerW (14)` left/right and `EdgeBand (6)` top
  so neither the drag surface nor the ✕ sits under a resize band — a ✕ whose corner is stolen by the NE zone is the
  classic overlay bug. Corner rows are 12 tall while the edge bands stay 6, so the bottom band grazes only the
  transport's 8-DIP bottom padding.
- **`SurfaceMotion.Channels = Opacity` only.** The scale rides on the *terminals*, so the node is never marked
  `BoundsAnimated` and a live drag/resize is never FLIP-chased. The recorder composites a node's local transform about
  its centre and the anim compose seeds Tx/Ty from the node's current `LocalTransform`, so the scale rides **on top
  of** the bound translation instead of replacing it.
- **Persist on the gesture's release edge, not per sample.** `OnClick` on an `OnDrag` node *is* the release edge.
  And persist only a `_placed` surface: writing the computed anchor would freeze it against a later window resize.
- **The PiP's fit has a CEILING as well as a floor.** `_h = max(135, min(w × ratio, max(135, vpH − 72 − 32)))`
  (`InWindowVideoPip.cs:158, 167`). Drop the `free` term and a 9:16 portrait source in a 360-wide mini player asks for
  640 DIP of height in a 900-tall window and the card walks off the bottom. The floor wins the tie (`max(MinH, free)`),
  so a very short window still gets a 135-tall card rather than a degenerate one.
- **The rail's Video-mode header carries its own ▣ / ⛶ (W23b).** Three entry points to the placement ladder is the
  design, not duplication: the card's strip is hover-only, the ⋯ menu needs the picture, and the header is the one
  that is always visible. Dropping the header pair to "avoid redundancy" removes the only always-visible way out of a
  docked video that does not require hovering the picture first.
- **The manager's row-action labels are full sentences, and the row wraps.** "Replace video file…" / "Locate video
  file…" / "Show in Explorer" / "Remove video" at `Gap 8` inside a 380-DIP row is *usually two lines*. `Wrap = true`
  is the fix that shipped; ellipsising them, or shortening them to "Replace"/"Remove", is a redesign — the long form
  is what makes a repair verb unambiguous next to a file path.
- **`Tok.TextOnAccentPrimary` is not an on-media token.** See §4.5 — it is `#000` in dark. Port the composition,
  not this colour.

### Traps

| Trap | What happens | The 0.2.9 answer |
|---|---|---|
| **Props freeze at mount** | `MediaPlayerElement.Player`, `PosterContent`, `SuppressTransport`, `IsHostFullscreen`, `DragMovesWindow`, `CursorAutoHide` all freeze | every prop that can change under a live stage is folded into the element **`Key`**: `"player:gen:N" + ":t0|:t1" + ":f0|:f1"`. `DragMovesWindow`/`CursorAutoHide` need no key segment because they are pure functions of `(Identity, IsHostFullscreen)` and both are already covered |
| **`OverlayHost.Child` is `[MountOnceContent]`** | passing the pop-out's element tree directly froze it at the first render, when no player existed — the window then rendered an empty root **forever**, nothing pumped the protected session, and the managed side sat at `Opening` until the watchdog gave up **while the native log showed the video licensed and playing** | the child MUST be a **Component** (`PopOutVideoContent`), which re-renders itself so its signal reads stay live |
| **A detached window builds its own AppHost** | `Ctx.Provide` chains do not cross the boundary; `UseContext(PlaybackBridge.Slot)` resolves to null, `UseContext(Overlay.Service)` resolves to `NullOverlayService` and every transport flyout becomes a silent no-op | hand signals and instances in as **frozen props**; wrap the content in `OverlayHost.Create` |
| **`AspectRatio` is ignored on a ZStack node** | `FlexLayout.MeasureZStack` returns before the aspect block runs — this is the trap that measured the docked art tile at 326×160 instead of its designed shape | the aspect box is a plain **column** and the ZStack is its `Grow = 1f` child (`WatchPageView.cs:133-138`) |
| **`Grow = 1` inside a NaN-height parent measures 0** | the docked cap collapsed to the 16-DIP splitter strip once `AspectRatio` came off | a **declared** `Height` bound to the same `FloatSignal` everyone writes — and a **stable bind**, not a fresh `Prop.Of` thunk each render (that left `LayoutInput.Height` NaN) |
| **A `Flow.KeepAlive` parked or exit-frozen page cannot re-render** | a claim/release handshake for the one video surface would be stuck on a page that is no longer running, and `OneSurfacePerPlayerGuard` would trip the moment any other face mounted | the host is **derived**, never claimed: every mount site asks `ShouldMount` the same question against the same plain values. A dead page cannot hold what it never held. The watch page's mount is `Flow.Show(Live, …)` — a **node-bound** effect, not a C# `if` |
| **`ReuseGuard`** | a card remounted on a source change tears down the MF pump | the stage key drops `src.Key` entirely; only a new `Generation` remounts |
| **Zero-allocation scroll frames vs per-row richness** | not applicable here — **this surface must never be inside a scroller.** `NowPlayingPanel`'s `ScrollView(...) with { AutoEdgeFade = true }` pushes an offscreen RT and erases the hole; `ScrollLeaseCapture` disqualifies the fling lease; rect-only ancestor clipping fights the rail's rounded silhouette | the card is a pinned `Shrink = 0f` **sibling** of the scrolled body, never its first child |
| **Two independent visibility flags** | is exactly how the fullscreen transport and the global player bar ended up **stacked** | one derived owner value, asserted by `SingleTransportInvariant()` |
| **`IsFullscreenPresentation` is `internal`, `PresentingFullscreen` is not the same thing** | the element's frame radius and border test the FORMER (`MediaPlayerElement.cs:157, 1057`), which Wavee can never set; only the transport glyph / ⋯ label / Escape arm test the latter (`:228`). So setting `IsHostFullscreen = true` fixes the *behaviour* and leaves the *frame* rounded and bordered even at full screen | nothing, in 0.2.9. In 0.3 this is the second engine change this chapter asks for (the first is the ⋯ "Full screen" label wart): make the frame follow `PresentingFullscreen`, or expose the flag. Until then, reproduce W25 rather than faking a square frame app-side |
| **A key handler that forgets `e.Mods`** | the fullscreen root's Escape/F arm bails on any Ctrl/Alt/Shift (`VideoFullscreenSurface.cs:162`) — correct, but it means the docked card's Space arm (`DockedVideoSurface.cs:299-308`) does **not** check modifiers, so Ctrl+Space and Shift+Space also toggle play/pause there | port both exactly; they differ on purpose, and "make them consistent" changes two shipped behaviours at once |
| **`Tok.TextOnAccentPrimary` on media** | black spinner + black "Loading…" over black in the default (dark) theme — §4.5 | use `Tok.OnMedia*`. The `Tok.TextOnAccent*` family means "ink on an accent FILL"; nothing on a video surface is one |

### Stale prose in the 0.2.9 sources — do not port the comments, port the code

Three doc comments contradict the code they sit on, and a re-author reading them would build the wrong thing:

1. `DockedVideoSurface.cs:35-38` ("the TRANSPORT is the global 72-DIP player bar … so **both faces pass**
   `MediaPlayerElement.SuppressTransport`") and `:349-354` ("`TransportOwnerFor(Docked)` hands the transport to the
   BAR"). **The code does the opposite**: `suppress = TransportOwnerNow.Value != TransportOwner.Docked` and
   `TransportOwnerFor(Docked) == TransportOwner.Docked`, so `suppress` is **false** and the docked card **draws** the
   engine transport (`DockedVideoSurface.cs:354`, `PlacementCore.cs:455`). `PlacementCore.cs:57-66` states the current
   rule correctly. Same for the PiP (`InWindowVideoPip.cs:310-314` claims suppression; its `VideoStageHost` identity is
   `TransportOwner.Docked`, so it does not suppress either). **Ship the code's behaviour**: docked card and PiP each
   carry an auto-hiding transport overlay inside their own bounds, and the 72-DIP bar keeps rendering below.
2. `InWindowVideoPip.cs:53` calls `360×202` "~16:9". Exact 16:9 of 360 is 202.5. Harmless, but the **fit** effect uses
   `DefaultH / DefaultW = 0.5611` as its no-report fallback ratio, not `9/16 = 0.5625` — a 0.25 % difference. Keep the
   0.2.9 numbers so a restored rect from a 0.2.9 profile lands identically.
3. `VideoRailPanel.cs:23-25` and `DockedVideoSurface.cs:406-408` both describe "the shared `Poster` composition".
   They are three separate literal copies (`DockedVideoSurface.Poster`, `InWindowVideoPip.BuildVideoArea`'s fallback,
   `VideoFullscreenSurface.VideoArea`'s fallback) plus a fourth, spinner-less one in `VideoRailPanel`. In 0.3 make
   this **one** helper with a `bool spinner` — but keep it byte-identical, because the watch page's idle layer being
   byte-identical to the card's poster is what makes the idle→live handover one cross-fade instead of two.

### Where design intent and code disagree (code wins; drift noted in one line each)

- `docs/plans/wavee/docked-video-snap-mica.html` (665 lines) prototypes **drag-the-PiP-onto-the-rail-to-dock**: a
  rotated `translate(16px,8px) rotate(3deg)` ghost, a `data-hot` drop target with a 2 px accent outline and a chip, and
  a 0.7 s `findme` accent-pulse on landing. **None of it exists in 0.2.9** — `InWindowVideoPip` has drag + eight-zone
  resize + viewport clamp and nothing else; there is no magnetic corner snap either. *Treat the snap prototype as a
  future design, not as parity.* (The task brief's "corner snap" refers to the **anchored** behaviour: while `_placed`
  is false the position is re-derived from the viewport each frame, so the card tracks the bottom-right corner.)
- `docs/plans/wavee/docked-video-mica.html:428,434,466` shows a **left-aligned eyebrow label** in the top strip
  ("NOW PLAYING" / "VIDEO" / the track title) beside the glyphs. The code has **no label**: three glyphs,
  right-aligned, `Justify = FlexJustify.End`. Code wins.
- `docs/plans/wavee/video-smooth-switching-implementation.md:129-131` specifies an app-side **switching overlay**
  (`LoadingOverlay` stacked on a live stage while `src.Key != VideoLiveSourceKey`) and a
  `PlaybackBridge.VideoLiveSourceKey` signal. Neither exists at HEAD — `VideoLiveSourceKey` has **zero** references in
  the tree, and all four surfaces now say "the ENGINE element is the one loading affordance … stacking the app's own
  LoadingOverlay on top produced two spinners at once." Code wins: **do not re-add the app-side switching overlay.**
- `DockedVideoSurface.cs:42-53` describes the rail's *third* face (a square Art-tile hero for the Details body) as
  removed, and `docked-video-mica.html:229-233` still shows an `.artslot`. The square is gone; the Details body mounts
  the same full-bleed `DockedCap` (`RightRail.cs:288-297`). Code wins.

### Where the 0.3 plan is wrong or too thin for this surface

1. **§2's tree had no home for any of this — settled 2026-09-12 (A13).** `Playback/Playback.Video.cs` is explicitly
   *"native video host + load pump"* and Wave 3 owner H's row says *"video host + `VideoLoadPump`"*. 2,229 UI lines and
   1,360 rule lines had nowhere to go. **Now in the tree: `Shell/Video.cs` (CORE ~900), `Shell/Video.UI.cs` (UI
   **1,450**), `Shell/Video.Host.cs` (SHELL ~250)** — three files, ~2,600 lines, added to §2's `Shell/` block, plus
   the named partial `Screens/+Settings.UI.Video.cs` (280, the override-manager flyout body, written by K in Wave 4,
   mounted by R in Wave 6) — this chapter's own header estimate of `Video.UI.cs` ~1,700 folded the flyout body in;
   the plan keeps it as its own file instead. The `Shell/` subtotal is recomputed centrally from every chapter's
   rows (A17) rather than from this chapter's old ~21,600 → ~24,450 delta. **`Playback/Playback.Video.cs` keeps its
   1,100 and its scope**: the decode/host half only.
2. ~~**No wave owns the video UI.**~~ **Settled 2026-09-12 (A13): `Shell/Video.{cs,UI.cs,Host.cs}` is Wave 4 owner K**
   — K already owns `Rail.UI.cs`, which mounts the docked cap and its splitter, and `Lyrics.UI.cs`, whose immersive
   surface is the structural twin of `VideoFullscreenSurface`. `Playback.Video.cs` stays **Wave 3 owner H**, and the
   rail's `RailMode.Video` panel body stays in `Rail.UI.cs` (K, chapter 21). Wave 4's owners are therefore
   Shell/Palette/Notify (I), Sidebar (J), Rail/Deck/Lyrics/**Stage**/**Video** (K), Design/Controls/Drag/Prefs (L).
3. **§4.2's Track columns cannot express video presence.** `VideoCounterpart` (slot) + flag `HasVideo` collapse three
   independent planes into one bit and drop the `videoGidHex` the Connect publisher needs. See DATA GAP 2.
4. **§4.7/§4.8 (the reducer + host loop) have no placement state at all.** `Playback.State` carries no
   `PlacementState`, `Effects` carries no video effect slot, and `Playback.Host`'s `Publish()` publishes no video
   signal. The single write path (`CommitVideoSurface`) — which is also where `DetachedFullscreenRule.After` clears
   the pop-out fullscreen bit, where the media kind is refreshed, and where Connect audio is cleared — has no
   counterpart in the plan. This is not optional plumbing: **it is the chokepoint that makes "closed the pop-out while
   fullscreen, reopened it fullscreen" unrepresentable.** Add `PlacementState Video` to `Playback.State`, an
   `Effects.VideoKindRefresh` slot, and `Playback.VideoSurface : Signal<PlacementState>` to the host's `Publish()`.
5. **§4.13's page shape assumes a scrolled column.** The watch-page stage must be mounted **outside** the page's
   `ScrollView`, outside any `Skel.Region` and outside any section wrapper that applies a fade. §4.13 gives no way to
   say "this child is exempt from the page's reveal choreography", and getting it wrong makes the video vanish
   silently. Chapter 09 needs the same note.
6. **§4.12's `Track.Row` has no video indicator column.** 0.2.9's `ColumnSet` carries `Video: true` and every row
   indicator calls `VideoPresence.HasVideo` — one allocation-free dictionary probe, deliberately **not** a signal,
   because row rendering must not subscribe per row. `RowStyle` needs a `ShowVideo` flag and the read must stay a
   single column probe.
7. **§1's "Out" column says "Visual redesign of any page"** but §2 silently deletes four surfaces' worth of UI. This
   chapter *is* the missing design; treat any simplification of it as a redesign.

### Line budget

| | 0.2.9 | §2 target | Honest estimate |
|---|--:|--:|--:|
| The four surfaces + shared stage + placement menu | 1,934 | **0 (absent)** | 1,450 (`Shell/Video.UI.cs`) |
| Override manager flyout | 295 | 0 (absent) | 280 (`Screens/+Settings.UI.Video.cs`, named partial — written by K in Wave 4, mounted by R in Wave 6) |
| Pure rules (9 classes) | 1,360 | 0 (absent) | 900 (`Shell/Video.cs`) — verbatim ports, minus the `RailMode` enum's new home |
| Detached-window owner | 203 | 0 (absent) | 250 (`Shell/Video.Host.cs`) — +50 for the `IDetachedVideoWindow` seam moving out of `InputHooks` |
| Mount sites (rail cap + hero, watch stage, shell layers, player-bar rows, content-host inset) | ~320 | folded into other files | ~320, unchanged, spread across `Rail.UI.cs`, `Modules.UI.cs`, `Shell.UI.cs`, `Shell.Host.cs` |
| **Total** | **4,112** | **0** | **~3,200** |

The 22 % reduction is entirely: one shared poster/loading helper instead of four literal copies (−90), one `Video.cs`
header instead of nine file headers (−60), and the three stale transport paragraphs (−40). **Nothing else should
shrink.** If the estimate comes in under 2,800 something has been dropped — most likely the eight resize zones, the
always-on log lines, or the fullscreen focus choreography.

**Counted exactly once (arbitration 2026-09-12, A13).** Of the ~3,200 above, **~2,600 is new `Shell/Video.*` budget**
(900 + 1,450 + 250) and **~600 is not `Shell/Video.*`'s to add**: the override flyout's 280 lands in its own named
partial, `Screens/+Settings.UI.Video.cs` (written by owner K in Wave 4, mounted by owner R's Playback tab in
Wave 6 — not folded into `Screens/Settings.UI.cs` itself), and the ~320 of mount sites are already inside
`Rail.UI.cs`, `Modules.UI.cs`, `Shell.UI.cs` and `Shell.Host.cs` — the rail's `RailMode.Video` body among them, which
chapter 21 bills inside its `Rail.UI.cs` 1,400. Nothing here is charged to `Playback/Playback.Video.cs`: that file
keeps its 1,100 for `FluentVideoMediaHost` + `VideoLoadPump` and gains nothing from this chapter.

### Files or pages missing from the §2 tree

- ~~`Shell/Video.cs`, `Shell/Video.UI.cs`, `Shell/Video.Host.cs`~~ — **settled 2026-09-12 (A13): in the tree, owner K, Wave 4** (above).
- A home for `IDetachedVideoWindow` / `DetachedWindowRequest` / `OpenDetachedWindow` — today an `InputHooks` seam.
  Propose `Platform/Platform.cs` (the seam) + `Platform/Platform.Host.cs` (the Win32 implementation), with
  `Shell/Video.Host.cs` the only caller.
- A home for the override roster service (DATA GAP 3): `Platform/Platform.cs` → `Platform.VideoOverrides`.
- `Screens/Settings.UI.cs` must grow the Video-overrides card + the Manage flyout (chapter 27 owns the card; this
  chapter owns the flyout body).

---

## 10. Parity checklist

Side by side against the kept 0.2.9 Release build
`C:\WAVEE\wavee-0.2.9\src\apps\Wavee\bin\Release\net10.0\win-arm64\publish\Wavee.exe`, both in `--fake` mode.
"Static capture" = a screenshot; "hover capture" = a screenshot with the pointer parked on the named target;
"frame recording" = a screen recording frame-differenced against the 0.2.9 one.

**Docked card — geometry and ground**

1. Route `player` / rail open in Queue mode, window 1440×900, rail 340. Dock video (player bar ⌄ → *Dock in rail*).
   The card is full-bleed at the rail's width with **no** border, **no** corner radius of its own and **no** shadow.
   *Static capture; measure the card's left edge against the rail's left edge — they must be identical.*
2. Same view: the card's height for a 16:9 source is **191.25 DIP** (= rail 340 × 9/16). *Static capture, measure.*
3. Drag the rail's left seam to 500. The card re-fits to **281.25** for 16:9. *Static capture at two widths.*
4. Play a 4:3 source: the card grows to **255** at rail 340 and shows **no** letterbox bars. *Static capture; sample the
   pixel rows immediately above and below the picture — they must be picture, not `#000`.*
5. Play a ≥ 32:9 source: the card floors at **120**, not at 191.25. *Static capture, measure.*
6. Play a portrait (9:16) source: the card ceilings at **560** and the **element** pillarboxes inside it (bars left and
   right, reachable by the ⋯ → Aspect ratio menu). *Static capture.*
7. The card is a pinned sibling **above** the rail header — the header and body do not move when the card mounts or
   unmounts. *Frame recording of a dock → turn-off → dock cycle; the header's Y must never change.*
8. With video off / on a video-less track, the rail shows **zero** reserved strip (no 16-DIP splitter band, no gap).
   *Static capture.*

**Docked card — chrome**

9. Hover the card: exactly **three** glyphs, right-aligned, in the order ▣ ⛶ ✕, each 24×24 with a 2-DIP gap, ending 8
   DIP from the card's right edge. *Hover capture, measure.*
10. There is **no** text label in the strip (the design mock's "NOW PLAYING" eyebrow must not appear). *Hover capture.*
11. The strip's ground is the top scrim (`#000 α153` at the very top, fully transparent by 100 % of 30 DIP).
    *Hover capture; sample the pixel at y = 0 and y = 29 over a bright frame.*
12. Chrome fade-in takes **167 ms** with a decelerate curve. *Frame recording at 60 fps: opacity reaches ~50 % by frame 5.*
13. Hover a glyph: fill goes to white α0.14; press: α0.22; the glyph itself goes `OnMediaSecondary` → `OnMediaPrimary`.
    *Hover + pressed captures.*
14. Each glyph's tooltip text matches `player.videoMiniPlayer` / `player.videoFullScreen` / `player.turnOffVideo`.
    *Hover capture with the tooltip up.*
15. The card **also** shows the engine transport (bottom-pinned, scrim-bottom ground, compact at 340 wide) **and** the
    72-DIP player bar is still on screen below it. *Hover capture of the whole window.*
16. The transport auto-hides 3 s after the pointer stops moving and reveals within 150 ms on a ≥ 3 DIP move.
    *Frame recording.*

**Docked card — behaviour**

17. Click ✕ → video is **off** and stays off across the next three track changes. *Frame recording of four boundaries.*
18. Click ▣ → the PiP appears at the bottom-right anchor and the card disappears with no rail reflow.
    *Frame recording; the rail header's Y must not move.*
19. Space with the card focused toggles play/pause; **Escape does nothing** (it must not close anything).
    *Frame recording.*
20. Drag the splitter down: the card grows to at most 560, never below 191.25 at rail 340, and the drag commits (the
    height survives a rail close/open). *Frame recording + a second static capture after reopening the rail.*
21. After a splitter drag, skip to a track with a **different** source: the height snaps back to the new content's own
    fit (the pin clears on a source-key change). *Frame recording across the boundary.*
22. Right-click the picture: the menu's first four rows are the placement ladder (Full screen **absent**), then the
    element's own playback rows. *Static capture of the open menu.*

**Docked card — poster and switching**

23. Toggle video on for a track whose manifest takes > 1 s: the card shows the track's art at 40 % over black with a
    20-DIP spinner and "Loading…" at 12/600, **never** a black rectangle. *Frame recording from the click.*
24. Skip video → video: the picture does **not** blank, the card does **not** reflow, and there is exactly **one**
    spinner (the engine's) at any instant. *Frame recording across the boundary.*
25. Open immersive lyrics over a docked video, then close it: playback resumes **mid-song**, not from 0:00.
    *Frame recording; read the transport time before and after.*

**In-window PiP**

26. Choose *Play in a mini player* at 1440×900: the card is 360×202 at (1064, 610) — 16 DIP from the right edge and
    16 above the 72-DIP bar. *Static capture, measure.*
27. While anchored, the page content ends **218 DIP** above the bar (the reserve), so nothing is covered.
    *Static capture of a long list; the last visible row's bottom must be at that inset.*
28. Resize the window: the card tracks the bottom-right corner. *Frame recording of a drag-resize.*
29. The card has `Radii.Card (8)` corners, a 1-DIP `StrokeCardDefault` border and the flyout shadow (dark:
    blur 16 / y 8 / `#00000042`). *Static capture in both themes.*
30. Mount animation: opacity 0 → 1 with a 0.94 → 1.0 scale over 240 ms `SmoothOut`; unmount 1.0 → 0.96 over 140 ms.
    *Frame recording.*
31. Hover: the top strip **and** the 8×8 SE nub fade in together; the nub is white α0.60 with a single rounded
    bottom-right corner. *Hover capture, zoom the SE corner.*
32. The ✕ sits 14 DIP from the right edge and 6 from the top — **not** under the NE resize zone (press exactly on the
    ✕ ten times; it must never start a resize). *Hover capture + interaction.*
33. Drag the strip: the card moves, the video stays live, and the page **re-expands** (the reserve drops to 0) the
    instant the drag starts. *Frame recording.*
34. Drag past every edge: the card clamps at 16 DIP from each edge and 88 (16 + 72) from the bottom.
    *Frame recording of a four-corner sweep.*
35. Resize from each of the eight zones with the documented cursor for each; the opposite edge stays anchored; the
    floor is 240×135. *Frame recording, one gesture per zone.*
36. Close the app and reopen with video on: the PiP opens at the remembered rect, already "placed" (no reserve).
    *Static capture before and after a restart.*
37. Click the PiP's ✕: video is **off** and stays off across the next three tracks. *Frame recording.*

**Pop-out window**

38. Choose *Play in a separate window*: a chromeless 640×360 window opens, always-on-top, titled with the track.
    *Static capture including the taskbar/Alt+Tab entry.*
39. Drag the picture: the **window** moves, Aero Snap engages at a screen edge. *Frame recording.*
40. Let the pointer idle over the window: the chrome fades in 400 ms and the **cursor disappears with it**.
    *Frame recording.*
41. Press F11 in the pop-out on a **second monitor**: it goes borderless fullscreen **on that monitor**; the main
    window does not change. *Frame recording of both displays.*
42. Exit fullscreen, close the window, reopen: it reopens at the **windowed** rect the user chose, not monitor-sized,
    and **not** fullscreen. *Static captures across the cycle.*
43. Close the pop-out with the OS ✕: video **falls back to the mini player** (it does not turn off).
    *Frame recording.*
44. Right-click in the pop-out: the menu carries the placement ladder **plus** a checked "Always on top"; unchecking it
    drops the window behind others **immediately**, without reopening it. *Frame recording.*

**Fullscreen surface**

45. Press F11 on a docked video: the window goes real borderless fullscreen, the title bar **and** the player bar are
    gone (unmounted, not dimmed), and there is exactly **one** transport. *Static capture.*
46. Hover: a 56-DIP top band fades in carrying the track title at 15/600 on the left and a 44-DIP circular exit FAB on
    the right, 12 DIP from the top-right. *Hover capture, measure.*
47. The FAB's ground is white α0.14 with a 1-DIP stroke **and a card shadow** (it must read as raised over undimmed
    video, in both a bright and a dark frame). *Hover captures over two frames.*
48. The enter transition is **scale only** — the video never goes translucent and never disappears.
    *Frame recording; sample the centre pixel every frame during the transition.*
49. Escape exits from any focus position: after clicking the letterbox bar, after Tab-ing to the FAB, after clicking
    the transport. *Frame recording, three attempts.*
50. Exiting returns to exactly the placement fullscreen was entered from (dock → fullscreen → dock; PiP → fullscreen →
    PiP at its remembered rect). *Frame recording of both cycles.*
51. Put the window in OS fullscreen first (Win+Up-style), then enter and exit video fullscreen: the window is **still**
    OS-fullscreen afterwards. *Static captures.*
52. Tab from inside fullscreen never reaches the (unmounted) shell chrome. *Frame recording of ten Tab presses.*

**Placement menu, watch stage and the manager**

53. Narrow the window until the rail cannot fit: *Dock in rail* becomes **disabled** with "Needs a wider window" in
    the accelerator column — still visible, not hidden — and the video demotes to the mini player.
    *Static capture of the open menu at two widths.*
54. Open the menu from the player bar (Full screen row present, accelerator "F11") and from the video's ⋯ (Full screen
    row **absent**). *Two static captures.*
55. "Turn off video" is absent while nothing is on and present while something is. *Two static captures.*
56. On a module watch page for the playing video: the video mounts **in the page's 16:9 stage** and the rail falls back
    to the Queue body with **no** card. *Static capture of the whole window.*
57. Navigate away from the watch page and back: the stage hands the surface to the rail and takes it back, with no
    flicker and no double-mount warning in the log. *Frame recording + the `docked host …` log lines.*
58. On the watch page before playing: the idle poster is the page's own art at 40 % with a 64-DIP accent play disc
    centred; pressing it dissolves (one cross-fade) into the live frame. *Frame recording.*
59. Settings → Playback → Video overrides → Manage: a 420-wide panel with a 396×32 search box, "RECENTLY ADDED", at
    most **four** compact rows, a divider and "Browse all… N ›". *Static capture, measure.*
60. Type a query: the recent section is replaced by full rows with actions and a match count; clear it and the resting
    root returns with the caret still in the box. *Frame recording.*
61. Drill into "Browse all…": the panel slides forward; ‹ mirrors it back. *Frame recording.*
62. A moved file shows the amber **Missing** chip and a "Locate video file…" action; an unplugged drive shows
    **Drive offline** with **no** repair action. *Two static captures.*

**Added by the audit (§11) — states the first pass did not cover**

63. **Dark theme, poster.** Turn video on for a slow-resolving track in the DEFAULT (dark) theme and read the spinner
    and the "Loading…" label. In 0.2.9 they are **black on black** (§4.5); 0.3 must show them in `Tok.OnMedia*`.
    *Static capture in dark; sample the ring's pixels — they must not equal the letterbox black.* This is the one
    checklist item where "differs from 0.2.9" is the pass condition.
64. **Light theme, fullscreen exit FAB.** Enter fullscreen in LIGHT theme and hover. The FAB's plate, stroke and glyph
    are near-black (`Tok.MediaStage`-derived), not white (§4.4). *Hover captures in both themes over the same frame;
    the two must differ.*
65. **The engine player frame.** Zoom the top-left corner of the docked cap, the PiP's video area, the pop-out's
    picture and the fullscreen picture. All four carry an **8-DIP rounded corner and a 1-DIP `StrokeFlyoutDefault`
    hairline** (W25) — including fullscreen, against the monitor edge. *Four zoomed static captures.* If 0.3 renders
    any of them square, an engine change was made; say so explicitly rather than letting it pass as parity.
66. **Pop-out, no player.** Choose *Play in a separate window* on a track whose manifest takes > 1 s. 0.2.9 shows a
    bare black rectangle (W14b); 0.3 must show the poster + spinner. *Frame recording from the click.* Second
    deliberate divergence.
67. **PiP portrait fit, and its ceiling.** Play a 9:16 source in the mini player at its default 360 width.
    In a **900**-tall window the fit is `min(360 × 1920/1080, 900 − 72 − 32) = min(640, 796) = 640`.
    In a **500**-tall window the same source re-fits to `min(640, 396) = 396` — the free-height ceiling wins and the
    card does not walk off the bottom. *Two static captures at the two window heights, measure.*
68. **Rail Video-mode header.** In `RailMode.Video`, the header carries ▣ and ⛶ beside the ✕, the ✕ closes the **rail**
    (and the video demotes to the mini player), and ▣/⛶ move the video. *Static capture + a frame recording of the ✕.*
69. **Rail Details body while the watch page hosts.** Navigate to a module watch page for the playing video with the
    rail open on Details: the hero shows the **bare art tile** — no docked card and no Art|Video toggle. *Static capture.*
70. **A watch page with no play action.** Open a module watch document whose projection offers no play verb: the stage
    is the poster with **no** 64-DIP disc at all (not a disabled one). *Static capture.*
71. **A LIVE module source.** Play a live broadcast: the transport shows the LIVE chip, a "Go live" affordance and a
    DVR rail instead of `0:42 / 3:18`, and no negative remaining time anywhere (W24). *Static capture of the transport.*
72. **Modified Escape in fullscreen.** Hold Shift and press Escape in video fullscreen: **nothing happens**. Release
    Shift, press Escape: it exits. *Frame recording.*
73. **Manager copy, verbatim.** The search placeholder reads "Search by track, artist or file name"; the recent label
    "Recently added"; a 3-hit search "3 results"; the browse-all row "12 attachments"; a healthy chip "OK"; the row
    verbs "Replace video file… / Locate video file… / Show in Explorer / Remove video", wrapping to two lines rather
    than ellipsising. *Static capture, read every string.*
74. **Remove-all has no undo.** Settings → Video overrides → Remove all → confirm: the "All attachments removed" toast
    carries **no** Undo, unlike a single row's. *Static capture of both toasts.*
75. **Engine startup-spinner delay.** Skip to a track whose first frame lands in < 500 ms: **no spinner flashes** over
    the poster at all. *Frame recording; count frames between the poster appearing and any overlay.*

---

## 11. Audit log

Adversarial re-read against the 0.2.9 sources, 2026-09-12. One line per correction; `wrong` = the chapter stated
something the code contradicts, `missing` = a state / element / rule the code has and the chapter did not,
`unverified` = a number kept but not confirmable from source, `overclaim` = true in part, stated absolutely.

| # | § | Kind | Correction |
|---|---|---|---|
| 1 | §0.14, §3, §5, W25 | **wrong** | "No border, no corners" was true of the app's wrapper only. `MediaPlayerElement.FrameCorners` is `Radii.OverlayAll (8)` and the frame paints a 1-DIP `Tok.StrokeFlyoutDefault` border for every element that is not `IsDecorative` with `CornerRadius == 0` (`MediaPlayerElement.cs:154-160, 1057-1058`). Both tests read the **internal** `IsFullscreenPresentation` (`:247`), not `PresentingFullscreen` (`:228`), so even the fullscreen surface is rounded and hairlined. Added W25, a §3 row, a §9 trap and parity item 65. |
| 2 | §4 | **wrong** | "Every on-media token is theme-invariant … the four surfaces look identical in both themes" — `Tok.TextOnAccentPrimary` is theme-split and is **`#000000` in dark** (`PaletteBuilder.cs:239` light / `:327` dark; Wavee resolves `Tok.NeutralPalette`, `WaveeTheme.cs:15`). The poster's `ProgressRing` + "Loading…" therefore render black-on-black in the default theme, on all three poster surfaces and on the B10 placeholder. Rewrote "exactly three things" to five, with the fix (`Tok.OnMedia*`) and parity item 63. |
| 3 | §4 | **wrong** | `StageInk.GlassPlate/Hover/Pressed` was cited to `Design/WaveeOnMedia.cs:102-106` and described as fixed white alphas. `StageInk` is a separate file and the app's **one theme branch** (`Design/StageInk.cs:16-19` → `Design/StageArm.cs:60, 79-83, 96`): in light theme the fullscreen exit FAB's plate, stroke and glyph invert to `Tok.MediaStage` (#0A0A0A). Corrected the cite and the claim; parity item 64. |
| 4 | §0.8, §1a, W14b | **missing** | The pop-out window has **no poster state at all** — `PopOutVideoContent` renders `Array.Empty<Element>()` while no player exists (`PopOutVideoWindow.cs:149-155`), so it is a bare black rectangle for the whole DRM round-trip. Added W14b, amended §0.8, and marked the 0.3 fix as a deliberate divergence (parity item 66). |
| 5 | §1a, §9 traps | **wrong** | The shared stage's element key was written `"player:gen:N:t0/t1:f0/f1"`. There is no `gen:` segment: it is `"player:" + Generation + …` (`PopOutVideoWindow.cs:255-256`). The `gen:` prefix belongs only to the four surface keys (`dockstage:` / `pipstage:` / `stage:` / `fsstage:`). |
| 6 | W20, W21, W22, §3 | **wrong** | Manager copy did not match `assets/loc/en-US.json`: the placeholder is "Search by track, artist or file name" (not "Search attached videos"); the match label is `matchCount` = "3 results" (not "3 MATCHES"); the counts are `settingsCount` = "12 attachments" (not a bare numeral); the healthy chip reads "OK" (not "Ok"); the row verbs are the full strings "Replace video file…" / "Remove video" (not "Replace" / "Remove"). Also `Caption` applies **no** uppercase transform (`Typography.cs:39`), so the wireframes' small-caps labels were stylisation, not the render. Parity item 73. |
| 7 | W21, W22, §3, §9 | **missing** | The row-actions row is `Wrap = true` (`SettingsPage.VideoOverrides.cs:252-256`) — at 380 usable DIP the four full labels take two lines by design. Added the row, the arithmetic and a must-not-simplify. |
| 8 | §6, §9 | **missing** | The fullscreen root's key handler bails on `e.Handled` or any of Ctrl/Alt/Shift **before** reading the key code (`VideoFullscreenSurface.cs:162`), so modified Escape/F do nothing — while the docked card's Space arm checks no modifiers at all (`DockedVideoSurface.cs:299-308`). Parity item 72. |
| 9 | §3, §9 | **missing** | The PiP's content fit has a **ceiling** as well as a floor: `max(135, min(w × ratio, max(135, vpH − 72 − 32)))` (`InWindowVideoPip.cs:158, 167`). Parity item 67. |
| 10 | §2 (W23b), §9 | **missing** | `RailMode.Video`'s header carries its own ▣ / ⛶ beside the ✕ (`RightRail.cs:262-278`) — a third placement entry point the chapter never listed — and the ✕ there closes the **rail**, not the video. Added W23b and parity item 68. |
| 11 | W23, §3 | **missing** | The Art\|Video toggle's halves: 24 × 24, `Radii.Control`, glyph 11 DIP, selected `Fill = OnMediaPrimary α0.16` + `OnMediaPrimary` glyph vs transparent + `OnMediaSecondary`, no hover/press fill, `AlignSelf Start` / `JustifySelf End` (`RightRail.cs:332-359`). Also the **`stageHosts`** gate — while a watch page hosts, the rail shows the bare art tile with no toggle (`:302`). Parity item 69. |
| 12 | §2 (W24), §7 | **missing** | `PopOutVideoSource.IsLive` (`PopOutVideoSource.cs:42-50`) opens the source as `SourceLiveness.Live` and the transport becomes a different control — LIVE chip, "Go live", a DVR rail, no duration (`PlacementCore.cs:441-444`). No wireframe covered it. Added W24, a §7 row and parity item 71. |
| 13 | §1a, W7 | **missing** | The watch page's 64-DIP play disc is added **only when `onPlay is not null`** (`WatchPageView.cs:101`) — a document with no play verb shows a bare poster and no affordance. Parity item 70. |
| 14 | §5 | **missing** | `StageChrome.ExitFab` carries `HoverScale = ScaleEmphatic.Hover (1.07)` / `PressScale (0.92)`, dropped to 1f as a VALUE under reduced motion (`StageChrome.cs:214`, `WaveeMotion.cs:48, 179-182`), plus the brush transition on its three fills. Two motion rows added. |
| 15 | §5 | **missing** | The engine's own loading affordance holds off: `StartupSpinnerDelayMs = 500`, `StartupDetailDelayMs = 10_000` (`MediaPlayerElement.cs:86-89`). The app's `LoadingOverlay` has no such delay — a parity capture that conflates the two will mis-read. Parity item 75. |
| 16 | §0.7 | **overclaim** | "Every in-app ✕ routes through `NotifyVideoSurfaceClosed` — never `TurnVideoOff` directly" holds for the ✕ glyphs, but the placement menu's own "Turn off video" row **does** call `b.TurnVideoOff` (`VideoPlacementMenu.cs:73`). Stated the split explicitly. |
| 17 | §1a | **missing** | Three small arms: `PopOutVideoStage` returns a bare `BoxEl{Grow=1}` when its bound player vanishes (`:204`); the fullscreen stage is handed **`Settings = null`** on purpose so "Always on top" is unreachable there (`VideoFullscreenSurface.cs:236`); the manager's search box is **not rendered at all** in the `Empty` section (`VideoOverrideManagerFlyout.cs:76`), so "stays mounted across every section swap" holds only for Recent↔Results↔NoMatches. |
| 18 | §3, W20 | **missing** | Manager flyout anchoring (`FlyoutPlacement.BottomEdgeAlignedLeft`, `ConstrainToRootBounds = true`, toggle-to-close), the status chip's type (12 / 600), the compact row's inner `Gap 1`, the section label's real type (`Caption` 12/16 weight 600), and the empty state's `Padding 8` / `Gap 4`. |
| 19 | §6 | **missing** | The manager's full mutation surface: the two rejection toasts and their log event, the `videoOverride.replaced` success toast, the Locate picker's caption (`locateTitle + " — " + NearestExistingAncestor`), **Remove all having no undo** while a single row has one, and the Manage deep-link's two-way realize/post race (`SettingsPage.VideoOverrides.cs:156-160, 200-216, 220-299`). Parity item 74. |
| 20 | §8 | **missing** | `VideoOverrideUx.IsAudioFile`, `VideoOverrideRow.CanLocate`/`CanReveal`, and `StatusOf`'s rootless-path arm (a relative path has no volume, so it degrades to `Missing`, `:159-169`). Added `VideoPresence` as an explicit **do-not-port** row: it is static, impure, and the thing DATA GAP 2 replaces. |
| 21 | §3 | **unverified** | "Engine transport overlay ≈ 102 tall" — the padding `(14,34,14,8)` and `Gap 2` are confirmed (`MediaPlayerElement.cs:1372-1379`); the "24 seek + 34 controls" row heights were **not** traced. Marked estimated in the table. |
| 22 | §5 | **overclaim** | The splitter row implied a hover indicator. The docked cap passes `ShowIndicator = false`, so the 2-DIP thumb is never built at all (`Splitter.cs:26-30`). Noted as not-applicable rather than left ambiguous. |
| 23 | header · §9 #1–#2 · §9 line budget · §9 files-missing | **arbitration** | arbitration 2026-09-12: **A13 settles the video split.** The chapter's proposal is taken whole — `Shell/Video.cs` (CORE ~900) + `Shell/Video.UI.cs` (~1,700) + `Shell/Video.Host.cs` (~250), **Wave 4 owner K** — so "NOT IN PLAN" / "Proposal" / "no wave owns the video UI" become a recorded decision. **`Playback/Playback.Video.cs` stays the decode/host only** (`FluentVideoMediaHost` + `VideoLoadPump`, 1,100 lines, **Wave 3 owner H**), and **the right rail's video panel body stays in `Rail.UI.cs`** (owner K, chapter 21, inside its 1,400) — this chapter bills it as a mount site, not a second copy. The line budget gains a "counted exactly once" note splitting the ~3,200 into ~2,850 of new `Shell/Video.*` and ~350 that already sits in other chapters' files; the `Shell/` subtotal is no longer stated here, because it is recomputed centrally (A17). No pixel, token, geometry or rule in §§0–8 changed. |

**Verified and left unchanged** (spot-checked against source, all correct): every number in W1–W6 and the aspect
ladder (`FitDockedVideoHeight` → 191.25 / 255 / 143.44 / 120 / 560, and the tests at
`ShellResponsiveLayoutTests.cs:158-188`); the PiP's 360×202 / 240×135 / 16-margin / 218-reserve / (1064, 610) anchor
arithmetic and the eight-zone band geometry; every chrome-strip height, padding, gap, radius and alpha in §3; the
fullscreen band's `(20,12,12,0)` = `(ExitInset + Spacing.S, ExitInset, ExitInset, 0)`; every `WaveeMotion` /
`MotionTok` duration and threshold in §5 (167 / 240 / 140 / 150 / 400 / 3000 / 4000 / 150 / 3 DIP / 420 / 460) and the
`PosterCrossFade` 150 ms `FluentStandard` `KeepFade`; every `Tok` value cited (`MediaLetterbox` #000, `ScrimTop`
153 → 51 @0.35 → 0, `ScrimBottom`, `OnMedia*` 1.0 / 0.80 / 0.60); the placement-menu rows, order, glyph codes,
radio / disabled semantics and every reason string; all seven persistence keys including `video.placement` and the
`ontop` default of `true`; `PlacementCore`'s ladder, `HostClosed`, `TransportOwnerFor` and `SingleTransportInvariant`;
`DockedVideoHosting.ShouldMount`; `VideoStageInput`; `DetachedFullscreenRule`; `VideoAspectPersistence`; and §9's
three "stale prose" findings, which reproduce exactly as described.

**token-reconcile (2026-09-12):** three tokens this chapter names were missing from the first build of `00-design-system.md §12.1` and are now indexed there: `Tok.MediaLetterbox` (`#000000`, theme-invariant) and the two gradients `Tok.ScrimTop` / `Tok.ScrimBottom`, with their exact stop ramps (`Dsl/Tokens.cs:372, 376-382, 385-390`). No value in this chapter changed.

**consistency 2026-09-12:** header and §9 item 1 cited `Video.UI.cs` at ~1,700, folding the override-manager flyout body in; the plan's settled number is **1,450** for `Video.UI.cs`, with the flyout body as its own named partial, `Screens/+Settings.UI.Video.cs` (280, written by K in Wave 4, mounted by R in Wave 6). Header, §1.2's `VideoOverrideManagerFlyout` row, §9 item 1 and the "counted exactly once" paragraph restated against the plan's numbers, with a note that this chapter's own estimate was higher. The "Line budget" table already carried the correct 1,450/280 split and needed only the file name.
