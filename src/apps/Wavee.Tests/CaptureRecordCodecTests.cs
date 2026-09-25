// ── Wavee.Tests/CaptureRecordCodecTests.cs — round-trip of `CaptureRecordCodec` against §6.1's byte layout ─────────
//
// This is the test that keeps `decode.py`'s `struct.unpack` format string honest (plan §7): every field written by
// `CaptureRecordCodec.Encode` must come back identical through `TryDecode`, and a torn trailing record (a crash
// mid-write, per §3.1) must be reported as "stop here", never an exception or corrupted data.
//
// No production source is read as text here — this instantiates the pure codec directly, per CLAUDE.md.

using System;
using System.Text;
using Xunit;

namespace Wavee.Tests;

public class CaptureRecordCodecTests
{
    static CaptureRecord Sample(string a = "hello", string b = "world", string c = "reason", bool truncated = false)
        => new(
            Seq: 42, Qpc: 123456789, UnixMs: 1758000000000, Id: 7, CauseId: 3, RootId: 1,
            Kind: CaptureKind.HttpCall, Phase: CapturePhase.End, Priority: CapturePriority.Normal, Truncated: truncated,
            N0: 200, N1: -1, PayloadOffset: 512, PayloadLength: 64,
            A: Encoding.UTF8.GetBytes(a), B: Encoding.UTF8.GetBytes(b), C: Encoding.UTF8.GetBytes(c));

    [Fact]
    public void A_record_round_trips_every_field_exactly()
    {
        var record = Sample();
        int size = CaptureRecordCodec.EncodedSize(in record);
        var buffer = new byte[size];

        int written = CaptureRecordCodec.Encode(in record, buffer);
        Assert.Equal(size, written);

        bool ok = CaptureRecordCodec.TryDecode(buffer, out var decoded, out int consumed);

        Assert.True(ok);
        Assert.Equal(size, consumed);
        Assert.Equal(record.Seq, decoded.Seq);
        Assert.Equal(record.Qpc, decoded.Qpc);
        Assert.Equal(record.UnixMs, decoded.UnixMs);
        Assert.Equal(record.Id, decoded.Id);
        Assert.Equal(record.CauseId, decoded.CauseId);
        Assert.Equal(record.RootId, decoded.RootId);
        Assert.Equal(record.Kind, decoded.Kind);
        Assert.Equal(record.Phase, decoded.Phase);
        Assert.Equal(record.Priority, decoded.Priority);
        Assert.Equal(record.Truncated, decoded.Truncated);
        Assert.Equal(record.N0, decoded.N0);
        Assert.Equal(record.N1, decoded.N1);
        Assert.Equal(record.PayloadOffset, decoded.PayloadOffset);
        Assert.Equal(record.PayloadLength, decoded.PayloadLength);
        Assert.Equal(record.A.ToArray(), decoded.A.ToArray());
        Assert.Equal(record.B.ToArray(), decoded.B.ToArray());
        Assert.Equal(record.C.ToArray(), decoded.C.ToArray());
    }

    [Fact]
    public void Truncated_flag_round_trips_true()
    {
        var record = Sample(truncated: true);
        var buffer = new byte[CaptureRecordCodec.EncodedSize(in record)];
        CaptureRecordCodec.Encode(in record, buffer);

        Assert.True(CaptureRecordCodec.TryDecode(buffer, out var decoded, out _));
        Assert.True(decoded.Truncated);
    }

    [Fact]
    public void Empty_ABC_fields_round_trip_as_empty_not_null()
    {
        var record = Sample(a: "", b: "", c: "");
        var buffer = new byte[CaptureRecordCodec.EncodedSize(in record)];
        CaptureRecordCodec.Encode(in record, buffer);

        Assert.True(CaptureRecordCodec.TryDecode(buffer, out var decoded, out int consumed));
        Assert.Equal(CaptureRecord.FixedHeaderBytes, consumed);
        Assert.Equal(0, decoded.A.Length);
        Assert.Equal(0, decoded.B.Length);
        Assert.Equal(0, decoded.C.Length);
    }

    [Fact]
    public void A_torn_header_shorter_than_the_fixed_96_bytes_fails_to_decode_without_throwing()
    {
        var record = Sample();
        var buffer = new byte[CaptureRecordCodec.EncodedSize(in record)];
        CaptureRecordCodec.Encode(in record, buffer);

        var torn = buffer.AsMemory(0, CaptureRecord.FixedHeaderBytes - 1);
        bool ok = CaptureRecordCodec.TryDecode(torn, out _, out int consumed);

        Assert.False(ok);
        Assert.Equal(0, consumed);
    }

