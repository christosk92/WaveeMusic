// ── Themes/WinUi/Structure/Rules/WinUiPlayerLayout.cs — the look numbers of WinUI's player family (P6 W4a) ──────────────────────────
//
// The player family's views (`Views\Player\*`, `Views\Panel\*`, `Views\Queue\*`) read their numbers here (WVTH003 allows literals only in
// `Rules\**`). What a level of the bar carries and where it starts is `Tiers.Player.cs`; this file is the look of the parts:
//
//   WinUiPlayerLayout     the bar (Shell-Player): the row's inset, the identity block, the 40 x 32 transport buttons and the 36 accent play
//                         circle, the 40 x 40 tools and their latched plate, the labelled device button, the video split, the Mini bar's
//                         door; the motion (a level crossing FLIPs the structural nodes, a control enters or leaves with a fade, a face lights
//                         by opacity); the ⋯ menu's order
//   WinUiTransportLayout  the seek rail (the 4-px rail, the 18 thumb with its 12 dot, a 24 hit box), the chapter ticks and tip, the scrub
//                         readout and the 40 time slots
//
// Literal numbers are allowed in this file (WVTH003 exempts `Rules\**`).

using FluentGpu.Animation;
using FluentGpu.Foundation;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

/// <summary>The constants of WinUI's play bar (Shell-Player).</summary>
public static class WinUiPlayerLayout
{
    // ── node keys (the mounted tests and `find` read them; no key names a level) ─────────────────────────────────────

    /// <summary>The bar inside its slot, its row, the identity, the centre (transport over seek), the transport row, the seek row, the play
    /// circle, the tools, the Mini bar's door and its progress edge.</summary>
    public const string BarKey = "winui-bar", RowKey = "winui-bar-row", LeftKey = "winui-bar-left", CentreKey = "winui-bar-centre",
        TransportKey = "winui-bar-transport", SeekRowKey = "winui-bar-seek-row", PlayKey = "winui-bar-play", RightKey = "winui-bar-right",
        DoorKey = "winui-bar-door", EdgeKey = "winui-bar-edge";

    /// <summary>The tools' keys (a stable key per control: a level change fades it in or out in place).</summary>
    public const string LyricsKey = "winui-bar-lyrics", QueueKey = "winui-bar-queue", DevicesKey = "winui-bar-devices", VolumeKey = "winui-bar-volume",
        VolumeSliderKey = "winui-bar-volume-slider", VideoKey = "winui-bar-video", ExpandKey = "winui-bar-expand", MoreKey = "winui-bar-more",
        LikeKey = "winui-bar-like", ShuffleKey = "winui-bar-shuffle", RepeatKey = "winui-bar-repeat", PrevKey = "winui-bar-prev",
        NextKey = "winui-bar-next", SpeedKey = "winui-bar-speed";

    // ── the bar ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The row's inset (the board's 0 16) and the Mini bar's (0 8 0 12).</summary>
    public static readonly Edges4 RowPadding = new(16f, 0f, 16f, 0f), MiniPadding = new(12f, 0f, 8f, 0f);

    /// <summary>The bar's top divider (1 px, the divider stroke).</summary>
    public const float DividerH = 1f;

    /// <summary>The identity block: 12 between the cover, the text and the heart; the cover's corner (4).</summary>
    public const float IdGap = 12f, ArtCorner = 4f;

    /// <summary>The title (Body Strong 14/20) and the artists (Caption 12/16, the secondary ink); the "playing on" line (12/16).</summary>
    public const float TitleSize = 14f, TitleLine = 20f, SubSize = 12f, SubLine = 16f;

    /// <summary>The title's weight.</summary>
    public const ushort TitleWeight = 600;

    /// <summary>The "playing on" line's device glyph and its gap.</summary>
    public const float RemoteGlyph = 12f, RemoteGap = 4f;

    /// <summary>The video badge on the cover (16, the film glyph 10, 2 in from the corner, black at 60 %).</summary>
    public const float FilmBadge = 16f, FilmGlyph = 10f, FilmInset = 2f, FilmCorner = 3f;

    public static readonly ColorF FilmFill = ColorF.FromRgba(0, 0, 0, 0x99), FilmInk = ColorF.FromRgba(255, 255, 255, 255);

    /// <summary>The centre: the transport row (36, buttons 8 apart) over the seek row (24, 8 between the times and the rail).</summary>
    public const float TransportRowH = 36f, TransportGap = 8f, SeekRowH = 24f, SeekGap = 8f;

    /// <summary>A transport button (40 x 32), a tool (40 x 40), a glyph (16), the ±10 s glyph (18), the button's corner (4).</summary>
    public const float TransportW = 40f, TransportH = 32f, ToolBox = 40f, Glyph = 16f, StepGlyph = 18f, ButtonCorner = 4f;

    /// <summary>The play circle (36, the accent, a 16 glyph in the accent's contrast ink).</summary>
    public const float Play = 36f, PlayGlyph = 16f;

    /// <summary>Segoe Fluent Icons' solid play and pause (the circle is filled, so its glyph is a filled shape too).</summary>
    public const string PlaySolid = "", PauseSolid = "";

