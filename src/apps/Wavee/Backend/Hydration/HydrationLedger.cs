using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wavee.Backend.Spotify;
using Wavee.Core;

namespace Wavee.Backend.Hydration;

// ── THE session freshness authority for the façade (design §2.1) ─────────────────────────────────────────────────────
// Before this there were two half-answers to "have we already asked for this?": MetadataService's per-uri Resource
// (which knew nothing about LEVELS, so a Rich ask and an Identity ask were the same key) and six per-service negative
// memos (each with its own cap, its own lifetime and its own idea of what a miss meant). Both die here.
//
// The key is (locale, uri, level) because all three change the answer: a locale flip invalidates every catalogue fact
// (same reason MetadataService keyed on it), and a uri sealed at Identity says nothing about whether Open was tried.
//
// Two things this seals, and the difference is the whole point:
//   • REACHED — the level is resident and we asked recently. Skip.
//   • EXHAUSTED (Partial) — the ladder RAN and could not get there. Also skip, for a SHORTER TTL. This is what replaces
//     the now-playing heartbeat gate: a thin track resolves getTrack once, not once per cluster update.
// A transport FAILURE seals nothing — the next ask retries.

/// <summary>One sealed rung. Locale is part of the identity because every catalogue fact is localized.</summary>
readonly record struct HydrationKey(string Locale, string Uri, HydrationLevel Level);

sealed class HydrationLedger
{
    /// <summary>The rungs a seal can cover, ascending. <see cref="HydrationLevel.None"/> is not a rung — nothing is
    /// ever asked for or sealed at it.</summary>
    static readonly HydrationLevel[] Rungs =
        [HydrationLevel.Identity, HydrationLevel.Open, HydrationLevel.Rich, HydrationLevel.Full];

    // Seals live in the Resource engine so MarkStale (Invalidate), the SWR staleness rule and the per-entry expiry are
    // the ONE implementation the rest of the app already uses.
    //
    // The policy window is TimeSpan.MaxValue and every seal carries an EXPLICIT ExpiresAt instead: the TTL depends on
    // (kind, level, outcome), and Resource's value-only `ttlOf` hook cannot see the key. Disabling the policy-level
    // window and stamping the deadline per seal is the only way to give an Artist Rich 12 h and an exhausted playable
    // 10 minutes out of one cache. MarkStale still works — it rides NeedsRevalidate, which is independent of both.
    readonly Resource<HydrationKey, HydrationOutcome> _res;

    // In-flight dedupe per (uri, level). Deliberately NOT Resource's own in-flight slot: that one is keyed to a fetch
    // delegate fixed at construction, and a ladder run is a different closure every time (and usually a BATCH shared by
    // many keys — see SpotifyProviderHydrator).
    readonly ConcurrentDictionary<HydrationKey, Task<HydrationOutcome>> _inFlight = new();

    readonly Func<SessionContext> _ctx;
    readonly HydrationPolicy _policy;

    public HydrationLedger(Func<SessionContext> ctx, HydrationPolicy policy, WaveeLogger log = default)
    {
        _ctx = ctx;
        _policy = policy;
        _res = new Resource<HydrationKey, HydrationOutcome>(
            // Unreachable by construction: the ledger never calls Use/GetAsync/Revalidate, because a seal is always
            // WRITTEN by whoever ran the ladder. Throwing rather than returning a fake outcome keeps a future misuse
            // loud instead of silently sealing "Reached, level None".
            (key, _) => throw new InvalidOperationException(
                "the hydration ledger is seal-only; " + key.Uri + " must be run through the provider hydrator"),
            new FreshnessPolicy.Etag(TimeSpan.MaxValue),
            ctx,
            // Unbounded ON PURPOSE, like the MetadataService Resource it replaces: entries are session-scoped, tiny,
            // and only minted for uris something actually asked for. Resource's eviction is an O(n) scan per victim,
            // so a cap sized below a 10k playlist would thrash far harder than the memory it saves.
            name: "hydration-ledger",
            debugLog: log);
    }

    string Locale => SpotifyHeaders.NormalizeLanguage(_ctx().Locale);

    /// <summary>Is this rung sealed AND still within its TTL? True for an EXHAUSTED seal too — "we asked, this is the
    /// answer for now" is exactly what suppresses the retry. <paramref name="outcome"/> is what was sealed.</summary>
    public bool TryPeek(in EntityUri u, HydrationLevel level, out HydrationOutcome outcome)
    {
        var peek = _res.Peek(new HydrationKey(Locale, u.Uri, level));
        outcome = peek.Value!;
        return peek.IsReady && !peek.IsStale;
    }

    /// <summary>The narrow question the design names: is this rung sealed as REACHED and still fresh?</summary>
    public bool IsFresh(in EntityUri u, HydrationLevel level)
        => TryPeek(u, level, out var o) && o.Status == HydrationStatus.Reached;

