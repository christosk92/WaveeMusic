using Xunit;
using static Wavee.Spotify.Audio;

namespace Wavee.Tests;

public sealed class KeyOutcomeBookTests
{
    sealed class Clock { public long Now; }

    static KeyOutcomeBook Book(Clock clock, List<int>? sleeps = null)
        => new(() => clock.Now, (ms, _) => sleeps?.Add(ms));

    static readonly byte[] Key = new byte[16];

    [Fact]
    public void A_403_is_one_request_and_never_retried()
    {
        var book = Book(new Clock());
        int calls = 0;
        var outcome = book.Run("f1", (_, _) => { calls++; return KeyOutcomeBook.Outcome.Refused; }, CancellationToken.None);

        Assert.Equal(KeyOutcomeBook.Verdict.Refused, outcome.Verdict);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void A_refused_file_is_not_asked_again_until_the_cooldown_ends_and_other_files_are_unaffected()
    {
        var clock = new Clock();
        var book = Book(clock);
        int calls = 0;
        KeyOutcomeBook.Outcome Refuse(int _, CancellationToken __) { calls++; return KeyOutcomeBook.Outcome.Refused; }

        book.Run("f1", Refuse, CancellationToken.None);
        clock.Now = KeyOutcomeBook.RefusedCooldownMs - 1;
        Assert.Equal(KeyOutcomeBook.Verdict.Refused, book.Run("f1", Refuse, CancellationToken.None).Verdict);
        Assert.Equal(1, calls);
        Assert.True(book.IsRefused("f1"));

        var other = book.Run("f2", (_, _) => KeyOutcomeBook.Outcome.Of(Key), CancellationToken.None);
        Assert.Equal(KeyOutcomeBook.Verdict.Ok, other.Verdict);
        Assert.False(book.IsRefused("f2"));

        clock.Now = KeyOutcomeBook.RefusedCooldownMs;
        Assert.False(book.IsRefused("f1"));
        book.Run("f1", Refuse, CancellationToken.None);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void A_5xx_is_retried_with_1s_then_2s_backoff_and_one_attempt_per_request()
    {
        var sleeps = new List<int>();
        var book = Book(new Clock(), sleeps);
        var attempts = new List<int>();

        var outcome = book.Run("f1", (n, _) =>
        {
            attempts.Add(n);
            return n < 3 ? KeyOutcomeBook.Outcome.Transient : KeyOutcomeBook.Outcome.Of(Key);
        }, CancellationToken.None);

        Assert.Equal(KeyOutcomeBook.Verdict.Ok, outcome.Verdict);
        Assert.Equal([1, 2, 3], attempts);          // the attempt delegate runs per attempt: it builds a fresh body each time
        Assert.Equal([1000, 2000], sleeps);
    }

    [Fact]
    public void A_transient_failure_gives_up_after_three_attempts_and_is_not_remembered()
    {
        var book = Book(new Clock());
        int calls = 0;
        var outcome = book.Run("f1", (_, _) => { calls++; return KeyOutcomeBook.Outcome.Transient; }, CancellationToken.None);

        Assert.Equal(KeyOutcomeBook.Verdict.Transient, outcome.Verdict);
        Assert.Equal(KeyOutcomeBook.MaxAttempts, calls);
        Assert.False(book.IsRefused("f1"));
    }

    [Fact]
    public void A_failed_derivation_is_not_retried_and_not_remembered()
    {
        var book = Book(new Clock());
        int calls = 0;
        book.Run("f1", (_, _) => { calls++; return KeyOutcomeBook.Outcome.Failed; }, CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.False(book.IsRefused("f1"));
    }

    [Fact]
    public async Task Concurrent_callers_for_one_file_share_one_flight_and_its_result()
    {
        var book = Book(new Clock());
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int calls = 0;

        var leader = Task.Run(() => book.Run("f1", (_, _) =>
        {
            Interlocked.Increment(ref calls);
            entered.Set();
            release.Wait();
            return KeyOutcomeBook.Outcome.Refused;
        }, CancellationToken.None));
        Assert.True(entered.Wait(5000));

        var joiner = Task.Run(() => book.Run("f1", (_, _) => { Interlocked.Increment(ref calls); return KeyOutcomeBook.Outcome.Of(Key); }, CancellationToken.None));
        Thread.Sleep(50);
        release.Set();

        Assert.Equal(KeyOutcomeBook.Verdict.Refused, (await leader).Verdict);
        Assert.Equal(KeyOutcomeBook.Verdict.Refused, (await joiner).Verdict);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void A_cancelled_leader_lets_the_next_caller_start_a_new_flight()
    {
        var book = Book(new Clock());
        using var cts = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => book.Run("f1", (_, ct) => { cts.Cancel(); ct.ThrowIfCancellationRequested(); return KeyOutcomeBook.Outcome.Failed; }, cts.Token));

        var outcome = book.Run("f1", (_, _) => KeyOutcomeBook.Outcome.Of(Key), CancellationToken.None);
        Assert.Equal(KeyOutcomeBook.Verdict.Ok, outcome.Verdict);
    }
}
