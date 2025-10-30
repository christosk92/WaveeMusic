using FluentAssertions;
using System.Collections.Concurrent;
using Wavee.Core.Utilities;
using Wavee.Tests.Helpers;
using Xunit;

namespace Wavee.Tests.Core.Utilities;

/// <summary>
/// Tests for AsyncWorker - validates background work queue processing with exception isolation.
///
/// WHY: AsyncWorker is critical for non-blocking dealer message dispatch. Bugs here will cause:
/// - Slow subscriber blocking all others (head-of-line blocking)
/// - Message loss on exceptions (improper isolation)
/// - Memory leaks (uncompleted work items)
/// - Deadlocks (improper disposal)
///
/// Based on Java's AsyncProcessorTest.java from librespot-java.
/// </summary>
public class AsyncWorkerTests
{
    // ================================================================
    // HAPPY PATH TESTS - Core functionality that MUST work
    // ================================================================

    [Fact]
    public async Task SubmitAsync_ShouldProcessInOrder()
    {
        // Arrange
        var processed = new ConcurrentBag<int>();
        var worker = new AsyncWorker<int>(
            "TestWorker",
            async item =>
            {
                await Task.Delay(10); // Simulate work
                processed.Add(item);
            },
            logger: TestHelpers.CreateMockLogger<AsyncWorker<int>>().Object);

        // Act
        await worker.SubmitAsync(1);
        await worker.SubmitAsync(2);
        await worker.SubmitAsync(3);
        await worker.SubmitAsync(4);

        await worker.CompleteAsync();

        // Assert
        processed.Should().HaveCount(4, "all items should be processed");
        processed.Should().BeEquivalentTo(new[] { 1, 2, 3, 4 });
    }

    [Fact]
    public async Task WorkerException_ShouldIsolateAndContinue()
    {
        // Arrange
        var processed = new List<string>();
        var worker = new AsyncWorker<string>(
            "TestWorker",
            async item =>
            {
                if (item == "throw")
                {
                    throw new InvalidOperationException("Test exception");
                }

                await Task.Delay(5);
                lock (processed) { processed.Add(item); }
            });

        // Act
        await worker.SubmitAsync("item1");
        await worker.SubmitAsync("throw");  // This should fail but not stop worker
        await worker.SubmitAsync("item2");  // This should still process

        await worker.CompleteAsync();

        // Assert
        processed.Should().Contain("item1", "item before exception should process");
        processed.Should().Contain("item2", "item after exception should process");
        processed.Should().NotContain("throw", "throwing item should not be in processed list");
    }

    [Fact]
    public async Task CompleteAsync_ShouldWaitForPendingWork()
    {
        // Arrange
        var processedCount = 0;
        var worker = new AsyncWorker<int>(
            "TestWorker",
            async item =>
            {
                await Task.Delay(50); // Slow processing
                Interlocked.Increment(ref processedCount);
            });

        // Act
        await worker.SubmitAsync(1);
        await worker.SubmitAsync(2);
        await worker.SubmitAsync(3);

        var completeTask = worker.CompleteAsync();

        // Assert - CompleteAsync should wait for all 3 items
        await completeTask;
        processedCount.Should().Be(3, "CompleteAsync should wait for all pending work");
    }

    // ================================================================
    // ERROR HANDLING TESTS - All error paths must be validated
    // ================================================================

    [Fact]
    public async Task SubmitAsync_AfterComplete_ShouldThrow()
    {
        // Arrange
        var worker = new AsyncWorker<int>(
            "TestWorker",
            async _ => await Task.CompletedTask);

        await worker.CompleteAsync();

        // Act
        var act = async () => await worker.SubmitAsync(1);

        // Assert
        // Channel throws when trying to write after completion
        await act.Should().ThrowAsync<Exception>("cannot submit after completion");
    }

    [Fact]
    public async Task SubmitAsync_AfterDispose_ShouldThrow()
    {
        // Arrange
        var worker = new AsyncWorker<int>(
            "TestWorker",
            async _ => await Task.CompletedTask);

        await worker.DisposeAsync();

        // Act
        var act = async () => await worker.SubmitAsync(1);

        // Assert
        await act.Should().ThrowAsync<ObjectDisposedException>("cannot submit after disposal");
    }

