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

    bool TryGetVideoUri(string audioTrackUri, out string videoTrackUri);

    Task<string?> TryResolveVideoUriViaExtendedMetadataAsync(
        string audioTrackUri,
        CancellationToken cancellationToken = default);

    Task<string?> ResolveManifestIdAsync(
        string audioTrackUri,
        CancellationToken cancellationToken = default);

    void NoteHasVideo(string audioTrackUri, bool hasVideo);

    void NoteVideoUri(string audioTrackUri, string videoTrackUri);

    void NoteManifestId(string audioTrackUri, string manifestId);
}
