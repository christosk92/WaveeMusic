// ── Wavee.Tests/DealerFrameDispositionTests.cs — which dealer frames the capture calls "ignored" (#163) ──────────
//
// 2026-10-01 flight recorder: 828 FrameIgnored in nine days — 428 playlist pushes and 12 collection pushes the library
// applies or re-asks, 385 of the Jam's social-connect broadcast status, 3 liked-songs-artist. The record was written
// before the library looked. `DealerFrameDisposition` (Spotify.Dealer.Rules.cs) is the pure call, and the Diagnostics
// anomaly list keeps IgnoredOnPurpose off itself (`CaptureIgnoreRules`, Capture.EchoDiff.cs).

using System.Text;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public class DealerFrameDispositionTests
{
    const string PlaylistId = "0123456789abcdefghijAB";   // 22 base62 characters

    /// <summary>What Connect computes for one topic. The library's answer is the pure relation classifier's: its
    /// <c>OnDealerPush</c> returns true exactly when that names a relation (its ban, show and saved-episodes handlers
    /// claim their own topics first, and those are handled too).</summary>
    static Spotify.DealerFrameDisposition.Verdict Of(string topic, bool handledLocally = false)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(topic);
        bool libraryTook = LibraryPushRules.Classify(bytes) != LibraryPush.None;
        return Spotify.DealerFrameDisposition.Of(handledLocally, libraryTook, Spotify.DealerTopicRules.Classify(bytes));
    }

    [Fact]
    public void A_playlist_push_is_handled()
        => Assert.Equal(Spotify.DealerFrameDisposition.Verdict.Handled, Of("hm://playlist/v2/playlist/" + PlaylistId));

    [Theory]
    [InlineData("hm://collection/collection/someuser")]
    [InlineData("hm://collection/collection/someuser/json")]
    [InlineData("hm://collection/artist/someuser")]
    [InlineData("hm://collection/artist/someuser/json")]
    [InlineData("hm://collection/show/someuser")]
    [InlineData("hm://collection/ylpin/someuser")]
    [InlineData("hm://playlist/v2/user/someuser/rootlist")]
    public void A_library_relation_push_is_handled(string topic)
        => Assert.Equal(Spotify.DealerFrameDisposition.Verdict.Handled, Of(topic));

    [Fact]
    public void The_liked_songs_artist_push_is_ignored_on_purpose()
        => Assert.Equal(Spotify.DealerFrameDisposition.Verdict.IgnoredOnPurpose,
            Of("hm://playlist/v2/list/liked-songs-artist/0123456789abcdef"));

    [Fact]
    public void The_jam_broadcast_status_is_unread()
        => Assert.Equal(Spotify.DealerFrameDisposition.Verdict.Unread, Of("social-connect/v2/broadcast_status_update"));

    [Theory]
    [InlineData("hm://something/else/entirely")]
    [InlineData("hm://playlist/v2/playlist/tooshort")]     // not a 22-character id: the library does not take it
    [InlineData("hm://collection/unknownset/someuser")]
    [InlineData("")]
    public void A_topic_nobody_reads_is_unread(string topic)
        => Assert.Equal(Spotify.DealerFrameDisposition.Verdict.Unread, Of(topic));

    [Theory]
    [InlineData(Spotify.DealerTopicRules.Kind.Hm)]
    [InlineData(Spotify.DealerTopicRules.Kind.NonHm)]
    [InlineData(Spotify.DealerTopicRules.Kind.IgnoredOnPurpose)]
    public void A_locally_handled_frame_is_never_recorded(Spotify.DealerTopicRules.Kind topic)
        => Assert.Equal(Spotify.DealerFrameDisposition.Verdict.Handled,
            Spotify.DealerFrameDisposition.Of(handledLocally: true, libraryTook: false, topic));

    [Theory]
    [InlineData(Spotify.DealerTopicRules.Kind.Hm)]
    [InlineData(Spotify.DealerTopicRules.Kind.NonHm)]
    [InlineData(Spotify.DealerTopicRules.Kind.IgnoredOnPurpose)]
    public void A_frame_the_library_took_is_handled_whatever_its_topic_kind(Spotify.DealerTopicRules.Kind topic)
        => Assert.Equal(Spotify.DealerFrameDisposition.Verdict.Handled,
            Spotify.DealerFrameDisposition.Of(handledLocally: false, libraryTook: true, topic));

    [Fact]
    public void A_handled_frame_has_no_reason_and_the_others_name_theirs()
    {
        Assert.False(Spotify.DealerFrameDisposition.TryReason(Spotify.DealerFrameDisposition.Verdict.Handled, out _));

        Assert.True(Spotify.DealerFrameDisposition.TryReason(Spotify.DealerFrameDisposition.Verdict.IgnoredOnPurpose, out var onPurpose));
        Assert.Equal(CaptureIgnoreReason.IgnoredOnPurpose, onPurpose);

        Assert.True(Spotify.DealerFrameDisposition.TryReason(Spotify.DealerFrameDisposition.Verdict.Unread, out var unread));
        Assert.Equal(CaptureIgnoreReason.Unread, unread);
    }

    // ── the Diagnostics anomaly list ─────────────────────────────────────────────────────────────────────────────────

    static CaptureEvent Ignored(long seq, string topic, string? reason)
        => new(seq, seq, seq, seq, 0, seq, CaptureKind.FrameIgnored, CapturePhase.Point, CapturePriority.Normal,
            new CaptureFields(topic, reason));

    [Fact]
    public void A_frame_ignored_on_purpose_is_not_an_anomaly_and_does_not_turn_the_root_amber()
    {
        var events = new[] { Ignored(1, "hm://playlist/v2/list/liked-songs-artist/abc", nameof(CaptureIgnoreReason.IgnoredOnPurpose)) };
        Assert.Empty(CaptureAnomalyScanner.Scan(events));
        Assert.Equal(CaptureRootStatus.Green, CaptureRootRollup.Compute(events));
    }

    [Theory]
    [InlineData(nameof(CaptureIgnoreReason.Unread))]
    [InlineData(nameof(CaptureIgnoreReason.StaleServerTime))]
    [InlineData((string?)null)]
    public void Any_other_ignored_frame_stays_an_anomaly_and_amber(string? reason)
    {
        var events = new[] { Ignored(1, "social-connect/v2/broadcast_status_update", reason) };
        var anomalies = CaptureAnomalyScanner.Scan(events);
        Assert.Single(anomalies);
        Assert.Equal(CaptureAnomalyKind.FrameIgnored, anomalies[0].Kind);
        Assert.Equal("social-connect/v2/broadcast_status_update", anomalies[0].Summary);
        Assert.Equal(CaptureRootStatus.Amber, CaptureRootRollup.Compute(events));
    }
}
