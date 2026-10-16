// ── Themes/Classic/Structure/Rules/Tiers.Profile.cs — Classic's people ladders and the profile pages' tier records (themes plan 3.2, 3.6; TC6) ──────
//
// THE SUB-LADDER (moved out of core: `Entities\Profile.Lists.cs` `ProfileGridFit.ShowsStrip` / `ToolsStackBelow`), written with the engine's `TierLadder`:
//
//   list   measured on the profile list page's body. 0 narrow below 640 (no A–Z strip, the chips over the find box), 1 wide (the strip beside the
//          grid, the tools side by side); re-admitted 24 past the edge, so a body resting on 640 never flickers between them.
//
// The profile HERO's sub-tier is the artist hero's (`ClassicArtistTiers.Hero`: the two pages share the band and the collapse arithmetic); what each sub-tier
// lays out per window tier is below (`ClassicProfileHeroForm`, from the old core `ProfileHeroLayout` at Standard), and the core reads its metrics through
// `ClassicArtistTierPolicy.ProfileHero` (`ThemePolicies.ArtistTiers`).
//
// THE RECORDS (boards C-Profile, C-Profile-Compact, C-Profile-Small, C-Profile-Huge; ProfileList has no board and is built from Classic's parts, owner Q18):
//
//   tier       hero                                                                        shelves               list grid
//   Compact    centred: a 128 avatar, the 32/40 name, the stats as 96 x 56 targets, the     a 164 strip, the      2 columns (128 floor)
//              actions a 40 row (C-Profile-Compact); Narrow splits the stats in two rows    3rd peeks
//   Small      a row: a 128 avatar, the 48/60 name and the stats beside it, the actions at   fit 150-200 (4 at
//              the row's right end, 200 tall (C-Profile-Small)                             800)                  the 176 floor
//   Standard   the tuned design (P3b): Wide 260 / Medium 220 rows, Compact 316 / Narrow 352  fit 150-200           the 176 floor
//              stacked
//   Huge       a row: a 240 avatar, the 84/96 name, the stats 32 apart, the actions at the   10 columns            the 176 floor
//              right end, 320 tall (C-Profile-Huge)
//   Short      a hero over 288 (60 % of the 480 page) becomes a 96-avatar row; under 480 tall the 64-avatar strip with the name alone.
//
// Public, so `Wavee.Tests` pins the ladder and the records (the assembly has no InternalsVisibleTo).

using System.Collections.Immutable;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>How the profile hero lays the avatar, the copy and the actions out.</summary>
public enum ClassicProfileHeroArm : byte
{
    /// <summary>The avatar beside the copy, bottom-aligned.</summary>
    Row,
    /// <summary>The avatar over the copy, both on the left (the tuned stacked arm).</summary>
    Stacked,
    /// <summary>The avatar over the copy, everything centred (C-Profile-Compact).</summary>
    Centred,
}

/// <summary>What the profile hero lays out in one (window tier, hero sub-tier).</summary>
public sealed record ClassicProfileHeroForm
{
    /// <summary>The hero's sub-tier (the artist hero's).</summary>
    public required ArtistHeroTier Tier { get; init; }

    /// <summary>How the avatar, the copy and the actions are laid out.</summary>
    public required ClassicProfileHeroArm Arm { get; init; }

    /// <summary>The avatar's edge.</summary>
    public required float Avatar { get; init; }

    /// <summary>The name's type rung and its line box.</summary>
    public required ClassicArtistName Name { get; init; }

    /// <summary>The name's line box (96 / 60 / 40).</summary>
    public required float NameLine { get; init; }

    /// <summary>The stat rows (0 hides the stats: the Short strip).</summary>
    public required int StatRows { get; init; }

    /// <summary>The air over and under the content.</summary>
    public required float TopPad { get; init; }

    /// <summary>The air under the content.</summary>
    public required float BottomPad { get; init; }

    /// <summary>The copy column's widest.</summary>
    public required float CopyMax { get; init; }

    /// <summary>The gap between the avatar and the copy.</summary>
    public required float AvatarGap { get; init; }

