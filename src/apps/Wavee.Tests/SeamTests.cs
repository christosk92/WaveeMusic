using Wavee.Backend;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Xunit;
using Col = Wavee.Protocol.Collection;

namespace Wavee.Tests;

public class PremiumGateTests
{
    [Fact]
    public void Premium_IsAllowed() => Assert.True(SessionGate.IsAllowed(Tier.Premium));

    [Fact]
    public void Free_IsRefused() => Assert.False(SessionGate.IsAllowed(Tier.Free));

    [Fact]
    public void WarningText_IsPresentable()
    {
        Assert.False(string.IsNullOrWhiteSpace(SessionGate.WarningTitle));
        Assert.Contains("Premium", SessionGate.WarningBody);
    }
}

public class SeamAdapterTests
{
    [Fact]
    public async Task EngineMutationSource_SetSaved_FlowsThroughEngines()
    {
        var store = new InMemoryStore();
        await using var host = new ReplicaTestHost(account: "me", store: store);
        var mut = host.Mutations;
        var ctx = new SessionContext("me", "US", "premium", "en", Tier.Premium, false);
        var stub = new CollectionTransport();
        IMutationSource ims = new EngineMutationSource(store, mut, host.AttachSync(new FakeExchange((_, _) => SyncHarness.Ok([])), stub), "liked");

        Assert.True(ims.Capabilities.HasFlag(SourceCapabilities.Mutations));
        Assert.True(ims.Owns("spotify:track:1"));
        Assert.False(ims.Owns("local:file:x"));
        Assert.False(ims.IsSaved("spotify:track:7"));

        await ims.SetSavedAsync("spotify:track:7", true);

        Assert.True(ims.IsSaved("spotify:track:7"));           // Pending → Confirmed after the drain
        Assert.Contains("spotify:track:7", ims.Saved);
        Assert.Equal(0, mut.Pending);                          // drained + reconciled

        // the drain hit the REAL collection write route/body (§2.4), not the old /collection/{set}/{verb}/{uri} GET.
        Assert.Equal("/collection/v2/write", stub.LastRequestRoute);
        Assert.Equal("POST", stub.LastRequestMethod);
        var wr = Col.WriteRequest.Parser.ParseFrom(stub.LastRequestBody);
        Assert.Equal("collection", wr.Set);                   // track → liked → "collection" wire set
        Assert.Equal("spotify:track:7", Assert.Single(wr.Items).Uri);
        Assert.False(wr.Items[0].IsRemoved);
    }

    [Fact]
    public async Task EngineMutationSource_MultiSet_RoutesByUriKind()
    {
        var store = new InMemoryStore();
        await using var host = new ReplicaTestHost(account: "me", store: store);
        var mut = host.Mutations;
        var ctx = new SessionContext("me", "US", "premium", "en", Tier.Premium, false);
        var src = new EngineMutationSource(store, mut, host.AttachSync(new FakeExchange((_, _) => SyncHarness.Ok([])), new CollectionTransport()));

        await src.SetSavedAsync("spotify:track:t", true);
        await src.SetSavedAsync("spotify:album:a", true);
        await src.SetSavedAsync("spotify:artist:r", true);

        Assert.True(store.IsSaved("liked", "spotify:track:t"));     // track → liked
        Assert.True(store.IsSaved("albums", "spotify:album:a"));    // album → albums
        Assert.True(store.IsSaved("artists", "spotify:artist:r"));  // artist → artists
        Assert.True(src.IsSaved("spotify:track:t"));                // one aggregated snapshot across sets
        Assert.True(src.IsSaved("spotify:album:a"));
        Assert.True(src.IsSaved("spotify:artist:r"));
    }

    [Fact]
    public async Task EngineMutationSource_SavedChanged_Emits()
    {
        var store = new InMemoryStore();
        await using var host = new ReplicaTestHost(account: "me", store: store);
        var mut = host.Mutations;
        var ctx = new SessionContext("me", "US", "premium", "en", Tier.Premium, false);
        var src = new EngineMutationSource(store, mut, host.AttachSync(new FakeExchange((_, _) => SyncHarness.Ok([])), new CollectionTransport()), "liked");

        int emissions = 0;
        using var sub = src.SavedChanged.Subscribe(new Obs(_ => emissions++));
        emissions = 0;   // discard the initial replay
        await src.SetSavedAsync("spotify:track:9", true);
        Assert.True(emissions >= 1);
    }

    [Fact]
    public async Task EngineSessionSource_Premium_Authenticates()
    {
        var host = new SessionContextHost(new SessionContext("me", "US", "premium", "en", Tier.Premium, false));
        ISpotifySession sess = new EngineSessionSource(host);
        Assert.Equal(AuthStatus.LoggedOut, sess.Status);
        Assert.True(await sess.ConnectAsync());
        Assert.Equal(AuthStatus.Authenticated, sess.Status);
    }

    [Fact]
    public async Task EngineSessionSource_Free_Refused()
    {
        var host = new SessionContextHost(new SessionContext("me", "US", "premium", "en", Tier.Free, false));
        ISpotifySession sess = new EngineSessionSource(host);
        Assert.False(await sess.ConnectAsync());
        Assert.Equal(AuthStatus.Error, sess.Status);
    }

    sealed class CollectionTransport : ITransport
    {
        public string? LastRequestRoute, LastRequestMethod;
        public byte[] LastRequestBody = [];
        public Task<Resp> Request(Channel channel, string route, ReadOnlyMemory<byte> body, CancellationToken ct = default,
            string? method = null, IReadOnlyDictionary<string, string>? headers = null)
        {
            LastRequestRoute = route; LastRequestMethod = method; LastRequestBody = body.ToArray();
            return Task.FromResult(new Resp(true, [], 200));
        }
        public IObservable<WireEvent> Events(string prefix) => new SimpleSubject<WireEvent>();
        public IObservable<WireRequest> Requests(string prefix) => new SimpleSubject<WireRequest>();
        public Task Reply(string id, RequestResult result) => Task.CompletedTask;
        public Task<Resp> Publish(string device, string connection, ReadOnlyMemory<byte> state, CancellationToken ct = default)
            => Task.FromResult(new Resp(true, [], 200));
    }

    sealed class Obs(Action<IReadOnlySet<string>> on) : IObserver<IReadOnlySet<string>>
    {
        public void OnCompleted() { }
        public void OnError(Exception e) { }
        public void OnNext(IReadOnlySet<string> v) => on(v);
    }
}
