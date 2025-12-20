namespace Wavee.Core.Audio;

/// <summary>
/// Represents a Spotify audio file ID (20 bytes, typically SHA-1 hash).
/// </summary>
/// <remarks>
/// File IDs are used to identify audio files on Spotify's CDN.
/// They are returned in track metadata and used for:
/// - AudioKey requests
/// - Storage resolve (CDN URL) requests
/// - Head file requests
/// - Cache identification
/// </remarks>
public readonly struct FileId : IEquatable<FileId>
{
    /// <summary>
    /// Length of file ID in bytes.
    /// </summary>
    public const int Length = 20;

    /// <summary>
    /// The raw 20-byte file ID.
    /// </summary>
    private readonly byte[] _bytes;

    /// <summary>
    /// Gets the raw bytes. Returns a copy to prevent mutation.
    /// </summary>
    public byte[] Raw => _bytes?.ToArray() ?? Array.Empty<byte>();

    /// <summary>
    /// Gets whether this FileId is valid (non-empty).
    /// </summary>
    public bool IsValid => _bytes is { Length: Length };

    /// <summary>
    /// Creates a FileId from raw bytes.
    /// </summary>
    /// <param name="bytes">20-byte file ID.</param>
    /// <exception cref="ArgumentException">Thrown if bytes is not exactly 20 bytes.</exception>
    public FileId(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Length)
            throw new ArgumentException($"FileId must be exactly {Length} bytes, got {bytes.Length}", nameof(bytes));

        _bytes = bytes.ToArray();
    }

    /// <summary>
    /// Creates a FileId from a byte array.
    /// </summary>
    /// <param name="bytes">20-byte file ID.</param>
    /// <exception cref="ArgumentException">Thrown if bytes is not exactly 20 bytes.</exception>
    public FileId(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length != Length)
            throw new ArgumentException($"FileId must be exactly {Length} bytes, got {bytes.Length}", nameof(bytes));

        _bytes = (byte[])bytes.Clone();
    }

    /// <summary>
    /// Private constructor for internal use (no copy).
    /// </summary>
    private FileId(byte[] bytes, bool noCopy)
    {
        _bytes = bytes;
    }

    /// <summary>
    /// Gets the base16 (hex) representation (lowercase).
    /// </summary>
    public string ToBase16()
    {
        if (_bytes is null)
            return string.Empty;

        return Convert.ToHexString(_bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Writes the raw bytes to a span.
    /// </summary>
    public void WriteRaw(Span<byte> destination)
    {
        if (destination.Length < Length)
            throw new ArgumentException($"Destination must be at least {Length} bytes", nameof(destination));

        if (_bytes is not null)
            _bytes.CopyTo(destination);
    }

    /// <summary>
    /// Creates a FileId from raw bytes.
    /// </summary>
    public static FileId FromBytes(ReadOnlySpan<byte> bytes) => new(bytes);

    /// <summary>
    /// Creates a FileId from raw bytes (no copy for internal use).
    /// </summary>
    internal static FileId FromBytesNoCopy(byte[] bytes) => new(bytes, noCopy: true);

    /// <summary>
    /// Creates a FileId from a byte array.
    /// </summary>
    public static FileId FromBytes(byte[] bytes) => new(bytes);

    /// <summary>
    /// Parses a base16 (hex) encoded file ID.
    /// </summary>
    /// <param name="hex">40-character hex string.</param>
    /// <returns>Parsed FileId.</returns>
    /// <exception cref="ArgumentException">Thrown if the hex string is invalid.</exception>
    public static FileId FromBase16(string hex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hex);

        if (hex.Length != Length * 2)
            throw new ArgumentException($"Hex file ID must be exactly {Length * 2} characters, got {hex.Length}", nameof(hex));

        var bytes = Convert.FromHexString(hex);
        return new FileId(bytes, noCopy: true);
    }

    /// <summary>
    /// Tries to parse a base16 (hex) encoded file ID.
    /// </summary>
    public static bool TryFromBase16(string hex, out FileId result)
    {
        try
        {
            result = FromBase16(hex);
            return true;
        }
        catch
        {
            result = default;
            return false;
        }
    }

    /// <summary>
    /// Empty/default file ID.
    /// </summary>
    public static FileId Empty => default;

    public bool Equals(FileId other)
    {
        if (_bytes is null && other._bytes is null)
            return true;
        if (_bytes is null || other._bytes is null)
            return false;

        return _bytes.AsSpan().SequenceEqual(other._bytes);
    }

    public override bool Equals(object? obj) => obj is FileId other && Equals(other);

    public override int GetHashCode()
    {
        if (_bytes is null)
            return 0;

        // Use first 8 bytes for hash (sufficient for uniqueness)
        var hashCode = new HashCode();
        for (int i = 0; i < Math.Min(8, _bytes.Length); i++)
            hashCode.Add(_bytes[i]);
        return hashCode.ToHashCode();
    }

    public override string ToString() => ToBase16();

    public static bool operator ==(FileId left, FileId right) => left.Equals(right);
    public static bool operator !=(FileId left, FileId right) => !left.Equals(right);
}
