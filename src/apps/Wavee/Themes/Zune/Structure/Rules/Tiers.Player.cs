// ── Themes/Zune/Structure/Rules/Tiers.Player.cs — Zune's play bar and right panel per tier (themes plan 3.2, 6.3; P5 Z4a) ────────────
//
// THE PLAY BAR'S SIX LEVELS (board Z-Part-PlayBar's `tier` enum, Z-PlayBar, Z-PlayBar-Small / -Huge / -Compact): the core's level
// machine walks them over the window width with a 24-DIP narrowing hold and clamps the result to the committed window tier's range
// (`PlayerBarPolicy.Windows`, from the records below), so a level that belongs to a tier changes on the crossing frame and animates.
//
//   level    from    tier      what it draws (the board's `tools` list and its identity column)
//   mini     0       Compact   the 64 mini bar: the 2-px line, art 44 · title · artist (one door to now playing), the disc and next (+10)
//   narrow   640     Small     the 200 identity column, previous · disc · next over the elapsed time, mute · devices · "…"
//   medium   760     Small/Std the 260 column with the heart, mute · lyrics · devices · "…" (shuffle, repeat and the queue move into "…")
//   full     1240    Standard  the 320 column, both times, shuffle · repeat · mute · the volume line · lyrics · video · queue · devices ·
//                              full screen · "…"
//   wide     1920    Huge      the full set in a 420 column, 32 apart, 48 in
//   big      3200    Huge 4K   the full set at 88: art 64, the 48 disc, 44 targets, title 17, words 15, 64 in
//
// The levels' own numbers (identity column, art, gaps, targets, the disc) are published to the core through `PlayerTier` so the bar's
// `PlayerBarMetrics` carries them; the look numbers the core has no field for (type, the words, the volume line) are `ZuneBarLook`,
// found from the metrics' identity column (each level's is distinct). A view never reads a width.
//
// THE BAR'S SLOT. The frame reserves the desktop bar's 72 at every tier (`ZuneTiers.BarReserveH`), so the mini bar (64) sits at the foot
// of the same slot and a 640 crossing moves nothing above it; Huge 4K's slot is 88 and its crossing animates the slot's height.
//
// THE BAR ACROSS A CROSSING (ZT2). The slot's height (72 ↔ 88 at 3200) and the bar's inside it (the mini 64 ↔ 72 at 640) ease through real
// layout (`SlotMotion`, `BarMotion`: size channels), so the page above and the bar's foot ride the re-solve; a position slide would be snapped
// by the engine's reflow-shove rule, as the title bar reflows on the same crossing (ZT1's lesson).
//
// THE BAR'S MARGINS AND RHYTHM (ZT2, the owner's review "check margins professionally"). Vertically: the 2-px seek line lies on the bar's top
// edge (it takes no room), and the transport cluster (the 40 disc row, 4, the 16 times; 48, 6, 16 at Huge 4K) is centred in the whole bar:
// 6 / 6 of air in the 72, 9 / 9 in the 88; the 48 cover sits 12 / 12 (the mini 44 in the 64: 10 / 10). Horizontally: the row's left inset is
// the frame's gutter per window tier (16 / 24 / 48 / 48 / 64: the cover lines up with the band and the page titles), its right inset puts the
// last tool's glyph on the same gutter (`RowInsets`); the identity column keeps the board's 12 between cover and words, the transport its
// 12 / 16 between buttons, the tools the board's 2 (each target carries its own air). The insets are boxes that ease on a crossing.
//
// THE PANEL PER WINDOW TIER is `Tiers.Panel.cs` (`ZunePanelTiers`).
//
// Literal numbers are allowed in this file (WVTH003 exempts `Rules\**`).

using System.Collections.Immutable;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

