using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Collections;
using Xunit;
using Col = Wavee.Protocol.Collection;

namespace Wavee.Tests;

// §2.4 (RC4) — the real collection write. SetReplayStrategy.Replay must POST /collection/v2/write with the vendor media
// type (Content-Type AND Accept), an explicit POST method, and a WriteRequest body carrying the wire set, the inverted
// is_removed flag, an added_at in UNIX SECONDS, and a non-empty client_update_id. Asserted via StubTransport.LastRequest*.
public class CollectionWriteTests
{
    static SessionContext Ctx => new("bob", "US", "premium", "en", Tier.Premium, false);

    [Fact]
    public async Task Replay_LikedTrack_PostsVendorWrite_WithWireSetAndSecondsTimestamp()
    {
        var strat = new SetReplayStrategy(new CollectionEchoRing());
        var t = new StubTransport();
        var op = new OutboxOp(1, "set", "spotify:track:x", "liked", true, 1, 0);

        var ok = await strat.Replay(op, t, Ctx, TestContext.Current.CancellationToken);

        Assert.True(ok);
        Assert.Equal("/collection/v2/write", t.LastRequestRoute);
        Assert.Equal("POST", t.LastRequestMethod);                 // explicit — not body-empty inference
        Assert.Equal("application/vnd.collection-v2.spotify.proto", t.LastRequestHeaders!["Content-Type"]);
        Assert.Equal("application/vnd.collection-v2.spotify.proto", t.LastRequestHeaders!["Accept"]);

        var wr = Col.WriteRequest.Parser.ParseFrom(t.LastRequestBody);
        Assert.Equal("bob", wr.Username);                          // from ctx.Account
        Assert.Equal("collection", wr.Set);                       // liked → the "collection" wire set
        var item = Assert.Single(wr.Items);
        Assert.Equal("spotify:track:x", item.Uri);
        Assert.False(item.IsRemoved);                             // saved → NOT removed
        Assert.True(item.AddedAt > 1_600_000_000);               // seconds sanity range (post-2020), not ms
        Assert.False(string.IsNullOrEmpty(wr.ClientUpdateId));
    }

    [Fact]
    public async Task Replay_ArtistFollow_UsesArtistWireSet()
    {
        var strat = new SetReplayStrategy();
        var t = new StubTransport();
        var op = new OutboxOp(2, "set", "spotify:artist:y", "artists", true, 2, 0);

        await strat.Replay(op, t, Ctx, TestContext.Current.CancellationToken);

        var wr = Col.WriteRequest.Parser.ParseFrom(t.LastRequestBody);
        Assert.Equal("artist", wr.Set);                          // artists → the "artist" wire set
        Assert.Equal("spotify:artist:y", Assert.Single(wr.Items).Uri);
    }

    [Fact]
    public async Task Replay_Unsave_InvertsIsRemoved()
    {
        var strat = new SetReplayStrategy();
        var t = new StubTransport();
        var op = new OutboxOp(3, "set", "spotify:track:x", "liked", false, 3, 0);

        await strat.Replay(op, t, Ctx, TestContext.Current.CancellationToken);

        Assert.True(Col.WriteRequest.Parser.ParseFrom(t.LastRequestBody).Items[0].IsRemoved);   // unsaved → is_removed
    }

    [Fact]
    public async Task Replay_AcceptedWrite_RecordsCuidInEchoRing()
    {
        var ring = new CollectionEchoRing();
        var strat = new SetReplayStrategy(ring);
        var t = new StubTransport();   // returns Ok

        await strat.Replay(new OutboxOp(4, "set", "spotify:track:x", "liked", true, 4, 0), t, Ctx,
            TestContext.Current.CancellationToken);

        var cuid = Col.WriteRequest.Parser.ParseFrom(t.LastRequestBody).ClientUpdateId;
        Assert.True(ring.Contains(cuid));   // an accepted write registers its echo id
    }
}
