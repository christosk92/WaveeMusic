using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Metadata;
using Wavee.Backend.Spotify;
using Wavee.Core;
using M = Wavee.Protocol.Metadata;
using Xm = Wavee.Protocol.ExtendedMetadata;
// EntityKind: the ONE uri vocabulary (Wavee.Core), not the transport's thin Backend.Metadata projection of it.
using EntityKind = Wavee.Core.EntityKind;

namespace Wavee.SpotifyLive;

/// <summary>
/// LIVE probe: does extended-metadata kind <b>182</b> (<c>CONSUMPTION_EXPERIENCE_TRAIT</c>) / kind <b>212</b>
/// (<c>PLAYBACK_TRAIT</c>) actually encode "has music video", or is kind <b>99</b> (<c>VIDEO_ASSOCIATIONS</c>) still
/// the only reliable gate?
/// <para>
/// Fetches all three kinds for a small set of known SAZ hit/miss tracks (plus any CLI URIs), dumps status + a protobuf
/// wire walk of each payload, and prints a concordance table vs kind-99 ground truth. Uses the stored credential via
/// <see cref="SpotifyLiveSpclient.ConnectAsync"/> — same path as <c>--spotify-metadata</c> /
/// <c>--spotify-video-manifest</c>.
/// </para>
/// Usage: <c>--spotify-video-traits [spotify:track:... ...]</c>
/// </summary>
public static class SpotifyVideoTraitProbe
{
    // SAZ-grounded fixtures (agent-xm-video.md / pathfinder report). Labels are expectations for kind 99 only.
    static readonly (string Uri, string Label, bool ExpectVaHit)[] DefaultTracks =
    [
        ("spotify:track:02lTDOxHeXTHsdwXoz6lpC", "SAZ-VA-hit", true),
        ("spotify:track:08cXy6KUizaAelYXtcew3w", "SAZ-VA-hit", true),
        ("spotify:track:2LwsunYgfRoqyIsNtgOCQx", "PF-totalCount=1", true),
        ("spotify:track:0VjIjW4GlUZAMYd2vXMi3b", "BlindingLights", true),
        ("spotify:track:1FOIZ4BVCqXu12Crq9yAai", "Paracetamollen-log", true),
        ("spotify:track:01Mpj13vURSO3cCLprPt5T", "SAZ-VA-404", false),
        ("spotify:track:01UYpHuzHi4eB9PAbDoPY2", "SAZ-VA-404", false),
        ("spotify:track:4uLU6hMCjMI75M1A2tKUQC", "NeverGonnaGiveYouUp", false),
    ];