    /// <summary>The gap between two stats.</summary>
    public required float StatGap { get; init; }

    /// <summary>The name → stats and stats → actions steps.</summary>
    public required float NameToStats { get; init; }

    /// <summary>The stats → actions step (when the actions sit under the copy).</summary>
    public required float StatsToActions { get; init; }

    /// <summary>The actions show (false: the Short strip; the band carries them).</summary>
    public required bool Actions { get; init; }

    /// <summary>The actions sit at the row's right end (C-Profile-Small, -Huge) instead of under the stats.</summary>
    public required bool ActionsBeside { get; init; }

    /// <summary>The actions' edge: 40 at Compact, the control's 32 elsewhere.</summary>
    public required float Target { get; init; }

    /// <summary>A stat cell's least height and width (C-Profile-Compact's 96 x 56 targets); 0 is the stat's own.</summary>
    public required float StatMinH { get; init; }

    /// <summary>A stat cell's least width.</summary>
    public required float StatMinW { get; init; }

    /// <summary>The stats, each <see cref="ClassicProfileTiers.StatHeight"/> tall (or <see cref="StatMinH"/>), and the gaps between their rows.</summary>
    public float StatsHeight => StatRows <= 0 ? 0f
        : StatRows * MathF.Max(ClassicProfileTiers.StatHeight, StatMinH) + (StatRows - 1) * ClassicProfileTiers.StatRowGap;

    /// <summary>The copy column's worst case: the name · gap · the stat rows · gap · the action row (when it sits under the copy).</summary>
    public float CopyHeight => NameLine
        + (StatRows > 0 ? NameToStats + StatsHeight : 0f)
        + (Actions && !ActionsBeside ? StatsToActions + Target : 0f);

    /// <summary>The whole hero: the pads around the avatar beside the copy, or over it.</summary>
    public float Height => TopPad + BottomPad + (Arm == ClassicProfileHeroArm.Row ? MathF.Max(Avatar, CopyHeight) : Avatar + AvatarGap + CopyHeight);

    /// <summary>The metrics the core latches for this form at the page gutter <paramref name="gutter"/>.</summary>
    public ProfileHeroMetrics Metrics(float gutter) => new(Tier, Height, gutter, Avatar, NameLine, 1, StatRows, TopPad, BottomPad, CopyMax);
}

/// <summary>What Classic's profile pages lay out in one window tier.</summary>
public sealed record ClassicProfileLayout
{
    /// <summary>The window tier.</summary>
    public required WidthTier Tier { get; init; }

    /// <summary>The shelves' fit.</summary>
    public required ClassicShelfBand Shelf { get; init; }

    /// <summary>The section pivot's row (C-Profile-Compact's 48; the band's 56 elsewhere).</summary>
    public required float Target { get; init; }

    /// <summary>The profile list grid's narrowest card.</summary>
    public required float ListMinCell { get; init; }

    /// <summary>The widest the magazine (and the hero's row) grows: the page measure up to Standard, Huge's 2400 fill rule.</summary>
    public float PageMaxWidth => Tier >= WidthTier.Huge ? ClassicArtistTiers.HugePageMax : R.Space.PageMaxW;
}

/// <summary>Classic's people ladder and the profile pages' tier records.</summary>
public static class ClassicProfileTiers
{
    // ── the list ladder ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The list page shows its A–Z strip and lays its tools side by side from this body width.</summary>
    public const float ListWideFrom = 640f;

    /// <summary>The list edge's hold on widening.</summary>
    public const float ListHold = 24f;

    /// <summary>The list arm ladder (0 narrow, 1 wide).</summary>
    public static TierLadder List { get; } = TierLadder.Of(new TierEdge(ListWideFrom, NarrowHold: 0f, WidenHold: ListHold));

    /// <summary>The ladder Classic's policy names.</summary>
    public static ImmutableArray<NamedLadder> Ladders { get; } = ImmutableArray.Create(new NamedLadder(ArtistLadders.ProfileList, List));

    // ── the hero's rhythm (the old core `ProfileHeroLayout`'s numbers) ───────────────────────────────────────────────────

