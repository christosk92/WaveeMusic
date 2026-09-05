using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Playlists;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// PlaylistExtenderClient.ExtendAsync over a scripted transport. There were no tests for this client at all — the
// silent-failure paths (an empty 200, a malformed body, a swallowed cancellation) were exactly what let a stalled
// recs fetch wedge the whole section forever (see RecsRefetchPolicyTests / DetailTracks.FetchRecs).
public class PlaylistExtenderClientTests
{
    sealed class ScriptedTransport(Func<CancellationToken, Resp> respond) : ITransport
    {
        public Task<Resp> Request(Channel ch, string route, ReadOnlyMemory<byte> body, CancellationToken ct = default,
            string? method = null, IReadOnlyDictionary<string, string>? headers = null)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(respond(ct));
        }
        public IObservable<WireEvent> Events(string topicPrefix) => new SimpleSubject<WireEvent>();
        public IObservable<WireRequest> Requests(string identPrefix) => new SimpleSubject<WireRequest>();
        public Task Reply(string requestId, RequestResult result) => Task.CompletedTask;
        public Task<Resp> Publish(string deviceId, string connectionId, ReadOnlyMemory<byte> putState, CancellationToken ct = default)
            => Task.FromResult(new Resp(true, Array.Empty<byte>(), 200));
    }

    const string ValidBody =
        """
        {"recommendedTracks":[
            {"id":"t1","originalId":"spotify:track:t1","name":"Song One",
             "artists":[{"id":"a1","name":"Artist One"}],
             "album":{"id":"al1","name":"Album One","imageUrl":"https://img/1"},
             "duration":180000,"explicit":false},
            {"id":"t2","originalId":"spotify:track:t2","name":"Song Two",
             "artists":[{"id":"a2","name":"Artist Two"}],
             "album":{"id":"al2","name":"Album Two","imageUrl":"https://img/2"},
             "duration":200000,"explicit":true}
        ]}
        """;

    [Fact]
    public async Task ValidBody_ReturnsMappedTracks()
    {
        var t = new ScriptedTransport(_ => new Resp(true, Encoding.UTF8.GetBytes(ValidBody), 200));
        var client = new PlaylistExtenderClient(t);

        var tracks = await client.ExtendAsync("spotify:playlist:p", Array.Empty<string>(), 20);

        Assert.Equal(2, tracks.Count);
        Assert.Equal("t1", tracks[0].Id);
        Assert.Equal("spotify:track:t1", tracks[0].Uri);
        Assert.Equal("Song One", tracks[0].Title);
        Assert.False(tracks[0].IsExplicit);
        Assert.True(tracks[1].IsExplicit);
    }

    [Fact]
    public async Task EmptyBody_ReturnsEmptyList_NotAnException()
    {
        var t = new ScriptedTransport(_ => new Resp(true, Array.Empty<byte>(), 200));
        var client = new PlaylistExtenderClient(t);

        var tracks = await client.ExtendAsync("spotify:playlist:p", Array.Empty<string>(), 20);

        Assert.Empty(tracks);
    }

    [Fact]
    public async Task MalformedBody_ReturnsEmptyList_NotAnException()
    {
        var t = new ScriptedTransport(_ => new Resp(true, Encoding.UTF8.GetBytes("{not json"), 200));
        var client = new PlaylistExtenderClient(t);

        var tracks = await client.ExtendAsync("spotify:playlist:p", Array.Empty<string>(), 20);

        Assert.Empty(tracks);
    }

    [Fact]
    public async Task NonOkStatus_ReturnsEmptyList()
    {
        var t = new ScriptedTransport(_ => new Resp(false, Array.Empty<byte>(), 500));
        var client = new PlaylistExtenderClient(t);

        var tracks = await client.ExtendAsync("spotify:playlist:p", Array.Empty<string>(), 20);

        Assert.Empty(tracks);
    }

    // The client must NOT swallow a cancellation — that is what lets FetchRecs tell a genuine supersede/unmount apart
    // from a completed result (see DetailTracks.FetchRecs: epoch check vs. OperationCanceledException).
    [Fact]
    public async Task Cancellation_Propagates_IsNotSwallowed()
    {
        var t = new ScriptedTransport(_ => new Resp(true, Encoding.UTF8.GetBytes(ValidBody), 200));
        var client = new PlaylistExtenderClient(t);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.ExtendAsync("spotify:playlist:p", Array.Empty<string>(), 20, cts.Token));
    }
}
