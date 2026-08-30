using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Wavee.Backend.Playlists;
using Wavee.Backend.Sync;
using Wavee.Core;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee.Backend.Realtime;

// ── The single dealer firehose router (decode → enqueue) ──────────────────────────────────────────────────────────────
// ONE ITransport.Events("hm://") subscription carries both arms; this decodes the topic + protos and enqueues typed
// commands onto the LibrarySync loop — it NO LONGER writes the store itself (single-writer: the in-place apply / mark-dirty
// / refetch policy all live in the loop). Playlist pushes → PlaylistPush; the rootlist topic → RootlistPush; collection
// pushes pass the RAW payload through so the loop can interpret it. Unit-tested via StubTransport.PushEvent + a real loop.
//
// I7 — every inbound frame is accounted for: an unparseable payload, a malformed head, an unusable uri and a duplicate
// head each take a LOGGED drop (PlaylistMutationDiagnostics.DealerDrop / RootlistPushDeduped). Nothing is swallowed.
public sealed class DealerRouter : IDisposable
{
    const int RecentHeadSlots = 4;   // the v2 + non-v2 copies of one rootlist head arrive same/adjacent ms; 4 tolerates interleave

    readonly LibrarySync _sync;
    readonly IDisposable _sub;
    readonly object _headGate = new();
    readonly byte[]?[] _recentHeads = new byte[]?[RecentHeadSlots];
    int _recentHeadWrite;

    /// <summary>Rootlist heads dropped because the SAME new_revision was already enqueued (the wire delivers every head
    /// twice — once on hm://playlist/v2/user/{u}/rootlist and once on hm://playlist/user/{u}/rootlist). One enqueue per
    /// head is the contract; this counter is how the replay harness proves it.</summary>
    public int RootlistPushDeduped;

    /// <summary>Frames dropped with a logged reason (unparseable / malformed head / unusable uri).</summary>
    public int DealerDrops;

    public DealerRouter(ITransport transport, LibrarySync sync)
    {
        _sync = sync;
        _sub = transport.Events("hm://").Subscribe(new Observer(this));
    }

    void OnEvent(WireEvent e)
    {
        if (e.Topic.StartsWith("hm://playlist/", StringComparison.Ordinal)) OnPlaylist(e);
        else if (e.Topic.StartsWith("hm://playlist-permission/", StringComparison.Ordinal)) OnPermission(e);
        else if (e.Topic.StartsWith("hm://collection/", StringComparison.Ordinal)) OnCollection(e);
    }

    // hm://playlist-permission/v1/playlist/{id}/permission/state — the authoritative public/private/collaborative state.
    // The payload is a PermissionStatePub and carries NO uri, so the playlist comes from the topic. base_permission is
    // the whole point of the frame: without it there is nothing to adopt, so that is a logged drop (I7), not a guess.
    void OnPermission(WireEvent e)
    {
        Pl.PermissionStatePub pub;
        try { pub = Pl.PermissionStatePub.Parser.ParseFrom(e.Payload); }
        catch { Drop(e, "unparseable"); return; }

        var state = pub.PermissionState;
        var basePermission = state?.Permissions?.BasePermission;
        if (state is null || basePermission is null) { Drop(e, "no-base-permission"); return; }

        var uri = PlaylistUriFromPermissionTopic(e.Topic);
        if (uri.Length == 0) { Drop(e, "no-uri"); return; }

        var push = new PlaylistPermissionPush(
            uri,
            LevelOf(basePermission.PermissionLevel),
            basePermission.HasRevision ? Convert.ToHexStringLower(basePermission.Revision.Span) : "",
            state.IsPrivate,
            state.IsCollaborative);
        _sync.Enqueue(new SyncCommand(SyncKind.PermissionPush, uri, Permission: push));
    }

    static Wavee.Core.PlaylistPermissionLevel LevelOf(Pl.PermissionLevel level) => level switch
    {
        Pl.PermissionLevel.Blocked => Wavee.Core.PlaylistPermissionLevel.Blocked,
        Pl.PermissionLevel.Contributor => Wavee.Core.PlaylistPermissionLevel.Contributor,
        _ => Wavee.Core.PlaylistPermissionLevel.Viewer,
    };

