using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Wavee.Core.Http;
using Wavee.Core.Http.Pathfinder;
using Wavee.UI.Services;
using Xunit;

namespace Wavee.UI.Tests.Services;

/// <summary>
/// Tests for TrackColorHintService — validates debounced/batched color resolution
/// used by virtualized track lists (liked songs, playlists, search).
///
/// WHY: Wrong behavior here causes:
/// - Duplicate backend calls on scroll bursts (dedup broken)
/// - Stampede on negative responses (negative caching broken)
/// - UI rows stuck on neutral even after backend returns color (TCS plumbing broken)
/// - Thousands of single-item requests instead of one ~50-item batch (debounce broken)
/// </summary>
public sealed class TrackColorHintServiceTests
{
    // Small debounce + batch size keep the test suite fast while still exercising the loop.
    private static readonly TimeSpan TestDebounce = TimeSpan.FromMilliseconds(30);
    private const int TestBatchSize = 50;

    // ───────────────────────── Basic resolution / caching ─────────────────────────

    [Fact]
    public void TryGet_BeforeResolution_ReturnsFalse()
    {
        var fake = new FakeColorService();
        using var svc = new DisposableService(new TrackColorHintService(fake, TestDebounce, TestBatchSize));

        svc.Inner.TryGet("https://example.com/a.jpg", out var hex).Should().BeFalse();
        hex.Should().BeNull();
    }

    [Fact]
    public async Task GetOrResolveAsync_NewUrl_FetchesColor()
    {
        var fake = new FakeColorService();
        fake.Seed["url-A"] = new ExtractedColor(DarkHex: "#AA0000", LightHex: null, RawHex: null);
        await using var svc = new TrackColorHintService(fake, TestDebounce, TestBatchSize);

        var hex = await svc.GetOrResolveAsync("url-A");

        hex.Should().Be("#AA0000");
        fake.TotalCalls.Should().Be(1);
    }

    [Fact]
    public async Task GetOrResolveAsync_CachedValue_CompletesSynchronously()
    {
        var fake = new FakeColorService();
        fake.Seed["url-A"] = new ExtractedColor("#AA0000", null, null);
        await using var svc = new TrackColorHintService(fake, TestDebounce, TestBatchSize);

        // First call populates the cache
        await svc.GetOrResolveAsync("url-A");

        // Second call should complete synchronously (same-frame)
        var secondCall = svc.GetOrResolveAsync("url-A");

        secondCall.IsCompletedSuccessfully.Should().BeTrue(
            "cache hits should short-circuit the async path");
        secondCall.Result.Should().Be("#AA0000");
        fake.TotalCalls.Should().Be(1, "no extra backend call on cache hit");
    }

    [Fact]
    public async Task TryGet_AfterResolution_ReturnsHex()
    {
        var fake = new FakeColorService();
        fake.Seed["url-A"] = new ExtractedColor("#AA", null, null);
        await using var svc = new TrackColorHintService(fake, TestDebounce, TestBatchSize);

        await svc.GetOrResolveAsync("url-A");

        svc.TryGet("url-A", out var hex).Should().BeTrue();
        hex.Should().Be("#AA");
    }

    [Fact]
    public async Task EmptyUrl_ReturnsNullAndDoesNotCallBackend()
    {
        var fake = new FakeColorService();
        await using var svc = new TrackColorHintService(fake, TestDebounce, TestBatchSize);

        var result = await svc.GetOrResolveAsync("");

        result.Should().BeNull();
        fake.TotalCalls.Should().Be(0);
    }

    // ───────────────────────── Dedup ─────────────────────────

    [Fact]
    public async Task ConcurrentRequestsForSameUrl_IssueOnlyOneBackendCall()
    {
        var fake = new FakeColorService();
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.OnBatch = async urls =>
        {
            await gate.Task;
            return urls.ToDictionary(
                u => u,
                u => new ExtractedColor("#CAFE", null, null));
        };

        await using var svc = new TrackColorHintService(fake, TestDebounce, TestBatchSize);

        // Fire 10 concurrent requests for the same URL. They must all share one backend call.
        const string url = "url-shared";
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => svc.GetOrResolveAsync(url).AsTask())
            .ToArray();

