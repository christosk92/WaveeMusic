using System.Globalization;
using Wavee.Sdk;

namespace Wavee.Module.Radio;

/// <summary>
/// What one Icecast/SHOUTcast response's headers said about the station. Parsed from the headers alone: the probe
/// aborts before any audio is read.
/// </summary>
/// <param name="Name">The <c>icy-name</c> station name.</param>
/// <param name="Genre">The <c>icy-genre</c> genre.</param>
/// <param name="Description">The <c>icy-description</c> blurb.</param>
/// <param name="Url">The <c>icy-url</c> homepage.</param>
/// <param name="BitrateKbps">The <c>icy-br</c> nominal bitrate, or null when the server sent none.</param>
/// <param name="MetaInt">The <c>icy-metaint</c> metadata interval; its presence means interleaved ICY titles.</param>
/// <param name="ContentType">The response's <c>Content-Type</c>.</param>
/// <param name="ContentLength">The response's <c>Content-Length</c>; a finite body means this is not a live stream.</param>
public sealed record IcyInfo(
    string? Name,
    string? Genre,
    string? Description,
    string? Url,
    int? BitrateKbps,
    int? MetaInt,
    string? ContentType,
    long? ContentLength)
{
    /// <summary>The header a client sends to ask for interleaved ICY metadata.</summary>
    public const string RequestHeader = "Icy-MetaData";

    /// <summary>Nothing known — what a probe that could not complete reports.</summary>
    public static IcyInfo Unknown { get; } = new(null, null, null, null, null, null, null, null);

    /// <summary>Builds the info from a response's headers.</summary>
    /// <param name="headers">Every response header, content headers included.</param>
    /// <param name="contentType">The <c>Content-Type</c> value.</param>
    /// <param name="contentLength">The <c>Content-Length</c> value.</param>
    public static IcyInfo FromHeaders(IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers,
        string? contentType, long? contentLength)
    {
        string? name = null, genre = null, description = null, url = null;
        int? bitrate = null, metaInt = null;

        foreach (KeyValuePair<string, IEnumerable<string>> header in headers)
        {
            string? value = First(header.Value);
            if (value is null) continue;

            switch (header.Key.ToLowerInvariant())
            {
                case "icy-name": name = value; break;
                case "icy-genre": genre = value; break;
                case "icy-description": description = value; break;
                case "icy-url": url = value; break;
                case "icy-br":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int br)) bitrate = br;
                    break;
                case "icy-metaint":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mi)) metaInt = mi;
                    break;
                default: break;
            }
        }

        return new IcyInfo(Blank(name), Blank(genre), Blank(description), Blank(url), bitrate, metaInt,
            Blank(contentType), contentLength);
    }

    /// <summary>
    /// Which <see cref="MediaLocator"/> container the app should use: an HLS content type wins, then an
    /// <c>icy-metaint</c> (interleaved titles), then a finite <c>Content-Length</c> (a plain downloadable file),
    /// and anything else is treated as an endless Icecast body.
    /// </summary>
    public string Container
    {
        get
        {
            if (IsHlsContentType(ContentType)) return MediaLocator.ContainerHls;
            if (MetaInt is > 0) return MediaLocator.ContainerIcy;
            return ContentLength is > 0 ? MediaLocator.ContainerProgressive : MediaLocator.ContainerIcy;
        }
    }

    /// <summary>True when the content type names an HLS manifest.</summary>
    /// <param name="contentType">The value to test.</param>
    public static bool IsHlsContentType(string? contentType)
    {
        if (contentType is null) return false;
        string ct = contentType.ToLowerInvariant();
        return ct.Contains("mpegurl", StringComparison.Ordinal);
    }

    private static string? First(IEnumerable<string> values)
    {
        foreach (string v in values) return v;
        return null;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