    /// <summary>StatHero's 36 + its 12/16 caption; the gap between two stat rows.</summary>
    public const float StatHeight = 52f, StatRowGap = 8f;

    /// <summary>The Compact list grid's narrowest card and every other tier's (<c>SectionScreen</c>'s <c>SectionCardMinWidth</c>).</summary>
    public const float CompactListMinCell = 128f, ListMinCell = 176f;

    private static readonly ClassicProfileHeroForm s_wide = new()
    {
        Tier = ArtistHeroTier.Wide, Arm = ClassicProfileHeroArm.Row, Avatar = 184f, Name = ClassicArtistName.Display, NameLine = 96f, StatRows = 1,
        TopPad = 16f, BottomPad = 32f, CopyMax = 1120f, AvatarGap = 36f, StatGap = 24f, NameToStats = 12f, StatsToActions = 20f,
        Actions = true, ActionsBeside = false, Target = ClassicArtistTiers.Target, StatMinH = 0f, StatMinW = 0f,
    };

    private static readonly ClassicProfileHeroForm s_medium = s_wide with
    {
        Tier = ArtistHeroTier.Medium, Avatar = 144f, Name = ClassicArtistName.Title, NameLine = 60f, BottomPad = 28f, CopyMax = 760f,
    };

    private static readonly ClassicProfileHeroForm s_compact = s_wide with
    {
        Tier = ArtistHeroTier.Compact, Arm = ClassicProfileHeroArm.Stacked, Avatar = 112f, Name = ClassicArtistName.Compact, NameLine = 40f,
        BottomPad = 24f, CopyMax = 640f, AvatarGap = 16f, NameToStats = 8f, StatsToActions = 16f,
    };

    private static readonly ClassicProfileHeroForm s_narrow = s_compact with
    {
        Tier = ArtistHeroTier.Narrow, Avatar = 96f, StatRows = 2, TopPad = 12f, BottomPad = 20f, CopyMax = 520f,
    };

    // C-Profile-Compact: centred, 20 over a 128 avatar, the name 12 under it, the stats 8 under that as 96 x 56 targets, the actions a 40 row 12 under them.
    private static readonly ClassicProfileHeroForm s_centred = new()
    {
        Tier = ArtistHeroTier.Compact, Arm = ClassicProfileHeroArm.Centred, Avatar = 128f, Name = ClassicArtistName.Compact, NameLine = 40f, StatRows = 1,
        TopPad = 20f, BottomPad = 16f, CopyMax = 640f, AvatarGap = 12f, StatGap = 4f, NameToStats = 8f, StatsToActions = 12f,
        Actions = true, ActionsBeside = false, Target = ClassicArtistTiers.CompactTarget, StatMinH = 56f, StatMinW = 96f,
    };

    private static readonly ClassicProfileHeroForm s_centredNarrow = s_centred with { Tier = ArtistHeroTier.Narrow, StatRows = 2 };

    // C-Profile-Small: a 200 row, a 128 avatar 24 from the copy, the 48/60 name 4 over the stats, the actions at the right end.
    private static readonly ClassicProfileHeroForm s_small = s_medium with
    {
        Avatar = 128f, TopPad = 52f, BottomPad = 20f, CopyMax = 560f, AvatarGap = 24f, NameToStats = 4f, ActionsBeside = true,
    };

    // C-Profile-Huge: a 320 row, a 240 avatar 40 from the copy, the stats 32 apart, the actions at the right end.
    private static readonly ClassicProfileHeroForm s_huge = s_wide with
    {
        Avatar = 240f, TopPad = 48f, BottomPad = 32f, AvatarGap = 40f, StatGap = 32f, ActionsBeside = true,
    };

