using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;

namespace Wavee.Backend.Hydration;

// ── the playlist ladder (design §2.3) ────────────────────────────────────────────────────────────────────────────────
// THE INVARIANT: this ladder never writes membership. The playlist plane (baseline, revision, dirty set, dealer diffs,
// mutations) is owned by the LibrarySync writer loop; the ladder only ASKS it, through IPlaylistOpener. That is why
// `sync.OnPlaylistHydrated` dies and why the ledger never TTL-seals a playlist Open on its own — LibrarySync's
// in-flight set and 5-minute window remain the freshness authority (plan §4 risk 2).
public sealed class PlaylistHydration : IKindHydration
{
    readonly IStore _store;
    readonly IPlaylistOpener _opener;
    readonly TraitPolicy _policy;
    readonly WaveeLogger _log;

    public PlaylistHydration(IStore store, IPlaylistOpener opener, TraitPolicy policy, WaveeLogger log = default)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _opener = opener ?? throw new ArgumentNullException(nameof(opener));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _log = log;
    }

    public EntityKind Kind => EntityKind.Playlist;

    /// <summary>The header/baseline rung, THEN a member scan — the same shape <c>CollectionHydration.LevelOf</c> and
    /// <c>ShowHydration</c>'s residency count use: a container is only as hydrated as its thinnest row. Without this a
    /// playlist read back Full the instant a baseline landed even when every member was still a gid-only stub (a cold
    /// restore, or a member that resolved AFTER this playlist's own hydrate ran), so nothing ever asked for it again.
    /// Reporting Identity here is what makes every subsequent Open re-ask the thin members through the ledger — a
    /// Reached seal only skips the ladder when the RESIDENT level is already ≥ what was asked, and a Partial one only
    /// brakes for its own TTL (10 min), so a caller asking for Open keeps re-driving <see cref="ContinueAsync"/> until
    /// the rows actually resolve. This cannot wedge: per <see cref="HydrationLevels.Of(Playlist?, bool)"/> the ledger
    /// never TTL-seals a playlist Open (LibrarySync stays the freshness authority for the plane itself), so a member
    /// scan reporting Identity forever just means the ladder keeps trying forever, not that it gives up.</summary>
    public HydrationLevel LevelOf(string uri)
    {
        var level = HydrationLevels.Of(_store.GetPlaylist(uri), _store.HasMembership(uri));
        if (level < HydrationLevel.Open) return level;
        var members = _store.Membership(uri);
        for (int i = 0; i < members.Count; i++)
        {
            var item = members[i].ItemUri;
            if (item.Length == 0) continue;
            HydrationLevel rowLevel = EntityUri.KindOf(item) switch
            {
                EntityKind.Track => HydrationLevels.Of(_store.GetTrack(item)),
                EntityKind.Episode => HydrationLevels.Of(_store.GetEpisode(item)),
                _ => HydrationLevel.Full,   // a member no row ladder owns cannot hold the container back
            };
            // A missing row (never hydrated at all) reads back None here, which is < Identity like any other thin row.
            if (rowLevel < HydrationLevel.Identity) return HydrationLevel.Identity;
        }
        return level;
    }

    /// <summary>The root-list playlists that still need an Open pass before their sidebar rows are authoritative.
    /// A header alone is not enough: without a membership baseline the projected count is only a thin metadata hint and
    /// a cover-less playlist cannot derive its track mosaic. Conversely, <see cref="IStore.HasMembership"/> keeps a
    /// genuinely empty playlist complete, so it is not fetched again on every login.
    /// <para>The third clause (<see cref="HydrationLevels.Of(Playlist?, bool)"/> reporting below Identity) exists for a
    /// header the first two clauses miss entirely: a RESTORED header can carry <c>Name == ""</c> while still having a
    /// membership baseline (persistence rejoin order, a torn cold-store row) — <c>GetPlaylist</c> is non-null and
    /// <c>HasMembership</c> is true, so the presence-only test above skipped it forever even though the row is unnamed
    /// and can never paint a sidebar label. Testing the LEVEL instead of raw presence is what catches that.</para></summary>
    public static IReadOnlyList<string> RootlistOpenPlan(IStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var root = store.Rootlist();
        for (int i = 0; i < root.Count; i++)
        {
            var e = root[i];
            if (e.Kind != 0 || EntityUri.KindOf(e.Uri) != EntityKind.Playlist || !seen.Add(e.Uri)) continue;
            var p = store.GetPlaylist(e.Uri);
            bool hasMembership = store.HasMembership(e.Uri);
            if (p is null || !hasMembership || HydrationLevels.Of(p, hasMembership) < HydrationLevel.Identity)
                result.Add(e.Uri);
        }
        return result;
    }

    /// <summary>Nothing extra: a playlist's catalogue answer is LIST_METADATA_V2 (205) alone.</summary>
    public void ExtraCatalogKinds(in EntityUri uri, HydrationLevel level, List<(string Uri, int Kind)> into) { }

    public async Task ContinueAsync(IReadOnlyList<EntityUri> uris, HydrationLevel level, HydrationOptions opts,
                                    HydrationContext ctx, CancellationToken ct)
    {
        for (int i = 0; i < uris.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            string uri = uris[i].Uri;

            // ── Identity ─────────────────────────────────────────────────────────────────────────────────────────────
            // Step 0's 205 is the general answer. A ROOTLIST member has a second, authoritative one — the header GET
            // LibrarySync already speaks — so it is the fallback for the case 205 cannot serve (a user-namespaced or
            // freshly-created list the catalogue does not carry). Asking for it only when the row is STILL unnamed is
            // what keeps the shared step-0 POST the common path instead of a per-playlist round trip.
            if (LevelOf(uri) < HydrationLevel.Identity && IsRootlistMember(uri))
            {
                try { await _opener.HeaderAsync(uri, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Warn("hydration.playlist.header", "playlist header fetch failed", uri, ex); }
            }

            if (level < HydrationLevel.Open) continue;

            // ── Open ─────────────────────────────────────────────────────────────────────────────────────────────────
            // No baseline ⇒ there is nothing to paint, so the open BLOCKS on LibrarySync's real open. With a baseline
            // it is a revalidation: enqueue and let the loop's own 5-minute/dirty gates decide whether anything fetches.
            bool hadBaseline = _store.HasMembership(uri);
            try
            {
                if (!hadBaseline) await _opener.OpenAsync(uri, ct).ConfigureAwait(false);
                else _opener.Revalidate(uri);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Warn("hydration.playlist.open", "playlist open failed", uri, ex); }

            // ── Open: thin-member repair — ask Identity for every member LevelOf found unnamed ───────────────────────────
            // PlaylistFetcher's hydrate delegate is ONE-SHOT: it only fires for a cold OpenAsync's full snapshot or for the
            // uris a diff just ADDED (PlaylistFetcher.HydrateAsync / HydrateUrisAsync). A row that stayed thin from an
            // earlier pass — a partial batch, a restore that never re-joined persisted rows, a member that resolved after
            // THIS playlist's own hydrate ran — was never re-asked: LevelOf saw it every time (the scan above) but nothing
            // acted on what it saw. This closes that gap with the same scan, turned into a blocking Identity ask, paged at
            // the transport's per-POST ceiling so a 10k-track cold-restored playlist becomes ~34 requests, not one giant one.
            List<string>? thin = null;
            {
                var members0 = _store.Membership(uri);
                for (int m = 0; m < members0.Count; m++)
                {
                    var item = members0[m].ItemUri;
                    if (item.Length == 0) continue;
                    HydrationLevel rowLevel = EntityUri.KindOf(item) switch
                    {
                        EntityKind.Track => HydrationLevels.Of(_store.GetTrack(item)),
                        EntityKind.Episode => HydrationLevels.Of(_store.GetEpisode(item)),
                        _ => HydrationLevel.Full,
                    };
                    if (rowLevel < HydrationLevel.Identity) (thin ??= new List<string>()).Add(item);
                }
            }
            if (thin is { Count: > 0 })
            {
                // Sub-asks inherit the caller's priority but never its surface (AlbumHydration:54's precedent) — a thin-
                // member repair is identity work, not a PlaylistOpen trait ask.
                var sub = new HydrationOptions(HydrationMode.Blocking, opts.Revalidate, TraitSurface.None, opts.Priority, SubAsk: true);
                try
                {
                    for (int start = 0; start < thin.Count; start += Metadata.MetadataChunking.MaxEntitiesPerRequest)
                    {
                        int count = Math.Min(Metadata.MetadataChunking.MaxEntitiesPerRequest, thin.Count - start);
                        await ctx.Hydrator.EnsureManyAsync(thin.GetRange(start, count), HydrationLevel.Identity, sub, ct)
                            .ConfigureAwait(false);
                    }
                    _log.Event(WaveeLogLevel.Debug, "hydration.playlist.members", "thin member identity ask",
                        fields: [WaveeLogField.Of("uri", uri), WaveeLogField.Of("thin", thin.Count)]);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Warn("hydration.playlist.members", "thin member identity ask failed", uri, ex); }
            }

            // ── post-step: the members' traits, on the pump ───────────────────────────────────────────────────────────
            // EVERY member, episodes included — that is the whole reason the trait door is addressed by uri rather than
            // by a `spotify:track:` prefix test. Nothing on screen waits for it.
            var members = _store.Membership(uri);
            if (members.Count == 0) continue;
            var memberUris = new List<string>(members.Count);
            for (int m = 0; m < members.Count; m++)
                if (members[m].ItemUri is { Length: > 0 } item) memberUris.Add(item);
            if (memberUris.Count == 0) continue;
            var traits = _policy.For(TraitSurface.PlaylistOpen);
            if (traits == TraitSet.None) continue;
            ctx.Pump.Enqueue(opts.Priority - 1,
                pumpCt => ctx.Hydrator.EnsureTraitsAsync(memberUris, traits, TraitSurface.PlaylistOpen, pumpCt));
        }
    }

    bool IsRootlistMember(string uri)
    {
        var root = _store.Rootlist();
        for (int i = 0; i < root.Count; i++)
            if (string.Equals(root[i].Uri, uri, StringComparison.Ordinal)) return true;
        return false;
    }

    void Warn(string eventId, string message, string uri, Exception ex)
        => _log.Event(WaveeLogLevel.Warning, eventId, message, ex: ex, fields: [WaveeLogField.Of("uri", uri)]);
}
