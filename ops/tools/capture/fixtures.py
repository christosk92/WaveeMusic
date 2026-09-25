#!/usr/bin/env python3
"""ops/tools/capture/fixtures.py — a synthetic capture generator, built from records constructed per the §6.1
layout (`layout.py`), used by:
  - `test_capture.py`'s self-test (imports `FixtureWriter` directly, no disk I/O needed for most assertions);
  - a human, via the CLI below, to produce a demo `logs/capture/`-shaped directory to poke at with
    `decode.py`/`query.py`/`html_report.py` before a real capture from the app exists.

`FixtureWriter` mirrors the ID-minting shape `Capture.cs` documents (§2.4/§6.5: `NewRoot`/`Begin`/`End`/`Point`) in
plain Python, so a fixture's `Id`/`CauseId`/`RootId` triples are exactly as causally consistent as a real capture
would be — this is what lets `story.py`'s tree walk be exercised meaningfully rather than against arbitrary ids.
"""

from __future__ import annotations

import argparse
import gzip
import os
import sys
from typing import Dict, List, Optional, Tuple

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from layout import CaptureKind, CapturePhase, CapturePriority, CaptureRecord, encode_record


class FixtureWriter:
    """Builds a list of `CaptureRecord`s plus a payload blob, in the exact two-buffer shape the real writer
    produces (§3.1: one `.idx`, one `.blob`, `PayloadOffset`/`PayloadLength` pointing into the latter)."""

    def __init__(self, start_unix_ms: int = 1_700_000_000_000, qpc_hz: int = 10_000_000):
        self._seq = 0
        self._unix_ms = start_unix_ms
        self._qpc = 0
        self._qpc_hz = qpc_hz
        self.records: List[CaptureRecord] = []
        self.blob = bytearray()
        self._causes: Dict[int, int] = {}
        self._roots: Dict[int, int] = {}
        self._open_kind: Dict[int, CaptureKind] = {}

    def advance(self, ms: int) -> None:
        self._unix_ms += ms
        self._qpc += int(ms * self._qpc_hz / 1000)

    def _next_seq(self) -> int:
        self._seq += 1
        return self._seq

    def _new_event_ids(self, seq: int, threaded_cause_id: int) -> Tuple[int, int, int]:
        if threaded_cause_id == 0:
            return seq, 0, seq
        root = self._roots.get(threaded_cause_id, 0)
        if root == 0:
            root = threaded_cause_id
        return seq, threaded_cause_id, root

    def _remember(self, id_: int, cause_id: int, root_id: int) -> None:
        self._roots[id_] = root_id
        self._causes[id_] = cause_id

    def _add_payload(self, payload: bytes) -> Tuple[int, int]:
        if not payload:
            return -1, 0
        offset = len(self.blob)
        self.blob += payload
        return offset, len(payload)

    def _emit(self, kind: CaptureKind, phase: CapturePhase, cause_id: int,
              a: Optional[str] = None, b: Optional[str] = None, c: Optional[str] = None,
              n0: int = 0, n1: int = 0, payload: bytes = b"", priority: Optional[CapturePriority] = None) -> int:
        seq = self._next_seq()
        id_, resolved_cause, root_id = self._new_event_ids(seq, cause_id)
        self._remember(id_, resolved_cause, root_id)
        if phase == CapturePhase.Begin:
            self._open_kind[id_] = kind
        payload_offset, payload_length = self._add_payload(payload)
        rec = CaptureRecord(
            seq=seq, qpc=self._qpc, unix_ms=self._unix_ms, id=id_, cause_id=resolved_cause, root_id=root_id,
            kind=kind, phase=phase, priority=priority or CapturePriority.Normal, truncated=False,
            n0=n0, n1=n1, payload_offset=payload_offset, payload_length=payload_length,
            a=a, b=b, c=c, payload=payload,
        )
        self.records.append(rec)
        return id_

    def new_root(self, kind: CaptureKind, a: Optional[str] = None, b: Optional[str] = None, n0: int = 0) -> int:
        return self._emit(kind, CapturePhase.Point, 0, a=a, b=b, n0=n0)

    def begin(self, kind: CaptureKind, cause_id: int, a: Optional[str] = None, b: Optional[str] = None,
              payload: bytes = b"") -> int:
        return self._emit(kind, CapturePhase.Begin, cause_id, a=a, b=b, payload=payload)

    def end(self, begin_id: int, n0: int = 0, n1: int = 0, payload: bytes = b"") -> int:
        kind = self._open_kind.get(begin_id, CaptureKind.HttpCall)
        cause_id = self._causes.get(begin_id, 0)
        root_id = self._roots.get(begin_id, begin_id)
        seq = self._next_seq()
        payload_offset, payload_length = self._add_payload(payload)
        rec = CaptureRecord(
            seq=seq, qpc=self._qpc, unix_ms=self._unix_ms, id=begin_id, cause_id=cause_id, root_id=root_id,
            kind=kind, phase=CapturePhase.End, priority=CapturePriority.Normal, truncated=False,
            n0=n0, n1=n1, payload_offset=payload_offset, payload_length=payload_length,
            a=None, b=None, c=None, payload=payload,
        )
        self.records.append(rec)
        return begin_id

    def point(self, kind: CaptureKind, cause_id: int, a: Optional[str] = None, b: Optional[str] = None,
              n0: int = 0, n1: int = 0, payload: bytes = b"") -> int:
        return self._emit(kind, CapturePhase.Point, cause_id, a=a, b=b, n0=n0, n1=n1, payload=payload)

    # ── serialisation ────────────────────────────────────────────────────────────────────────────────────────

    def to_idx_bytes(self) -> bytes:
        return b"".join(encode_record(r) for r in self.records)

    def to_blob_bytes(self) -> bytes:
        return bytes(self.blob)

    def write(self, out_dir: str, date_stamp: str = "20260923", gzip_it: bool = False) -> Tuple[str, str]:
        os.makedirs(out_dir, exist_ok=True)
        idx_path = os.path.join(out_dir, f"capture-{date_stamp}.idx")
        blob_path = os.path.join(out_dir, f"capture-{date_stamp}.blob")
        idx_bytes = self.to_idx_bytes()
        blob_bytes = self.to_blob_bytes()
        if gzip_it:
            idx_path += ".gz"
            blob_path += ".gz"
            idx_bytes = gzip.compress(idx_bytes)
            blob_bytes = gzip.compress(blob_bytes)
        with open(idx_path, "wb") as f:
            f.write(idx_bytes)
        with open(blob_path, "wb") as f:
            f.write(blob_bytes)
        return idx_path, blob_path

    def roll_segment(self, out_dir: str, date_stamp: str, gzip_it: bool = False) -> Tuple[str, str]:
        """Writes everything buffered SO FAR to its own segment file pair, then clears the buffers for the next
        segment while KEEPING the seq/id/causality state flowing — mirroring a real day/size roll (§3.1): `Seq`
        and every minted `Id` stay globally unique and causally resolvable across the roll; only the blob's own
        byte offsets and the in-memory record list restart per segment file. Two independent `FixtureWriter()`
        instances would NOT be equivalent to this — their id spaces would collide (both start at 1), which is
        exactly the cross-segment contamination this method exists to avoid in a multi-day fixture."""
        paths = self.write(out_dir, date_stamp=date_stamp, gzip_it=gzip_it)
        self.records = []
        self.blob = bytearray()
        return paths


