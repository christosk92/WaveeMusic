using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;

namespace Wavee.SpotifyLive;

/// <summary>The authoritative subset of a stored playlist header that can replace a shallow Home identity.</summary>
internal readonly record struct HomePlaylistHeader(
    string Title, string? Subtitle, string? OwnerName, Image? Cover, int TrackCount);

/// <summary>The answer to one playlist HEAD read: the current playlist4 base revision, plus whether the read actually
/// happened. The two are kept apart on purpose — a FAILED probe is "we learned nothing", which must behave differently
/// from "the server says there is no revision", or a flaky network would turn into a per-read refresh storm.</summary>
internal readonly record struct RevisionProbe(bool Ok, byte[]? Revision)
{
    public static readonly RevisionProbe Unknown = new(false, null);
}

/// <summary>
/// Resolves provider-marked shallow Daylist cards before Home becomes Ready. Duplicate occurrences share one header
/// read, the Home transport is requeried at most once per observed content ROLLOVER (never once per read — see
/// <see cref="ClaimRequery"/>), and every overlay is applied to both presentation groups and the lossless section
/// ledger. The class owns orchestration only; its four required seams keep it engine-free and directly testable
/// without weakening live wiring.
/// </summary>
/// <remarks>
/// <para>STALENESS IS THE PLAYLIST REVISION, and nothing else. A daylist is one URI whose title, artwork and contents
/// roll over through the day behind a monotonically advancing playlist4 revision (4-byte big-endian counter + hash).
/// Two weaker primitives were considered and are deliberately NOT used: a TTL on the header is a guess that is wrong in
/// both directions (it refetches a card that did not move, and serves a card that did), and a (uri, title) identity
/// diff is a proxy that misses every rollover which happens to keep the same title. The Pathfinder <c>home</c> response
/// carries no revision of its own — it is GraphQL and exposes no playlist4 field — so the revision has to be READ, and
/// that is what the head probe seam is for: one <c>?decorate=revision</c> GET per hydration-marked URI per Home read,
/// coalesced, never per render and never on a cadence of its own.</para>
/// <para>INSTANCE STATE: one hydrator must be held for the lifetime of the Home source it serves. A per-read instance
/// would forget which revision the composed body reflects and reintroduce the once-per-read invalidation this class
/// exists to bound.</para>
/// </remarks>
internal sealed class HomeDaylistHydrator
{
    /// <summary>How long a completed head probe answers a second caller. This is REQUEST COALESCING, not a freshness
    /// policy: one logical refresh — a reactivation compare that finds a newer revision, then the Home read that
    /// compare triggers — must cost one call rather than two. Nothing about staleness is decided by it.</summary>
    public const long ProbeCoalesceMs = 5_000;

    readonly Func<string, HomePlaylistHeader?> _readHeader;
    readonly Func<string, CancellationToken, Task> _fetchHeader;
    readonly Func<string, CancellationToken, Task<byte[]?>> _probeRevision;
    /// <summary>(facet, ct) → a fresh, UNCACHED Home body. The facet is passed rather than captured: the requery
    /// invalidates a transport cache key, and the key is the request body — repairing a "Podcasts" read by refetching
    /// the unfiltered feed would invalidate the wrong entry and overlay the wrong document.</summary>
    readonly Func<string?, CancellationToken, Task<LiveHomeResult>> _refreshHome;
    readonly Func<long> _nowMs;

    // uri → the playlist4 revision the composed Home body currently REFLECTS. The key space is the set of
    // hydration-marked Home cards (Spotify marks only a daylist whose name is empty or equal to its daylist_pretitle),
    // so this stays a handful of entries per session. A missing key means "never resolved", which always resolves.
    readonly Dictionary<string, byte[]?> _reflected = new(StringComparer.Ordinal);

    // uri → the in-flight (or just-completed) head probe. See ProbeCoalesceMs.
    readonly Dictionary<string, (Task<RevisionProbe> Task, long At)> _probes = new(StringComparer.Ordinal);

    long _identityVersion;

