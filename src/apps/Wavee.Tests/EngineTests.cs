using Wavee.Backend;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

static class T
{
    public static Track Trk(string uri, string title, string artist, long durationMs = 200_000) =>
        new(uri[(uri.LastIndexOf(':') + 1)..], uri, title,
            [new ArtistRef(artist, "spotify:artist:" + artist, artist)],
            new AlbumRef("al", "spotify:album:al", "Album"), durationMs, false, null);
}

public class StoreTests
{
    [Fact]
    public void UpsertAndGet()
    {
        var s = new InMemoryStore();
        var t = T.Trk("spotify:track:1", "Alpha", "A");
        s.UpsertTrack(t);
        Assert.Same(t, s.GetTrack("spotify:track:1"));
        Assert.Null(s.GetTrack("spotify:track:none"));
    }

    [Fact]
    public void Query_FilterAndSort_CaseInsensitive()
    {
        var s = new InMemoryStore();
        s.UpsertTrack(T.Trk("spotify:track:1", "Beta", "Z"));
        s.UpsertTrack(T.Trk("spotify:track:2", "alpha", "A"));
        Assert.Single(s.QueryTracks("ALPHA"));
        var sorted = s.QueryTracks(null, TrackSort.Title);
        Assert.Equal("alpha", sorted[0].Title);   // NOCASE-ish ordinal-ignore-case
    }

    [Fact]
    public void Query_SortByDuration()
    {
        var s = new InMemoryStore();
        s.UpsertTrack(T.Trk("spotify:track:1", "Long", "A", 300_000));
        s.UpsertTrack(T.Trk("spotify:track:2", "Short", "A", 100_000));
        Assert.Equal("Short", s.QueryTracks(null, TrackSort.DurationAsc)[0].Title);
    }

    [Fact]
    public void Bump_FiresChangeSignal()
    {
        var s = new InMemoryStore();
        s.UpsertTrack(T.Trk("spotify:track:1", "A", "A"));
        bool fired = false;
        using var sub = s.Changes.Subscribe(new Obs(_ => fired = true));
        fired = false;   // discard the replay of the last change
        s.Bump("spotify:track:1");
        Assert.True(fired);
        Assert.True(s.Version("spotify:track:1") >= 2);
    }

    [Fact]
    public void SavedSet_Membership()
    {
        var s = new InMemoryStore();
        s.SetSaved("liked", "spotify:track:1", true, SyncState.Confirmed);
        Assert.True(s.IsSaved("liked", "spotify:track:1"));
        Assert.Contains("spotify:track:1", s.SavedUris("liked"));
        s.SetSaved("liked", "spotify:track:1", false, SyncState.Confirmed);
        Assert.False(s.IsSaved("liked", "spotify:track:1"));
    }

    sealed class Obs(Action<StoreChange> on) : IObserver<StoreChange>
    {
        public void OnCompleted() { }
        public void OnError(Exception e) { }
        public void OnNext(StoreChange v) => on(v);
    }
}

public class ResourceTests
{
    static SessionContext Ctx => new("me", "US", "premium", "en", Tier.Premium, false);

    [Fact]
    public async Task ColdUse_IsLoading_ThenReadyAfterRevalidate()
    {
        var r = new Resource<string, Track>((k, _) => Task.FromResult(T.Trk(k, "X", "X")),
            new FreshnessPolicy.Etag(TimeSpan.FromMinutes(5)), () => Ctx);
        Assert.True(r.Use("spotify:track:9").IsLoading);
        await r.Revalidate("spotify:track:9");
        var ld = r.Use("spotify:track:9");
        Assert.True(ld.IsReady);
        Assert.Equal("spotify:track:9", ld.Value!.Uri);
    }

    [Fact]
    public async Task ConcurrentRevalidate_DedupsToOneFetch()
    {
        int fetches = 0;
        var tcs = new TaskCompletionSource<Track>();
        var r = new Resource<string, Track>((k, _) => { fetches++; return tcs.Task; },
            new FreshnessPolicy.Etag(TimeSpan.FromMinutes(5)), () => Ctx);
        var a = r.Revalidate("k");
        var b = r.Revalidate("k");
        tcs.SetResult(T.Trk("k", "K", "K"));
        await Task.WhenAll(a, b);
        Assert.Equal(1, fetches);
    }
}

public class MutationTests
{
    static SessionContext Ctx => new("me", "US", "premium", "en", Tier.Premium, false);

