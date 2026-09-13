namespace Wavee.Core;

/// <summary>Colour parsing shared by every Spotify surface that ships colour as a CSS hex string.
///
/// Spotify sends colour two different ways: the protobuf surfaces (extended-metadata kind 179) send RGBA channels,
/// while the GraphQL surfaces (browse headers, category cards, Camelot key colours) send <c>"#1e3264"</c>. This is the
/// single parser for the latter — it lived in three places before, each with its own subtly different validation.</summary>
public static class SpotifyColor
{
    /// <summary>"#1e3264" → opaque ARGB (the packing every colour in the app uses, from the cover-colour plane's
    /// roles to a Camelot key swatch).
    ///
    /// Returns null for ANYTHING that is not exactly a 6-digit hex colour. Degrading to "no colour" is deliberate: a
    /// server-side format change should drop back to the neutral treatment, never render a wrong colour that looks
    /// intentional. Short form (#abc) is not accepted — Spotify has never sent it, and silently guessing an expansion
    /// would be inventing data.</summary>
    public static uint? FromHex(string? hex)
    {
        if (hex is null || hex.Length != 7 || hex[0] != '#') return null;

        uint value = 0;
        for (int i = 1; i < 7; i++)
        {
            int digit = hex[i] switch
            {
                >= '0' and <= '9' => hex[i] - '0',
                >= 'a' and <= 'f' => hex[i] - 'a' + 10,
                >= 'A' and <= 'F' => hex[i] - 'A' + 10,
                _ => -1,
            };
            if (digit < 0) return null;
            value = (value << 4) | (uint)digit;
        }
        return 0xFF000000u | value;
    }

    /// <summary>Pack 8-bit channels into opaque ARGB. Alpha 0 is treated as "unspecified" and promoted to opaque:
    /// every captured colour carries a=255, and a fully transparent accent would render as nothing at all.</summary>
    public static uint Pack(uint r, uint g, uint b, uint a = 255)
    {
        uint alpha = a == 0 ? 255u : (a > 255u ? 255u : a);
        return (alpha << 24)
             | ((r > 255u ? 255u : r) << 16)
             | ((g > 255u ? 255u : g) << 8)
             | (b > 255u ? 255u : b);
    }
}