    /// <summary>The play triangle's optical nudge (a twelfth of the glyph).</summary>
    public const float PlayNudgeDivisor = 12f;

    /// <summary>The ±10 s steps skip this far; their curved arrows (back, forward) with the digits under the arc (7, 6 below the centre).</summary>
    public const int SeekStepMs = 10_000;

    public const string StepBackGlyph = "", StepForwardGlyph = "", StepDigits = "10";

    public const float StepDigitsSize = 7f;

    public static readonly Edges4 StepDigitsPadding = new(0f, 6f, 0f, 0f);

    /// <summary>The tools' glyphs as the board draws them (Segoe Fluent Icons: quote U+E9B2, music queue U+E90B, devices U+E975, full screen
    /// brackets U+E9A6).</summary>
    public static readonly string LyricsGlyph = ((char)0xE9B2).ToString(), QueueGlyph = ((char)0xE90B).ToString(),
        DevicesGlyph = ((char)0xE975).ToString(), ExpandGlyph = ((char)0xE9A6).ToString();

    /// <summary>The tools' gap (the board's 4).</summary>
    public const float ToolGap = 4f;

    /// <summary>The labelled device button (Huge): 32 tall, 10 in, 8 between the glyph and the name (Body 14).</summary>
    public const float DeviceH = 32f, DevicePadX = 10f, DeviceGap = 8f, DeviceText = 14f;

    /// <summary>The episode speed button (40 x 32, Caption Strong 12).</summary>
    public const float SpeedW = 40f, SpeedH = 32f, SpeedText = 12f;

    public const ushort SpeedWeight = 600;

    /// <summary>The speed presets the speed menu offers.</summary>
    public static readonly float[] SpeedPresets = [0.75f, 1f, 1.25f, 1.5f, 1.75f, 2f, 2.5f, 3f];

    /// <summary>The video split (32 tall, the subtle fill in the control stroke; the switch 36 x 30, a 1-px divider 6 in, the chevron 24 x 30
    /// with a 12 glyph).</summary>
    public const float SplitH = 32f, SplitMainW = 36f, SplitInnerH = 30f, SplitDividerInset = 6f, SplitChevronW = 24f, SplitChevronGlyph = 12f;

    /// <summary>The inline volume slider's hit box (the slider's own ring height) and 4 of air before it.</summary>
    public const float VolumeThickness = 24f, VolumeLead = 4f;

    /// <summary>The volume flyout's slider (Compact and Medium: "volume stays a button with a flyout slider").</summary>
    public const float VolumeFlyoutW = 160f;

    public static readonly Edges4 VolumeFlyoutPadding = new(16f, 8f, 16f, 8f);

    /// <summary>The Mini bar: 4 between its parts, the door 56 tall with 12 inside and 8 before the heart.</summary>
    public const float MiniGap = 4f, DoorH = 56f, DoorPadRight = 8f;

    // ── the ⋯ menu ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The ⋯ rows' order (W-Shell-Compact 4: the transport, the panes and devices, the volume, full screen), the core's rows sorted by
    /// it; a separator falls between groups.</summary>
    public static int MenuRank(OverflowCommand command) => command switch
    {
        OverflowCommand.Previous => 0,
        OverflowCommand.Next => 1,
        OverflowCommand.Shuffle => 2,
        OverflowCommand.Repeat => 3,
        OverflowCommand.Lyrics => 10,
        OverflowCommand.Queue => 11,
        OverflowCommand.NowPlaying => 12,
        OverflowCommand.Video => 20,
        OverflowCommand.Mute => 21,
        _ => 30,   // FullScreen
    };

    /// <summary>The rank of the Devices row the Mini bar adds (in the panes' group, after them).</summary>
    public const int DevicesRank = 13;

    /// <summary>The group a rank belongs to (a separator between groups).</summary>
    public static int MenuGroup(int rank) => rank / 10;

    /// <summary>The Full screen row's accelerator hint.</summary>
    public const string FullScreenAccelerator = "F11";

    // ── motion ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A level crossing: every structural node FLIPs to its new rect over 167 ms (W-Tiers: "the change between them animates over
    /// 167 ms").</summary>
    public static readonly LayoutTransition MoveMotion = new(
        TransitionChannels.Bounds, TransitionDynamics.Tween(Design.Motion.Fast, Easing.FluentPopOpen), SizeMode.Reveal);

    /// <summary>A control entering or leaving on a crossing ("player controls fade out and in during a tier change"): fade + size, the
    /// neighbours slide over.</summary>
    public static readonly LayoutTransition ItemMotion = new(
        TransitionChannels.Bounds | TransitionChannels.Opacity,
        TransitionDynamics.Tween(Design.Motion.Fast, Easing.SmoothOut),
        SizeMode.Reflow,
        Enter: new EnterExit(Opacity: 0f, Active: true),
        Exit: new EnterExit(Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(Design.Motion.Fast, Easing.SmoothOut));

    /// <summary>A reserved control lights or dims: opacity only, so nothing around it moves.</summary>
    public static readonly LayoutTransition FaceMotion = new(
        TransitionChannels.Opacity, TransitionDynamics.Tween(Design.Motion.Standard, Easing.SmoothOut), SizeMode.Auto);

    /// <summary>The identity's text cross-fades when the face swaps to another playable.</summary>
    public static readonly MotionTokenDef FaceSwap =
        MotionTokenDef.Eased(Design.Motion.Standard, Easing.FluentDecelerate, ReducedMotionPolicy.KeepFade);
}

/// <summary>The constants of WinUI's seek rail, chapter ticks, readout and time labels (the bar's, the sheet's, the stage's).</summary>
public static class WinUiTransportLayout
{
    // ── the seek rail (Shell-Player) ──

