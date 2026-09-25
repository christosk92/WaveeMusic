"""ops/tools/capture/layout.py — the ONE place the on-disk `.idx`/`.blob` contract is written down for Python.

Unit 7 of docs/plans/wavee/realtime-capture-implementation.md (§5, §6.1). This module owns:
  - the `CaptureKind`/`CapturePhase`/`CapturePriority` enums, in EXACTLY the declaration order used by the C#
    enums in src/apps/Wavee/Diagnostics/Capture.cs (a C# enum with no explicit numbers assigns 0, 1, 2, ... in
    declaration order — so the order below must track that file, not be re-alphabetised; verified against that
    file, no explicit numeric values are assigned to any member there);
  - the fixed 96-byte header layout, mirrored byte-for-byte from unit 2's REAL writer/codec —
    `src/apps/Wavee/Diagnostics/Capture.Host.cs`'s `CaptureRecordCodec.Encode`/`TryDecode` (~lines 76-148), read
    directly rather than re-derived from unit 1's prose comment (which undersold the exact field order and the
    "no per-string length prefix, three lengths live in the header" shape — this module now matches the code, not
    the comment);
  - segment discovery under `logs/capture/` (§3.1's naming, confirmed against `RealtimeCaptureWriter`'s own
    `LiveIdxPath`/`LiveBlobPath`/`RollSegment`: `capture-yyyyMMdd.idx`/`.blob` for the live segment, and
    `capture-yyyyMMdd-<segmentIndex>-<HHmmss>.idx.gz`/`.blob.gz` for a rolled one).

The REAL on-disk layout (`CaptureRecordCodec.Encode`, `Capture.Host.cs:76-105`), little-endian, 96 bytes:

    [ 0.. 8) Seq          int64
    [ 8..16) Qpc          int64
    [16..24) UnixMs       int64
    [24..32) Id           int64
    [32..40) CauseId      int64
    [40..48) RootId       int64
    [48..56) N0           int64
    [56..64) N1           int64
    [64..68) PayloadOffset int32
    [68..72) PayloadLength int32
    [72]     Kind          uint8
    [73]     Phase         uint8
    [74]     Priority      uint8
    [75]     Truncated     uint8 (0/1)
    [76..78) len(A)        uint16
    [78..80) len(B)        uint16
    [80..82) len(C)        uint16
    [82..96) reserved, zero (14 bytes)

followed immediately by A's bytes, then B's, then C's, back to back — NOT individually length-prefixed inline;
the three lengths above are the only place a length lives. `FIXED_HEADER_FORMAT` below is this exact layout; if
the C# codec ever changes, that one line (and the field order in `encode_record`/`iter_records`) is what to fix.
"""

from __future__ import annotations

import glob
import gzip
import io
import os
import re
import struct
from dataclasses import dataclass
from enum import IntEnum
from typing import BinaryIO, Iterator, List, Optional, Tuple

# ── enums — declaration order MUST match src/apps/Wavee/Diagnostics/Capture.cs ──────────────────────────────────


class CaptureKind(IntEnum):
    UserInput = 0
    ActionInvoke = 1
    Navigate = 2
    RemoteRoot = 3
    PlaybackCommand = 4
    PlaybackTransition = 5
    QueueMutation = 6
    QueueSeed = 7
    HttpCall = 8
    ConnectStatePut = 9
    ConnectStatePutResponse = 10
    DealerFrameIn = 11
    DealerFrameOut = 12
    RemoteRequest = 13
    OwnershipTransition = 14
    UiRerender = 15
    DecodeFailed = 16
    FrameIgnored = 17
    Retry = 18
    EchoMissing = 19  # never written by the C# side — read-time-inferred only (§2.5); kept here for completeness


class CapturePhase(IntEnum):
    Point = 0
    Begin = 1
    End = 2


class CapturePriority(IntEnum):
    Low = 0
    Normal = 1


# ── the fixed 96-byte header — byte-for-byte from CaptureRecordCodec.Encode (Capture.Host.cs:76-98) ────────────────
#
# Order: Seq, Qpc, UnixMs, Id, CauseId, RootId, N0, N1 (8 x int64, 64 bytes), PayloadOffset, PayloadLength
# (2 x int32, 8 bytes), Kind, Phase, Priority, Truncated (4 x uint8, 4 bytes), len(A), len(B), len(C)
# (3 x uint16, 6 bytes), then 14 reserved zero bytes -> 96.
FIXED_HEADER_FORMAT = "<8q2i4B3H14x"
FIXED_HEADER_SIZE = struct.calcsize(FIXED_HEADER_FORMAT)
assert FIXED_HEADER_SIZE == 96, f"layout drifted from the documented 96 bytes: {FIXED_HEADER_SIZE}"

