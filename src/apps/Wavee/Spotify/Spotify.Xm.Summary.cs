// ── Spotify/Spotify.Xm.Summary.cs — what ONE extended-metadata answer said, per kind, per asked entity ───────────────
//
// Evidence (2026-09-25, owner-approved RCA of the Oscar Dunbar artist page): the planner's `fetch.miss` line said a row
// was "missed" but not WHY — did the 200 body omit the entity, name it with an empty payload, name it with a non-2xx
// entity status, or name it with a payload that carries nothing (kind 185 with zero plays, kind 99 with no association)?
// Those are four different answers and they call for different fixes (a known negative, a retry, a seal), so this file
// reads a `BatchedExtensionResponse` against the uris and kinds that ASKED and says, per kind:
//
//     asked · returned (2xx, a payload) · empty (2xx, no/zero-length payload) · failed (non-2xx entity status)
//     · omitted (the kind's array never names the uri) · pos / zero (the payload's own verdict, for the trait kinds
//       whose "nothing" is a value: 185 plays > 0, 99 an association or renditions, 182 the music-video experience
//       0x02, 6 at least one descriptor)
//
// PURE over bytes: no table, no scope, no clock, no log — `Spotify.Api.MetadataFor` writes the always-on `fetch.xm` line
// from `Tally`, and the `wavee://diag?cmd=xm` probe writes every `Rows` entry (with the payload) to a TSV. Runs on an api
// worker thread, off the frame; it allocates (one pass over a response that has already been allocated).

using System.Globalization;
using System.Text;

namespace Wavee;

/// <summary>The per-kind account of one extended-metadata answer against what asked for it (see the file header).</summary>
public sealed class XmAnswerSummary
{
    /// <summary>What one asked (kind, uri) got back.</summary>
    public enum Verdict : byte
    {
        /// <summary>The kind's array never named the uri — the entity was left out of a 200 body.</summary>
        Omitted,
        /// <summary>A 2xx entity header with no extension_data, or with a zero-length payload.</summary>
        Empty,
        /// <summary>A non-2xx entity status (404/403/410/451 are terminal; 5xx/429 are this request being unlucky).</summary>
        Failed,
        /// <summary>A 2xx payload whose trait verdict is POSITIVE (or a kind this reader has no verdict for).</summary>
        Positive,
        /// <summary>A 2xx payload that carries the trait's "nothing": 185 plays ≤ 0, 99 no association and no
        /// renditions, 182 no music-video experience, 6 no descriptor.</summary>
        Zero,
    }

    /// <summary>One (kind, uri) outcome. <see cref="Payload"/> is kept only when the caller asked for rows.</summary>
    public readonly record struct Row(int Kind, string Uri, Verdict Verdict, int Status, int PayloadBytes, byte[]? Payload);

    /// <summary>Per-kind counts over the ASKED uris (duplicates in the ask are folded: a uri is asked once per kind).</summary>
    public readonly record struct KindTally(int Kind, int Asked, int Returned, int Empty, int Failed, int Omitted, int Positive, int Zero);

    public KindTally[] Tally { get; private init; } = [];
    public Row[] Rows { get; private init; } = [];

    /// <summary>Read <paramref name="response"/> against <paramref name="askedUris"/> × <paramref name="askedKinds"/>.
    /// A response entity for a uri or kind nobody asked is ignored (the server echoes relinks under the asked uri; an
    /// unasked one is not this request's business). <paramref name="keepPayloads"/> copies each payload into its row —
    /// the probe's TSV wants them, the always-on line does not.</summary>
    public static XmAnswerSummary Read(ReadOnlySpan<byte> response, ReadOnlySpan<string> askedUris, ReadOnlySpan<int> askedKinds,
                                       bool keepPayloads = false)
    {
        // (kind, uri) → the entity's outcome, from the body.
        var seen = new Dictionary<(int, string), (Verdict Verdict, int Status, int Bytes, byte[]? Payload)>();
        var r = new Spotify.Decode.ProtoReader(response);
        var pending = new List<(string Uri, Verdict Verdict, int Status, int Bytes, byte[]? Payload)>(8);
        while (r.Next())
        {
            if (r.Field != 2 || r.Wire != 2) { r.Skip(); continue; }
            var array = r.Message();
            int kind = 0;
            pending.Clear();
            while (array.Next())
            {
                if (array.Field == 2 && array.Wire == 0) { kind = array.Int32(); continue; }
                if (array.Field != 3 || array.Wire != 2) { array.Skip(); continue; }
                var entity = array.Message();
                ReadOnlySpan<byte> uri = default, payload = default;
                int status = 200;
                bool answered = false;
                while (entity.Next())
                {
                    if (entity.Field == 1 && entity.Wire == 2) status = (int)entity.Message().Varint(1, 200);
                    else if (entity.Field == 2 && entity.Wire == 2) uri = entity.Bytes();
                    else if (entity.Field == 3 && entity.Wire == 2) { payload = entity.Message().Bytes(2); answered = true; }
                    else entity.Skip();
                }
                if (uri.IsEmpty) continue;
                string text = Encoding.UTF8.GetString(uri);
                Verdict verdict = status is < 200 or >= 300 ? Verdict.Failed
                                : !answered || payload.IsEmpty ? Verdict.Empty
                                : Verdict.Positive;   // refined per kind once the array's kind is known (it may come last)
                pending.Add((text, verdict, status, payload.Length, keepPayloads || verdict == Verdict.Positive ? payload.ToArray() : null));
            }
            foreach (var p in pending)
            {
                Verdict v = p.Verdict == Verdict.Positive && p.Payload is { } bytes && IsZero(kind, bytes) ? Verdict.Zero : p.Verdict;
                seen[(kind, p.Uri)] = (v, p.Status, p.Bytes, keepPayloads ? p.Payload : null);
            }
        }

        var tally = new KindTally[askedKinds.Length];
        var rows = new List<Row>(askedUris.Length * Math.Max(1, askedKinds.Length));
        var unique = new HashSet<string>(StringComparer.Ordinal);
        for (int k = 0; k < askedKinds.Length; k++)
        {
            int kind = askedKinds[k];
            int asked = 0, returned = 0, empty = 0, failed = 0, omitted = 0, pos = 0, zero = 0;
            unique.Clear();
            foreach (string uri in askedUris)
            {
                if (string.IsNullOrEmpty(uri) || !unique.Add(uri)) continue;
                asked++;
                if (!seen.TryGetValue((kind, uri), out var hit))
                {
                    omitted++;
                    rows.Add(new Row(kind, uri, Verdict.Omitted, 0, 0, null));
                    continue;
                }
                switch (hit.Verdict)
                {
                    case Verdict.Failed: failed++; break;
                    case Verdict.Empty: empty++; break;
                    case Verdict.Zero: returned++; zero++; break;
                    default: returned++; pos++; break;
                }
                rows.Add(new Row(kind, uri, hit.Verdict, hit.Status, hit.Bytes, hit.Payload));
            }
            tally[k] = new KindTally(kind, asked, returned, empty, failed, omitted, pos, zero);
        }
        return new XmAnswerSummary { Tally = tally, Rows = rows.ToArray() };
    }

