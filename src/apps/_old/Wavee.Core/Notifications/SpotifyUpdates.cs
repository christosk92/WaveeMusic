using System;
using System.Globalization;

namespace Wavee.Core;

/// <summary>What a Spotify-category (gander) notification actually IS, as far as any surface outside the notification
/// center is allowed to care. The feed's own taxonomy is a display CATEGORY (<see cref="NotificationCategory.Social"/>
/// holds followers, concert/show announcements and generic announcements alike), so a surface that wants only the
/// live-event items has to gate on something concrete — never on the category pill's label.</summary>
public enum SpotifyUpdateKind
{
    /// <summary>A follower, a generic announcement, anything unrecognised. Never leaves the notification center.</summary>
    Other,
    /// <summary>A concert / live-show announcement or a "days away" reminder for one.</summary>
    Concert,
}

/// <summary>The pure classifier + text hygiene for <see cref="SocialNotification"/> rows. Engine-free and
/// side-effect-free so both the notification center and Home's timeline can share ONE answer to "is this a concert
/// announcement", and so the answer is unit-testable without a feed.</summary>
public static class SpotifyUpdates
{
    /// <summary>Classify one gander row. The server's own discriminator wins when it shipped one; otherwise the ACTION
    /// TARGET decides, because a concert announcement is the only Spotify-category item whose click resolves to a
    /// concert entity. Deliberately NOT derived from the title: the title is server-localized prose.</summary>
    public static SpotifyUpdateKind KindOf(SocialNotification? n)
    {
        if (n is null) return SpotifyUpdateKind.Other;
        if (IsConcertWireType(n.WireType)) return SpotifyUpdateKind.Concert;
        return IsConcertTarget(n.ActionUri) ? SpotifyUpdateKind.Concert : SpotifyUpdateKind.Other;
    }

    /// <summary>True for the concert/live-show announcements — the ONLY Spotify-category rows a surface outside the
    /// center may adopt.</summary>
    public static bool IsConcert(SocialNotification? n) => KindOf(n) == SpotifyUpdateKind.Concert;

    /// <summary>The feed's optional per-item discriminator, when the payload carried one. Matched on CONCERT/LIVE only —
    /// both are concrete; a generic "EVENT" bucket is not, and a false positive here puts a follower row on Home.</summary>
    public static bool IsConcertWireType(string? wireType)
        => wireType is { Length: > 0 } t
           && (t.Contains("CONCERT", StringComparison.OrdinalIgnoreCase)
               || t.Contains("LIVE", StringComparison.OrdinalIgnoreCase));

    /// <summary>True when a notification's action target is a concert entity — the <c>spotify:concert:</c> uri form, or
    /// the web forms the feed uses when it hands out a NAVIGATE_WEBVIEW target.</summary>
    public static bool IsConcertTarget(string? uri)
    {
        if (uri is not { Length: > 0 }) return false;
        if (uri.Contains(":concert:", StringComparison.OrdinalIgnoreCase)) return true;
        if (!uri.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return false;
        return uri.Contains("concerts.spotify.com", StringComparison.OrdinalIgnoreCase)
            || uri.Contains("/concert/", StringComparison.OrdinalIgnoreCase)
            || uri.Contains("/concerts/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The artist/act the announcement is about, when the feed named one — the multi-user image's display
    /// names double as the act for event rows. Null when it named none; a caller supplies its own fallback line.</summary>
    public static string? ActName(SocialNotification? n)
    {
        if (n is null) return null;
        var names = n.UserNames;
        for (int i = 0; i < names.Count; i++)
            if (names[i] is { Length: > 0 } s && !string.IsNullOrWhiteSpace(s)) return s;
        return null;
    }

    /// <summary>The title with any LEADING decorative glyphs removed ("⏰ Just days away: …" → "Just days away: …").
    /// A row that carries its own KIND badge does not need the feed's emoji to say what it is, and the glyph throws the
    /// row's optical left edge out against its neighbours.
    /// <para>Casing is left ALONE: the string is server-localized prose, and a case transform over a localized string
    /// mangles Turkish dotted i and expands German ß (the app-wide rule — see the eyebrow conversion in D32).</para>
    /// <para>Never returns empty: a title that is nothing but glyphs is returned verbatim, because a blank row is worse
    /// than an emoji.</para></summary>
    public static string CleanTitle(string? title)
    {
        if (title is not { Length: > 0 }) return "";
        int i = 0;
        while (i < title.Length)
        {
            char c = title[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            int step = char.IsHighSurrogate(c) && i + 1 < title.Length && char.IsLowSurrogate(title[i + 1]) ? 2 : 1;
            var cat = CharUnicodeInfo.GetUnicodeCategory(title, i);
            if (cat is UnicodeCategory.OtherSymbol or UnicodeCategory.ModifierSymbol or UnicodeCategory.NonSpacingMark
                or UnicodeCategory.EnclosingMark or UnicodeCategory.Format or UnicodeCategory.PrivateUse
                or UnicodeCategory.OtherNotAssigned or UnicodeCategory.Surrogate)
            {
                i += step;
                continue;
            }
            break;
        }
        if (i == 0) return title;                       // the common case allocates nothing
        var rest = title[i..];
        return rest.Length == 0 ? title : rest;
    }
}
