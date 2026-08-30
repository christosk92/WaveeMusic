using System;
using Wavee.Core;

namespace Wavee.Backend.Hydration;

// ── Blocking-vs-background, and freshness, in ONE place (design §2.1) ────────────────────────────────────────────────
// Before this, "how much do we wait for before painting?" was decided ad hoc at ~14 call sites, and "is it still
// fresh?" lived in MetadataService's Resource plus six per-service negative memos. Both are policy, not mechanism, so
// both are pure tables here — the ladders read them and never re-decide.

/// <summary>What opening a surface of one kind should ask for. <see cref="Blocking"/> is awaited before the page
/// paints its primary content; <see cref="Background"/> is enqueued on the pump and repaints in place when it lands.
/// <see cref="HydrationLevel.None"/> in either slot means "don't ask".</summary>
/// <param name="Revalidate">Ask the transport even when the ledger says fresh — the "we already have a baseline, but
/// something may have changed while we were away" open.</param>
public readonly record struct OpenPlan(HydrationLevel Blocking, HydrationLevel Background, bool Revalidate = false);

/// <summary>THE kind → (blocking, background) table for a page open.</summary>
public static class OpenPolicy
{
    /// <param name="hasBaseline">Only meaningful for a playlist: whether a membership baseline is already resident.
    /// With one, the open is a revalidation in the background (LibrarySync's own 5-minute/dirty gates decide whether
    /// it actually fetches); without one there is nothing to paint, so Open is blocking.</param>
    public static OpenPlan For(EntityKind kind, bool hasBaseline = false) => kind switch
    {
        // Album: await Rich, so the ©/℗ line and the Plays star are there at FIRST paint (and in the same POST as the
        // V4) rather than popping in. Full is the getAlbum envelope — asked only by the below-the-fold surface.
        EntityKind.Album => new OpenPlan(HydrationLevel.Rich, HydrationLevel.None),

        // Artist: Open is the assembled discography the library pane needs. Rich (the overview) costs a second
        // transport, so only the standalone artist page asks for it — explicitly, not on every open.
        EntityKind.Artist => new OpenPlan(HydrationLevel.Open, HydrationLevel.None),

        EntityKind.Playlist => hasBaseline
            ? new OpenPlan(HydrationLevel.None, HydrationLevel.Open, Revalidate: true)
            : new OpenPlan(HydrationLevel.Open, HydrationLevel.None),

        // Show: the header + the first page of episodes is the primary content; the remaining pages page on the pump.
        EntityKind.Show => new OpenPlan(HydrationLevel.Open, HydrationLevel.Full),

        // A playable's own surface (now playing, an expanded row) needs Open and nothing more; Full is only the
        // availability verdict, which no open blocks on.
        EntityKind.Track or EntityKind.Episode => new OpenPlan(HydrationLevel.Open, HydrationLevel.None),

        // A collection paints from the saved-set plane immediately; its members hydrate underneath it.
        EntityKind.Collection => new OpenPlan(HydrationLevel.None, HydrationLevel.Open),

        // A profile is one name + avatar — one batched resolve, worth awaiting.
        EntityKind.User => new OpenPlan(HydrationLevel.Identity, HydrationLevel.None),

        // Prerelease/Concert/Unknown have no ladder: their return-only services own them.
        _ => new OpenPlan(HydrationLevel.None, HydrationLevel.None),
    };
}

/// <summary>The freshness (AGE) half of the policy — what the ledger seals an outcome for. Presence is
/// <see cref="HydrationLevels"/>; this is the only clock. One record so a test can shorten every TTL at once.</summary>
public sealed record HydrationPolicy
{
    /// <summary>Identity/Open for every kind — the old MetadataService <c>FreshnessPolicy.Etag</c> window.</summary>
    public TimeSpan IdentityTtl { get; init; } = TimeSpan.FromHours(1);
    public TimeSpan OpenTtl { get; init; } = TimeSpan.FromHours(1);
    /// <summary>The artist overview + chart: expensive, and it moves on the order of a day.</summary>
    public TimeSpan ArtistRichTtl { get; init; } = TimeSpan.FromHours(12);
    /// <summary>getAlbum — short, because it backs a below-the-fold panel the user can scroll to repeatedly.</summary>
    public TimeSpan AlbumFullTtl { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How long a ladder that RAN and did not get there stays sealed, for a playable. This is what replaces
    /// the now-playing heartbeat gate: a thin track resolves getTrack ONCE, not once per cluster update.</summary>
    public TimeSpan ExhaustedPlayableTtl { get; init; } = TimeSpan.FromMinutes(10);
    /// <summary>The same seal for an album that has no ©/℗ to give — a full day, because the answer is "this release
    /// simply carries no publishing facet", which does not change.</summary>
    public TimeSpan ExhaustedAlbumRichTtl { get; init; } = TimeSpan.FromHours(24);

    public static readonly HydrationPolicy Default = new();

    /// <summary>How long to seal (uri, level) given how the attempt ended. <paramref name="ok"/> false = Exhausted.</summary>
    /// <param name="transient">The run reported a swallowed transport failure for this uri
    /// (<see cref="HydrationRunScope"/>). "We could not ask properly" is NOT "there is nothing to get", so it never
    /// earns the long genuinely-absent window — a 24-hour album Rich seal off one 503 is a day without ©/℗ and without
    /// the row bundle. It still seals for the short window, because hammering a failing transport on every heartbeat is
    /// the bug the exhausted seal was introduced to kill.</param>
    public TimeSpan Ttl(EntityKind kind, HydrationLevel level, bool ok, bool transient = false)
    {
        if (!ok)
            return !transient && kind == EntityKind.Album && level >= HydrationLevel.Rich
                ? ExhaustedAlbumRichTtl : ExhaustedPlayableTtl;
        if (kind == EntityKind.Artist && level >= HydrationLevel.Rich) return ArtistRichTtl;
        if (kind == EntityKind.Album && level >= HydrationLevel.Full) return AlbumFullTtl;
        return level <= HydrationLevel.Identity ? IdentityTtl : OpenTtl;
    }
}