    // "hm://playlist-permission/v1/playlist/{base62}/permission/state" → "spotify:playlist:{base62}".
    static string PlaylistUriFromPermissionTopic(string topic)
    {
        const string marker = "/playlist/";
        int i = topic.IndexOf(marker, "hm://playlist-permission".Length, StringComparison.Ordinal);
        if (i < 0) return "";
        var rest = topic[(i + marker.Length)..];
        int cut = rest.IndexOf('/');
        var id = cut < 0 ? rest : rest[..cut];
        return id.Length == 0 ? "" : "spotify:playlist:" + id;
    }

    void OnPlaylist(WireEvent e)
    {
        if (e.Topic.EndsWith("/rootlist", StringComparison.Ordinal)) { OnRootlist(e); return; }

        Pl.PlaylistModificationInfo info;
        try { info = Pl.PlaylistModificationInfo.Parser.ParseFrom(e.Payload); }
        catch { Drop(e, "unparseable"); return; }

        string uri = info.HasUri ? Encoding.UTF8.GetString(info.Uri.Span) : PlaylistUriFromTopic(e.Topic);
        if (uri.Length == 0) { Drop(e, "no-uri"); return; }
        if (EntityUri.Parse(uri).Provider != EntityProviders.Spotify) { Drop(e, "not-a-spotify-uri"); return; }

        var newRev = info.HasNewRevision ? info.NewRevision.ToByteArray() : null;
        IReadOnlyList<PlaylistOp> ops;
        // An op shape this client cannot express (a MOV anchored with add_before_item) is not "apply what we understood"
        // — it is a drop with a reason, and the head we still hold makes the next read converge.
        try { ops = PlaylistWireMapper.MapOps(info.Ops); }
        catch (ArgumentOutOfRangeException) { Drop(e, "unsupported-op"); return; }
        // Nothing actionable: no storable head to compare or adopt AND no ops to apply. The dealer really does send these
        // (hm://playlist/v2/list/liked-songs-artist/… carries a non-playlist uri and no revision at all) — enqueuing them
        // would only mark playlists dirty for no reason.
        if (!PlaylistRevisions.IsWellFormed(newRev) && ops.Count == 0) { Drop(e, "no-head-no-ops"); return; }

        _sync.Enqueue(new SyncCommand(SyncKind.PlaylistPush, uri,
            ParentRev: info.HasParentRevision ? info.ParentRevision.ToByteArray() : null,
            NewRev: newRev,
            Ops: ops));
    }

    // The rootlist topic carries a head-only PlaylistModificationInfo (uri + new_revision, no parent, no ops), NOT the
    // sibling RootlistModificationInfo the topic suggests — the two messages differ by exactly one field slot, so parsing
    // a rootlist push as an RMI lands the URI BYTES in new_revision and (before this) persisted them as the rootlist
    // revision. Sniff the shape, then gate the head on I1 before anything reaches the loop.
    void OnRootlist(WireEvent e)
    {
        if (!TryDecodeRootlistPush(e.Payload, out var parentRev, out var newRev, out var ops, out var reason))
        { Drop(e, reason); return; }

        if (!TakeNewHead(newRev!))
        {
            Interlocked.Increment(ref RootlistPushDeduped);
            PlaylistMutationDiagnostics.RootlistPushDeduped(e.Topic);
            return;
        }

        _sync.Enqueue(new SyncCommand(SyncKind.RootlistPush, ParentRev: parentRev, NewRev: newRev, Ops: ops));
    }

