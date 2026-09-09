using System;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;

namespace Wavee.Backend.Wiring;

/// <summary>A failed initialization becomes visible before teardown waits on the partial session.</summary>
public static class GoLiveFailure
{
    public static async Task ReportAndRollbackAsync(Exception error, ILoginProgress? progress, string message,
        bool ownsProgress, CancellationToken attemptToken, Func<Exception, Task> rollback)
    {
        try
        {
            if (ownsProgress && error is not OperationCanceledException && !attemptToken.IsCancellationRequested)
                progress?.Report(new LoginSnapshot(LoginPhase.Failed, Error: message));
        }
        finally { await rollback(error).ConfigureAwait(false); }
    }
}