/// <summary>The play bar's six levels, narrowest first (their index is the core's level).</summary>
public enum ZuneBarLevel : byte
{
    /// <summary>The Compact mini bar.</summary>
    Mini = 0,
    /// <summary>640: the 200 identity column, mute · devices · "…".</summary>
    Narrow = 1,
    /// <summary>760: the 260 column and the heart, mute · lyrics · devices · "…".</summary>
    Medium = 2,
    /// <summary>1240: the full tool set.</summary>
    Full = 3,
    /// <summary>1920: the full set in the 420 column.</summary>
    Wide = 4,
    /// <summary>3200: the 88 bar.</summary>
    Big = 5,
}

/// <summary>The form the bar takes in a window tier.</summary>
public enum ZuneBarForm : byte
{
    /// <summary>The 64 mini bar (Compact).</summary>
    Mini,
    /// <summary>The bar: the seek line over the transport row.</summary>
    Bar,
}

/// <summary>The play bar's record for one window tier.</summary>
/// <param name="Tier">The window tier.</param>
/// <param name="Form">The bar's form.</param>
/// <param name="MinLevel">The lowest level the tier lays the bar out at.</param>
/// <param name="MaxLevel">The highest level.</param>
/// <param name="SlotH">The slot the bar keeps in the frame: the reserved 72, 88 at Huge 4K.</param>
/// <param name="BarH">The bar inside it: 64 for the mini bar, else the slot.</param>
public sealed record ZunePlayerBarTier(WidthTier Tier, ZuneBarForm Form, ZuneBarLevel MinLevel, ZuneBarLevel MaxLevel, float SlotH, float BarH);

/// <summary>What one level of the bar looks like beyond the numbers the core carries (Z-Part-PlayBar).</summary>
/// <param name="Level">The level.</param>
/// <param name="IdW">The identity column (art · title · heart); 0 for the mini bar, whose door grows.</param>
/// <param name="Art">The cover: 44 mini, 48, 64 at Huge 4K.</param>
/// <param name="RowGap">Between the identity column, the transport and the tools: 12 mini, 24, 32 wide.</param>
/// <param name="PadLeft">The row's left inset: 16 mini, 24, 48 wide, 64 at Huge 4K.</param>
/// <param name="PadRight">The row's right inset: 8 mini, else 12.</param>
/// <param name="Button">A glyph target: 40, 44 at Huge 4K.</param>
/// <param name="Glyph">A glyph: 16, 19 at Huge 4K.</param>
/// <param name="Disc">The accent play disc: 40, 48 at Huge 4K.</param>
/// <param name="DiscGlyph">The disc's glyph box before the solid scale: 20, 24 at Huge 4K.</param>
/// <param name="TransportGap">Between the transport's buttons: 4 mini, 12 narrow and medium, 16 from full.</param>
/// <param name="WordSize">The tool words (lyrics, queue, devices, the podcast steps): 13, 15 at Huge 4K.</param>
/// <param name="TitleSize">The title's size and line: 15/20, 17/22 at Huge 4K.</param>
/// <param name="TitleLine">The title's line height.</param>
/// <param name="SubSize">The artist line: 13/18, 15/20 at Huge 4K (13/16 in the mini bar).</param>
/// <param name="SubLine">The artist line's height.</param>
/// <param name="FromSize">The accent "playing on" line above the title: 11/14, 13/18 at Huge 4K.</param>
/// <param name="FromLine">Its line height.</param>
/// <param name="VolumeW">The inline volume line: 96, 128 from wide.</param>
/// <param name="TimesGap">Between the transport row and the times under it: 4, 6 at Huge 4K (ZT2, the owner's review: the board's 2 read as
/// jammed under the disc). The cluster (40 + 4 + 16, 48 + 6 + 16) is centred in the whole bar, 6 / 6 of air in the 72, 9 / 9 in the 88.</param>
public sealed record ZuneBarLook(ZuneBarLevel Level, float IdW, float Art, float RowGap, float PadLeft, float PadRight, float Button, float Glyph,
    float Disc, float DiscGlyph, float TransportGap, float WordSize, float TitleSize, float TitleLine, float SubSize, float SubLine,
    float FromSize, float FromLine, float VolumeW, float TimesGap);

