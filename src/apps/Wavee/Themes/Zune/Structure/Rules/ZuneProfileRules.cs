// ── Themes/Zune/Structure/Rules/ZuneProfileRules.cs — the numbers of Zune's profile page and its people lists (themes plan 6.3; P5 Z7) ──
//
// Boards Z-Profile (Standard), Z-Profile-Compact, -Small and -Huge (ZT7 refines the three). The band's row 2 carries the page's head (the core host
// publishes it: the person's name as the title, the sections as the views, "open account" and "share" as the actions), so the page is the body:
//
//   HERO     the round avatar beside the copy (an eyebrow, the name, the stats as one line of words: "12 followers · 48 following · 6 public
//            playlists", the two people words links into their list), the actions under the copy (Follow for another person, the "…" menu).
//   SECTIONS each a heading row (26/32 light, "only visible to you" 14 after the top artists) over its body, the sections 28 apart (40 at Huge):
//            the top artists as ranked rows (the 40 light numeral, the round art, the name), the public playlists, the recently played artists and the
//            two people sections as shelves of 154 cards 16 apart.
//   LISTS    (Pages.ProfileList, UNDESIGNED: owner Q18, built from these parts) the heading row (the facet word, the person's name as a link back),
//            the tools (the word run of chips, the find box) and the lettered grid of cards the core plans (`ProfileGridFit`).
//
// Per CONTENT tier (Compact 300-639 · Small 640-1007 · Standard 1008-1919 · Huge 1920-3199 · Huge 4K = Huge): `ZuneProfileLayout`. WVTH003 allows
// literals only here, in `Rules\**`; WVTH013 keeps every width decision in the record.

using System.Collections.Immutable;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

/// <summary>The voice of the hero's name (the style's roles: the page-title voice, the detail hero, the panorama).</summary>
public enum ZuneProfileName : byte
{
    /// <summary>The 40/48 light page title (Compact, Small).</summary>
    Title,

    /// <summary>The 64/72 light hero (Standard).</summary>
    Hero,

    /// <summary>The 120/130 panorama word (Huge).</summary>
    Panorama,
}

/// <summary>The numbers and node keys of Zune's profile pages.</summary>
public static class ZuneProfileRules
{
    // ── the node keys the views carry (the tests find them) ──────────────────────────────────────────────────────────────

    /// <summary>The profile page's root, its column, its hero, the actions row, a section's anchor (`zune-profile-section:<section key>`), a top-artist
    /// row (`zune-profile-top:<card key>`), the failure, the "isn't available" face.</summary>
    public const string PageKey = "zune-profile-page", ColumnKey = "zune-profile-column", HeroKey = "zune-profile-hero",
        ActionsKey = "zune-profile-actions", SectionPrefix = "zune-profile-section:", TopPrefix = "zune-profile-top:",
        ScrollPrefix = "zune-profile-scroll:", BodyPrefix = "zune-profile-body:", UnavailableKey = "zune-profile-unavailable";

    /// <summary>The list page's root, its head, its tools, a face (`zune-people-face:<load>`).</summary>
    public const string ListPageKey = "zune-people-page", ListHeadKey = "zune-people-head", ListToolsKey = "zune-people-tools",
        ListFacePrefix = "zune-people-face:", ListBodyKey = "zune-people-body";

    // ── the page ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The page's foot (the player dock's reserve is the frame's).</summary>
    public const float PageBottom = 32f;

    /// <summary>The hero's copy: the gap between its lines, and the stats' gap between words.</summary>
    public const float CopyGap = 6f, StatGap = 6f;

    /// <summary>The actions row's step down from the copy, and the gap between its actions.</summary>
    public const float ActionsTop = 8f, ActionsGap = 14f;

    /// <summary>A pill action (Open account): its stroke and its padding.</summary>
    public const float PillStroke = 2f, PillPadX = 16f;

    /// <summary>A ring action (share, the "…" menu): the stroke and the glyph.</summary>
    public const float RingStroke = 2f, RingGlyph = 13f;

    /// <summary>The round art of a card-less avatar placeholder's border.</summary>
    public const float AvatarBorder = 1f;

    /// <summary>A top-artist row: the gap between its numeral, art and words; the words' line gap.</summary>
    public const float TopGap = 14f, TopLineGap = 2f;

    /// <summary>The seeds the shimmer stands in with, at most (a shelf of this many seed cards).</summary>
    public const int SeedCards = 8;

