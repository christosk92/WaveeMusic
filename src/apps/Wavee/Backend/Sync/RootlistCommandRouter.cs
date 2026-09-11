using System;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;

namespace Wavee.Backend.Sync;

/// <summary>Stable session boundary. Only the installed LibrarySync owns protocol command execution.</summary>
public sealed class RootlistCommandRouter(Func<IRootlistCommandQueue?> current) : IRootlistCommandQueue
{
    public Task DrainWritesAsync(CancellationToken ct)
        => current()?.DrainWritesAsync(ct) ?? Task.CompletedTask;
    public Task ExecuteRootlistAsync(Func<CancellationToken, Task> command, CancellationToken ct = default)
        => current()?.ExecuteRootlistAsync(command, ct)
            ?? Task.FromException(new PlaylistMutationException(PlaylistMutationFailure.Offline, "The playlist service is offline."));
}
