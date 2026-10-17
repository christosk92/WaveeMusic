// ── Entities/Profile.Rules.cs ──────────────────────────────────────────────────────────────────────────────────────────
// The profile page's PURE decisions (engine-free; Wavee.Tests drives each one): the body reveal latch, the section plan
// and each section's body, the hero's metrics and collapse (the tiers are the structure's, TC6), the hero's stat cells and the tone. The LOAD verdict is NOT here: it is
// the data layer's one vocabulary (`ProfileLoad` + `ProfileLoadRule`, Entities/User.Profile.Rules.cs).
//
// Role: CORE (pure)   Owner: U1   Template: ArtistSections / ArtistHeroLayout (Artist.UI.cs §1-§3), BrowseLoadGate
// Spec: docs/plans/wavee/profile-pages-implementation.md Appendix U §2 + "Reconciliation, round 2"
//
// Types are PUBLIC (this assembly has no InternalsVisibleTo; Wavee.Tests pins them directly).

using System.Globalization;
using FluentGpu.Dsl;

namespace Wavee;

// ══ 1. THE REVEAL ═════════════════════════════════════════════════════════════════════════════════════════════════════

public static class ProfileReveal
{
    /// <summary>The ONE reveal: the header Ready and the width measured; latched once true (a refresh never re-shimmers).</summary>
    public static bool BodyReady(ProfileLoad header, bool measured, bool revealed)
        => revealed || (header == ProfileLoad.Ready && measured);
}

// ══ 2. THE SECTIONS ═══════════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>Every section the page can render, in the ONE fixed order (official shelf order, research §1). Distinct from
/// <see cref="ProfileShelf"/> (the data relation) and the list route's facet on purpose.</summary>
public enum ProfileSection : byte { TopArtists, Playlists, RecentArtists, Following, Followers }

/// <summary>What a present section's region shows.</summary>
public enum ProfileSectionBody : byte { Seed, Cards, Empty, Error }

/// <summary>What the page knows, read once per compose. Edge states are <c>Readiness</c> (so Failed is visible); the
/// follow lists go through <see cref="ProfileSections.ListState"/>, top artists through <see cref="ProfileSections.TopState"/>.</summary>
public readonly record struct ProfileFacts(
    bool Own, bool ShowFollows,
    EdgeState TopArtists, int TopArtistCount,
    EdgeState Playlists, int PlaylistCount,
    EdgeState RecentArtists, int RecentArtistCount,
    EdgeState Following, int FollowingCount,
    EdgeState Followers, int FollowerCount);

/// <summary>One shelf card: the row it stands for, that row's version (the shelf's value gate), the edge's follower
/// count (playlist subtitle), its 1-based rank, and its key (kind-tagged: Following mixes artist and user slots).</summary>
public readonly record struct ProfileCard(EntityKind Kind, int Slot, uint Version, int Followers, int Rank, string Key);

public static class ProfileSections
{
    public const int Count = 5;
    public const int TopCap = 10, PlaylistCap = 10, ArtistCap = 10, PeopleCap = 20;

    static readonly string[] s_keys = ["top-artists", "playlists", "recent-artists", "following", "followers"];
    public static string Key(ProfileSection s) => s_keys[(int)s];

    public static int CapOf(ProfileSection s) => s switch
    {
        ProfileSection.TopArtists => TopCap,
        ProfileSection.Playlists => PlaylistCap,
        ProfileSection.RecentArtists => ArtistCap,
        _ => PeopleCap,
    };

    /// <summary>The follow lists show on your own profile always, on another's only when its owner shows them.</summary>
    public static bool ShowsPeople(bool own, bool showFollows) => own || showFollows;

    /// <summary>Rows win over every state (a failed refresh keeps rendering what landed); then Failed → Error,
    /// Complete → Empty, anything still coming → Seed.</summary>
    public static ProfileSectionBody BodyOf(EdgeState state, int count)
        => count > 0 ? ProfileSectionBody.Cards
         : state == EdgeState.Failed ? ProfileSectionBody.Error
         : state == EdgeState.Complete ? ProfileSectionBody.Empty
         : ProfileSectionBody.Seed;

    /// <summary>The page's sections into <paramref name="into"/> (≥ <see cref="Count"/>). Public playlists is
    /// unconditional (empty is a designed state); the optional shelves drop on an empty or failed answer; a people
    /// preview drops only on an EMPTY answer (a failed one carries its own Retry).</summary>
    public static int Plan(in ProfileFacts f, Span<ProfileSection> into)
    {
        int n = 0;
        if (f.Own && Optional(f.TopArtists, f.TopArtistCount)) into[n++] = ProfileSection.TopArtists;
        into[n++] = ProfileSection.Playlists;
        if (Optional(f.RecentArtists, f.RecentArtistCount)) into[n++] = ProfileSection.RecentArtists;
        if (ShowsPeople(f.Own, f.ShowFollows))
        {
            if (People(f.Following, f.FollowingCount)) into[n++] = ProfileSection.Following;
            if (People(f.Followers, f.FollowerCount)) into[n++] = ProfileSection.Followers;
        }
        return n;
    }

    static bool Optional(EdgeState s, int count) => count > 0 || s is EdgeState.Unknown or EdgeState.Partial;
    static bool People(EdgeState s, int count) => count > 0 || s != EdgeState.Complete;

    /// <summary><see cref="ProfileLoad"/> as an edge state: in flight → Unknown, failed → Failed, concluded (Ready, or
    /// nothing will ever come) → Complete.</summary>
    public static EdgeState StateOf(ProfileLoad load)
        => load == ProfileLoad.Loading ? EdgeState.Unknown
         : load == ProfileLoad.Failed ? EdgeState.Failed
         : EdgeState.Complete;

