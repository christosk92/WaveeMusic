using System;

namespace Wavee;

/// <summary>Where the home LANDING feed (the unfaceted document) currently stands, so <c>HomePage.ApplyFeed</c> knows
/// whether to keep the region skeletonized, publish a genuinely empty page, or paint the feed. Pure (System only) — see
/// <c>HomeFeedReadinessTests</c>.
///
/// <para>The rule: Home reveals ONCE, from the feed the session actually settles on. Before the live-catalog attempt has
/// concluded, EVERY read is provisional — including a read that already carries the resident library shelves. Publishing
/// those shelves while the session was still connecting is exactly what the user recorded as "why does Home have to
/// open like this": the page revealed the cached "Jump back in" + library sections, a lone "No charts right now" chrome
/// row painted under it, the notification timeline popped in above the fold once the session went live, and ~1.5 s
/// after launch the live feed landed and REPLACED everything — chips appeared, the hero band and the weekly pair pushed
/// the cached grid 350 px down, and every row remounted. One reveal from the settled feed is the whole fix; the
/// skeleton the user was already looking at simply stays up ~1 s longer.</para></summary>
public enum HomeFeedState
{
    /// <summary>The live-catalog attempt has not concluded — the feed on screen is provisional, whatever it holds
    /// (the pre-GoLive placeholder, the resident library shelves, or a resume still in flight). Keep the region
    /// Pending: the skeleton stays up and the read that lands once the attempt concludes replaces it.</summary>
    Placeholder,
    /// <summary>0 groups and the live-catalog attempt HAS concluded (successfully with nothing to show, or having
    /// failed/gone offline) — there is nothing more to wait for. Publish it: the page shows its real empty state.</summary>
    Empty,
    /// <summary>At least one group after the attempt concluded — the live document, or a returning, currently-offline
    /// user's cached library shelves (the attempt concluded Offline, so the shelves ARE the settled feed).</summary>
    Ready,
}

public static class HomeFeedReadiness
{
    /// <param name="groupCount">The unfaceted feed's <c>HomeFeed.Groups.Count</c>.</param>
    /// <param name="liveCatalogConcluded">Whether the live-catalog attempt has CONCLUDED — succeeded (Live), failed or
    /// gone offline (Offline), or was never going to run (no stored credential; the fake backend). The one state that
    /// is NOT concluded is Connecting: a silent resume in flight whose answer supersedes anything read before it. NOT
    /// "did this one fetch happen to get a live answer": that flavor can never tell "not tried yet" from "tried and
    /// failed" apart. See <c>HomePage.Render</c>'s <c>bridge.AuthState</c> read for where this is derived.</param>
    public static HomeFeedState Classify(int groupCount, bool liveCatalogConcluded)
        => !liveCatalogConcluded ? HomeFeedState.Placeholder
         : groupCount > 0 ? HomeFeedState.Ready
         : HomeFeedState.Empty;

    /// <summary>The hard fallback that keeps a withheld <see cref="HomeFeedState.Placeholder"/> from stranding the
    /// page on its skeleton forever: <c>HomePage</c>'s refresh effect re-reads on the feed epoch AND an
    /// <c>AuthState</c> flip, but nothing GUARANTEES either fires promptly — the session's opening post-login read
    /// deliberately publishes no epoch bump (<c>LiveHomeCache.GetAsync</c> in <c>SpotifyOnlineCatalog.cs</c>: "a bump
    /// nobody needed"), and a slow/stalled resume can leave <c>AuthState</c> at <c>Connecting</c> past any bound this
    /// page could otherwise reason about. Past <see cref="ForceReleaseMs"/> since mount, the page stops waiting for a
    /// better answer and force-publishes whatever it has (the resident shelves, typically).</summary>
    public const double ForceReleaseMs = 8000d;

    /// <summary>Pure elapsed-time gate for the hard fallback — see <see cref="ForceReleaseMs"/>.</summary>
    public static bool ShouldForceRelease(double elapsedMs) => elapsedMs >= ForceReleaseMs;

    /// <summary>How long the FIRST reveal waits, after the feed settled, for the chrome rows that ride their own
    /// resources (the Charts deck, the notification timeline) to conclude — so they paint WITH the reveal instead of
    /// popping into an already-revealed page. A cap, not a sleep: the reveal fires the instant they conclude, and this
    /// is only the bound for a slow chart read. Later publishes (the 60 s poll, an epoch bump) never wait.</summary>
    public const double ChromeSettleMs = 1500d;

    /// <summary>The reveal gate: the first paint may happen once the feed has settled AND the chrome rows have concluded
    /// (or <see cref="ChromeSettleMs"/> has elapsed since the feed settled, or the hard fallback forced it).</summary>
    public static bool MayReveal(bool feedSettled, bool chromeConcluded, double msSinceSettled, bool force = false)
        => feedSettled && (chromeConcluded || force || msSinceSettled >= ChromeSettleMs);

