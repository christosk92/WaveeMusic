using System;
using System.Collections.Generic;
using Google.Protobuf;
using Wavee.Core;
using De = Wavee.Protocol.DescriptorExtension;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee.Backend.Hydration.Projectors;

// ── Kind 6 TRACK_DESCRIPTOR → Track.Tags (design §2.4) ───────────────────────────────────────────────────────────────
// The parse moves verbatim from SpotifyTrackAdornmentService.ParseTags, with ONE behaviour restored (probe finding 27):
// a descriptor list that comes back EMPTY writes `Tags = []`, not null. Null means "not fetched" and empty means "this
// track genuinely has none" — collapsing the two is what left the Liked Songs content-filter chips unable to tell a
// track it had never asked about from one the server had answered for.

/// <summary>The kind-6 projector: the genre/mood/activity concepts Spotify itself uses for the content-filter chips.</summary>
public sealed class DescriptorProjector : ITraitProjector
{
    static readonly MessageParser<De.ExtensionDescriptorData> DescriptorParser =
        De.ExtensionDescriptorData.Parser.WithDiscardUnknownFields(true);

    /// <summary>How many descriptors of a track are kept. The corpus runs 1..33 per track (median ~13) in descending
    /// weight, and the tail is noise for a chip bar — the first few ARE the track's identity.</summary>
    public const int MaxTagsPerTrack = 6;

    static readonly IReadOnlyList<string> NoTags = Array.Empty<string>();

    public TraitSet Trait => TraitSet.Descriptors;
    public Xm.ExtensionKind Kind => Xm.ExtensionKind.TrackDescriptor;

    public bool AppliesTo(EntityKind kind) => TraitApplicability.Applies(Kind, kind);

    /// <summary>Non-null Tags is the mark — INCLUDING the empty list, which is a real answer.</summary>
    public bool AlreadyHas(IStore store, string uri, DateTimeOffset now) => store.GetTrack(uri) is { Tags: not null };

    public TraitOutcome Project(TraitBatch batch, string uri, in TraitPayloads payloads)
    {
        // Get, not Payload: an EMPTY body is meaningful here. A descriptor message with no descriptors serializes to
        // zero bytes, so "answered with nothing" and "answered with an empty list" are the same thing on the wire — and
        // both mean the track genuinely has no tags (finding 27). Only an explicit 404 is a negative.
        var res = payloads.Get(Kind);
        if (res is null) return TraitOutcome.NotResident;   // omitted — not an answer, so it stays re-askable
        if (res.Missing) return TraitOutcome.Negative;

        IReadOnlyList<string> tags;
        try
        {
            var data = res.Payload is { IsEmpty: false } payload ? DescriptorParser.ParseFrom(payload) : null;
            if (data is null || data.Descriptors.Count == 0)
            {
                tags = NoTags;   // a real "this track has none" — written, not skipped
            }
            else
            {
                // Takes display_name — the presentation form ("K-Pop") — falling back to the lowercase match token when
                // the server omits it. Wire order is descending weight, so the first N are the strongest; no re-sorting.
                var list = new List<string>(Math.Min(data.Descriptors.Count, MaxTagsPerTrack));
                foreach (var d in data.Descriptors)
                {
                    if (list.Count >= MaxTagsPerTrack) break;
                    string label = d.DisplayName is { Length: > 0 } dn ? dn : d.Text;
                    if (label.Length > 0) list.Add(label);
                }
                tags = list;
            }
        }
        catch (InvalidProtocolBufferException ex)
        {
            batch.Log.Event(WaveeLogLevel.Warning, "traits.6.parse", "descriptor parse failed", uri, ex: ex);
            return TraitOutcome.Negative;
        }

        // Descriptors DECORATE a row the list already projected; they never mint one.
        if (batch.Store.GetTrack(uri) is not { } row) return TraitOutcome.NotResident;
        if (row.Tags is { } current && Same(current, tags)) return TraitOutcome.Unchanged;

        batch.Write(s => s.UpsertTrack(row with { Tags = tags }));
        return TraitOutcome.Applied;
    }

    static bool Same(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
        return true;
    }
}
