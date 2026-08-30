using System;
using System.Threading;

namespace Wavee.Backend.Spotify;

/// <summary>Resolves the machine's local zone to the IANA id Spotify's <c>home</c> query expects.
///
/// Why this exists: Windows reports zones as Windows ids ("W. Europe Standard Time"); the captured desktop client
/// sends IANA ("Europe/Amsterdam"). The zone drives the greeting bucket and the time-of-day shelves ("Soundtrack your
/// Tuesday afternoon"), so sending a wrong zone is not cosmetic — it returns someone else's afternoon.
///
/// The conversion reads ICU data, which a globalization-invariant trim could remove. Every failure path lands on
/// <see cref="Fallback"/> and reports itself through <see cref="LastResolveFailed"/> so the caller can log it ONCE
/// instead of per request.</summary>
public static class SpotifyTimeZone
{
    /// <summary>What we send when the local zone cannot be expressed as IANA. UTC is a real zone, so the server still
    /// answers; the shelves are simply not localised to the user's clock.</summary>
    public const string Fallback = "Etc/UTC";

    static string? _cachedIana;
    static string? _cachedFromWindowsId;
    static int _failed;

    /// <summary>True when the most recent resolve fell back. Read after <see cref="LocalIana"/>; latched, not reset.</summary>
    public static bool LastResolveFailed => Volatile.Read(ref _failed) != 0;

    /// <summary>The local zone as an IANA id, or <see cref="Fallback"/>. Cached against the current Windows zone id so
    /// a mid-session timezone change (travel, DST-policy update) is picked up without re-running the conversion on
    /// every call.</summary>
    public static string LocalIana
    {
        get
        {
            string windowsId;
            try
            {
                windowsId = TimeZoneInfo.Local.Id;
            }
            catch (Exception)
            {
                // TimeZoneInfo.Local itself can throw on a machine with a corrupt/absent registry zone.
                Volatile.Write(ref _failed, 1);
                return Fallback;
            }

            // Fast path: same zone as last time.
            if (Volatile.Read(ref _cachedFromWindowsId) == windowsId && Volatile.Read(ref _cachedIana) is { } hit)
                return hit;

            string resolved = Convert(windowsId);
            Volatile.Write(ref _cachedIana, resolved);
            Volatile.Write(ref _cachedFromWindowsId, windowsId);
            return resolved;
        }
    }

    static string Convert(string windowsId)
    {
        // Already IANA (Linux/macOS, or a Windows build carrying ICU ids) — "Area/Location" always contains '/'.
        if (windowsId.Contains('/'))
        {
            Volatile.Write(ref _failed, 0);
            return windowsId;
        }

        try
        {
            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(windowsId, out var iana) && iana is { Length: > 0 })
            {
                Volatile.Write(ref _failed, 0);
                return iana;
            }
        }
        catch (Exception)
        {
            // Globalization-invariant mode throws rather than returning false.
        }

        Volatile.Write(ref _failed, 1);
        return Fallback;
    }
}