    public HomeDaylistHydrator(
        Func<string, HomePlaylistHeader?> readHeader,
        Func<string, CancellationToken, Task> fetchHeader,
        Func<string, CancellationToken, Task<byte[]?>> probeRevision,
        Func<string?, CancellationToken, Task<LiveHomeResult>> refreshHome,
        Func<long>? nowMs = null)
    {
        ArgumentNullException.ThrowIfNull(readHeader);
        ArgumentNullException.ThrowIfNull(fetchHeader);
        ArgumentNullException.ThrowIfNull(probeRevision);
        ArgumentNullException.ThrowIfNull(refreshHome);
        _readHeader = readHeader;
        _fetchHeader = fetchHeader;
        _probeRevision = probeRevision;
        _refreshHome = refreshHome;
        _nowMs = nowMs ?? (static () => Environment.TickCount64);
    }

    /// <summary>Monotonic count of observed content rollovers. The Home feed epoch is published off this: a step means
    /// the card a mounted or KeepAlive-parked Home page is showing has been superseded, and an unchanged value means a
    /// read produced nothing any page needs to re-render for.</summary>
    public long IdentityVersion { get { lock (_reflected) return _identityVersion; } }

    /// <summary>True once this hydrator has resolved <paramref name="uri"/> — i.e. this URI is one whose STORE header
    /// the composed Home feed now depends on. The store-change watch filters on it, so a rewrite of some unrelated
    /// playlist can never wake Home.</summary>
    public bool Hydrated(string uri)
    {
        lock (_reflected) return _reflected.ContainsKey(uri);
    }

    /// <summary>The reactivation compare: head-probe every already-hydrated URI and answer whether any of them has
    /// rolled over since the composed body was built. It resolves NOTHING itself — a true answer bumps the feed epoch,
    /// and the epoch is what makes the page re-read through the one ordinary path. Costs one small GET per hydrated URI
    /// (one, in practice) and zero when nothing has been hydrated yet.</summary>
    public async Task<bool> RevalidateAsync(CancellationToken ct)
    {
        string[] uris;
        lock (_reflected)
        {
            if (_reflected.Count == 0) return false;
            uris = new string[_reflected.Count];
            _reflected.Keys.CopyTo(uris, 0);
        }

        bool moved = false;
        for (int i = 0; i < uris.Length; i++)
        {
            var probe = await ProbeAsync(uris[i], ct).ConfigureAwait(false);
            if (!probe.Ok) continue;   // learned nothing — never report a rollover we did not observe
            lock (_reflected)
                if (_reflected.TryGetValue(uris[i], out var reflected) && !RevisionEquals(probe.Revision, reflected))
                    moved = true;
        }
        return moved;
    }

