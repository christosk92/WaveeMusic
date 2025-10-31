using Wavee.Connect.Playback.Abstractions;

namespace Wavee.Connect.Playback.Decoders;

/// <summary>
/// Registry for audio decoders with automatic format detection.
/// </summary>
public sealed class AudioDecoderRegistry
{
    private readonly List<IAudioDecoder> _decoders = new();

    /// <summary>
    /// Registers an audio decoder.
    /// </summary>
    public void Register(IAudioDecoder decoder)
    {
        _decoders.Add(decoder);
    }

    /// <summary>
    /// Finds a decoder that can decode the given stream.
    /// </summary>
    public IAudioDecoder? FindDecoder(Stream stream)
    {
        var originalPosition = stream.Position;

        foreach (var decoder in _decoders)
        {
            stream.Position = originalPosition;
            if (decoder.CanDecode(stream))
            {
                stream.Position = originalPosition;
                return decoder;
            }
        }

        stream.Position = originalPosition;
        return null;
    }

    /// <summary>
    /// Gets the audio format from the stream using the appropriate decoder.
    /// </summary>
    public async Task<(IAudioDecoder decoder, AudioFormat format)> DetectFormatAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        var decoder = FindDecoder(stream);
        if (decoder == null)
            throw new NotSupportedException("No decoder found for audio format");

        var format = await decoder.GetFormatAsync(stream, cancellationToken);
        return (decoder, format);
    }
}
