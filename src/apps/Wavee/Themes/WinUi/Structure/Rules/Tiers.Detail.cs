// ── Themes/WinUi/Structure/Rules/Tiers.Detail.cs — WinUI's detail ladders and the detail pages' tier records (themes plan 3.2, 6.2; P6 W6a) ───────────
//
// THE TWO SUB-LADDERS the detail frame resolves by id (`DetailLadders`):
//
//   mode    The page-mode ladder, measured on the page's own column. WinUI has ONE composition at every width: the hero over the table (the core's
//           "vertical arm"); the two-column rail arm is Classic's and Zune's. Its three edges are therefore absent (`TierEdge.None`): every width answers
//           index 0, the vertical arm. What changes with the width is the hero's FORM (stacked and centred at Compact, horizontal from Small: the window
//           tier's record below), never the arm, so a tier crossing never swaps the page's table (it would lose the list's place) and the Hero page layout
//           (`DetailPolicy`) says the same to the host.
//   height  The window's height rungs: Short below 720 (held 24 on narrowing, the window's own Short | Regular edge), Regular from 720; the tall rung is
//           never reached (the board draws two heroes: the tall and the short one). Short is also `TierFacts.Height`, which the hero reads to pick its form.
//
// THE RECORDS (one per WINDOW tier, boards W-Album / -Compact / -Small / -Huge / -Huge4K, W-SkeletonAlbum, W-Shell-Short; the numbers are `WinUiDetailRules`):
//   · Compact: the stacked hero (a 160 cover centred over the centred words, 40 targets), the Compact row form (the table record in `WinUiListRules`), the page
//     inset 16.
//   · Small: the horizontal hero (168 cover, Title Large 40/52), the table without its chevron lane, inset 24.
//   · Standard: the same hero at inset 36, every lane the list column admits.
//   · Huge (and 3840, which is Huge's record with more room): the 232 cover, inset 48, the 72 rows, and the ASIDE column: 440 (480 from 3200) docked beside the
//     hero and the list (About this album · Artists on this album · More by), `DetailTierLayout.AsideInHero`.
//   · Short (any width tier, `TierFacts.Height`): the hero is a 104 cover with the title at 28/36 and no eyebrow, and the pinned band the hero collapses into is
//     the 48 sticky bar, after 120 of scroll (the short hero's 168 less the band).
//
// Public, so `Wavee.Tests` pins the ladders and the records.

using System.Collections.Immutable;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

public static partial class WinUiTiers
{
    static partial void LaddersDetail(ImmutableArray<NamedLadder>.Builder ladders)
    {
        ladders.Add(new NamedLadder(DetailLadders.Mode, WinUiDetailTiers.Mode));
        ladders.Add(new NamedLadder(DetailLadders.Height, WinUiDetailTiers.Height));
    }

    static partial void RungsDetail(ImmutableArray<NamedRung>.Builder rungs)
    {
        rungs.Add(WinUiDetailTiers.AsideWide);
    }
}

/// <summary>WinUI's detail ladders and the hero records.</summary>
public static class WinUiDetailTiers
{
    // ── the ladders ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The page-mode ladder: every edge absent, so the vertical (hero) arm at every width (index 0).</summary>
    public static TierLadder Mode { get; } = TierLadder.Of(TierEdge.None, TierEdge.None, TierEdge.None);

    /// <summary>The height ladder: Short (rung 0) below 720 held 24, Regular (rung 1) from 720; the tall rung (2) is never reached.</summary>
    public static TierLadder Height { get; } = TierLadder.Of(
        new TierEdge(WinUiTiers.ShortBelow, NarrowHold: WinUiTiers.Hold, WidenHold: 0f),
        TierEdge.None);

    /// <summary>The rung from which the Huge aside is the wide one (480, W-Album-Huge4K; Huge's is 440): window width 3200, held 40 on narrowing (the midpoint of the
    /// 2560 and 3840 boards, as Classic's 4K rung).</summary>
    public const string AsideWideRung = "winui.detail.aside.wide";

    /// <summary>Where the wide aside begins and its hold on narrowing.</summary>
    public const float AsideWideFrom = 3200f, AsideWideHold = 40f;

    /// <summary>The wide aside's rung, on the window's width.</summary>
    public static NamedRung AsideWide { get; } = new(AsideWideRung, new TierEdge(AsideWideFrom, NarrowHold: AsideWideHold, WidenHold: 0f), RungAxis.Window);

    // ── the records ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The Compact hero (W-Album-Compact).</summary>
    public static WinUiDetailTier Compact { get; } = new(WidthTier.Compact, WinUiDetailRules.CompactHero, WinUiDetailRules.ShortHeroCompact);

    /// <summary>The Small hero (W-Album-Small).</summary>
    public static WinUiDetailTier Small { get; } = new(WidthTier.Small, WinUiDetailRules.SmallHero, WinUiDetailRules.SmallShortHero);

    /// <summary>The Standard hero (W-Album).</summary>
    public static WinUiDetailTier Standard { get; } = new(WidthTier.Standard, WinUiDetailRules.StandardHero, WinUiDetailRules.ShortHero);

    /// <summary>The Huge hero (W-Album-Huge; 3840 is the same record with the wide aside).</summary>
    public static WinUiDetailTier Huge { get; } = new(WidthTier.Huge, WinUiDetailRules.HugeHero, WinUiDetailRules.ShortHero);

    /// <summary>The record of window tier <paramref name="tier"/> (Huge's from 1920 up).</summary>
    public static WinUiDetailTier For(WidthTier tier) => tier switch
    {
        WidthTier.Compact => Compact,
        WidthTier.Small => Small,
        WidthTier.Standard => Standard,
        _ => Huge,
    };

