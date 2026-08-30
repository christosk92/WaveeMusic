using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend.Metadata;
using Wavee.Core;
using Xm = Wavee.Protocol.ExtendedMetadata;
// EntityKind: the ONE uri vocabulary (Wavee.Core), not Backend.Metadata's thin transport projection of it — importing
// that namespace for CachedExtension/ExtendedMetadataSource is what makes the alias necessary.
using EntityKind = Wavee.Core.EntityKind;

namespace Wavee.Backend.Hydration.Projectors;

// ── Kind 99 VIDEO_ASSOCIATIONS → the VideoAssociation plane (design §2.4) ─────────────────────────────────────────────
// Moved verbatim from SpotifyVideoService (Fold/Project/ParseAssoc/CeHasVideo/AssociatedVideoGid/NeedsRecovery/
// RecoverCanonicalAsync/ApplyRecovered): the DECODING is unchanged, only the shape around it. What the service used to
// own — the 300-uri slice, the freshness filter, the etag-vs-raw fork, the bulk scope, the negative memo — is the
// pipeline's now, which is the whole point: one POST carries this kind next to 222/6/179/185 instead of four.
//
// Two behaviours changed on purpose (design D9, both about a NEGATIVE never beating a POSITIVE):
//   • a Missing answer no longer downgrades a resident `HasVideo:true` — it refreshes its freshness instead. The old
//     code let an alias's own 404 overwrite the record recovery had just placed under it, so a row lit up and went
//     dark again on the next list realize.
//   • the post-recovery `ExtensionEtagCache.MarkStale(alias, 99)` is GONE. It existed to force the next detect to
//     re-run recovery; with the rule above, the recovered positive simply stands, and marking the alias stale only
//     bought another 404 (and, with it, the downgrade).

/// <summary>The kind-99 projector: folds an entity's video association into the store plane and — for the alias/relink
/// case that 404s on 99 — recovers it through the canonical id once per alias per session.</summary>
public sealed class VideoProjector : ITraitProjector
{
    static readonly MessageParser<Xm.VideoAssociations> AssocParser = Xm.VideoAssociations.Parser.WithDiscardUnknownFields(true);

    // Kind 182 rides the SAME entity request (GzipExtensionRequest groups kinds under one EntityRequest, so it costs no
    // round trip) and is the gate for canonical recovery: it is canonical-computed, so it still reports the video when
    // kind 99 — which is keyed by the canonical id — 404s on an alias.
    static readonly Xm.ExtensionKind[] CompanionKinds = [Xm.ExtensionKind.ConsumptionExperienceTrait];

    /// <summary>Ceiling on the per-session "already tried to recover this alias" set. Past it we stop remembering and
    /// pay a repeat recovery rather than grow without bound — the same trade <see cref="NegativeMemo"/> makes.</summary>
    const int AttemptCap = NegativeMemo.Cap;
    const int RecoverLineCap = 24;   // per-pass budget for the per-alias detail; the summary always fires

    readonly IExtensionReader _reader;
    readonly ConcurrentDictionary<string, byte> _attempted = new(StringComparer.Ordinal);

    /// <param name="reader">The display-only extension reader (design §2.5) — recovery's TrackV4/212/99 reads go
    /// through it so they share the pipeline's etag cache and negative memo instead of a second transport.</param>
    public VideoProjector(IExtensionReader reader) => _reader = reader ?? throw new ArgumentNullException(nameof(reader));

    public TraitSet Trait => TraitSet.Video;
    public Xm.ExtensionKind Kind => Xm.ExtensionKind.VideoAssociations;
    public ReadOnlySpan<Xm.ExtensionKind> Companions => CompanionKinds;

    public bool AppliesTo(EntityKind kind) => TraitApplicability.Applies(Kind, kind);

    /// <summary>The plane's own verdict-shaped freshness IS the mark (a positive stands for 6 h, a negative for 30 min
    /// — see <see cref="VideoAssociation.IsFresh"/>), so there is nothing to re-derive here.</summary>
    public bool AlreadyHas(IStore store, string uri, DateTimeOffset now)
        => store.GetVideoAssociation(uri) is { } a && a.IsFresh(now);

