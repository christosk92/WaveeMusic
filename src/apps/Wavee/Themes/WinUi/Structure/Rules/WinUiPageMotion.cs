// ── Themes/WinUi/Structure/Rules/WinUiPageMotion.cs — WinUI's navigation transitions, per kind of page swap (winui page motion) ─────────
//
// WinUI's own NavigationTransitionInfos, from the WinUI 3 source (microsoft-ui-xaml, dxaml/phone/lib/ThemeTransitions.cpp and .h; the
// Microsoft Learn pages "Page transitions" and "Motion in practice" describe the same):
//
//   kind (PageSwap)        WinUI                                          outgoing page                         incoming page
//   Refresh                EntranceNavigationTransitionInfo (3180-3221)   fade 150 ms, (0.7,0,1,0.5)           from 140 below, 300 ms, (0.1,0.9,0.2,1), after 150
//   RefreshBack            the same, BackNavigating*                      down 140 + fade, 150 ms              fade in 300 ms, (0.1,0.9,0.2,1), after 150
//   DrillIn                DrillInNavigationTransitionInfo (2860-2940)   scale 1.04 + fade, 100 ms            scale 0.94 -> 1, 783 ms, (0.1,0.9,0.2,1); fade 333 ms
//   DrillBack              the same, BackNavigating* (2945-3021)         scale 0.96 + fade, 100 ms            scale 1.06 -> 1 + fade, 333 ms, (0.12,0,0,1)
//   SlideFromRight / Left  SlideNavigationTransitionInfo (1545-1592)     150 aside, 150 ms, (0.7,0,1,0.5)     from 200 the other side, 300 ms, (0.1,0.9,0.2,1), after 150
//   (durations: ThemeTransitions.cpp:1551-1552, 3185-3186; drill: ThemeTransitions.h:326-333)
//
// THE STAGGER. WinUI staggers a page's content with CommonNavigationTransitionInfo.IsStaggeringEnabled and a panel's children with
// EntranceThemeTransition.IsStaggeringEnabled; the delays come from the OS animation table (ThemeGenerator.cpp:868-889: a delay, a
// cap, a reduce factor), not from the source. Here: the incoming page's EntranceGroup units (`Element.EntranceGroup`) carry the
// slide and fade, 30 ms apart, each gap 0.85 of the one before, the last starting at most 180 ms after the first; the page root
// itself only fades in over ControlFaster (83 ms) as the units begin, so the parts nothing groups appear with the page. A drill keeps
// WinUI's zoom on the root and gives the units a short rise (40) on top. Back fades its units in, as WinUI's back has no slide in.
//
// WHERE THIS DIFFERS FROM THE SOURCE, and why (each a constant below, the owner's call):
//   · the incoming page starts 33 ms before the outgoing one's 150 ms end (HandOverMs): WinUI's out spline drops the old page on its last
//     frame, so a hand-over exactly at 150 left one or two frames with neither page on them;
//   · the drill's zoom in runs 500 ms, not 783 (DrillInScaleMs): the same spline, which is within a pixel of the end by 350 ms, while a zoom
//     re-records the page every frame (a translate is a composite parameter, a scale is not);
//   · the drill's outgoing zoom and fade run evenly (DrillAway): WinUI's fast spline moved the page edges half their way on the first frame;
//   · a sideways slide keeps every part inside the page (EntranceCascade.KeepInRoot): a part travels at most the room between it and the
//     page's edge on the side it comes from (the gutter, for a part that starts at it; the whole 200 for one with room beside it), and parts
//     side by side travel the same distance. WinUI slides the whole frame, which the frame's edge cuts; parted into sections, that edge cut
//     a list's covers and titles off for most of the slide (owner, 2026-11-07).
//
// Reduced motion ("Animation effects" off): a quick cross-fade (ControlFaster each way), no slide, no scale, no stagger.
//
// THE ARTIST PAGE'S TABS scroll (SectionJump): a section within two viewports glides there; a farther one cuts to a lead short of it on the side the
// user comes from and decelerates in, while what the page shows fades in from 40 % over 100 ms (the same-kind swap's numbers). The pinned pivot
// neither moves nor fades. Reduced motion jumps and keeps the fade. (They used to jump and slide the section in sideways: the owner, 2026-11-15:
// "things just randomly scroll and appear. i would expect scrolls".)
//
// Literal numbers are allowed in this file (WVTH003 exempts `Rules\**`).