    public static async Task<int> RunAsync(IReadOnlyList<string> extraUris, WaveeLogger log, CancellationToken ct, string language = "en")
    {
        try { return await ProbeAsync(extraUris, log, ct, language).ConfigureAwait(false); }
        catch (Exception ex)
        {
            log.Info("video-trait probe failed: " + ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
    }

    static async Task<int> ProbeAsync(IReadOnlyList<string> extraUris, WaveeLogger log, CancellationToken ct, string language)
    {
        var live = await SpotifyLiveSpclient.ConnectAsync(log, ct, language: language).ConfigureAwait(false);
        if (live is null) return 1;

        var source = new ExtendedMetadataSource(live.Pipeline, () => live.BaseUrl, () => live.Session);

        var tracks = new List<(string Uri, string Label, bool? ExpectVaHit)>();
        foreach (var t in DefaultTracks) tracks.Add((t.Uri, t.Label, t.ExpectVaHit));
        foreach (var u in extraUris)
        {
            string uri = NormalizeTrackUri(u);
            if (uri.Length == 0) continue;
            if (tracks.Any(t => string.Equals(t.Uri, uri, StringComparison.Ordinal))) continue;
            tracks.Add((uri, "cli", null));
        }

        var reqs = new List<(string Uri, Xm.ExtensionKind Kind, string? Etag)>(tracks.Count * 5);
        foreach (var (uri, _, _) in tracks)
        {
            reqs.Add((uri, Xm.ExtensionKind.VideoAssociations, null));            // 99 — ground truth
            reqs.Add((uri, Xm.ExtensionKind.OriginalVideo, null));               // 85 — claimed noise
            reqs.Add((uri, Xm.ExtensionKind.ConsumptionExperienceTrait, null)); // 182 — claimed cheap bool
            reqs.Add((uri, Xm.ExtensionKind.PlaybackTrait, null));              // 212 — claimed video gid + URI
            reqs.Add((uri, Xm.ExtensionKind.TrackV4, null));                    //  4 — typed canonical_uri(36)/alternative(13)
        }

        log.Info("Fetching kinds 99/85/182/212/TrackV4 for " + tracks.Count.ToString(CultureInfo.InvariantCulture) + " track(s) ...");
        var results = await source.GetExtensionsWithHeadersAsync(reqs, ct).ConfigureAwait(false);
        CheckKeyIntegrity(log, "primary", reqs, results);

        var rows = new List<Row>(tracks.Count);
        foreach (var (uri, label, expect) in tracks)
        {
            var va = Read(results, uri, Xm.ExtensionKind.VideoAssociations);
            var ov = Read(results, uri, Xm.ExtensionKind.OriginalVideo);
            var ce = Read(results, uri, Xm.ExtensionKind.ConsumptionExperienceTrait);
            var pb = Read(results, uri, Xm.ExtensionKind.PlaybackTrait);
            var tv = Read(results, uri, Xm.ExtensionKind.TrackV4);

            var vaParsed = ParseVa(va);
            var ceSig = CeSignal(ce);
            var pbSig = PbSignal(pb);
            var tvSig = TrackV4Signal(tv);

            log.Info("");
            log.Info("══ " + uri + "  (" + label + (expect is { } e ? ", expect99=" + (e ? "hit" : "miss") : "") + ")");
            DumpKind(log, "99 VIDEO_ASSOCIATIONS", va, vaParsed.Summary);
            DumpKind(log, "85 ORIGINAL_VIDEO", ov, OvSummary(ov));
            DumpKind(log, "182 CONSUMPTION_EXPERIENCE", ce, ceSig.Summary);
            DumpKind(log, "212 PLAYBACK_TRAIT", pb, pbSig.Summary);
            DumpKind(log, "4 TRACK_V4", tv, tvSig.Summary, walk: false);   // full Track walks to hundreds of fields; typed dump above

            // Canonical preference: typed TrackV4.canonical_uri first (production-cheap), 212 field 1 as the fallback.
            string? canonical = null, canonSource = null;
            if (tvSig.CanonicalUri is { Length: > 0 } tc && !string.Equals(tc, uri, StringComparison.Ordinal)) { canonical = tc; canonSource = "trackv4"; }
            else if (pbSig.CanonicalUri is { Length: > 0 } pc && !string.Equals(pc, uri, StringComparison.Ordinal)) { canonical = pc; canonSource = "pb212-field1"; }
            else if (tvSig.CanonicalUri is { Length: > 0 }) { canonical = tvSig.CanonicalUri; canonSource = "trackv4(self)"; }
            else if (pbSig.CanonicalUri is { Length: > 0 }) { canonical = pbSig.CanonicalUri; canonSource = "pb212-field1(self)"; }

            log.Info("  canonical: trackv4=" + (tvSig.CanonicalUri ?? "-") + "  pb212.f1=" + (pbSig.CanonicalUri ?? "-")
                     + "  chosen=" + (canonical ?? "-") + " (" + (canonSource ?? "none") + ")"
                     + (string.Equals(canonical, uri, StringComparison.Ordinal) ? " [== self]" : ""));

            rows.Add(new Row(uri, label, expect, vaParsed.HasVideo, ceSig.HasVideoHint, pbSig.HasVideoHint,
                va.Status, ce.Status, pb.Status, canonical, canonSource, tvSig.CanonicalUri, pbSig.CanonicalUri,
                pbSig.VideoTrackUri, pbSig.VideoGidHex, tvSig.AlternativeUris, RecoveredVa: null, RecoveredCounterpart: null, RecoveredStatus: 0));
        }

        // ── Recovery leg: alias/relinked ids 404 on kind 99; the canonical id (212 f1 / TrackV4 f36) does not. ─────────
        var needRecovery = new List<int>();
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.VaStatus == 404 && r.Canonical is { Length: > 0 } c && !string.Equals(c, r.Uri, StringComparison.Ordinal))
                needRecovery.Add(i);
        }
        log.Info("");
        log.Info("══ CANONICAL RECOVERY (VA 404 ∧ canonical≠self) — " + needRecovery.Count.ToString(CultureInfo.InvariantCulture) + " candidate(s) ══");
        if (needRecovery.Count > 0)
        {
            var reqs2 = new List<(string Uri, Xm.ExtensionKind Kind, string? Etag)>(needRecovery.Count);
            foreach (int i in needRecovery) reqs2.Add((rows[i].Canonical!, Xm.ExtensionKind.VideoAssociations, null));
            var results2 = await source.GetExtensionsWithHeadersAsync(reqs2, ct).ConfigureAwait(false);
            CheckKeyIntegrity(log, "recovery", reqs2, results2);
            foreach (int i in needRecovery)
            {
                var r = rows[i];
                var va2 = Read(results2, r.Canonical!, Xm.ExtensionKind.VideoAssociations);
                var parsed2 = ParseVa(va2);
                log.Info("  " + r.Uri + " → " + r.Canonical + " (" + r.CanonicalSource + ")");
                DumpKind(log, "  99 VIDEO_ASSOCIATIONS (canonical)", va2, parsed2.Summary);
                rows[i] = r with { RecoveredVa = parsed2.HasVideo, RecoveredCounterpart = parsed2.Counterpart, RecoveredStatus = va2.Status };
            }
        }
        else log.Info("  (no alias candidates — every VA 404 row had canonical == self or no canonical at all)");

        log.Info("");
        log.Info("══ CONCORDANCE (kind 99 = ground truth) ══");
        log.Info(string.Format(CultureInfo.InvariantCulture,
            "{0,-24} {1,-8} {2,-8} {3,-8} {4,-10} {5,-10} {6}",
            "label", "expect", "va99", "ce182", "pb212", "ce==va", "pb==va"));
        int ceAgree = 0, pbAgree = 0, n = 0;
        foreach (var r in rows)
        {
            n++;
            bool ceOk = r.CeHint == r.VaHas;
            bool pbOk = r.PbHint == r.VaHas;
            if (ceOk) ceAgree++;
            if (pbOk) pbAgree++;
            log.Info(string.Format(CultureInfo.InvariantCulture,
                "{0,-24} {1,-8} {2,-8} {3,-8} {4,-10} {5,-10} {6}",
                Trunc(r.Label, 24),
                r.Expect is { } e ? (e ? "hit" : "miss") : "?",
                Flag(r.VaHas) + "/" + r.VaStatus.ToString(CultureInfo.InvariantCulture),
                Flag(r.CeHint) + "/" + r.CeStatus.ToString(CultureInfo.InvariantCulture),
                Flag(r.PbHint) + "/" + r.PbStatus.ToString(CultureInfo.InvariantCulture),
                ceOk ? "YES" : "NO",
                pbOk ? "YES" : "NO"));
        }
        log.Info(string.Format(CultureInfo.InvariantCulture,
            "agreement vs kind99:  CE(182)={0}/{1}  PB(212)={2}/{3}",
            ceAgree, n, pbAgree, n));
        log.Info("Verdict rules: CE ⇔ status200 ∧ field4 LENGTH bytes contain 0x02; PB ⇔ status200 ∧ field2 present (nested message). Self spotify:track: URIs alone do NOT count.");
        log.Info("If agreement is poor, the final-analysis claim about 182/212 as has-video signals is NOT confirmed.");

        log.Info("");
        log.Info("══ CANONICAL / RECOVERY TABLE ══");
        log.Info(string.Format(CultureInfo.InvariantCulture,
            "{0,-24} {1,-9} {2,-24} {3,-19} {4,-24} {5}",
            "label", "va99", "canonical", "source", "recovered-va", "pb212-counterpart/gid"));
        int tvCanon = 0, pbCanon = 0, tvAlias = 0, pbAlias = 0, recovered = 0, bothDisagree = 0;
        foreach (var r in rows)
        {
            if (r.TrackV4Canonical is { Length: > 0 }) { tvCanon++; if (!string.Equals(r.TrackV4Canonical, r.Uri, StringComparison.Ordinal)) tvAlias++; }
            if (r.Pb212Canonical is { Length: > 0 }) { pbCanon++; if (!string.Equals(r.Pb212Canonical, r.Uri, StringComparison.Ordinal)) pbAlias++; }
            if (r.TrackV4Canonical is { Length: > 0 } && r.Pb212Canonical is { Length: > 0 }
                && !string.Equals(r.TrackV4Canonical, r.Pb212Canonical, StringComparison.Ordinal)) bothDisagree++;
            if (r.RecoveredVa == true) recovered++;
            log.Info(string.Format(CultureInfo.InvariantCulture,
                "{0,-24} {1,-9} {2,-24} {3,-19} {4,-24} {5}",
                Trunc(r.Label, 24),
                Flag(r.VaHas) + "/" + r.VaStatus.ToString(CultureInfo.InvariantCulture),
                Trunc(ShortId(r.Canonical), 24),
                r.CanonicalSource ?? "-",
                r.RecoveredVa is { } rv
                    ? (rv ? "HIT/" : "miss/") + r.RecoveredStatus.ToString(CultureInfo.InvariantCulture)
                      + (r.RecoveredCounterpart is { Length: > 0 } rc ? " " + ShortId(rc) : "")
                    : "-",
                (r.Pb212VideoUri is { Length: > 0 } pv ? ShortId(pv) : "-") + " / " + (r.Pb212VideoGidHex ?? "-")));
        }
        log.Info(string.Format(CultureInfo.InvariantCulture,
            "canonical populated:  TrackV4(f36)={0}/{1} (alias≠self {2})   PB212(f1)={3}/{4} (alias≠self {5})   trackv4≠pb212 on {6} row(s)   recovered VA hits={7}",
            tvCanon, n, tvAlias, pbCanon, n, pbAlias, bothDisagree, recovered));
        log.Info("MECHANISM VERDICT: " + (tvCanon > 0
            ? "trackv4 — TrackV4.canonical_uri (field 36) IS populated; production C2 can use typed TrackV4."
            : pbCanon > 0
                ? "pb212-field1 — TrackV4.canonical_uri is EMPTY on the wire; production C2 must hand-decode kind 212 field 1."
                : "NONE — neither TrackV4.canonical_uri nor 212 field 1 yielded a canonical URI; canonical recovery is not viable as designed."));
        return 0;
    }

