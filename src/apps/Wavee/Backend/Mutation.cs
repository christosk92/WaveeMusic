using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Collections;
using Wavee.Backend.Playlists;
using Wavee.Backend.Spotify;
using Wavee.Backend.Sync;
using Wavee.Core;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee.Backend;

public sealed record OutboxOp(long Id, string Type, string EntityKey, string SetId, bool TargetSaved, long LogicalTs, int Attempts,
    IReadOnlyList<PlaylistOp>? Ops = null, byte[]? BaseRev = null, string? ParentFolderId = null,
    string? OwnerAccount = null, ReplicaIntentState State = ReplicaIntentState.Pending,
    byte[]? AcknowledgedRevision = null, long CreatedAtMs = 0, string StorageAccount = "default");

public enum MutationReplayDisposition : byte { Applied, Verify, Rebase, Retry }
public sealed record MutationReplayResult(MutationReplayDisposition Disposition,
    PlaylistReadResult? Playlist = null, RootlistReadResult? Rootlist = null, byte[]? AcknowledgedRevision = null);

/// <summary>Transport strategy. Preparation uses confirmed state; replies are observations and never write a store.</summary>
public interface IMutationStrategy
{
    string Type { get; }
    OutboxOp Prepare(OutboxOp intent);
    Task<MutationReplayResult> Replay(OutboxOp attempted, ITransport transport, SessionContext context, CancellationToken ct);
}

public sealed class SetReplayStrategy : IMutationStrategy
{
    const string VendorType = "application/vnd.collection-v2.spotify.proto";
    readonly CollectionEchoRing _echoRing;
    public SetReplayStrategy(CollectionEchoRing echoRing) => _echoRing = echoRing;
    public string Type => "set";
    public OutboxOp Prepare(OutboxOp intent) => intent;
    public async Task<MutationReplayResult> Replay(OutboxOp op, ITransport transport, SessionContext ctx, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        var body = CollectionWriteMapper.BuildWrite(ctx.Account, op.SetId, op.EntityKey, op.TargetSaved,
            op.CreatedAtMs / 1000, id);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            { ["Content-Type"] = VendorType, ["Accept"] = VendorType };
        var reply = await transport.Request(Channel.Spclient, "/collection/v2/write", body, ct, "POST", headers).ConfigureAwait(false);
        if (!reply.Ok) return IdempotentReply(reply.Status);
        _echoRing.Record(id);
        return new(MutationReplayDisposition.Applied);
    }

    // Finding #2: a "set" (like/unlike, follow-toggle) write is idempotent — replaying it is always safe. Unlike a
    // base-revision op, an ambiguous transport/5xx/429 reply here stays Pending with backoff (Retry) and is resent,
    // never Verify/NeedsAttention. This does NOT go through the shared MutationReply.Rejected: that function also
    // backs RootlistProtocol.PostAsync, whose OTHER caller (the rootlist-edit structural seam in
    // PlaylistMutationSource.RunRootlistOpAsync) rejects+rolls back on ANY Retry disposition — a contract that must
    // stay reserved for a genuine 429, not broaden to ambiguous transport failures.
    internal static MutationReplayResult IdempotentReply(int status) => MutationReplyRules.Classify(status) switch
    {
        MutationReplyClass.Retry => new(MutationReplayDisposition.Retry),
        MutationReplyClass.Verify => new(MutationReplayDisposition.Verify),   // 409: double-check before re-sending
        _ => MutationReply.Rejected(status),                                 // definitive 4xx: throws the right kind
    };
}