    /// <summary>The core epoch/placeholder transition <c>HomePage.ApplyFeed</c> runs for every UNFACETED read — pure
    /// and directly testable, extracted so the bookkeeping invariant a withheld <see cref="HomeFeedState.Placeholder"/>
    /// depends on can be pinned outside a full component render: a withheld read returns
    /// <c>AppliedEpoch == appliedEpoch</c> (UNCHANGED) rather than <paramref name="epoch"/>, which is what lets a
    /// LATER read at the SAME epoch — e.g. the AuthState-flip re-read, or the ordinary 60 s poll tick — still publish
    /// instead of being mistaken for stale. <paramref name="force"/> is the 8 s hard fallback's escape hatch: it
    /// publishes a Placeholder as-is rather than withholding it, but the epoch gate still applies, so a forced publish
    /// can never regress behind a real answer that already landed.</summary>
    /// <param name="appliedEpoch">The page's current applied epoch (-1 before any read has published).</param>
    /// <param name="epoch">This read's feed epoch.</param>
    public static (bool Publish, int AppliedEpoch) ApplyEpoch(
        int appliedEpoch, int epoch, int groupCount, bool liveCatalogConcluded, bool force = false)
    {
        if (epoch < appliedEpoch) return (false, appliedEpoch);
        if (!force && Classify(groupCount, liveCatalogConcluded) == HomeFeedState.Placeholder) return (false, appliedEpoch);
        return (true, epoch);
    }
}

/// <summary>What <see cref="HomeRevealGate{TFeed}.Offer"/> decided about a read.</summary>
public enum HomeRevealVerdict : byte
{
    /// <summary>Withheld — stale epoch, or a provisional read the page keeps skeletonized.</summary>
    Withheld,
    /// <summary>The feed settled but the chrome rows have not concluded yet: held for the first reveal. The page
    /// publishes it from <see cref="HomeRevealGate{TFeed}.Tick"/> once they do (or the cap elapses).</summary>
    Held,
    /// <summary>The FIRST reveal: publish now — the shimmer cross-dissolves to this feed, once.</summary>
    Reveal,
    /// <summary>The page is already revealed: publish now as an in-place Ready→Ready swap — never a second reveal,
    /// never a skeleton.</summary>
    Swap,
}

/// <summary>The Home page's reveal state machine, engine-free so the whole launch sequence can be driven in a test:
/// which read settles the page, when the first reveal may fire, and that nothing after it ever reveals again. One
/// instance per mounted page (two tabs each track what THEY consumed). <c>HomePage</c> is the one caller; it owns the
/// <c>Loadable</c> and calls <c>SetReady</c> exactly when this says so.</summary>
public sealed class HomeRevealGate<TFeed> where TFeed : class
{
    /// <summary>The feed epoch this page's rendered (or held) feed was read at. -1 until the first read lands, so a
    /// fresh mount never skips one.</summary>
    public int AppliedEpoch { get; private set; } = -1;

    /// <summary>The page has painted a real branch (feed, empty state or error) — from here on every publish is a swap.</summary>
    public bool Revealed { get; private set; }

    // The most recent UNFACETED read SEEN, whether or not applied — i.e. also a read withheld as Placeholder. This is what
    // the 8 s hard fallback force-publishes: the best answer on hand beats an indefinite skeleton.
    int _lastSeenEpoch = -1;
    TFeed? _lastSeenFeed;

    // The settled feed waiting on the chrome rows for the first reveal, and when it settled (the cap's origin).
    TFeed? _held;
    double _heldAtMs;

    /// <summary>A settled feed is waiting for the chrome rows.</summary>
    public bool IsHolding => _held is not null;

    /// <summary>A read landed. <paramref name="alreadyResolved"/> is the loadable's own view (it left Pending by another
    /// path — an initial failure painted the error state, a facet tap published directly): a page that has painted
    /// anything real is revealed, whatever this gate saw. <paramref name="chromeConcluded"/> and <paramref name="nowMs"/>
    /// feed the reveal check the same way <see cref="Tick"/> does, so a read that lands after the chrome concluded
    /// reveals in the same call rather than waiting for a tick.</summary>
    public HomeRevealVerdict Offer(int epoch, TFeed feed, int groupCount, bool faceted, bool liveCatalogConcluded,
        bool force, bool alreadyResolved, bool chromeConcluded, double nowMs)
    {
        if (alreadyResolved) Revealed = true;
        if (epoch < AppliedEpoch) return HomeRevealVerdict.Withheld;
        if (!faceted && epoch >= _lastSeenEpoch) { _lastSeenEpoch = epoch; _lastSeenFeed = feed; }
        // A faceted read always passes: it is the server's own ordered document (VirtualFacet renders whatever it says,
        // including genuinely empty), so Classify never applies to it — and a facet can only be tapped on a revealed page.
        var (publish, applied) = HomeFeedReadiness.ApplyEpoch(AppliedEpoch, epoch, groupCount, liveCatalogConcluded, force || faceted);
        AppliedEpoch = applied;
        if (!publish) return HomeRevealVerdict.Withheld;
        if (Revealed) { _held = null; return HomeRevealVerdict.Swap; }
        _held = feed;
        _heldAtMs = nowMs;
        return Tick(chromeConcluded, nowMs, force) is null ? HomeRevealVerdict.Held : HomeRevealVerdict.Reveal;
    }

    /// <summary>The chrome moved, or the cap timer fired: the held feed to publish as the first reveal, or null if
    /// there is nothing held or the gate still says wait. Idempotent — a second tick after the reveal returns null.</summary>
    public TFeed? Tick(bool chromeConcluded, double nowMs, bool force = false)
    {
        if (Revealed || _held is null) return null;
        if (!HomeFeedReadiness.MayReveal(feedSettled: true, chromeConcluded, nowMs - _heldAtMs, force)) return null;
        var feed = _held;
        _held = null;
        Revealed = true;
        return feed;
    }

    /// <summary>The 8 s hard fallback's input: the best unfaceted answer on hand (a held feed first, then the last read
    /// seen even if it was withheld) and the epoch to offer it at. Null feed ⇒ nothing has landed at all yet.</summary>
    public (int Epoch, TFeed? Feed) ForceRelease()
        => (Math.Max(_lastSeenEpoch, AppliedEpoch), _held ?? _lastSeenFeed);
}
