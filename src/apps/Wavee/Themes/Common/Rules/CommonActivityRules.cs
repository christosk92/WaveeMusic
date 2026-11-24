// ── Themes/Common/Rules/CommonActivityRules.cs — the numbers of the library activity chip and its flyout (#240 UX S1, S2) ───────────────
//
// Data, not decisions: `CommonActivity.cs` reads them (WVTH003 allows literals only here, in `Rules\**`). Type, colours, radii and the spacing
// rhythm come from the active style's tokens and roles; these are the board's own geometry (boards240/Activity "Library activity: the chip and
// its flyout", WinUI dark 1440x960; the chip again in FirstScan, Changes and Missing).

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee.Themes.Common;

/// <summary>The constants of the library activity chip and its flyout.</summary>
internal static class CommonActivityRules
{
    // ── keys (`find prefix=activity`, `click key=nav-activity`) ──

    /// <summary>The chip's lane under "Local files" in the nav pane, and the chip itself (the click target).</summary>
    public const string LaneKey = "nav-activity-lane", ChipKey = "nav-activity";

    /// <summary>The flyout's root and its parts.</summary>
    public const string FlyoutKey = "activity-flyout", HeadKey = "activity-flyout:head", PhasesKey = "activity-flyout:phases",
        PhaseKey = "activity-flyout:phase:", ChangesKey = "activity-flyout:changes", ChangeKey = "activity-flyout:change:",
        AttentionKey = "activity-flyout:attention", AttentionItemKey = "activity-flyout:attention:", VerbKey = "activity-flyout:verb:",
        FooterKey = "activity-flyout:footer", RescanKey = "activity-flyout:rescan", SettingsKey = "activity-flyout:settings",
        PauseKey = "activity-flyout:pause";

    // ── the chip (board: 180 x 20 at the label's x, its top 5 above the row's foot; the cell grows 36 → 56) ──

    /// <summary>The chip's reserved width: every state's words fit it, so a state change never resizes it.</summary>
    public const float ChipWidth = 180f;

    /// <summary>The chip's height and its pill's corner (half the height).</summary>
    public const float ChipHeight = 20f;

    /// <summary>The lane the chip adds under the row, reserved in every state and at every tier (the board's 56-DIP cell less the 40 pitch).</summary>
    public const float Lane = 16f;

    /// <summary>The lane in the overlay drawer (Narrow and Tiny): the Compact target, so the chip takes it whole as its hit rect; and the slop
    /// above and below the 20-DIP chip that makes it so.</summary>
    public const float DrawerLane = 40f, DrawerSlop = 10f;

    /// <summary>How far the chip rises into the row above (the row's 2-DIP margin plus the board's 5 of overlap).</summary>
    public const float Lift = 7f;

    /// <summary>The chip's lead and end padding, and the gap between its mark and its words.</summary>
    public const float ChipPadLead = 6f, ChipPadEnd = 8f, ChipGap = 6f;

    /// <summary>The marks: the ring and the tick in the chip, the amber dot.</summary>
    public const float ChipRing = 12f, ChipTick = 12f, Dot = 8f;

    /// <summary>The chip's words fade in and out (the lane never moves).</summary>
    public static readonly EnterExit Fade = new(Opacity: 0f, Active: true);

    public static MotionTokenDef FadeMotion => MotionTok.ControlFaster;

    // ── the flyout (board: 420 wide, the head 14/10, the phase rows 32, the sections 12/14, the attention rows 48) ──

    /// <summary>The flyout's width; a window narrower than this plus <see cref="WindowEdge"/> twice gets one as wide as it allows.</summary>
    public const float Width = 420f;

    /// <summary>The air the flyout keeps from the window's edges when the window is narrow.</summary>
    public const float WindowEdge = 16f;

    /// <summary>The flyout's side padding.</summary>
    public const float Pad = 20f;

    /// <summary>The head's padding (top, bottom; its end is the side padding less 4, as the board).</summary>
    public const float HeadTop = 14f, HeadBottom = 10f, HeadEnd = 16f;

    /// <summary>A phase row's height, its mark's cell and the gap after the mark.</summary>
    public const float PhaseRow = 32f, Mark = 16f, MarkGap = 12f;

    /// <summary>The pending phase's hollow ring: its edge and its stroke.</summary>
    public const float Pending = 11f, PendingStroke = 1.4f;

    /// <summary>The running phase's padding (top, bottom), the line its mark centres on, and the gaps above its bar and its rate line.</summary>
    public const float ActiveTop = 6f, ActiveBottom = 8f, ActiveLine = 20f, BarGap = 6f;

    /// <summary>The phase list's foot.</summary>
    public const float PhasesBottom = 10f;

    /// <summary>A section's padding (top, bottom), the gap under its head, and the links' gap.</summary>
    public const float SectionTop = 12f, SectionBottom = 14f, AttentionBottom = 8f, SectionGap = 8f, AttentionGap = 6f, LinkGap = 8f;

    /// <summary>An attention row's least height and its mark.</summary>
    public const float AttentionRow = 48f, AttentionMark = 16f;

    /// <summary>The footer's padding (top, bottom).</summary>
    public const float FooterTop = 12f, FooterBottom = 14f;

    /// <summary>The hairline between the sections.</summary>
    public const float Hairline = 1f;

    /// <summary>The weight of a section's head (the board's 600).</summary>
    public const ushort HeadWeight = 600;

    /// <summary>How often the open flyout re-reads "2 min ago".</summary>
    public const float AgoTickMs = 30_000f;
}
