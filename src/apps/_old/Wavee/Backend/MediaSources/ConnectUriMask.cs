using System;
using System.Globalization;
using System.Text;
using Wavee.Core;

namespace Wavee.Backend.MediaSources;

// ── Connect publishability: what a remote controller is allowed to see ───────────────────────────────────────────────
// A Spotify Connect controller (phone, web player, another desktop) resolves every uri we publish against Spotify's own
// catalog. A `wavee:local:file:…` uri would resolve to NOTHING there — the row renders blank, and a tap on it asks us to
// play a uri it echoed back to us. Spotify solved this for ITS local files long ago with a self-describing namespace:
//
//     spotify:local:<artist>:<album>:<title>:<durationSeconds>
//
// — a uri that carries its own display metadata, so a controller renders the row correctly without resolving anything.
// (Wavee already recognizes that shape inbound; see NotificationPanel's uri-kind test.) So: publishable uris go on the
// wire verbatim, and everything else is rewritten into that shape. The QueueEntry UID is untouched by construction —
// the publisher masks the uri field ONLY — so skip_to/remove commands from a controller still address the right row.

/// <summary>Builds the <c>PublishUriMask</c> hook the <see cref="DeviceStatePublisher"/> applies to the current, prev
/// and next wire rows. Engine-free and pure so the golden wire shapes are unit-testable.</summary>
public static class ConnectUriMask
{
    public const string Prefix = "spotify:local:";

    /// <summary>The context a masked row belongs to: Spotify's own Local Files context, which is exactly what a real
    /// client publishes while playing a file off disk. A CONTEXT uri had to be masked too — the track mask alone left us
    /// publishing <c>context_uri: wavee:module:…</c>, a uri the connect-state service cannot resolve. Everything
    /// downstream then follows: the service never adopts us as the cluster's active device, the cluster we get back is
    /// the SAME state re-echoed with our masked track uri in it, and (nobody being "active") the fold treats that echo as
    /// a foreign track change. That is what turned a playing music video into audio and what wrote a masked uri into
    /// <c>session.json</c> for the next launch to choke on.</summary>
    public const string LocalFilesContext = "spotify:local-files";

    /// <summary>The context mask the <see cref="DeviceStatePublisher"/> applies to <c>context_uri</c>. Pure (no registry:
    /// a CONTEXT is not a playable and has no owning media provider) — a context Spotify can resolve goes verbatim,
    /// everything else publishes as <see cref="LocalFilesContext"/>, the container the masked rows claim to come from.</summary>
    public static string? MaskContext(string? contextUri)
        => string.IsNullOrEmpty(contextUri) || ContextResolve.IsSpotifyContext(contextUri)
            ? contextUri
            : LocalFilesContext;

    /// <summary>The <see cref="MaskContext"/> delegate, for wiring.</summary>
    public static readonly Func<string?, string?> ContextMask = MaskContext;

    /// <summary>The mask for a live session: verbatim when the owning provider declares
    /// <see cref="MediaProviderCaps.ConnectPublish"/>, else Spotify's local-file shape. An UNOWNED uri is masked too —
    /// a uri no provider claims is certainly not one a remote controller can resolve.</summary>
    public static Func<Track, string> For(MediaProviderRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return track => registry.IsConnectPublishable(track.Uri) ? track.Uri : Mask(track);
    }

    /// <summary>Rewrite a playable into <c>spotify:local:artist:album:title:durSec</c>. Fields are individually encoded
    /// so a ':' inside a title can never fork the five segments; an unknown field stays EMPTY between its colons (that
    /// is what Spotify's own local uris do for a file with no album tag), and an unknown duration publishes 0.</summary>
    public static string Mask(Track track)
    {
        string artist = track.Artists is { Count: > 0 } a ? a[0].Name ?? "" : "";
        string album = track.Album?.Name ?? "";
        string title = track.Title ?? "";
        long seconds = track.DurationMs > 0 ? track.DurationMs / 1000 : 0;

        var sb = new StringBuilder(Prefix.Length + artist.Length + album.Length + title.Length + 16);
        sb.Append(Prefix);
        Append(sb, artist); sb.Append(':');
        Append(sb, album); sb.Append(':');
        Append(sb, title); sb.Append(':');
        sb.Append(seconds.ToString(CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    /// <summary>Spotify's local-uri field encoding: percent-encoding with spaces written as '+'. Every reserved
    /// character (including ':') is escaped, which is what keeps the five-segment shape parseable.</summary>
    static void Append(StringBuilder sb, string field)
    {
        if (field.Length == 0) return;
        var escaped = Uri.EscapeDataString(field);
        for (int i = 0; i < escaped.Length; i++)
        {
            // "%20" → "+" (the one deviation from plain percent-encoding, and the one Spotify's own uris use).
            if (escaped[i] == '%' && i + 2 < escaped.Length && escaped[i + 1] == '2' && escaped[i + 2] == '0')
            {
                sb.Append('+');
                i += 2;
                continue;
            }
            sb.Append(escaped[i]);
        }
    }
}
