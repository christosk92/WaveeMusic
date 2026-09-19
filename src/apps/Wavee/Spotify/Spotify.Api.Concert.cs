// ── Spotify/Spotify.Api.Concert.cs ──────────────────────────────────────────────────────────────────────────────────
// the two concert fetch answers (WP-5.N contract §3) and the hub's live host — the concert data that `Fetch` cannot plan
//
// Role: SHELL
// Owner: N (stream N-C)
// Wave: 5
// Budget: ~450 (new api partial, WP-5.N contract §0)
// Spec: ch 17 §7 (data & readiness), §9 item 2 (a late answer never paints); 0.2.9 `SpotifyLive/SpotifyConcertService.cs`
//   + `Features/Concerts/ConcertLocationController.cs` + `ConcertHubPage.cs`'s query orchestration
//
// TWO DOORS.
//   · THE FETCH DOOR. `ConcertAnswer` (PathfinderOp.Concert, a concert row's groups) and `ArtistConcertsAnswer`
//     (PathfinderOp.ArtistConcerts, the artist's schedule edge) run inside the provider (`Spotify.Api.cs` AnswerQuery
//     arms, the coordinator's routing patch): request, decode into the provider's staging, return the Result.
//   · THE HOST DOOR (`ConcertHost`, Concert.cs). A feed subject, a count preview, a place's concepts, a city search, a
//     reverse lookup, a save and the two location reads are not rows of a `Table` — `Fetch` has nothing to plan — so the
//     hub page asks the host. THE ONE DATA PATH IN THE CONCERT SURFACES THAT DOES NOT GO THROUGH `Fetch`, reported as
//     such; it still lands the only legal way: the work runs on an api thread exactly like the provider's
//     (`Spotify.Api.cs` `SpotifyFetchProvider.Execute`: rent a staging, stamp the scope epoch, request, decode) and
//     `Spotify.Post` carries the staging back to the UI thread, where it is committed and published (the
//     `Spotify.Library.cs` SeedCreated/StageOwned precedent) and returned to the pool.
//
// LATE ANSWERS (ch 17 §9 item 2). A feed answer lands on ITS OWN subject (the key it was asked for), so a filter change
// cannot be painted over by the previous tuple's page. A count lands only while its ask is still the newest one for the
// process and newer than the count the subject holds. Every answer for a retired scope is dropped whole.
//
// NOT A SPOTIFY SCOPE ⇒ EMPTY (WP-5.N contract §7). Outside a Spotify catalogue (`--fake`, a signed-out graph) every
// host method answers through `ConcertHost.Offline`: empty, Complete lists and refused saves — never a throw, never a
// spinner. Pages never ask "am I fake".
//
// GEOLOCATION. `Platform/` has no OS geolocation seam (no `IGeolocationProvider` is constructed anywhere in the app), so
// `Geolocation` is null and the picker's "Use my location" reports `concerts.location.unavailable`. Reported as a gap.

using System.Text;

namespace Wavee;

public static partial class Spotify
{
    public static partial class Api
    {
        // ── 1. the fetch answers (contract §3) ───────────────────────────────────────────────────────────────────────

        /// <summary><c>PathfinderOp.Concert</c>: the concert detail — the row at Full authority for every group, its
        /// offers, lineup and related shows. API THREAD.</summary>
        public static Result ConcertAnswer(string concertUri, Staging s)
        {
            Result result = Concert(concertUri, authenticated: true, CancellationToken.None);
            if (result.Ok && result.Body.Length > 0) Decode.ConcertDetail(result.Bytes, s);
            return result;
        }

        /// <summary><c>PathfinderOp.ArtistConcerts</c>: the artist's schedule edge. The saved place's geohash rides along
        /// when one is known (0.2.9 sent the page's saved place); the query is not paged, so <paramref name="offset"/> is
        /// accepted and ignored. API THREAD.</summary>
        public static Result ArtistConcertsAnswer(string artistUri, int offset, Staging s)
        {
            _ = offset;
            Result result = ArtistConcerts(artistUri, ConcertPlaces.SavedGeoHash, includeNearby: true, CancellationToken.None);
            if (result.Ok && result.Body.Length > 0) Decode.ArtistConcerts(result.Bytes, Encoding.UTF8.GetBytes(artistUri), s);
            return result;
        }

