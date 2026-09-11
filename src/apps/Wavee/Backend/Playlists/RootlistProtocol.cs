using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Spotify;
using Wavee.Backend.Sync;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee.Backend.Playlists;

/// <summary>Rootlist transport observations. Callers execute these on LibrarySync and commit through the replica.</summary>
public static class RootlistProtocol
{
    public static async Task<RootlistReadResult> ReadAsync(ITransport transport, SessionContext context, CancellationToken ct)
    {
        var reply = await transport.Request(Channel.Spclient,
            $"/playlist/v2/user/{context.Account}/rootlist?decorate=revision", ReadOnlyMemory<byte>.Empty, ct, "GET",
            new Dictionary<string, string> { ["Accept"] = "application/protobuf" }).ConfigureAwait(false);
        if (!reply.Ok) throw new InvalidOperationException("The rootlist could not be refreshed.");
        var body = Pl.SelectedListContent.Parser.ParseFrom(SpotifyZstd.MaybeDecompressZstd(reply.Body));
        return PlaylistFetcher.ReadRootlist(body);
    }

    public static async Task<MutationReplayResult> PostAsync(RootlistReplicaBaseline baseline, ITransport transport,
        string baseUrl, SessionContext context, IReadOnlyList<PlaylistOp> ops, CancellationToken ct)
    {
        if (!PlaylistRevisions.IsWellFormed(baseline.Revision))
            throw new InvalidOperationException("Rootlist operations require a confirmed base revision.");
        var body = PlaylistWireMapper.BuildRootlistChanges(baseline.Revision, ops, context.Account,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var reply = await transport.Request(Channel.Spclient, $"/playlist/v2/user/{context.Account}/rootlist/changes",
            body, ct, "POST", SpotifyHeaders.PlaylistV2Mutation(context.Locale, baseUrl)).ConfigureAwait(false);
        if (!reply.Ok) return MutationReply.Rejected(reply.Status);
        Pl.SelectedListContent content;
        try { content = Pl.SelectedListContent.Parser.ParseFrom(SpotifyZstd.MaybeDecompressZstd(reply.Body)); }
        catch { return new(MutationReplayDisposition.Verify); }
        var revision = PlaylistWireMapper.LastResultingRevision(content);
        if (content.MultipleHeads || content.ChangesRequireResync || !PlaylistRevisions.IsWellFormed(revision))
            return new(MutationReplayDisposition.Verify, AcknowledgedRevision: revision);
        try
        {
            if (content.Contents is not null)
                return new(MutationReplayDisposition.Applied, Rootlist: PlaylistFetcher.ReadRootlist(content) with { Revision = revision });
            var members = baseline.Entries.Select(x => new PlaylistMember("", x.Uri, null, x.AddedAtMs)).ToList();
            PlaylistDiffApplier.Apply(members, ops);
            if (content.SyncResult is { } sync) PlaylistDiffApplier.Apply(members, PlaylistWireMapper.MapOps(sync.Ops));
            var entries = RootlistTreeBuilder.EntriesFromUris(members.Select(x => x.ItemUri), members.Select(x => x.AddedAt).ToArray());
            return new(MutationReplayDisposition.Applied, Rootlist: new RootlistReadResult(entries.ToImmutableArray(), revision));
        }
        catch (ArgumentOutOfRangeException) { return new(MutationReplayDisposition.Verify, AcknowledgedRevision: revision); }
    }
}
