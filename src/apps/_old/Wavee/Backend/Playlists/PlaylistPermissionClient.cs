using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Core;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee.Backend.Playlists;

/// <summary>The desktop permission dialect for <c>/playlist-permission/v1/playlist/{id}/permission/base</c> — PROTOBUF,
/// not JSON, and not revision-chained (verified 2026-08-15: <c>GET …/permission/base</c> answers
/// <c>Permission{revision, level}</c> and <c>POST …/permission/base/level</c> takes a two-byte
/// <c>SetPermissionLevelRequest</c>, <c>08 01</c> = BLOCKED / <c>08 02</c> = VIEWER).
/// <para>The client is STATELESS on purpose: the permission revision lives on the store header
/// (<c>Playlist.BasePermissionRevision</c>, fed by this GET on open and by the dealer's <c>permission/state</c> push),
/// so there is no second copy to go stale and no "default" sentinel to invent.</para></summary>
public sealed class PlaylistPermissionClient
{
    readonly ITransport _transport;

    public PlaylistPermissionClient(ITransport transport) => _transport = transport;

    static Dictionary<string, string> GetHeaders => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Accept"] = "application/protobuf",
    };

    static Dictionary<string, string> PostHeaders => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Content-Type"] = "application/protobuf",
        ["Accept"] = "application/protobuf",
    };

    /// <summary>Read the base permission. <c>null</c> means "there is nothing to read": a 404 is what a freshly created
    /// playlist answers (desktop gets one too, right after create) and any other failure is logged and treated the same
    /// — the caller keeps whatever the store already believes rather than flipping a playlist public.</summary>
    public async Task<PlaylistBasePermission?> GetBasePermissionAsync(string playlistUri, CancellationToken ct = default)
    {
        var id = EntityUri.IdOf(playlistUri);
        var r = await _transport.Request(Channel.Spclient,
            $"/playlist-permission/v1/playlist/{id}/permission/base",
            ReadOnlyMemory<byte>.Empty, ct, "GET", GetHeaders).ConfigureAwait(false);
        if (r.Status == 404) return null;                    // fresh playlist — no permission row yet
        if (!r.Ok)
        {
            PlaylistMutationDiagnostics.PermissionGetFailed(playlistUri, r.Status);
            return null;
        }
        return ParsePermission(r.Body);
    }

    /// <summary>Set the base permission. The request carries the level ALONE — no revision, so there is no chain to
    /// rebase; a 409 is the server telling us it raced someone else, which we answer with one refresh + one retry.</summary>
    public async Task<PlaylistBasePermission> SetBasePermissionAsync(
        string playlistUri, PlaylistPermissionLevel level, CancellationToken ct = default)
    {
        var r = await PostLevelAsync(playlistUri, level, ct).ConfigureAwait(false);
        if (r.Status == 409)
        {
            PlaylistMutationDiagnostics.PermissionConflict(playlistUri);
            await GetBasePermissionAsync(playlistUri, ct).ConfigureAwait(false);
            r = await PostLevelAsync(playlistUri, level, ct).ConfigureAwait(false);
            if (r.Status == 409)
                throw new PlaylistMutationException(PlaylistMutationFailure.Conflict,
                    "That playlist's sharing settings changed while you were editing them — try again.");
        }
        if (r.Status == 403)
        {
            PlaylistMutationDiagnostics.PermissionSetFailed(playlistUri, r.Status, level);
            throw new PlaylistMutationException(PlaylistMutationFailure.Forbidden,
                "You no longer have permission to change who can see that playlist.");
        }
        if (!r.Ok)
        {
            PlaylistMutationDiagnostics.PermissionSetFailed(playlistUri, r.Status, level);
            throw new PlaylistMutationException(PlaylistMutationFailure.Unknown,
                "That sharing change could not be saved.");
        }
        return ParseSetResponse(r.Body)
            ?? throw new PlaylistMutationException(PlaylistMutationFailure.Unknown,
                "That sharing change could not be saved.");
    }

    Task<Resp> PostLevelAsync(string playlistUri, PlaylistPermissionLevel level, CancellationToken ct)
        => _transport.Request(Channel.Spclient,
            $"/playlist-permission/v1/playlist/{EntityUri.IdOf(playlistUri)}/permission/base/level",
            BuildSetLevel(level), ct, "POST", PostHeaders);

    /// <summary>The two-byte request body: <c>SetPermissionLevelRequest{permission_level}</c>.</summary>
    public static byte[] BuildSetLevel(PlaylistPermissionLevel level)
        => new Pl.SetPermissionLevelRequest { PermissionLevel = WireLevel(level) }.ToByteArray();

    /// <summary>Parse a <c>Permission</c> body. The revision is 8 opaque bytes (or the "default" sentinel string on a
    /// never-set playlist) and is carried as lowercase hex — it is NOT a playlist4 revision and never enters a
    /// membership/rootlist revision slot (invariant I1).</summary>
    public static PlaylistBasePermission? ParsePermission(byte[] body)
    {
        Pl.Permission perm;
        try { perm = Pl.Permission.Parser.ParseFrom(body); }
        catch (InvalidProtocolBufferException) { return null; }
        if (!perm.HasPermissionLevel) return null;
        return new PlaylistBasePermission(DomainLevel(perm.PermissionLevel), Convert.ToHexStringLower(perm.Revision.Span));
    }

    /// <summary>Parse a <c>SetPermissionResponse</c> body into its resulting permission.</summary>
    public static PlaylistBasePermission? ParseSetResponse(byte[] body)
    {
        Pl.SetPermissionResponse resp;
        try { resp = Pl.SetPermissionResponse.Parser.ParseFrom(body); }
        catch (InvalidProtocolBufferException) { return null; }
        if (resp.ResultingPermission is not { } p || !p.HasPermissionLevel) return null;
        return new PlaylistBasePermission(DomainLevel(p.PermissionLevel), Convert.ToHexStringLower(p.Revision.Span));
    }

    static PlaylistPermissionLevel DomainLevel(Pl.PermissionLevel wire) => wire switch
    {
        Pl.PermissionLevel.Blocked => PlaylistPermissionLevel.Blocked,
        Pl.PermissionLevel.Contributor => PlaylistPermissionLevel.Contributor,
        _ => PlaylistPermissionLevel.Viewer,
    };

    static Pl.PermissionLevel WireLevel(PlaylistPermissionLevel level) => level switch
    {
        PlaylistPermissionLevel.Blocked => Pl.PermissionLevel.Blocked,
        PlaylistPermissionLevel.Contributor => Pl.PermissionLevel.Contributor,
        _ => Pl.PermissionLevel.Viewer,
    };

}
