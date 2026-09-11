using System;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.Backend.Wiring;

/// <summary>Shared storage initialization outlives any one login attempt and its acquired AP channel.</summary>
public static class GoLiveReadiness
{
    public static async Task WaitAsync(Task sharedInitialization, CancellationToken attemptToken, Action abandonAttempt)
    {
        try
        {
            attemptToken.ThrowIfCancellationRequested();
            await sharedInitialization.WaitAsync(attemptToken).ConfigureAwait(false);
        }
        catch
        {
            try { abandonAttempt(); }
            catch (Exception error) { System.Diagnostics.Trace.TraceError("Pre-live channel cleanup failed: {0}", error); }
            throw;
        }
    }
}
