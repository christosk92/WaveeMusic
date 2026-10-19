// ── Themes/WinUi/Structure/Rules/Tiers.Player.cs — WinUI's play bar and side panel per tier (themes plan 6.2; P6 W4a) ─────────────────
//
// THE PLAY BAR'S LEVELS (boards Shell-Player's `tier` enum, W-Tiers "Player bar tiers: what collapses into ⋯"). The core's level machine walks
// them over the WINDOW width with the 24-DIP narrowing hold and clamps the result to the committed window tier's range (`PlayerBarPolicy.Windows`),
// so a level that belongs to a window tier changes on the crossing frame:
//
//   level     from    window tier      what the bar carries (W-Tiers' player table)
//   Mini      0       Compact          64 tall: the 2-px progress edge, art 40 · title · artists as ONE door to the sheet, heart, play, next, ⋯
//   Compact   641     Small            the 300 seek column with both times, previous · play · next, devices (icon), mute (flyout slider), ⋯
//   Medium    860     Small, Standard  the 420 column; shuffle and repeat, lyrics and queue return; the video split; the "playing on" line
//   Full      1100    Standard         the 520 column; the 96 volume slider and full screen return
//   Huge      1920    Huge             the 720 column, the labelled "This PC" device button, the 128 slider
//   (Huge4K   3840    -                the Huge set again: the core needs six levels; WinUI's window tier never reaches it)
//
// Collapse order as the window narrows (W-Tiers): volume slider → full screen → lyrics, queue → shuffle, repeat → previous and the seek bar
// (Mini). The heart, play / pause and next never collapse. "⋯" lists what the bar no longer shows (the core's overflow rows, ordered by the
// view, `WinUiPlayerLayout.MenuOrder`). The bar's height (72) is reserved in every tier but Compact (the Mini's 64): the bar resizes only on
// the 641 crossing, through real layout (`SlotMotion`); every other crossing fades controls in and out in place (167 ms).
//
// The levels' own numbers reach the core through `PlayerTier`, so `PlayerBarMetrics` carries them. WinUI's identity column is not fixed
// (the board's `minmax(0, 1fr)`): `LeftW` carries the level's SEEK COLUMN instead (0 / 300 / 420 / 520 / 720), which is how the view finds its
// level's look (`LookOf`). A view never reads a width.
//
// THE PANEL (W-Tiers "Side panes: dock or overlay", W-Shell-Compact / Small / Huge, W-Queue). 340 wide, the docked pane resizable 280-480.
//   Compact   a bottom sheet (85 % high, the grabber, the tabs; it remembers its tab)
//   Small     docked when nav + 340 + 480 fits (the 48 rail: from 868), else it overlays from the right as acrylic at the same width
//   Standard  the same rule (the 280 pane: from 1100); one pane, tabbed (Queue · Lyrics · Now playing · Friends)
//   Huge      two panes of 340 docked side by side, each its own tab group: Queue + Now playing | Lyrics + Friends (a LINKED arrangement:
//             the Queue pane left of the primary, `DockedPane.Tabs`, P6 WK)
// The dock line is the core's `PanelTierRule.DockContentFloor` (480) with the board's 24 hold (P6 W4a seam): the docked pane turns into an
// overlay at the same width once the page would keep less than 480, and docks again once it has 24 more.
//
// Literal numbers are allowed in this file (WVTH003 exempts `Rules\**`).

using System.Collections.Immutable;
using FluentGpu.Animation;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

/// <summary>The play bar's six levels, narrowest first (their index is the core's level).</summary>
public enum WinUiBarLevel : byte
{
    /// <summary>The Compact Mini bar (64).</summary>
    Mini = 0,
    /// <summary>641: the 300 seek column.</summary>
    Compact = 1,
    /// <summary>860: the 420 column, shuffle, repeat, lyrics and queue.</summary>
    Medium = 2,
    /// <summary>1100: the 520 column, the volume slider and full screen.</summary>
    Full = 3,
    /// <summary>1920: the 720 column, the labelled device button, the 128 slider.</summary>
    Huge = 4,
    /// <summary>3840: the Huge set again (the core's sixth level; no WinUI window tier reaches it).</summary>
    Huge4K = 5,
}

/// <summary>What one level of the bar looks like beyond the numbers the core carries (Shell-Player).</summary>
/// <param name="Level">The level.</param>
/// <param name="SeekW">The centre column (the transport over the seek row): 300 / 420 / 520 / 720; 0 for the Mini bar.</param>
/// <param name="ColumnGap">Between the identity, the centre and the tools: 16 at Compact, 0 above (the centre is centred).</param>
/// <param name="Art">The cover: 40 Mini, 48.</param>
/// <param name="VolumeW">The inline volume slider: 96, 128 at Huge.</param>
/// <param name="DeviceLabel">The device button carries its name ("This PC") at Huge.</param>
/// <param name="Speed">An episode's speed button is in the right cluster (Medium up).</param>
public sealed record WinUiBarLook(WinUiBarLevel Level, float SeekW, float ColumnGap, float Art, float VolumeW, bool DeviceLabel, bool Speed)
{
    /// <summary>The Mini bar.</summary>
    public bool Mini => Level == WinUiBarLevel.Mini;
}

