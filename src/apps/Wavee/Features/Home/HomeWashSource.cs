using System;
using FluentGpu.Foundation;
using Wavee.Core;
using Wavee.SpotifyLive;

namespace Wavee;

/// <summary>The three cards Home's shell wash is derived from, one per wash slot. Selection is by SEMANTIC KIND AND
/// ORDINAL only — never by a title, a subtitle or any other localized string — so a server that renames a section, or
/// an account in another language, resolves to exactly the same three cards.</summary>
internal readonly record struct HomeWashCards(HomeCard? Hero, HomeCard? Weekly, HomeCard? Mix);

/// <summary>One resolved wash leg: the FULL-ALPHA base colour plus the stable artwork identity it was resolved from.
/// <para>Alpha is deliberately 1: <c>ShellMaterialLayer</c> owns wash strength (it re-stamps
/// <c>ShellWashGeometry.HeroAlpha/ShelfAlpha</c> onto both gradient stops per theme), so a colour carrying its own
/// alpha here would be silently discarded at the origin stop and would fight the theme table.</para>
/// <para><see cref="Key"/> is the wash's IDENTITY, not decoration — <c>ShellMaterialLayer</c> keys its layer node on it,
/// so a new key remounts (and therefore cross-fades) the layer while a colour change under the same key snaps.</para></summary>
internal readonly record struct HomeWashPick(ColorF Color, string Key);

/// <summary>Home's three resolved wash legs, in stacking order. Any leg may be null — a slot with no card, or a card
/// with neither a payload accent nor a landed grading, contributes NO layer rather than a made-up colour.</summary>
internal readonly record struct HomeWashPicks(HomeWashPick? Hero, HomeWashPick? Weekly, HomeWashPick? Mix);

/// <summary>
/// The PURE selector behind Home's shell wash: <c>HomeFeed</c> + a plane lookup → three optional colour legs.
///
/// Two rules it exists to hold:
///   • SELECTION is structural. Hero ← the first card of the first <see cref="HomeGroupKind.Hero"/> group, Weekly ← the
///     first card of the first <see cref="HomeGroupKind.WeeklyPair"/> group, Mix ← the first card of the first
///     <see cref="HomeGroupKind.MixBand"/> group. No copy is ever inspected.
///   • RESOLUTION is payload-accent-first and never invents. <c>HomeCardMeta.Accent</c> (the server's
///     <c>extractedColors.colorDark</c>) is available before a single image byte lands, so a cold feed is already in its
///     own colours; the graded <c>CoverColorPlane</c> scheme is the fallback; and a card with neither yields a NULL leg.
///     There is deliberately no app-accent fallback — a default-blue wash under the whole shell is a lie about the
///     content, and the deterministic ground alone is the honest answer.
///
/// The plane is injected as a delegate rather than read from <c>CoverColorPlane.Current</c> so this stays a pure
/// function of its arguments (and so the tests can drive both tiers without a plane, a theme or a window).
/// </summary>
internal static class HomeWashSource
{
    /// <summary>The three source cards, by kind + ordinal. Null feed / missing kind ⇒ a null slot.</summary>
    internal static HomeWashCards Sources(HomeFeed? feed) => new(
        FirstCard(feed, HomeGroupKind.Hero),
        FirstCard(feed, HomeGroupKind.WeeklyPair),
        FirstCard(feed, HomeGroupKind.MixBand));

    /// <summary>Select AND resolve in one call (the shape the tests drive).</summary>
    internal static HomeWashPicks Select(HomeFeed? feed, Func<string?, CoverColorPlane.Scheme?> schemeFor)
        => Select(Sources(feed), schemeFor);

    /// <summary>Resolve already-selected cards. The caller selects first when it needs the cards themselves — HomePage
    /// does, so it can watch exactly the (≤3) artworks whose colour is still pending.</summary>
    internal static HomeWashPicks Select(in HomeWashCards cards, Func<string?, CoverColorPlane.Scheme?> schemeFor)
        => new(Pick(cards.Hero, schemeFor), Pick(cards.Weekly, schemeFor), Pick(cards.Mix, schemeFor));

    /// <summary>One card → its wash leg, or null when the card has no honest colour yet.</summary>
    internal static HomeWashPick? Pick(HomeCard? card, Func<string?, CoverColorPlane.Scheme?> schemeFor)
    {
        if (card is null) return null;
        // Tier 1: the payload accent, lifted exactly as HomePage.GroupAccent lifts it — colorDark is a near-black tone
        // that would vanish into the dark ground and bruise the light one.
        if (card.Meta is { Accent: not 0u } meta)
            return new HomeWashPick(WaveePalette.Lift(WaveePalette.ToColor(meta.Accent)) with { A = 1f }, KeyOf(card));
        // Tier 2: the graded cover, through the SAME chrome derivation every accent-filled surface uses. A card with no
        // artwork is not asked: it has no cover to grade, and a plane keyed on "" answers for the wrong thing.
        if (card.Image?.Url is { Length: > 0 } url && schemeFor(url) is { } scheme)
            return new HomeWashPick(WaveePalette.ChromeAccent(scheme) with { A = 1f }, KeyOf(card));
        return null;   // tier 3 does not exist on purpose — see the type doc
    }

    /// <summary>The artwork this card's colour is still WAITING on, or null when it needs no watch: a card carrying a
    /// payload accent is already resolved, and a card with no image can never be graded. HomePage watches exactly these
    /// (at most three) so a landed grading re-publishes the wash without coupling Home to the plane's global epoch —
    /// which every scrolling grid batch bumps.</summary>
    internal static string? PlaneUrl(HomeCard? card)
        => card is { Meta: null or { Accent: 0u } } c && c.Image?.Url is { Length: > 0 } url ? url : null;

    /// <summary>The leg's identity: the size-independent artwork key (so the 64px grid cover and the 640px hero are ONE
    /// wash), falling back to the card's uri when it has no artwork at all — two accent-only cards must still be two
    /// distinct layers, or the shell would snap between them instead of cross-fading.</summary>
    internal static string KeyOf(HomeCard card)
    {
        string key = CoverColorPlane.KeyForUrl(card.Image?.Url);
        return key.Length > 0 ? key : card.Uri;
    }

    /// <summary>A value fingerprint over the three legs — what the publishing effect keys on, so the shell material is
    /// written on a real colour/artwork change rather than once per render.</summary>
    internal static int Fingerprint(in HomeWashPicks picks)
        => HashCode.Combine(Leg(picks.Hero), Leg(picks.Weekly), Leg(picks.Mix));

    static int Leg(HomeWashPick? pick)
        => pick is { } p ? HashCode.Combine(p.Key, p.Color.R, p.Color.G, p.Color.B) : 0;

    static HomeCard? FirstCard(HomeFeed? feed, HomeGroupKind kind)
    {
        if (feed is null) return null;
        var groups = feed.Groups;
        for (int i = 0; i < groups.Count; i++)
            if (groups[i].Kind == kind && groups[i].Cards.Count > 0)
                return groups[i].Cards[0];
        return null;
    }
}