        // ── 2. the hub's host ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>THE concert host — install as <c>ConcertHost.Current = Spotify.Api.Concerts</c> (Concert.InstallPages).</summary>
        public static ConcertHost Concerts { get; } = new SpotifyConcertHost();

        /// <summary>The newest count ask this process has sent (UI thread). An answer to an older ask never commits.</summary>
        static uint s_latestCountAsk;

        sealed class SpotifyConcertHost : ConcertHost
        {
            static bool Live => Fetch.CatalogProvider(Entities.Current.Key) == EntityProvider.Spotify;

            public override void ResolveLocation(Action<bool>? done)
            {
                if (!Live) { Offline.ResolveLocation(done); return; }
                Answer(Entities.Current.Epoch, static s =>
                {
                    var ct = CancellationToken.None;
                    Result inferredAnswer = InferredUserLocation(ct);
                    bool? inferred = inferredAnswer.Ok ? Decode.InferredUserLocation(inferredAnswer.Bytes) : null;
                    Result result = UserLocation(ct);
                    return (result.Status, result.Ok && Decode.UserLocation(result.Bytes, inferred, s));
                }, null, (ok, _, _, _) => done?.Invoke(ok));
            }

            public override void ResolveArtistPageLocation(Action<bool>? done)
            {
                if (!Live) { Offline.ResolveArtistPageLocation(done); return; }
                Answer(Entities.Current.Epoch, static s =>
                {
                    Result result = ArtistConcertsPageLocation(CancellationToken.None);
                    return (result.Status, result.Ok && Decode.ArtistPageLocation(result.Bytes, s));
                }, null, (ok, _, _, _) => done?.Invoke(ok));
            }

            public override void Concepts(int placeSlot, string? biasConceptUri, Action<bool>? done)
            {
                if (!Live) { Offline.Concepts(placeSlot, biasConceptUri, done); return; }
                var scope = Entities.Current;
                var edge = scope.Edges.PlaceConcepts;
                var place = ConcertPlaces.From(placeSlot);
                if (place is null) { done?.Invoke(false); return; }
                if (edge.WasAsked(placeSlot, 0)) { done?.Invoke(true); return; }
                if (string.IsNullOrWhiteSpace(place.GeoHash))
                {
                    // No geohash ⇒ no concepts call (ch 17 W6): the strip is the lone "All", answered, not pending.
                    AnswerConceptsEmpty(placeSlot);
                    Entities.Publish();
                    done?.Invoke(true);
                    return;
                }
                edge.MarkAsked(placeSlot, 0);
                string geoHash = place.GeoHash!;
                byte[] key = Encoding.UTF8.GetBytes(ConcertFeedKey.PlaceKey(place));
                Answer(scope.Epoch, s =>
                {
                    Result result = ConcertConcepts(geoHash, biasConceptUri, CancellationToken.None);
                    if (result.Ok) Decode.ConcertConcepts(result.Bytes, key, s);
                    return (result.Status, result.Ok);
                }, null, (ok, current, status, _) =>
                {
                    if (current)
                    {
                        if (ok) edge.MarkAnswered(placeSlot);
                        else edge.MarkFailed(placeSlot, 0, status);
                    }
                    done?.Invoke(ok);
                });
            }