    public TraitOutcome Project(TraitBatch batch, string uri, in TraitPayloads payloads)
    {
        var res = payloads.Get(Kind);
        // ABSENT IS NOT MISSING: a uri the response simply omitted has not been answered, so it must stay re-askable
        // (a memo here is the 24 h wedge the extension cache refuses to invent for exactly the same reason).
        if (res is null) return TraitOutcome.NotResident;

        if (!Fold(batch, uri, res)) return TraitOutcome.NotResident;   // malformed / nothing to write — never memoized

        // Kind 182 is consulted ONLY for a kind-99 miss. A 404 the consumption-experience trait contradicts is the
        // alias/relinked-id case; everything else is a plain "no video" and must not cost a recovery pass.
        if (NeedsRecovery(batch.Store, uri) && CeHasVideo(payloads.Payload(Xm.ExtensionKind.ConsumptionExperienceTrait)))
            batch.FollowUp.Add(uri);

        // Always Applied, never Negative: the "no video" verdict IS a write (VideoAssociation.None), and its own
        // 30-minute negative window is what stops the re-ask. A pipeline memo on top of that would be permanent —
        // which is precisely how a track that GAINS a video used to keep a stale "no" for the whole session.
        return TraitOutcome.Applied;
    }

    // ── the fold ─────────────────────────────────────────────────────────────────────────────────────────────────────
    // THE kind-99 fold, shared by every fetcher of the association (this pipeline and SpotifyTrackExpansionService's
    // drawer read). One fold is what makes the row indicator and the expand drawer structurally unable to disagree:
    // whichever path fetched the payload, the same projection lands in the same plane — so expanding a row that showed
    // no video HEALS the row the moment the drawer's own fetch comes back.

    /// <summary>Fold one answer into the plane through the batch's bulk window. Returns whether anything was written.</summary>
    public static bool Fold(TraitBatch batch, string uri, CachedExtension res)
    {
        VideoAssociation? projected;
        try { projected = Project(batch.Store, uri, res, batch.Now); }
        catch (InvalidProtocolBufferException) { return false; }   // skip one malformed entity, keep the batch
        if (projected is null) return false;
        batch.Write(s => s.UpsertVideoAssociation(projected));
        return true;
    }

    /// <summary>The same fold straight at a store, for callers outside a trait pass (the expand drawer). Returns
    /// whether anything was written.</summary>
    public static bool Fold(IStore store, string uri, CachedExtension res, DateTimeOffset now)
    {
        VideoAssociation? projected;
        try { projected = Project(store, uri, res, now); }
        catch (InvalidProtocolBufferException) { return false; }
        if (projected is null) return false;
        store.UpsertVideoAssociation(projected);
        return true;
    }

    /// <summary>One answer → the record to store, or null for "leave the plane untouched".</summary>
    public static VideoAssociation? Project(IStore store, string uri, CachedExtension res, DateTimeOffset now)
    {
        if (res.Missing || res.Payload is null || res.Payload.IsEmpty)
        {
            // D9. A Missing may never turn a positive into a negative.
            if (store.GetVideoAssociation(uri) is { HasVideo: true } positive)
                // Its OWN record: keep the answer, refresh the freshness. A BRIDGED one (the plane answered under this
                // track's CanonicalUri) belongs to another uri — writing anything under the alias would shadow the
                // bridge with a negative, so the plane is left exactly as it is.
                return string.Equals(positive.Uri, uri, StringComparison.Ordinal) ? positive with { FetchedAt = now } : null;
            return VideoAssociation.None(uri, res.Etag, now, res.OfflineTtlSeconds);
        }

        var (counterpart, files) = ParseAssoc(res.Payload);
        bool has = files.Count > 0 || !string.IsNullOrEmpty(counterpart);
        return new VideoAssociation(uri, has, counterpart, files, res.Etag, now, res.OfflineTtlSeconds);
    }

