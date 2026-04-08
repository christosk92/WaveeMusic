using System.Collections.Generic;
using Wavee.UI.WinUI.Data.Models;

namespace Wavee.UI.WinUI.Data;

public static class ChangelogData
{
    public static readonly IReadOnlyList<ChangelogRelease> Releases =
    [
        new ChangelogRelease
        {
            Version = "1.0.0",
            ReleaseTitle = "What's new in Wavee",
            Announcement = "Thanks for using Wavee! This release brings some of the most requested features. If you run into any issues, please send feedback from Settings.",
            ReleaseUrl = "https://github.com/christosk92/WaveeMusic/releases/tag/v1.0.0",
            Features =
            [
                new ChangelogFeature
                {
                    Title = "Canvas Lyrics",
                    ShortDescription = "Spotify Canvas visuals behind your lyrics",
                    Glyph = "\uE8D6",
                    DetailTitle = "Canvas Lyrics Overlay",
                    DetailDescription = "Experience lyrics like never before. When a track has a Spotify Canvas video, it plays as a beautiful backdrop behind synced lyrics, bringing your music to life with immersive visuals.",
                },
                new ChangelogFeature
                {
                    Title = "10-Band Equalizer",
                    ShortDescription = "Fine-tune your sound with precision",
                    Glyph = "\uE9E9",
                    DetailTitle = "Graphic Equalizer",
                    DetailDescription = "Take full control of your audio with a 10-band graphic equalizer. Choose from presets like Bass Boost, Treble Boost, Vocal, and Radio, or create your own custom profile.",
                },
                new ChangelogFeature
                {
                    Title = "Home Feed",
                    ShortDescription = "A personalized home experience",
                    Glyph = "\uE80F",
                    DetailTitle = "Your Personalized Home",
                    DetailDescription = "The redesigned home feed surfaces your recently played tracks, personalized mixes, and curated recommendations. Pin your favorite sections and reorder them to your liking.",
                },
                new ChangelogFeature
                {
                    Title = "Smart Reconnect",
                    ShortDescription = "Seamless recovery from connection drops",
                    Glyph = "\uE701",
                    DetailTitle = "Auto-Reconnect",
                    DetailDescription = "Wavee now automatically reconnects when your network drops. Audio playback resumes right where you left off with no manual intervention required.",
                },
                new ChangelogFeature
                {
                    Title = "Audio Caching",
                    ShortDescription = "Listen offline with local caching",
                    Glyph = "\uE896",
                    DetailTitle = "Local Audio Cache",
                    DetailDescription = "Frequently played tracks are cached locally so they start instantly and work even when your connection is spotty. Configure the cache size in Settings.",
                },
            ]
        }
    ];
}