            public override void Feed(int feedSlot, ConcertFeedQuery query, bool append, Action<bool>? done)
            {
                if (!Live) { Offline.Feed(feedSlot, query, append, done); return; }
                var scope = Entities.Current;
                if (feedSlot <= Table.None || feedSlot >= scope.ConcertFeeds.Count) { done?.Invoke(false); return; }
                var edge = scope.Edges.FeedSection;
                if (!append)
                {
                    if (edge.WasAsked(feedSlot, 0)) { done?.Invoke(true); return; }
                    edge.MarkAsked(feedSlot, 0);
                }
                var wire = Wire.Of(query);
                // The key is the TUPLE's — the answer lands on this subject whatever the page shows by then.
                byte[] key = Encoding.UTF8.GetBytes(ConcertFeedKey.For(ConcertFeedKey.PlaceKey(query.Location),
                    query.RadiusKm ?? ConcertHub.DefaultRadiusKm, query.DateRange, query.ConceptUris));
                string? pagination = append ? query.PaginationKey : null;
                Answer(scope.Epoch, s =>
                {
                    Result result = ConcertFeed(wire.GeoHash, wire.GeonameId, wire.From, wire.To, wire.Concepts, wire.Radius,
                        pagination, CancellationToken.None);
                    return (result.Status, result.Ok && Decode.ConcertFeed(result.Bytes, key, append, s));
                }, null, (ok, current, status, _) =>
                {
                    if (current && !append)
                    {
                        if (ok) edge.MarkAnswered(feedSlot);
                        else edge.MarkFailed(feedSlot, 0, status);        // Readiness → Failed: the hub's Retry vacancy
                    }
                    done?.Invoke(ok);
                });
            }

            public override void Count(int feedSlot, ConcertFeedQuery query, uint askVersion, Action<bool>? done)
            {
                if (!Live) { Offline.Count(feedSlot, query, askVersion, done); return; }
                if (askVersion > s_latestCountAsk) s_latestCountAsk = askVersion;
                var wire = Wire.Of(query);
                byte[] key = Encoding.UTF8.GetBytes(ConcertFeedKey.For(ConcertFeedKey.PlaceKey(query.Location),
                    query.RadiusKm ?? ConcertHub.DefaultRadiusKm, query.DateRange, query.ConceptUris));
                Answer(Entities.Current.Epoch, s =>
                {
                    Result result = ConcertCount(wire.GeonameId, wire.Radius, wire.From, wire.To, wire.Concepts, CancellationToken.None);
                    return (result.Status, result.Ok && Decode.ConcertCount(result.Bytes, key, askVersion, s));
                }, () => askVersion >= s_latestCountAsk, (ok, _, _, _) => done?.Invoke(ok));
            }

            public override void SearchPlaces(string query, Action<ConcertPlaceAnswer> done)
            {
                if (!Live) { Offline.SearchPlaces(query, done); return; }
                if (string.IsNullOrWhiteSpace(query)) { done(ConcertPlaceAnswer.Empty); return; }
                string text = query.Trim();
                PlaceQuery(() => SearchConcertLocations(text, CancellationToken.None), done);
            }

            public override void ReversePlaces(double latitude, double longitude, Action<ConcertPlaceAnswer> done)
            {
                if (!Live) { Offline.ReversePlaces(latitude, longitude, done); return; }
                PlaceQuery(() => ConcertLocationsByLatLon(latitude, longitude, CancellationToken.None), done);
            }

            public override void SavePlace(int placeSlot, Action<bool> done)
            {
                if (!Live) { Offline.SavePlace(placeSlot, done); return; }
                var place = ConcertPlaces.From(placeSlot);
                if (place is null || string.IsNullOrWhiteSpace(place.Id)) { done(false); return; }
                string id = place.Id;
                Answer(Entities.Current.Epoch, _ =>
                {
                    Result result = SaveConcertLocation(id, CancellationToken.None);
                    return (result.Status, result.Ok && Decode.SaveConcertLocation(result.Bytes));
                }, null, (ok, current, _, _) =>
                {
                    bool saved = ok && current;
                    if (saved)
                    {
                        ConcertPlaces.MarkSaved(placeSlot);
                        Entities.Publish();
                    }
                    done(saved);
                });
            }

            /// <summary>This build constructs no OS geolocation provider (see the file header).</summary>
            public override FluentGpu.Pal.IGeolocationProvider? Geolocation => null;