public sealed class OpRebaseStrategy : IMutationStrategy
{
    readonly LibraryReplicaCoordinator _replicas;
    readonly Func<string> _baseUrl;
    public OpRebaseStrategy(LibraryReplicaCoordinator replicas, Func<string> baseUrl)
        => (_replicas, _baseUrl) = (replicas, baseUrl);
    public string Type => "oprebase";
    public OutboxOp Prepare(OutboxOp op)
    {
        var baseline = _replicas.ReadConfirmedPlaylist(op.EntityKey);
        if (baseline.Header?.DeletedByOwner == true)
            throw new PlaylistMutationException(PlaylistMutationFailure.Deleted, "That playlist no longer exists.");
        if (!PlaylistRevisions.IsWellFormed(baseline.Revision))
            throw new PlaylistMutationException(PlaylistMutationFailure.Conflict, "Refresh that playlist before editing it.");
        var ops = PlaylistIntentRebaser.Rebase(op, baseline.Revision, baseline.Members);
        return op with { Ops = ops, BaseRev = baseline.Revision.ToArray() };
    }
    public async Task<MutationReplayResult> Replay(OutboxOp op, ITransport transport, SessionContext ctx, CancellationToken ct)
    {
        var path = op.EntityKey.StartsWith("spotify:", StringComparison.Ordinal) ? op.EntityKey[8..].Replace(':', '/') : op.EntityKey;
        var body = PlaylistWireMapper.BuildChanges(op.BaseRev, op.Ops ?? [], ctx.Account, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var reply = await transport.Request(Channel.Spclient, $"/playlist/v2/{path}/changes", body, ct, "POST",
            SpotifyHeaders.PlaylistV2Mutation(ctx.Locale, _baseUrl())).ConfigureAwait(false);
        return reply.Ok ? MutationReply.Playlist(_replicas.ReadConfirmedPlaylist(op.EntityKey), op, reply.Body)
            : OpRebaseReply(reply.Status);
    }

    // Finding #2: a base-revision op is also safe to retry — 409 already means "rebase against the latest and try
    // again" (MutationReply.Rejected's Rebase). An ambiguous transport/5xx/429 reply must ALSO stay Pending with
    // backoff (Retry), same reasoning and same carve-out as SetReplayStrategy.IdempotentReply above: this bypasses
    // the shared Rejected() for that case so RootlistProtocol.PostAsync's OTHER caller (the rootlist-edit seam) can
    // keep treating any non-429 ambiguity as Verify.
    static MutationReplayResult OpRebaseReply(int status)
    {
        if (status == 409) return MutationReply.Rejected(status);   // → Rebase
        if (MutationReplyRules.Classify(status) == MutationReplyClass.Retry) return new(MutationReplayDisposition.Retry);
        return MutationReply.Rejected(status);                      // definitive 4xx: throws the right kind
    }
}

public sealed class CreatePlaylistStrategy : IMutationStrategy
{
    readonly LibraryReplicaCoordinator _replicas;
    readonly Func<string> _baseUrl;
    public CreatePlaylistStrategy(LibraryReplicaCoordinator replicas, Func<string> baseUrl)
        => (_replicas, _baseUrl) = (replicas, baseUrl);
    public string Type => "create";
    public OutboxOp Prepare(OutboxOp op) => op;
    public async Task<MutationReplayResult> Replay(OutboxOp op, ITransport transport, SessionContext ctx, CancellationToken ct)
    {
        var body = PlaylistWireMapper.BuildCreateChanges(NameOf(op.Ops), ctx.Account, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var reply = await transport.Request(Channel.Spclient, $"/playlist/v2/playlist/{EntityUri.IdOf(op.EntityKey)}/changes", body, ct, "POST",
            SpotifyHeaders.PlaylistV2Create(ctx.Locale, _baseUrl())).ConfigureAwait(false);
        // A conflict on a minted URI may be an earlier accepted create; verify its existence instead of minting again.
        if (reply.Status == 409) return new(MutationReplayDisposition.Verify);
        return reply.Ok ? MutationReply.Playlist(_replicas.ReadConfirmedPlaylist(op.EntityKey), op, reply.Body)
            : MutationReply.Rejected(reply.Status);
    }
    internal static string NameOf(IReadOnlyList<PlaylistOp>? ops)
        => ops?.FirstOrDefault(x => x.Kind == PlaylistOpKind.UpdateList)?.ListPatch?.Name ?? "";
}

public sealed class RootlistFollowStrategy : IMutationStrategy
{
    readonly LibraryReplicaCoordinator _replicas;
    readonly Func<string> _baseUrl;
    public RootlistFollowStrategy(LibraryReplicaCoordinator replicas, Func<string> baseUrl)
        => (_replicas, _baseUrl) = (replicas, baseUrl);
    public string Type => "rootlist";
    public OutboxOp Prepare(OutboxOp op)
    {
        var root = _replicas.ReadConfirmedRootlist();
        if (!PlaylistRevisions.IsWellFormed(root.Revision))
            throw new PlaylistMutationException(PlaylistMutationFailure.Conflict, "Refresh your library before changing it.");
        int at = RootlistOps.PlacementIndex(root.Entries, new RootlistPlacement(op.ParentFolderId));
        var change = op.TargetSaved
            ? new PlaylistOp(PlaylistOpKind.Add, FromIndex: Math.Max(0, at), Items: [new PlaylistMember("", op.EntityKey, null, op.CreatedAtMs)])
            : new PlaylistOp(PlaylistOpKind.Remove, Items: [new PlaylistMember("", op.EntityKey, null, 0)], ItemsAsKey: true);
        // A verified existing follow is a server no-op; do not insert a duplicate URI in the rootlist.
        if (op.TargetSaved && root.Entries.Any(x => x.Kind == 0 && x.Uri == op.EntityKey))
            return op with { Ops = [], BaseRev = root.Revision.ToArray() };
        return op with { Ops = [change], BaseRev = root.Revision.ToArray() };
    }
    public Task<MutationReplayResult> Replay(OutboxOp op, ITransport transport, SessionContext ctx, CancellationToken ct)
        => op.Ops is { Count: 0 }
            ? Task.FromResult(new MutationReplayResult(MutationReplayDisposition.Applied,
                Rootlist: new RootlistReadResult(_replicas.ReadConfirmedRootlist().Entries, op.BaseRev)))
            : RootlistProtocol.PostAsync(_replicas.ReadConfirmedRootlist(), transport, _baseUrl(), ctx, op.Ops ?? [], ct);
}

/// <summary>Finding #2 (library-v3-1-findings-2026-09-06.md Part 2 2.1 #2): a pure, unit-testable classification of
/// a transport reply for an op this outbox owns end to end (a "set" like/unlike/save, or an oprebase playlist edit).
/// A network failure (no confirmed response) or a 5xx/429 is ambiguous about whether the server ever applied the
/// write — it should stay Pending with backoff and replay on reconnect, never jump straight to Verify/NeedsAttention.
/// Only a definitive 4xx is a Verify/NeedsAttention candidate. Consumed by <c>SetReplayStrategy.IdempotentReply</c>
/// and <c>OpRebaseStrategy.OpRebaseReply</c>; deliberately NOT consumed by <see cref="MutationReply.Rejected"/>,
/// whose 0/5xx mapping stays Verify for a different, shared caller — see that method's remarks.</summary>
public enum MutationReplyClass { Retry, Verify, Rejected }

public static class MutationReplyRules
{
    public static MutationReplyClass Classify(int status, Exception? exception = null)
    {
        // A transport-class exception means no confirmed server response at all: ambiguous, retry with backoff.
        // Anything else thrown on the replay path (an un-encodable op, a bad argument) is deterministic — the request
        // was never sent, so it is definitively not applied and must not burn ten retries looking like a network fault.
        if (exception is not null) return IsTransportFailure(exception) ? MutationReplyClass.Retry : MutationReplyClass.Rejected;
        if (status == 0 || status >= 500 || status == 429) return MutationReplyClass.Retry;   // ambiguous / rate-limited
        if (status == 409) return MutationReplyClass.Verify;                    // conflict: an earlier attempt may already have landed
        return MutationReplyClass.Rejected;                                     // definitive 4xx (400/403/404/412/…): never retryable
    }

