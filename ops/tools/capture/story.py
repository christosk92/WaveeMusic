"""ops/tools/capture/story.py — the causal-tree/story builder shared by query.py's --causes-of/--story/--anomalies
and html_report.py's --html rendering (§5, §2.5 of the realtime-capture plan). Pure functions over the decoded
record list `decode.iter_decoded`/`layout.load_all` already produced — no I/O here, so `test_capture.py` can drive
it directly against small, hand-built fixtures.
"""

from __future__ import annotations

import os
import sys
from dataclasses import dataclass
from typing import Any, Dict, List, Optional

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from layout import CapturePhase

# Anomaly kinds this module detects (§5's own list): echo mismatch, missing echo, UI disagreeing with the latest
# echoed state, and errors (non-2xx / DecodeFailed / FrameIgnored).
ANOMALY_ECHO_MISMATCH = "EchoMismatch"
ANOMALY_ECHO_MISSING = "EchoMissing"
ANOMALY_UI_DISAGREES = "UiDisagreesWithEcho"
ANOMALY_HTTP_ERROR = "HttpError"
ANOMALY_DECODE_FAILED = "DecodeFailed"
ANOMALY_FRAME_IGNORED = "FrameIgnored"

# The write-side convention this tool assumes for a `ConnectStatePutResponse` Point record (documented here since
# unit 4/5's call sites were not landed when this was written — see decode.py's module docstring for the same
# caveat on the byte layout): `n1` is 1 when the echo matched the message id we sent (0 = mismatch), `b` carries
# the `QueueEchoVerdict` name ("Exact"/"PositionShifted"/"RowMissing"/"RowsAdded", §6.4).
ECHO_VERDICT_EXACT = "Exact"


def _k(rec: Dict[str, Any]) -> str:
    return rec["kind"] if isinstance(rec["kind"], str) else str(rec["kind"])


def build_children_index(records: List[Dict[str, Any]]) -> Dict[int, List[Dict[str, Any]]]:
    """cause_id -> records directly caused by it, in Seq order. A record's own `id` is what a later record's
    `cause_id` names as its parent (Begin/End pairs share their Begin's `id`, so a Point caused by a still-open
    Begin naturally nests under it before the End even arrives — the "still open" tolerance §2.5 calls out)."""
    idx: Dict[int, List[Dict[str, Any]]] = {}
    for r in sorted(records, key=lambda r: r["seq"]):
        idx.setdefault(r["cause_id"], []).append(r)
    return idx


def find_roots(records: List[Dict[str, Any]]) -> List[Dict[str, Any]]:
    return sorted([r for r in records if r["root_id"] == r["id"]], key=lambda r: r["unix_ms"])


def by_id(records: List[Dict[str, Any]]) -> Dict[int, List[Dict[str, Any]]]:
    out: Dict[int, List[Dict[str, Any]]] = {}
    for r in records:
        out.setdefault(r["id"], []).append(r)
    return out


@dataclass
class StoryLine:
    depth: int
    record: Dict[str, Any]
    matched_end: Optional[Dict[str, Any]]  # the End record, when `record` is a Begin merged with its End
    latency_ms: Optional[int]  # from the ROOT's own timestamp
    anomaly: Optional[str] = None
    label: Optional[str] = None  # "optimistic render" / "echo render" etc.


def _describe(rec: Dict[str, Any], end: Optional[Dict[str, Any]]) -> str:
    parts = [_k(rec)]
    if rec.get("a"):
        parts.append(str(rec["a"]))
    if rec.get("b"):
        parts.append(str(rec["b"]))
    if rec.get("n0"):
        parts.append(f"n0={rec['n0']}")
    if rec.get("n1"):
        parts.append(f"n1={rec['n1']}")
    if end is not None:
        bits = [f"n0={end['n0']}"] if end.get("n0") else []
        if end.get("b"):
            bits.append(str(end["b"]))
        parts.append("-> " + (" ".join(bits) if bits else "(end)"))
    elif rec["phase"] == "Begin" or rec["phase"] == CapturePhase.Begin:
        parts.append("(no response yet)")
    return " ".join(parts)


