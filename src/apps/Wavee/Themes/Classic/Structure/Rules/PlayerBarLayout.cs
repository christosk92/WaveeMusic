// ── Themes/Classic/Structure/Rules/PlayerBarLayout.cs — the numbers of Classic's player bar (themes plan 6.1, T6a) ─────────
//
// What `Shell.PlayerBar.UI.cs` held as file-level constants beside the bar, moved verbatim with the bar's picture: the motion
// specs (a level crossing FLIPs every structural node over 167 ms, a line enters or leaves with a fade, a slot lights over the
// control-normal beat, the face cross-fades), the marquee's look, the title and meta type, the remote-device line, the volume
// popup's size and the podcast speed panel's. They are data, not decisions: `Views\Player\ClassicPlayerBar.cs` and
// `ClassicPlayerBarPodcast.cs` read them (WVTH003 allows literals only here, in `Rules\**`).
//
// WHERE THE LEVELS ARE. The six pressure levels (where each starts, which slots it carries, its spacing, cover and hit
// targets, the narrowing hold and the bar's height) are NOT here: they are `PlayerBarPolicy`'s, declared by `ClassicPolicies`,
// because the core's level machine runs on them (`Shell.PlayerBar`, `Shell.PlayerBarLayout.ForTier`). What this file adds is what
// the policy does not carry: the look of the parts inside a level.

using FluentGpu.Animation;
using FluentGpu.Foundation;

namespace Wavee.Themes.Classic;

/// <summary>The constants of Classic's player bar.</summary>
internal static class ClassicPlayerBarLayout
{
    // ── the motion specs (ch 20 §5) ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>A breakpoint crossing: every structural node FLIPs to its new rect over 167 ms.</summary>
    public static readonly LayoutTransition MoveMotion = new(
        TransitionChannels.Bounds, TransitionDynamics.Tween(Design.Motion.Fast, Easing.FluentPopOpen), SizeMode.Reveal);

    /// <summary>A cluster the bar's FORM adds or drops on a crossing (the seek row and the right cluster across the mini bar's 528 edge): it FLIPs
    /// like <see cref="MoveMotion"/> and fades in or out as it comes or goes, instead of popping beside the parts that glide.</summary>
    public static readonly LayoutTransition FormMotion = MoveMotion with
    {
        Channels = TransitionChannels.Bounds | TransitionChannels.Opacity,
        Enter = new EnterExit(Opacity: 0f, Active: true), Exit = new EnterExit(Opacity: 0f, Active: true),
    };