    sealed class FailTransport : ITransport
    {
        public Task<Resp> Request(Channel ch, string route, ReadOnlyMemory<byte> body, CancellationToken ct = default,
            string? method = null, IReadOnlyDictionary<string, string>? headers = null)
            => Task.FromResult(new Resp(false, [], 500));
        public IObservable<WireEvent> Events(string p) => new SimpleSubject<WireEvent>();
        public IObservable<WireRequest> Requests(string p) => new SimpleSubject<WireRequest>();
        public Task Reply(string id, RequestResult result) => Task.CompletedTask;
        public Task<Resp> Publish(string deviceId, string connectionId, ReadOnlyMemory<byte> putState, CancellationToken ct = default) => Task.FromResult(new Resp(false, [], 500));
    }

    static MutationEngine Engine(IStore store) => new(store, [new SetReplayStrategy()]);

    [Fact]
    public void Save_IsOptimistic_AndOnePendingRow()
    {
        var store = new InMemoryStore();
        var m = Engine(store);
        m.Save("liked", "spotify:track:1", true);
        Assert.True(store.IsSaved("liked", "spotify:track:1"));
        Assert.Equal(1, m.Pending);
    }

    [Fact]
    public void Save_SameEntity_Coalesces()
    {
        var m = Engine(new InMemoryStore());
        m.Save("liked", "spotify:track:1", true);
        m.Save("liked", "spotify:track:1", false);
        m.Save("liked", "spotify:track:1", true);
        Assert.Equal(1, m.Pending);
    }

    [Fact]
    public async Task Drain_Success_Reconciles()
    {
        var store = new InMemoryStore();
        var m = Engine(store);
        m.Save("liked", "spotify:track:1", true);
        await m.Drain(new StubTransport(), Ctx);
        Assert.Equal(0, m.Pending);
        Assert.True(store.IsSaved("liked", "spotify:track:1"));
    }

    [Fact]
    public async Task Drain_TerminalFailure_DeadLettersAndRollsBack()
    {
        var store = new InMemoryStore();
        var clock = DateTime.UtcNow;
        var m = new MutationEngine(store, [new SetReplayStrategy()], null, () => clock);   // inject a clock so the §8.3 backoff is deterministic
        m.Save("liked", "spotify:track:1", true);
        var fail = new FailTransport();
        for (int i = 0; i < 10; i++) { await m.Drain(fail, Ctx); clock = clock.AddSeconds(120); }   // advance past the backoff each time → 10 attempts → terminal
        Assert.Single(m.DeadLetter);
        Assert.Equal(0, m.Pending);
        Assert.False(store.IsSaved("liked", "spotify:track:1"));   // rolled back
    }
}

public class QueueCoreTests
{
    static QueueCore WithContext(int count, int start = 0)
    {
        var q = new QueueCore();
        var tracks = new List<Track>();
        for (int i = 0; i < count; i++) tracks.Add(T.Trk($"spotify:track:{i}", $"t{i}", "A"));
        q.SetContext("spotify:playlist:p", tracks, start);
        return q;
    }

    [Fact]
    public void StartsAtIndex()
    {
        var q = WithContext(3, 1);
        Assert.Equal("t1", q.Current!.Title);
    }

    [Fact]
    public void UserQueue_DrainsFirst_WithoutAdvancingContextCursor()
    {
        var q = WithContext(3, 0);
        q.EnqueueUser(T.Trk("spotify:track:u", "user", "U"));
        Assert.Equal("user", q.Next()!.Title);   // user pick
        Assert.Equal("t1", q.Next()!.Title);      // context resumes at 1 (cursor wasn't moved by the user pop)
    }

    [Fact]
    public void RepeatTrack_Holds()
    {
        var q = WithContext(3, 0);
        q.SetRepeat(RepeatMode.Track);
        Assert.Equal("t0", q.Next()!.Title);
        Assert.Equal("t0", q.Next()!.Title);
    }

    [Fact]
    public void RepeatContext_WrapsAtEnd()
    {
        var q = WithContext(2, 0);
        q.SetRepeat(RepeatMode.Context);
        q.Next();                 // t1
        Assert.Equal("t0", q.Next()!.Title);   // wrap
    }

    [Fact]
    public void EndOfContext_ReturnsNull_WhenNoRepeat()
    {
        var q = WithContext(1, 0);
        Assert.Null(q.Next());
    }

    [Fact]
    public void Shuffle_AnchorsCurrentAtFront()
    {
        var q = WithContext(10, 3);
        var before = q.Current;
        q.SetShuffle(true);
        Assert.Same(before, q.Current);   // playing track stays current (logical 0)
    }