using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's page motion per kind of swap.</summary>
public static class WinUiPageMotion
{
    // ── WinUI's KeySplines (ThemeTransitions.cpp) ────────────────────────────────────────────────────────────────────
    /// <summary>The incoming leg's spline (inControlPoint1/2 = (0.1, 0.9), (0.2, 1.0)): the page is most of the way in on its first frames.</summary>
    public static EasingSpec In => EasingSpec.CubicBezier(0.1f, 0.9f, 0.2f, 1f);
    /// <summary>The outgoing leg's spline (outControlPoint1/2 = (0.7, 0), (1.0, 0.5)): it holds, then goes.</summary>
    public static EasingSpec Out => EasingSpec.CubicBezier(0.7f, 0f, 1f, 0.5f);
    /// <summary>The drill's opacity spline ((0.17, 0.17), (0, 1)).</summary>
    public static EasingSpec DrillFade => EasingSpec.CubicBezier(0.17f, 0.17f, 0f, 1f);
    /// <summary>The drill's outgoing leg. WinUI runs the 1.04 zoom on its fast spline over 100 ms; in six frames that moves the page's edges
    /// half their way on the first one, which reads as a jump, so the zoom and the fade run evenly here (same length, same end).</summary>
    public static EasingSpec DrillAway => Easing.Linear;
    /// <summary>The drill back's incoming scale spline ((0.12, 0), (0, 1)).</summary>
    public static EasingSpec DrillBackIn => EasingSpec.CubicBezier(0.12f, 0f, 0f, 1f);

    // ── WinUI's numbers ─────────────────────────────────────────────────────────────────────────────────────────────
    public const float OutMs = 150f, InMs = 300f;
    public const float RiseDy = 140f;                                   // EntranceNavigationTransitionInfo translationOffset
    public const float SlideOutDx = 150f, SlideInDx = 200f;             // SlideNavigationTransitionInfo exit / entrance offsets
    public const float DrillAwayMs = 100f, DrillBackMs = 333f;
    /// <summary>The drill's zoom in. WinUI runs it 783 ms (ThemeTransitions.h:328) on a spline that is within 1 % of the end by 350 ms; a zoom
    /// cannot be a composite parameter of the page layer (a translate is), so every frame of it re-records the page, and the last 400 ms of
    /// WinUI's length move the page edges by under a pixel. The same spline over 500 ms: the zoom reads the same, the page pays 30 frames
    /// instead of 47. (The owner's call: 783 restores WinUI's length.)</summary>
    public const float DrillInScaleMs = 500f;
    public const float DrillInScale = 0.94f, DrillAwayScale = 1.04f, DrillBackInScale = 1.06f, DrillBackAwayScale = 0.96f;

    // ── the stagger (the owner's taste: see the header) ───────────────────────────────────────────────────────────
    /// <summary>The gap between the first two units, ms.</summary>
    public const float StaggerStepMs = 30f;
    /// <summary>Each later gap is the previous one × this.</summary>
    public const float StaggerDecay = 0.85f;
    /// <summary>The last unit starts at most this long after the first, ms.</summary>
    public const float StaggerCapMs = 180f;
    /// <summary>The incoming page starts this long before the outgoing one's 150 ms is over: the outgoing spline holds the old page up to its
    /// last frames and drops it on the 150th ms, so starting exactly there leaves one or two frames with neither page on them. Two frames of
    /// overlap hand over without a blank frame (the new page draws over the old one's last half).</summary>
    public const float HandOverMs = 33f;
    /// <summary>When the incoming page and its units begin.</summary>
    public const float InDelayMs = OutMs - HandOverMs;
    /// <summary>The page root's own fade as its units begin (ControlFaster).</summary>
    public const float RootFadeMs = 83f;
    /// <summary>A drilled page's units rise this far on top of the zoom.</summary>
    public const float DrillUnitDy = 40f;
    /// <summary>A drilled page's units start this long after the zoom does: at once. They used to wait 60 ms for the old page's 100 ms fade to
    /// be most of the way out, and in that window neither page covered the card (the owner's recording, 2026-11-15: a washed-out, near-blank
    /// page between two playlists). Started with the zoom, the header is up as the old page leaves.</summary>
    public const float DrillUnitDelayMs = 0f;