/// <summary>WinUI's play bar and panel per tier (public so the tests pin them).</summary>
public static class WinUiPlayerTiers
{
    // ── the bar's levels ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Where each level starts (W-Tiers): 641 / 860 / 1100 / 1920 (3840 for the unreachable sixth).</summary>
    public const float CompactFrom = 641f, MediumFrom = 860f, FullFrom = 1100f, HugeFrom = 1920f, Huge4KFrom = 3840f;

    /// <summary>Narrowing holds every level through a 24-DIP dip (W-Tiers); widening commits at the line.</summary>
    public const float Hold = 24f;

    /// <summary>The reserved bar (72) and the Mini bar (64).</summary>
    public const float BarH = WinUiTiers.BarH, MiniH = WinUiTiers.MiniBarH;

    private static readonly WinUiBarLook[] s_looks =
    [
        new(WinUiBarLevel.Mini, SeekW: 0f, ColumnGap: 4f, Art: 40f, VolumeW: 96f, DeviceLabel: false, Speed: false),
        new(WinUiBarLevel.Compact, SeekW: 300f, ColumnGap: 16f, Art: 48f, VolumeW: 96f, DeviceLabel: false, Speed: false),
        new(WinUiBarLevel.Medium, SeekW: 420f, ColumnGap: 0f, Art: 48f, VolumeW: 96f, DeviceLabel: false, Speed: true),
        new(WinUiBarLevel.Full, SeekW: 520f, ColumnGap: 0f, Art: 48f, VolumeW: 96f, DeviceLabel: false, Speed: true),
        new(WinUiBarLevel.Huge, SeekW: 720f, ColumnGap: 0f, Art: 48f, VolumeW: 128f, DeviceLabel: true, Speed: true),
        new(WinUiBarLevel.Huge4K, SeekW: 720.5f, ColumnGap: 0f, Art: 48f, VolumeW: 128f, DeviceLabel: true, Speed: true),
    ];

    /// <summary>The look of <paramref name="level"/>.</summary>
    public static WinUiBarLook Look(WinUiBarLevel level) => s_looks[Math.Clamp((int)level, 0, s_looks.Length - 1)];

    /// <summary>The look of the level the core resolved, found from the seek column its metrics carry (each level's is distinct); the window
    /// tier's highest level where none matches (a policy that is not WinUI's).</summary>
    public static WinUiBarLook LookOf(in PlayerBarMetrics metrics, WidthTier window)
    {
        foreach (var look in s_looks)
            if (look.SeekW == metrics.LeftW) return look;
        return Look((WinUiBarLevel)Rule(window).MaxLevel);
    }

    // ── the levels the core walks ─────────────────────────────────────────────────────────────────────────────────────

    // Every level: the identity block (title, artists, the heart), the device route and previous / next (the Mini bar draws next only and lists
    // previous in ⋯ itself: the core's pair would put next in ⋯ too).
    const PlayerBarSlots Identity = PlayerBarSlots.Devices | PlayerBarSlots.Subtitle | PlayerBarSlots.Title | PlayerBarSlots.LikeSlot
        | PlayerBarSlots.PrevNext;

    const PlayerBarSlots CompactSet = Identity | PlayerBarSlots.TimesElapsed | PlayerBarSlots.TimesRemaining | PlayerBarSlots.VolumeButton;

    const PlayerBarSlots MediumSet = CompactSet | PlayerBarSlots.ShuffleRepeat | PlayerBarSlots.Lyrics | PlayerBarSlots.Queue
        | PlayerBarSlots.RemoteDeviceLine | PlayerBarSlots.VideoSplit;

    const PlayerBarSlots FullSet = MediumSet | PlayerBarSlots.VolumeSlider | PlayerBarSlots.Expand;

    // The podcast bar (Shell-Player `variant=podcast`): no shuffle or repeat; previous / next episode only from Medium (Compact draws ±10 alone,
    // the Mini bar +10 alone); the volume slider stays.
    const PlayerBarSlots NoEpisodeSteps = ~(PlayerBarSlots.ShuffleRepeat | PlayerBarSlots.PrevNext);

