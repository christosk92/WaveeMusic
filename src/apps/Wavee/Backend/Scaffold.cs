using System;
using System.Threading.Tasks;
using Wavee.Core;

namespace Wavee.Backend;

// ── The backend composition root (the plan's Services.CreateReal, scaffold form) ─────────────────────────────────────
// Constructs the five engines over the store. The Spotify configs (Resource fetchers wired to real protocol routes), the
// real Transport (AP/dealer/HTTPS), and real audio are STUBS — they need network/credentials/protocol mechanics that are
// out of scope now. This wires the engines so they are exercisable end-to-end (see BackendSelfTest).
public sealed class BackendScaffold
{
    public IStore Store { get; }
    public SessionContextHost Session { get; }
    public ITransport Transport { get; }
    public MutationEngine Mutations { get; }
    public StubAudioEngine Audio { get; }
    public PlaybackReducer Playback { get; }

    public BackendScaffold()
    {
        Store = new InMemoryStore();
        Session = new SessionContextHost(new SessionContext("me", "US", "premium", "en", Tier.Premium, false));
        Transport = new StubTransport();
        Mutations = new MutationEngine(Store, new IMutationStrategy[] { new SetReplayStrategy(), new RootlistFollowStrategy(Store, new Playlists.RootlistLane()) });
        Audio = new StubAudioEngine();
        Playback = new PlaybackReducer(Audio);
    }
}

