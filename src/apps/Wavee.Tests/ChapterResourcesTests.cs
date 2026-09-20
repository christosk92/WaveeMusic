using System.Threading.Channels;
using Wavee;
using Xunit;
using Chapter = Wavee.Spotify.Podcasts.Chapter;
using ChapterPage = Wavee.Spotify.Podcasts.Page<Wavee.Spotify.Podcasts.Chapter>;

namespace Wavee.Tests;

public sealed class ChapterResourcesTests
{
    static ChapterResources.Identity Key(uint epoch = 1, string account = "a")
    {
        Assert.True(EntityId.TryParseGid("spotify:episode:0Q86acNRm6V9GYx55SXKwf".AsSpan(), out var id));
        return new(epoch, account, id);
    }
    static ChapterPage Page(string title) => new([new Chapter("c", title, 0, 1000)], "", 1, "", 200);
    sealed class Harness
    {
        public readonly List<(TaskCompletionSource<ChapterPage> Completion, CancellationToken Token)> Requests = [];
        readonly Channel<Action> _posts = Channel.CreateUnbounded<Action>();
        public ChapterResources.Identity Current = Key();
        public ChapterResources Pool { get; }
        public Harness()
        {
            Pool = new((key, token) =>
            {
                var completion = new TaskCompletionSource<ChapterPage>(TaskCreationOptions.RunContinuationsAsynchronously);
                Requests.Add((completion, token)); return completion.Task;
            }, action => _posts.Writer.TryWrite(action), key => key == Current);
        }
        public async Task Land(int index, ChapterPage page)
        {
            Requests[index].Completion.SetResult(page);
            var post = await _posts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            post();
        }
    }

    [Fact]
    public async Task Reader_and_player_share_one_request_until_last_lease_releases()
    {
        var h = new Harness();
        using var reader = h.Pool.Acquire(Key());
        var player = h.Pool.Acquire(Key());
        reader.Start(); player.Start();
        Assert.Same(reader.Data, player.Data);
        Assert.Single(h.Requests);
        player.Dispose();
        Assert.False(h.Requests[0].Token.IsCancellationRequested);
        await h.Land(0, Page("shared"));
        Assert.True(reader.Data.IsReady);
        Assert.Equal("shared", reader.Data.Value.Peek().Items[0].Title);
        reader.Dispose();
        Assert.True(h.Requests[0].Token.IsCancellationRequested);
        using var next = h.Pool.Acquire(Key());
        next.Start();
        Assert.Equal(2, h.Requests.Count);
        Assert.NotSame(reader.Data, next.Data);
    }

    [Fact]
    public async Task Late_refresh_completion_cannot_replace_new_request()
    {
        var h = new Harness();
        using var lease = h.Pool.Acquire(Key());
        lease.Start(); lease.Refresh();
        Assert.True(h.Requests[0].Token.IsCancellationRequested);
        await h.Land(1, Page("latest"));
        await h.Land(0, Page("stale"));
        Assert.Equal("latest", lease.Data.Value.Peek().Items[0].Title);
    }

    [Fact]
    public async Task Account_epoch_switch_and_last_consumer_release_reject_late_results()
    {
        var h = new Harness();
        var old = h.Pool.Acquire(Key()); old.Start();
        h.Current = Key(2, "b");
        using var next = h.Pool.Acquire(h.Current); next.Start();
        await h.Land(0, Page("old account"));
        Assert.True(old.Data.IsLoading);
        await h.Land(1, Page("current account"));
        Assert.Equal("current account", next.Data.Value.Peek().Items[0].Title);
        next.Refresh(); next.Dispose();
        await h.Land(2, Page("released"));
        Assert.Equal("current account", next.Data.Value.Peek().Items[0].Title);
        old.Dispose();
    }

    [Theory]
    [InlineData(404, true)]
    [InlineData(204, true)]
    [InlineData(500, false)]
    public async Task Missing_chapters_are_empty_but_failures_remain_retryable(int status, bool empty)
    {
        var h = new Harness();
        using var lease = h.Pool.Acquire(Key()); lease.Start();
        await h.Land(0, new([], "", 0, "", status));
        Assert.Equal(empty, lease.Data.IsReady);
        Assert.Equal(!empty, lease.Data.IsFailed);
        if (empty) Assert.Empty(lease.Data.Value.Peek().Items);
        lease.Refresh(); await h.Land(1, Page("retried"));
        Assert.True(lease.Data.IsReady);
    }
}
