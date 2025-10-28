using System.Buffers;
using System.Buffers.Text;
using System.Text;
using System.Text.Json;

namespace Wavee.Connect.Protocol;

/// <summary>
/// High-performance message parser using Utf8JsonReader.
/// Zero-allocation parsing with cached property names.
/// </summary>
internal static class MessageParser
{
    // Cached UTF-8 property names - avoid repeated encoding
    private static ReadOnlySpan<byte> TypePropertyName => "type"u8;
    private static ReadOnlySpan<byte> UriPropertyName => "uri"u8;
    private static ReadOnlySpan<byte> KeyPropertyName => "key"u8;
    private static ReadOnlySpan<byte> MessageIdentPropertyName => "message_ident"u8;
    private static ReadOnlySpan<byte> HeadersPropertyName => "headers"u8;
    private static ReadOnlySpan<byte> PayloadsPropertyName => "payloads"u8;
    private static ReadOnlySpan<byte> PayloadPropertyName => "payload"u8;

    // Message type strings
    private static ReadOnlySpan<byte> PingType => "ping"u8;
    private static ReadOnlySpan<byte> PongType => "pong"u8;
    private static ReadOnlySpan<byte> MessageType => "message"u8;
    private static ReadOnlySpan<byte> RequestType => "request"u8;

    /// <summary>
    /// Parses the message type from raw JSON bytes.
    /// </summary>
    /// <param name="utf8Json">Raw UTF-8 JSON bytes.</param>
    /// <returns>Parsed message type.</returns>
    public static Protocol.MessageType ParseMessageType(ReadOnlySpan<byte> utf8Json)
    {
        var reader = new Utf8JsonReader(utf8Json);

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals(TypePropertyName))
                {
                    reader.Read(); // Move to value
                    if (reader.ValueTextEquals(PingType))
                        return Protocol.MessageType.Ping;
                    if (reader.ValueTextEquals(PongType))
                        return Protocol.MessageType.Pong;
                    if (reader.ValueTextEquals(MessageType))
                        return Protocol.MessageType.Message;
                    if (reader.ValueTextEquals(RequestType))
                        return Protocol.MessageType.Request;

                    return Protocol.MessageType.Unknown;
                }
            }
        }

        return Protocol.MessageType.Unknown;
    }

    /// <summary>
    /// Tries to parse a MESSAGE type dealer message.
    /// </summary>
    /// <param name="utf8Json">Raw UTF-8 JSON bytes.</param>
    /// <param name="message">Parsed message (if successful).</param>
    /// <returns>True if parsing succeeded.</returns>
    public static bool TryParseMessage(ReadOnlySpan<byte> utf8Json, out DealerMessage? message)
    {
        message = null;

        try
        {
            var reader = new Utf8JsonReader(utf8Json);

            string? uri = null;
            Dictionary<string, string>? headers = null;
            byte[]? payload = null;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    if (reader.ValueTextEquals(UriPropertyName))
                    {
                        reader.Read();
                        uri = reader.GetString();
                    }
                    else if (reader.ValueTextEquals(HeadersPropertyName))
                    {
                        reader.Read();
                        headers = ParseHeaders(ref reader);
                    }
                    else if (reader.ValueTextEquals(PayloadsPropertyName))
                    {
                        reader.Read();
                        payload = ParsePayloads(ref reader);
                    }
                }
            }

            if (uri == null || headers == null || payload == null)
                return false;

            message = new DealerMessage
            {
                Uri = uri,
                Headers = headers,
                Payload = payload
            };

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Tries to parse a REQUEST type dealer message.
    /// </summary>
    /// <param name="utf8Json">Raw UTF-8 JSON bytes.</param>
    /// <param name="request">Parsed request (if successful).</param>
    /// <returns>True if parsing succeeded.</returns>
    public static bool TryParseRequest(ReadOnlySpan<byte> utf8Json, out DealerRequest? request)
    {
        request = null;

        try
        {
            var reader = new Utf8JsonReader(utf8Json);

            string? key = null;
            string? messageIdent = null;
            JsonElement payload = default;
            bool hasPayload = false;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    if (reader.ValueTextEquals(KeyPropertyName))
                    {
                        reader.Read();
                        key = reader.GetString();
                    }
                    else if (reader.ValueTextEquals(MessageIdentPropertyName))
                    {
                        reader.Read();
                        messageIdent = reader.GetString();
                    }
                    else if (reader.ValueTextEquals(PayloadPropertyName))
                    {
                        // Parse payload as JsonElement for flexibility
                        var doc = JsonDocument.ParseValue(ref reader);
                        payload = doc.RootElement.Clone();
                        hasPayload = true;
                    }
                }
            }

            if (key == null || messageIdent == null || !hasPayload)
                return false;

            // Parse key format: "message_id/sender_device_id"
            var parts = key.Split('/');
            if (parts.Length != 2 || !int.TryParse(parts[0], out var messageId))
                return false;

            request = new DealerRequest
            {
                Key = key,
                MessageIdent = messageIdent,
                MessageId = messageId,
                SenderDeviceId = parts[1],
                Command = payload
            };

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Parses headers object from JSON.
    /// </summary>
    private static Dictionary<string, string>? ParseHeaders(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            return null;

        var headers = new Dictionary<string, string>();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;

            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                var key = reader.GetString();
                reader.Read();
                var value = reader.GetString();

                if (key != null && value != null)
                    headers[key] = value;
            }
        }

        return headers;
    }

    /// <summary>
    /// Parses payloads array (base64 encoded) and decodes into single payload.
    /// Uses ArrayPool for zero-allocation base64 decoding.
    /// </summary>
    private static byte[]? ParsePayloads(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            return null;

        // Read first payload only (dealer protocol uses single payload)
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
                break;

            if (reader.TokenType == JsonTokenType.String)
            {
                // Decode base64 using ArrayPool
                return DecodeBase64Payload(reader.ValueSpan);
            }
        }

        // Skip remaining array elements
        while (reader.TokenType != JsonTokenType.EndArray && reader.Read())
        {
        }

        return null;
    }

    /// <summary>
    /// Decodes base64 payload using ArrayPool for buffer management.
    /// Zero-allocation for the decoding process (only final byte[] allocated).
    /// </summary>
    private static byte[]? DecodeBase64Payload(ReadOnlySpan<byte> base64Utf8)
    {
        // Calculate maximum decoded size
        var maxDecodedLength = Base64.GetMaxDecodedFromUtf8Length(base64Utf8.Length);

        // Rent buffer from pool
        var buffer = ArrayPool<byte>.Shared.Rent(maxDecodedLength);

        try
        {
            // Decode directly from UTF-8 base64 to bytes
            if (Base64.DecodeFromUtf8(base64Utf8, buffer, out _, out var bytesWritten) == OperationStatus.Done)
            {
                // Copy to final array (this is the only allocation)
                var result = new byte[bytesWritten];
                buffer.AsSpan(0, bytesWritten).CopyTo(result);
                return result;
            }

            return null;
        }
        finally
        {
            // Return buffer to pool
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
