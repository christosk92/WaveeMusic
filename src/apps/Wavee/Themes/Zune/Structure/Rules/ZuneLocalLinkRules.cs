// ── Themes/Zune/Structure/Rules/ZuneLocalLinkRules.cs — the numbers of Zune's "Your files and Spotify" (#240 phase 1; board frames 28-30) ────
//
// Data, not decisions: `ZuneLocalLink.cs` reads them (WVTH003). Zune's voice: no plates, no pills, no borders; large lowercase words, accent text
// verbs on 40-high targets, the art large, air instead of lines. The filter rail is the discography page's sub-pivot (its weights and accent line).

namespace Wavee.Themes.Zune;

/// <summary>Node keys and fixed geometry of Zune's offer and review.</summary>
internal static class ZuneLocalLinkRules
{
    public const string OfferKey = "zll-offer", ReviewKey = "zll-review", RailKey = "zll-rail", GroupKey = "zll-group:", DrawerKey = "zll-drawer:";
    public const string AcceptKey = "zll-accept:", DetailsKey = "zll-details:", NotThisKey = "zll-notthis:", NotOnKey = "zll-noton:", SearchKey = "zll-search:";
    public const string SearchBoxKey = "zll-searchbox:", PickKey = "zll-pick:", UndoKey = "zll-undo:", WinsKey = "zll-wins:", AllExactKey = "zll-accept-exact";
    public const string ShowMeKey = "zll-offer-show", NotNowKey = "zll-offer-not-now", ShowAllKey = "zll-show-all", BackKey = "zll-back", EmptyKey = "zll-empty";

    /// <summary>The offer band: its padding (the column's lead at the sides), the gap between its words and its verbs.</summary>
    public const float BandPadY = 18f, BandGap = 24f, BandTop = 4f;

    /// <summary>The head: the trail line, the title under it, the line under the title.</summary>
    public const float HeadTop = 8f, TitleTop = 2f, SubTop = 6f, NoteTop = 8f;
    /// <summary>The filter rail (the discography sub-pivot): word gap, the count's gap after its word, the accent line.</summary>
    public const float RailTop = 20f, RailGap = 24f, CountGap = 6f, RailLine = 2f;
    public const ushort RailRest = 400, RailSelected = 600;

    /// <summary>A text verb: its height (the Compact target everywhere), the gap between verbs.</summary>
    public const float VerbH = 40f, VerbGap = 24f;

    /// <summary>A group: the air between groups, the art pair (64 each, the arrow between), the gap to the words.</summary>
    public const float GroupsTop = 28f, GroupGap = 36f, Art = 64f, ArrowW = 28f, ArrowGlyph = 14f, ArtGap = 20f, LineGap = 2f;
    /// <summary>The evidence dot and its gap.</summary>
    public const float Dot = 8f, DotGap = 10f;
    /// <summary>Details: the inset under the head, a field row, the field's column, the arrow column; candidates: their art and row.</summary>
    public const float DrawerTop = 12f, FieldRow = 32f, FieldLabel = 160f, FieldArrow = 28f, Swatch = 24f, CandidateArt = 48f, CandidateGap = 16f;
    public const float SearchW = 360f, CandidateBottom = 8f;
    /// <summary>A decided group's art dims to this.</summary>
    public const float DecidedOpacity = 0.7f;
    public const float ReviewBottom = 48f;
    public const int CandidatesShown = 3;
}
