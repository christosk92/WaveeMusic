using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Wavee.UI.Services.Infra;
using Xunit;

namespace Wavee.UI.Tests.Services.Infra;

public sealed class BackgroundWorkRunnerTests
{
    [Fact]
    public async Task SuccessfulWork_DoesNotLog()
    {
        var logger = new Mock<ILogger<BackgroundWorkRunner>>();
        var runner = new BackgroundWorkRunner(logger.Object);

        var task = runner.RunAsync(_ => Task.CompletedTask, "noop");
        await task;
        // Give the continuation a turn.
        await Task.Yield();

        logger.Verify(
            x => x.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    [Fact]
    public async Task FaultedWork_LogsErrorWithOpName()
    {
        var logger = new Mock<ILogger<BackgroundWorkRunner>>();
        var runner = new BackgroundWorkRunner(logger.Object);

        var inner = new InvalidOperationException("boom");
        var task = runner.RunAsync(_ => Task.FromException(inner), "explode");

        var awaiting = async () => await task;
        await awaiting.Should().ThrowAsync<InvalidOperationException>();

        // Continuation runs on TaskScheduler.Default after the task transitions.
        // Wait briefly for it to land.
        for (var i = 0; i < 10 && !LoggerInvocations.WasCalled(logger); i++)
            await Task.Delay(10);

        logger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("explode")),
                inner,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task CancelledWork_DoesNotLog()
    {
        var logger = new Mock<ILogger<BackgroundWorkRunner>>();
        var runner = new BackgroundWorkRunner(logger.Object);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var task = runner.RunAsync(ct => Task.FromCanceled(ct), "cancel", cts.Token);

        var awaiting = async () => await task;
        await awaiting.Should().ThrowAsync<OperationCanceledException>();

        await Task.Yield();

        logger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    private static class LoggerInvocations
    {
        public static bool WasCalled<T>(Mock<ILogger<T>> mock)
            => mock.Invocations.Count > 0;
    }
}