    static TransitionDynamics Tween(float ms, EasingSpec e) => TransitionDynamics.Tween(ms, e);

    const TransitionChannels Pose = TransitionChannels.Position | TransitionChannels.Opacity;

    /// <summary>A page that waits out the 150 ms exit, then fades in under its units. <paramref name="exit"/> is the outgoing leg.</summary>
    static LayoutTransition AfterExit(EnterExit exit) => new(Pose, Tween(RootFadeMs, In),
        Enter: new EnterExit(Opacity: 0f, Active: true), Exit: exit, ExitDynamics: Tween(OutMs, Out), DelayMs: InDelayMs, ExitDelayMs: 0f);

    static readonly LayoutTransition s_refresh = AfterExit(new EnterExit(Opacity: 0f, Active: true));
    static readonly LayoutTransition s_refreshBack = AfterExit(new EnterExit(Dy: RiseDy, Opacity: 0f, Active: true));
    static readonly LayoutTransition s_slideFromRight = AfterExit(new EnterExit(Dx: -SlideOutDx, Opacity: 0f, Active: true));
    static readonly LayoutTransition s_slideFromLeft = AfterExit(new EnterExit(Dx: SlideOutDx, Opacity: 0f, Active: true));

    // The drill overlaps its legs (the source starts the incoming storyboard at 0): the old page zooms past and fades in 100 ms while the
    // new one zooms in from 0.94. One tween per leg: the incoming zoom's 783 ms spline brings the fade to ~0.95 by 333 ms, WinUI's fade.
    static readonly LayoutTransition s_drillIn = new(Pose, Tween(DrillInScaleMs, In),
        Enter: new EnterExit(Sx: DrillInScale, Sy: DrillInScale, Opacity: 0f, Active: true),
        Exit: new EnterExit(Sx: DrillAwayScale, Sy: DrillAwayScale, Opacity: 0f, Active: true),
        ExitDynamics: Tween(DrillAwayMs, DrillAway), DelayMs: 0f, ExitDelayMs: 0f);

    static readonly LayoutTransition s_drillBack = new(Pose, Tween(DrillBackMs, DrillBackIn),
        Enter: new EnterExit(Sx: DrillBackInScale, Sy: DrillBackInScale, Opacity: 0f, Active: true),
        Exit: new EnterExit(Sx: DrillBackAwayScale, Sy: DrillBackAwayScale, Opacity: 0f, Active: true),
        ExitDynamics: Tween(DrillAwayMs, DrillAway), DelayMs: 0f, ExitDelayMs: 0f);

    /// <summary>Reduced motion: the old page fades out, then the new one in, ControlFaster each.</summary>
    static readonly LayoutTransition s_reduced = new(TransitionChannels.Opacity, Tween(RootFadeMs, In),
        Enter: new EnterExit(Opacity: 0f, Active: true), Exit: new EnterExit(Opacity: 0f, Active: true),
        ExitDynamics: Tween(RootFadeMs, Out), DelayMs: RootFadeMs, ExitDelayMs: 0f);

    static EntranceCascade Units(EnterExit terminal, float delayMs) => new(terminal with { Active = true }, Tween(InMs, In), delayMs,
        StaggerStepMs, StaggerDecay, StaggerCapMs);

    static readonly EntranceCascade s_riseUnits = Units(new EnterExit(Dy: RiseDy, Opacity: 0f), InDelayMs);
    static readonly EntranceCascade s_fadeUnits = Units(new EnterExit(Opacity: 0f), InDelayMs);
    // A sideways slide inside a page keeps every part inside the page (KeepInRoot): a part by the page's gutter moves as far as the gutter,
    // one with room beside it the whole 200. The page edge cut a list's covers and titles off for most of the slide (owner, 2026-11-07).
    static readonly EntranceCascade s_fromRightUnits = Units(new EnterExit(Dx: SlideInDx, Opacity: 0f), InDelayMs) with { KeepInRoot = true };
    static readonly EntranceCascade s_fromLeftUnits = Units(new EnterExit(Dx: -SlideInDx, Opacity: 0f), InDelayMs) with { KeepInRoot = true };
    static readonly EntranceCascade s_drillUnits = Units(new EnterExit(Dy: DrillUnitDy, Opacity: 0f), DrillUnitDelayMs);
    static readonly EntranceCascade s_drillBackUnits = Units(new EnterExit(Opacity: 0f), DrillUnitDelayMs);

