using System.Buffers;
using System.Buffers.Text;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

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
    public static bool TryParseMessage(ReadOnlySpan<byte> utf8Json, out DealerMessage? message, ILogger? logger = null)
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
            {
                logger?.LogWarning("MESSAGE missing required fields - uri:{HasUri} headers:{HasHeaders}",
                    uri != null, headers != null);

                if (logger?.IsEnabled(LogLevel.Trace) == true)
                {
                    logger.LogTrace("Incomplete MESSAGE JSON: {Json}", Encoding.UTF8.GetString(utf8Json));
                }

                return false;
            }

            message = new DealerMessage
            {
                Uri = uri,
                Headers = headers,
                Payload = payload ?? Array.Empty<byte>()
            };

            return true;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "MESSAGE parse exception: {Message}", ex.Message);

            if (logger?.IsEnabled(LogLevel.Trace) == true)
            {
                logger.LogTrace("Raw MESSAGE JSON that failed parsing: {Json}", Encoding.UTF8.GetString(utf8Json));
            }

            return false;
        }
    }

    /// <summary>
    /// Tries to parse a REQUEST type dealer message.
    /// </summary>
    /// <param name="utf8Json">Raw UTF-8 JSON bytes.</param>
    /// <param name="request">Parsed request (if successful).</param>
    /// <returns>True if parsing succeeded.</returns>
    public static bool TryParseRequest(ReadOnlySpan<byte> utf8Json, out DealerRequest? request, ILogger? logger = null)
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
            Dictionary<string, string>? headers = null;
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
                    else if (reader.ValueTextEquals(HeadersPropertyName))
                    {
                        reader.Read();
                        headers = ParseHeaders(ref reader);
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

            // Handle gzip-compressed payloads (librespot compatibility)
            // When Transfer-Encoding header is "gzip", payload contains { "compressed": "<base64>" }
            if (hasPayload &&
                headers != null &&
                headers.TryGetValue("Transfer-Encoding", out var encoding) &&
                encoding.Equals("gzip", StringComparison.OrdinalIgnoreCase))
            {
                if (payload.TryGetProperty("compressed", out var compressedProp))
                {
                    try
                    {
                        var base64 = compressedProp.GetString();
                        if (!string.IsNullOrEmpty(base64))
                        {
                            var gzipBytes = Convert.FromBase64String(base64);
                            using var ms = new MemoryStream(gzipBytes);
                            using var gzip = new GZipStream(ms, CompressionMode.Decompress);
                            using var decompressedDoc = JsonDocument.Parse(gzip);
                            payload = decompressedDoc.RootElement.Clone();
                            logger?.LogTrace("Decompressed gzip payload successfully");
                        }
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning(ex, "Failed to decompress gzip payload");
                        return false;
                    }
                }
            }

            if (key == null || messageIdent == null || !hasPayload)
            {
                logger?.LogWarning("REQUEST missing required fields - key:{HasKey} messageIdent:{HasMessageIdent} payload:{HasPayload}",
                    key != null, messageIdent != null, hasPayload);

                if (logger?.IsEnabled(LogLevel.Trace) == true)
                {
                    logger.LogTrace("Incomplete REQUEST JSON: {Json}", Encoding.UTF8.GetString(utf8Json));
                }

                return false;
            }

            // Parse key format: Try to extract "message_id/device_id" if format matches,
            // otherwise treat key as opaque string (like librespot reference implementations).
            // Only the Key field is used for replies - MessageId and SenderDeviceId are optional metadata.
            int messageId = 0;
            string senderDeviceId = string.Empty;

            var parts = key.Split('/');
            if (parts.Length == 2 && int.TryParse(parts[0], out var parsedId) && !string.IsNullOrWhiteSpace(parts[1]))
            {
                // Standard format - extract both
                messageId = parsedId;
                senderDeviceId = parts[1];
            }
            else
            {
                // Non-standard format - treat as opaque string (reference implementation behavior)
                logger?.LogDebug("REQUEST key doesn't match standard 'id/device' format, treating as opaque: {Key}", key);
            }

            request = new DealerRequest
            {
                Key = key,
                MessageIdent = messageIdent,
                MessageId = messageId,
                SenderDeviceId = senderDeviceId,
                Command = payload,
                Headers = headers
            };

            return true;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "REQUEST parse exception: {Message}", ex.Message);

            if (logger?.IsEnabled(LogLevel.Trace) == true)
            {
                logger.LogTrace("Raw REQUEST JSON that failed parsing: {Json}", Encoding.UTF8.GetString(utf8Json));
            }

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
    /// Parses multiple concatenated JSON messages from a single buffer.
    /// WebSocket can sometimes deliver multiple JSON objects without delimiters.
    /// </summary>
    /// <param name="utf8Json">Raw UTF-8 JSON bytes (may contain multiple objects).</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <returns>List of parsed messages.</returns>
    public static List<DealerMessage> ParseMessages(ReadOnlySpan<byte> utf8Json, ILogger? logger = null)
    {
        var messages = new List<DealerMessage>();

        if (utf8Json.IsEmpty)
            return messages;

        var remaining = utf8Json;
        while (remaining.Length > 0)
        {
            // Find the boundary between JSON objects
            var endIndex = FindJsonObjectEnd(remaining);
            if (endIndex <= 0)
            {
                // Try to parse whatever is left
                if (TryParseMessage(remaining, out var lastMessage, logger) && lastMessage != null)
                    messages.Add(lastMessage);
                break;
            }

            var jsonObject = remaining.Slice(0, endIndex);
            if (TryParseMessage(jsonObject, out var message, logger) && message != null)
            {
                messages.Add(message);
            }

            remaining = remaining.Slice(endIndex);

            // Skip any whitespace between objects
            while (remaining.Length > 0 && (remaining[0] == ' ' || remaining[0] == '\n' || remaining[0] == '\r' || remaining[0] == '\t'))
            {
                remaining = remaining.Slice(1);
            }
        }

        return messages;
    }

    /// <summary>
    /// Finds the end of a JSON object by counting braces.
    /// Returns the index after the closing brace, or 0 if not found.
    /// </summary>
    private static int FindJsonObjectEnd(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length == 0 || utf8Json[0] != '{')
            return 0;

        int depth = 0;
        bool inString = false;
        bool escape = false;

        for (int i = 0; i < utf8Json.Length; i++)
        {
            byte b = utf8Json[i];

            if (escape)
            {
                escape = false;
                continue;
            }

            if (b == '\\' && inString)
            {
                escape = true;
                continue;
            }

            if (b == '"')
            {
                inString = !inString;
                continue;
            }

            if (inString)
                continue;

            if (b == '{')
                depth++;
            else if (b == '}')
            {
                depth--;
                if (depth == 0)
                    return i + 1;
            }
        }

        return 0; // Unbalanced braces
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