    // ── the lists ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The list page's head: the top inset under the band, the gap under it, the gap between the heading and the name link.</summary>
    public const float ListHeadBottom = 12f, ListHeadGap = 14f, ListBottom = 16f;

    /// <summary>The tools: the gap between the word run and the find box, the air under them, the gap between two chip words.</summary>
    public const float ToolsGap = 20f, ToolsBottom = 8f, WordGap = 20f;

    /// <summary>The shimmer's rows of seed cards, the columns it assumes before the first measure, and the tool bones.</summary>
    public const int ListShimmerRows = 2, ChipBones = 3;

    /// <summary>The tool bones: a chip word, the find box.</summary>
    public const float ChipBoneW = 72f, ChipBoneH = 20f, FindBoneH = 32f;

    /// <summary>The Follow line's bone under a card (the artists of Following).</summary>
    public const float FollowBoneW = 96f, FollowBoneH = 32f;

    /// <summary>The letters (Zune draws them plateless): the header's glyph, the strip's rows and weights.</summary>
    public const float LetterSize = 22f, LetterLine = 28f;

    /// <summary>The letters' weight (light).</summary>
    public const ushort LetterWeight = 300;

    /// <summary>The A-Z strip: a row's width and height, the strip's width, the corner, the absent letter's opacity.</summary>
    public const float StripRowW = 18f, StripRowH = 13f, StripW = 18f, StripRowRadius = 3f, AbsentOpacity = 0.3f;

    /// <summary>The strip's ink weights: resting and active.</summary>
    public const ushort InkWeight = 400, InkActiveWeight = 700;
}

/// <summary>What Zune's profile pages lay out in one CONTENT tier.</summary>
public sealed record ZuneProfileLayout : ITierLayoutRecord
{
    /// <inheritdoc />
    public required WidthTier Tier { get; init; }

    /// <inheritdoc />
    public ImmutableArray<TierRegion> Regions { get; init; } = ImmutableArray<TierRegion>.Empty;

    /// <summary>The page's gutter (the frame's).</summary>
    public required float Gutter { get; init; }

    /// <summary>The gap between sections.</summary>
    public required float SectionGap { get; init; }

    /// <summary>The hero's avatar edge and the gap between it and the copy.</summary>
    public required float Avatar { get; init; }

    /// <inheritdoc cref="Avatar"/>
    public required float HeroGap { get; init; }

    /// <summary>The name's voice.</summary>
    public required ZuneProfileName Name { get; init; }

    /// <summary>The line height the name's box reserves (the largest line of its voice), so a crossing scales the word and moves nothing under it.</summary>
    public required float NameLine { get; init; }

    /// <summary>A hit target: 40 at Compact, 32 elsewhere (the words, the pills, the rings).</summary>
    public required float Target { get; init; }

    /// <summary>The actions row's height (the target, or the rings' 36).</summary>
    public required float ActionsH { get; init; }

    /// <summary>The stats line's height: a linked word is a 40 target at Compact, the line's own 20 elsewhere.</summary>
    public required float StatH { get; init; }

    /// <summary>The hero carries "open account" and the share ring itself (the board's Compact, Small and Huge; at Standard the band's row 2 holds them).</summary>
    public required bool OwnVerbsInHero { get; init; }

    /// <summary>The top artists: the most columns, the narrowest column, the gap between columns and rows, the row, the round art, the numeral's column.</summary>
    public required int TopCols { get; init; }

    /// <inheritdoc cref="TopCols"/>
    public required float TopMinCol { get; init; }

    /// <inheritdoc cref="TopCols"/>
    public required float TopColGap { get; init; }

    /// <inheritdoc cref="TopCols"/>
    public required float TopRowGap { get; init; }

    /// <inheritdoc cref="TopCols"/>
    public required float TopRowH { get; init; }

    /// <inheritdoc cref="TopCols"/>
    public required float TopArt { get; init; }

    /// <inheritdoc cref="TopCols"/>
    public required float RankW { get; init; }

    /// <summary>The list page's narrowest card (the shimmer's fit; the core's grid plan reads the style's artist policy, 176 by default).</summary>
    public required float ListMinCell { get; init; }

    /// <summary>The list shimmer's columns before the body is measured.</summary>
    public required int ListShimmerCols { get; init; }
}