    /// <summary>Seal the outcome of one ladder run. Every rung up to <paramref name="reachedUpTo"/> is sealed, each
    /// with ITS OWN verdict: a rung at or below <c>outcome.Reached</c> seals Reached (ok TTL), a rung above it seals
    /// Partial (the shorter exhausted TTL). Callers pass the HIGHER of "what we reached" and "what was asked for", so
    /// a run that fell short still seals the ask it could not satisfy.
    /// <para>A <see cref="HydrationStatus.Failed"/>/<see cref="HydrationStatus.Cancelled"/>/<see
    /// cref="HydrationStatus.Unsupported"/> outcome seals NOTHING: a transport error is not an answer, and an
    /// unsupported kind never reaches the ledger at all.</para>
    /// <para>A PLAYLIST is sealed at Identity only (see <see cref="SealsLevel"/>).</para></summary>
    /// <param name="transient">The run reported a swallowed transport failure for this uri
    /// (<see cref="HydrationRunScope"/>). An exhausted rung then seals on the SHORT window instead of the
    /// "this facet genuinely does not exist" one — a 503 must not cost an album its ©/℗ for a day.</param>
    public void Seal(in EntityUri u, HydrationLevel reachedUpTo, HydrationOutcome outcome, bool transient = false)
    {
        if (outcome.Status is not (HydrationStatus.Reached or HydrationStatus.Partial)) return;
        string locale = Locale;
        var now = DateTime.UtcNow;
        for (int i = 0; i < Rungs.Length; i++)
        {
            var rung = Rungs[i];
            if (rung > reachedUpTo) break;
            if (!SealsLevel(u.Kind, rung)) continue;
            bool ok = rung <= outcome.Reached;
            var sealedOutcome = ok
                ? new HydrationOutcome(rung, HydrationStatus.Reached)
                : new HydrationOutcome(outcome.Reached, HydrationStatus.Partial, outcome.Error);
            _res.Seed(new HydrationKey(locale, u.Uri, rung), sealedOutcome,
                now, now + _policy.Ttl(u.Kind, rung, ok, transient), needsRevalidate: false);
        }
    }

    /// <summary>May this (kind, rung) be TTL-sealed at all? Everything except a PLAYLIST at Open-or-above, which the
    /// design pins explicitly (design §2.1, plan §4 risk 2): the playlist plane's freshness authority is the LibrarySync
    /// writer loop — its in-flight map, its 5-minute window and its dirty set — and a ledger seal sitting on top of it
    /// is a SECOND, disagreeing gate. Two ways it bit: a first open that FAILED sealed Open Exhausted for 10 minutes, so
    /// re-navigating to the playlist did not retry and the page stayed empty; and a successful open sealed Open Reached
    /// for an hour, so `opener.Revalidate` — the call that lets the loop act on a dealer-marked dirty list — was never
    /// made again by a non-revalidating caller (the play path). The ledger still dedupes CONCURRENT playlist callers:
    /// that is <see cref="RunOnce"/>, which is independent of sealing. Re-running the ladder costs no network either —
    /// step 0's 205 rides <c>ExtensionEtagCache</c>, which has its own per-entry TTL.
    /// <para>Identity IS sealed: a playlist header (205 / the rootlist header GET) is a catalogue fact like any other,
    /// and it is what keeps the cold-start rootlist prefetch from re-asking.</para></summary>
    static bool SealsLevel(EntityKind kind, HydrationLevel rung)
        => kind != EntityKind.Playlist || rung < HydrationLevel.Open;

    /// <summary>A known-better outcome arrived out of band (a dealer push, a video canonical recovery): unseal EVERY
    /// rung so the next ask really re-fetches. The escape hatch for an exhausted seal (plan §4 risk 8).</summary>
    public void Invalidate(string uri)
    {
        if (string.IsNullOrEmpty(uri)) return;
        string locale = Locale;
        for (int i = 0; i < Rungs.Length; i++)
            _res.MarkStale(new HydrationKey(locale, uri, Rungs[i]));
    }

