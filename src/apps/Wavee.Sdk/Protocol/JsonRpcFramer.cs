using System.Globalization;
using System.Text;

namespace Wavee.Sdk.Protocol;

/// <summary>One decoded frame: either a UTF-8 JSON envelope or a raw binary payload answering a request id.</summary>
public sealed class JsonRpcFrame
{
    /// <summary>True when the frame carried <c>Content-Type: application/octet-stream</c>.</summary>
    public required bool IsBinary { get; init; }

    /// <summary>The frame body: UTF-8 JSON when <see cref="IsBinary"/> is false, raw bytes when it is true.</summary>
    public required byte[] Payload { get; init; }

    /// <summary>The <c>X-Wavee-Request</c> id a binary frame answers; 0 for JSON frames.</summary>
    public long RequestId { get; init; }

    /// <summary>The <c>X-Wavee-Eof</c> flag of a binary frame.</summary>
    public bool Eof { get; init; }
}

/// <summary>
/// LSP-style framing over a byte stream: <c>Content-Length: N\r\n\r\n</c> followed by N bytes. A frame that also
/// carries <c>Content-Type: application/octet-stream</c> is a <b>binary</b> frame and is routed by its
/// <c>X-Wavee-Request</c> header instead of being parsed as JSON — that is the audio path, so it never sees base64.
/// The reader buffers, so a frame split across reads and several frames inside one read both work.
/// </summary>
/// <param name="input">The stream to read frames from.</param>
public sealed class JsonRpcFramer(Stream input)
{
    private const string BinaryContentType = "application/octet-stream";
    private const int MaxHeaderBytes = 8 * 1024;

    private readonly Stream _input = input ?? throw new ArgumentNullException(nameof(input));
    private byte[] _buffer = new byte[16 * 1024];
    private int _length;

    /// <summary>
    /// Reads the next complete frame, buffering across reads as needed.
    /// </summary>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The frame, or null at a clean end of stream.</returns>
    /// <exception cref="EndOfStreamException">The stream ended mid-frame.</exception>
    /// <exception cref="InvalidDataException">The header block was malformed.</exception>
    public async ValueTask<JsonRpcFrame?> ReadAsync(CancellationToken ct)
    {
        while (true)
        {
            if (TryParse(_buffer.AsSpan(0, _length), out JsonRpcFrame? frame, out int consumed))
            {
                _buffer.AsSpan(consumed, _length - consumed).CopyTo(_buffer);
                _length -= consumed;
                return frame;
            }

            if (_length == _buffer.Length) Array.Resize(ref _buffer, _buffer.Length * 2);
            int n = await _input.ReadAsync(_buffer.AsMemory(_length), ct).ConfigureAwait(false);
            if (n == 0)
            {
                if (_length == 0) return null;
                throw new EndOfStreamException("The stream ended in the middle of a frame.");
            }

            _length += n;
        }
    }

    /// <summary>
    /// Tries to decode one frame from the front of <paramref name="buffer"/>.
    /// </summary>
    /// <param name="buffer">Bytes received so far.</param>
    /// <param name="frame">Receives the frame when one is complete.</param>
    /// <param name="consumed">How many bytes the frame occupied.</param>
    /// <returns>False when more bytes are needed.</returns>
    /// <exception cref="InvalidDataException">The header block was malformed.</exception>
    public static bool TryParse(ReadOnlySpan<byte> buffer, out JsonRpcFrame? frame, out int consumed)
    {
        frame = null;
        consumed = 0;

        int headerEnd = IndexOfHeaderEnd(buffer);
        if (headerEnd < 0)
        {
            if (buffer.Length > MaxHeaderBytes) throw new InvalidDataException("Frame header block is too large.");
            return false;
        }

        int bodyStart = headerEnd + 4;
        long contentLength = -1;
        bool binary = false;
        long requestId = 0;
        bool eof = false;

        ReadOnlySpan<byte> headers = buffer[..headerEnd];
        while (!headers.IsEmpty)
        {
            int nl = headers.IndexOf((byte)13);
            ReadOnlySpan<byte> line = nl < 0 ? headers : headers[..nl];
            headers = nl < 0 ? default : headers[Math.Min(nl + 2, headers.Length)..];
            if (line.IsEmpty) continue;

            int colon = line.IndexOf((byte)58);
            if (colon < 0) throw new InvalidDataException("Frame header line has no name/value separator.");

            string name = Encoding.ASCII.GetString(line[..colon]).Trim();
            string value = Encoding.ASCII.GetString(line[(colon + 1)..]).Trim();

            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out contentLength) ||
                    contentLength < 0)
                {
                    throw new InvalidDataException($"Invalid Content-Length '{value}'.");
                }
            }
            else if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                binary = value.StartsWith(BinaryContentType, StringComparison.OrdinalIgnoreCase);
            }
            else if (name.Equals("X-Wavee-Request", StringComparison.OrdinalIgnoreCase))
            {
                _ = long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out requestId);
            }
            else if (name.Equals("X-Wavee-Eof", StringComparison.OrdinalIgnoreCase))
            {
                eof = value is "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
            }
        }

        if (contentLength < 0) throw new InvalidDataException("Frame header block has no Content-Length.");
        if (buffer.Length - bodyStart < contentLength) return false;

        var payload = new byte[contentLength];
        buffer.Slice(bodyStart, (int)contentLength).CopyTo(payload);
        frame = new JsonRpcFrame { IsBinary = binary, Payload = payload, RequestId = requestId, Eof = eof };
        consumed = bodyStart + (int)contentLength;
        return true;
    }

    /// <summary>Writes a UTF-8 JSON frame. Callers serialize writes themselves.</summary>
    /// <param name="output">The destination stream.</param>
    /// <param name="json">The UTF-8 JSON body.</param>
    public static void WriteJson(Stream output, ReadOnlySpan<byte> json)
    {
        ArgumentNullException.ThrowIfNull(output);
        WriteAscii(output, $"Content-Length: {json.Length}\r\n\r\n");
        output.Write(json);
        output.Flush();
    }

    /// <summary>Writes a binary frame answering one request id. Callers serialize writes themselves.</summary>
    /// <param name="output">The destination stream.</param>
    /// <param name="requestId">The request the bytes answer (<c>X-Wavee-Request</c>).</param>
    /// <param name="eof">True when this payload reaches the end of the stream (<c>X-Wavee-Eof</c>).</param>
    /// <param name="payload">The raw bytes.</param>
    public static void WriteBinary(Stream output, long requestId, bool eof, ReadOnlySpan<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(output);
        WriteAscii(output,
            $"Content-Length: {payload.Length}\r\nContent-Type: {BinaryContentType}\r\nX-Wavee-Request: {requestId}\r\nX-Wavee-Eof: {(eof ? 1 : 0)}\r\n\r\n");
        output.Write(payload);
        output.Flush();
    }

    private static void WriteAscii(Stream output, string text)
    {
        Span<byte> bytes = stackalloc byte[256];
        int n = Encoding.ASCII.GetBytes(text, bytes);
        output.Write(bytes[..n]);
    }

    private static int IndexOfHeaderEnd(ReadOnlySpan<byte> buffer)
    {
        for (int i = 0; i + 3 < buffer.Length; i++)
        {
            if (buffer[i] == 13 && buffer[i + 1] == 10 && buffer[i + 2] == 13 && buffer[i + 3] == 10) return i;
        }

        return -1;
    }
}
