namespace Wavee.Sdk;

/// <summary>
/// Bytes served by the module itself (the <c>"stream"</c> locator): the app pulls ranges over
/// <c>stream/open</c> / <c>stream/read</c> / <c>stream/close</c> and each read comes back as a raw binary frame —
/// no base64, no JSON parse on the audio path.
/// </summary>
public interface IModuleStream : IDisposable
{
    /// <summary>Total length in bytes when known, null when it is not (yet).</summary>
    long? Length { get; }

    /// <summary>True when the module can serve an arbitrary <c>offset</c>; false means forward-only.</summary>
    bool Seekable { get; }

    /// <summary>MIME type of the bytes when known (drives codec selection before any sniffing).</summary>
    string? ContentType { get; }

    /// <summary>
    /// Reads up to <paramref name="dst"/>.Length bytes starting at <paramref name="offset"/>.
    /// Returns 0 only at end of stream.
    /// </summary>
    /// <param name="offset">Absolute byte offset into the logical stream.</param>
    /// <param name="dst">Destination buffer.</param>
    /// <param name="ct">Cancels the read.</param>
    ValueTask<int> ReadAsync(long offset, Memory<byte> dst, CancellationToken ct);
}