    /// <summary>The page root's recipe for a kind; null (Neutral) keeps the core's fade.</summary>
    public static LayoutTransition? RecipeFor(PageSwap kind) => kind switch
    {
        PageSwap.Refresh => s_refresh,
        PageSwap.RefreshBack => s_refreshBack,
        PageSwap.DrillIn => s_drillIn,
        PageSwap.DrillBack => s_drillBack,
        PageSwap.SlideFromRight => s_slideFromRight,
        PageSwap.SlideFromLeft => s_slideFromLeft,
        _ => null,
    };

    /// <summary>The incoming page's staggered entrance for a kind; null (Neutral) enters as one block.</summary>
    public static EntranceCascade? CascadeFor(PageSwap kind) => kind switch
    {
        PageSwap.Refresh => s_riseUnits,
        PageSwap.RefreshBack => s_fadeUnits,
        PageSwap.DrillIn => s_drillUnits,
        PageSwap.DrillBack => s_drillBackUnits,
        PageSwap.SlideFromRight => s_fromRightUnits,
        PageSwap.SlideFromLeft => s_fromLeftUnits,
        _ => null,
    };

    // ── the artist page's tabs (SectionJump; see the header) ────────────────────────────────────────────────────────────
    /// <summary>A section at most this many viewport heights away glides there: Overview to Albums is about 1.6 in the owner's session.</summary>
    public const float SectionNearViewports = 2f;
    /// <summary>A farther section arrives from this share of the viewport's height, clamped to [<see cref="SectionLeadMin"/>,
    /// <see cref="SectionLeadMax"/>]: the tail of a real scroll, long enough to read as one.</summary>
    public const float SectionLeadViewports = 0.4f;
    public const float SectionLeadMin = 160f, SectionLeadMax = 320f;

    static readonly SectionJump s_sectionJump = new(SectionNearViewports, SectionLeadViewports, SectionLeadMin, SectionLeadMax);

    // ── the library's pivot (the body under a fixed head; winui library family) ─────────────────────────────────────────────
    //
    // The library's kinds share one head ("Library" over the kinds' SelectorBar): a switch moves only the body under it, as the SelectorBar sample
    // navigates a Frame under the bar by the index's direction. WinUI's slide, cut short because only a body moves and the owner switches often:
    // the old body fades out over ControlFaster, drifting half the travel toward the side it leaves by; the new one waits HandOverMs short of that
    // (two frames of overlap, never a frame with neither), then slides in from the side the pivot order points to over 250 ms on the in spline,
    // fading in on the same tween. The travel is the tier's page inset, at least 24: content coming from the left starts at the card's edge, as
    // KeepInRoot keeps a part inside the page. Reduced motion: the quick cross-fade.
    // The old body's leg runs evenly (LibraryPivotAway), as the drill's outgoing leg does: on WinUI's out spline five frames put 69 % of the drift in
    // the last one, which read (and gated) as a jump.

    /// <summary>The old body's fade (ControlFaster) and the new body's slide (ControlNormal), ms.</summary>
    public const float LibraryPivotOutMs = RootFadeMs, LibraryPivotInMs = 250f;
    /// <summary>The new body starts this long after the click: the old one's fade less the hand-over.</summary>
    public const float LibraryPivotDelayMs = LibraryPivotOutMs - HandOverMs;
    /// <summary>The smallest travel (the Compact and Small insets are 16 and 24).</summary>
    public const float LibraryPivotMinTravel = 24f;
    /// <summary>The old body's leg: even, as <see cref="DrillAway"/>.</summary>
    public static EasingSpec LibraryPivotAway => Easing.Linear;

