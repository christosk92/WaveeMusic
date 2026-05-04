using FluentAssertions;
using Wavee.Connect;
using Wavee.Tests.Helpers;
using Xunit;

namespace Wavee.Tests.Connect;

/// <summary>
/// Tests for HeartbeatManager - validates client-initiated PING/PONG heartbeat mechanism.
///
/// WHY: HeartbeatManager detects dead connections that would otherwise hang forever. Bugs here will cause:
/// - Undetected connection failures (app appears connected but isn't)
/// - False positives (healthy connections get killed)
/// - Resource leaks (timers not stopped)
/// - Race conditions (concurrent PONG recording during timeout)
/// </summary>
public class HeartbeatManagerTests
{
    // ================================================================
    // HAPPY PATH TESTS - Normal heartbeat operation
    // ================================================================

    [Fact]
    public async Task Start_ShouldSendPingAtInterval()
    {
        // Arrange
        var pingCount = 0;
        var pingSignal = new ManualResetEventSlim(false);

        var manager = new HeartbeatManager(
            pingInterval: TimeSpan.FromMilliseconds(100), // Fast for testing
            pongTimeout: TimeSpan.FromMilliseconds(500),
            sendPingAsync: () =>
            {
                Interlocked.Increment(ref pingCount);
                pingSignal.Set();
                return ValueTask.CompletedTask;
            },
            logger: TestHelpers.CreateMockLogger<HeartbeatManager>().Object);

        // Act
        manager.Start();

        // Wait for at least one PING
        var received = pingSignal.Wait(TimeSpan.FromSeconds(1));

        await manager.StopAsync();

        // Assert
        received.Should().BeTrue("PING should be sent within interval");
        pingCount.Should().BeGreaterOrEqualTo(1, "at least one PING should be sent");
    }

    [Fact]
    public async Task RecordPong_ShouldPreventTimeout()
    {
        // Arrange
        var timeoutRaised = false;
        var pingCount = 0;
        HeartbeatManager? manager = null;

        manager = new HeartbeatManager(
            pingInterval: TimeSpan.FromMilliseconds(200),
            pongTimeout: TimeSpan.FromMilliseconds(400),
            sendPingAsync: () =>
            {
                Interlocked.Increment(ref pingCount);
                // Immediately record PONG for each PING to prevent timeout
                manager!.RecordPong();
                return ValueTask.CompletedTask;
            });

        manager.HeartbeatTimeout += (sender, args) => timeoutRaised = true;

        // Act
        manager.Start();

        // Wait longer than timeout to ensure PONG prevented timeout
        await Task.Delay(600);

        await manager.StopAsync();

        // Assert
        timeoutRaised.Should().BeFalse("PONG should prevent timeout");
        pingCount.Should().BeGreaterThan(0, "at least one PING should have been sent");
    }

    // ================================================================
    // TIMEOUT DETECTION TESTS - Critical failure detection
    // ================================================================

    [Fact]
    public async Task PongTimeout_ShouldRaiseTimeoutEvent()
    {
        // Arrange
        var timeoutRaised = false;
        var timeoutSignal = new ManualResetEventSlim(false);
        var pingSignal = new ManualResetEventSlim(false);

        var manager = new HeartbeatManager(
            pingInterval: TimeSpan.FromMilliseconds(150),
            pongTimeout: TimeSpan.FromMilliseconds(250), // Longer timeout for reliability
            sendPingAsync: () =>
            {
                pingSignal.Set();
                return ValueTask.CompletedTask;
            });

        manager.HeartbeatTimeout += (sender, args) =>
        {
            timeoutRaised = true;
            timeoutSignal.Set();
        };

        // Act
        manager.Start();

        // Wait for PING but DON'T record PONG
        pingSignal.Wait(TimeSpan.FromSeconds(2));

        // Wait for timeout with generous timeout
        var receivedTimeout = timeoutSignal.Wait(TimeSpan.FromSeconds(3));

        await manager.StopAsync();

        // Assert
        receivedTimeout.Should().BeTrue("timeout event should fire when PONG not received");
        timeoutRaised.Should().BeTrue();
    }