    static (string? Counterpart, IReadOnlyList<VideoFileRef> Files) ParseAssoc(ByteString payload)
    {
        var va = AssocParser.ParseFrom(payload);
        if (va.Association is not { } assoc) return (null, VideoAssociation.NoFiles);
        var files = new List<VideoFileRef>(assoc.Files?.File.Count ?? 0);
        if (assoc.Files is { } group)
            foreach (var f in group.File)
            {
                if (f.FileId.Length == 0) continue;
                files.Add(new VideoFileRef(Convert.ToHexStringLower(f.FileId.Span), f.Variant, f.Width, f.Height));
            }
        return (assoc.HasAssociatedUri ? assoc.AssociatedUri : null, files);
    }

    // A kind-99 miss that kind 182 contradicts is the alias/relinked-id case (kind 99 is keyed by the CANONICAL id and
    // 404s on an alias; 182 is canonical-computed, so it still reports the video).
    static bool NeedsRecovery(IStore store, string uri) => store.GetVideoAssociation(uri) is null or { HasVideo: false };

    // ── canonical-id recovery ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Canonical-id recovery for the aliases <see cref="Project"/> flagged. Once per alias per SESSION: a
    /// second page listing the same relinked track re-uses the record the first pass stored, so the two extra reads
    /// are paid at most once for a uri no matter how many surfaces show it.</summary>
    public async ValueTask CompleteBatchAsync(TraitBatch batch, CancellationToken ct)
    {
        if (batch.FollowUp.Count == 0) return;

        List<string>? aliases = null;
        foreach (var uri in batch.FollowUp)
        {
            if (_attempted.Count >= AttemptCap) break;
            if (_attempted.TryAdd(uri, 0)) (aliases ??= new List<string>(batch.FollowUp.Count)).Add(uri);
        }
        if (aliases is null) return;

        await RecoverCanonicalAsync(batch, aliases, ct).ConfigureAwait(false);
    }

    /// <summary>For each alias: its canonical uri (the resident row's <c>CanonicalUri</c> — <c>ProjectTrack</c> stamps
    /// it from the same TrackV4 field — else a TrackV4 read) plus PLAYBACK_TRAIT (212) for the associated video's gid,
    /// then ONE kind-99 read over the canonical ids. Everything lands under the REQUESTED (alias) uri: the store
    /// indexes <c>a.Uri</c> and every consumer looks up the uri it rendered/played.</summary>
    async Task RecoverCanonicalAsync(TraitBatch batch, List<string> aliases, CancellationToken ct)
    {
        var pairs = new List<(string Alias, string Canonical, string? GidHex)>(aliases.Count);
        List<(string Uri, Xm.ExtensionKind Kind)>? reqs = null;
        List<string>? unresolved = null;
        int lines = 0, noCanonical = 0;

        foreach (var alias in aliases)
        {
            // The free tier: the row already carries the canonical the catalogue projection decoded.
            if (batch.Store.GetTrack(alias)?.CanonicalUri is { Length: > 0 } known
                && !string.Equals(known, alias, StringComparison.Ordinal))
            {
                pairs.Add((alias, known, null));
                continue;
            }
            (reqs ??= new List<(string, Xm.ExtensionKind)>(aliases.Count * 2)).Add((alias, Xm.ExtensionKind.TrackV4));
            reqs.Add((alias, Xm.ExtensionKind.PlaybackTrait));   // field 2 → the associated video's 16-byte gid
            (unresolved ??= new List<string>(aliases.Count)).Add(alias);
        }

        if (reqs is not null)
        {
            IReadOnlyDictionary<(string Uri, Xm.ExtensionKind Kind), CachedExtension> canon;
            try { canon = await _reader.ReadRawAsync(reqs, batch.Surface, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                batch.Log.Info("VIDEO_ASSOCIATIONS canonical: " + ex.Message);
                return;
            }
            foreach (var alias in unresolved!)
            {
                // The ONE canonical decoder (ExtendedMetadataSource) — it also answers null for a canonical that names
                // the alias itself, which is the same dead end as no canonical at all.
                string? canonical = canon.TryGetValue((alias, Xm.ExtensionKind.TrackV4), out var tv)
                    ? ExtendedMetadataSource.CanonicalUriOf(tv.Payload, alias) : null;
                if (canonical is null)
                {
                    noCanonical++;
                    LogRecover(batch, ref lines, alias, null, null, "no-canonical-uri");
                    continue;
                }
                string? gid = canon.TryGetValue((alias, Xm.ExtensionKind.PlaybackTrait), out var pb)
                    ? AssociatedVideoGid(pb.Payload) : null;
                pairs.Add((alias, canonical, gid));
            }
        }

        if (pairs.Count == 0)
        {
            // The H1 dead end the old code hit SILENTLY: kind 182 said "there is a video" but nothing offered a
            // canonical uri to ask kind 99 about, so the alias keeps its negative and its row stays dark.
            LogRecoverSummary(batch, aliases.Count, 0, noCanonical, 0, 0, 0);
            return;
        }

        var second = new List<(string Uri, Xm.ExtensionKind Kind)>(pairs.Count);
        var asked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, canonical, _) in pairs)
            if (asked.Add(canonical)) second.Add((canonical, Xm.ExtensionKind.VideoAssociations));