/// <summary>Zune's play bar and panel per tier (public so the tests pin them).</summary>
public static class ZunePlayerTiers
{
    // ── the bar's levels ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Where each level starts (the board's tier enum): 0 / 640 / 760 / 1240 / 1920 / 3200.</summary>
    public const float NarrowFrom = 640f, MediumFrom = 760f, FullFrom = 1240f, WideFrom = 1920f, BigFrom = 3200f;

    /// <summary>Narrowing holds every level through a 24-DIP dip (the board's hysteresis); widening commits at the line.</summary>
    public const float Hold = 24f;

    /// <summary>The seek row in the bar: the 2-px line (seekTimes InFace), or 16 tall with the times at its ends (AtSliderEnds).</summary>
    public const float SeekLineH = 2f, SeekEndsRowH = 16f;

    private static readonly ZuneBarLook[] s_looks =
    [
        new(ZuneBarLevel.Mini, IdW: 0f, Art: 44f, RowGap: 12f, PadLeft: 16f, PadRight: 8f, Button: 40f, Glyph: 16f, Disc: 40f, DiscGlyph: 20f,
            TransportGap: 4f, WordSize: 13f, TitleSize: 15f, TitleLine: 20f, SubSize: 13f, SubLine: 16f, FromSize: 11f, FromLine: 14f, VolumeW: 96f, TimesGap: 4f),
        new(ZuneBarLevel.Narrow, IdW: 200f, Art: 48f, RowGap: 24f, PadLeft: 24f, PadRight: 12f, Button: 40f, Glyph: 16f, Disc: 40f, DiscGlyph: 20f,
            TransportGap: 12f, WordSize: 13f, TitleSize: 15f, TitleLine: 20f, SubSize: 13f, SubLine: 18f, FromSize: 11f, FromLine: 14f, VolumeW: 96f, TimesGap: 4f),
        new(ZuneBarLevel.Medium, IdW: 260f, Art: 48f, RowGap: 24f, PadLeft: 24f, PadRight: 12f, Button: 40f, Glyph: 16f, Disc: 40f, DiscGlyph: 20f,
            TransportGap: 12f, WordSize: 13f, TitleSize: 15f, TitleLine: 20f, SubSize: 13f, SubLine: 18f, FromSize: 11f, FromLine: 14f, VolumeW: 96f, TimesGap: 4f),
        new(ZuneBarLevel.Full, IdW: 320f, Art: 48f, RowGap: 24f, PadLeft: 24f, PadRight: 12f, Button: 40f, Glyph: 16f, Disc: 40f, DiscGlyph: 20f,
            TransportGap: 16f, WordSize: 13f, TitleSize: 15f, TitleLine: 20f, SubSize: 13f, SubLine: 18f, FromSize: 11f, FromLine: 14f, VolumeW: 96f, TimesGap: 4f),
        new(ZuneBarLevel.Wide, IdW: 420f, Art: 48f, RowGap: 32f, PadLeft: 48f, PadRight: 12f, Button: 40f, Glyph: 16f, Disc: 40f, DiscGlyph: 20f,
            TransportGap: 16f, WordSize: 13f, TitleSize: 15f, TitleLine: 20f, SubSize: 13f, SubLine: 18f, FromSize: 11f, FromLine: 14f, VolumeW: 128f, TimesGap: 4f),
        new(ZuneBarLevel.Big, IdW: 520f, Art: 64f, RowGap: 32f, PadLeft: 64f, PadRight: 12f, Button: 44f, Glyph: 19f, Disc: 48f, DiscGlyph: 24f,
            TransportGap: 16f, WordSize: 15f, TitleSize: 17f, TitleLine: 22f, SubSize: 15f, SubLine: 20f, FromSize: 13f, FromLine: 18f, VolumeW: 128f, TimesGap: 6f),
    ];