    [Fact]
    public void Shuffle_Off_RestoresNaturalOrder()
    {
        var q = WithContext(10, 0);   // playing t0
        q.SetShuffle(true);
        q.SetShuffle(false);          // un-shuffle must restore the natural order (was previously a no-op)
        Assert.Equal("t0", q.Current!.Title);
        var order = new List<string> { q.Current.Title };
        for (int i = 1; i < 10; i++) order.Add(q.Next()!.Title);
        Assert.Equal(new[] { "t0", "t1", "t2", "t3", "t4", "t5", "t6", "t7", "t8", "t9" }, order);
    }

    [Fact]
    public void SetContext_WhileShuffleOn_ShufflesTheNewContext()
    {
        var q = WithContext(2, 0);
        q.SetShuffle(true);
        var many = new List<Track>();
        for (int i = 0; i < 50; i++) many.Add(T.Trk($"spotify:track:n{i}", $"n{i}", "A"));
        q.SetContext("spotify:playlist:p2", many, 0);   // a context set under shuffle must be shuffled (was ignored)

        Assert.True(q.Shuffle);
        Assert.Same(many[0], q.Current);   // start track anchored at front
        var seen = new List<string> { q.Current!.Title };
        for (int i = 1; i < 50; i++) seen.Add(q.Next()!.Title);
        var natural = new List<string>();
        for (int i = 0; i < 50; i++) natural.Add($"n{i}");
        Assert.NotEqual(natural, seen);   // deterministic LCG shuffle of 50 is not the identity
    }

    [Fact]
    public void Snapshot_NowThenUserQueueThenContextNextUp()
    {
        var q = WithContext(3, 0);   // t0 current; t1, t2 upcoming
        q.EnqueueUser(T.Trk("spotify:track:u", "user", "U"));
        var snap = q.Snapshot();
        Assert.Equal(4, snap.Count);
        Assert.Equal(QueueBucket.NowPlaying, snap[0].Bucket); Assert.Equal("t0", snap[0].Track.Title);
        Assert.Equal(QueueBucket.UserQueue, snap[1].Bucket);  Assert.Equal("user", snap[1].Track.Title);
        Assert.Equal(QueueBucket.NextUp, snap[2].Bucket);     Assert.Equal("t1", snap[2].Track.Title);
        Assert.Equal(QueueBucket.NextUp, snap[3].Bucket);     Assert.Equal("t2", snap[3].Track.Title);
    }

    [Fact]
    public void Snapshot_ContextNextUp_IsCapped()
    {
        var q = WithContext(500, 0);
        Assert.True(q.Snapshot().Count <= 52);   // now + cap(50) + slack; never the full 500
    }
}

public class PlaybackReducerTests
{
    [Fact]
    public void Play_StartsAudio_AndProjectionSeesStart()
    {
        var audio = new StubAudioEngine();
        var reducer = new PlaybackReducer(audio);
        var hist = new HistoryProjection();
        reducer.Subscribe(hist);

        reducer.Play("spotify:playlist:p", [T.Trk("spotify:track:1", "a", "A"), T.Trk("spotify:track:2", "b", "A")]);

        Assert.True(reducer.IsPlaying);
        Assert.True(audio.IsPlaying);
        Assert.Equal("play", audio.LastCmd);
        Assert.Equal(1, hist.Plays);
    }

    [Fact]
    public void Next_AdvancesAndCountsProjection()
    {
        var reducer = new PlaybackReducer(new StubAudioEngine());
        var hist = new HistoryProjection();
        reducer.Subscribe(hist);
        reducer.Play("spotify:playlist:p", [T.Trk("spotify:track:1", "a", "A"), T.Trk("spotify:track:2", "b", "A")]);
        reducer.Next();
        Assert.Equal("b", reducer.Current!.Title);
        Assert.Equal(2, hist.Plays);
    }
}

public class SessionContextTests
{
    [Fact]
    public void Premium_CanSeek_NotShuffleOnly()
    {
        var s = new SessionContext("me", "US", "premium", "en", Tier.Premium, false);
        Assert.True(s.CanSeek);
        Assert.False(s.ShuffleOnly);
    }

    [Fact]
    public void Free_ShuffleOnly_CannotSeek()
    {
        var s = new SessionContext("me", "US", "premium", "en", Tier.Free, false);
        Assert.False(s.CanSeek);
        Assert.True(s.ShuffleOnly);
    }

    [Fact]
    public void Host_Set_PublishesAndUpdatesCurrent()
    {
        var host = new SessionContextHost(SessionContext.LoggedOut);
        host.Set(host.Current with { Tier = Tier.Premium });
        Assert.Equal(Tier.Premium, host.Current.Tier);
    }
}