    /// <summary>CLAIM the (uri, level) slot for every uri in <paramref name="work"/> that no one else is already
    /// running, and hand back both halves: the subset this caller now owns and must fetch, and the tasks to JOIN for the
    /// rest. Nothing runs yet — that is the point of the name.
    ///
    /// <para>The predecessor published a slot per uri and then let the FIRST claimant run a batch over its OWN whole
    /// list, which was wrong twice over. Two callers with partially overlapping sets (a page open and a prefetch wave
    /// sharing three of ten uris) each ran a batch covering the overlap, so the shared uris were fetched twice — the
    /// exact double-fetch the ledger exists to prevent. And the shared batch inherited the FIRST caller's cancellation
    /// token, so a user who navigated away killed the fetch every joiner was still waiting on. Claiming first fixes
    /// both: the batch covers exactly the claimed subset, and the caller who happened to win the race contributes no
    /// lifetime — the provider runs it on the session/pump token and every caller waits with its own.</para>
    ///
    /// <para>Like <c>Resource.Revalidate</c>, the slot is published BEFORE the work starts: a run that completes
    /// synchronously (everything warm) must not remove a key that has not been inserted yet.</para></summary>
    public HydrationClaims Claim(IReadOnlyList<EntityUri> work, HydrationLevel level)
    {
        string locale = Locale;
        var claims = new HydrationClaims(this, level);
        for (int i = 0; i < work.Count; i++)
        {
            var key = new HydrationKey(locale, work[i].Uri, level);
            while (true)
            {
                if (_inFlight.TryGetValue(key, out var joined)) { claims.AddJoin(joined); break; }
                var slot = new TaskCompletionSource<HydrationOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!_inFlight.TryAdd(key, slot.Task)) continue;   // lost the race — loop and join the winner
                claims.AddClaim(work[i], key, slot);
                break;
            }
        }
        return claims;
    }

    /// <summary>Release a claimed slot. Called by <see cref="HydrationClaims"/> as each result is published; the
    /// key/task pair overload is what makes it safe against a later run that has already re-claimed the same key.</summary>
    internal void Release(HydrationKey key, Task<HydrationOutcome> slot)
        => _inFlight.TryRemove(new KeyValuePair<HydrationKey, Task<HydrationOutcome>>(key, slot));

    /// <summary>Diagnostic: how many runs are in flight right now (the dedupe assertion hook).</summary>
    public int InFlight => _inFlight.Count;
}

/// <summary>One caller's share of a ladder pass: the uris it CLAIMED (nobody else is fetching them, so it must) and the
/// tasks it JOINED (someone else already is). Owned by whoever runs the batch — completing or disposing it is what
/// releases the ledger's in-flight slots, so an abandoned claim can never wedge a uri for the session.</summary>
sealed class HydrationClaims : IDisposable
{
    readonly HydrationLedger _ledger;
    readonly HydrationLevel _level;
    readonly List<(EntityUri Uri, HydrationKey Key, TaskCompletionSource<HydrationOutcome> Slot)> _claimed = new();
    readonly List<EntityUri> _claimedUris = new();
    readonly List<Task<HydrationOutcome>> _waits = new();
    bool _settled;

    internal HydrationClaims(HydrationLedger ledger, HydrationLevel level) { _ledger = ledger; _level = level; }

    internal void AddClaim(in EntityUri uri, in HydrationKey key, TaskCompletionSource<HydrationOutcome> slot)
    {
        _claimed.Add((uri, key, slot));
        _claimedUris.Add(uri);
        _waits.Add(slot.Task);
    }

    internal void AddJoin(Task<HydrationOutcome> joined) => _waits.Add(joined);

    /// <summary>The uris this caller owns — and therefore EXACTLY what its batch must cover.</summary>
    public List<EntityUri> ClaimedUris => _claimedUris;
    public int ClaimedCount => _claimed.Count;
    /// <summary>Every task this caller has to see through: its own slots plus the runs it joined.</summary>
    public IReadOnlyList<Task<HydrationOutcome>> Waits => _waits;

    /// <summary>The pass finished: seal each claimed uri at what it actually reached and answer its slot (and every
    /// joiner's). <paramref name="wasTransient"/> is the run's failure channel — a uri that reported one seals on the
    /// short window rather than the "genuinely absent" one.</summary>
    public void Publish(Func<EntityUri, HydrationOutcome> outcomeOf, Func<string, bool> wasTransient)
    {
        if (_settled) return;
        _settled = true;
        for (int i = 0; i < _claimed.Count; i++)
        {
            var (uri, key, slot) = _claimed[i];
            var outcome = outcomeOf(uri);
            // Callers pass the HIGHER of "what we reached" and "what was asked for", so a run that fell short still
            // seals the ask it could not satisfy.
            _ledger.Seal(uri, outcome.Reached > _level ? outcome.Reached : _level, outcome, wasTransient(uri.Uri));
            _ledger.Release(key, slot.Task);
            slot.TrySetResult(outcome);
        }
    }

    /// <summary>The pass died as a whole (transport, cancellation). Seals NOTHING — a transport error is not an answer,
    /// so the next ask really retries — and answers every slot with the verdict rather than an exception, because a
    /// joiner that merely rode along should read a status, not catch someone else's stack trace.</summary>
    public void Fail(HydrationStatus status, string? error)
    {
        if (_settled) return;
        _settled = true;
        for (int i = 0; i < _claimed.Count; i++)
        {
            var (_, key, slot) = _claimed[i];
            _ledger.Release(key, slot.Task);
            slot.TrySetResult(new HydrationOutcome(HydrationLevel.None, status, error));
        }
    }

    /// <summary>Belt and braces: a runner that escaped without settling (an exception path we did not foresee) must not
    /// leave a uri in flight forever, and must not leave a joiner awaiting a task that will never complete.</summary>
    public void Dispose() => Fail(HydrationStatus.Failed, "hydration run ended without publishing");
}
