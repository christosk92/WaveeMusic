// ── Themes/WinUi/Structure/Rules/WinUiDialogRules.cs — the numbers of WinUI's dialog bodies (themes plan 6.1, T11a) ───────────
//
// What `Shell.Overlays.UI.cs` (the log-out card, the play-a-link card, the signature sheet), `Actions.UI.cs` (the destination picker) and
// `Track.Drawer.cs` (the credits sheet) held as file-level constants and inline literals, moved verbatim with their pictures. They are data,
// not decisions: the views under `Views\Dialogs` read them (WVTH003 allows literals only here, in `Rules\**`).
//
// THE WIDTH LADDER stays the core's (`Controls.DialogWidthCompact` / `Wide` / `Max`: 320, 480, 548 are the engine ContentDialog's own rungs and
// each opener passes its rung); the two bare cards below are off-ladder on purpose.

using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee.Themes.WinUi;

/// <summary>The constants of the dialog bodies.</summary>
internal static class WinUiDialogRules
{
    // ── the card's width ladder (W-Tiers: "ContentDialogs go full-screen under 641 and cap at 588 / 760 elsewhere") ───────────

    /// <summary>The widest a dialog card is above Compact (W-Dialog: 588), the report form's own card (W-Feedback: 760), Huge's two-pane form card
    /// (W-Feedback-Huge's 760 form with the live report beside it, W8b's body: 1040), and the widest a full-screen Compact card can be (the window below 641).</summary>
    public const float CardCap = 588f, ReportWidth = 760f, HugeFormWidth = 1040f, FullScreenCap = 640f;

    /// <summary>The window margin a card keeps on each side (above Compact), the margin above and below, and the card's corner.</summary>
    public const float WindowInset = 16f, EdgeY = 24f, CardRadius = 8f;

    // ── the two bare modal cards (no title band, no separator, no command-row fill) ─────────────────────────────────

    /// <summary>The card's padding.</summary>
    public const float CardPadding = 24f;

    /// <summary>The card's title: 20 over 28, semi-bold.</summary>
    public const float CardTitleSize = 20f, CardTitleLine = 28f;

    /// <summary>The card's body: 14 over 20.</summary>
    public const float CardBodySize = 14f, CardBodyLine = 20f;

    /// <summary>A card button's minimum width (Cancel, Log out, Start).</summary>
    public const float CardButtonMinWidth = 96f;

    // ── the log-out card: 380 wide (320..420) ───────────────────────────────────────────────────────────────────────

    /// <summary>The log-out card's width and its cap (never wider, never wider than the window: <c>WinUiOverlayTiers.CardWidth</c>).</summary>
    public const float LogoutWidth = 380f, LogoutMaxWidth = 420f;

    // ── the play-a-link card ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The card's width and its cap.</summary>
    public const float PlayLinkWidth = 420f, PlayLinkMaxWidth = 480f;

    /// <summary>The status line's reserved height (even when empty, so the button row never jumps when a status appears) and its text.</summary>
    public const float PlayLinkStatusHeight = 18f, PlayLinkStatusSize = 12f, PlayLinkStatusLine = 16f;

    // ── the signature sheet ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The summary line: 14 over 20, semi-bold.</summary>
    public const float SignatureSummarySize = 14f, SignatureSummaryLine = 20f;

    /// <summary>A row's gap between its label and its value, and the value's size and line.</summary>
    public const float SignatureRowGap = 2f, SignatureValueSize = 12f, SignatureValueLine = 16f;

    // ── the destination picker ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The picker's panel width (the filter field is the panel less its padding), the field's height, and the list's cap.</summary>
    public const float PickerPanelWidth = 320f, PickerFieldHeight = 32f, PickerListMaxHeight = 360f;

    /// <summary>What the panel holds above the list (the guidance over two lines, the field and their gaps), so the list is bounded by the card's body room less it.</summary>
    public const float PickerHeadHeight = 96f;

    /// <summary>The tree indent per level: the sidebar tree's own step.</summary>
    public const float PickerIndentStep = 12f;

    /// <summary>The rows' gap in the list.</summary>
    public const float PickerListGap = 2f;

    /// <summary>The filtered-empty line's height and padding.</summary>
    public const float PickerEmptyHeight = 44f;

    /// <summary>The filtered-empty line's padding.</summary>
    public static readonly Edges4 PickerEmptyPadding = new(8f, 0f, 8f, 0f);

    /// <summary>A row: the height with and without art, the gap, the left padding before the indent, the right padding, the corner.</summary>
    public const float PickerRowArtHeight = 44f, PickerRowHeight = 40f, PickerRowGap = 10f, PickerRowLeft = 6f, PickerRowRight = 8f, PickerRowRadius = 4f;

    /// <summary>The row's lead: a plated glyph (the "New playlist" row) or art, 40 with a 6 corner; the plated glyph and the bare glyph sizes.</summary>
    public const float PickerLead = 40f, PickerLeadRadius = 6f, PickerPlatedIcon = 20f, PickerBareIcon = 18f;

    /// <summary>The art's decode size.</summary>
    public const int PickerLeadDecodePx = 80;

    /// <summary>The label and the subtitle: 14 over 20 and 12 over 16, with 1 DIP between them.</summary>
    public const float PickerLabelSize = 14f, PickerLabelLine = 20f, PickerSubSize = 12f, PickerSubLine = 16f, PickerTextGap = 1f;

    // ── the credits sheet ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The body's cap.</summary>
    public const float CreditsMaxWidth = 440f;

    /// <summary>The signature sheet's scrolling body cap (its nine rows and the summary fit under it in a tall window; a short one scrolls inside the card).</summary>
    public const float SignatureMaxHeight = 560f;

    /// <summary>The credits list's cap and the loading bars' gap.</summary>
    public const float CreditsListMaxHeight = 420f, CreditsLoadingGap = 10f;

    /// <summary>The loading bars: heights and widths (a short heading bar, then three lines).</summary>
    public const float CreditsBar1H = 12f, CreditsBar1W = 120f, CreditsBar2H = 14f, CreditsBar2W = 220f, CreditsBar3W = 190f, CreditsBar4W = 210f;

    /// <summary>The name's weight.</summary>
    public const int CreditsNameWeight = 650;
}
