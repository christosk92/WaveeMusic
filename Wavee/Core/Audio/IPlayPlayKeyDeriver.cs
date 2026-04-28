namespace Wavee.Core.Audio;

// PlayPlay is Spotify property. Fallback when the AP audio-key channel returns
// a permanent error or repeated timeouts. No deriver registered = AP-only
// behavior, unchanged.
public interface IPlayPlayKeyDeriver
{
    Task<byte[]> DeriveAsync(SpotifyId trackId, FileId fileId, CancellationToken cancellationToken = default);
}