MAX_STRING_BYTES = 252  # CaptureRecordCodec.MaxFieldBytes
TRUNCATION_MARKER = "…"  # CaptureRecordCodec.Ellipsis: Encoding.UTF8.GetBytes("…") — 3 bytes, E2 80 A6


@dataclass(frozen=True)
class CaptureRecord:
    seq: int
    qpc: int
    unix_ms: int
    id: int
    cause_id: int
    root_id: int
    kind: CaptureKind
    phase: CapturePhase
    priority: CapturePriority
    truncated: bool
    n0: int
    n1: int
    payload_offset: int
    payload_length: int
    a: Optional[str]
    b: Optional[str]
    c: Optional[str]
    payload: bytes = b""  # resolved from the paired .blob file by the reader below; b"" when no payload

    @property
    def is_root(self) -> bool:
        return self.root_id == self.id


def _truncate_utf8(s: Optional[str]) -> bytes:
    """Mirrors `CaptureRecordCodec.TruncateUtf8` (`Capture.Host.cs:50-67`) exactly: truncate by CHARACTER count
    first (an over-estimate — never fewer chars than the byte budget could hold), then trim down to a char count
    whose UTF-8 re-encoding actually fits `room = MaxFieldBytes - len(ellipsis)`, then append the 3-byte '…'
    marker. This is deliberately the same two-pass shape as the C# code, not a byte-boundary scan, so the two
    agree on the exact truncation point for any input (including multi-byte codepoints) — a golden byte comparison
    (`test_capture.py`'s cross-language test) is what keeps this honest.
    """
    if not s:
        return b""
    raw = s.encode("utf-8")
    if len(raw) <= MAX_STRING_BYTES:
        return raw
    marker = TRUNCATION_MARKER.encode("utf-8")  # 3 bytes
    room = MAX_STRING_BYTES - len(marker)
    char_guess = min(len(s), room)
    while char_guess > 0 and len(s[:char_guess].encode("utf-8")) > room:
        char_guess -= 1
    head = s[:char_guess].encode("utf-8")
    return head + marker


def encode_record(rec: CaptureRecord) -> bytes:
    """The inverse of `iter_records` below — used by fixtures.py to build synthetic segments for the self-test,
    and usable standalone to hand-build a repro capture. Byte-for-byte mirrors `CaptureRecordCodec.Encode`
    (`Capture.Host.cs:76-105`): fixed 96-byte header (§ this module's header comment) then A, B, C back to back,
    each already truncated/length-known from the header — no per-string length prefix inline."""
    a = _truncate_utf8(rec.a)
    b = _truncate_utf8(rec.b)
    c = _truncate_utf8(rec.c)
    header = struct.pack(
        FIXED_HEADER_FORMAT,
        rec.seq, rec.qpc, rec.unix_ms, rec.id, rec.cause_id, rec.root_id, rec.n0, rec.n1,
        rec.payload_offset, rec.payload_length,
        int(rec.kind), int(rec.phase), int(rec.priority), 1 if rec.truncated else 0,
        len(a), len(b), len(c),
    )
    return header + a + b + c


def iter_records(idx_bytes: bytes) -> Iterator[CaptureRecord]:
    """Parses a `.idx` blob's raw bytes into `CaptureRecord`s (payload NOT yet resolved — `payload`/`payload_offset`/
    `payload_length` are left as read from the header; join against a `.blob` with `resolve_payloads` below).
    Mirrors `CaptureRecordCodec.TryDecode` (`Capture.Host.cs:111-148`) field-for-field.

    Crash-safety (§1 item 7, §3.1): a torn trailing record — the fixed header itself cut short, or a header whose
    declared A/B/C lengths run past EOF — is NOT an error. It means the capture ended here (a hard kill mid-write).
    Parsing simply stops and yields everything decoded up to that point, exactly as `TryDecode` returning false
    tells its C# caller to do.
    """
    pos = 0
    n = len(idx_bytes)
    while pos < n:
        if pos + FIXED_HEADER_SIZE > n:
            return  # torn trailing header — capture ended here
        header = idx_bytes[pos:pos + FIXED_HEADER_SIZE]
        fields = struct.unpack(FIXED_HEADER_FORMAT, header)
        (seq, qpc, unix_ms, id_, cause_id, root_id, n0, n1, payload_offset, payload_length,
         kind, phase, priority, truncated, len_a, len_b, len_c) = fields

        total = FIXED_HEADER_SIZE + len_a + len_b + len_c
        if pos + total > n:
            return  # a valid header, but A/B/C run past EOF — torn trailing record, stop here (§1 item 7)

        cursor = pos + FIXED_HEADER_SIZE
        a_bytes = idx_bytes[cursor:cursor + len_a]; cursor += len_a
        b_bytes = idx_bytes[cursor:cursor + len_b]; cursor += len_b
        c_bytes = idx_bytes[cursor:cursor + len_c]; cursor += len_c

        try:
            kind_e = CaptureKind(kind)
        except ValueError:
            kind_e = kind  # an unknown byte from a newer writer — surface it verbatim rather than crashing
        try:
            phase_e = CapturePhase(phase)
        except ValueError:
            phase_e = phase
        try:
            priority_e = CapturePriority(priority)
        except ValueError:
            priority_e = priority

        yield CaptureRecord(
            seq=seq, qpc=qpc, unix_ms=unix_ms, id=id_, cause_id=cause_id, root_id=root_id,
            kind=kind_e, phase=phase_e, priority=priority_e, truncated=bool(truncated),
            n0=n0, n1=n1, payload_offset=payload_offset, payload_length=payload_length,
            a=a_bytes.decode("utf-8", errors="replace") if len_a else None,
            b=b_bytes.decode("utf-8", errors="replace") if len_b else None,
            c=c_bytes.decode("utf-8", errors="replace") if len_c else None,
        )
        pos = cursor


