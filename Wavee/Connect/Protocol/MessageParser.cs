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
        // Handle empty/null input
        if (utf8Json.IsEmpty)
            return Protocol.MessageType.Unknown;

        try
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
        catch
        {
            // Malformed JSON or parsing errors
            return Protocol.MessageType.Unknown;
        }
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
            // Configure reader to handle large payloads
            // NOTE: Utf8JsonReader in .NET has no MaxTokenSize option - it uses internal limits
            var options = new JsonReaderOptions
            {
                MaxDepth = 64,
                CommentHandling = JsonCommentHandling.Skip
            };
            var reader = new Utf8JsonReader(utf8Json, options);

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

            // Payload is optional - some messages (like connection ID) have no payload
            if (uri == null || headers == null)
                return false;

            message = new DealerMessage
            {
                Uri = uri,
                Headers = headers,
                Payload = payload ?? Array.Empty<byte>()
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
            // Configure reader to handle large payloads
            var options = new JsonReaderOptions
            {
                MaxDepth = 64,
                CommentHandling = JsonCommentHandling.Skip
            };
            var reader = new Utf8JsonReader(utf8Json, options);

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
            if (parts.Length != 2 || !int.TryParse(parts[0], out var messageId) || string.IsNullOrWhiteSpace(parts[1]))
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
    /// Uses JsonDocument to handle multi-segment buffers and provides GetBytesFromBase64()
    /// for efficient direct decoding without intermediate string allocation.
    /// </summary>
    private static byte[]? ParsePayloads(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            return null;

        try
        {
            // Use JsonDocument.ParseValue to handle large tokens and multi-segment sequences
            using var doc = JsonDocument.ParseValue(ref reader);
            var array = doc.RootElement;

            if (array.ValueKind != JsonValueKind.Array)
                return null;

            // Get first element (dealer protocol uses single payload)
            using var enumerator = array.EnumerateArray();
            if (enumerator.MoveNext())
            {
                var element = enumerator.Current;
                if (element.ValueKind == JsonValueKind.String)
                {
                    // GetBytesFromBase64() decodes directly from UTF-8 base64 to bytes
                    // More efficient than GetString() + Convert.FromBase64String()
                    return element.GetBytesFromBase64();
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }
}
