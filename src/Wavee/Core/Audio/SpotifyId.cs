using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Wavee.Core.Audio;

/// <summary>
/// Represents a Spotify ID (track, album, artist, episode, etc.).
/// </summary>
/// <remarks>
/// Spotify IDs are 128-bit (16-byte) identifiers that can be represented as:
/// - Base62 string (22 characters, e.g., "4iV5W9uYEdYUVa79Axb7Rh")
/// - Base16 hex string (32 characters)
/// - Raw bytes (16 bytes)
/// - URI format (e.g., "spotify:track:4iV5W9uYEdYUVa79Axb7Rh")
/// </remarks>
public readonly struct SpotifyId : IEquatable<SpotifyId>
{
    /// <summary>
    /// Base62 alphabet used by Spotify.
    /// </summary>
    private const string Base62Alphabet = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";

    /// <summary>
    /// Length of base62 encoded ID.
    /// </summary>
    public const int Base62Length = 22;

    /// <summary>
    /// Length of raw ID in bytes.
    /// </summary>
    public const int RawLength = 16;

    /// <summary>
    /// The raw 128-bit ID value.
    /// </summary>
    private readonly UInt128 _id;

    /// <summary>
    /// The type of Spotify entity this ID represents.
    /// </summary>
    public SpotifyIdType Type { get; }

    /// <summary>
    /// Creates a SpotifyId from a raw 128-bit value.
    /// </summary>
    public SpotifyId(UInt128 id, SpotifyIdType type)
    {
        _id = id;
        Type = type;
    }

    /// <summary>
    /// Creates a SpotifyId from raw bytes.
    /// </summary>
    /// <param name="bytes">16-byte raw ID.</param>
    /// <param name="type">Type of Spotify entity.</param>
    /// <exception cref="ArgumentException">Thrown if bytes is not exactly 16 bytes.</exception>
    public SpotifyId(ReadOnlySpan<byte> bytes, SpotifyIdType type)
    {
        if (bytes.Length != RawLength)
            throw new ArgumentException($"SpotifyId must be exactly {RawLength} bytes", nameof(bytes));

        // Read as big-endian UInt128
        _id = new UInt128(
            BinaryPrimitives.ReadUInt64BigEndian(bytes),
            BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]));
        Type = type;
    }

    /// <summary>
    /// Gets the raw 16-byte representation.
    /// </summary>
    public byte[] ToRaw()
    {
        var bytes = new byte[RawLength];
        WriteRaw(bytes);
        return bytes;
    }

    /// <summary>
    /// Writes the raw 16-byte representation to a span.
    /// </summary>
    public void WriteRaw(Span<byte> destination)
    {
        if (destination.Length < RawLength)
            throw new ArgumentException($"Destination must be at least {RawLength} bytes", nameof(destination));

        BinaryPrimitives.WriteUInt64BigEndian(destination, (ulong)(_id >> 64));
        BinaryPrimitives.WriteUInt64BigEndian(destination[8..], (ulong)_id);
    }

    /// <summary>
    /// Gets the base16 (hex) representation.
    /// </summary>
    public string ToBase16()
    {
        Span<byte> bytes = stackalloc byte[RawLength];
        WriteRaw(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Gets the base62 representation (22 characters).
    /// </summary>
    public string ToBase62()
    {
        Span<char> result = stackalloc char[Base62Length];
        var value = _id;

        for (int i = Base62Length - 1; i >= 0; i--)
        {
            var (quotient, remainder) = UInt128.DivRem(value, 62);
            result[i] = Base62Alphabet[(int)remainder];
            value = quotient;
        }

        return new string(result);
    }

    /// <summary>
    /// Gets the full URI representation (e.g., "spotify:track:xxx").
    /// </summary>
    public string ToUri()
    {
        var typeStr = Type switch
        {
            SpotifyIdType.Track => "track",
            SpotifyIdType.Album => "album",
            SpotifyIdType.Artist => "artist",
            SpotifyIdType.Playlist => "playlist",
            SpotifyIdType.Episode => "episode",
            SpotifyIdType.Show => "show",
            SpotifyIdType.User => "user",
            SpotifyIdType.Local => "local",
            _ => "unknown"
        };

        return $"spotify:{typeStr}:{ToBase62()}";
    }

    /// <summary>
    /// Parses a Spotify URI (e.g., "spotify:track:4iV5W9uYEdYUVa79Axb7Rh").
    /// </summary>
    /// <param name="uri">The URI to parse.</param>
    /// <returns>Parsed SpotifyId.</returns>
    /// <exception cref="ArgumentException">Thrown if the URI format is invalid.</exception>
    public static SpotifyId FromUri(string uri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);

        var parts = uri.Split(':');
        if (parts.Length < 3 || parts[0] != "spotify")
            throw new ArgumentException($"Invalid Spotify URI format: {uri}", nameof(uri));

        var type = parts[1].ToLowerInvariant() switch
        {
            "track" => SpotifyIdType.Track,
            "album" => SpotifyIdType.Album,
            "artist" => SpotifyIdType.Artist,
            "playlist" => SpotifyIdType.Playlist,
            "episode" => SpotifyIdType.Episode,
            "show" => SpotifyIdType.Show,
            "user" => SpotifyIdType.User,
            "local" => SpotifyIdType.Local,
            _ => SpotifyIdType.Unknown
        };

        return FromBase62(parts[2], type);
    }

    /// <summary>
    /// Parses a base62 encoded ID.
    /// </summary>
    /// <param name="base62">22-character base62 string.</param>
    /// <param name="type">Type of Spotify entity.</param>
    /// <returns>Parsed SpotifyId.</returns>
    /// <exception cref="ArgumentException">Thrown if the base62 string is invalid.</exception>
    public static SpotifyId FromBase62(string base62, SpotifyIdType type = SpotifyIdType.Track)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(base62);

        if (base62.Length != Base62Length)
            throw new ArgumentException($"Base62 ID must be exactly {Base62Length} characters", nameof(base62));

        UInt128 value = 0;

        foreach (var c in base62)
        {
            var digit = Base62Alphabet.IndexOf(c);
            if (digit < 0)
                throw new ArgumentException($"Invalid base62 character: '{c}'", nameof(base62));

            value = value * 62 + (uint)digit;
        }

        return new SpotifyId(value, type);
    }

    /// <summary>
    /// Parses a base16 (hex) encoded ID.
    /// </summary>
    /// <param name="hex">32-character hex string.</param>
    /// <param name="type">Type of Spotify entity.</param>
    /// <returns>Parsed SpotifyId.</returns>
    /// <exception cref="ArgumentException">Thrown if the hex string is invalid.</exception>
    public static SpotifyId FromBase16(string hex, SpotifyIdType type = SpotifyIdType.Track)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hex);

        if (hex.Length != RawLength * 2)
            throw new ArgumentException($"Hex ID must be exactly {RawLength * 2} characters", nameof(hex));

        var bytes = Convert.FromHexString(hex);
        return new SpotifyId(bytes, type);
    }

    /// <summary>
    /// Creates a SpotifyId from raw bytes.
    /// </summary>
    /// <param name="bytes">16-byte raw ID.</param>
    /// <param name="type">Type of Spotify entity.</param>
    /// <returns>Parsed SpotifyId.</returns>
    public static SpotifyId FromRaw(ReadOnlySpan<byte> bytes, SpotifyIdType type = SpotifyIdType.Track)
        => new(bytes, type);

    /// <summary>
    /// Tries to parse a Spotify URI.
    /// </summary>
    public static bool TryFromUri(string uri, out SpotifyId result)
    {
        try
        {
            result = FromUri(uri);
            return true;
        }
        catch
        {
            result = default;
            return false;
        }
    }

    /// <summary>
    /// Parses a Spotify URL (e.g., "https://open.spotify.com/track/4iV5W9uYEdYUVa79Axb7Rh").
    /// </summary>
    /// <param name="url">The URL to parse.</param>
    /// <returns>Parsed SpotifyId.</returns>
    /// <exception cref="ArgumentException">Thrown if the URL format is invalid.</exception>
    public static SpotifyId FromUrl(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        // Parse the URL - handles both with and without query strings
        // Format: https://open.spotify.com/{type}/{id}?...
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new ArgumentException($"Invalid URL format: {url}", nameof(url));

        if (uri.Host != "open.spotify.com")
            throw new ArgumentException($"Not a Spotify URL: {url}", nameof(url));

        // Path segments: ["", "{type}", "{id}"]
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
            throw new ArgumentException($"Invalid Spotify URL format: {url}", nameof(url));

        var typeStr = segments[0].ToLowerInvariant();
        var id = segments[1];

        var type = typeStr switch
        {
            "track" => SpotifyIdType.Track,
            "album" => SpotifyIdType.Album,
            "artist" => SpotifyIdType.Artist,
            "playlist" => SpotifyIdType.Playlist,
            "episode" => SpotifyIdType.Episode,
            "show" => SpotifyIdType.Show,
            "user" => SpotifyIdType.User,
            _ => SpotifyIdType.Unknown
        };

        return FromBase62(id, type);
    }

    /// <summary>
    /// Tries to parse a Spotify URL.
    /// </summary>
    public static bool TryFromUrl(string url, out SpotifyId result)
    {
        try
        {
            result = FromUrl(url);
            return true;
        }
        catch
        {
            result = default;
            return false;
        }
    }

    /// <summary>
    /// Parses a Spotify URI or URL to a SpotifyId.
    /// Accepts both "spotify:track:xxx" and "https://open.spotify.com/track/xxx" formats.
    /// </summary>
    /// <param name="uriOrUrl">The URI or URL to parse.</param>
    /// <returns>Parsed SpotifyId.</returns>
    /// <exception cref="ArgumentException">Thrown if the format is invalid.</exception>
    public static SpotifyId Parse(string uriOrUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uriOrUrl);

        // Check if it's a URL (starts with http)
        if (uriOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            uriOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return FromUrl(uriOrUrl);
        }

        // Otherwise, treat as URI
        return FromUri(uriOrUrl);
    }

    /// <summary>
    /// Tries to parse a Spotify URI or URL.
    /// </summary>
    public static bool TryParse(string uriOrUrl, out SpotifyId result)
    {
        try
        {
            result = Parse(uriOrUrl);
            return true;
        }
        catch
        {
            result = default;
            return false;
        }
    }

    public bool Equals(SpotifyId other) => _id == other._id && Type == other.Type;
    public override bool Equals(object? obj) => obj is SpotifyId other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(_id, Type);
    public override string ToString() => ToUri();

    public static bool operator ==(SpotifyId left, SpotifyId right) => left.Equals(right);
    public static bool operator !=(SpotifyId left, SpotifyId right) => !left.Equals(right);
}

/// <summary>
/// Types of Spotify entities.
/// </summary>
public enum SpotifyIdType
{
    Unknown,
    Track,
    Album,
    Artist,
    Playlist,
    Episode,
    Show,
    User,
    Local
}
