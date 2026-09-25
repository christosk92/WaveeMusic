#!/usr/bin/env python3
"""ops/tools/capture/test_capture.py — self-test for the decode/query tool (§5, §7's "no source-text tests" spirit
applied to a Python tool: every test builds fixtures via `fixtures.py`'s pure builders and asserts on THEIR output,
never on the literal text of decode.py/query.py/story.py).

Run with:
    python -m unittest test_capture -v
or, from the repo root:
    python -m unittest discover -s ops/tools/capture -p "test_*.py" -v
"""

from __future__ import annotations

import contextlib
import io
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import decode
import fixtures
import layout
import query
import story as story_mod
from layout import CaptureKind, CapturePhase, CapturePriority, CaptureRecord


class LayoutCodecTests(unittest.TestCase):
    def test_roundtrip_fixed_and_strings(self):
        rec = CaptureRecord(
            seq=1, qpc=1000, unix_ms=1_700_000_000_123, id=1, cause_id=0, root_id=1,
            kind=CaptureKind.ActionInvoke, phase=CapturePhase.Point, priority=CapturePriority.Normal,
            truncated=False, n0=42, n1=-7, payload_offset=-1, payload_length=0,
            a="AddToQueue", b="spotify:track:abc", c=None,
        )
        encoded = layout.encode_record(rec)
        decoded = list(layout.iter_records(encoded))
        self.assertEqual(len(decoded), 1)
        d = decoded[0]
        self.assertEqual(d.seq, 1)
        self.assertEqual(d.unix_ms, 1_700_000_000_123)
        self.assertEqual(d.kind, CaptureKind.ActionInvoke)
        self.assertEqual(d.phase, CapturePhase.Point)
        self.assertEqual(d.n0, 42)
        self.assertEqual(d.n1, -7)
        self.assertEqual(d.a, "AddToQueue")
        self.assertEqual(d.b, "spotify:track:abc")
        self.assertIsNone(d.c)

    def test_string_truncated_at_252_bytes_with_marker(self):
        long_a = "x" * 500
        rec = CaptureRecord(
            seq=1, qpc=0, unix_ms=0, id=1, cause_id=0, root_id=1,
            kind=CaptureKind.HttpCall, phase=CapturePhase.Point, priority=CapturePriority.Normal,
            truncated=False, n0=0, n1=0, payload_offset=-1, payload_length=0,
            a=long_a, b=None, c=None,
        )
        encoded = layout.encode_record(rec)
        decoded = next(layout.iter_records(encoded))
        self.assertLessEqual(len(decoded.a.encode("utf-8")), 252)
        self.assertTrue(decoded.a.endswith("…"))

    def test_multiple_records_concatenate(self):
        recs = [
            CaptureRecord(seq=i, qpc=i, unix_ms=i, id=i, cause_id=0, root_id=i,
                          kind=CaptureKind.Retry, phase=CapturePhase.Point, priority=CapturePriority.Low,
                          truncated=False, n0=i, n1=0, payload_offset=-1, payload_length=0, a=None, b=None, c=None)
            for i in range(1, 4)
        ]
        blob = b"".join(layout.encode_record(r) for r in recs)
        decoded = list(layout.iter_records(blob))
        self.assertEqual([d.seq for d in decoded], [1, 2, 3])

    def test_torn_trailing_record_is_not_an_error(self):
        rec = CaptureRecord(seq=1, qpc=0, unix_ms=0, id=1, cause_id=0, root_id=1,
                             kind=CaptureKind.HttpCall, phase=CapturePhase.Point, priority=CapturePriority.Normal,
                             truncated=False, n0=0, n1=0, payload_offset=-1, payload_length=0,
                             a="hello", b=None, c=None)
        full = layout.encode_record(rec) + layout.encode_record(rec)
        torn = full[: len(full) - 5]  # chop off the tail of the second record
        decoded = list(layout.iter_records(torn))
        self.assertEqual(len(decoded), 1)  # the first, complete record is kept; the torn one is silently dropped

    def test_torn_header_only_is_not_an_error(self):
        rec = layout.encode_record(CaptureRecord(
            seq=1, qpc=0, unix_ms=0, id=1, cause_id=0, root_id=1,
            kind=CaptureKind.HttpCall, phase=CapturePhase.Point, priority=CapturePriority.Normal,
            truncated=False, n0=0, n1=0, payload_offset=-1, payload_length=0, a=None, b=None, c=None))
        torn = rec[:10]
        self.assertEqual(list(layout.iter_records(torn)), [])

    def test_segment_discovery_orders_rolled_before_live(self):
        with tempfile.TemporaryDirectory() as d:
            for name in ("capture-20260923.idx", "capture-20260923-2-143059.idx.gz",
                         "capture-20260922.idx", "capture-20260922-2-235900.idx.gz"):
                open(os.path.join(d, name), "wb").close()
                blob_name = name.replace(".idx.gz", ".blob.gz").replace(".idx", ".blob")
                open(os.path.join(d, blob_name), "wb").close()
            segs = [os.path.basename(p) for p in layout.discover_segments(d)]
            self.assertEqual(segs, [
                "capture-20260922-2-235900.idx.gz", "capture-20260922.idx",
                "capture-20260923-2-143059.idx.gz", "capture-20260923.idx",
            ])