    /// <summary>The hero's form for sub-tier <paramref name="tier"/> in the tiers <paramref name="facts"/>; the Standard forms where no frame resolved them.</summary>
    public static ClassicProfileHeroForm HeroForm(ArtistHeroTier tier, in TierFacts facts, float width)
    {
        var window = facts.Policy is null ? WidthTier.Standard : (WidthTier)Math.Min((int)facts.Window, (int)WidthTier.Huge);
        var form = window switch
        {
            WidthTier.Compact => tier >= ArtistHeroTier.Compact ? s_centred : s_centredNarrow,
            WidthTier.Small => tier >= ArtistHeroTier.Medium ? s_small : s_centred with { Target = ClassicArtistTiers.Target, StatMinH = 0f, StatMinW = 0f },
            WidthTier.Huge when tier == ArtistHeroTier.Wide => s_huge,
            _ => StandardForm(tier),
        };
        if (facts.Policy is null || facts.Height != HeightTier.Short) return form;
        bool strip = facts.Policy.IndexOfRung(ClassicTiers.HeroFullRung) >= 0 && !facts.Rung(ClassicTiers.HeroFullRung);
        if (strip)
            return form with
            {
                Arm = ClassicProfileHeroArm.Row, Avatar = 64f, Name = ClassicArtistName.Compact, NameLine = 40f, StatRows = 0, TopPad = 16f, BottomPad = 12f,
                AvatarGap = 16f, Actions = false, StatMinH = 0f, StatMinW = 0f,
            };
        if (form.Height <= ClassicArtistTiers.ShortHeroCap) return form;
        return form with
        {
            Arm = ClassicProfileHeroArm.Row, Avatar = 96f, Name = ClassicArtistName.Compact, NameLine = 40f, TopPad = 16f, BottomPad = 16f, AvatarGap = 16f,
            NameToStats = 8f, StatsToActions = 12f, ActionsBeside = false, StatMinH = 0f, StatMinW = 0f,
            StatRows = tier == ArtistHeroTier.Narrow ? 2 : 1,
        };
    }

    /// <summary>The tuned Standard form of a sub-tier.</summary>
    public static ClassicProfileHeroForm StandardForm(ArtistHeroTier tier) => tier switch
    {
        ArtistHeroTier.Wide => s_wide,
        ArtistHeroTier.Medium => s_medium,
        ArtistHeroTier.Compact => s_compact,
        _ => s_narrow,
    };

    /// <summary>The hero's metrics: the form's, at the page gutter of the content tier on screen.</summary>
    public static ProfileHeroMetrics HeroMetrics(ArtistHeroTier tier, in TierFacts facts, float width)
        => HeroForm(tier, in facts, width).Metrics(ClassicTiers.PageOf(in facts).Gutter);

    // ── the page records (per window tier) ──────────────────────────────────────────────────────────────────────────

    /// <summary>Compact (C-Profile-Compact): a 164 strip whose third card peeks, the 48 pivot row, a two-column list grid.</summary>
    public static ClassicProfileLayout Compact { get; } = new()
    {
        Tier = WidthTier.Compact, Shelf = ClassicArtistTiers.Compact.Shelf, Target = ClassicArtistTiers.CompactTarget, ListMinCell = CompactListMinCell,
    };

    /// <summary>Small (C-Profile-Small: four columns at 800).</summary>
    public static ClassicProfileLayout Small { get; } = new()
    {
        Tier = WidthTier.Small, Shelf = ClassicArtistTiers.Small.Shelf, Target = ClassicArtistTiers.Target, ListMinCell = ListMinCell,
    };

    /// <summary>Standard: the tuned design.</summary>
    public static ClassicProfileLayout Standard { get; } = Small with { Tier = WidthTier.Standard };

    /// <summary>Huge (C-Profile-Huge: ten columns).</summary>
    public static ClassicProfileLayout Huge { get; } = Small with { Tier = WidthTier.Huge, Shelf = ClassicArtistTiers.Huge.Shelf };

    /// <summary>The record of window tier <paramref name="tier"/>.</summary>
    public static ClassicProfileLayout For(WidthTier tier) => tier switch
    {
        WidthTier.Compact => Compact,
        WidthTier.Small => Small,
        WidthTier.Standard => Standard,
        _ => Huge,
    };

    /// <summary>The record of <paramref name="facts"/>; Standard's where no frame resolved the tiers.</summary>
    public static ClassicProfileLayout Of(in TierFacts facts) => facts.Policy is null ? Standard : For(facts.Window);
}
