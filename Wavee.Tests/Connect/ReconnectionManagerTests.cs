using FluentAssertions;
using Wavee.Connect;
using Wavee.Tests.Helpers;
using Xunit;

namespace Wavee.Tests.Connect;

/// <summary>
/// Tests for ReconnectionManager - validates automatic reconnection with exponential backoff.
///
/// WHY: ReconnectionManager ensures the dealer client automatically recovers from network failures. Bugs here will cause:
/// - Permanent disconnections (app never reconnects)
/// - Connection storms (no backoff, hammers server)
/// - Resource leaks (reconnection loops never stop)
/// - Race conditions (concurrent reconnection attempts)
/// </summary>
public class ReconnectionManagerTests
{
    // ================================================================
    // HAPPY PATH TESTS - Successful reconnection
    // ================================================================

    [Fact]
    public async Task TriggerReconnection_WithSuccessfulCallback_ShouldRaiseSuccessEvent()
    {
        // Arrange
        var successRaised = false;
        var successSignal = new ManualResetEventSlim(false);

        var manager = new ReconnectionManager(
            initialDelay: TimeSpan.FromMilliseconds(10), // Fast for testing
            maxDelay: TimeSpan.FromSeconds(5),
            maxAttempts: 3,
            reconnectCallback: () =>
            {
                // Simulate successful reconnection
                return ValueTask.CompletedTask;
            },
            logger: TestHelpers.CreateMockLogger<ReconnectionManager>().Object);

        manager.ReconnectionSucceeded += (sender, args) =>
        {
            successRaised = true;
            successSignal.Set();
        };

        // Act
        manager.TriggerReconnection();

        var received = successSignal.Wait(TimeSpan.FromSeconds(2));

        await manager.DisposeAsync();

        // Assert
        received.Should().BeTrue("success event should fire on successful reconnection");
        successRaised.Should().BeTrue();
        manager.AttemptCount.Should().Be(0, "attempt count should reset after success");
    }

    // ================================================================
    // EXPONENTIAL BACKOFF TESTS - Retry delay calculation
    // ================================================================

    [Fact]
    public async Task TriggerReconnection_WithFailingCallback_ShouldRetryWithBackoff()
    {
        // Arrange
        var attemptTimes = new List<DateTime>();
        var attemptCount = 0;

        var manager = new ReconnectionManager(
            initialDelay: TimeSpan.FromMilliseconds(100), // 100ms initial
            maxDelay: TimeSpan.FromSeconds(10),
            maxAttempts: 4,
            reconnectCallback: () =>
            {
                lock (attemptTimes)
                {
                    attemptTimes.Add(DateTime.UtcNow);
                    attemptCount++;
                }

                // Fail first 3 times
                if (attemptCount < 4)
                {
                    throw new Exception("Connection failed");
                }

                return ValueTask.CompletedTask; // Success on 4th attempt
            });

        // Act
        manager.TriggerReconnection();

        // Wait for completion
        await Task.Delay(TimeSpan.FromSeconds(2));

        await manager.DisposeAsync();

        // Assert
        attemptTimes.Should().HaveCount(4, "should retry 3 times then succeed on 4th");

        // Verify exponential backoff: 100ms, 200ms, 400ms delays between attempts
        if (attemptTimes.Count >= 2)
        {
            var delay1 = (attemptTimes[1] - attemptTimes[0]).TotalMilliseconds;
            delay1.Should().BeGreaterThan(80, "first delay should be ~100ms (with tolerance)");
            delay1.Should().BeLessThan(300, "first delay should not be too long");
        }

        if (attemptTimes.Count >= 3)
        {
            var delay2 = (attemptTimes[2] - attemptTimes[1]).TotalMilliseconds;
            delay2.Should().BeGreaterThan(150, "second delay should be ~200ms (doubled)");
            delay2.Should().BeLessThan(500, "second delay should not be too long");
        }
    }

    [Fact]
    public async Task CalculateDelay_ShouldClampToMaxDelay()
    {
        // Arrange
        var attemptTimes = new List<DateTime>();

        var manager = new ReconnectionManager(
            initialDelay: TimeSpan.FromMilliseconds(100),
            maxDelay: TimeSpan.FromMilliseconds(300), // Low max for testing
            maxAttempts: 10,
            reconnectCallback: () =>
            {
                lock (attemptTimes) { attemptTimes.Add(DateTime.UtcNow); }
                throw new Exception("Keep failing");
            });

        // Act
        manager.TriggerReconnection();

        // Wait for several attempts
        await Task.Delay(TimeSpan.FromSeconds(2));

        await manager.CancelReconnectionAsync();

        // Assert
        attemptTimes.Should().HaveCountGreaterThan(3, "multiple attempts should occur");

        // Check that delays don't exceed max
        for (int i = 1; i < attemptTimes.Count && i < 5; i++)
        {
            var delay = (attemptTimes[i] - attemptTimes[i - 1]).TotalMilliseconds;
            delay.Should().BeLessThan(500, $"delay {i} should be clamped to maxDelay");
        }
    }

    // ================================================================
    // MAX ATTEMPTS TESTS - Limit enforcement
    // ================================================================

