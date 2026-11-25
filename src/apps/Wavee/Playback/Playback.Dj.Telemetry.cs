// ── Playback/Playback.Dj.Telemetry.cs ────────────────────────────────────────────────────────────────────────────────
// AI DJ play registration (plan-dj §2.9, D5): what the official client reports for DJ plays, through the host's one play report
// (`Playback.Host.Context.cs`, `Report`) and the one registration (`Spotify.Telemetry`).
//
// Role: SHELL (UI thread: the report runs in the drain)
// Spec: docs/plans/wavee/dj-implementation.md §2.9; dj-protocol.md §5
//
//   a narration starts (a Tts row) ─▶ ReportNarration: a registration under the item's WIRE name (spotify:media:<id>, the welcome's
//                                     spotify:media:0000…), provider narration/<slot>, the start reason the reducer names (trackdone,
//                                     fwdbtn; the welcome's playbtn), the DJ list as the play context, the narration's dj~ decision id and its
//                                     attribution; no media id, no play log, no play history, no herodotus (it is no track)
//   a DJ track starts             ─▶ AttributeDjTrack: the row's ssp~ decision id and attribution from the DJ book
//   either ends                   ─▶ the ordinary Ended: RawCoreStreamSegment + RawCoreStream (+ the trailers), with the end reason
//   the talk-over (D3)            ─▶ Spotify.Telemetry.Overlapped: ms_played_overlapping (16, 67) on the narration and
//                                     ms_narration_overlapping (46) on the track under it; 0 until then
//
// A Tts identity never reaches the wire: every event names the narration's spotify:media: uri (Spotify.Egress refuses a wavee: one).
// Not sent, because no capture names its field: the "dj" agentic product type the official RawCoreStream carries (dj-protocol §5).

namespace Wavee;

public static partial class Playback
{
    /// <summary>The audio format the official client names a narration clip by in its RawCoreStream (dj-protocol §5: "MP3 160 kbps",
    /// whatever the clip's own rate: the CDN serves about 320 kbit/s).</summary>
    public const string NarrationAudioFormat = "MP3 160 kbps";

    /// <summary>The narration whose clip is <paramref name="id"/>: the deck row's when it is that clip, else any this session made.</summary>
    static Dj.Spoken? NarrationReported(EntityId id)
    {
        ReadOnlySpan<QueueEdge> rows = Queue.Rows;
        int deck = s_state.Cursor.Index;
        if ((uint)deck < (uint)rows.Length && Dj.NarrationOf(rows[deck].ItemId) is { } onDeck && onDeck.Id.Equals(id)) return onDeck;
        foreach (Dj.Spoken sp in Dj.AllSpoken)
            if (sp.Id.Equals(id)) return sp;
        return null;
    }

    /// <summary>A narration started (<paramref name="id"/> its Tts clip): open its registration (see the banner). Answers false when the
    /// clip is no narration this session knows (nothing is reported).</summary>
    static bool ReportNarration(in PlayReport r, EntityId id)
    {
        if (NarrationReported(id) is not { } sp) return false;
        string contextUri = r.Context.IsEmpty ? "" : r.Context.Text;
        long duration = r.DurationMs > 0 ? r.DurationMs : Math.Max(0L, sp.DurationMs);
        // A DJ start is always a press of a play button (the DJ list has no rows to click: its page, its card and its sidebar row all start it
        // with Play), so the welcome that opens it starts with `playbtn`, as the official client reports it (dj-protocol §5, 3 of 3 starts).
        string reason = sp.Slot == Dj.Slot.Welcome && r.StartReason == PlayReason.ClickRow ? ReasonText(PlayReason.PlayButton) : ReasonText(r.StartReason);
        s_registration = Spotify.Telemetry.Started(NewPlaybackIds(), sp.MediaUri, contextUri, sp.Provider, reason,
            mediaId: null, bitrateKbps: 0, NarrationAudioFormat, duration, r.StartPosMs,
            PlayerSession.TelemetryText(s_wireIds.SessionId, video: false));
        Spotify.Telemetry.Attribute(ref s_registration, sp.Keys.GetValueOrDefault("decision_id", ""),
            sp.Keys.GetValueOrDefault("custom_reporting_attribution", ""));
        s_registeredId = id;
        return true;
    }

    /// <summary>A track started under a DJ session: its row's decision id and attribution from the DJ book (§2.9), when the deck row is
    /// a DJ row for <paramref name="id"/>.</summary>
    static void AttributeDjTrack(EntityId id)
    {
        ReadOnlySpan<QueueEdge> rows = Queue.Rows;
        int deck = s_state.Cursor.Index;
        if ((uint)deck >= (uint)rows.Length || !Queue.RefAt(deck).Id.Equals(id) || Dj.RowOf(rows[deck].ItemId) is not { } row) return;
        Spotify.Telemetry.Attribute(ref s_registration, row.DecisionId, row.Attribution);
    }
}