    private static PlayerTier Level(float from, PlayerBarSlots slots, WinUiBarLook look, PlayerBarSlots podcast) => new(
        from, slots,
        LeftW: look.SeekW, ArtSize: look.Art, RowGap: look.ColumnGap, RowPad: 16f, ClusterGap: 8f, LeftGap: 12f,
        SeekGap: 8f, RightGap: 4f, ButtonBox: 40f, ButtonGlyph: 16f, PrimaryBox: 36f, PrimaryGlyph: 18f,
        PodcastSlots: podcast);

    /// <summary>The window tiers' level ranges (Compact first): Mini; Compact or Medium at Small; Medium or Full at Standard; Huge.</summary>
    private static readonly PlayerBarWindowRule[] s_rules =
    [
        new((int)WinUiBarLevel.Mini, (int)WinUiBarLevel.Mini),
        new((int)WinUiBarLevel.Compact, (int)WinUiBarLevel.Medium),
        new((int)WinUiBarLevel.Medium, (int)WinUiBarLevel.Full),
        new((int)WinUiBarLevel.Huge, (int)WinUiBarLevel.Huge),
    ];

    /// <summary>The level range of window tier <paramref name="window"/> (Huge's from 1920 up).</summary>
    public static PlayerBarWindowRule Rule(WidthTier window) => s_rules[Math.Clamp((int)window, 0, s_rules.Length - 1)];

    /// <summary>The bar's policy: the six levels, the 24 hold, the reserved 72, and the levels each window tier may use.</summary>
    public static PlayerBarPolicy Policy { get; } = new(
        ImmutableArray.Create(
            Level(0f, Identity, s_looks[0], Identity & NoEpisodeSteps),
            Level(CompactFrom, CompactSet, s_looks[1], CompactSet & NoEpisodeSteps),
            Level(MediumFrom, MediumSet, s_looks[2], MediumSet & ~PlayerBarSlots.ShuffleRepeat),
            Level(FullFrom, FullSet, s_looks[3], FullSet & ~PlayerBarSlots.ShuffleRepeat),
            Level(HugeFrom, FullSet, s_looks[4], FullSet & ~PlayerBarSlots.ShuffleRepeat),
            Level(Huge4KFrom, FullSet, s_looks[5], FullSet & ~PlayerBarSlots.ShuffleRepeat)),
        Hysteresis: Hold, Height: BarH)
    {
        Windows = ImmutableArray.Create(s_rules),
    };

    /// <summary>The bar's height in window tier <paramref name="window"/>: the Mini's 64 at Compact, the reserved 72 elsewhere.</summary>
    public static float HeightOf(WidthTier window) => WinUiTiers.Frame(window).BarH;

    // ── the bar across a crossing ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The bar's height across the 641 crossing (64 ↔ 72): 167 ms on the smooth-out curve, a reflow on the height axis anchored at the
    /// foot, so the page above yields frame by frame and nothing below moves. Null under reduced motion at the call site.</summary>
    public static readonly LayoutTransition SlotMotion = new(TransitionChannels.Size,
        TransitionDynamics.Tween(Design.Motion.Fast, Easing.SmoothOut),
        Size: SizeMode.Reflow, Anchor: SizeAnchor.Leading, Axes: SizeAxes.Height);

    // ── the panel ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The pane's width (340) and the docked pane's drag range (280-480).</summary>
    public const float PanelW = 340f, PanelMinW = 280f, PanelMaxW = 480f;

    /// <summary>The page's floor beside a docked pane (the board's 480) and the hold before an overlay docks again (24).</summary>
    public const float DockFloor = WinUiTiers.ContentFloorW, DockHold = 24f;

    private static readonly PanelTierRule s_docked = new PanelTierRule(PanelPlacement.Dock) { DockContentFloor = DockFloor, DockHold = DockHold };

    /// <summary>Huge's two panes: Queue + Now playing LEFT of the primary (Lyrics + Friends), each docked pane its own tab group; linked, so both
    /// open and close as one, and the primary shows Lyrics where it was asked for a body the left pane holds.</summary>
    public static PanelArrangement HugePanes { get; } = new(
        new DockedPane(PanelMode.Queue, PaneSide.Before) { Tabs = default(PanelModeSet).With(PanelMode.NowPlaying) },
        Linked: true, Fallback: PanelMode.Lyrics);

    /// <summary>The placement per window tier, Compact first.</summary>
    public static ImmutableArray<PanelTierRule> PanelRules { get; } = ImmutableArray.Create(
        new PanelTierRule(PanelPlacement.Sheet),
        s_docked,
        s_docked,
        s_docked with { MaxDocked = 2, Panes = HugePanes });

    /// <summary>The panel's policy: 340, resizable 280-480, the content row's height (the pane sits between the toolbar and the bar), the
    /// core's mini-player bounds, the rules above.</summary>
    public static PanelPolicy Panel { get; } = new(PanelMinW, PanelMaxW, FullHeight: false, PanelPolicy.Default.Pip) { Tiers = PanelRules };
}