# ── small protobuf builders (the inverse of proto_dump.py's decoder — just enough to build a Cluster/PutState
# payload byte-for-byte for the self-test, no third-party protobuf library involved) ─────────────────────────────


def _tag(field_no: int, wire_type: int) -> bytes:
    return _varint((field_no << 3) | wire_type)


def _varint(value: int) -> bytes:
    out = bytearray()
    v = value
    while True:
        b = v & 0x7F
        v >>= 7
        if v:
            out.append(b | 0x80)
        else:
            out.append(b)
            break
    return bytes(out)


def _len_delim(field_no: int, data: bytes) -> bytes:
    return _tag(field_no, 2) + _varint(len(data)) + data


def _pb_string(field_no: int, s: str) -> bytes:
    return _len_delim(field_no, s.encode("utf-8"))


def _pb_varint(field_no: int, v: int) -> bytes:
    return _tag(field_no, 0) + _varint(v)


def build_provided_track(uri: str, uid: str = "", provider: str = "context") -> bytes:
    out = bytearray()
    out += _pb_string(1, uri)
    if uid:
        out += _pb_string(2, uid)
    if provider:
        out += _pb_string(6, provider)
    return bytes(out)


def build_context_index(page: int, track: int) -> bytes:
    return _pb_varint(1, page) + _pb_varint(2, track)


def build_player_state(next_tracks: List[Tuple[str, str]], queue_revision: str, index=(0, 0),
                        timestamp: int = 0) -> bytes:
    out = bytearray()
    out += _pb_varint(1, timestamp)
    out += _len_delim(6, build_context_index(*index))
    for uri, uid in next_tracks:
        out += _len_delim(20, build_provided_track(uri, uid))
    out += _pb_string(24, queue_revision)
    return bytes(out)


def build_cluster(active_device_id: str, server_timestamp_ms: int, next_tracks: List[Tuple[str, str]],
                   queue_revision: str) -> bytes:
    out = bytearray()
    out += _pb_varint(1, server_timestamp_ms)
    out += _pb_string(2, active_device_id)
    out += _len_delim(3, build_player_state(next_tracks, queue_revision))
    out += _pb_varint(9, server_timestamp_ms)
    return bytes(out)


def build_put_state_request(message_id: int, device_id: str) -> bytes:
    out = bytearray()
    out += _pb_varint(6, message_id)
    out += _pb_string(7, device_id)
    return bytes(out)


# ── the two canonical scenarios from §2.5, plus an anomaly-rich segment for --anomalies ────────────────────────


