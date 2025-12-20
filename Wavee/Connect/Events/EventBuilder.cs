using System.Text;

namespace Wavee.Connect.Events;

/// <summary>
/// Event types for Spotify's event-service.
/// </summary>
public enum EventType
{
    /// <summary>New playback session started (context changed).</summary>
    NewSessionId,

    /// <summary>New track playback started.</summary>
    NewPlaybackId,

    /// <summary>Track ended or transitioned - CRITICAL for artist payouts.</summary>
    TrackTransition
}

/// <summary>
/// Builds event messages for Spotify's event-service.
/// Fields are separated by 0x09 (tab character).
/// </summary>
/// <remarks>
/// Based on librespot-java's EventService.EventBuilder.
/// Events are sent to hm://event-service/v1/events via Mercury POST.
/// </remarks>
public sealed class EventBuilder
{
    private const byte Delimiter = 0x09; // Tab character

    // Event type identifiers (from librespot-java)
    private static readonly (string Id, string Unknown) NewSessionIdType = ("557", "3");
    private static readonly (string Id, string Unknown) NewPlaybackIdType = ("558", "1");
    private static readonly (string Id, string Unknown) TrackTransitionType = ("12", "38");

    private readonly MemoryStream _body;

    /// <summary>
    /// Creates a new EventBuilder for the specified event type.
    /// </summary>
    public EventBuilder(EventType type)
    {
        _body = new MemoryStream(256);

        var (id, unknown) = type switch
        {
            EventType.NewSessionId => NewSessionIdType,
            EventType.NewPlaybackId => NewPlaybackIdType,
            EventType.TrackTransition => TrackTransitionType,
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };

        // Write type ID without delimiter (first field)
        AppendNoDelimiter(id);
        // Write unknown field with delimiter
        Append(unknown);
    }

    /// <summary>
    /// Appends a string value with delimiter prefix.
    /// </summary>
    public EventBuilder Append(string? value)
    {
        _body.WriteByte(Delimiter);
        AppendNoDelimiter(value);
        return this;
    }

    /// <summary>
    /// Appends a single character with delimiter prefix.
    /// </summary>
    public EventBuilder Append(char c)
    {
        _body.WriteByte(Delimiter);
        _body.WriteByte((byte)c);
        return this;
    }

    /// <summary>
    /// Appends an integer value with delimiter prefix.
    /// </summary>
    public EventBuilder Append(int value)
    {
        return Append(value.ToString());
    }

    /// <summary>
    /// Appends a long value with delimiter prefix.
    /// </summary>
    public EventBuilder Append(long value)
    {
        return Append(value.ToString());
    }

    /// <summary>
    /// Appends a boolean as '0' or '1' with delimiter prefix.
    /// </summary>
    public EventBuilder Append(bool value)
    {
        return Append(value ? '1' : '0');
    }

    /// <summary>
    /// Converts the event to a byte array for transmission.
    /// </summary>
    public byte[] ToArray()
    {
        return _body.ToArray();
    }

    /// <summary>
    /// Returns a debug-friendly string representation.
    /// </summary>
    public override string ToString()
    {
        return $"EventBuilder{{{ToDebugString(ToArray())}}}";
    }

    /// <summary>
    /// Converts event bytes to a readable debug string (tabs shown as |).
    /// </summary>
    public static string ToDebugString(byte[] body)
    {
        var sb = new StringBuilder(body.Length);
        foreach (var b in body)
        {
            if (b == Delimiter)
                sb.Append('|');
            else
                sb.Append((char)b);
        }
        return sb.ToString();
    }

    private void AppendNoDelimiter(string? str)
    {
        if (string.IsNullOrEmpty(str))
            return;

        var bytes = Encoding.UTF8.GetBytes(str);
        _body.Write(bytes);
    }
}
