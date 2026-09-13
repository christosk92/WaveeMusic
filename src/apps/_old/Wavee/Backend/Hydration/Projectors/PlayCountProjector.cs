using System;
using Wavee.Core;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee.Backend.Hydration.Projectors;

// ── Kind 185 ON_PLATFORM_REPUTATION_TRAIT → Track.PlayCount (design §2.4) ────────────────────────────────────────────
// The decoder moves verbatim from Backend/Metadata's OnPlatformReputation; the fill rules move verbatim from
// TrackPlayCountHydrator (decorate a resident row, never mint one, never invent a 0). What is gone is the pair of
// classes around them — a reader that built its own request and a hydrator that built its own 300-cap and memo — which
// is why an album open used to ask for kind 185 twice: once from the album ladder and once from the plays column.
//
// The message is POLYMORPHIC: xpui reads `onPlatformReputationTrait.rating.average` off shows / audiobooks / artist
// unions from the same kind. Nothing here decodes a `rating` arm — a track payload never carries one, and
// TraitApplicability keeps the ask to playables.

/// <summary>The kind-185 projector: the stream count an album page's Plays column (and the artist chart's ordering)
/// renders. An ABSENT count means "unknown" — never an invented 0.</summary>
public sealed class PlayCountProjector : ITraitProjector
{
    /// <summary>The only field a track's payload carries: proto2, one varint at field 3 — the stream count
    /// (docs/plans/wavee/xm-playcount-handoff.md, confirmed live on 1.2.95.453 and by the SAZ census: 100/100 tracks
    /// 200, never a 304, range 1.4e8–2.1e9). No <c>.proto</c>: one field is not worth a schema, and the message's other
    /// arms must NOT be decoded here.</summary>
    public const int PlayCountField = 3;

    public TraitSet Trait => TraitSet.PlayCount;
    public Xm.ExtensionKind Kind => Xm.ExtensionKind.OnPlatformReputationTrait;

    public bool AppliesTo(EntityKind kind) => TraitApplicability.Applies(Kind, kind);

    /// <summary>A resident count is the mark. A count is never 0 by contract (the decoder refuses to invent one), so
    /// <c>&gt; 0</c> is exactly "this row is filled".</summary>
    public bool AlreadyHas(IStore store, string uri, DateTimeOffset now) => store.GetTrack(uri) is { PlayCount: > 0 };

    public TraitOutcome Project(TraitBatch batch, string uri, in TraitPayloads payloads)
    {
        var payload = payloads.Payload(Kind);
        if (payload is null)
            return payloads.HasAnswer(Kind) ? TraitOutcome.Negative : TraitOutcome.NotResident;
        // Undecodable or a zero count is the same answer as a 404: this uri has no count, and re-asking cannot change
        // that this session.
        if (!TryReadPlayCount(payload.Span, out long plays) || plays <= 0) return TraitOutcome.Negative;

        // Counts DECORATE a row the list already projected; minting one would materialise a nameless track that paints
        // as a blank line. NotResident is never memoized — the count is wanted the moment the row lands.
        if (batch.Store.GetTrack(uri) is not { } row) return TraitOutcome.NotResident;
        if (row.PlayCount == plays) return TraitOutcome.Unchanged;

        batch.Write(s => s.UpsertTrack(row with { PlayCount = plays }));
        return TraitOutcome.Applied;
    }

    /// <summary>Walks the message, skipping unknown fields by wire type, and returns field 3's varint when it is
    /// present, varint-typed and positive. Truncated / malformed bytes, a wrong wire type on field 3, or a zero count →
    /// false (a count is never invented).</summary>
    public static bool TryReadPlayCount(ReadOnlySpan<byte> payload, out long plays)
    {
        plays = 0;
        int pos = 0;
        while (pos < payload.Length)
        {
            if (!TryReadVarint(payload, ref pos, out ulong tag) || tag > int.MaxValue) return false;
            int field = (int)(tag >> 3), wireType = (int)(tag & 7);
            if (field == 0) return false;
            switch (wireType)
            {
                case 0:   // varint
                    if (!TryReadVarint(payload, ref pos, out ulong v)) return false;
                    if (field == PlayCountField)
                    {
                        if (v == 0 || v > long.MaxValue) return false;
                        plays = (long)v;
                        return true;
                    }
                    break;
                case 1:   // fixed64
                    if (field == PlayCountField) return false;
                    pos += 8;
                    if (pos > payload.Length) return false;
                    break;
                case 2:   // length-delimited
                    if (field == PlayCountField) return false;
                    if (!TryReadVarint(payload, ref pos, out ulong len) || len > (ulong)(payload.Length - pos)) return false;
                    pos += (int)len;
                    break;
                case 5:   // fixed32
                    if (field == PlayCountField) return false;
                    pos += 4;
                    if (pos > payload.Length) return false;
                    break;
                default:  // groups (3/4) and anything else: not a shape this trait uses
                    return false;
            }
        }
        return false;
    }

    static bool TryReadVarint(ReadOnlySpan<byte> data, ref int pos, out ulong value)
    {
        value = 0;
        int shift = 0;
        while (pos < data.Length && shift < 64)
        {
            byte b = data[pos++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
        }
        return false;
    }
}
