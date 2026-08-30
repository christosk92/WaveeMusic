using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Spotify;
using Wavee.SpotifyLive.Audio;
using Xunit;

namespace Wavee.Tests.Audio;

public class HeadFileClientTests
{
    [Fact]
    public async Task SequentialGets_SameFile_OneHttpCall()
    {
        var body = new byte[200];
        new Random(1).NextBytes(body);
        int calls = 0;
        var http = new CountingHttpExchange(req =>
        {
            Interlocked.Increment(ref calls);
            return new HttpResp(200, new Dictionary<string, string>(), body);
        });
        var client = new HeadFileClient(http, A.Ctx);

        var h1 = await client.GetAsync("abc123");
        var h2 = await client.GetAsync("abc123");

        Assert.Equal(1, calls);
        Assert.Equal(h1.Data, h2.Data);
    }

    [Fact]
    public async Task FailedFetch_RetriesOnNextGet()
    {
        int calls = 0;
        var okBody = new byte[200];
        var http = new CountingHttpExchange(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1) return new HttpResp(500, new Dictionary<string, string>(), Array.Empty<byte>());
            return new HttpResp(200, new Dictionary<string, string>(), okBody);
        });
        var client = new HeadFileClient(http, A.Ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("deadbeef"));
        var head = await client.GetAsync("deadbeef");

        Assert.Equal(2, calls);
        Assert.Equal(okBody.Length, head.Data.Length);
    }

    sealed class CountingHttpExchange(Func<HttpReq, HttpResp> responder) : IHttpExchange
    {
        public Task<HttpResp> SendAsync(HttpReq req, CancellationToken ct) => Task.FromResult(responder(req));
    }
}