    static LayoutTransition LibraryPivotBody(WidthTier tier, bool fromRight)
    {
        float travel = MathF.Max(WinUiDetailRules.InsetOf(tier), LibraryPivotMinTravel);
        float side = fromRight ? 1f : -1f;
        return new(Pose, Tween(LibraryPivotInMs, In),
            Enter: new EnterExit(Dx: side * travel, Opacity: 0f, Active: true),
            Exit: new EnterExit(Dx: -side * travel * 0.5f, Opacity: 0f, Active: true),
            ExitDynamics: Tween(LibraryPivotOutMs, LibraryPivotAway), DelayMs: LibraryPivotDelayMs, ExitDelayMs: 0f);
    }

    /// <summary>Per window tier (Compact, Small, Standard, Huge) from the left, then from the right.</summary>
    static readonly LayoutTransition[] s_libraryPivot =
    [
        LibraryPivotBody(WidthTier.Compact, false), LibraryPivotBody(WidthTier.Small, false), LibraryPivotBody(WidthTier.Standard, false), LibraryPivotBody(WidthTier.Huge, false),
        LibraryPivotBody(WidthTier.Compact, true), LibraryPivotBody(WidthTier.Small, true), LibraryPivotBody(WidthTier.Standard, true), LibraryPivotBody(WidthTier.Huge, true),
    ];

    /// <summary>The library family's body switch (<see cref="PageMotionPolicy.LibraryPivot"/>) for the tier the host is drawn in.</summary>
    public static LayoutTransition? LibraryPivot(bool fromRight, TierFacts facts)
    {
        if (FluentGpu.Dsl.Motion.ReducedMotion) return s_reduced;
        int tier = Math.Clamp((int)WinUiDetailTiers.Of(in facts).Tier - (int)WidthTier.Compact, 0, 3);
        return s_libraryPivot[(fromRight ? 4 : 0) + tier];
    }

    // ── THE CONNECTED NAVIGATION FLIGHT (a picture into the header of the page it opens, and back) ──────────────────────────────
    // A card's, a row's or a tile's cover into an album's, a playlist's, a show's or an episode's header cover; a generated cover (Liked
    // Songs, a playlist with no image) as one whole picture. Not the artist: its page grows out of the item instead (GrowInto, below). WinUI's ConnectedAnimation runs 300 ms on
    // (0.8, 0, 0.2, 1), its Back (Direct) configuration 150-200 ms on (0.1, 0.9, 0.2, 1). Here a critically damped spring whose
    // response grows with the distance (a turn-around keeps its velocity): 90 % of the way at 0.62 x the response, 99 % at 1.06 x,
    // so forward reads as 250-340 ms and Back 15 % quicker. No arc, no dip, no overshoot.
    /// <summary>The shortest response (s): a short hop.</summary>
    public const float ConnectedResponseMin = 0.24f;
    /// <summary>The longest response (s): across the window.</summary>
    public const float ConnectedResponseMax = 0.32f;
    /// <summary>The response added per DIP of centre-to-centre travel (s).</summary>
    public const float ConnectedResponsePerDip = 0.00008f;
    /// <summary>Back's response against forward's.</summary>
    public const float ConnectedBackScale = 0.85f;
    /// <summary>The least share of a cover on screen that flies (under it: the plain swap).</summary>
    public const float ConnectedMinVisible = 0.5f;
    /// <summary>The longest the flyer holds at the card for the page to mount: WinUI mounts it on the click frame.</summary>
    public const float ConnectedPendingMs = 0f;
    /// <summary>An abandoned flyer's fade where it stands (ControlFaster).</summary>
    public const float ConnectedAbandonFadeMs = 83f;
    /// <summary>The longest a landed flyer stands in for a header picture that is not drawable yet (a cold page loading its cover) before it
    /// fades out and the page's own entrance takes over.</summary>
    public const float ConnectedHandOffWaitMs = 2500f;
    /// <summary>The smallest picture that flies (its shorter edge, DIP): rows' 40 px art and up; a sidebar's 16 px thumb does not.</summary>
    public const float ConnectedMinSourceEdge = 40f;