def _ui_rerender_label(rec: Dict[str, Any], seen_ui_for_root: int) -> Optional[str]:
    if _k(rec) != "UiRerender":
        return None
    return "optimistic render" if seen_ui_for_root == 0 else "echo render"


def build_story(root: Dict[str, Any], records: List[Dict[str, Any]]) -> List[StoryLine]:
    """Depth-first walk of everything under `root` (root itself included as depth 0), in causal + time order,
    Begin/End pairs merged onto one line, optimistic-vs-echo `UiRerender`s labelled (§2.5's exact printout)."""
    children = build_children_index(records)
    ends_by_id = {r["id"]: r for r in records if r["phase"] in ("End", CapturePhase.End)}

    lines: List[StoryLine] = []
    ui_seen = 0

    def visit(rec: Dict[str, Any], depth: int) -> None:
        nonlocal ui_seen
        if rec["phase"] in ("End", CapturePhase.End):
            return  # printed merged onto its Begin line, not separately
        end = ends_by_id.get(rec["id"]) if rec["phase"] in ("Begin", CapturePhase.Begin) else None
        latency = rec["unix_ms"] - root["unix_ms"]
        label = _ui_rerender_label(rec, ui_seen)
        if label is not None:
            ui_seen += 1
        line = StoryLine(depth=depth, record=rec, matched_end=end, latency_ms=latency, label=label)
        line.anomaly = _line_anomaly(rec, end)
        lines.append(line)
        kids = sorted(children.get(rec["id"], []), key=lambda r: r["seq"])
        for kid in kids:
            visit(kid, depth + 1)

    visit(root, 0)
    return lines


def _line_anomaly(rec: Dict[str, Any], end: Optional[Dict[str, Any]]) -> Optional[str]:
    kind = _k(rec)
    if kind == "ConnectStatePutResponse":
        if rec.get("n1") == 0 or (rec.get("b") and rec["b"] != ECHO_VERDICT_EXACT):
            return ANOMALY_ECHO_MISMATCH
    if kind in ("HttpCall", "ConnectStatePut") and end is not None:
        status = end.get("n0")
        if isinstance(status, int) and not (200 <= status < 300):
            return ANOMALY_HTTP_ERROR
    if kind == "DecodeFailed":
        return ANOMALY_DECODE_FAILED
    if kind == "FrameIgnored":
        return ANOMALY_FRAME_IGNORED
    return None


def render_story_text(root: Dict[str, Any], records: List[Dict[str, Any]]) -> str:
    lines = build_story(root, records)
    out: List[str] = []
    header_time = _fmt_time(root["unix_ms"])
    # lines[0] is the root's own line (depth 0) — reuse its resolved `matched_end` so a root that is ITSELF a
    # Begin (e.g. --causes-of anchored on a ConnectStatePut) shows its End instead of a spurious "(no response yet)".
    root_line = lines[0] if lines else None
    root_end = root_line.matched_end if root_line else None
    if root_line and root_line.anomaly:
        pass  # the anomaly list below already reports it; the header line stays plain
    out.append(f"root #{root['id']}  {_describe(root, root_end)}  {header_time}")
    # figure out, per depth, whether a line is the last sibling at that depth (for the tree-drawing glyphs)
    for i, line in enumerate(lines[1:], start=1):
        is_last = _is_last_sibling(lines, i)
        prefix = _tree_prefix(lines, i, is_last)
        text = _describe(line.record, line.matched_end)
        time_s = _fmt_time(line.record["unix_ms"])
        lat = f"(+{line.latency_ms}ms)" if line.latency_ms is not None else ""
        marker = ""
        if line.label:
            marker = "   ← " + line.label.upper()
        if line.anomaly:
            marker += f"   ⚠ {line.anomaly}"
        out.append(f"{prefix}{text:<45} {time_s}  {lat}{marker}")
    anomalies = [l for l in lines if l.anomaly]
    for l in anomalies:
        out.append(f"⚠ ANOMALY {l.anomaly}: {_describe(l.record, l.matched_end)}")
    return "\n".join(out)