    public static bool IsTransportFailure(Exception exception) => exception is System.Net.Http.HttpRequestException
        or System.IO.IOException or System.Net.Sockets.SocketException or TimeoutException or OperationCanceledException
        || exception.InnerException is { } inner && IsTransportFailure(inner);
}

/// <summary>Response reduction against exactly the attempted state, excluding later local edits.</summary>
public static class MutationReply
{
    /// <summary>Kept at its ORIGINAL (pre-finding-#2) status mapping deliberately: this is the sole classifier behind
    /// <c>RootlistProtocol.PostAsync</c>, which is shared by <c>RootlistFollowStrategy</c> (drain-replayed) AND
    /// <c>PlaylistMutationSource.RunRootlistOpAsync</c> — the rootlist-edit structural seam (folder/move ops), which
    /// is NOT part of this outbox lane and rejects+rolls back its optimistic overlay on ANY Retry disposition (a
    /// contract five RootlistFolderOpsTests/RootlistMoveSeamTests pin). Broadening 0/5xx here to Retry would make an
    /// ambiguous transport fault on a folder move silently revert the user's edit instead of keeping it queued.
    /// <c>SetReplayStrategy.IdempotentReply</c> and <c>OpRebaseStrategy.OpRebaseReply</c> carry finding #2's actual
    /// fix (0/5xx/429 → Retry) for the ops this outbox DOES auto-replay.</summary>
    public static MutationReplayResult Rejected(int status)
    {
        if (status == 409) return new(MutationReplayDisposition.Rebase);
        if (status == 429) return new(MutationReplayDisposition.Retry);
        if (status is 0 or >= 500) return new(MutationReplayDisposition.Verify);
        throw new PlaylistMutationException(status switch { 403 => PlaylistMutationFailure.Forbidden,
            404 or 410 => PlaylistMutationFailure.Deleted, _ => PlaylistMutationFailure.Unknown }, "Spotify rejected that change.");
    }
    public static MutationReplayResult Playlist(PlaylistReplicaBaseline baseline, OutboxOp attempted, byte[] body)
    {
        Pl.SelectedListContent response;
        try { response = Pl.SelectedListContent.Parser.ParseFrom(SpotifyZstd.MaybeDecompressZstd(body)); }
        catch { return new(MutationReplayDisposition.Verify); }
        var revision = PlaylistWireMapper.LastResultingRevision(response);
        if (response.MultipleHeads || response.ChangesRequireResync || !PlaylistRevisions.IsWellFormed(revision)
            || response.Contents is { Truncated: true } || response.Contents is { Pos: > 0 })
            return new(MutationReplayDisposition.Verify, AcknowledgedRevision: revision);
        if (attempted.Type != "create" && !PlaylistRevisions.Equal(baseline.Revision, attempted.BaseRev))
            return new(MutationReplayDisposition.Verify, AcknowledgedRevision: revision);
        try
        {
            var rows = baseline.Members.ToList();
            var syncOps = response.SyncResult is { } sync ? PlaylistWireMapper.MapOps(sync.Ops) : Array.Empty<PlaylistOp>();
            if (response.Contents is not null) rows = PlaylistWireMapper.ParseContents(response).Members.ToList();
            else
            {
                PlaylistDiffApplier.Apply(rows, attempted.Ops ?? []);
                PlaylistDiffApplier.Apply(rows, syncOps);
            }
            var header = PlaylistReplicaReducer.ApplyHeader(PlaylistReplicaReducer.ApplyHeader(baseline.Header, attempted.Ops ?? []), syncOps);
            return new(MutationReplayDisposition.Applied, new PlaylistReadResult(attempted.EntityKey,
                PlaylistReadKind.Snapshot, baseline.Revision, revision, rows.ToImmutableArray(), [], header, HeaderIsComplete: false), AcknowledgedRevision: revision);
        }
        catch (ArgumentOutOfRangeException) { return new(MutationReplayDisposition.Verify, AcknowledgedRevision: revision); }
    }
}

/// <summary>Durable intent sequencing. Every accepted attempt is journalled before transport dispatch; ambiguous
/// replies remain visible for bounded verification and are never replayed automatically.</summary>
public sealed class MutationEngine : IDisposable
{
    readonly LibraryReplicaCoordinator _replicas;
    readonly Dictionary<string, IMutationStrategy> _strategies;
    readonly Dictionary<long, TaskCompletionSource> _completions = new();
    readonly Dictionary<long, PlaylistMutationFailure> _terminal = new();
    readonly Dictionary<long, DateTime> _nextAttempt = new();
    readonly object _gate = new();
    readonly SimpleEvent<string> _pendingChanged = new();
    readonly Func<DateTime> _now;
    readonly IDisposable _rejections;
    readonly IDisposable _changes;
    public MutationEngine(LibraryReplicaCoordinator replicas, IEnumerable<IMutationStrategy> strategies, Func<DateTime>? now = null)
    {
        _replicas = replicas; _strategies = strategies.ToDictionary(x => x.Type); _now = now ?? (() => DateTime.UtcNow);
        _rejections = replicas.IntentRejected.Subscribe(Observers.From<ReplicaDeadLetter>(rejected =>
        {
            lock (_gate) { _terminal[rejected.Intent.Id] = rejected.Failure; DeadLetter.Add(rejected.Intent); }
            Settle(rejected.Intent.Id, rejected.Failure);
        }));
        _changes = replicas.PendingChanged.Subscribe(Observers.From<string>(uri => _pendingChanged.OnNext(uri)));
    }
    public void Dispose()
    {
        _rejections.Dispose(); _changes.Dispose();
        TaskCompletionSource[] pending;
        lock (_gate) { pending = _completions.Values.ToArray(); _completions.Clear(); }
        foreach (var completion in pending) completion.TrySetCanceled();
    }
    public int Pending => _replicas.Intents.Length;
    public int ReplayablePending => _replicas.Intents.Count(x => x.State != ReplicaIntentState.NeedsAttention
        && !string.IsNullOrEmpty(x.OwnerAccount) && x.OwnerAccount == _replicas.Scope.Account);
    public IObservable<string> PendingChanged => _pendingChanged;
    public List<OutboxOp> DeadLetter { get; } = new();
    public int PendingFor(string uri) => _replicas.PendingFor(uri);
    public bool HasPending(string set, string uri) => _replicas.HasPending(set, uri);
    public bool IsEditPending(long id) => _replicas.Intents.Any(x => x.Id == id);
    public bool TryTakeTerminal(long id, out PlaylistMutationFailure kind) { lock (_gate) return _terminal.Remove(id, out kind); }
    public async Task SaveAsync(string set, string uri, bool saved, CancellationToken ct = default)
    { await _replicas.StageAsync("set", uri, set, saved, ct: ct).ConfigureAwait(false); }
    public async Task FollowAsync(string uri, bool follow, string? folder = null, CancellationToken ct = default)
    { await _replicas.StageAsync("rootlist", uri, "playlists", follow, folder: folder, ct: ct).ConfigureAwait(false); }
    public async Task<long> EditAsync(string uri, IReadOnlyList<PlaylistOp> ops, byte[]? baseRev = null, CancellationToken ct = default, IReadOnlyList<Track>? seedTracks = null)
    {
        await _replicas.EnsurePlaylistCachedAsync(uri, ct).ConfigureAwait(false);
        var intent = await _replicas.StageAsync("oprebase", uri, uri, false, ops, baseRev, ct: ct, seedTracks: seedTracks).ConfigureAwait(false);
        return intent.Id;
    }
    public async Task<(long Id, Task Completion)> CreateAsync(string uri, string name, Playlist header, string? folder = null, CancellationToken ct = default)
    {
        var intent = await _replicas.StageCreateAsync(header, folder, ct).ConfigureAwait(false);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _completions[intent.Id] = done;
        // A concurrent protocol drain may settle this durable recipe between staging and this continuation.
        if (!IsEditPending(intent.Id))
        {
            PlaylistMutationFailure? failure;
            lock (_gate) failure = _terminal.TryGetValue(intent.Id, out var rejected) ? rejected : null;
            Settle(intent.Id, failure);
        }
        return (intent.Id, done.Task);
    }
    public async Task Drain(ITransport transport, SessionContext context, CancellationToken ct = default, ReplicaScope? expectedScope = null)
    {
        var scope = expectedScope ?? _replicas.Scope;
        if (scope != _replicas.Scope || scope.Account != context.Account) throw new OperationCanceledException("The library session changed.");
        var live = transport;
        while (live is SwitchableTransport switchable) live = switchable.Inner;
        if (live is StubTransport || string.IsNullOrEmpty(context.Account)) return;
        var blocked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var queued in _replicas.Intents)
        {
            ct.ThrowIfCancellationRequested();
            if (blocked.Contains(queued.EntityKey)) continue;
            if (queued.Type is "oprebase" or "create") await _replicas.EnsurePlaylistCachedAsync(queued.EntityKey, ct).ConfigureAwait(false);
            if (!_replicas.CanReplay(queued, context.Account)) { blocked.Add(queued.EntityKey); continue; }
            lock (_gate) if (_nextAttempt.TryGetValue(queued.Id, out var due) && _now() < due) { blocked.Add(queued.EntityKey); continue; }
            if (!_strategies.TryGetValue(queued.Type, out var strategy)) { blocked.Add(queued.EntityKey); continue; }
            OutboxOp attempt;
            try { attempt = strategy.Prepare(queued); }
            catch (PlaylistMutationException error) { await RejectAsync(queued, error.Kind, error.Message, ct, scope).ConfigureAwait(false); blocked.Add(queued.EntityKey); continue; }
            // Persist exact wire intent first. A crash from this point on enters verification on the next session.
            if (!await _replicas.BeginAttemptAsync(attempt, ct, scope).ConfigureAwait(false)) continue;
            if (scope != _replicas.Scope) throw new OperationCanceledException("The library session changed before dispatch.");
            MutationReplayResult reply;
            try { reply = await strategy.Replay(attempt, transport, context, ct).ConfigureAwait(false); }
            catch (PlaylistMutationException error) { await RejectAsync(attempt, error.Kind, error.Message, ct, scope).ConfigureAwait(false); blocked.Add(attempt.EntityKey); continue; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            // Finding #2: a transport-class exception means the request never confirmed a server response (offline,
            // DNS, socket reset) — ambiguous, so it stays Pending with backoff via Retry. Any other exception is a
            // deterministic failure on our side (the op could not even be encoded): the request was never sent, so
            // the intent is rejected with its reason instead of retrying ten times as if the network were down.
            catch (Exception ex)
            {
                bool ambiguous = MutationReplyRules.Classify(0, ex) == MutationReplyClass.Retry;
                WaveeLog.Instance.Event(WaveeLogLevel.Warning, "playlist-mutations", "outbox.replay.threw",
                    $"outbox replay threw type={attempt.Type} key={attempt.EntityKey} transport={ambiguous} {ex.GetType().Name}: {ex.Message}");
                if (!ambiguous)
                {
                    await RejectAsync(attempt, PlaylistMutationFailure.Conflict, "That change could not be sent: " + ex.Message, ct, scope).ConfigureAwait(false);
                    blocked.Add(attempt.EntityKey);
                    continue;
                }
                reply = new(MutationReplayDisposition.Retry);
            }
            if (reply.Disposition is MutationReplayDisposition.Applied or MutationReplayDisposition.Verify)
            {
                await _replicas.CompleteAsync(attempt, reply.Playlist, reply.Rootlist,
                    reply.Disposition == MutationReplayDisposition.Verify, reply.AcknowledgedRevision, ct, scope).ConfigureAwait(false);
                if (reply.Disposition == MutationReplayDisposition.Applied) Settle(attempt.Id, null);
                else blocked.Add(attempt.EntityKey);
            }
            else
            {
                // Finding #2: HEAD's policy, restored — Retry/Rebase stay Pending for up to 10 attempts with
                // exponential backoff (min 60s, 1s · 2^attempts). This branch is now reachable for the ambiguous
                // transport/5xx cases above, instead of those short-circuiting into Verify and never retrying.
                var retry = attempt with { State = ReplicaIntentState.Pending, Attempts = queued.Attempts + 1 };
                if (retry.Attempts >= 10) await RejectAsync(retry, PlaylistMutationFailure.Conflict, "That change needs to be retried manually.", ct, scope).ConfigureAwait(false);
                else
                {
                    await _replicas.RetryAsync(retry, reply.Disposition == MutationReplayDisposition.Rebase, ct, scope).ConfigureAwait(false);
                    lock (_gate) _nextAttempt[retry.Id] = _now() + TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, queued.Attempts)));
                }
                blocked.Add(attempt.EntityKey);
            }
        }
        SettleVerified();
    }
    public async Task VerificationFailedAsync(OutboxOp intent, CancellationToken ct, ReplicaScope? expectedScope = null)
    {
        if (!_replicas.Intents.Any(x => x.Id == intent.Id)) { Settle(intent.Id, null); return; }
        int attempts = intent.Attempts + 1;
        await _replicas.SetIntentAsync(intent with { Attempts = attempts,
            State = attempts >= 3 ? ReplicaIntentState.NeedsAttention : ReplicaIntentState.AwaitingVerification }, ct, expectedScope).ConfigureAwait(false);
        if (attempts >= 3) Settle(intent.Id, PlaylistMutationFailure.Conflict);
    }
    public void SettleVerified()
    {
        long[] ids; lock (_gate) ids = _completions.Keys.ToArray();
        foreach (var id in ids) if (!IsEditPending(id)) Settle(id, null);
    }
    async Task RejectAsync(OutboxOp intent, PlaylistMutationFailure kind, string reason, CancellationToken ct, ReplicaScope scope)
    {
        await _replicas.RejectAsync(intent, kind, reason, ct, scope).ConfigureAwait(false);
    }

    void Settle(long id, PlaylistMutationFailure? failure)
    {
        TaskCompletionSource? done;
        lock (_gate) { _completions.Remove(id, out done); _nextAttempt.Remove(id); }
        if (failure is { } kind) done?.TrySetException(new PlaylistMutationException(kind, "That change could not be verified. Review it before retrying."));
        else done?.TrySetResult();
    }
}
