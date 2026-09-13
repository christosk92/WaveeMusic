using System;
using Google.Protobuf;
using Wavee.Core;
using Aa = Wavee.Protocol.AudioAttributes;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee.Backend.Hydration.Projectors;

// ── Kind 222 AUDIO_ATTRIBUTES_V2 → Track.TempoBpm / MusicalKey / Camelot* (design §2.4) ──────────────────────────────
// The parse moves verbatim from SpotifyTrackAdornmentService.ParseAudio; what it no longer carries is the request half
// (that service asked 179+222+6 in its own POST, with its own 300-cap and its own negative dictionary). Tempo is also
// what marked a row "already adorned" there, and it stays the mark here — but as this projector's OWN mark, so a track
// with a tempo and no descriptors is no longer counted as done for kind 6.

/// <summary>The kind-222 projector: tempo, key name, and the Camelot-wheel slot + its colour onto a resident row.</summary>
public sealed class AudioAttributesProjector : ITraitProjector
{
    // Unknown fields discarded: nothing round-trips a payload back to the server and Spotify keeps growing these.
    static readonly MessageParser<Aa.AudioAttributes> AudioParser = Aa.AudioAttributes.Parser.WithDiscardUnknownFields(true);

    public TraitSet Trait => TraitSet.AudioAttributes;
    public Xm.ExtensionKind Kind => Xm.ExtensionKind.AudioAttributesV2;

    public bool AppliesTo(EntityKind kind) => TraitApplicability.Applies(Kind, kind);

    /// <summary>A resident tempo is the mark. A tempo is never 0 by contract (0 on the wire is "unknown", never
    /// "silent"), so "not null" is exactly "this row has been through here".</summary>
    public bool AlreadyHas(IStore store, string uri, DateTimeOffset now) => store.GetTrack(uri) is { TempoBpm: not null };

    public TraitOutcome Project(TraitBatch batch, string uri, in TraitPayloads payloads)
    {
        var payload = payloads.Payload(Kind);
        if (payload is null)
            // An explicit 404/empty is the answer for plenty of tracks (11.5k kind-222 payloads against 217k TrackV4 in
            // the corpus) — memoize it. A uri the response omitted has NOT been answered, so it stays re-askable.
            return payloads.HasAnswer(Kind) ? TraitOutcome.Negative : TraitOutcome.NotResident;

        double? bpm;
        string? key, camelot;
        uint? camelotColor;
        try
        {
            var attrs = AudioParser.ParseFrom(payload);
            // tempo is a DOUBLE on the wire. A 0 tempo is "unknown", not "silent" — never surface it as 0 BPM.
            bpm = attrs.Tempo > 0d ? attrs.Tempo : null;
            key = attrs.Key is { Name.Length: > 0 } k ? k.Name : null;
            camelot = attrs.Key?.Camelot is { Code.Length: > 0 } c ? c.Code : null;
            camelotColor = SpotifyColor.FromHex(attrs.Key?.Camelot?.Color);
        }
        catch (InvalidProtocolBufferException ex)
        {
            // One malformed entity must not sink the page — the other 299 rows still get their tempo.
            batch.Log.Event(WaveeLogLevel.Warning, "traits.222.parse", "audio-attributes parse failed", uri, ex: ex);
            return TraitOutcome.Negative;
        }

        if (bpm is null && key is null) return TraitOutcome.Negative;   // answered, but with nothing usable in it

        // Adornments DECORATE a row the list already projected; they never mint one (a minted row is a nameless row
        // that paints as a blank line). Not memoized — the answer will be wanted the moment the row lands.
        if (batch.Store.GetTrack(uri) is not { } row) return TraitOutcome.NotResident;
        if (row.TempoBpm == bpm && row.MusicalKey == key && row.CamelotCode == camelot && row.CamelotColor == camelotColor)
            return TraitOutcome.Unchanged;

        batch.Write(s => s.UpsertTrack(row with
        {
            TempoBpm = bpm ?? row.TempoBpm,
            MusicalKey = key ?? row.MusicalKey,
            CamelotCode = camelot ?? row.CamelotCode,
            CamelotColor = camelotColor ?? row.CamelotColor,
        }));
        return TraitOutcome.Applied;
    }
}