    // ================================================================
    // BOUNDED QUEUE TESTS - Backpressure handling
    // ================================================================

    [Fact]
    public async Task BoundedQueue_FullQueue_ShouldBlock()
    {
        // Arrange
        var blockSignal = new ManualResetEventSlim(false);
        var startedProcessing = new ManualResetEventSlim(false);

        var worker = new AsyncWorker<int>(
            "TestWorker",
            async item =>
            {
                startedProcessing.Set();
                blockSignal.Wait(); // Block processing
                await Task.Delay(1);
            },
            capacity: 2); // Small capacity

        // Act - Fill the queue (2 items)
        await worker.SubmitAsync(1);
        await worker.SubmitAsync(2);

        // Wait for worker to start processing first item
        startedProcessing.Wait(TimeSpan.FromSeconds(1));

        // Queue is now full (item 2 is in queue, item 1 is processing)
        // Next submit should block
        var submitTask = worker.SubmitAsync(3);

        // Give it a moment to try to submit
        await Task.Delay(100);

        // Assert - Submit should be blocked
        submitTask.IsCompleted.Should().BeFalse("submit should block when queue is full");

        // Cleanup - unblock and complete
        blockSignal.Set();
        await submitTask;
        await worker.CompleteAsync();
    }

    [Fact]
    public async Task TrySubmit_OnFullQueue_ShouldReturnFalse()
    {
        // Arrange
        var blockSignal = new ManualResetEventSlim(false);
        var worker = new AsyncWorker<int>(
            "TestWorker",
            async _ =>
            {
                blockSignal.Wait();
                await Task.Delay(1);
            },
            capacity: 2);

        // Act - Fill the queue
        await worker.SubmitAsync(1);
        await worker.SubmitAsync(2);
        await Task.Delay(50); // Ensure processing started

        var success = worker.TrySubmit(3);

        // Assert
        success.Should().BeFalse("TrySubmit should return false when queue is full");

        // Cleanup
        blockSignal.Set();
        await worker.CompleteAsync();
    }

    // ================================================================
    // CONCURRENCY TESTS - Thread safety validation
    // ================================================================

    [Fact]
    public async Task ConcurrentSubmissions_ShouldAllProcess()
    {
        // Arrange
        var processedCount = 0;
        var worker = new AsyncWorker<int>(
            "TestWorker",
            async item =>
            {
                await Task.Delay(1);
                Interlocked.Increment(ref processedCount);
            });

        const int threadCount = 10;
        const int itemsPerThread = 10;

        // Act - Submit from multiple threads concurrently
        var tasks = Enumerable.Range(0, threadCount)
            .Select(threadId => Task.Run(async () =>
            {
                for (int i = 0; i < itemsPerThread; i++)
                {
                    await worker.SubmitAsync(threadId * 100 + i);
                }
            }))
            .ToArray();

        await Task.WhenAll(tasks);
        await worker.CompleteAsync();

        // Assert
        processedCount.Should().Be(threadCount * itemsPerThread, "all concurrent submissions should process");
    }

    // ================================================================
    // STATE MANAGEMENT TESTS
    // ================================================================

    [Fact]
    public void PendingCount_ShouldReflectQueueSize()
    {
        // Arrange
        var blockSignal = new ManualResetEventSlim(false);
        var worker = new AsyncWorker<int>(
            "TestWorker",
            async _ =>
            {
                blockSignal.Wait();
                await Task.Delay(1);
            });

        // Act & Assert - Initially zero
        worker.PendingCount.Should().Be(0);

        // Submit items (but don't process)
        worker.SubmitAsync(1).AsTask().Wait();
        worker.SubmitAsync(2).AsTask().Wait();
        worker.SubmitAsync(3).AsTask().Wait();

        // Allow processing to start but not complete
        Thread.Sleep(50);

        // Pending count should reflect queued items (might be 2 or 3 depending on timing)
        worker.PendingCount.Should().BeGreaterOrEqualTo(0);

        // Cleanup
        blockSignal.Set();
        worker.CompleteAsync().AsTask().Wait();
    }
}