def build_clean_add_to_queue_story(w: FixtureWriter) -> int:
    """§2.5's first printout — an echo that matches exactly."""
    root = w.new_root(CaptureKind.ActionInvoke, a="AddToQueue", b="spotify:track:abc")
    w.advance(1)
    w.point(CaptureKind.QueueMutation, root, a="insert", b="spotify:track:abc", n0=3)
    w.advance(2)
    w.point(CaptureKind.UiRerender, root, a="queue.panel.rows [pos 3 = abc]")
    w.advance(2)
    put = w.begin(CaptureKind.ConnectStatePut, root, a="AddToQueue",
                  payload=build_put_state_request(889, "device-1"))
    w.advance(192)
    w.end(put, n0=200, payload=build_cluster("device-1", w._unix_ms, [("spotify:track:abc", "u1")], "rev-1"))
    w.point(CaptureKind.ConnectStatePutResponse, put, b="Exact", n0=192, n1=1)
    w.advance(1)
    w.point(CaptureKind.UiRerender, put, a="queue.panel.rows [pos 3 = abc]")
    return root


def build_anomaly_add_to_queue_story(w: FixtureWriter) -> int:
    """§2.5's second printout — the echo moved the row (`PositionShifted`)."""
    root = w.new_root(CaptureKind.ActionInvoke, a="AddToQueue", b="spotify:track:xyz")
    w.advance(1)
    w.point(CaptureKind.QueueMutation, root, a="insert", b="spotify:track:xyz", n0=1)
    w.advance(1)
    w.point(CaptureKind.UiRerender, root, a="queue.panel.rows [pos 1 = xyz]")
    w.advance(2)
    put = w.begin(CaptureKind.ConnectStatePut, root, a="AddToQueue",
                  payload=build_put_state_request(902, "device-1"))
    w.advance(203)
    w.end(put, n0=200, payload=build_cluster("device-1", w._unix_ms, [("spotify:track:xyz", "u2")], "rev-2"))
    w.point(CaptureKind.ConnectStatePutResponse, put, b="PositionShifted", n0=203, n1=1)
    w.advance(1)
    w.point(CaptureKind.UiRerender, put, a="queue.panel.rows [pos 4 = xyz]")
    return root


def build_anomaly_segment(w: FixtureWriter) -> Dict[str, int]:
    """One of each anomaly kind story.detect_anomalies knows about, for the self-test and for a demo `--anomalies`
    run: EchoMismatch, EchoMissing, FrameIgnored, a non-2xx HttpCall, DecodeFailed."""
    ids: Dict[str, int] = {}

    # EchoMismatch: matched=0
    root = w.new_root(CaptureKind.ActionInvoke, a="Play")
    put = w.begin(CaptureKind.ConnectStatePut, root, a="Play", payload=b"")
    w.advance(50)
    w.end(put, n0=200)
    w.point(CaptureKind.ConnectStatePutResponse, put, b="RowMissing", n0=50, n1=0)
    ids["echo_mismatch_root"] = root

    # EchoMissing: a Begin with no matching End at all
    root2 = w.new_root(CaptureKind.ActionInvoke, a="SetShuffle")
    w.begin(CaptureKind.ConnectStatePut, root2, a="SetShuffle")
    ids["echo_missing_root"] = root2

    # FrameIgnored
    root3 = w.new_root(CaptureKind.RemoteRoot, a="hm://connect-state/v1/cluster")
    w.point(CaptureKind.FrameIgnored, root3, a="StaleServerTime")
    ids["frame_ignored_root"] = root3

    # non-2xx HttpCall
    root4 = w.new_root(CaptureKind.ActionInvoke, a="LoadImage")
    http = w.begin(CaptureKind.HttpCall, root4, a="/v1/me/player")
    w.advance(10)
    w.end(http, n0=503)
    ids["http_error_root"] = root4

    # DecodeFailed
    root5 = w.new_root(CaptureKind.RemoteRoot, a="hm://connect-state/v1/cluster")
    w.point(CaptureKind.DecodeFailed, root5, a="JsonException", b="cluster decode")
    ids["decode_failed_root"] = root5

    return ids


def build_demo_writer() -> FixtureWriter:
    w = FixtureWriter()
    build_clean_add_to_queue_story(w)
    w.advance(500)
    build_anomaly_add_to_queue_story(w)
    w.advance(500)
    build_anomaly_segment(w)
    return w


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(description="Write a synthetic logs/capture/-shaped directory for manual testing.")
    parser.add_argument("out_dir")
    parser.add_argument("--date", default="20260923")
    parser.add_argument("--gzip", action="store_true", help="write a rolled/gzipped segment instead of a live one")
    args = parser.parse_args(argv)

    w = build_demo_writer()
    idx_path, blob_path = w.write(args.out_dir, date_stamp=args.date, gzip_it=args.gzip)
    print(f"wrote {idx_path}\nwrote {blob_path}\n{len(w.records)} records")
    return 0


if __name__ == "__main__":
    sys.exit(main())