    /// <summary>The transport row's insets in window tier <paramref name="window"/> at <paramref name="look"/> (ZT2's margin audit): the
    /// left inset is the frame's gutter (16 / 24 / 48 / 48 / 64), so the cover lines up with the band and every page title above it; the
    /// right inset puts the last tool's GLYPH on the same gutter (the gutter less the target's own air, (button - glyph) / 2: 12 at Small,
    /// 36 at Standard and Huge, 52 at Huge 4K). The mini bar keeps the board's 16 / 8 (its gutter is 16; its last target is the 40 next).
    /// The board's 24 at Standard is the conflict this settles (the owner: the bar's gutter is the frame's).</summary>
    public static Edges4 RowInsets(WidthTier window, ZuneBarLook look)
    {
        if (look.Level == ZuneBarLevel.Mini) return new Edges4(look.PadLeft, 0f, look.PadRight, 0f);
        float gutter = ZuneTiers.Frame(window).Gutter;
        return new Edges4(gutter, 0f, MathF.Round(MathF.Max(0f, gutter - (look.Button - look.Glyph) / 2f)), 0f);
    }

    /// <summary>The look of <paramref name="level"/>.</summary>
    public static ZuneBarLook Look(ZuneBarLevel level) => s_looks[Math.Clamp((int)level, 0, s_looks.Length - 1)];

    /// <summary>The look of the level the core resolved, found from its identity column (each level's is distinct); the window tier's
    /// highest level where none matches (a policy that is not Zune's).</summary>
    public static ZuneBarLook LookOf(in PlayerBarMetrics metrics, WidthTier window)
    {
        foreach (var look in s_looks)
            if (look.IdW == metrics.LeftW) return look;
        return Look(Bar(window).MaxLevel);
    }

    // ── the bar per window tier ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Compact: the mini bar (64) at the foot of the reserved 72.</summary>
    public static ZunePlayerBarTier CompactBar { get; } = new(WidthTier.Compact, ZuneBarForm.Mini, ZuneBarLevel.Mini, ZuneBarLevel.Mini, ZuneTiers.BarReserveH, 64f);

    /// <summary>Small (640-1007): narrow below 760, medium from it.</summary>
    public static ZunePlayerBarTier SmallBar { get; } = new(WidthTier.Small, ZuneBarForm.Bar, ZuneBarLevel.Narrow, ZuneBarLevel.Medium, ZuneTiers.BarReserveH, ZuneTiers.BarReserveH);

    /// <summary>Standard (1008-1919): medium below 1240, full from it.</summary>
    public static ZunePlayerBarTier StandardBar { get; } = new(WidthTier.Standard, ZuneBarForm.Bar, ZuneBarLevel.Medium, ZuneBarLevel.Full, ZuneTiers.BarReserveH, ZuneTiers.BarReserveH);

    /// <summary>Huge (1920-3199): wide.</summary>
    public static ZunePlayerBarTier HugeBar { get; } = new(WidthTier.Huge, ZuneBarForm.Bar, ZuneBarLevel.Wide, ZuneBarLevel.Wide, ZuneTiers.BarReserveH, ZuneTiers.BarReserveH);

    /// <summary>Huge 4K (from 3200): big, 88 tall.</summary>
    public static ZunePlayerBarTier Huge4KBar { get; } = new(WidthTier.Huge4K, ZuneBarForm.Bar, ZuneBarLevel.Big, ZuneBarLevel.Big, 88f, 88f);

    private static readonly ZunePlayerBarTier[] s_bars = [CompactBar, SmallBar, StandardBar, HugeBar, Huge4KBar];

    /// <summary>The record of window tier <paramref name="window"/>.</summary>
    public static ZunePlayerBarTier Bar(WidthTier window) => s_bars[Math.Clamp((int)window, 0, s_bars.Length - 1)];

    /// <summary>The record for <paramref name="facts"/>; Standard's where no frame resolved the tiers (a view mounted alone).</summary>
    public static ZunePlayerBarTier BarOf(in TierFacts facts) => Bar(facts.Policy is null ? WidthTier.Standard : facts.Window);

    // ── the levels the core walks ─────────────────────────────────────────────────────────────────────────────────────

