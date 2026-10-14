// ── Themes/Classic/Structure/Rules/ClassicSetupRules.cs — the numbers of Classic's setup views (themes plan 6.1, T11c) ──────────
//
// What `Screens\Setup.UI.cs` and `Setup.UI.Runtime.cs` held beside the pictures they size: the footer's buttons, the sign-in door's and the runtime card's command rows, the QR plate's
// paper and ink, the Busy ladder's rungs, the status glyph, the fact boxes, the setting rows and the Optional pill. The wizard plate's own geometry is `SetupGeometry`. They are data, not
// decisions (WVTH003 allows literals only in `Rules\**`).

using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee.Themes.Classic;

/// <summary>The constants of Classic's setup views.</summary>
public static class ClassicSetupRules
{
    // ── the wizard's footer, the Optional pill and the body's enter / exit ────────────────────────────────────────────────

    /// <summary>The footer's button lane height and its step label's size.</summary>
    public const float FooterButtonHeight = 32f, FooterStepLabelSize = 14f;

    /// <summary>The page body's enter and exit offsets (a page swap's content slides in from below, out upward).</summary>
    public const float BodyEnterDy = 6f, BodyExitDy = -4f;

    /// <summary>The Optional pill's height and padding.</summary>
    public const float PillMinHeight = 22f;
    public static readonly Edges4 PillPadding = new(9f, 2f, 9f, 2f);

    // ── the sign-in surfaces ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The Busy rows' minimum height (the ring beside its sentence).</summary>
    public const float BusyRowMinHeight = 48f;

    /// <summary>The pairing countdown's tick and the working ring's size.</summary>
    public const float PairingTickMs = 1000f, PairingRingSize = 20f;

    /// <summary>The 12 / 16 caption the countdown and the ladder's rows use.</summary>
    public const float CaptionSize = 12f, CaptionLine = 16f;

    /// <summary>The QR plate: pure black on white whatever the theme (contrast is what a phone camera binarizes on) and the fallback note's green and size.</summary>
    public static readonly ColorF QrInk = ColorF.FromRgba(0x00, 0x00, 0x00);
    public static readonly ColorF QrPaper = ColorF.FromRgba(0xFF, 0xFF, 0xFF);
    public static readonly ColorF QrFallbackGlyph = ColorF.FromRgba(0x1D, 0xB9, 0x54);
    public const float QrFallbackGlyphSize = 28f;

    /// <summary>The command rows' buttons (the door's and the runtime card's): minimum width and height.</summary>
    public const float DialogButtonMinWidth = 96f, DialogButtonHeight = 32f;

    /// <summary>The hyperlink's own 11-DIP inner padding: −11 puts the first link's TEXT flush with the copy.</summary>
    public static readonly Edges4 FlushLinkMargin = new(-11f, 0f, 0f, 0f);

    /// <summary>"Is this you?": the avatar's size and the gap between the name and the tier.</summary>
    public const float AccountPicture = 40f, AccountTextGap = 2f;

    /// <summary>The Busy ladder: the ring, the done / failed glyph, the pending bullet, the row, the mark's box and the bar.</summary>
    public const float StepRingSize = 16f, StepGlyphSize = 15f, StepBulletSize = 11f;
    public const float StepRowHeight = 26f, StepRowWidth = 300f, StepMarkBox = 18f, StepBarWidth = 220f;

    // ── the runtime card ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The inline status block: the severity glyph, its top nudge and the gap between the heading and the copy.</summary>
    public const float StatusGlyphSize = 18f, StatusTextGap = 4f;
    public static readonly Edges4 StatusGlyphMargin = new(0f, 1f, 0f, 0f);

    /// <summary>The rule above the local-source rows.</summary>
    public static readonly Edges4 CardRuleMargin = new(0f, 4f, 0f, 4f);

    /// <summary>The Ready fact box's padding and gap, and the [Signature] button.</summary>
    public const float ReadyFactPad = 12f, ReadyFactGap = 6f;
    public const float SignatureButtonMinWidth = 86f, SignatureButtonHeight = 28f;

    /// <summary>A local-source setting row: the gap, the padding, the glyph and the chevron.</summary>
    public const float SettingRowGap = 13f, SettingRowGlyphSize = 16f, SettingRowChevronSize = 12f;
    public static readonly Edges4 SettingRowPadding = new(12f, 9f, 12f, 9f);
}