    [Fact]
    public void A_valid_header_whose_ABC_bytes_run_past_EOF_fails_to_decode_without_throwing()
    {
        var record = Sample();
        var buffer = new byte[CaptureRecordCodec.EncodedSize(in record)];
        CaptureRecordCodec.Encode(in record, buffer);

        // The 96-byte header (with its declared A/B/C lengths) is intact, but the file was truncated one byte into
        // the inline string bytes — exactly a crash mid-write, per §3.1's "at most one torn trailing record".
        var torn = buffer.AsMemory(0, CaptureRecord.FixedHeaderBytes + 1);
        bool ok = CaptureRecordCodec.TryDecode(torn, out _, out int consumed);

        Assert.False(ok);
        Assert.Equal(0, consumed);
    }

    [Fact]
    public void TruncateUtf8_leaves_a_short_string_untouched()
    {
        var bytes = CaptureRecordCodec.TruncateUtf8("spotify:track:abc123");
        Assert.Equal("spotify:track:abc123", Encoding.UTF8.GetString(bytes.Span));
    }

    [Fact]
    public void TruncateUtf8_cuts_an_overlong_string_to_the_cap_with_a_trailing_ellipsis_marker()
    {
        string overlong = new string('x', CaptureRecordCodec.MaxFieldBytes + 50);
        var bytes = CaptureRecordCodec.TruncateUtf8(overlong);

        Assert.Equal(CaptureRecordCodec.MaxFieldBytes, bytes.Length);
        string decoded = Encoding.UTF8.GetString(bytes.Span);
        Assert.EndsWith("…", decoded, StringComparison.Ordinal);
    }

    [Fact]
    public void TruncateUtf8_of_null_or_empty_is_empty()
    {
        Assert.Equal(0, CaptureRecordCodec.TruncateUtf8(null).Length);
        Assert.Equal(0, CaptureRecordCodec.TruncateUtf8("").Length);
    }

    /// <summary>The golden cross-language fixture: this EXACT byte sequence is also asserted, independently, in
    /// ops/tools/capture/test_capture.py's `GoldenCrossLanguageTests` (computed there via `layout.encode_record`
    /// over the identical field values). If either side's encoding ever drifts — a reordered field, a changed
    /// padding byte, a different truncation rule — one of the two golden tests fails, instead of the drift being
    /// discovered only when a real capture fails to decode. Do not "fix" a failure here by editing the array;
    /// regenerate it from BOTH sides and confirm they still agree.</summary>
    [Fact]
    public void Golden_bytes_match_the_python_decode_py_cross_language_fixture()
    {
        var record = new CaptureRecord(
            Seq: 42, Qpc: 123456789, UnixMs: 1700000000123, Id: 42, CauseId: 7, RootId: 1,
            Kind: CaptureKind.ActionInvoke, Phase: CapturePhase.Point, Priority: CapturePriority.Normal, Truncated: false,
            N0: 99, N1: -5, PayloadOffset: -1, PayloadLength: 0,
            A: Encoding.UTF8.GetBytes("AddToQueue"), B: Encoding.UTF8.GetBytes("spotify:track:abc"), C: default);

        var buffer = new byte[CaptureRecordCodec.EncodedSize(in record)];
        int written = CaptureRecordCodec.Encode(in record, buffer);

        byte[] expected =
        {
            0x2A, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x15, 0xCD, 0x5B, 0x07, 0x00, 0x00, 0x00, 0x00,
            0x7B, 0x68, 0xE5, 0xCF, 0x8B, 0x01, 0x00, 0x00, 0x2A, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x07, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x63, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFB, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
            0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x0A, 0x00, 0x11, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x41, 0x64, 0x64, 0x54, 0x6F, 0x51, 0x75, 0x65, 0x75, 0x65,                                     // "AddToQueue"
            0x73, 0x70, 0x6F, 0x74, 0x69, 0x66, 0x79, 0x3A, 0x74, 0x72, 0x61, 0x63, 0x6B, 0x3A, 0x61, 0x62, 0x63, // "spotify:track:abc"
        };

        Assert.Equal(123, written);
        Assert.Equal(expected.Length, written);
        Assert.Equal(expected, buffer);
    }

    [Fact]
    public void A_record_built_through_TruncateUtf8_round_trips_the_truncated_bytes()
    {
        string overlong = new string('y', CaptureRecordCodec.MaxFieldBytes + 10);
        var record = new CaptureRecord(
            Seq: 1, Qpc: 1, UnixMs: 1, Id: 1, CauseId: 0, RootId: 1,
            Kind: CaptureKind.DealerFrameIn, Phase: CapturePhase.Point, Priority: CapturePriority.Low, Truncated: false,
            N0: 0, N1: 0, PayloadOffset: -1, PayloadLength: 0,
            A: CaptureRecordCodec.TruncateUtf8(overlong), B: default, C: default);

        var buffer = new byte[CaptureRecordCodec.EncodedSize(in record)];
        CaptureRecordCodec.Encode(in record, buffer);
        Assert.True(CaptureRecordCodec.TryDecode(buffer, out var decoded, out _));
        Assert.Equal(CaptureRecordCodec.MaxFieldBytes, decoded.A.Length);
    }
}