    /// <summary>Does a 2xx payload of <paramref name="kind"/> carry the trait's "nothing"? The same field reads the
    /// decoders use (Spotify.Decode.cs: PlayCount field 3; VideoAssociations field 1 / renditions; ConsumptionExperience
    /// field 4 byte 0x02; Descriptors field 1). A kind without a trait verdict is never Zero.</summary>
    public static bool IsZero(int kind, ReadOnlySpan<byte> payload)
    {
        switch (kind)
        {
            case FetchRoutes.PlayCount:
                return new Spotify.Decode.ProtoReader(payload).Varint(3) <= 0;
            case FetchRoutes.VideoAssociations:
            {
                var r = new Spotify.Decode.ProtoReader(payload);
                while (r.Next())
                {
                    if (r.Field != 1 || r.Wire != 2) { r.Skip(); continue; }
                    var a = r.Message();
                    while (a.Next())
                    {
                        if (a.Wire != 2) { a.Skip(); continue; }
                        ReadOnlySpan<byte> value = a.Bytes();
                        // associated_uri (1) or a renditions group (2): the decoder's own "a video exists" (G-057).
                        if ((a.Field == 1 || a.Field == 2) && !value.IsEmpty) return false;
                    }
                }
                return true;
            }
            case FetchRoutes.ConsumptionExperience:
            {
                var r = new Spotify.Decode.ProtoReader(payload);
                while (r.Next())
                {
                    if (r.Field != 4 || r.Wire != 2) { r.Skip(); continue; }
                    var ids = r.Bytes();
                    for (int i = 0; i < ids.Length; i++) if (ids[i] == 0x02) return false;
                }
                return true;
            }
            case FetchRoutes.TrackDescriptor:
            {
                var r = new Spotify.Decode.ProtoReader(payload);
                while (r.Next())
                {
                    if (r.Field == 1) return false;
                    r.Skip();
                }
                return true;
            }
            default:
                return false;
        }
    }

    /// <summary>One kind's counts as the <c>fetch.xm</c> line's field value: <c>ask=8 ret=7 empty=0 fail=0 omit=1 pos=7
    /// zero=0</c>.</summary>
    public static string Format(in KindTally t)
        => string.Create(CultureInfo.InvariantCulture,
            $"ask={t.Asked} ret={t.Returned} empty={t.Empty} fail={t.Failed} omit={t.Omitted} pos={t.Positive} zero={t.Zero}");

    /// <summary>The first <paramref name="max"/> (kind, uri) pairs with <paramref name="verdict"/>, as
    /// <c>185:spotify:track:…;99:spotify:track:…</c> (<c>-</c> when none) — the line names the entities, not just a count.</summary>
    public string Named(Verdict verdict, int max = 8)
    {
        var sb = new StringBuilder();
        int n = 0;
        foreach (var row in Rows)
        {
            if (row.Verdict != verdict) continue;
            if (n++ == max) { sb.Append(";…"); break; }
            if (sb.Length > 0) sb.Append(';');
            sb.Append(row.Kind.ToString(CultureInfo.InvariantCulture)).Append(':').Append(row.Uri);
        }
        return sb.Length == 0 ? "-" : sb.ToString();
    }

    /// <summary>The probe TSV (<c>wavee://diag?cmd=xm</c>): a header and one row per asked (kind, uri) with the payload
    /// as hex (empty when not kept).</summary>
    public string ToTsv()
    {
        var sb = new StringBuilder(256 + Rows.Length * 96);
        sb.Append("kind\turi\tverdict\tstatus\tbytes\tpayloadHex\n");
        foreach (var row in Rows)
        {
            sb.Append(row.Kind.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(row.Uri).Append('\t')
              .Append(row.Verdict.ToString()).Append('\t').Append(row.Status.ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(row.PayloadBytes.ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(row.Payload is { } p ? Convert.ToHexString(p) : "").Append('\n');
        }
        return sb.ToString();
    }
}