    const PlayerBarSlots Identity = PlayerBarSlots.Devices | PlayerBarSlots.Subtitle | PlayerBarSlots.Title | PlayerBarSlots.PrevNext
        | PlayerBarSlots.RemoteDeviceLine;

    const PlayerBarSlots NarrowSet = Identity | PlayerBarSlots.VolumeButton | PlayerBarSlots.TimesElapsed;

    const PlayerBarSlots MediumSet = NarrowSet | PlayerBarSlots.LikeSlot | PlayerBarSlots.Lyrics;

    const PlayerBarSlots FullSet = MediumSet | PlayerBarSlots.ShuffleRepeat | PlayerBarSlots.VolumeSlider | PlayerBarSlots.Queue
        | PlayerBarSlots.VideoSplit | PlayerBarSlots.Expand | PlayerBarSlots.TimesRemaining;

    private static PlayerTier Level(float from, PlayerBarSlots slots, ZuneBarLook look) => new(
        from, slots,
        LeftW: look.IdW, ArtSize: look.Art, RowGap: look.RowGap, RowPad: look.PadRight, ClusterGap: look.TransportGap, LeftGap: 12f,
        SeekGap: 0f, RightGap: 2f, ButtonBox: look.Button, ButtonGlyph: look.Glyph, PrimaryBox: look.Disc, PrimaryGlyph: look.DiscGlyph);

    /// <summary>The bar's policy: the six levels, the 24 hold, the reserved 72, and the levels each window tier may use.</summary>
    public static PlayerBarPolicy Policy { get; } = new(
        ImmutableArray.Create(
            Level(0f, Identity, s_looks[0]),
            Level(NarrowFrom, NarrowSet, s_looks[1]),
            Level(MediumFrom, MediumSet, s_looks[2]),
            Level(FullFrom, FullSet, s_looks[3]),
            Level(WideFrom, FullSet, s_looks[4]),
            Level(BigFrom, FullSet, s_looks[5])),
        Hysteresis: Hold, Height: ZuneTiers.BarReserveH)
    {
        Windows = ImmutableArray.Create(
            Rule(CompactBar), Rule(SmallBar), Rule(StandardBar), Rule(HugeBar), Rule(Huge4KBar)),
    };

    private static PlayerBarWindowRule Rule(ZunePlayerBarTier bar) => new((int)bar.MinLevel, (int)bar.MaxLevel);

    // ── the bar across a crossing ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The bar's slot across the 3200 crossing (72 ↔ 88): the pivot slide's 250 ms on the smooth-out curve, a reflow on the height
    /// axis (the title bar's own motion, ZT1), so the page above yields frame by frame. Null under reduced motion at the call site.</summary>
    public static readonly LayoutTransition SlotMotion = new(TransitionChannels.Size,
        TransitionDynamics.Tween(ZuneMotion.PivotSlideMs, Easing.SmoothOut),
        Size: SizeMode.Reflow, Anchor: SizeAnchor.Trailing, Axes: SizeAxes.Height);

    /// <summary>The bar inside its slot across a crossing (the mini 64 ↔ 72 at 640, 72 ↔ 88 at 3200): the same reflow, so its top edge rises
    /// and falls with no position slide. Null under reduced motion at the call site.</summary>
    public static readonly LayoutTransition BarMotion = SlotMotion;

    /// <summary>A row inset across a crossing that moves the gutter (24 ↔ 48 at 1008, 48 ↔ 64 at 3200): the pivot slide's 250 ms, a reflow on
    /// the width axis, so the identity, the transport and the tools ride the re-solve with no position slide. Null under reduced motion.</summary>
    public static readonly LayoutTransition InsetMotion = new(TransitionChannels.Size,
        TransitionDynamics.Tween(ZuneMotion.PivotSlideMs, Easing.SmoothOut),
        Size: SizeMode.Reflow, Anchor: SizeAnchor.Leading, Axes: SizeAxes.Width);

    /// <summary>The panel's one width (`ZunePanelTiers.PanelW`; the bar's tests and the panel's share it).</summary>
    public const float PanelW = ZunePanelTiers.PanelW;
}
