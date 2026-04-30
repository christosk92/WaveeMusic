using System;

namespace Wavee.UI.WinUI.Services;

internal static class VideoAutoNavigationSuppressor
{
    private static readonly object Gate = new();
    private static readonly TimeSpan SuppressionWindow = TimeSpan.FromSeconds(8);

    private static string? s_suppressedTrackUri;
    private static bool s_suppressNextLocalVideo;
    private static DateTimeOffset s_expiresAt;

    public static void SuppressNextLocalVideoNavigation(string? trackUri = null)
    {
        lock (Gate)
        {
            s_suppressedTrackUri = IsLocalTrackUri(trackUri) ? trackUri : null;
            s_suppressNextLocalVideo = s_suppressedTrackUri is null;
            s_expiresAt = DateTimeOffset.UtcNow.Add(SuppressionWindow);
        }
    }

    public static bool TryConsume(string? trackUri)
    {
        if (!IsLocalTrackUri(trackUri))
            return false;

        lock (Gate)
        {
            if (DateTimeOffset.UtcNow > s_expiresAt)
            {
                Clear();
                return false;
            }

            var shouldSuppress = s_suppressNextLocalVideo ||
                string.Equals(s_suppressedTrackUri, trackUri, StringComparison.Ordinal);
            if (shouldSuppress)
                Clear();

            return shouldSuppress;
        }
    }

    private static void Clear()
    {
        s_suppressedTrackUri = null;
        s_suppressNextLocalVideo = false;
        s_expiresAt = default;
    }

    private static bool IsLocalTrackUri(string? uri)
        => !string.IsNullOrWhiteSpace(uri) &&
           uri.StartsWith("wavee:local:track:", StringComparison.Ordinal);
}
