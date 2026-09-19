using System;
using System.Globalization;
using Google.Protobuf;
using Wavee.Core;
using Ca = Wavee.Protocol.ContentAgnostic;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee.Backend.Hydration.Projectors;

// ── Kind 183 PUBLISHING_METADATA_TRAIT → Album.Copyright / ReleaseDate / ReleaseDatePrecision (design §2.4) ───────────
// Moved verbatim from AlbumPublishingSource.Apply/ReleaseDate. The "About this release" tile used to ride getAlbum — a
// heavy Pathfinder round trip whose label / other-versions / more-by halves belong below the fold. Kind 183 is the same
// two facts as a ~262-byte payload, and it now rides the album ladder's OWN catalogue POST (fused as an extra kind)
// instead of the per-album coalescer + per-session memo pair the source carried.
//
// ADDITIVE ONLY. It fills Copyright / ReleaseDate / ReleaseDatePrecision when — and only when — the resident album has
// none, and never touches Label (183 carries no label), Hydration (this is not an envelope) or Tracks. When getAlbum
// lands, its values are the richer ones; the store's album merge is null-coalesce for exactly these fields and the ??s
// below make the same decision, so the ORDER the two sources land in cannot change what the tile shows.
//
// Albums only (TraitApplicability): the probe's artist payload is junk (an 8-byte date near the probe day, not a career
// date) and the track payload is date-only with nowhere on Track to put it.

/// <summary>The kind-183 projector: an album's ©/℗ block and calendar release date.</summary>
public sealed class PublishingProjector : ITraitProjector
{
    static readonly MessageParser<Ca.PublishingMetadataTrait> PayloadParser =
        Ca.PublishingMetadataTrait.Parser.WithDiscardUnknownFields(true);

    public TraitSet Trait => TraitSet.Publishing;
    public Xm.ExtensionKind Kind => Xm.ExtensionKind.PublishingMetadataTrait;

    public bool AppliesTo(EntityKind kind) => TraitApplicability.Applies(Kind, kind);

    /// <summary>Both facets present is the mark — an album carrying only one of them still has something to gain.</summary>
    public bool AlreadyHas(IStore store, string uri, DateTimeOffset now)
        => store.GetAlbum(uri) is { Copyright: not null, ReleaseDate: not null };

    public TraitOutcome Project(TraitBatch batch, string uri, in TraitPayloads payloads)
    {
        var payload = payloads.Payload(Kind);
        if (payload is null)
            // The ordinary answer for plenty of albums; the tile simply does not render.
            return payloads.HasAnswer(Kind) ? TraitOutcome.Negative : TraitOutcome.NotResident;

        Ca.PublishingMetadataTrait msg;
        try { msg = PayloadParser.ParseFrom(payload); }
        catch (InvalidProtocolBufferException ex)
        {
            batch.Log.Event(WaveeLogLevel.Warning, "traits.183.parse", "publishing parse failed", uri, ex: ex);
            return TraitOutcome.Negative;
        }

        // The 183 lines already carry their ©/℗ symbol, so this only normalizes + de-duplicates + joins them the way the
        // getAlbum path does — the tile must read identically whichever source filled the field.
        string? copyright = SpotifyExportMapper.JoinCopyrightLines(msg.Copyright);
        var (date, precision) = ReleaseDate(msg.Date);
        // Neither facet (the artist-shaped junk, or a date with no year) is a miss, not an answer.
        if (copyright is null && date is null) return TraitOutcome.Negative;

        // NEVER MINT AN ALBUM. This projector knows a uri and two facets — not a name, cover or artist — so a row
        // invented here would be a nameless album in the store and in every grid that enumerates it. Not memoized: a
        // later open, by which time the row exists, must be free to try again (an etag hit, so it costs no payload).
        if (batch.Store.GetAlbum(uri) is not { } album) return TraitOutcome.NotResident;

        string? mergedCopyright = album.Copyright ?? copyright;
        string? mergedDate = album.ReleaseDate ?? date;
        // The precision rides its OWN date: if getAlbum already supplied a ReleaseDate, stamping OUR precision onto it
        // could mislabel THEIR value (their isoString and this calendar date need not agree on granularity).
        string? mergedPrecision = album.ReleaseDate is null && date is not null
            ? album.ReleaseDatePrecision ?? precision
            : album.ReleaseDatePrecision;

        if (ReferenceEquals(mergedCopyright, album.Copyright) && ReferenceEquals(mergedDate, album.ReleaseDate)
            && ReferenceEquals(mergedPrecision, album.ReleaseDatePrecision))
            return TraitOutcome.Unchanged;   // getAlbum already filled every facet — no write, hence no change signal

        batch.Write(s => s.UpsertAlbum(album with
        {
            Copyright = mergedCopyright,
            ReleaseDate = mergedDate,
            ReleaseDatePrecision = mergedPrecision,
        }));
        return TraitOutcome.Applied;
    }

    /// <summary>The calendar date as an ISO string plus the precision word the release-date formatters switch on
    /// ("YEAR" / "MONTH" / "DAY" — the vocabulary getAlbum's own <c>date.precision</c> writes). Month and day are 0 on a
    /// coarse release, which IS the precision signal; an out-of-range part degrades to the coarser form rather than
    /// minting an unparseable "2014-13-40". No year at all → no date. The payload's two unix timestamp arms are
    /// deliberately never read: on the probe album they say 2020-11-12 (the Expanded EDITION) while the calendar date
    /// says 2014-11-18 (the album).</summary>
    public static (string? Date, string? Precision) ReleaseDate(Ca.PublishingMetadataTrait.Types.Date? date)
    {
        if (date is not { Year: > 0 and <= 9999 } d) return (null, null);
        if (d.Month is < 1 or > 12) return (d.Year.ToString("D4", CultureInfo.InvariantCulture), "YEAR");
        if (d.Day < 1 || d.Day > DateTime.DaysInMonth(d.Year, d.Month))
            return (string.Create(CultureInfo.InvariantCulture, $"{d.Year:D4}-{d.Month:D2}"), "MONTH");
        return (string.Create(CultureInfo.InvariantCulture, $"{d.Year:D4}-{d.Month:D2}-{d.Day:D2}"), "DAY");
    }
}
