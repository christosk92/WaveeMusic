using System;

namespace Wavee.Core;

/// <summary>The real-world material a simulated event dresses itself in — a real album, artist or show from the user's
/// own library, so the resulting row is clickable and reads like the genuine article. <see cref="Uri"/> empty means the
/// caller found nothing real and the builder should fall back to a clearly-marked placeholder.</summary>
public readonly record struct SimSeed(string Uri, string Name, string? ImageUrl, string? CreatorName)
{
    // Null-safe on purpose: this is a record STRUCT, so `default(SimSeed)` — the "library is still empty" case, which is
    // every launch before the collections warm — leaves Uri and Name null, not "".
    public bool HasReal => !string.IsNullOrEmpty(Uri) && !string.IsNullOrEmpty(Name);
}

/// <summary>
/// Builders for SIMULATED notification-centre rows (the Settings → Notifications "Send event" affordance). Pure and
/// engine-free so the three invariants below are unit-testable — each of them fails SILENTLY in the real pipeline, which
/// is exactly the class of bug a test affordance must not have.
/// </summary>
/// <remarks>
/// <b>1. Unread depends on beating a watermark.</b> <c>NotificationMerge.Build</c> gates unread on
/// <c>Timestamp &gt; lastSeenMs</c> — STRICTLY greater — and both feed watermarks are stamped to "now" whenever the panel
/// is opened or Mark-all-read is pressed. A simulated event built with a plain <c>UtcNow</c> immediately after opening the
/// panel would therefore arrive already-read and never escalate. <see cref="NextTimestamp"/> is the fix.
///
/// <b>2. Ids must be unique per event.</b> The merge does not dedup, the panel keys rows on <c>"ntf:" + Id</c>, and the
/// live toast tag is <c>"live:" + Id</c> — so a reused id makes Windows REPLACE the previous banner instead of raising a
/// second one, which looks like "the second press did nothing".
///
/// <b>3. Topic is DERIVED from content, not declared.</b> A social row is classified as a concert only when
/// <see cref="SpotifyUpdates.IsConcert"/> says so (wire type or a concert action target); otherwise it is a follower. An
/// episode needs <see cref="NewReleaseKind.Episode"/>. Get either wrong and the event tests the wrong dial.
/// </remarks>
public static class SimulatedNotifications
{
    /// <summary>Marks every simulated id, so a row can be recognised as simulated anywhere downstream.</summary>
    public const string IdPrefix = "sim:";

    /// <summary>The wire type that makes <see cref="SpotifyUpdates.IsConcert"/> true — see invariant 3. Shaped like the
    /// real feed's discriminator (it matches on "CONCERT"/"LIVE").</summary>
    public const string ConcertWireType = "CONCERT_ANNOUNCEMENT";

    /// <summary>A timestamp that is guaranteed to read as unread: later than "now" AND later than both feed watermarks.
    /// Same-millisecond case included — a press in the same tick as a panel-open still counts.</summary>
    public static long NextTimestamp(long nowUnixMs, long ganderSeenMs, long whatsNewSeenMs)
    {
        long floor = Math.Max(ganderSeenMs, whatsNewSeenMs);
        return floor >= nowUnixMs ? floor + 1 : nowUnixMs;
    }

    /// <summary>A followed artist released something. <paramref name="episode"/> selects the
    /// <see cref="NotifyTopic"/>-deciding kind: podcast episodes are a different dial from albums.</summary>
    public static NewReleaseNotification NewRelease(in SimSeed seed, bool episode, long timestampMs, long seq)
    {
        string uri = seed.HasReal ? seed.Uri
            : episode ? "spotify:episode:simulated" : "spotify:album:simulated";
        string name = seed.HasReal ? seed.Name : (episode ? "A simulated episode" : "A simulated release");
        string creator = seed.CreatorName is { Length: > 0 } c ? c : "A followed artist";
        return new NewReleaseNotification(
            Id: IdPrefix + (episode ? "episode:" : "album:") + seq.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Timestamp: timestampMs,
            IsUnread: true,
            Kind: episode ? NewReleaseKind.Episode : NewReleaseKind.Album,
            Uri: uri,
            Name: name,
            ImageUrl: seed.ImageUrl,
            CreatorName: creator,
            AlbumType: episode ? null : "album",
            Played: false);
    }

    /// <summary>A live show announced near the user. Carries <see cref="ConcertWireType"/> so it classifies as the
    /// Concerts topic rather than Followers (invariant 3) — the title alone would NOT do it, and must not: the real
    /// feed's titles are server-localized prose.</summary>
    public static SocialNotification Concert(in SimSeed seed, long timestampMs, long seq)
    {
        string act = seed.HasReal ? seed.Name : "A followed artist";
        return new SocialNotification(
            Id: IdPrefix + "concert:" + seq.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Timestamp: timestampMs,
            IsUnread: true,
            Title: "New " + act + " show just announced near you. Save the date!",
            // Navigating to the artist is the closest real destination we can offer; the feed's own concert rows open a
            // concert page we cannot fabricate. A seedless build has nowhere to go, so it carries no action.
            ActionUri: seed.HasReal ? seed.Uri : null,
            ActionType: SocialActionType.Navigate,
            ImageUrl: seed.ImageUrl,
            UserNames: [act],
            StorageId: null,
            WireType: ConcertWireType);
    }

    /// <summary>Someone started following the user. Deliberately carries NO concert markers so it lands on the Followers
    /// dial. There is no real local source for "a follower", so this one is always a marked placeholder.</summary>
    public static SocialNotification Follower(long timestampMs, long seq)
        => new(
            Id: IdPrefix + "follower:" + seq.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Timestamp: timestampMs,
            IsUnread: true,
            Title: "A simulated listener started following you",
            ActionUri: null,
            ActionType: SocialActionType.Navigate,
            ImageUrl: null,
            UserNames: ["A simulated listener"],
            StorageId: null,
            WireType: null);

    /// <summary>An available app update. <see cref="AppUpdateState.Available"/> specifically: the Failed state renders no
    /// toast title and would silently never banner, which would read as a broken test.
    /// <para>Note this row's id is fixed by <see cref="AppUpdateNotification"/> itself ("update"), so repeated sends
    /// REPLACE the banner rather than stacking — unavoidable, and correct for a state-driven notification.</para></summary>
    public static AppUpdateNotification AppUpdate(string? version, long timestampMs)
        => new(timestampMs, IsUnread: true, AppUpdateSnapshot.Idle with
        {
            State = AppUpdateState.Available,
            TargetQuad = version is { Length: > 0 } v ? v : "0.0.0.0",
            TargetSemVer = version is { Length: > 0 } s ? AppUpdateVersion.ReleaseTagVersion(s) : "0.0.0",
            LastCheckedMs = timestampMs,
        });

    /// <summary>The target uri a simulated LIBRARY-ACTIVITY entry records against. Deliberately unresolvable: the undo
    /// path for a real save calls <c>SetSaved(uri, false)</c>, and a simulated entry must never be able to unsave
    /// something the user actually owns. Paired with a non-invertible kind, undo is not even offered.</summary>
    public static string ActivityTargetUri(long seq)
        => "wavee:simulated:activity:" + seq.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