def _is_last_sibling(lines: List[StoryLine], i: int) -> bool:
    depth = lines[i].depth
    for j in range(i + 1, len(lines)):
        if lines[j].depth < depth:
            return True
        if lines[j].depth == depth:
            return False
    return True


def _tree_prefix(lines: List[StoryLine], i: int, is_last: bool) -> str:
    depth = lines[i].depth
    segs = []
    for d in range(1, depth):
        # is there a later sibling at depth `d` that continues the vertical line?
        continues = False
        for j in range(i + 1, len(lines)):
            if lines[j].depth < d:
                break
            if lines[j].depth == d:
                continues = True
                break
        segs.append("│   " if continues else "    ")
    segs.append("└─ " if is_last else "├─ ")
    return "".join(segs)


def _fmt_time(unix_ms: int) -> str:
    import datetime

    dt = datetime.datetime.fromtimestamp(unix_ms / 1000.0, tz=datetime.timezone.utc)
    return dt.strftime("%H:%M:%S.") + f"{unix_ms % 1000:03d}"


# ── anomalies over a whole segment (query.py's --anomalies, and the in-app "Anomalies" card's CLI equivalent) ──


@dataclass
class Anomaly:
    unix_ms: int
    kind: str
    detail: str
    root_id: int
    record: Dict[str, Any]


def detect_anomalies(records: List[Dict[str, Any]]) -> List[Anomaly]:
    out: List[Anomaly] = []
    ends_by_id = {r["id"]: r for r in records if r["phase"] in ("End", CapturePhase.End)}
    begins_by_id = {r["id"]: r for r in records if r["phase"] in ("Begin", CapturePhase.Begin)}
    max_unix_ms = max((r["unix_ms"] for r in records), default=0)

    for r in records:
        kind = _k(r)
        if kind == "ConnectStatePutResponse":
            if r.get("n1") == 0 or (r.get("b") and r["b"] != ECHO_VERDICT_EXACT):
                out.append(Anomaly(r["unix_ms"], ANOMALY_ECHO_MISMATCH,
                                    f"matched={r.get('n1')} diff={r.get('b')}", r["root_id"], r))
        elif kind == "DecodeFailed":
            out.append(Anomaly(r["unix_ms"], ANOMALY_DECODE_FAILED, f"{r.get('a')}: {r.get('b')}", r["root_id"], r))
        elif kind == "FrameIgnored":
            out.append(Anomaly(r["unix_ms"], ANOMALY_FRAME_IGNORED, f"reason={r.get('a')}", r["root_id"], r))
        elif kind in ("HttpCall", "ConnectStatePut") and r["phase"] in ("End", CapturePhase.End):
            status = r.get("n0")
            if isinstance(status, int) and not (200 <= status < 300):
                out.append(Anomaly(r["unix_ms"], ANOMALY_HTTP_ERROR, f"status={status} a={r.get('a')}",
                                    r["root_id"], r))

    # EchoMissing: a ConnectStatePut Begin with no matching End at all — read-time inferred, per §2.5/§5.
    for id_, begin in begins_by_id.items():
        if _k(begin) != "ConnectStatePut":
            continue
        if id_ not in ends_by_id:
            elapsed = max_unix_ms - begin["unix_ms"]
            out.append(Anomaly(begin["unix_ms"], ANOMALY_ECHO_MISSING,
                                f"no response after {elapsed}ms (a={begin.get('a')})", begin["root_id"], begin))

    out.sort(key=lambda a: a.unix_ms, reverse=True)
    return out