    [Fact]
    public async Task MaxAttemptsExhausted_ShouldRaiseFailureEvent()
    {
        // Arrange
        var failureRaised = false;
        var failureSignal = new ManualResetEventSlim(false);

        var manager = new ReconnectionManager(
            initialDelay: TimeSpan.FromMilliseconds(10),
            maxDelay: TimeSpan.FromSeconds(5),
            maxAttempts: 3, // Only 3 attempts
            reconnectCallback: () => throw new Exception("Always fail"));

        manager.ReconnectionFailed += (sender, args) =>
        {
            failureRaised = true;
            failureSignal.Set();
        };

        // Act
        manager.TriggerReconnection();

        var received = failureSignal.Wait(TimeSpan.FromSeconds(2));

        await manager.DisposeAsync();

        // Assert
        received.Should().BeTrue("failure event should fire after max attempts");
        failureRaised.Should().BeTrue();
        manager.AttemptCount.Should().Be(3, "should have tried exactly maxAttempts times");
    }

    [Fact]
    public async Task UnlimitedAttempts_ShouldContinueIndefinitely()
    {
        // Arrange
        var attemptCount = 0;

        var manager = new ReconnectionManager(
            initialDelay: TimeSpan.FromMilliseconds(10),
            maxDelay: TimeSpan.FromMilliseconds(50),
            maxAttempts: null, // Unlimited
            reconnectCallback: () =>
            {
                Interlocked.Increment(ref attemptCount);
                throw new Exception("Keep failing");
            });

        // Act
        manager.TriggerReconnection();

        // Let it run for a bit
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        await manager.CancelReconnectionAsync();

        // Assert
        attemptCount.Should().BeGreaterThan(5, "should retry many times with unlimited attempts");
    }

    // ================================================================
    // CANCELLATION TESTS - Stopping reconnection
    // ================================================================

    [Fact]
    public async Task CancelReconnectionAsync_DuringDelay_ShouldCancel()
    {
        // Arrange
        var attemptCount = 0;

        var manager = new ReconnectionManager(
            initialDelay: TimeSpan.FromSeconds(10), // Long delay
            maxDelay: TimeSpan.FromSeconds(60),
            maxAttempts: null,
            reconnectCallback: () =>
            {
                Interlocked.Increment(ref attemptCount);
                throw new Exception("Fail");
            });

        // Act
        manager.TriggerReconnection();

        // Cancel immediately (should cancel during delay)
        await manager.CancelReconnectionAsync();

        // Wait a bit
        await Task.Delay(100);

        // Assert
        attemptCount.Should().BeLessThanOrEqualTo(1, "should not retry after cancellation");
    }

    [Fact]
    public async Task CancelReconnectionAsync_DuringCallback_ShouldCancel()
    {
        // Arrange
        var callbackStarted = new ManualResetEventSlim(false);
        var attemptCount = 0;

        var manager = new ReconnectionManager(
            initialDelay: TimeSpan.FromMilliseconds(10),
            maxDelay: TimeSpan.FromSeconds(5),
            maxAttempts: 10,
            reconnectCallback: async () =>
            {
                Interlocked.Increment(ref attemptCount);
                callbackStarted.Set();
                await Task.Delay(1000); // Simulate slow reconnect
                throw new Exception("Fail");
            });

        // Act
        manager.TriggerReconnection();

        // Wait for callback to start
        callbackStarted.Wait(TimeSpan.FromSeconds(1));

        // Cancel while callback is running
        await manager.CancelReconnectionAsync();

        var finalAttemptCount = attemptCount;

        // Wait a bit more
        await Task.Delay(200);

        // Assert
        attemptCount.Should().Be(finalAttemptCount, "no more attempts after cancellation");
    }

    // ================================================================
    // STATE MANAGEMENT TESTS
    // ================================================================

    [Fact]
    public void TriggerReconnection_WhenAlreadyReconnecting_ShouldIgnore()
    {
        // Arrange
        var callbackCalled = 0;

        var manager = new ReconnectionManager(
            initialDelay: TimeSpan.FromMilliseconds(100),
            maxDelay: TimeSpan.FromSeconds(5),
            maxAttempts: 5,
            reconnectCallback: async () =>
            {
                Interlocked.Increment(ref callbackCalled);
                await Task.Delay(500); // Slow callback
                throw new Exception("Fail");
            });

        // Act
        manager.TriggerReconnection();
        manager.IsReconnecting.Should().BeTrue("should be reconnecting after trigger");

        // Try to trigger again (should be ignored)
        manager.TriggerReconnection();
        manager.TriggerReconnection();

        // Wait for first attempt
        Thread.Sleep(200);

        // Assert
        callbackCalled.Should().Be(1, "only one reconnection loop should be running");

        // Cleanup
        manager.CancelReconnectionAsync().AsTask().Wait();
    }

    [Fact]
    public void Reset_ShouldClearAttemptCount()
    {
        // Arrange
        var manager = new ReconnectionManager(
            initialDelay: TimeSpan.FromMilliseconds(10),
            maxDelay: TimeSpan.FromSeconds(5),
            maxAttempts: 5,
            reconnectCallback: () => throw new Exception("Fail"));

        manager.TriggerReconnection();
        Thread.Sleep(100); // Let it try a few times

        manager.CancelReconnectionAsync().AsTask().Wait();

        var countBeforeReset = manager.AttemptCount;

        // Act
        manager.Reset();

        // Assert
        countBeforeReset.Should().BeGreaterThan(0, "should have had attempts");
        manager.AttemptCount.Should().Be(0, "reset should clear attempt count");
        manager.IsReconnecting.Should().BeFalse("reset should clear reconnecting flag");
    }
}