        IReadOnlyDictionary<(string Uri, Xm.ExtensionKind Kind), CachedExtension> recovered;
        try { recovered = await _reader.ReadRawAsync(second, batch.Surface, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            batch.Log.Info("VIDEO_ASSOCIATIONS canonical: " + ex.Message);
            return;
        }

        int ok = 0, canonicalNoRow = 0, canonicalNoVideo = 0;
        // The page's OWN batch, not a fresh one: the pipeline runs CompleteBatchAsync inside the still-open bulk window,
        // so a recovered row rides the same single change signal the page's other writes do.
        foreach (var (alias, canonical, gid) in pairs)
        {
            if (!recovered.TryGetValue((canonical, Xm.ExtensionKind.VideoAssociations), out var res))
            {
                canonicalNoRow++;
                LogRecover(batch, ref lines, alias, canonical, gid, "canonical-no-kind99-row");
                continue;
            }
            if (!ApplyRecovered(batch, alias, res, gid))
            {
                canonicalNoVideo++;
                LogRecover(batch, ref lines, alias, canonical, gid, "canonical-no-video");
                continue;
            }
            // Stamp the derived canonical onto the resident alias row so the store's miss-bridge and later passes do
            // not re-read TrackV4 for it. Skipped when the row already agrees — a write here is a change signal.
            if (batch.Store.GetTrack(alias) is { } row && !string.Equals(row.CanonicalUri, canonical, StringComparison.Ordinal))
                batch.Write(s => s.UpsertTrack(row with { CanonicalUri = canonical }));
            ok++;
            LogRecover(batch, ref lines, alias, canonical, gid, "recovered");
        }
        // NO MarkStale(alias, 99) here — see the header note (D9).
        LogRecoverSummary(batch, aliases.Count, pairs.Count, noCanonical, ok, canonicalNoRow, canonicalNoVideo);
    }

    /// <summary>Fold a CANONICAL entity's association under the ALIAS uri. Returns whether anything was recovered (only
    /// a real hit counts — a canonical that also has no video leaves the alias's own verdict alone).</summary>
    static bool ApplyRecovered(TraitBatch batch, string alias, CachedExtension res, string? gidHex)
    {
        VideoAssociation? projected;
        try { projected = Project(batch.Store, alias, res, batch.Now); }
        catch (InvalidProtocolBufferException) { return false; }
        if (projected is not { HasVideo: true }) return false;
        // The etag belongs to the CANONICAL entity — never persist it against the alias, or the next pass would send it
        // as the alias's conditional and 304 us onto the wrong entity's freshness.
        var write = projected with { Etag = null, VideoGidHex = gidHex ?? projected.VideoGidHex };
        batch.Write(s => s.UpsertVideoAssociation(write));
        return true;
    }

    // ── hand decoders (no generated message ships for either kind, and one bit/one field is all that is wanted) ───────