/// <summary>Zune's profile tier records (content tier; ZT7 tunes Compact, Small and Huge against their boards).</summary>
public static class ZuneProfileTiers
{
    private static ZuneProfileLayout Make(WidthTier tier, float sectionGap, float avatar, float heroGap, ZuneProfileName name, float nameLine,
        float target, float actionsH, float statH, bool ownVerbs, int topCols, float topMinCol, float topColGap, float topRowGap, float topRowH, float topArt,
        float rankW, int shimmerCols) => new()
    {
        Tier = tier, Gutter = ZuneTiers.Frame(tier).Gutter, SectionGap = sectionGap, Avatar = avatar, HeroGap = heroGap, Name = name, NameLine = nameLine,
        Target = target, ActionsH = actionsH, StatH = statH, OwnVerbsInHero = ownVerbs, TopCols = topCols, TopMinCol = topMinCol, TopColGap = topColGap,
        TopRowGap = topRowGap, TopRowH = topRowH, TopArt = topArt, RankW = rankW, ListMinCell = 176f, ListShimmerCols = shimmerCols,
    };

    private static readonly ZuneProfileLayout[] s_records =
    [
        // Compact (Z-Profile-Compact): gutter 16, a 96 avatar, one column of 64 ranked rows, every action 40.
        Make(WidthTier.Compact, sectionGap: 20f, avatar: 96f, heroGap: 16f, ZuneProfileName.Title, nameLine: 48f, target: 40f, actionsH: 40f, statH: 40f, ownVerbs: true,
            topCols: 1, topMinCol: 200f, topColGap: 0f, topRowGap: 0f, topRowH: 64f, topArt: 48f, rankW: 48f, shimmerCols: 2),
        // Small (Z-Profile-Small): a 112 avatar, the 40/48 name, two columns of 56 rows, 36 actions.
        Make(WidthTier.Small, sectionGap: 20f, avatar: 112f, heroGap: 24f, ZuneProfileName.Title, nameLine: 48f, target: 32f, actionsH: 36f, statH: 20f, ownVerbs: true,
            topCols: 2, topMinCol: 240f, topColGap: 32f, topRowGap: 0f, topRowH: 56f, topArt: 44f, rankW: 48f, shimmerCols: 3),
        // Standard (Z-Profile): the tuned page: a 160 avatar, the 64/72 name, five columns of 64 rows (56 art), the band holds open account and share.
        Make(WidthTier.Standard, sectionGap: 28f, avatar: 160f, heroGap: 32f, ZuneProfileName.Hero, nameLine: 72f, target: 32f, actionsH: 36f, statH: 20f, ownVerbs: false,
            topCols: 5, topMinCol: 220f, topColGap: 24f, topRowGap: 12f, topRowH: 64f, topArt: 56f, rankW: 52f, shimmerCols: 4),
        // Huge (Z-Profile-Huge): a 240 avatar, the 120 panorama name, five columns of 80 rows (72 art), sections 40 apart.
        Make(WidthTier.Huge, sectionGap: 40f, avatar: 240f, heroGap: 48f, ZuneProfileName.Panorama, nameLine: 130f, target: 32f, actionsH: 36f, statH: 20f, ownVerbs: true,
            topCols: 5, topMinCol: 260f, topColGap: 32f, topRowGap: 20f, topRowH: 80f, topArt: 72f, rankW: 56f, shimmerCols: 6),
        // Huge 4K: Huge's (the page caps at 2400, decision 2).
        Make(WidthTier.Huge4K, sectionGap: 40f, avatar: 240f, heroGap: 48f, ZuneProfileName.Panorama, nameLine: 130f, target: 32f, actionsH: 36f, statH: 20f, ownVerbs: true,
            topCols: 5, topMinCol: 260f, topColGap: 32f, topRowGap: 20f, topRowH: 80f, topArt: 72f, rankW: 56f, shimmerCols: 6),
    ];

    /// <summary>The record of content tier <paramref name="content"/>.</summary>
    public static ZuneProfileLayout For(WidthTier content) => s_records[Math.Clamp((int)content, 0, s_records.Length - 1)];

    /// <summary>The record for <paramref name="facts"/>; Standard's where no frame resolved the tiers (a view mounted alone).</summary>
    public static ZuneProfileLayout Of(in TierFacts facts) => For(facts.Policy is null ? WidthTier.Standard : facts.Content);
}