            static void PlaceQuery(Func<Result> request, Action<ConcertPlaceAnswer> done)
            {
                Answer(Entities.Current.Epoch, s =>
                {
                    Result result = request();
                    if (result.Ok) Decode.ConcertLocations(result.Bytes, s);
                    return (result.Status, result.Ok);
                }, null, (ok, current, _, s) => done(ok && current && s is not null
                    ? new ConcertPlaceAnswer(true, MatchedPlaces(s))
                    : ConcertPlaceAnswer.Failed));
            }
        }

        /// <summary>A feed query spelled the way the wire wants it: the place id when there is one, else the geohash (the two
        /// are alternatives — sending both narrows the feed to nothing).</summary>
        readonly struct Wire
        {
            public readonly string? GeonameId, GeoHash;
            public readonly DateOnly? From, To;
            public readonly string[] Concepts;
            public readonly double? Radius;

            Wire(string? geonameId, string? geoHash, DateOnly? from, DateOnly? to, string[] concepts, double? radius)
            {
                GeonameId = geonameId;
                GeoHash = geoHash;
                From = from;
                To = to;
                Concepts = concepts;
                Radius = radius;
            }

            public static Wire Of(ConcertFeedQuery query)
            {
                var place = query.Location;
                string? id = string.IsNullOrWhiteSpace(place?.Id) ? null : place!.Id;
                string? geo = id is null && !string.IsNullOrWhiteSpace(place?.GeoHash) ? place!.GeoHash : null;
                string[] concepts = [];
                if (query.ConceptUris is { Count: > 0 } uris)
                {
                    concepts = new string[uris.Count];
                    for (int i = 0; i < concepts.Length; i++) concepts[i] = uris[i];
                }
                return new Wire(id, geo, query.DateRange?.From, query.DateRange?.To, concepts, query.RadiusKm);
            }
        }

        /// <summary>THE HOST'S RUNNER. <paramref name="work"/> runs on an api thread against a rented staging stamped with the
        /// scope epoch and answers (status, ok). Back on the UI thread: a still-current, ok answer that
        /// <paramref name="stillWanted"/> accepts is committed and published; then <paramref name="landed"/> runs with
        /// (ok, current, status, staging) and the staging goes back to the pool. A full queue answers (false, true, 0,
        /// null) at once; a decoder that throws answers not-ok. Never throws, never leaves the callback uncalled.</summary>
        static void Answer(uint epoch, Func<Staging, (int Status, bool Ok)> work, Func<bool>? stillWanted,
            Action<bool, bool, int, Staging?> landed)
        {
            bool queued = Run(() =>
            {
                Staging s = Staging.Rent();
                s.Epoch = epoch;
                int status = 0;
                bool ok;
                try
                {
                    (status, ok) = work(s);
                }
                catch (Exception ex)
                {
                    Log.Error("spotify", "concert host faulted", ex);
                    ok = false;
                    s.Reset();
                    s.Epoch = epoch;
                }
                Post(() =>
                {
                    try
                    {
                        bool current = Entities.Current.Epoch == epoch;
                        bool commit = ok && current && (stillWanted is null || stillWanted());
                        if (commit)
                        {
                            Entities.Commit(s);
                            Entities.Publish();
                        }
                        landed(ok && current, current, status, s);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("spotify", "concert host commit faulted", ex);
                    }
                    finally
                    {
                        Staging.Return(s);
                    }
                });
            });
            if (!queued) landed(false, true, 0, null);
        }

        /// <summary>The place slots a location answer staged, in wire order, deduplicated (0.2.9 MapPlaceItems). UI THREAD,
        /// after the commit and before the staging is returned.</summary>
        static int[] MatchedPlaces(Staging s)
        {
            var staged = s.Places.Span;
            var places = Entities.Current.Places;
            var slots = new List<int>(staged.Length);
            foreach (ref readonly var row in staged)
            {
                if (row.Role != PlaceRole.Match || row.Key.IsEmpty) continue;
                if (!places.TryGetSlot(s.Intern(row.Key), out int slot) || slots.Contains(slot)) continue;
                slots.Add(slot);
            }
            return slots.ToArray();
        }
    }
}