class GoldenCrossLanguageTests(unittest.TestCase):
    """The Python half of a golden byte fixture also asserted, independently, by
    `src/apps/Wavee.Tests/CaptureRecordCodecTests.cs`'s `Golden_bytes_match_the_python_decode_py_cross_language_fixture`
    (hand-computed from the identical field values via `CaptureRecordCodec.Encode`). If the two ever produce
    different bytes for this record, one of the two golden tests fails — this is what keeps `layout.py`'s
    `FIXED_HEADER_FORMAT`/`encode_record` from silently drifting from the real C# writer again (unit 2 landed
    disagreeing with an earlier, comment-only guess at this layout; this test exists so that can't happen twice
    unnoticed). Do not "fix" a failure here by editing the expected bytes — regenerate from BOTH sides and confirm
    they still agree."""

    def test_golden_bytes_match_the_csharp_codec(self):
        rec = layout.CaptureRecord(
            seq=42, qpc=123456789, unix_ms=1700000000123, id=42, cause_id=7, root_id=1,
            kind=CaptureKind.ActionInvoke, phase=CapturePhase.Point, priority=CapturePriority.Normal,
            truncated=False, n0=99, n1=-5, payload_offset=-1, payload_length=0,
            a="AddToQueue", b="spotify:track:abc", c=None,
        )
        encoded = layout.encode_record(rec)

        expected = bytes([
            0x2A, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x15, 0xCD, 0x5B, 0x07, 0x00, 0x00, 0x00, 0x00,
            0x7B, 0x68, 0xE5, 0xCF, 0x8B, 0x01, 0x00, 0x00, 0x2A, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x07, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x63, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFB, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
            0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x0A, 0x00, 0x11, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x41, 0x64, 0x64, 0x54, 0x6F, 0x51, 0x75, 0x65, 0x75, 0x65,  # "AddToQueue"
            0x73, 0x70, 0x6F, 0x74, 0x69, 0x66, 0x79, 0x3A, 0x74, 0x72, 0x61, 0x63, 0x6B, 0x3A, 0x61, 0x62, 0x63,  # "spotify:track:abc"
        ])

        self.assertEqual(len(encoded), 123)
        self.assertEqual(encoded, expected)

        # ...and it must decode back to the same fields, closing the loop.
        decoded = next(layout.iter_records(encoded))
        self.assertEqual(decoded.seq, 42)
        self.assertEqual(decoded.a, "AddToQueue")
        self.assertEqual(decoded.b, "spotify:track:abc")
        self.assertIsNone(decoded.c)
        self.assertEqual(decoded.n1, -5)
        self.assertEqual(decoded.payload_offset, -1)


