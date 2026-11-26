// ── Themes/Common/Rules/RateNudgeRules.cs — the numbers of the rate & star card (rate-nudge-implementation.md §5.3) ─────────────────
//
// Data, not decisions: `RateNudgeCard` reads them (WVTH003 allows literals only here, in `Rules\**`). Type, colours, radii and the spacing
// rhythm come from the active style's tokens and roles; these are the card's own geometry and its motion.

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee.Themes.Common;

/// <summary>The constants of the rate &amp; star card.</summary>
internal static class RateNudgeRules
{
    /// <summary>The card's node key and its parts' (`find rate-nudge`, `click key=rate-nudge:later`).</summary>
    public const string Key = "rate-nudge", BigKey = "rate-nudge:big", TitleKey = "rate-nudge:title", BodyKey = "rate-nudge:body",
        MetaKey = "rate-nudge:meta", PrimaryKey = "rate-nudge:primary", LaterKey = "rate-nudge:later", NeverKey = "rate-nudge:never",
        IconKey = "rate-nudge:icon";

    /// <summary>The card's widest extent: a reading line of the body in three lines or so. A window narrower than this plus the side margins
    /// gets a card as wide as the window allows (never wider: it hugs, capped here).</summary>
    public const float Width = 368f;   // the milestone caption ("of music played in Wavee since July 30") stays one line beside the icon

    /// <summary>The gap between the card's bottom and the player bar's top.</summary>
    public const float AboveBar = 14f;

    /// <summary>The size the app's icon is decoded at. Its edge is not a number here: it spans the head's first two lines (the card reads
    /// them off the style's roles), about 60 DIP in WinUI; 160 px covers that on a 250 % display.</summary>
    public const float IconDecodePx = 160f;

    /// <summary>The gap between the head's lines, which the icon's edge includes.</summary>
    public const float LineGap = 2f;

    /// <summary>Segoe UI's glyph box over its em, (ascent 2210 + descent 514) / 2048: a line is never shorter than its size times this.</summary>
    public const float FaceBox = 1.33f;

    /// <summary>The star before the GitHub count, in GitHub's gold: the premium ink WinUI's and Classic's account menus give Spotify
    /// Premium's star, per app mode (read per render, so a mode switch re-resolves it).</summary>
    public static ColorF StarInk => Theme.Dark ? ColorF.FromRgba(0xE6, 0xC2, 0x6C) : ColorF.FromRgba(0x8A, 0x63, 0x12);

    /// <summary>Where the icon ships: `assets\**` is copied beside the exe (Wavee.csproj), the 1254-px source the .ico is cut from.</summary>
    public const string IconFolder = "assets/AppIcon", IconFile = "appicon-source.png";

    /// <summary>The divider between a milestone's head and its body.</summary>
    public const float Hairline = 1f;

    /// <summary>The star before the count line, at the meta line's scale.</summary>
    public const float MetaGlyph = 12f;

    /// <summary>The Store's bag on a rating card's primary button: Segoe Fluent Icons / MDL2 "Shop" (U+E719). No <c>Icons</c> member names it.</summary>
    public const string StoreGlyph = "";

    /// <summary>How far the card rises as it enters and sinks as it leaves.</summary>
    public const float RiseDip = 12f;

    /// <summary>The enter and the exit: one duration both ways (owner rule: motion is symmetric).</summary>
    public const float MotionMs = 220f;

    /// <summary>The terminal it enters from and leaves to: a 12-DIP rise with a fade.</summary>
    public static readonly EnterExit FadeUp = new(Dy: RiseDip, Opacity: 0f, Active: true);

    /// <summary>The same under reduced motion: the fade alone (the rise is motion, a fade orients).</summary>
    public static readonly EnterExit Fade = new(Opacity: 0f, Active: true);

    /// <summary>The enter's and the exit's timing: one eased curve both ways, so a card leaving mid-entrance retraces it; reduced motion keeps the fade.</summary>
    public static readonly MotionTokenDef Motion = MotionTokenDef.Eased(MotionMs, Easing.FluentStandard, ReducedMotionPolicy.KeepFade);
}