    /// <summary>The record of <paramref name="facts"/>; Standard's where no frame resolved the tiers (a view mounted alone).</summary>
    public static WinUiDetailTier Of(in TierFacts facts) => facts.Policy is null ? Standard : For(facts.Window);

    /// <summary>Is the wide aside (3200 and up) the one this window draws?</summary>
    public static bool AsideIsWide(in TierFacts facts)
        => facts.Policy is { } policy && facts.Window >= WidthTier.Huge && policy.IndexOfRung(AsideWideRung) >= 0 && facts.Rung(AsideWideRung);

    /// <summary>The aside column's width in <paramref name="facts"/>: 440, 480 from 3200.</summary>
    public static float AsideWidthOf(in TierFacts facts) => AsideIsWide(in facts) ? WinUiDetailRules.AsideWideWidth : WinUiDetailRules.AsideWidth;

    /// <summary>The hero's geometry in <paramref name="facts"/>: the short form when the window is short, the tall one otherwise.</summary>
    public static WinUiHeroForm HeroOf(in TierFacts facts)
    {
        var tier = Of(in facts);
        return facts.Height == HeightTier.Short ? tier.Short : tier.Tall;
    }

    /// <summary>The page mode of a <paramref name="width"/>-wide column held against <paramref name="previous"/>: always the vertical arm (3).</summary>
    public static int ModeFor(float width, int previous, bool initialized = true)
    {
        if (!(width > 0f)) return previous;
        return DetailLadders.ModeOf(Mode.Resolve(width, initialized ? DetailLadders.IndexOfMode(previous) : -1));
    }
}

/// <summary>What WinUI's detail pages lay out in one window tier: the tall hero and the short one.</summary>
/// <param name="Tier">The window tier.</param>
/// <param name="Tall">The hero of a window 720 tall and over.</param>
/// <param name="Short">The hero of a short window (the 104 cover, no eyebrow).</param>
public sealed record WinUiDetailTier(WidthTier Tier, WinUiHeroForm Tall, WinUiHeroForm Short)
{
    /// <summary>The page inset, left and right: 16 / 24 / 36 / 48 (the frame's; `WinUiFrameLayout.InsetX`).</summary>
    public float InsetX => WinUiDetailRules.InsetOf(Tier);

    /// <summary>The page inset at the top: 16 / 20 / 24 / 32 (the frame's).</summary>
    public float InsetTop => WinUiDetailRules.InsetTopOf(Tier);

    /// <summary>The hit target of the hero's actions and the list bar's commands: 40 at Compact, 32 elsewhere.</summary>
    public float Target => Tier == WidthTier.Compact ? WinUiDetailRules.CompactTarget : WinUiDetailRules.Target;

    /// <summary>The table's record in this tier (`WinUiListRules`), with the page inset, the band and the aside.</summary>
    public DetailTierLayout Core => Tier switch
    {
        WidthTier.Compact => WinUiListRules.Compact,
        WidthTier.Small => WinUiListRules.Small,
        WidthTier.Standard => WinUiListRules.Standard,
        _ => WinUiListRules.Huge,
    };
}

/// <summary>The geometry of one hero form.</summary>
/// <param name="Stacked">The cover over the words, both centred (W-Album-Compact); false: the cover left, the words bottom-aligned beside it.</param>
/// <param name="Cover">The cover's edge.</param>
/// <param name="Gap">The gap between the cover and the words (horizontal) or under the cover (stacked).</param>
/// <param name="Eyebrow">The eyebrow row is drawn.</param>
/// <param name="EyebrowSize">The eyebrow's size and line (12/16, 14/20 at Huge).</param>
/// <param name="EyebrowLine">The eyebrow's line height.</param>
/// <param name="TitleSize">The title's size (Title Large 40, Title 28 stacked, 28 short).</param>
/// <param name="TitleLine">The title's line height (52, 36, 36).</param>
/// <param name="InfoGap">The gap between the words' rows (4; 6 at Huge; 2 stacked).</param>
/// <param name="Avatar">The attribution's avatars (24, 28 at Huge; 0: none).</param>
/// <param name="ActionTop">The air above the action row (12, 14 at Huge).</param>
/// <param name="ListGap">The air between the hero and the list bar under it.</param>
/// <param name="TopInset">The air over the hero (the tier's top inset; the short hero's 8).</param>
/// <param name="ListAvatar">A LIST's attribution portrait (a playlist's owner, Liked Songs: W-Playlist-Small's 20, W-Playlist-Compact's 24) where it differs from an
/// album's <paramref name="Avatar"/> (W-Album-Small draws none); negative: the same as <paramref name="Avatar"/>.</param>
/// <param name="Description">A playlist's description line is drawn under the title (W-Playlist, W-PlaylistFollowed); false: the short heroes, where a followed list's
/// description rides the eyebrow (W-PlaylistFollowed-Small) and the own list's prompt lives in the menu.</param>
/// <param name="ChipHeight">The eyebrow's "Saved" chip (W-Playlist: 20, 18 on the Small board): the eyebrow row of a list that can carry it holds this height.</param>
public sealed record WinUiHeroForm(bool Stacked, float Cover, float Gap, bool Eyebrow, float EyebrowSize, float EyebrowLine, float TitleSize, float TitleLine,
    float InfoGap, float Avatar, float ActionTop, float ListGap, float TopInset, float ListAvatar = -1f, bool Description = true, float ChipHeight = 20f)
{
    /// <summary>The attribution portrait for a page of <paramref name="kind"/>.</summary>
    public float AvatarFor(DetailKind kind) => kind is DetailKind.Playlist or DetailKind.Liked && ListAvatar >= 0f ? ListAvatar : Avatar;
}
