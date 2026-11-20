// ── Themes/Common/Rules/RateNudgeRules.cs — the numbers of the rate & star card (rate-nudge-implementation.md §5.3) ─────────────────
//
// Data, not decisions: `RateNudgeCard` reads them (WVTH003 allows literals only here, in `Rules\**`). Type, colours, radii and the spacing
// rhythm come from the active style's tokens and roles; these are the card's own geometry and its motion.

using FluentGpu.Animation;
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
    public const float Width = 336f;

    /// <summary>The gap between the card's bottom and the player bar's top.</summary>
    public const float AboveBar = 14f;

    /// <summary>The app's icon at the card's scale, and the size it is decoded at (two device pixels per DIP covers a 200 % display).</summary>
    public const float Icon = 44f, IconDecodePx = 96f;

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