    /// <summary>Own top artists ride Home.Feeds (a HomeLoad, not an edge mark) — D3's <see cref="ProfileLoadRule.TopArtists"/>
    /// read as an edge state: rows → Complete; in flight → Unknown; failed → Failed; Idle (offline / --fake) or Ready-empty
    /// → Complete (the section drops).</summary>
    public static EdgeState TopState(HomeLoad load, int count) => StateOf(ProfileLoadRule.TopArtists(load, count));

    /// <summary>A follow list's readiness for the preview: a list with no route (nothing will ever answer, no failure to
    /// retry) is concluded-empty, so its section drops; every other state is the edge's own.</summary>
    public static EdgeState ListState(EdgeState readiness, int failure)
        => ProfileLoadRule.List(readiness, failure) == ProfileLoad.Unavailable ? EdgeState.Complete : readiness;

    /// <summary>"See all" only for the two people sections (public playlists has no list route), and only when there is
    /// more than the shelf shows.</summary>
    public static bool SeeAll(ProfileSection s, int total, int shown)
        => (s == ProfileSection.Following || s == ProfileSection.Followers) && total > shown;
}

// ══ 3. THE HERO'S METRICS AND ITS COLLAPSE ARITHMETIC ════════════════════════════════════════════════════════════════

public readonly record struct ProfileHeroMetrics(
    ArtistHeroTier Tier, float Height, float Gutter, float Avatar, float NameLine, int NameLines, int StatRows,
    float TopPad, float BottomPad, float CopyMaxWidth)
{
    public bool Stacked => Tier is ArtistHeroTier.Compact or ArtistHeroTier.Narrow;
}

/// <summary>The round-avatar hero's tier-free arithmetic: the collapse into the 56-DIP band and the wash under it. The sub-tier is the ARTIST hero's (the
/// two pages share the band and the collapse arithmetic: <see cref="Wavee.Theming.ArtistLadders.Hero"/>); what each sub-tier lays out (the avatar, the name's
/// line, the stat rows, the pads and the height they budget) is the structure's (<see cref="Wavee.Theming.ArtistTierPolicy.ProfileHero"/>; Classic:
/// <c>Rules\Tiers.Profile.cs</c>), content bottom-aligned, so the sticky box never resizes.</summary>
public static class ProfileHeroLayout
{
    /// <summary>The ab6775700000ee85 avatar rendition is 300 px; decoding larger is wasted.</summary>
    public const int AvatarDecodePx = 300;

    public static float CollapseDistance(in ProfileHeroMetrics m) => ArtistHeroLayout.CollapseDistance(m.Height);
    public static float WashHeight(in ProfileHeroMetrics m) => m.Height + ArtistHeroLayout.ContentBlendTail;
    public static float WashBoundary(in ProfileHeroMetrics m) => m.Height / (m.Height + ArtistHeroLayout.ContentBlendTail);
}

// ══ 4. THE HERO'S STATS ═══════════════════════════════════════════════════════════════════════════════════════════════

public enum ProfileStat : byte { Followers, Following, Playlists }
public readonly record struct ProfileStatCell(ProfileStat Kind, int Value, bool Links);

public static class ProfileStats
{
    public const int Max = 3;

    /// <summary>Followers · Following (only when the lists show) · Public playlists (always). Followers/Following link to
    /// their list (a zero never links); Public playlists NEVER links (the list route has no playlists facet).</summary>
    public static int Plan(bool own, bool showFollows, int followers, int following, int playlists, Span<ProfileStatCell> into)
    {
        int n = 0;
        if (ProfileSections.ShowsPeople(own, showFollows))
        {
            into[n++] = new(ProfileStat.Followers, Math.Max(0, followers), followers > 0);
            into[n++] = new(ProfileStat.Following, Math.Max(0, following), following > 0);
        }
        into[n++] = new(ProfileStat.Playlists, Math.Max(0, playlists), false);
        return n;
    }

    /// <summary>Cells per row for a row budget (Narrow splits 3 → 2 + 1).</summary>
    public static int PerRow(int cells, int rows) => rows <= 1 || cells <= 1 ? Math.Max(1, cells) : (cells + rows - 1) / rows;

    public static string Numeral(int value, CultureInfo culture) => Math.Max(0, value).ToString("N0", culture);
}

// ══ 5. THE TONE ═══════════════════════════════════════════════════════════════════════════════════════════════════════

public readonly record struct ProfileToneSource(string? PaletteUrl, uint PayloadArgb);

/// <summary>Which colour leads the page. A GRADEABLE avatar is the person's colour (the official header uses the
/// avatar's colorRaw); the profile's brand colour is unrelated to that photo, so it is the payload ONLY when there is no
/// gradeable avatar (no image, or a non-scdn CDN) — then it paints on the first frame and is definite.</summary>
public static class ProfileTone
{
    /// <summary>0xRRGGBB (or 0xFFRRGGBB) → the app's payload convention (opaque ARGB); 0 stays "none". Idempotent.</summary>
    public static uint Argb(uint rgb) => (rgb & 0x00FFFFFFu) == 0 ? 0u : 0xFF000000u | (rgb & 0x00FFFFFFu);

    public static ProfileToneSource Of(string? avatarUrl, uint rgb, bool gradeable)
        => avatarUrl is { Length: > 0 } && gradeable ? new(avatarUrl, 0u) : new(null, Argb(rgb));
}