    /// <summary>The rail's hit box (the seek row's 24).</summary>
    public const float SeekHitHeight = 24f;

    /// <summary>The rail (4, r2), the thumb (18) and its accent dot (12; 14 on hover, 10 pressed: WinUI's Slider steps).</summary>
    public const float SeekTrack = 4f, SeekRingDiameter = 18f, SeekDot = 12f, SeekDotHover = 14f / 12f, SeekDotPress = 10f / 12f;

    /// <summary>The Mini bar's progress edge (2 px).</summary>
    public const float MiniProgressH = 2f;

    /// <summary>The DVR rail's live-edge mark.</summary>
    public const float LiveEdgeWidth = 2f;

    /// <summary>The live line (nothing to rewind): its stroke and its corner.</summary>
    public const float LiveLineThickness = 2f, LiveLineCorner = 1f;

    /// <summary>The live line's breath (0.55 ↔ 1 over 3 s) and the finite flat track that replaces the loop in place.</summary>
    public static readonly Keyframe[] LiveLineBreathe = [new(0f, 0.55f), new(0.5f, 1f), new(1f, 0.55f)];

    /// <summary>The flat track.</summary>
    public static readonly Keyframe[] LiveLineFlat = [new(0f, 1f), new(1f, 1f)];

    /// <summary>One breath; the flat track's length.</summary>
    public const float LiveLineBreatheMs = 3000f, LiveLineFlatMs = 1f;

    // ── the chapter ticks and their tip ──

    /// <summary>A chapter tick (and the tip's invisible anchor).</summary>
    public const float ChapterTickHeight = 8f;

    /// <summary>The tip's column.</summary>
    public const float ChapterTipGap = 2f, ChapterTipWidth = 260f, ChapterTipMaxWidth = 320f;

    /// <summary>The tip's padding (the ToolTip's 9 6 9 8).</summary>
    public static readonly Edges4 ChapterTipPadding = new(9f, 6f, 9f, 8f);

    /// <summary>The tip's lines: the time and the range (12), the title (14).</summary>
    public const float ChapterTipTimeSize = 12f, ChapterTipTitleSize = 14f;

    /// <summary>The tip's emphasis weight.</summary>
    public const int ChapterTipWeight = 600;

    /// <summary>The title's line cap.</summary>
    public const int ChapterTipTitleLines = 2;

    // ── the scrub readout (the Slider's thumb tip) ──

    public static readonly Edges4 ReadoutPadding = new(8f, 5f, 8f, 7f);

    public const float ReadoutSize = 14f;

    // ── the time labels ──

    /// <summary>The time slot (Shell-Player: 40, the digits centred).</summary>
    public const float TimeSlotWidth = 40f;

    /// <summary>The remaining slot's height at Compact (a toggle: the touch floor).</summary>
    public const float TouchSlotH = 40f;

    /// <summary>The remaining slot's height elsewhere (a toggle: the 32 hit floor; it overflows the 24 seek row evenly).</summary>
    public const float ToggleSlotH = 32f;

    /// <summary>The live slot, the GO LIVE text button, the live type, the red dot and the LIVE badge.</summary>
    public const float LiveSlotWidth = 104f, GoLiveMinWidth = 44f, GoLiveHeight = 20f, LiveTextSize = 9f, LiveTextLineHeight = 12f,
        LiveDotSize = 6f, LiveDotCorner = 3f, LiveBadgeHeight = 14f, LiveBadgeCorner = 2f;

    public const int LiveTextWeight = 600;
}

/// <summary>The strip over a video (the on-media transport; no WinUI board draws it: Classic's numbers).</summary>
internal static class WinUiOnMediaLayout
{
    /// <summary>A transport glyph button: its box, glyph and the hover and press plates.</summary>
    public const float GlyphBox = 36f, GlyphSize = 16f, GlyphHoverAlpha = 0.14f, GlyphPressedAlpha = 0.22f;

    /// <summary>The play disc: its box, glyph and its three plates.</summary>
    public const float PlayBox = 44f, PlayGlyph = 18f, PlayAlpha = 0.18f, PlayHoverAlpha = 0.28f, PlayPressedAlpha = 0.36f;

    /// <summary>The volume popup: a slider card over the video hole.</summary>
    public const float VolumePopupW = 52f, VolumePopupH = 168f, VolumeSliderLength = 124f, VolumeSliderThickness = 32f;

    public static readonly Edges4 VolumePopupPadding = new(10f, 14f, 10f, 14f);
}
