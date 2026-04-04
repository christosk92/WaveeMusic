using System.Collections.Generic;
using System.Threading;
using CommunityToolkit.Mvvm.Messaging.Messages;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.Data.Messages;

/// <summary>
/// Sent by PlaybackStateService when a new track starts playing.
/// TrackMetadataEnricher picks this up and fetches full API metadata.
/// </summary>
public sealed class TrackEnrichmentRequestMessage(string trackUri)
    : ValueChangedMessage<string>(trackUri);

/// <summary>
/// Sent by TrackMetadataEnricher with full API-fetched track metadata.
/// PlaybackStateService receives this and overwrites UI properties.
/// </summary>
public sealed class TrackMetadataEnrichedMessage
{
    public required string TrackId { get; init; }
    public required string TrackUri { get; init; }
    public string? Title { get; init; }
    public string? ArtistName { get; init; }
    public string? AlbumArt { get; init; }
    public string? AlbumArtLarge { get; init; }
    public string? ArtistId { get; init; }
    public string? AlbumId { get; init; }
}

/// <summary>
/// Request from ArtistService for extended top tracks.
/// TrackMetadataEnricher handles this if it exists (post-connect).
/// </summary>
public sealed class ExtendedTopTracksRequest : AsyncRequestMessage<List<ArtistTopTrackResult>>
{
    public required string ArtistUri { get; init; }
    public CancellationToken CancellationToken { get; init; }
}