    /// <summary>Decode a rootlist push into (parent, head, ops). PMI first: a payload whose field 1 is a
    /// <c>spotify:</c> uri IS a PlaylistModificationInfo (head-only — the shape every observed rootlist push takes).
    /// A real RootlistModificationInfo puts its 24-byte new_revision in that slot, which fails the prefix test, so the
    /// fallthrough is exact and the ops-carrying RMI stays supported. A non-24-byte head is never storable → drop.</summary>
    internal static bool TryDecodeRootlistPush(byte[] payload, out byte[]? parentRev, out byte[]? newRev,
                                               out IReadOnlyList<PlaylistOp>? ops, out string reason)
    {
        parentRev = null; newRev = null; ops = null;
        Pl.PlaylistModificationInfo pmi;
        try { pmi = Pl.PlaylistModificationInfo.Parser.ParseFrom(payload); }
        catch { reason = "unparseable"; return false; }

        try
        {
            if (pmi.HasUri && IsSpotifyUri(pmi.Uri.Span))
            {
                newRev = pmi.HasNewRevision ? pmi.NewRevision.ToByteArray() : null;
                parentRev = pmi.HasParentRevision ? pmi.ParentRevision.ToByteArray() : null;
                ops = PlaylistWireMapper.MapOps(pmi.Ops);
            }
            else
            {
                Pl.RootlistModificationInfo rmi;
                try { rmi = Pl.RootlistModificationInfo.Parser.ParseFrom(payload); }
                catch { reason = "unparseable"; return false; }
                newRev = rmi.HasNewRevision ? rmi.NewRevision.ToByteArray() : null;
                parentRev = rmi.HasParentRevision ? rmi.ParentRevision.ToByteArray() : null;
                ops = PlaylistWireMapper.MapOps(rmi.Ops);
            }
        }
        catch (ArgumentOutOfRangeException)   // an op shape this client cannot express → drop with a reason (I7)
        {
            reason = "unsupported-op";
            parentRev = null; newRev = null; ops = null;
            return false;
        }

        if (!PlaylistRevisions.IsWellFormed(newRev))
        {
            reason = "bad-revision:" + (newRev?.Length ?? 0);
            parentRev = null; newRev = null; ops = null;
            return false;
        }
        reason = "";
        return true;
    }

    static readonly byte[] SpotifyPrefix = Encoding.ASCII.GetBytes("spotify:");

    static bool IsSpotifyUri(ReadOnlySpan<byte> uri) => uri.StartsWith(SpotifyPrefix);

    /// <summary>Record a head in the recent ring; false when it was already seen (the duplicate copy of a pair).</summary>
    bool TakeNewHead(byte[] head)
    {
        lock (_headGate)
        {
            for (int i = 0; i < _recentHeads.Length; i++)
                if (PlaylistRevisions.Equal(_recentHeads[i], head)) return false;
            _recentHeads[_recentHeadWrite] = head;
            _recentHeadWrite = (_recentHeadWrite + 1) % _recentHeads.Length;
            return true;
        }
    }

    void Drop(WireEvent e, string reason)
    {
        Interlocked.Increment(ref DealerDrops);
        PlaylistMutationDiagnostics.DealerDrop(e.Topic, reason, e.Payload?.Length ?? 0);
    }

    // Pass the RAW payload through — the loop attempts the PubSubUpdate parse (Phase 3); router stays parse-only for playlist4.
    void OnCollection(WireEvent e)
        => _sync.Enqueue(new SyncCommand(SyncKind.CollectionPush, CollectionSetFromTopic(e.Topic), Payload: e.Payload));

    // "hm://playlist/v2/playlist/{base62}" → "spotify:playlist:{base62}".
    static string PlaylistUriFromTopic(string topic)
    {
        int i = topic.LastIndexOf('/');
        return i >= 0 && i + 1 < topic.Length ? "spotify:playlist:" + topic[(i + 1)..] : "";
    }

    // "hm://collection/{set}/{user}[/json]" → a best-effort set hint for the freshness invalidation.
    static string CollectionSetFromTopic(string topic)
    {
        var rest = topic.StartsWith("hm://collection/", StringComparison.Ordinal) ? topic["hm://collection/".Length..] : topic;
        int slash = rest.IndexOf('/');
        return slash > 0 ? rest[..slash] : rest;
    }

    sealed class Observer(DealerRouter owner) : IObserver<WireEvent>
    {
        public void OnNext(WireEvent e) => owner.OnEvent(e);
        public void OnCompleted() { }
        public void OnError(Exception e) { }
    }

    public void Dispose() => _sub.Dispose();
}