    public async Task<LiveHomeResult> ResolveAsync(LiveHomeResult source, CancellationToken ct)
    {
        var shallow = ShallowCards(source);
        if (shallow.Count == 0) return source;

        // The head probes go out together: the map is already keyed by URI, so the fan-out is deduplicated before it
        // starts, and in practice there is exactly one daylist card.
        var probes = new Dictionary<string, RevisionProbe>(shallow.Count, StringComparer.Ordinal);
        foreach (var pair in shallow) probes[pair.Key] = RevisionProbe.Unknown;
        {
            var keys = new string[shallow.Count];
            probes.Keys.CopyTo(keys, 0);
            var tasks = new Task<RevisionProbe>[keys.Length];
            for (int i = 0; i < keys.Length; i++) tasks[i] = ProbeAsync(keys[i], ct);
            await Task.WhenAll(tasks).ConfigureAwait(false);
            for (int i = 0; i < keys.Length; i++) probes[keys[i]] = tasks[i].Result;
        }
        ct.ThrowIfCancellationRequested();

        var exact = new Dictionary<string, HomePlaylistHeader>(shallow.Count, StringComparer.Ordinal);
        var resident = new HashSet<string>(StringComparer.Ordinal);
        var fetched = new HashSet<string>(StringComparer.Ordinal);
        List<KeyValuePair<string, HomeCard>>? pending = null;
        foreach (var pair in shallow)
        {
            ct.ThrowIfCancellationRequested();
            // Residency is trusted IFF the revision the composed body reflects is still the server's. Otherwise the URI
            // joins the header batch exactly as a miss would — which is also what a never-resolved URI does, so a cold
            // start reads today's header instead of overlaying yesterday's, still-resident one onto today's feed.
            if (IsCurrent(pair.Key, probes[pair.Key]) && TryExact(pair.Value, _readHeader(pair.Key), out var residentHeader))
            {
                exact.Add(pair.Key, residentHeader);
                resident.Add(pair.Key);
            }
            else (pending ??= new List<KeyValuePair<string, HomeCard>>(shallow.Count)).Add(pair);
        }

        if (pending is { Count: > 0 })
        {
            // Issuing the misses together keeps first paint off an N-round-trip serial chain. Each miss swallows its own
            // failure — one unavailable playlist must not fail or delete the rest of Home, and there is deliberately no
            // title synthesis from tags — and cancellation is re-asserted once afterwards so an abandoned batch still
            // propagates instead of surfacing as a silent partial hydration.
            if (pending.Count == 1)
            {
                await FetchQuietAsync(pending[0].Key, ct).ConfigureAwait(false);
            }
            else
            {
                var fetches = new Task[pending.Count];
                for (int i = 0; i < fetches.Length; i++) fetches[i] = FetchQuietAsync(pending[i].Key, ct);
                await Task.WhenAll(fetches).ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();
            for (int i = 0; i < pending.Count; i++)
                if (TryExact(pending[i].Value, _readHeader(pending[i].Key), out var fetchedHeader))
                {
                    exact.Add(pending[i].Key, fetchedHeader);
                    fetched.Add(pending[i].Key);
                }
        }

        if (exact.Count == 0)
        {
            LogResolve(shallow, probes, exact, resident, fetched, 0);
            return source;
        }

        LiveHomeResult basis = source;
        List<string>? claimed = null;
        // A resident header is already enough to RENDER the card; the Home requery only gives the transport body its own
        // chance to carry the exact identity. Because it invalidates and refetches UNCACHED, it must never ride the read
        // cadence: Home is polled on a 60 s timer, and firing per read pinned Home permanently off the Pathfinder TTL.
        if (ClaimRequery(exact, probes) is { } newlyClaimed)
        {
            claimed = newlyClaimed;
            try
            {
                var refreshed = await _refreshHome(source.Facet, ct).ConfigureAwait(false);
                if (HasContent(refreshed)) basis = refreshed;
            }
            catch (OperationCanceledException)
            {
                Unclaim(newlyClaimed);   // the attempt never completed; do not spend this rollover's one requery on it
                throw;
            }
            catch
            {
                // The exact stored headers are already authoritative. A failed Home requery must not make the successful
                // hydration disappear, and the original source ledger remains the accounting baseline. The claim stands:
                // a failing requery must not turn into a per-read retry storm either.
            }
        }

        LiveHomeResult overlaid = Overlay(basis, exact);
        LogResolve(shallow, probes, exact, resident, fetched, claimed?.Count ?? 0);
        return overlaid;
    }

    void LogResolve(IReadOnlyDictionary<string, HomeCard> shallow,
                    Dictionary<string, RevisionProbe> probes,
                    Dictionary<string, HomePlaylistHeader> exact,
                    HashSet<string> resident, HashSet<string> fetched, int claimed)
    {
        if (!WaveeLog.Instance.IsEnabled(WaveeLogLevel.Info)) return;

        foreach (var pair in shallow)
        {
            string uri = pair.Key;
            var probe = probes.TryGetValue(uri, out var p) ? p : RevisionProbe.Unknown;
            byte[]? reflected;
            lock (_reflected) _reflected.TryGetValue(uri, out reflected);
            exact.TryGetValue(uri, out var header);
            string path = resident.Contains(uri) ? "resident" : fetched.Contains(uri) ? "fetched" : "overlay";
            WaveeLog.Instance.Event(WaveeLogLevel.Info, "home", "home.daylist.resolve",
                "daylist identity resolved",
                fields:
                [
                    WaveeLogField.Of("uri", uri),
                    WaveeLogField.Of("probe.ok", probe.Ok),
                    WaveeLogField.Of("rev.reflected", DaylistIdentity.ShortRev(reflected)),
                    WaveeLogField.Of("rev.probe", DaylistIdentity.ShortRev(probe.Revision)),
                    WaveeLogField.Of("path", path),
                    WaveeLogField.Of("title.home", pair.Value.Title ?? ""),
                    WaveeLogField.Of("title.header", header.Title ?? ""),
                    WaveeLogField.Of("cover.home", DaylistIdentity.CoverId(pair.Value.Image)),
                    WaveeLogField.Of("cover.header", DaylistIdentity.CoverId(header.Cover)),
                    WaveeLogField.Of("sameArt", ImageSource.SameArt(pair.Value.Image, header.Cover)),
                    WaveeLogField.Of("claimedRequery", claimed),
                ]);
        }
    }

    async Task FetchQuietAsync(string uri, CancellationToken ct)
    {
        try { await _fetchHeader(uri, ct).ConfigureAwait(false); }
        catch { /* per-URI: the raw provider card stays the truthful fallback; ct is re-checked by the caller */ }
    }

    Task<RevisionProbe> ProbeAsync(string uri, CancellationToken ct)
    {
        lock (_probes)
        {
            if (_probes.TryGetValue(uri, out var existing)
                && (!existing.Task.IsCompleted || (ulong)(_nowMs() - existing.At) < ProbeCoalesceMs))
                return existing.Task;
            var started = RunProbeAsync(uri, ct);
            _probes[uri] = (started, _nowMs());
            return started;
        }
    }

    async Task<RevisionProbe> RunProbeAsync(string uri, CancellationToken ct)
    {
        // A failed head read reports Unknown rather than "no revision": every consumer treats Unknown as "keep serving
        // what we have", so an unreachable spclient degrades to the previous answer instead of refetching every read.
        try { return new RevisionProbe(true, await _probeRevision(uri, ct).ConfigureAwait(false)); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return RevisionProbe.Unknown; }
    }

    bool IsCurrent(string uri, RevisionProbe probe)
    {
        lock (_reflected)
            return _reflected.TryGetValue(uri, out var reflected)
                && (!probe.Ok || RevisionEquals(probe.Revision, reflected));
    }

    /// <summary>Reserves the single Home requery owed to any identity in <paramref name="exact"/> whose revision the
    /// composed body does not already reflect, returning the newly claimed URIs, or null when every identity is already
    /// accounted for. Revision equality IS the "already refreshed, do not requery again" answer, so a repeated read of
    /// an unmoved daylist claims nothing while a genuine rollover claims exactly once.</summary>
    List<string>? ClaimRequery(Dictionary<string, HomePlaylistHeader> exact, Dictionary<string, RevisionProbe> probes)
    {
        List<string>? claimed = null;
        lock (_reflected)
        {
            foreach (var pair in exact)
            {
                var probe = probes.TryGetValue(pair.Key, out var p) ? p : RevisionProbe.Unknown;
                bool known = _reflected.TryGetValue(pair.Key, out var reflected);
                if (known && (!probe.Ok || RevisionEquals(probe.Revision, reflected))) continue;
                _reflected[pair.Key] = probe.Ok ? probe.Revision : reflected;
                _identityVersion++;   // a rollover — the epoch every mounted/parked Home page compares against
                (claimed ??= new List<string>(exact.Count)).Add(pair.Key);
            }
        }
        return claimed;
    }

    void Unclaim(List<string> claimed)
    {
        lock (_reflected)
        {
            // Forget the reflection entirely rather than restoring the previous one: "never resolved" always resolves,
            // which is the conservative answer for a rollover we started to adopt and then abandoned.
            for (int i = 0; i < claimed.Count; i++) _reflected.Remove(claimed[i]);
            _identityVersion -= claimed.Count;   // the attempt never completed; the version must not advertise it
        }
    }

    static bool RevisionEquals(byte[]? a, byte[]? b)
        => a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);