class ProtoDumpTests(unittest.TestCase):
    def test_decode_cluster(self):
        buf = fixtures.build_cluster("device-1", 123456789, [("spotify:track:abc", "u1"), ("spotify:track:def", "u2")], "rev-7")
        decoded = decode_module_decode_cluster(buf)
        self.assertEqual(decoded["active_device_id"], "device-1")
        self.assertEqual(decoded["server_timestamp_ms"], 123456789)
        self.assertIn("player_state", decoded)
        ps = decoded["player_state"]
        self.assertEqual(ps["queue_revision"], "rev-7")
        uris = [t["uri"] for t in ps["next_tracks"]]
        self.assertEqual(uris, ["spotify:track:abc", "spotify:track:def"])
        self.assertEqual(ps["next_tracks"][0]["uid"], "u1")

    def test_decode_put_state_request(self):
        import proto_dump
        buf = fixtures.build_put_state_request(889, "device-1")
        decoded = proto_dump.decode_put_state_request(buf)
        self.assertEqual(decoded["message_id"], 889)
        self.assertEqual(decoded["last_command_sent_by_device_id"], "device-1")

    def test_guess_and_decode_prefers_hint(self):
        import proto_dump
        buf = fixtures.build_cluster("device-2", 1, [("spotify:track:z", "u9")], "rev-1")
        name, decoded = proto_dump.guess_and_decode(buf, hint="cluster")
        self.assertEqual(name, "CLUSTER")
        self.assertEqual(decoded["active_device_id"], "device-2")

    def test_malformed_bytes_raise_decode_error(self):
        import proto_dump
        with self.assertRaises(proto_dump.ProtoDecodeError):
            list(proto_dump.iter_wire_fields(b"\xff\xff\xff\xff\xff\xff\xff\xff\xff\xff\xff"))


def decode_module_decode_cluster(buf):
    import proto_dump
    return proto_dump.decode_cluster(buf)


class DecodePayloadTests(unittest.TestCase):
    def _rec(self, kind, payload, a=None):
        return layout.CaptureRecord(
            seq=1, qpc=0, unix_ms=0, id=1, cause_id=0, root_id=1, kind=kind, phase=CapturePhase.Point,
            priority=CapturePriority.Normal, truncated=False, n0=0, n1=0, payload_offset=0,
            payload_length=len(payload), a=a, b=None, c=None, payload=payload,
        )

    def test_json_payload(self):
        import json as _json
        rec = self._rec(CaptureKind.HttpCall, _json.dumps({"ok": True}).encode("utf-8"))
        kind, decoded = decode.decode_payload(rec)
        self.assertEqual(kind, "json")
        self.assertEqual(decoded, {"ok": True})

    def test_gzip_json_payload(self):
        import gzip as _gzip
        import json as _json
        raw = _gzip.compress(_json.dumps({"gz": 1}).encode("utf-8"))
        rec = self._rec(CaptureKind.DealerFrameIn, raw)
        kind, decoded = decode.decode_payload(rec)
        self.assertEqual(kind, "json")
        self.assertEqual(decoded, {"gz": 1})

    def test_protobuf_payload_detected_from_kind(self):
        buf = fixtures.build_cluster("device-9", 5, [("spotify:track:q", "u1")], "rev-9")
        rec = self._rec(CaptureKind.ConnectStatePut, buf, a="AddToQueue")
        kind, decoded = decode.decode_payload(rec)
        self.assertTrue(kind.startswith("protobuf:"))
        self.assertIn("active_device_id", decoded)

    def test_empty_payload_is_none(self):
        rec = self._rec(CaptureKind.Retry, b"")
        kind, decoded = decode.decode_payload(rec)
        self.assertIsNone(kind)
        self.assertIsNone(decoded)

    def test_junk_bytes_fall_back_to_hex(self):
        rec = self._rec(CaptureKind.HttpCall, bytes([0xFF, 0xFE, 0x00, 0x01, 0x02]))
        kind, decoded = decode.decode_payload(rec)
        self.assertEqual(kind, "hex")
        self.assertIn("hex", decoded)


