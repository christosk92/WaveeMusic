// ── Themes/Classic/Structure/Rules/Tiers.Podcast.cs — Classic's podcast pages v2 per window tier (the show, the episode, the audiobook) ─────────────
//
// The concept `winui-podcast-v2.html` (theme=classic): an album page. A cover column on the left that stays while the list scrolls (280 from 1008, 240
// below), stacked over the list at Compact (one scroller; a 120 cover, a 200 still). The episode page keeps the chapters and the transcript side by side
// only at Standard and up; below, the transcript is a tab. Rows expand in place where the column stands beside the list; at Compact a row opens its
// episode. Compact targets are 40 (owner rule), 32 elsewhere (Classic's control height).
//
// The WINDOW tier (`ctx.Tier().Window`): Compact < 528, Small 528-1007, Standard 1008-1919, Huge >= 1920 (Classic's ladder, `ClassicTiers`). A view reads
// `ClassicPodcastTiers.Of(ctx.Tier())` and never compares a width (WVTH013).

using Wavee.Theming;
using Wavee.Theming.Contracts;
using P = Wavee.Themes.Classic.ClassicPodcastRules;

namespace Wavee.Themes.Classic;

/// <summary>What Classic's podcast pages are in one window tier.</summary>
public sealed record ClassicPodcastLayout
{
    public required WidthTier Tier { get; init; }
    /// <summary>The cover column stands over the list (Compact): one scroller, the column's parts in a narrower form.</summary>
    public required bool Stacked { get; init; }
    /// <summary>The cover column's width (0 when stacked).</summary>
    public required float ColumnW { get; init; }
    /// <summary>The cover (the show's, the book's) and the episode still's width.</summary>
    public required float Cover { get; init; }
    public required float StillW { get; init; }
    /// <summary>A control's height: 32, Compact's 40.</summary>
    public required float Target { get; init; }
    /// <summary>The list's side padding (24; 16 at Compact).</summary>
    public required float MainPad { get; init; }
    /// <summary>A row shows its one line of description (not at Compact).</summary>
    public required bool RowDescription { get; init; }
    /// <summary>A row click expands it in place (beside the column) or opens its episode (Compact).</summary>
    public required bool RowExpands { get; init; }
    /// <summary>The episode page: the chapters card and the transcript side by side (Standard and up), else the transcript is a tab.</summary>
    public required bool TwoCards { get; init; }
    /// <summary>The episode page's cards' height.</summary>
    public required float CardsH { get; init; }
    /// <summary>The audiobook's About sits beside its facts (Standard and up), else under them.</summary>
    public required bool AboutBeside { get; init; }
    /// <summary>Following carries its check glyph (the 240 column has no room for it beside the bell, Share and ⋯; the word carries the state).</summary>
    public required bool FollowGlyph { get; init; }
    /// <summary>The filters are a 40 menu button (Compact) instead of the words rail.</summary>
    public required bool FilterMenu { get; init; }
}

/// <summary>Classic's podcast pages' tier records.</summary>
public static class ClassicPodcastTiers
{
    public static readonly ClassicPodcastLayout Standard = new()
    {
        Tier = WidthTier.Standard, Stacked = false, ColumnW = P.ColumnW, Cover = P.ColumnW - P.ColumnPadStart - P.ColumnPadEnd,
        StillW = P.ColumnW - P.ColumnPadStart - P.ColumnPadEnd, Target = P.Target, MainPad = P.MainPad, RowDescription = true, RowExpands = true,
        TwoCards = true, CardsH = P.CardsH, AboutBeside = true, FollowGlyph = true, FilterMenu = false,
    };

    public static readonly ClassicPodcastLayout Huge = Standard with { Tier = WidthTier.Huge };

    public static readonly ClassicPodcastLayout Small = Standard with
    {
        Tier = WidthTier.Small, ColumnW = P.ColumnSmallW, Cover = P.ColumnSmallW - P.ColumnPadStart - P.ColumnPadEnd,
        StillW = P.ColumnSmallW - P.ColumnPadStart - P.ColumnPadEnd, TwoCards = false, AboutBeside = false, FollowGlyph = false,
    };

    public static readonly ClassicPodcastLayout Compact = Small with
    {
        Tier = WidthTier.Compact, Stacked = true, ColumnW = 0f, Cover = P.CoverStacked, StillW = P.StillStacked, Target = P.TargetCompact,
        MainPad = P.MainPadCompact, RowDescription = false, RowExpands = false, CardsH = P.CardsHCompact, FollowGlyph = true, FilterMenu = true,
    };

    /// <summary>The window tier's record; where the CONTENT region is narrower than Standard (an inline panel beside a Standard window: the queue), the
    /// side-by-side forms of the list column (the chapters beside the transcript, About beside the facts) give way, so nothing is squeezed.</summary>
    public static ClassicPodcastLayout Of(in TierFacts facts)
    {
        if (facts.Policy is null) return Standard;
        var l = For(facts.Window);
        return l.TwoCards && facts.Content < WidthTier.Standard ? l with { TwoCards = false, AboutBeside = false } : l;
    }

    public static ClassicPodcastLayout For(WidthTier tier) => tier switch
    {
        WidthTier.Compact => Compact,
        WidthTier.Small => Small,
        WidthTier.Huge => Huge,
        _ => Standard,
    };
}
