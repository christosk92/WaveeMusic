using System.Net;
using System.Net.Http.Headers;
using Wavee.Backend.Audio;
using Xunit;
using Wavee.Sdk.Streams;

namespace Wavee.Tests.Audio;

public class RangedHttpSourceRecoveryTests
{
    [Fact]
    public void TransientFailures_RetryAndPublishSingleRecoveryLifecycle()
    {
        var body = A.Bytes(3, 100_000);
        var handler = new FlakyRangeHandler(body, failuresBeforeSuccess: 2);
        using var http = new HttpClient(handler);
        var events = new List<AudioNetworkRecoveryEvent>();
        var policy = new RangedHttpRecoveryPolicy(VisibilityMs: 0, BudgetMs: 2_000,
            AttemptTimeoutMs: 250, Jitter: _ => 0);
        using var source = new RangedHttpSource(http, "track", default, 0, null,
            maxRetries: 1, onRecovery: events.Add, recoveryPolicy: policy);
        source.Configure(["https://cdn.test/audio"], body.Length);

        source.EnsureRange(0, 1024);
        var actual = new byte[1024];
        source.ReadRaw(0, actual, 0, actual.Length);

        Assert.Equal(body.AsSpan(0, actual.Length).ToArray(), actual);
        Assert.Equal(3, handler.Calls);
        Assert.Single(events, e => e.Stage == AudioNetworkRecoveryStage.Started);
        Assert.Single(events, e => e.Stage == AudioNetworkRecoveryStage.Recovered);
    }

    [Fact]
    public void ContinuousNetworkFailure_StopsAtConfiguredBudgetWithTypedNetworkError()
    {
        var handler = new FlakyRangeHandler(new byte[100_000], failuresBeforeSuccess: int.MaxValue);
        using var http = new HttpClient(handler);
        var events = new List<AudioNetworkRecoveryEvent>();
        var policy = new RangedHttpRecoveryPolicy(VisibilityMs: 0, BudgetMs: 120,
            AttemptTimeoutMs: 30, Jitter: _ => 0);
        using var source = new RangedHttpSource(http, "track", default, 0, null,
            maxRetries: 1, onRecovery: events.Add, recoveryPolicy: policy);
        source.Configure(["https://cdn.test/audio"], 100_000);

        var error = Assert.Throws<AudioRangeFetchException>(() => source.EnsureRange(0, 1024));

        Assert.Equal(StreamFailureReason.Network, error.Reason);
        Assert.Contains(events, e => e.Stage == AudioNetworkRecoveryStage.Exhausted);
    }

    sealed class FlakyRangeHandler(byte[] body, int failuresBeforeSuccess) : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int call = Interlocked.Increment(ref Calls);
            if (call <= failuresBeforeSuccess)
                throw new HttpRequestException("dns failed");

            long from = request.Headers.Range?.Ranges.FirstOrDefault()?.From ?? 0;
            long to = Math.Min(request.Headers.Range?.Ranges.FirstOrDefault()?.To ?? body.Length - 1, body.Length - 1);
            var bytes = body.AsSpan((int)from, checked((int)(to - from + 1))).ToArray();
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(bytes) };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, body.Length);
            return Task.FromResult(response);
        }
    }
}