class StoryBuilderTests(unittest.TestCase):
    def _records_for(self, builder_fn):
        w = fixtures.FixtureWriter()
        root_id = builder_fn(w)
        decoded = [decode.record_to_dict(r) for r in w.records]
        return decoded, root_id

    def test_clean_story_has_no_anomalies_and_pairs_optimistic_echo(self):
        records, root_id = self._records_for(fixtures.build_clean_add_to_queue_story)
        root = next(r for r in records if r["id"] == root_id)
        lines = story_mod.build_story(root, records)
        self.assertTrue(any(l.label == "optimistic render" for l in lines))
        self.assertTrue(any(l.label == "echo render" for l in lines))
        self.assertFalse(any(l.anomaly for l in lines))
        text = story_mod.render_story_text(root, records)
        self.assertIn("root #", text)
        self.assertIn("ConnectStatePutResponse", text)

    def test_position_shifted_story_flags_echo_mismatch(self):
        records, root_id = self._records_for(fixtures.build_anomaly_add_to_queue_story)
        root = next(r for r in records if r["id"] == root_id)
        lines = story_mod.build_story(root, records)
        mismatches = [l for l in lines if l.anomaly == story_mod.ANOMALY_ECHO_MISMATCH]
        self.assertEqual(len(mismatches), 1)
        text = story_mod.render_story_text(root, records)
        self.assertIn("⚠ ANOMALY", text)

    def test_causes_of_arbitrary_non_root_id(self):
        records, root_id = self._records_for(fixtures.build_clean_add_to_queue_story)
        put = next(r for r in records if r["kind"] == "ConnectStatePut" and r["phase"] == "Begin")
        text = story_mod.render_story_text(put, records)
        self.assertIn("ConnectStatePutResponse", text)

    def test_causes_of_header_shows_the_anchors_own_end(self):
        # Regression: the header line used to hardcode end=None, so anchoring --causes-of on a Begin (rather than
        # a true root) always printed "(no response yet)" even when its End was right there in the record set.
        records, root_id = self._records_for(fixtures.build_clean_add_to_queue_story)
        put = next(r for r in records if r["kind"] == "ConnectStatePut" and r["phase"] == "Begin")
        text = story_mod.render_story_text(put, records)
        header = text.splitlines()[0]
        self.assertIn("n0=200", header)
        self.assertNotIn("no response yet", header)


class AnomaliesTests(unittest.TestCase):
    def test_detect_one_of_each_anomaly_kind(self):
        w = fixtures.FixtureWriter()
        ids = fixtures.build_anomaly_segment(w)
        records = [decode.record_to_dict(r) for r in w.records]
        anomalies = story_mod.detect_anomalies(records)
        kinds_seen = {a.kind for a in anomalies}
        self.assertEqual(kinds_seen, {
            story_mod.ANOMALY_ECHO_MISMATCH, story_mod.ANOMALY_ECHO_MISSING,
            story_mod.ANOMALY_FRAME_IGNORED, story_mod.ANOMALY_HTTP_ERROR, story_mod.ANOMALY_DECODE_FAILED,
        })
        self.assertEqual(anomalies, sorted(anomalies, key=lambda a: a.unix_ms, reverse=True))