    static readonly FluentGpu.Animation.ConnectedNavSpec s_connected = new(ConnectedResponseMin, ConnectedResponseMax, ConnectedResponsePerDip,
        ConnectedBackScale, ConnectedMinVisible, ConnectedPendingMs, ConnectedAbandonFadeMs, ConnectedHandOffWaitMs,
        HandOffFadeMs: 0f, MinSourceEdge: ConnectedMinSourceEdge);

    // ── THE ARTIST GROWS (nothing flies) ─────────────────────────────────────────────────────────────────────────────────────
    // The owner, 2026-11-16, after the artist's round photo flew and morphed into the 1088x260 header photograph: "please think of something
    // for artist navigation, maybe just scale. dont copy the image". So no picture leaves the card: the artist page grows out of the item that
    // opened it (a top result, a circle card, a row, a tile). It scales from GrowEnterScale to 1 about that item's centre and fades in from 0,
    // on the flights' spring family (critically damped, GrowResponse: 90 % in ~175 ms, settled to the pixel by ~300 ms), while the old page
    // keeps only its recipe's fade (the quiet swap). The page's sections keep their rise (the owner: "keep blur rise"). Back shrinks the page
    // to GrowBackScale toward the same item and fades it out on the spring x ConnectedBackScale while the restored page fades in; an item no
    // longer half on screen (or a page box that changed size since) gives the page's centre: WinUI's own drill back, a page receding, which
    // stays calm and never points at something the user cannot see. Mouse Forward after Back grows from the remembered item again.
    // Material's container transform and iOS's zoom transition grow a page from its card the same way; WinUI's drill in scales 0.94 -> 1.
    /// <summary>The artist page's scale as it starts to grow (WinUI's drill in is 0.94; a little more travel reads as coming out of the item).</summary>
    public const float GrowEnterScale = 0.92f;
    /// <summary>Where the artist page shrinks to on Back.</summary>
    public const float GrowBackScale = 0.94f;
    /// <summary>The grow's spring response (s): the flights' mid response for a half-window hop.</summary>
    public const float GrowResponse = 0.28f;

    static readonly GrowNav s_grow = new(GrowEnterScale, GrowBackScale, EnterOpacity: 0f, GrowResponse, ConnectedBackScale, ConnectedMinVisible);

    /// <summary>The kinds whose page grows out of its item instead of flying a picture (<see cref="PageMotionPolicy.GrowInto"/>): the artist.</summary>
    static GrowNav? GrowInto(EntityKind kind) => kind == EntityKind.Artist ? s_grow : null;

    // ── A SECTION'S "SEE ALL" GROWS OUT OF THE SECTION ─────────────────────────────────────────────────────────────────────
    // The owner, 2026-11-18, on a recording of "See all": "a LOT more smooth and less out of body experience, something with connected or
    // expanding. now it feels too heavy and like we are leaving the page". The swap was the drill-in of any other page: Home left, a page
    // arrived, its cards rose one after another. The drill is the section the user was looking at, with the rest of its cards: so the page
    // grows out of the "See all" link it was opened from (0.96 to 1 about that point, fading in on the flights' spring, while Home only
    // fades: no slide, no gap between the two), as one block with no stagger, and it opens on the cards the section already showed
    // (SectionScreen holds a place for the rest). Back shrinks it toward the same point while Home fades back where it was left.
    /// <summary>The section page's scale as it starts to grow: a short travel, it is the same content getting room.</summary>
    public const float SectionGrowEnterScale = 0.96f;
    /// <summary>Where the section page shrinks to on Back.</summary>
    public const float SectionGrowBackScale = 0.97f;
    /// <summary>The section grow's spring response (s): the flights' short hop.</summary>
    public const float SectionGrowResponse = 0.24f;

    static readonly GrowNav s_sectionGrow = new(SectionGrowEnterScale, SectionGrowBackScale, EnterOpacity: 0f, SectionGrowResponse, ConnectedBackScale, MinVisible: 0f);

    /// <summary>The policy WinUI publishes (<c>ThemePolicies.PageMotion</c>).</summary>
    public static PageMotionPolicy Policy { get; } = new(RecipeFor, CascadeFor)
    {
        ReducedFade = s_reduced, SectionJump = s_sectionJump, LibraryPivot = LibraryPivot, Connected = s_connected,
        GrowInto = GrowInto, GrowIntoSection = s_sectionGrow,
    };
}
