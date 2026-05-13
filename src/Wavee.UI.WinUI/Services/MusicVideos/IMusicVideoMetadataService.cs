using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.UI.WinUI.Services;

public interface IMusicVideoMetadataService
{
    bool? GetKnownAvailability(string audioTrackUri);

    Task<IReadOnlyDictionary<string, bool>> GetCachedAvailabilityAsync(
        IEnumerable<string> trackUris,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, bool>> EnsureAvailabilityAsync(
        IEnumerable<string> trackUris,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// One-shot helper for any track-list VM that wants the "has linked local
    /// video" badge on its rows. Pulls Spotify track URIs out of <paramref name="items"/>
    /// via <paramref name="getSpotifyTrackUri"/>, runs
    /// <see cref="EnsureAvailabilityAsync"/> over the deduped set, and (on the
    /// UI thread) calls <paramref name="setHasVideo"/> for each item whose URI
    /// has a linked local video. Items without a URI / without a link are
    /// untouched.
    /// </summary>
    Task ApplyAvailabilityToAsync<T>(
        IReadOnlyList<T> items,
        Func<T, string?> getSpotifyTrackUri,
        Action<T, bool> setHasVideo,
        CancellationToken cancellationToken = default);

    bool TryGetVideoUri(string audioTrackUri, out string videoTrackUri);

    bool TryGetAudioUri(string videoTrackUri, out string audioTrackUri);

    Task<string?> TryResolveVideoUriViaExtendedMetadataAsync(
        string audioTrackUri,
        CancellationToken cancellationToken = default);

    Task<string?> TryResolveAudioUriViaExtendedMetadataAsync(
        string videoTrackUri,
        CancellationToken cancellationToken = default);

    Task<string?> ResolveManifestIdAsync(
        string audioTrackUri,
        CancellationToken cancellationToken = default);

    void NoteHasVideo(string audioTrackUri, bool hasVideo);

    void NoteVideoUri(string audioTrackUri, string videoTrackUri);

    void ForgetVideoAssociation(string audioTrackUri);

    void NoteManifestId(string audioTrackUri, string manifestId);
}