    /// <summary>A line entering/leaving a column: fade + size 0 → natural, so neighbours SLIDE over (Reflow is not
    /// resize-gated, so it animates during a pointer resize too). 250 in, 167 out. ONLY the remote-device line and the
    /// two time labels carry it: every command slot is fixed by the level and lights its face instead (<see cref="FaceMotion"/>),
    /// so nothing in the row enters or leaves with playback STATE. The one exception is the video split, which exists iff the
    /// current track has a video and rides the right box's <see cref="MoveMotion"/> when it comes or goes (user decision 2026-09-16).</summary>
    public static readonly LayoutTransition ItemMotion = new(
        TransitionChannels.Bounds | TransitionChannels.Opacity,
        TransitionDynamics.Tween(Design.Motion.Standard, Easing.SmoothOut),
        SizeMode.Reflow,
        Enter: new EnterExit(Opacity: 0f, Active: true),
        Exit: new EnterExit(Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(Design.Motion.Fast, Easing.SmoothOut));

    /// <summary>THE FACE SWAP. When the shown identity genuinely changes — the face onto a DIFFERENT playable, the one change a
    /// skip cannot avoid — the identity block CROSS-FADES instead of cutting: opacity 0 → 1 over the Standard beat on
    /// <see cref="Easing.FluentDecelerate"/>, played on the meta column's captured node (the same imperative shape the heart's
    /// like-pop uses, and for the same reason: a keyed remount of the marquee host would reset it mid-swap).
    ///
    /// <para>OPACITY ONLY, never through the layout: no scale, no slide, no Bounds/Reflow channel — the row must not
    /// move while it re-letters (the rule <see cref="FaceMotion"/> already follows for a lighting slot). The ART is
    /// deliberately NOT on this timeline: the engine already dissolves an <c>ImageEl</c> whose source changes from the
    /// old texture to the new (hold-last-good → <c>ImageCache.SwapCrossfadeMs</c>, 150 ms), and a second fade stacked
    /// on top would double-dip the tile. The art blanked to shimmer before this change only because the art url
    /// went NULL mid-skip and the tile un-mounted its image outright; holding the url is that whole fix.</para>
    ///
    /// <para>Reduced motion is a VALUE, not a branch: the token carries <see cref="ReducedMotionPolicy.KeepFade"/>,
    /// the engine's own answer for an opacity cross-fade — a fade aids orientation, it is not motion.</para></summary>
    public static readonly MotionTokenDef FaceSwap =
        MotionTokenDef.Eased(Design.Motion.Standard, Easing.FluentDecelerate, ReducedMotionPolicy.KeepFade);

    /// <summary>The face fade of a reserved slot: opacity only, never Bounds/Reflow, so lighting a button cannot reflow the row.</summary>
    public static readonly LayoutTransition FaceMotion = new(
        TransitionChannels.Opacity, TransitionDynamics.Tween(Design.Motion.Standard, Easing.SmoothOut), SizeMode.Auto);

    /// <summary>Shuffle's and repeat's slots: the face fade plus a position FLIP (still never Bounds/Reflow, so lighting cannot reflow the row),
    /// so the 1920 crossing glides them between the right cluster's lead and the Huge transport's flanks, both in the centre column.</summary>
    public static readonly LayoutTransition FlankMotion = new(
        TransitionChannels.Opacity | TransitionChannels.Position, TransitionDynamics.Tween(Design.Motion.Standard, Easing.SmoothOut), SizeMode.Auto);

    // ── the now-playing text ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The now-playing marquee's look: its pace (DIP per second) and the cut edge's fade. The behaviour (the start at rest, two
    /// passes after a track change, a pass on hover, one line moving at a time) is the engine control's, the same in every theme.</summary>
    public const float MarqueeSpeed = 30f, MarqueeFade = 12f;

    /// <summary>The stage's display title passes at the bar's reading rate in ems, a little calmer for the large face: 1.8 em per second of the
    /// title's own size (the bar's 30 DIP/s over its 13-14 DIP type is about 2.2 em/s).</summary>
    public const float StageMarqueeEmRate = 1.8f;

    /// <summary>The title line's size.</summary>
    public const float TitleSize = 14f;

    /// <summary>The title line's weight.</summary>
    public const int TitleWeight = 700;

    /// <summary>The gap between the title and the artist line.</summary>
    public const float MetaGap = 2f;

    /// <summary>The cover's corner radius.</summary>
    public const float ArtCorner = 6f;

    /// <summary>The app-local critical ink for an errored title (≈ #ED6B73, ch 00 §12.2 magic number).</summary>
    public static readonly ColorF CriticalInk = new(0.93f, 0.42f, 0.45f, 1f);

    /// <summary>The top edge's one seam in light themes. Light takes a black ALPHA literal, not StrokeCardDefault, which every
    /// seeded light preset resolves OPAQUE.</summary>
    public static readonly ColorF TopEdgeSeamLight = ColorF.FromRgba(0, 0, 0, 0x0F);

    // ── the buttons ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Segoe Fluent Icons' SOLID transport pair (PlaySolid / PauseSolid). The primary sits on a filled accent
    /// disc, and the outline pair there reads as a thin dark wireframe on a bright plate; on a filled plate the glyph has to be
    /// a filled shape too.</summary>
    public const string PlaySolid = "", PauseSolid = "";

    /// <summary>The primary's glyph is drawn at this fraction of the level's glyph size — a solid shape carries more weight than
    /// the outline the size was tuned for.</summary>
    public const float PrimaryGlyphScale = 0.8f;

    /// <summary>The play triangle is nudged right by a twelfth of its size (<c>size / PlayNudgeDivisor</c>, rounded), its optical
    /// centre (the pause bars are symmetric and stay put).</summary>
    public const float PlayNudgeDivisor = 12f;

    /// <summary>The split video button's chevron glyph.</summary>
    public const float SplitChevronGlyph = 10f;

    // ── the remote-device line ("Playing on ...", above the title) ───────────────────────────────────────────────────

    /// <summary>The line's height.</summary>
    public const float RemoteLineHeight = 13f;

    /// <summary>The line's icon and text size.</summary>
    public const float RemoteLineSize = 12f;

    /// <summary>The line's text height.</summary>
    public const float RemoteLineTextHeight = 16f;

    /// <summary>The line's weight.</summary>
    public const int RemoteLineWeight = 600;

    /// <summary>The line's accent at rest; full accent on hover.</summary>
    public const float RemoteLineRestAlpha = 0.88f;

    // ── the volume popup (the levels without the inline rail) ────────────────────────────────────────────────────────

    /// <summary>The popup's size.</summary>
    public const float VolumePopupWidth = 52f, VolumePopupHeight = 168f;

    /// <summary>The popup's padding: 10 above and below, 14 either side.</summary>
    public static readonly Edges4 VolumePopupPadding = new(10f, 14f, 10f, 14f);

    /// <summary>The vertical rail's length and thickness.</summary>
    public const float VolumePopupRailLength = 124f, VolumePopupRailThickness = 32f;

    // ── the podcast transport ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The step buttons skip this far (ms), back and forward.</summary>
    public const int SeekStepMs = 10_000;

    /// <summary>The step buttons' label size and weight; the speed button's.</summary>
    public const float StepLabelSize = 12f;

    /// <summary>The step and speed labels' weight.</summary>
    public const int StepLabelWeight = 600;

    /// <summary>The speed panel's width and its slider's length.</summary>
    public const float SpeedPanelWidth = 256f, SpeedSliderLength = 224f;

    /// <summary>The speed slider's range and steps (the model clamps to the same 0.5-3 band).</summary>
    public const float SpeedMin = .5f, SpeedMax = 3f, SpeedStep = .05f, SpeedLargeStep = .25f;

    /// <summary>The presets under the speed slider.</summary>
    public static readonly float[] SpeedPresets = [1f, 1.5f, 2f, 3f];
}
