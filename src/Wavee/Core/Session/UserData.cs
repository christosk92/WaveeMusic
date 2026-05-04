namespace Wavee.Core.Session;

/// <summary>
/// Authenticated user data from Spotify Access Point.
/// </summary>
/// <remarks>
/// This data is returned from successful authentication and remains
/// valid for the lifetime of the session.
/// </remarks>
public sealed record UserData
{
    /// <summary>
    /// Canonical Spotify username.
    /// </summary>
    public required string Username { get; init; }

    /// <summary>
    /// ISO 3166-1 alpha-2 country code (e.g., "US", "GB", "SE").
    /// Will be null until the CountryCode packet (0x1b) is received.
    /// </summary>
    public string? CountryCode { get; init; }

    /// <summary>
    /// User's subscription tier.
    /// Will be null until the ProductInfo packet (0x50) is received.
    /// </summary>
    public AccountType? AccountType { get; init; }

    /// <summary>
    /// URL template for head files (e.g., "https://heads-fa-tls13.spotifycdn.com/head/{file_id}").
    /// Will be null until the ProductInfo packet (0x50) is received.
    /// </summary>
    public string? HeadFilesUrl { get; init; }

    /// <summary>
    /// URL template for images (e.g., "https://i.scdn.co/image/{file_id}").
    /// Will be null until the ProductInfo packet (0x50) is received.
    /// </summary>
    public string? ImageUrl { get; init; }

    /// <summary>
    /// Whether explicit content filtering is enabled.
    /// Will be false until the ProductInfo packet (0x50) is received.
    /// </summary>
    public bool FilterExplicitContent { get; init; }

    /// <summary>
    /// User's preferred locale (e.g., "en", "es", "fr").
    /// Will be null until the ProductInfo packet (0x50) is received.
    /// </summary>
    public string? PreferredLocale { get; init; }

    /// <summary>
    /// User's display name from Spotify Web API /me endpoint.
    /// May differ from Username (which is the canonical ID).
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// URL to user's profile image from Spotify Web API /me endpoint.
    /// </summary>
    public string? ProfileImageUrl { get; init; }

    /// <summary>
    /// URL template for video keyframes (e.g., "http://keyframes-fa.cdn.spotify.com/keyframes/v1/sources/{source_id}/keyframe/heights/{height}/timestamps/{timestamp_ms}.jpg").
    /// Will be null until the ProductInfo packet (0x50) is received.
    /// </summary>
    public string? VideoKeyframeUrl { get; init; }

    /// <summary>
    /// ReplayGain target levels for the three Spotify "normalisation" modes plus
    /// default, parsed from the <c>loudness-levels</c> attribute in the
    /// ProductInfo XML (format <c>"1:-5.0,0.0,3.0:-2.0"</c> — version, then
    /// quiet/normal/loud triplet, then default). Applied on top of the
    /// per-track gain in <c>NormalizationData</c> so tracks sit at the target
    /// loudness librespot / Spotify desktop aim for. Null until ProductInfo
    /// arrives.
    /// </summary>
    public LoudnessLevels? LoudnessLevels { get; init; }

    /// <summary>
    /// True when the server flagged this client as deprecated via ProductInfo's
    /// <c>client-deprecated</c> element. Diagnostic only — Spotify still serves
    /// traffic to deprecated clients for some time, but the flag is worth
    /// surfacing so we notice when it starts biting.
    /// </summary>
    public bool IsClientDeprecated { get; init; }
}

/// <summary>
/// ReplayGain target offsets for Spotify's three loudness modes, parsed from
/// the ProductInfo <c>loudness-levels</c> XML element.
/// </summary>
/// <param name="Quiet">Offset (dB) to apply when the user picks "quiet" normalisation.</param>
/// <param name="Normal">Offset (dB) for "normal" (the default UX choice).</param>
/// <param name="Loud">Offset (dB) for "loud".</param>
/// <param name="Default">Fallback offset (dB) when no explicit mode is selected.</param>
public sealed record LoudnessLevels(double Quiet, double Normal, double Loud, double Default)
{
    /// <summary>
    /// Parses the server's format: <c>{version}:{quiet},{normal},{loud}:{default}</c>.
    /// Returns null on any parse failure — caller falls back to default gain.
    /// </summary>
    public static LoudnessLevels? TryParse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        // Expected shape: "1:-5.0,0.0,3.0:-2.0"
        var colonParts = raw.Split(':');
        if (colonParts.Length < 3) return null;
        var triplet = colonParts[1].Split(',');
        if (triplet.Length < 3) return null;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (!double.TryParse(triplet[0], System.Globalization.NumberStyles.Float, inv, out var q)) return null;
        if (!double.TryParse(triplet[1], System.Globalization.NumberStyles.Float, inv, out var n)) return null;
        if (!double.TryParse(triplet[2], System.Globalization.NumberStyles.Float, inv, out var l)) return null;
        if (!double.TryParse(colonParts[2], System.Globalization.NumberStyles.Float, inv, out var d)) return null;
        return new LoudnessLevels(q, n, l, d);
    }
}

/// <summary>
/// Spotify account subscription tiers.
/// </summary>
public enum AccountType
{
    /// <summary>
    /// Free tier (ads, limited features).
    /// </summary>
    Free,

    /// <summary>
    /// Premium subscription (full features).
    /// </summary>
    Premium,

    /// <summary>
    /// Spotify for Artists.
    /// </summary>
    Artist,

    /// <summary>
    /// Premium Family plan.
    /// </summary>
    Family,

    /// <summary>
    /// Unknown or unrecognized account type.
    /// </summary>
    Unknown
}
