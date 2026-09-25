#!/usr/bin/env python3
"""ops/tools/capture/decode.py — idx+blob -> time-ordered JSON, one line per event, gzip/base64/redaction-aware.

Unit 7 (§5 of docs/plans/wavee/realtime-capture-implementation.md). Stdlib-only.

Usage:
    python decode.py <capture-dir-or-.idx-file> [--pretty] [--kind KIND] [--limit N]

Each event is printed as one JSON object per line (ndjson), sorted by `Seq` (the only total order that survives a
day roll, per `CaptureEvent`'s own doc comment). `--pretty` prints an indented, human-scannable form instead.

Payload decode ladder (§5's own wording): gunzip when the bytes sniff as gzip -> UTF-8 JSON -> a protobuf guess
(cluster / put-state / player-state, via proto_dump.py) keyed off the record's own `Kind`/`A` field -> hex dump.
Redaction already happened on the C# side before bytes ever reached the blob (§4/unit 3) — this tool never
un-redacts anything, it only decodes structure.
"""

from __future__ import annotations

import argparse
import gzip
import json
import os
import sys
from typing import Any, Dict, List, Optional

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import layout
import proto_dump

# CaptureKinds whose payload is plausibly protobuf (connect-state), vs. dealer/HTTP JSON text, vs. never a payload.
_PROTOBUF_KINDS = {
    layout.CaptureKind.ConnectStatePut,
    layout.CaptureKind.ConnectStatePutResponse,
}


def _hex_summary(raw: bytes, limit: int = 256) -> Dict[str, Any]:
    head = raw[:limit]
    return {
        "length": len(raw),
        "hex": head.hex(),
        "truncated_for_display": len(raw) > limit,
    }


def decode_payload(rec: "layout.CaptureRecord") -> "tuple[Optional[str], Any]":
    """Returns (payload_kind, decoded) — payload_kind is one of "json", "protobuf:<Schema>", "text", "hex", or
    None when there is no payload at all."""
    raw = rec.payload
    if not raw:
        return None, None

    if raw[:2] == b"\x1f\x8b":
        try:
            raw = gzip.decompress(raw)
        except OSError:
            return "hex", _hex_summary(rec.payload)

    # 1. UTF-8 JSON (dealer frames, some HTTP bodies)
    try:
        text = raw.decode("utf-8")
        try:
            return "json", json.loads(text)
        except json.JSONDecodeError:
            # decodes as text but isn't JSON — dealer topic bodies etc. are still worth printing verbatim.
            if all(32 <= ord(ch) or ch in "\r\n\t" for ch in text[:64]):
                return "text", text
        except Exception:
            pass
    except UnicodeDecodeError:
        pass

    # 2. protobuf guess, hinted by Kind/A (connect-state bodies are never valid UTF-8 text in practice)
    if rec.kind in _PROTOBUF_KINDS or (rec.a and any(h in rec.a.lower() for h in ("cluster", "put", "player"))):
        try:
            name, decoded = proto_dump.guess_and_decode(raw, hint=rec.a or "")
            return f"protobuf:{name}", decoded
        except proto_dump.ProtoDecodeError:
            pass

    # 3. last-ditch protobuf guess for anything else length-delimited-shaped, before giving up
    try:
        name, decoded = proto_dump.guess_and_decode(raw, hint=rec.a or "")
        if decoded:
            return f"protobuf:{name}", decoded
    except proto_dump.ProtoDecodeError:
        pass

    return "hex", _hex_summary(raw)


def record_to_dict(rec: "layout.CaptureRecord", include_payload: bool = True) -> Dict[str, Any]:
    d: Dict[str, Any] = {
        "seq": rec.seq,
        "qpc": rec.qpc,
        "unix_ms": rec.unix_ms,
        "id": rec.id,
        "cause_id": rec.cause_id,
        "root_id": rec.root_id,
        "kind": rec.kind.name if isinstance(rec.kind, layout.CaptureKind) else rec.kind,
        "phase": rec.phase.name if isinstance(rec.phase, layout.CapturePhase) else rec.phase,
        "priority": rec.priority.name if isinstance(rec.priority, layout.CapturePriority) else rec.priority,
        "truncated": rec.truncated,
        "n0": rec.n0,
        "n1": rec.n1,
        "a": rec.a,
        "b": rec.b,
        "c": rec.c,
    }
    if include_payload:
        payload_kind, decoded = decode_payload(rec)
        d["payload_kind"] = payload_kind
        d["payload"] = decoded
    return d


def iter_decoded(root: str) -> List[Dict[str, Any]]:
    """The shared entry point `query.py` and any future caller use: load every segment under `root`, decode every
    record's payload, return dicts in Seq order."""
    return [record_to_dict(r) for r in layout.load_all(root)]


def _make_console_utf8_safe() -> None:
    for stream_name in ("stdout", "stderr"):
        stream = getattr(sys, stream_name)
        if hasattr(stream, "reconfigure"):
            try:
                stream.reconfigure(encoding="utf-8", errors="replace")
            except (ValueError, OSError):
                pass


def main(argv: Optional[List[str]] = None) -> int:
    _make_console_utf8_safe()
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("path", help="a capture directory, or a single .idx/.idx.gz file")
    parser.add_argument("--pretty", action="store_true", help="indent each JSON object instead of ndjson")
    parser.add_argument("--kind", help="only print records of this CaptureKind (by name)")
    parser.add_argument("--limit", type=int, default=0, help="stop after N records (0 = no limit)")
    parser.add_argument("--no-payload", action="store_true", help="skip payload decode (faster, header-only)")
    args = parser.parse_args(argv)

    count = 0
    for rec in layout.load_all(args.path):
        if args.kind and rec.kind.name != args.kind:
            continue
        d = record_to_dict(rec, include_payload=not args.no_payload)
        if args.pretty:
            print(json.dumps(d, indent=2, default=str))
        else:
            print(json.dumps(d, default=str))
        count += 1
        if args.limit and count >= args.limit:
            break
    return 0


if __name__ == "__main__":
    sys.exit(main())