def resolve_payloads(records: List[CaptureRecord], blob_bytes: bytes) -> List[CaptureRecord]:
    out = []
    for r in records:
        if r.payload_offset >= 0 and r.payload_length > 0 and r.payload_offset + r.payload_length <= len(blob_bytes):
            payload = blob_bytes[r.payload_offset:r.payload_offset + r.payload_length]
        else:
            payload = b""
        out.append(
            CaptureRecord(
                seq=r.seq, qpc=r.qpc, unix_ms=r.unix_ms, id=r.id, cause_id=r.cause_id, root_id=r.root_id,
                kind=r.kind, phase=r.phase, priority=r.priority, truncated=r.truncated,
                n0=r.n0, n1=r.n1, payload_offset=r.payload_offset, payload_length=r.payload_length,
                a=r.a, b=r.b, c=r.c, payload=payload,
            )
        )
    return out


def _maybe_gunzip(path: str) -> bytes:
    with open(path, "rb") as f:
        data = f.read()
    if path.endswith(".gz") or data[:2] == b"\x1f\x8b":
        return gzip.decompress(data)
    return data


_SEGMENT_RE = re.compile(r"^capture-(\d{8})(?:-(\d+)-(\d{6}))?\.idx(\.gz)?$")


def _segment_sort_key(idx_path: str) -> Tuple[str, int, str]:
    name = os.path.basename(idx_path)
    m = _SEGMENT_RE.match(name)
    if not m:
        return (name, 0, "")
    date, roll, hhmmss, _gz = m.groups()
    # A bare "capture-YYYYMMDD.idx" (no roll suffix) is the LIVE, currently-open segment for that day — it always
    # sorts after every rolled/closed segment for the same day (§3.1: rolled segments are the ones already closed).
    roll_n = int(roll) if roll else 10 ** 9
    return (date, roll_n, hhmmss or "")


def discover_segments(root: str) -> List[str]:
    """Returns `.idx`/`.idx.gz` paths under `root` (a directory, or a single file path handed through as-is),
    oldest-first by the ordering §3.1's naming scheme implies (see `_segment_sort_key`)."""
    if os.path.isfile(root):
        return [root]
    paths = glob.glob(os.path.join(root, "capture-*.idx")) + glob.glob(os.path.join(root, "capture-*.idx.gz"))
    return sorted(paths, key=_segment_sort_key)


def _blob_path_for(idx_path: str) -> str:
    if idx_path.endswith(".idx.gz"):
        return idx_path[: -len(".idx.gz")] + ".blob.gz"
    if idx_path.endswith(".idx"):
        return idx_path[: -len(".idx")] + ".blob"
    raise ValueError(f"not a capture .idx path: {idx_path}")


def load_segment(idx_path: str) -> List[CaptureRecord]:
    idx_bytes = _maybe_gunzip(idx_path)
    records = list(iter_records(idx_bytes))
    blob_path = _blob_path_for(idx_path)
    blob_bytes = _maybe_gunzip(blob_path) if os.path.exists(blob_path) else b""
    return resolve_payloads(records, blob_bytes)


def load_all(root: str) -> List[CaptureRecord]:
    """Loads every segment under `root` and returns records in a single, globally-sorted-by-Seq list — Seq is the
    only total order a reader can trust across a day roll (CaptureEvent's own doc comment, §2.1)."""
    all_records: List[CaptureRecord] = []
    for seg in discover_segments(root):
        all_records.extend(load_segment(seg))
    all_records.sort(key=lambda r: r.seq)
    return all_records