    static Dictionary<string, HomeCard> ShallowCards(LiveHomeResult source)
    {
        var result = new Dictionary<string, HomeCard>(StringComparer.Ordinal);
        for (int g = 0; g < source.Groups.Count; g++) Add(source.Groups[g].Cards, result);
        if (source.Sections is { } sections)
            for (int s = 0; s < sections.Count; s++) Add(sections[s].Cards, result);
        return result;

        static void Add(IReadOnlyList<HomeCard> cards, Dictionary<string, HomeCard> target)
        {
            for (int i = 0; i < cards.Count; i++)
            {
                var card = cards[i];
                if (card.Meta?.NeedsHydration != true || card.Uri.Length == 0) continue;
                target.TryAdd(card.Uri, card);
            }
        }
    }

    static bool TryExact(HomeCard shallow, HomePlaylistHeader? candidate, out HomePlaylistHeader exact)
    {
        if (candidate is { } header
            && !string.IsNullOrWhiteSpace(header.Title)
            && !string.Equals(header.Title, shallow.Title, StringComparison.Ordinal)
            && !string.Equals(header.Title, shallow.Meta?.GenericTitle, StringComparison.Ordinal))
        {
            exact = header;
            return true;
        }

        exact = default;
        return false;
    }

    static bool HasContent(LiveHomeResult result) => result.Groups.Count > 0 || result.Sections is { Count: > 0 };