// ── Headless self-test: exercises every engine and returns 0 (all passed) / 1 (any failed) ───────────────────────────
public static class BackendSelfTest
{
    public static int Run(WaveeLogger log)
    {
        int pass = 0, fail = 0;
        void Check(string name, bool ok)
        {
            if (ok) { pass++; log.Info("  PASS  " + name); }
            else { fail++; log.Info("  FAIL  " + name); }
        }

        log.Info("Wavee backend self-test - the five engines over a queryable store");
        var sc = new BackendScaffold();

        // ── store (queryable spine) ──
        var t1 = Trk("spotify:track:1", "Alpha Song", "Aretha");
        var t2 = Trk("spotify:track:2", "Beta Tune", "Beck");
        sc.Store.UpsertTrack(t1);
        sc.Store.UpsertTrack(t2);
        Check("store: read back by uri", ReferenceEquals(sc.Store.GetTrack("spotify:track:1"), t1));
        Check("store: query filters by title (case-insensitive)", sc.Store.QueryTracks("alpha", TrackSort.Title).Count == 1);
        Check("store: sort by title", sc.Store.QueryTracks(null, TrackSort.Title)[0].Title == "Alpha Song");

        bool fired = false;
        var sub = sc.Store.Changes.Subscribe(Obs<StoreChange>(_ => fired = true));
        fired = false;   // ignore the BehaviorSubject replay of the last change on subscribe
        sc.Store.Bump("spotify:track:1");
        Check("store: bump fires a change signal", fired);
        sub.Dispose();

        // ── ⑤ SessionContext (gate) ──
        Check("session: premium can seek", sc.Session.Current.CanSeek);
        sc.Session.Set(sc.Session.Current with { Tier = Tier.Free });
        Check("session: free is shuffle-only", sc.Session.Current.ShuffleOnly && !sc.Session.Current.CanSeek);

        // ── ① Resource (stale-while-revalidate + in-flight dedup) ──
        var resFresh = new FreshnessPolicy.Etag(TimeSpan.FromMinutes(5));
        var res = new Resource<string, Track>((k, ctx) => Task.FromResult(Trk(k, "Fetched " + k, "X")), resFresh, () => sc.Session.Current);
        Check("resource: cold Use is Loading", res.Use("spotify:track:9").IsLoading);
        res.Revalidate("spotify:track:9").GetAwaiter().GetResult();
        Check("resource: Use is Ready after revalidate", res.Use("spotify:track:9").IsReady);

        int fetches = 0;
        var tcs = new TaskCompletionSource<Track>();
        var resDedup = new Resource<string, Track>((k, ctx) => { fetches++; return tcs.Task; }, resFresh, () => sc.Session.Current);
        var r1 = resDedup.Revalidate("k");   // starts the fetch (awaits the TCS — stays in-flight)
        var r2 = resDedup.Revalidate("k");   // sees in-flight → coalesces, no second fetch
        tcs.SetResult(Trk("k", "K", "K"));
        Task.WaitAll(r1, r2);
        Check("resource: concurrent revalidate dedups to one fetch", fetches == 1);

        // ── ② Mutation (optimistic + outbox + drain + reconcile) ──
        sc.Mutations.Save("liked", "spotify:track:1", true);
        Check("mutation: optimistic save reflects in the store", sc.Store.IsSaved("liked", "spotify:track:1"));
        Check("mutation: one pending outbox row", sc.Mutations.Pending == 1);
        sc.Mutations.Save("liked", "spotify:track:1", false);   // coalesce: same entity → still one row
        Check("mutation: re-save same entity coalesces (still 1 row)", sc.Mutations.Pending == 1);
        sc.Mutations.Save("liked", "spotify:track:1", true);
        sc.Mutations.Drain(sc.Transport, sc.Session.Current).GetAwaiter().GetResult();
        Check("mutation: drain clears the outbox", sc.Mutations.Pending == 0);
        Check("mutation: still saved after confirm", sc.Store.IsSaved("liked", "spotify:track:1"));

        // ── ④ QueueCore (pure) ──
        var t3 = Trk("spotify:track:3", "Gamma", "G");
        var qc = new QueueCore();
        qc.SetContext("spotify:playlist:p", new[] { t1, t2, t3 }, 0);
        Check("queue: starts at the start index", ReferenceEquals(qc.Current, t1));
        var ux = Trk("spotify:track:u", "User Pick", "U");
        qc.EnqueueUser(ux);
        qc.Next();
        Check("queue: the user-queue drains first", ReferenceEquals(qc.Current, ux));
        qc.Next();
        Check("queue: then the context advances (cursor unmoved by the user pop)", ReferenceEquals(qc.Current, t2));
        qc.SetRepeat(RepeatMode.Track);
        qc.Next();
        Check("queue: repeat-track holds the current track", ReferenceEquals(qc.Current, t2));

        // ── ④ Playback reducer + projections (+ stub audio) ──
        var hist = new HistoryProjection();
        sc.Playback.Subscribe(hist);
        sc.Playback.Play("spotify:playlist:p", new[] { t1, t2 });
        Check("playback: play -> audio engine is playing", sc.Audio.IsPlaying && sc.Audio.LastCmd == "play");
        Check("playback: history projection saw the start", hist.Plays >= 1);
        sc.Playback.Next();
        Check("playback: next -> projection counted a track change", hist.Plays >= 2);
        Check("playback: stub decrypt passthrough has the right shape", StubCrypto.Decrypt(new byte[] { 1, 2, 3 }, new byte[16]).Length == 3);

        // ── §7 seam adapters (engines → Wavee.Core facets) — driven THROUGH the seam interfaces ──
        IMutationSource ims = new EngineMutationSource(sc.Store, sc.Mutations, sc.Transport, () => sc.Session.Current, "liked2");
        Check("seam: IMutationSource declares Mutations + owns spotify uris", ims.Capabilities.HasFlag(SourceCapabilities.Mutations) && ims.Owns("spotify:track:1"));
        Check("seam: starts not-saved", !ims.IsSaved("spotify:track:7"));
        ims.SetSavedAsync("spotify:track:7", true).GetAwaiter().GetResult();
        Check("seam: SetSavedAsync flows through the engines (store + outbox + drain)", ims.IsSaved("spotify:track:7"));

        // a fresh Premium session (the scaffold's was flipped to Free above for the gating check)
        ISpotifySession sess = new EngineSessionSource(new SessionContextHost(new SessionContext("me", "US", "premium", "en", Tier.Premium, false)));
        Check("seam: session starts logged out", sess.Status == AuthStatus.LoggedOut);
        sess.ConnectAsync().GetAwaiter().GetResult();
        Check("seam: ConnectAsync → authenticated + a current user", sess.Status == AuthStatus.Authenticated && sess.CurrentUser != null);

        // ── premium-only gate: Wavee refuses Free outright ──
        Check("gate: premium is allowed", SessionGate.IsAllowed(Tier.Premium));
        Check("gate: free is refused", !SessionGate.IsAllowed(Tier.Free));
        ISpotifySession freeSess = new EngineSessionSource(new SessionContextHost(new SessionContext("f", "US", "premium", "en", Tier.Free, false)));
        bool freeConnected = freeSess.ConnectAsync().GetAwaiter().GetResult();
        Check("gate: a Free account is refused at connect (no launch)", !freeConnected && freeSess.Status == AuthStatus.Error);

        log.Info($"backend self-test: {pass} passed, {fail} failed");
        return fail == 0 ? 0 : 1;
    }

    static Track Trk(string uri, string title, string artist) =>
        new(EntityUri.IdOf(uri), uri, title,
            new[] { new ArtistRef(artist, "spotify:artist:" + artist, artist) },
            new AlbumRef("al", "spotify:album:al", "Album"),
            200_000, false, null);

    static IObserver<T> Obs<T>(Action<T> onNext) => new InlineObserver<T>(onNext);

    sealed class InlineObserver<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(T value) => onNext(value);
    }
}
