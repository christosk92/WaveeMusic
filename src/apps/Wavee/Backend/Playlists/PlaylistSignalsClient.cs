using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee.Backend.Playlists;

/// <summary>Initiator for Spotify playlist session controls: protobuf POST /signals -> full playlist snapshot.</summary>
public sealed class PlaylistSignalsClient
{
    readonly IHttpExchange _http;
    readonly Func<string> _baseUrl;
    readonly Func<string> _language;

    public PlaylistSignalsClient(IHttpExchange http, Func<string> baseUrl, Func<string> language)
    {
        _http = http;
        _baseUrl = baseUrl;
        _language = language;
    }

    public async Task<Pl.SelectedListContent> ApplyAsync(
        string playlistUri,
        byte[] revision,
        string optionIdentifier,
        CancellationToken ct = default)
    {
        if (revision.Length != 24) throw new ArgumentException("Playlist signal revision must be exactly 24 bytes.", nameof(revision));
        if (string.IsNullOrWhiteSpace(optionIdentifier)) throw new ArgumentException("A playlist signal identifier is required.", nameof(optionIdentifier));

        var signal = new Pl.AvailableSignal
        {
            Identifier = optionIdentifier,
            Interaction = new Pl.PlaylistSignalInteraction { Uuid = Guid.NewGuid().ToString("D") },
        };
        var request = new Pl.ApplyPlaylistSignals { Revision = ByteString.CopyFrom(revision) };
        request.Signals.Add(signal);

        string baseUrl = _baseUrl().TrimEnd('/');
        string url = baseUrl + "/playlist/v2/playlist/" + Uri.EscapeDataString(EntityUri.IdOf(playlistUri)) + "/signals";
        var headers = SpotifyHeaders.PlaylistSignals(_language(), baseUrl);
        byte[] body = request.ToByteArray();

        byte[] responseBody;
        using (var response = await _http.SendAsync(new HttpReq("POST", url, headers, body), ct).ConfigureAwait(false))
        {
            if (response.Status != 200)
                throw new InvalidOperationException($"playlist signals failed ({response.Status}) for {playlistUri}");
            using var buffer = new MemoryStream();
            await response.Body.CopyToAsync(buffer, ct).ConfigureAwait(false);
            responseBody = buffer.ToArray();
        }

        Pl.SelectedListContent snapshot;
        try { snapshot = Pl.SelectedListContent.Parser.ParseFrom(SpotifyZstd.MaybeDecompressZstd(responseBody)); }
        catch (Exception ex) { throw new InvalidDataException("Playlist signals returned malformed protobuf.", ex); }

        if (!snapshot.HasRevision || snapshot.Revision.Length != 24)
            throw new InvalidDataException("Playlist signals response did not contain a 24-byte revision.");
        if (snapshot.Contents is null)
            throw new InvalidDataException("Playlist signals response did not contain a full playlist snapshot.");
        if (snapshot.Revision.Span.SequenceEqual(revision))
            throw new InvalidDataException("Playlist signals response did not advance the playlist revision.");
        return snapshot;
    }

}