    static LiveHomeResult Overlay(LiveHomeResult source, IReadOnlyDictionary<string, HomePlaylistHeader> exact)
    {
        bool changed = false;
        var groups = new HomeGroup[source.Groups.Count];
        for (int i = 0; i < groups.Length; i++)
        {
            var group = source.Groups[i];
            var cards = OverlayCards(group.Cards, exact, ref changed);
            groups[i] = ReferenceEquals(cards, group.Cards) ? group : group with { Cards = cards };
        }

        IReadOnlyList<HomeSection>? sections = source.Sections;
        if (source.Sections is { } sourceSections)
        {
            var mapped = new HomeSection[sourceSections.Count];
            for (int i = 0; i < mapped.Length; i++)
            {
                var section = sourceSections[i];
                var cards = OverlayCards(section.Cards, exact, ref changed);
                mapped[i] = ReferenceEquals(cards, section.Cards) ? section : section with { Cards = cards };
            }
            sections = mapped;
        }

        return changed ? source with { Groups = groups, Sections = sections } : source;
    }

    static IReadOnlyList<HomeCard> OverlayCards(IReadOnlyList<HomeCard> source,
        IReadOnlyDictionary<string, HomePlaylistHeader> exact, ref bool changed)
    {
        HomeCard[]? mapped = null;
        for (int i = 0; i < source.Count; i++)
        {
            var card = source[i];
            if (card.Meta?.NeedsHydration != true || !exact.TryGetValue(card.Uri, out var header)) continue;
            mapped ??= Copy(source);
            mapped[i] = OverlayCard(card, header);
            changed = true;
        }
        return mapped ?? source;
    }

    static HomeCard[] Copy(IReadOnlyList<HomeCard> source)
    {
        var copy = new HomeCard[source.Count];
        for (int i = 0; i < copy.Length; i++) copy[i] = source[i];
        return copy;
    }

    /// <summary>Reports "this daylist window ends at &lt;unix ms&gt;" as the feed hydrates: <c>(contextUri, expiresAtMs,
    /// title)</c>. Assigned by the composition root (Wavee wires it to the scheduled-toast notifier); null in tests and
    /// before wiring, which makes the report a no-op. Deliberately a hook, not a direct call — the hydrator resolves feed
    /// data and must stay ignorant of whether anything notifies.</summary>
    internal static Action<string, long, string?>? WindowObserved;

    static HomeCard OverlayCard(HomeCard card, HomePlaylistHeader header)
    {
        var meta = card.Meta!;
        // A hydrated daylist card carries the end of its own window — a KNOWN future moment. Reported through the hook
        // rather than calling a notifier directly: this type is source-included by Wavee.Tests, so it must not reach into
        // the app's toast layer, and the hydrator has no business knowing that anyone notifies.
        if (meta.ExpiresAtMs > 0) WindowObserved?.Invoke(card.Uri, meta.ExpiresAtMs, header.Title);
        return card with
        {
            Title = header.Title,
            Subtitle = header.Subtitle ?? card.Subtitle,
            Image = header.Cover ?? card.Image,
            Meta = meta with
            {
                TrackCount = header.TrackCount > 0 ? header.TrackCount : meta.TrackCount,
                OwnerName = string.IsNullOrWhiteSpace(header.OwnerName) ? meta.OwnerName : header.OwnerName,
                NeedsHydration = false,
            },
        };
    }
}

/// <summary>Engine-free field helpers for <c>home.daylist.resolve</c> so tests can table-drive the log shape without
/// a feed or a logger. Cover identity uses <see cref="ImageSource.ImageIdSpan"/> — the same span
/// <c>CoverColorPlane.IdSpan</c> wraps — so this file never takes a UI dependency on <c>DetailCoverTrace</c>.</summary>
internal static class DaylistIdentity
{
    public static string ShortRev(byte[]? rev)
    {
        if (rev is null || rev.Length == 0) return "-";
        int n = Math.Min(2, rev.Length);
        return Convert.ToHexStringLower(rev.AsSpan(0, n));
    }

    public static string CoverId(Image? image)
    {
        string? url = image?.Url;
        if (string.IsNullOrEmpty(url)) return "-";
        var id = ImageSource.ImageIdSpan(url);
        return id.Length == 0 ? "-" : id.ToString();
    }

    public static string Path(bool resident, bool fetched) => resident ? "resident" : fetched ? "fetched" : "overlay";
}