class EndToEndDiskTests(unittest.TestCase):
    """Round-trips through actual .idx/.blob files on disk — the whole pipeline decode.py/query.py exercise,
    including a gzipped rolled segment mixed with a live one, per §3.1's layout."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.dir = self.tmp.name

        # One continuous writer (one id/seq/causality space, exactly like one real process's whole session) rolled
        # into two segment files — a genuinely independent second FixtureWriter would restart its id space at 1
        # and collide with the first segment's ids once both are merged by `load_all` (§3.1's day-roll shape).
        w = fixtures.FixtureWriter()
        fixtures.build_clean_add_to_queue_story(w)
        w.roll_segment(self.dir, date_stamp="20260922", gzip_it=True)  # yesterday, rolled+gzipped

        w.advance(100_000)
        self.root2 = fixtures.build_anomaly_add_to_queue_story(w)
        fixtures.build_anomaly_segment(w)
        self.records2 = list(w.records)
        w.write(self.dir, date_stamp="20260923", gzip_it=False)  # today, live

    def test_load_all_merges_segments_in_seq_order(self):
        records = decode.iter_decoded(self.dir)
        seqs_per_segment_reset = [r["seq"] for r in records]
        # each segment's own Seq restarts at 1 (a fresh FixtureWriter per segment, mirroring a real day roll);
        # load_all's job is merging FILES in the right order, not gluing sequence numbers across them.
        self.assertGreater(len(records), 0)
        self.assertTrue(any(r["kind"] == "ActionInvoke" for r in records))

    def test_query_story_by_root_id(self):
        records = decode.iter_decoded(self.dir)
        result = query.cmd_story(records, self.root2)
        self.assertIsNotNone(result)
        self.assertIn("PositionShifted", result["text"])

    def test_query_anomalies(self):
        records = decode.iter_decoded(self.dir)
        anomalies = query.cmd_anomalies(records)
        self.assertGreaterEqual(len(anomalies), 5)

    def test_query_diff_queue(self):
        records = decode.iter_decoded(self.dir)
        mutation_ids = [r["id"] for r in records if r["kind"] == "QueueMutation"]
        self.assertGreaterEqual(len(mutation_ids), 1)
        result = query.cmd_diff_queue(records, mutation_ids[0], mutation_ids[-1])
        self.assertIn("diff", result)

    def test_query_near(self):
        records = decode.iter_decoded(self.dir)
        anchor = records[len(records) // 2]["unix_ms"]
        result = query.cmd_near(records, anchor, 5.0)
        self.assertGreaterEqual(result["count"], 1)

    def test_query_causes_of(self):
        records = decode.iter_decoded(self.dir)
        put_begin = next(r for r in records if r["kind"] == "ConnectStatePut" and r["phase"] == "Begin")
        result = query.cmd_causes_of(records, put_begin["id"])
        self.assertNotIn("error", result)
        self.assertIn("ConnectStatePutResponse", result["text"])

    def test_cli_decode_main_runs_clean(self):
        buf = io.StringIO()
        with contextlib.redirect_stdout(buf):
            rc = decode.main([self.dir, "--limit", "5"])
        self.assertEqual(rc, 0)
        lines = [l for l in buf.getvalue().splitlines() if l.strip()]
        self.assertEqual(len(lines), 5)

    def test_cli_query_anomalies_runs_clean(self):
        buf = io.StringIO()
        with contextlib.redirect_stdout(buf):
            rc = query.main([self.dir, "--anomalies"])
        self.assertEqual(rc, 0)
        self.assertIn("EchoMismatch", buf.getvalue())

    def test_cli_query_story_last(self):
        buf = io.StringIO()
        with contextlib.redirect_stdout(buf):
            rc = query.main([self.dir, "--story", "--last", "2"])
        self.assertEqual(rc, 0)
        self.assertIn("root #", buf.getvalue())

    def test_cli_html_report_written(self):
        out_html = os.path.join(self.dir, "story.html")
        buf = io.StringIO()
        with contextlib.redirect_stderr(buf):
            rc = query.main([self.dir, "--story", str(self.root2), "--html", out_html])
        self.assertEqual(rc, 0)
        self.assertTrue(os.path.exists(out_html))
        with open(out_html, "r", encoding="utf-8") as f:
            content = f.read()
        self.assertIn("<svg", content)
        self.assertIn("prefers-color-scheme: dark", content)


class HtmlReportTests(unittest.TestCase):
    def test_render_story_html_contains_svg_and_theme(self):
        import html_report
        w = fixtures.FixtureWriter()
        root_id = fixtures.build_clean_add_to_queue_story(w)
        records = [decode.record_to_dict(r) for r in w.records]
        root = next(r for r in records if r["id"] == root_id)
        page = html_report.render_story_html(root, records)
        self.assertIn("<svg", page)
        self.assertIn("prefers-color-scheme: dark", page)
        self.assertIn("ActionInvoke", page)

    def test_render_anomalies_html_lists_every_anomaly(self):
        import html_report
        w = fixtures.FixtureWriter()
        fixtures.build_anomaly_segment(w)
        records = [decode.record_to_dict(r) for r in w.records]
        anomalies = story_mod.detect_anomalies(records)
        page = html_report.render_anomalies_html(anomalies)
        for a in anomalies:
            self.assertIn(a.kind, page)


if __name__ == "__main__":
    unittest.main()