        // Let the debounce window elapse so the worker enters the backend call.
        await Task.Delay(TestDebounce + TimeSpan.FromMilliseconds(20));

        // Release the backend.
        gate.SetResult(true);

        var results = await Task.WhenAll(tasks);

        results.Should().OnlyContain(hex => hex == "#CAFE");
        fake.TotalCalls.Should().Be(1, "10 concurrent requests for the same URL must dedupe to one backend call");

        var firstBatch = fake.BatchCalls.ToArray()[0];
        firstBatch.Should().ContainSingle().Which.Should().Be(url);
    }

    // ───────────────────────── Batching / debounce ─────────────────────────

    [Fact]
    public async Task MultipleUrlsWithinDebounceWindow_AreBatchedIntoOneCall()
    {
        var fake = new FakeColorService();
        for (var i = 0; i < 5; i++)
            fake.Seed[$"url-{i}"] = new ExtractedColor($"#C{i:X}", null, null);

        // Use a generous debounce so 5 synchronous enqueues easily fit into one window.
        await using var svc = new TrackColorHintService(
            fake,
            debounceWindow: TimeSpan.FromMilliseconds(100),
            batchSize: 50);

        // Fire all 5 requests in a tight loop — all should land inside the soak window.
        var tasks = Enumerable.Range(0, 5)
            .Select(i => svc.GetOrResolveAsync($"url-{i}").AsTask())
            .ToArray();

        await Task.WhenAll(tasks);

        fake.TotalCalls.Should().Be(1, "5 rapid URLs within the debounce window should batch into one backend call");
        var batch = fake.BatchCalls.ToArray()[0];
        batch.Should().HaveCount(5);
        batch.Should().BeEquivalentTo(new[] { "url-0", "url-1", "url-2", "url-3", "url-4" });
    }

    [Fact]
    public async Task UrlAfterDebounceExpires_StartsNewBatch()
    {
        var fake = new FakeColorService();
        fake.Seed["url-A"] = new ExtractedColor("#AA", null, null);
        fake.Seed["url-B"] = new ExtractedColor("#BB", null, null);

        await using var svc = new TrackColorHintService(
            fake,
            debounceWindow: TimeSpan.FromMilliseconds(20),
            batchSize: 50);

        await svc.GetOrResolveAsync("url-A");

        // After the first batch has fully resolved, wait clearly past the debounce window.
        await Task.Delay(60);

        await svc.GetOrResolveAsync("url-B");

        fake.TotalCalls.Should().Be(2, "requests separated by more than the debounce window should NOT be batched");
        var batches = fake.BatchCalls.ToArray();
        batches[0].Should().ContainSingle().Which.Should().Be("url-A");
        batches[1].Should().ContainSingle().Which.Should().Be("url-B");
    }

    [Fact]
    public async Task BatchSizeCap_SplitsIntoMultipleBatches()
    {
        var fake = new FakeColorService();
        for (var i = 0; i < 5; i++)
            fake.Seed[$"url-{i}"] = new ExtractedColor($"#0{i:X}", null, null);

        // Batch size 2 → 5 URLs should split across 3 batches: [0,1] [2,3] [4]
        await using var svc = new TrackColorHintService(
            fake,
            debounceWindow: TimeSpan.FromMilliseconds(100),
            batchSize: 2);

        var tasks = Enumerable.Range(0, 5)
            .Select(i => svc.GetOrResolveAsync($"url-{i}").AsTask())
            .ToArray();

        await Task.WhenAll(tasks);

        fake.TotalCalls.Should().Be(3, "batch size 2 across 5 URLs should produce 3 backend calls");
        var batches = fake.BatchCalls.ToArray();
        batches[0].Should().HaveCount(2);
        batches[1].Should().HaveCount(2);
        batches[2].Should().HaveCount(1);

        // All distinct URLs covered exactly once.
        batches.SelectMany(b => b).Should().BeEquivalentTo(new[] { "url-0", "url-1", "url-2", "url-3", "url-4" });
    }

    // ───────────────────────── Failure / negative caching ─────────────────────────

    [Fact]
    public async Task BackendFailure_ReturnsNullAndNegativeCaches()
    {
        var fake = new FakeColorService
        {
            ThrowOnBatch = new InvalidOperationException("backend down")
        };
        await using var svc = new TrackColorHintService(fake, TestDebounce, TestBatchSize);

        var first = await svc.GetOrResolveAsync("url-A");
        first.Should().BeNull("failed backend call should return null gracefully");

        // Even after the backend "recovers", the negative cache should prevent a refetch.
        fake.ThrowOnBatch = null;
        fake.Seed["url-A"] = new ExtractedColor("#AA", null, null);

        var second = await svc.GetOrResolveAsync("url-A");
        second.Should().BeNull("negative cache should prevent retry even after the backend is healthy");

        fake.TotalCalls.Should().Be(1, "negative cache must stop subsequent backend calls for the same URL");
    }

    // ───────────────────────── Cancellation ─────────────────────────

    [Fact]
    public async Task Cancellation_BeforeBatch_CancelsAwaiter()
    {
        var fake = new FakeColorService();
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.OnBatch = async urls =>
        {
            await gate.Task;
            return urls.ToDictionary(u => u, u => new ExtractedColor("#CAFE", null, null));
        };

        await using var svc = new TrackColorHintService(fake, TestDebounce, TestBatchSize);

        using var cts = new CancellationTokenSource();
        var task = svc.GetOrResolveAsync("url-A", cts.Token).AsTask();

        cts.Cancel();

        var act = async () => await task;
        await act.Should().ThrowAsync<OperationCanceledException>();

        // Unblock the backend so the worker can clean up.
        gate.SetResult(true);
    }

    [Fact]
    public async Task Cancellation_DoesNotAffectOtherAwaitersOfSameUrl()
    {
        var fake = new FakeColorService();
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.OnBatch = async urls =>
        {
            await gate.Task;
            return urls.ToDictionary(u => u, u => new ExtractedColor("#CAFE", null, null));
        };

        await using var svc = new TrackColorHintService(fake, TestDebounce, TestBatchSize);

        using var cts = new CancellationTokenSource();
        var cancelled = svc.GetOrResolveAsync("url-A", cts.Token).AsTask();
        var living = svc.GetOrResolveAsync("url-A").AsTask();

        cts.Cancel();
        gate.SetResult(true);

        var act = async () => await cancelled;
        await act.Should().ThrowAsync<OperationCanceledException>();

        var result = await living;
        result.Should().Be("#CAFE", "other awaiters of the same URL should be unaffected by one cancellation");
    }

    // ───────────────────────── Color preference ─────────────────────────

    [Fact]
    public async Task ColorPreference_PrefersDarkOverRawOverLight()
    {
        var fake = new FakeColorService();
        fake.Seed["dark-only"] = new ExtractedColor(DarkHex: "#DD", LightHex: null, RawHex: null);
        fake.Seed["raw-only"] = new ExtractedColor(DarkHex: null, LightHex: null, RawHex: "#RR");
        fake.Seed["light-only"] = new ExtractedColor(DarkHex: null, LightHex: "#LL", RawHex: null);
        fake.Seed["all-three"] = new ExtractedColor(DarkHex: "#DD", LightHex: "#LL", RawHex: "#RR");

        await using var svc = new TrackColorHintService(
            fake,
            TimeSpan.FromMilliseconds(40),
            batchSize: 10);

        // Submit all four together so we exercise one batch, then read results from cache.
        var tasks = new[]
        {
            svc.GetOrResolveAsync("dark-only").AsTask(),
            svc.GetOrResolveAsync("raw-only").AsTask(),
            svc.GetOrResolveAsync("light-only").AsTask(),
            svc.GetOrResolveAsync("all-three").AsTask(),
        };
        var results = await Task.WhenAll(tasks);

        results[0].Should().Be("#DD");
        results[1].Should().Be("#RR");
        results[2].Should().Be("#LL");
        results[3].Should().Be("#DD", "dark wins when all three are present");

        fake.TotalCalls.Should().Be(1, "all four URLs should batch into one backend call");
    }

    // ───────────────────────── Concurrency smoke test ─────────────────────────

    [Fact]
    public async Task ManyConcurrentDistinctUrls_AllResolve()
    {
        var fake = new FakeColorService();
        const int n = 100;
        for (var i = 0; i < n; i++)
            fake.Seed[$"url-{i}"] = new ExtractedColor($"#{i:X4}", null, null);

        await using var svc = new TrackColorHintService(
            fake,
            TimeSpan.FromMilliseconds(50),
            batchSize: 30);

        var tasks = Enumerable.Range(0, n)
            .Select(i => Task.Run(async () => await svc.GetOrResolveAsync($"url-{i}")))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        results.Should().HaveCount(n);
        results.Should().OnlyContain(hex => hex != null);

        // Every URL should have been fetched exactly once across all batches.
        var allFetched = fake.BatchCalls.SelectMany(b => b).ToList();
        allFetched.Should().HaveCount(n);
        allFetched.Distinct().Should().HaveCount(n);
    }

    // ───────────────────────── Disposal ─────────────────────────

    [Fact]
    public async Task DisposeAsync_PendingAwaiters_CompleteWithCancellation()
    {
        var fake = new FakeColorService();
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.OnBatch = async urls =>
        {
            await gate.Task;
            return new Dictionary<string, ExtractedColor>();
        };

        var svc = new TrackColorHintService(fake, TestDebounce, TestBatchSize);

        var pending = svc.GetOrResolveAsync("url-A").AsTask();

        await svc.DisposeAsync();

        // Release the fake gate so nothing leaks.
        gate.TrySetResult(true);

        var act = async () => await pending;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ───────────────────────── Fake IColorService ─────────────────────────

    private sealed class FakeColorService : IColorService
    {
        public ConcurrentQueue<IReadOnlyList<string>> BatchCalls { get; } = new();
        public int TotalCalls => BatchCalls.Count;
        public Dictionary<string, ExtractedColor> Seed { get; } = new();
        public Func<IReadOnlyList<string>, Task<Dictionary<string, ExtractedColor>>>? OnBatch { get; set; }
        public Exception? ThrowOnBatch { get; set; }

        public Task<ExtractedColor?> GetColorAsync(string imageUrl, CancellationToken ct = default)
            => throw new NotSupportedException(
                "TrackColorHintService is expected to use GetColorsAsync (batched) exclusively.");

        public async Task<Dictionary<string, ExtractedColor>> GetColorsAsync(
            IReadOnlyList<string> imageUrls,
            CancellationToken ct = default)
        {
            // Snapshot the list at record time so later mutations don't confuse assertions.
            BatchCalls.Enqueue(imageUrls.ToList());

            if (ThrowOnBatch != null)
                throw ThrowOnBatch;

            if (OnBatch != null)
                return await OnBatch(imageUrls).ConfigureAwait(false);

            var result = new Dictionary<string, ExtractedColor>();
            foreach (var url in imageUrls)
            {
                if (Seed.TryGetValue(url, out var c))
                    result[url] = c;
            }
            return result;
        }
    }

    // Tiny wrapper so synchronous tests get deterministic disposal without 'await using'.
    private sealed class DisposableService : IDisposable
    {
        public TrackColorHintService Inner { get; }
        public DisposableService(TrackColorHintService inner) { Inner = inner; }
        public void Dispose() => Inner.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
