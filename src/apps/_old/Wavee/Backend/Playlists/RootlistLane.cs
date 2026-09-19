using System.Threading;
using System.Threading.Tasks;

namespace Wavee.Backend.Playlists;

/// <summary>Invariant I2 — ONE writer per entity, applied to the rootlist. Every rootlist write serializes here: the
/// direct lane ops on <see cref="PlaylistMutationSource"/> (move/delete/visibility/create-follow) AND the durable
/// outbox's <c>RootlistFollowStrategy.Replay</c>. Both used to run on their own schedule, so a drain could interleave
/// a follow ADD with an in-flight positional MOV and rebase it against the wrong marker indices.
/// <para>One instance is shared by the strategy and the mutation source at the composition root (a required ctor
/// dependency on both — never an optional/nullable one). Nothing that holds the lane awaits a drain, so it cannot
/// deadlock against the sync loop.</para></summary>
public sealed class RootlistLane
{
    readonly SemaphoreSlim _gate = new(1, 1);

    public Task WaitAsync(CancellationToken ct = default) => _gate.WaitAsync(ct);

    public void Release() => _gate.Release();
}