    /// <summary>Kind 182 (CONSUMPTION_EXPERIENCE_TRAIT): field 4 is a LENGTH-DELIMITED blob of small experience ids;
    /// the byte <c>0x02</c> is the music-video experience (live-probed — video tracks carry 01 02 04, plain audio
    /// 01 04).</summary>
    internal static bool CeHasVideo(ByteString? payload)
    {
        if (payload is null || payload.IsEmpty) return false;
        try
        {
            var input = payload.CreateCodedInput();
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                if (WireFormat.GetTagFieldNumber(tag) == 4 && WireFormat.GetTagWireType(tag) == WireFormat.WireType.LengthDelimited)
                {
                    var b = input.ReadBytes();
                    for (int i = 0; i < b.Length; i++) if (b.Span[i] == 0x02) return true;
                }
                else input.SkipLastField();
            }
        }
        catch (InvalidProtocolBufferException) { }
        return false;
    }

    /// <summary>Kind 212 (PLAYBACK_TRAIT): field 2 carries the associated video's 16-byte gid — either directly or one
    /// nesting level in. That gid IS the video manifest_id / Connect's <c>associated_video_id</c>, so decoding it here
    /// saves the resolve round trip later.</summary>
    internal static string? AssociatedVideoGid(ByteString? payload)
    {
        if (payload is null || payload.IsEmpty) return null;
        try
        {
            var input = payload.CreateCodedInput();
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                if (WireFormat.GetTagFieldNumber(tag) == 2 && WireFormat.GetTagWireType(tag) == WireFormat.WireType.LengthDelimited)
                {
                    var b = input.ReadBytes();
                    if (b.Length == 16) return Convert.ToHexStringLower(b.Span);
                    if (NestedGid(b, depth: 3) is { } nested) return nested;
                }
                else input.SkipLastField();
            }
        }
        catch (InvalidProtocolBufferException) { }
        return null;
    }

    static string? NestedGid(ByteString msg, int depth)
    {
        try
        {
            var input = msg.CreateCodedInput();
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                if (WireFormat.GetTagWireType(tag) == WireFormat.WireType.LengthDelimited)
                {
                    var b = input.ReadBytes();
                    if (b.Length == 16) return Convert.ToHexStringLower(b.Span);
                    if (depth > 1 && b.Length > 1 && NestedGid(b, depth - 1) is { } nested) return nested;
                }
                else input.SkipLastField();
            }
        }
        catch (InvalidProtocolBufferException) { }
        return null;
    }

    // ── the recovery ledger ──────────────────────────────────────────────────────────────────────────────────────────
    // Per-alias verdicts, bounded: a page can carry hundreds of aliases and the detail is only useful for the first
    // handful — the summary below carries the totals. (The per-SLICE detect tally the service logged is gone: the
    // pipeline owns the per-kind/per-EntityKind `traits.batch` line now.)

    static void LogRecover(TraitBatch batch, ref int lines, string alias, string? canonical, string? gid, string verdict)
    {
        if (lines >= RecoverLineCap) return;
        lines++;
        batch.Log.Event(WaveeLogLevel.Debug, "video.assoc.recover", "alias → canonical kind-99 recovery",
            fields:
            [
                WaveeLogField.Of("alias", alias), WaveeLogField.Of("canonical", canonical ?? "-"),
                WaveeLogField.Of("gid", gid ?? "-"), WaveeLogField.Of("verdict", verdict),
            ]);
    }

    // THE H1 line. `suspects` is how many kind-99 misses kind 182 contradicted (i.e. relink suspects); everything after
    // it is where each suspect ended up. `recovered > 0` means the relink path is working; a large `noCanonicalUri` or
    // `canonicalNoKind99Row` means it is not, and the playlist row will stay dark while search shows a video.
    static void LogRecoverSummary(TraitBatch batch, int suspects, int pairs, int noCanonicalUri, int recovered,
                                  int canonicalNoRow, int canonicalNoVideo)
        => batch.Log.Event(WaveeLogLevel.Info, "video.assoc.recover.done", "relink recovery pass finished",
            fields:
            [
                WaveeLogField.Of("suspects", suspects), WaveeLogField.Of("pairs", pairs),
                WaveeLogField.Of("noCanonicalUri", noCanonicalUri), WaveeLogField.Of("recovered", recovered),
                WaveeLogField.Of("canonicalNoKind99Row", canonicalNoRow),
                WaveeLogField.Of("canonicalNoVideo", canonicalNoVideo),
            ]);
}