    static ExtensionView Read(
        IReadOnlyDictionary<(string Uri, Xm.ExtensionKind Kind), ExtendedMetadataSource.ExtensionResult> results,
        string uri, Xm.ExtensionKind kind)
    {
        if (!results.TryGetValue((uri, kind), out var r))
            return new ExtensionView(0, null, 0, null);
        return new ExtensionView(r.Status, r.Etag, r.OfflineTtlSeconds, r.Payload);
    }

    // The response dictionary is keyed by the RESPONSE's EntityUri (ExtendedMetadataSource:158). A server that echoed the
    // relinked/canonical id instead of the requested one would make every TryGetValue silently miss — assert it here.
    static void CheckKeyIntegrity(WaveeLogger log, string phase,
        IReadOnlyList<(string Uri, Xm.ExtensionKind Kind, string? Etag)> requested,
        IReadOnlyDictionary<(string Uri, Xm.ExtensionKind Kind), ExtendedMetadataSource.ExtensionResult> results)
    {
        var asked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (uri, _, _) in requested) asked.Add(uri);
        int stray = 0, missing = 0;
        foreach (var key in results.Keys)
            if (!asked.Contains(key.Uri))
            {
                stray++;
                log.Info("  !! KEY MISMATCH (" + phase + "): response key " + key.Uri + " / " + key.Kind + " was never requested");
            }
        foreach (var (uri, kind, _) in requested)
            if (!results.ContainsKey((uri, kind))) missing++;
        log.Info("key-integrity(" + phase + "): requested=" + requested.Count.ToString(CultureInfo.InvariantCulture)
                 + " returned=" + results.Count.ToString(CultureInfo.InvariantCulture)
                 + " stray-keys=" + stray.ToString(CultureInfo.InvariantCulture)
                 + " absent-pairs=" + missing.ToString(CultureInfo.InvariantCulture)
                 + (stray == 0 ? "  (every response key echoed the requested uri)" : "  !! server echoed a DIFFERENT uri"));
    }

    static void DumpKind(WaveeLogger log, string title, ExtensionView v, string summary, bool walk = true)
    {
        int len = v.Payload?.Length ?? 0;
        log.Info("  " + title + ": status=" + v.Status.ToString(CultureInfo.InvariantCulture)
                 + " bytes=" + len.ToString(CultureInfo.InvariantCulture)
                 + " etag=" + (string.IsNullOrEmpty(v.Etag) ? "-" : Trunc(v.Etag!, 16))
                 + "  → " + summary);
        if (walk && v.Payload is { Length: > 0 } p)
        {
            foreach (var line in WalkWire(p.Span))
                log.Info("      " + line);
            string ascii = ExtractAsciiUris(p.Span);
            if (ascii.Length > 0) log.Info("      ascii-uris: " + ascii);
        }
    }

    static (bool HasVideo, string? Counterpart, string Summary) ParseVa(ExtensionView v)
    {
        if (v.Status == 404 || v.Status == 304 || v.Payload is null || v.Payload.IsEmpty)
            return (false, null, v.Status == 404 ? "MISS (404)" : v.Status == 304 ? "MISS/cached (304)" : "MISS (empty)");
        if (v.Status != 200) return (false, null, "odd status " + v.Status.ToString(CultureInfo.InvariantCulture));
        try
        {
            var va = Xm.VideoAssociations.Parser.WithDiscardUnknownFields(true).ParseFrom(v.Payload);
            string? counterpart = va.Association is { HasAssociatedUri: true } a ? a.AssociatedUri : null;
            int files = va.Association?.Files?.File.Count ?? 0;
            bool has = files > 0 || !string.IsNullOrEmpty(counterpart);
            return (has, counterpart, has
                ? "HIT counterpart=" + (counterpart ?? "-") + " files=" + files.ToString(CultureInfo.InvariantCulture)
                : "MISS (200 empty assoc)");
        }
        catch (Exception ex) { return (false, null, "parse-fail: " + ex.Message); }
    }

    static string OvSummary(ExtensionView v)
    {
        if (v.Status != 200) return "status=" + v.Status.ToString(CultureInfo.InvariantCulture);
        int len = v.Payload?.Length ?? 0;
        return len == 0 ? "200 EMPTY (noise)" : "200 bytes=" + len.ToString(CultureInfo.InvariantCulture);
    }

    // Hypothesis (revised from live dump): CE field 4 is LENGTH-DELIMITED packed small ints; byte 0x02 present ⇒ video.
    // Empirically: VA hits → f4 hex "010204"; many VA misses → "0104". Not a varint.
    static (bool HasVideoHint, string Summary) CeSignal(ExtensionView v)
    {
        if (v.Status != 200 || v.Payload is null || v.Payload.IsEmpty)
            return (false, "no payload (status=" + v.Status.ToString(CultureInfo.InvariantCulture) + ")");
        string hex = "-";
        bool hint = false;
        try
        {
            var input = new CodedInputStream(v.Payload.ToByteArray());
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                int field = WireFormat.GetTagFieldNumber(tag);
                var wt = WireFormat.GetTagWireType(tag);
                if (field == 4 && wt == WireFormat.WireType.LengthDelimited)
                {
                    var b = input.ReadBytes();
                    hex = Convert.ToHexStringLower(b.Span);
                    for (int i = 0; i < b.Length; i++) if (b.Span[i] == 0x02) { hint = true; break; }
                }
                else input.SkipLastField();
            }
        }
        catch (Exception ex) { return (false, "wire-fail: " + ex.Message); }
        return (hint, "field4=hex:" + hex + " contains02=" + hint + " hint=" + (hint ? "YES" : "NO"));
    }

    // Hypothesis (revised): PB field 2 present (nested ~100B message) correlates with an associated video track URI.
    // Do NOT treat any ascii spotify:track: as a hit — the payload always embeds the entity's own URI in field 1.
    static (bool HasVideoHint, string? CanonicalUri, string? VideoTrackUri, string? VideoGidHex, string Summary) PbSignal(ExtensionView v)
    {
        if (v.Status != 200 || v.Payload is null || v.Payload.IsEmpty)
            return (false, null, null, null, "no payload (status=" + v.Status.ToString(CultureInfo.InvariantCulture) + ")");
        string? canonical = null, videoUri = null, gidHex = null;
        var lens = new List<int>();
        try
        {
            var input = new CodedInputStream(v.Payload.ToByteArray());
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                int field = WireFormat.GetTagFieldNumber(tag);
                var wt = WireFormat.GetTagWireType(tag);
                if (field == 1 && wt == WireFormat.WireType.LengthDelimited)
                {
                    // Field 1 is a nested message that EMBEDS the entity's own URI as the server computes it — for an
                    // alias/relinked id the embedded uri is server-side-computed, which is what makes 212 a candidate
                    // recovery mechanism. The uri sits one level in, so scan rather than read it as a string.
                    var b = input.ReadBytes();
                    canonical ??= PrintableUri(b) ?? NestedGid(b, depth: 4).Uri;
                }
                else if (field == 2 && wt == WireFormat.WireType.LengthDelimited)
                {
                    var b = input.ReadBytes();
                    lens.Add(b.Length);
                    if (b.Length == 16) gidHex ??= Convert.ToHexStringLower(b.Span);
                    else
                    {
                        var (g, u) = NestedGid(b, depth: 4);
                        gidHex ??= g is null ? null : Convert.ToHexStringLower(g.Span);
                        videoUri ??= u;
                    }
                }
                else input.SkipLastField();
            }
        }
        catch (Exception ex) { return (false, null, null, null, "wire-fail: " + ex.Message); }
        bool hint = lens.Count > 0;   // presence of field 2 is the candidate signal
        return (hint, canonical, videoUri, gidHex,
            "f2-lens=[" + string.Join(",", lens) + "] f1-canonical=" + (canonical ?? "-")
            + " f2-uri=" + (videoUri ?? "-") + " f2-gid=" + (gidHex ?? "-")
            + " hint=" + (hint ? "YES" : "NO"));
    }

    // Scan a nested message for the counterpart's 16-byte gid and/or its spotify: URI (the 212 f2 payload nests one level).
    static (ByteString? Gid, string? Uri) NestedGid(ByteString msg, int depth)
    {
        ByteString? gid = null;
        string? uri = null;
        try
        {
            var input = new CodedInputStream(msg.ToByteArray());
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                if (WireFormat.GetTagWireType(tag) == WireFormat.WireType.LengthDelimited)
                {
                    var b = input.ReadBytes();
                    if (b.Length == 16) gid ??= b;
                    else if (PrintableUri(b) is { Length: > 0 } u) uri ??= u;
                    else if (depth > 1 && b.Length > 1)
                    {
                        var (g2, u2) = NestedGid(b, depth - 1);
                        gid ??= g2;
                        uri ??= u2;
                    }
                }
                else input.SkipLastField();
            }
        }
        catch { /* cold probe */ }
        return (gid, uri);
    }

    static string? PrintableUri(ByteString b)
    {
        if (b.Length is < 9 or > 128) return null;
        for (int i = 0; i < b.Length; i++) { byte c = b.Span[i]; if (c < 0x20 || c > 0x7e) return null; }
        string s = Encoding.ASCII.GetString(b.Span);
        return EntityUri.Parse(s).Provider == EntityProviders.Spotify ? s : null;
    }

    // TrackV4 parsed with the FULL Wavee.Protocol.Metadata.Track — LeanTrack drops canonical_uri(36)/alternative(13),
    // which are precisely the fields that decide whether production recovery can skip hand-decoding kind 212.
    static (string? CanonicalUri, string? AlternativeUris, string? OriginalVideoGids, string Summary) TrackV4Signal(ExtensionView v)
    {
        if (v.Status != 200 || v.Payload is null || v.Payload.IsEmpty)
            return (null, null, null, "no payload (status=" + v.Status.ToString(CultureInfo.InvariantCulture) + ")");
        try
        {
            var t = M.Track.Parser.ParseFrom(v.Payload);
            string self = t.Gid.Length == 16 ? "spotify:track:" + Base62.Encode(t.Gid.Span) : "-";
            string? canonical = t.HasCanonicalUri && t.CanonicalUri.Length > 0 ? NormalizeTrackUriOrRaw(t.CanonicalUri) : null;
            var alts = new List<string>();
            foreach (var a in t.Alternative)
                alts.Add(a.Gid.Length == 16 ? "spotify:track:" + Base62.Encode(a.Gid.Span) : "(gid-less)");
            var vids = new List<string>();
            foreach (var ov in t.OriginalVideo)
                if (ov.Gid.Length > 0) vids.Add(Convert.ToHexStringLower(ov.Gid.Span));
            string altStr = alts.Count == 0 ? "-" : string.Join(",", alts);
            string vidStr = vids.Count == 0 ? "-" : string.Join(",", vids);
            return (canonical, alts.Count == 0 ? null : altStr, vids.Count == 0 ? null : vidStr,
                "name=\"" + Trunc(t.Name ?? "", 40) + "\" self=" + self
                + " canonical_uri(36)=" + (canonical ?? "<empty>")
                + " alternative(13)=" + altStr
                + " original_video(38)=" + vidStr);
        }
        catch (Exception ex) { return (null, null, null, "parse-fail: " + ex.Message); }
    }

    static string NormalizeTrackUriOrRaw(string raw)
        => EntityUri.Parse(raw).Provider == EntityProviders.Spotify ? raw
         : raw.Length == 22 ? "spotify:track:" + raw
         : raw;

    static IEnumerable<string> WalkWire(ReadOnlySpan<byte> payload)
    {
        // Copy once — CodedInputStream needs a stream/array; probe is cold-path.
        var bytes = payload.ToArray();
        var input = new CodedInputStream(bytes);
        var lines = new List<string>();
        try
        {
            uint tag;
            int n = 0;
            while ((tag = input.ReadTag()) != 0 && n < 24)
            {
                n++;
                int field = WireFormat.GetTagFieldNumber(tag);
                var wt = WireFormat.GetTagWireType(tag);
                string detail = wt switch
                {
                    WireFormat.WireType.Varint => "varint=" + input.ReadUInt64().ToString(CultureInfo.InvariantCulture),
                    WireFormat.WireType.Fixed64 => "fixed64=" + input.ReadFixed64().ToString("x", CultureInfo.InvariantCulture),
                    WireFormat.WireType.Fixed32 => "fixed32=" + input.ReadFixed32().ToString("x", CultureInfo.InvariantCulture),
                    WireFormat.WireType.LengthDelimited => LenDetail(input.ReadBytes()),
                    WireFormat.WireType.StartGroup => "start-group (skipped)",
                    WireFormat.WireType.EndGroup => "end-group",
                    _ => "wt=" + wt,
                };
                if (wt is WireFormat.WireType.StartGroup) input.SkipLastField();
                lines.Add("f" + field.ToString(CultureInfo.InvariantCulture) + " " + detail);
            }
            if (input.ReadTag() != 0) lines.Add("… truncated walk");
        }
        catch (Exception ex) { lines.Add("walk-error: " + ex.Message); }
        return lines;
    }

    static string LenDetail(ByteString b)
    {
        if (b.Length == 0) return "len=0";
        if (b.Length == 16) return "len=16 gid=" + Convert.ToHexStringLower(b.Span);
        // Prefer ascii preview when printable.
        bool printable = true;
        for (int i = 0; i < b.Length && i < 64; i++)
        {
            byte c = b.Span[i];
            if (c < 0x20 || c > 0x7e) { printable = false; break; }
        }
        if (printable)
        {
            string s = Encoding.ASCII.GetString(b.Span);
            return "len=" + b.Length.ToString(CultureInfo.InvariantCulture) + " \"" + Trunc(s, 80) + "\"";
        }
        int show = Math.Min(12, b.Length);
        return "len=" + b.Length.ToString(CultureInfo.InvariantCulture)
               + " hex=" + Convert.ToHexStringLower(b.Span[..show]) + (b.Length > show ? "…" : "");
    }

    static string ExtractAsciiUris(ReadOnlySpan<byte> payload)
    {
        var sb = new StringBuilder();
        var found = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < payload.Length; i++)
        {
            if (payload[i] != (byte)'s') continue;
            // spotify:
            if (i + 8 > payload.Length) continue;
            if (payload[i + 1] != (byte)'p' || payload[i + 2] != (byte)'o' || payload[i + 3] != (byte)'t') continue;
            int end = i;
            while (end < payload.Length)
            {
                byte c = payload[end];
                if (c < 0x20 || c > 0x7e) break;
                end++;
            }
            string s = Encoding.ASCII.GetString(payload[i..end]);
            // video/canvas have no EntityKind (they are not addressable entities), so those two stay prefix tests.
            if ((EntityUri.KindOf(s) == EntityKind.Track || s.StartsWith("spotify:video:", StringComparison.Ordinal)
                 || s.StartsWith("spotify:canvas:", StringComparison.Ordinal))
                && found.Add(s))
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(s);
            }
        }
        return sb.ToString();
    }

    static string NormalizeTrackUri(string raw)
    {
        raw = raw.Trim();
        if (EntityUri.KindOf(raw) == EntityKind.Track) return raw;
        if (raw.Length == 22) return "spotify:track:" + raw;
        return "";
    }

    static string Flag(bool v) => v ? "YES" : "no";
    static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";
    static string ShortId(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return "-";
        return EntityUri.IdOf(uri!);
    }

    readonly record struct ExtensionView(int Status, string? Etag, long OfflineTtl, ByteString? Payload);

    readonly record struct Row(
        string Uri, string Label, bool? Expect, bool VaHas, bool CeHint, bool PbHint,
        int VaStatus, int CeStatus, int PbStatus,
        string? Canonical, string? CanonicalSource, string? TrackV4Canonical, string? Pb212Canonical,
        string? Pb212VideoUri, string? Pb212VideoGidHex, string? TrackV4Alternatives,
        bool? RecoveredVa, string? RecoveredCounterpart, int RecoveredStatus);
}
