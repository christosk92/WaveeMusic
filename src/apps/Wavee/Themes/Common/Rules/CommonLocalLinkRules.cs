// ── Themes/Common/Rules/CommonLocalLinkRules.cs — the numbers of "Your files and Spotify" in the WinUI picture (#240 phase 1; frames 28-30) ──
//
// Data, not decisions: `CommonLocalLink.cs` reads them (WVTH003 allows literals only in `Rules\**`). WinUI and Classic draw the offer and the
// review from WinUI's controls (InfoBar, BreadcrumbBar, SelectorBar, SettingsCard); these are only the gaps between them and the art sizes.
// The opt-in body's numbers serve every theme (it sits in each theme's own dialog).

using FluentGpu.Foundation;

namespace Wavee.Themes.Common;

/// <summary>Node keys and fixed geometry of the WinUI picture of "Your files and Spotify".</summary>
internal static class CommonLocalLinkRules
{
    public const string OfferKey = "ll-offer", OptInKey = "ll-optin", ReviewKey = "ll-review", CrumbKey = "ll-crumb", ChipsKey = "ll-chips";
    public const string CardKey = "ll-card:", AcceptKey = "ll-accept:", DetailsKey = "ll-details:", DrawerKey = "ll-drawer:", PickKey = "ll-pick:";
    public const string NotThisKey = "ll-notthis:", NotOnKey = "ll-noton:", SearchKey = "ll-search:", SearchBoxKey = "ll-searchbox:", UndoKey = "ll-undo:";
    public const string AllExactKey = "ll-accept-exact", ShowAllKey = "ll-show-all", WinsKey = "ll-wins:", EmptyKey = "ll-empty", ShowMeKey = "ll-offer-show", NotNowKey = "ll-offer-not-now";

    // ── the opt-in body (board 28 ②), every theme ──
    public const float OptInLineGap = 16f, OptInGlyphBox = 32f, OptInGlyph = 16f, OptInTextGap = 14f, OptInRuleTop = 20f, OptInBoxTop = 16f, OptInBodyGap = 20f;
    /// <summary>The sub line sits under the box's label (the CheckBox glyph and its gap).</summary>
    public const float OptInCheckInset = 28f;

    // ── the rename dialog's body (phase 2), every theme ──
    public const string RenameKey = "ll-rename";
    /// <summary>The air between the sentence, the three boxes and the link.</summary>
    public const float RenameGap = 12f;
    /// <summary>HyperlinkButton's side padding (WinUI HyperlinkButtonPadding 11,5): the reset link bleeds it so its text sits on the boxes' line.</summary>
    public const float RenameLinkPadX = 11f;

    // ── the review (board 29), as the discography page's head and Settings' card stack ──
    /// <summary>The trail's slot (WinUI's 28) and its gap to the title; the top of the page; the bar row and the InfoBar under the title.</summary>
    public const float TrailH = 28f, HeadGap = 8f, ReviewTop = 12f, ToolbarTop = 12f, ToolbarGap = 8f, NoteTop = 12f, CardsTop = 16f, ReviewBottom = 48f;
    /// <summary>Cards stack as Settings stacks its cards (4 apart); a card's items hang 1 DIP apart under its header (a SettingsExpander).</summary>
    public const float CardGap = 4f, ItemGap = 1f;
    /// <summary>A header's cover (the card's icon slot), a candidate's cover, their corner.</summary>
    public const float Cover = 48f, CandidateCover = 40f, CoverRadius = 4f;
    /// <summary>The header's right side: the gap between status, Accept and the chevron; the status glyph, its gap, its air before Accept.</summary>
    public const float ActionsGap = 12f, EvidenceGlyph = 12f, EvidenceGap = 8f, StatusGap = 8f, NoteGlyph = 18f;
    /// <summary>A field item's arrow and Spotify's cover swatch.</summary>
    public const float ArrowGlyph = 12f, Swatch = 20f, SwatchGap = 8f;
    /// <summary>The verbs of an item, the radio buttons' gap, the search box.</summary>
    public const float FootGap = 12f, RadioGap = 16f, SearchBoxW = 320f;
    /// <summary>A decided card's cover dims to this.</summary>
    public const float DecidedCoverOpacity = 0.8f;
    /// <summary>The candidates a card lists (board 29: three).</summary>
    public const int CandidatesShown = 3;
    /// <summary>A candidate item: the header card's icon column (16 + the 48 cover + its 20 gap) less the candidate's own 40 cover.</summary>
    public static readonly Edges4 CandidatePad = new(76f, 8f, 16f, 8f);
}