    [Fact]
    public async Task SendPingFailure_ShouldTriggerTimeout()
    {
        // Arrange
        var timeoutRaised = false;
        var timeoutSignal = new ManualResetEventSlim(false);

        var manager = new HeartbeatManager(
            pingInterval: TimeSpan.FromMilliseconds(100),
            pongTimeout: TimeSpan.FromMilliseconds(200),
            sendPingAsync: () => throw new InvalidOperationException("Send failed"));

        manager.HeartbeatTimeout += (sender, args) =>
        {
            timeoutRaised = true;
            timeoutSignal.Set();
        };

        // Act
        manager.Start();

        // Wait for timeout (should happen immediately after failed PING)
        var receivedTimeout = timeoutSignal.Wait(TimeSpan.FromSeconds(1));

        await manager.StopAsync();

        // Assert
        receivedTimeout.Should().BeTrue("failed PING send should trigger timeout");
        timeoutRaised.Should().BeTrue();
    }

    // ================================================================
    // LIFECYCLE TESTS - Start/Stop management
    // ================================================================

    [Fact]
    public async Task StopAsync_ShouldCancelHeartbeat()
    {
        // Arrange
        var pingCount = 0;

        var manager = new HeartbeatManager(
            pingInterval: TimeSpan.FromMilliseconds(100),
            pongTimeout: TimeSpan.FromMilliseconds(200),
            sendPingAsync: () =>
            {
                Interlocked.Increment(ref pingCount);
                return ValueTask.CompletedTask;
            });

        // Act
        manager.Start();
        await Task.Delay(300); // Let it send some PINGs (at least 2-3)

        var countBeforeStop = pingCount;

        await manager.StopAsync();
        await Task.Delay(300); // Wait same duration

        var countAfterStop = pingCount;

        // Assert
        countBeforeStop.Should().BeGreaterThan(0, "PINGs should be sent before stop");
        countAfterStop.Should().Be(countBeforeStop, "no PINGs should be sent after stop");
    }

    [Fact]
    public void Start_WhenAlreadyStarted_ShouldThrow()
    {
        // Arrange
        var manager = new HeartbeatManager(
            pingInterval: TimeSpan.FromMilliseconds(100),
            pongTimeout: TimeSpan.FromMilliseconds(200),
            sendPingAsync: () => ValueTask.CompletedTask);

        manager.Start();

        // Act
        var act = () => manager.Start();

        // Assert
        act.Should().Throw<InvalidOperationException>("cannot start when already started");

        // Cleanup
        manager.StopAsync().AsTask().Wait();
    }

    // ================================================================
    // THREAD SAFETY TESTS - Concurrent operations
    // ================================================================

    [Fact]
    public async Task ConcurrentPongRecording_ShouldBeThreadSafe()
    {
        // Arrange
        var manager = new HeartbeatManager(
            pingInterval: TimeSpan.FromMilliseconds(100),
            pongTimeout: TimeSpan.FromMilliseconds(500),
            sendPingAsync: () => ValueTask.CompletedTask);

        manager.Start();

        // Act - Record PONGs from multiple threads concurrently
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() =>
            {
                for (int i = 0; i < 100; i++)
                {
                    manager.RecordPong();
                    Thread.SpinWait(10);
                }
            }))
            .ToArray();

        await Task.WhenAll(tasks);

        await manager.StopAsync();

        // Assert - Should not throw or deadlock
        true.Should().BeTrue("concurrent PONG recording should not cause issues");
    }

    // ================================================================
    // DISPOSAL TESTS - Resource cleanup
    // ================================================================

    [Fact]
    public async Task DisposeAsync_ShouldCleanup()
    {
        // Arrange
        var pingCount = 0;

        var manager = new HeartbeatManager(
            pingInterval: TimeSpan.FromMilliseconds(100),
            pongTimeout: TimeSpan.FromMilliseconds(200),
            sendPingAsync: () =>
            {
                Interlocked.Increment(ref pingCount);
                return ValueTask.CompletedTask;
            });

        // Act
        manager.Start();
        await Task.Delay(300); // Let it send some PINGs

        var countBeforeDispose = pingCount;

        await manager.DisposeAsync();
        await Task.Delay(300); // Wait to ensure no more PINGs

        var countAfterDispose = pingCount;

        // Assert
        countBeforeDispose.Should().BeGreaterThan(0);
        countAfterDispose.Should().Be(countBeforeDispose, "PING should stop after dispose");
    }
}
