using System.Buffers;
using System.Text;
using System.Text.Json;
using Wavee.Connect.Protocol;

namespace Wavee.Connect;

/// <summary>
/// Parses raw WebSocket messages into dealer protocol messages.
/// </summary>
internal static class MessageParser
{
    /// <summary>
    /// Parses raw JSON bytes into a message type and optional data.
    /// </summary>
    /// <param name="data">The raw JSON bytes from WebSocket.</param>
    /// <returns>Message type and optional parsed message data.</returns>
    public static (MessageType Type, DealerMessage? Message, DealerRequest? Request) Parse(
        ReadOnlyMemory<byte> data)
    {
        // Parse raw message using source-generated JSON
        var raw = JsonSerializer.Deserialize(
            data.Span,
            DealerJsonSerializerContext.Default.RawDealerMessage);
        if (raw == null)
        {
            throw new DealerException(
                DealerFailureReason.MessageError,
                "Failed to parse message: null result");
        }

        // Determine message type
        var messageType = raw.Type.ToLowerInvariant() switch
        {
            "ping" => MessageType.Ping,
            "pong" => MessageType.Pong,
            "message" => MessageType.Message,
            "request" => MessageType.Request,
            _ => MessageType.Unknown
        };

        // Parse MESSAGE type
        if (messageType == MessageType.Message)
        {
            if (string.IsNullOrEmpty(raw.Uri))
            {
                throw new DealerException(
                    DealerFailureReason.MessageError,
                    "MESSAGE missing required 'uri' field");
            }

            var payload = DecodePayload(raw.Payloads);
            var message = new DealerMessage
            {
                Uri = raw.Uri,
                Headers = raw.Headers ?? new Dictionary<string, string>(),
                Payload = payload
            };

            return (MessageType.Message, message, null);
        }

        // Parse REQUEST type
        if (messageType == MessageType.Request)
        {
            if (string.IsNullOrEmpty(raw.Key))
            {
                throw new DealerException(
                    DealerFailureReason.MessageError,
                    "REQUEST missing required 'key' field");
            }

            if (string.IsNullOrEmpty(raw.MessageIdent))
            {
                throw new DealerException(
                    DealerFailureReason.MessageError,
                    "REQUEST missing required 'message_ident' field");
            }

            if (!raw.Payload.HasValue)
            {
                throw new DealerException(
                    DealerFailureReason.MessageError,
                    "REQUEST missing required 'payload' field");
            }

            // Parse payload to extract command details
            var payloadObj = raw.Payload.Value;
            if (payloadObj.ValueKind != JsonValueKind.Object)
            {
                throw new DealerException(
                    DealerFailureReason.MessageError,
                    "REQUEST payload must be an object");
            }

            // Extract message_id and sender_device_id
            if (!payloadObj.TryGetProperty("message_id", out var messageIdProp) ||
                !messageIdProp.TryGetInt32(out var messageId))
            {
                throw new DealerException(
                    DealerFailureReason.MessageError,
                    "REQUEST payload missing 'message_id'");
            }

            if (!payloadObj.TryGetProperty("sent_by_device_id", out var deviceIdProp))
            {
                throw new DealerException(
                    DealerFailureReason.MessageError,
                    "REQUEST payload missing 'sent_by_device_id'");
            }

            var senderDeviceId = deviceIdProp.GetString() ?? string.Empty;

            // Extract command object
            if (!payloadObj.TryGetProperty("command", out var commandProp))
            {
                throw new DealerException(
                    DealerFailureReason.MessageError,
                    "REQUEST payload missing 'command'");
            }

            var request = new DealerRequest
            {
                Key = raw.Key,
                MessageIdent = raw.MessageIdent,
                MessageId = messageId,
                SenderDeviceId = senderDeviceId,
                Command = commandProp
            };

            return (MessageType.Request, null, request);
        }

        // PING/PONG or unknown
        return (messageType, null, null);
    }

    /// <summary>
    /// Decodes base64-encoded payloads array into a single byte array.
    /// </summary>
    private static byte[] DecodePayload(string[]? payloads)
    {
        if (payloads == null || payloads.Length == 0)
        {
            return Array.Empty<byte>();
        }

        // Single payload - decode directly
        if (payloads.Length == 1)
        {
            return Convert.FromBase64String(payloads[0]);
        }

        // Multiple payloads - concatenate
        var chunks = new byte[payloads.Length][];
        var totalLength = 0;

        for (var i = 0; i < payloads.Length; i++)
        {
            chunks[i] = Convert.FromBase64String(payloads[i]);
            totalLength += chunks[i].Length;
        }

        var result = new byte[totalLength];
        var offset = 0;

        foreach (var chunk in chunks)
        {
            Buffer.BlockCopy(chunk, 0, result, offset, chunk.Length);
            offset += chunk.Length;
        }

        return result;
    }
}
