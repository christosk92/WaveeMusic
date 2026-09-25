#!/usr/bin/env python3
"""ops/tools/capture/query.py — the query verbs from §5 of docs/plans/wavee/realtime-capture-implementation.md.

    query.py <capture-dir-or-.idx-file> --causes-of ID
    query.py <capture-dir-or-.idx-file> --near UNIX_MS WINDOW_SECONDS
    query.py <capture-dir-or-.idx-file> --diff-queue ID1 ID2
    query.py <capture-dir-or-.idx-file> --story ROOT_ID
    query.py <capture-dir-or-.idx-file> --story --last N
    query.py <capture-dir-or-.idx-file> --anomalies
    ... any of the above, plus --html OUT.html to also write a graphical report (§5's "so the owner can see a
        click's story graphically" ask) alongside the text output.

Stdlib-only. Loads every segment under the given path with `layout.load_all`, decodes payloads with `decode.py`,
and hands the result to `story.py`'s pure tree/anomaly builders.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from typing import Any, Dict, List, Optional

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import decode
import html_report
import story as story_mod


def _load(path: str) -> List[Dict[str, Any]]:
    return decode.iter_decoded(path)


def _find_by_id(records: List[Dict[str, Any]], event_id: int) -> Optional[Dict[str, Any]]:
    candidates = [r for r in records if r["id"] == event_id]
    if not candidates:
        return None
    # Prefer a Begin/Point over an End (a Begin/End pair shares one id — the Begin is the more useful "root of
    # this subtree" anchor for --causes-of; the End's own children, if any, are still reachable via the same id).
    for r in candidates:
        if r["phase"] != "End":
            return r
    return candidates[0]


def cmd_causes_of(records: List[Dict[str, Any]], event_id: int) -> Dict[str, Any]:
    anchor = _find_by_id(records, event_id)
    if anchor is None:
        return {"error": f"no event with id {event_id}"}
    text = story_mod.render_story_text(anchor, records)
    return {"anchor_id": event_id, "text": text, "root": anchor}


def cmd_near(records: List[Dict[str, Any]], unix_ms: int, window_seconds: float) -> Dict[str, Any]:
    window_ms = int(window_seconds * 1000)
    hits = [r for r in records if abs(r["unix_ms"] - unix_ms) <= window_ms]
    hits.sort(key=lambda r: r["unix_ms"])
    return {"unix_ms": unix_ms, "window_seconds": window_seconds, "count": len(hits), "events": hits}


def _apply_mutation(state: List[str], verb: Optional[str], uri: Optional[str], pos: Optional[int]) -> None:
    if not uri:
        return
    if verb == "insert":
        idx = pos if pos is not None and 0 <= pos <= len(state) else len(state)
        state.insert(idx, uri)
    elif verb == "remove":
        if uri in state:
            state.remove(uri)
    elif verb == "move":
        if uri in state:
            state.remove(uri)
        idx = pos if pos is not None and 0 <= pos <= len(state) else len(state)
        state.insert(idx, uri)
    # an unrecognised verb is left alone — this tool never invents a mutation it wasn't told about


def cmd_diff_queue(records: List[Dict[str, Any]], id1: int, id2: int) -> Dict[str, Any]:
    e1 = _find_by_id(records, id1)
    e2 = _find_by_id(records, id2)
    if e1 is None or e2 is None:
        return {"error": f"could not resolve both ids ({id1}, {id2})"}
    ts1, ts2 = sorted((e1["unix_ms"], e2["unix_ms"]))

    mutations = sorted(
        [r for r in records if r["kind"] == "QueueMutation" or str(r["kind"]).endswith("QueueMutation")],
        key=lambda r: r["seq"],
    )
    before: List[str] = []
    after: List[str] = []
    window: List[Dict[str, Any]] = []
    for m in mutations:
        pos = m.get("n0")
        pos_int = int(pos) if isinstance(pos, int) else None
        if m["unix_ms"] < ts1:
            _apply_mutation(before, m.get("a"), m.get("b"), pos_int)
            _apply_mutation(after, m.get("a"), m.get("b"), pos_int)
        elif ts1 <= m["unix_ms"] <= ts2:
            _apply_mutation(after, m.get("a"), m.get("b"), pos_int)
            window.append(m)
        # mutations after ts2 do not affect this diff

    diff_rows = []
    before_pos = {uri: i for i, uri in enumerate(before)}
    after_pos = {uri: i for i, uri in enumerate(after)}
    for uri in sorted(set(before) | set(after)):
        b = before_pos.get(uri)
        a = after_pos.get(uri)
        if b is None:
            verdict = "added"
        elif a is None:
            verdict = "removed"
        elif a != b:
            verdict = "moved"
        else:
            verdict = "unchanged"
        diff_rows.append({"uri": uri, "before_pos": b, "after_pos": a, "verdict": verdict})

    return {
        "id1": id1, "id2": id2,
        "before": before, "after": after,
        "mutations_in_window": len(window),
        "diff": diff_rows,
    }


def cmd_story(records: List[Dict[str, Any]], root_id: int) -> Optional[Dict[str, Any]]:
    root = next((r for r in records if r["id"] == root_id and r["root_id"] == root_id), None)
    if root is None:
        root = _find_by_id(records, root_id)
    if root is None:
        return None
    return {"root": root, "text": story_mod.render_story_text(root, records)}


def cmd_story_last(records: List[Dict[str, Any]], n: int) -> List[Dict[str, Any]]:
    roots = story_mod.find_roots(records)
    roots.sort(key=lambda r: r["unix_ms"], reverse=True)
    chosen = roots[:n]
    return [{"root": r, "text": story_mod.render_story_text(r, records)} for r in chosen]


def cmd_anomalies(records: List[Dict[str, Any]]) -> List[story_mod.Anomaly]:
    return story_mod.detect_anomalies(records)


def _print_json(obj: Any) -> None:
    print(json.dumps(obj, indent=2, default=str))


def _make_console_utf8_safe() -> None:
    # A Windows console's legacy codepage (cp1252/cp437) cannot encode the tree-drawing glyphs / arrows / warning
    # sign this tool prints (⚠, ├─, └─, │, ←) — reconfigure stdout/stderr to UTF-8 so a run from cmd.exe/PowerShell
    # never crashes on an ordinary anomaly report. Redirected-to-file streams already default to UTF-8; this is a
    # no-op there.
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
    g = parser.add_mutually_exclusive_group(required=True)
    g.add_argument("--causes-of", type=int, metavar="ID")
    g.add_argument("--near", nargs=2, metavar=("UNIX_MS", "WINDOW_SECONDS"))
    g.add_argument("--diff-queue", nargs=2, type=int, metavar=("ID1", "ID2"))
    g.add_argument("--story", nargs="?", const="__no_id__", metavar="ROOT_ID")
    g.add_argument("--anomalies", action="store_true")
    parser.add_argument("--last", type=int, metavar="N", help="with --story: the N most recent roots instead of one id")
    parser.add_argument("--html", metavar="OUT.html", help="also write a graphical report to this path")
    args = parser.parse_args(argv)

    records = _load(args.path)

    if args.causes_of is not None:
        result = cmd_causes_of(records, args.causes_of)
        if "error" in result:
            print(result["error"], file=sys.stderr)
            return 1
        print(result["text"])
        if args.html:
            open(args.html, "w", encoding="utf-8").write(
                html_report.render_story_html(result["root"], records, title=f"causes-of #{args.causes_of}")
            )
            print(f"wrote {args.html}", file=sys.stderr)
        return 0

    if args.near is not None:
        unix_ms = int(args.near[0])
        window_s = float(args.near[1])
        result = cmd_near(records, unix_ms, window_s)
        _print_json(result)
        return 0

    if args.diff_queue is not None:
        id1, id2 = args.diff_queue
        result = cmd_diff_queue(records, id1, id2)
        if "error" in result:
            print(result["error"], file=sys.stderr)
            return 1
        _print_json(result)
        return 0

    if args.story is not None:
        if args.last:
            stories = cmd_story_last(records, args.last)
            if not stories:
                print("(no roots in this capture)")
                return 0
            for i, s in enumerate(stories):
                if i:
                    print()
                print(s["text"])
            if args.html:
                # one HTML file per requested story, suffixed by root id, plus an index-less concat for convenience
                base, ext = os.path.splitext(args.html)
                for s in stories:
                    out_path = f"{base}.{s['root']['id']}{ext or '.html'}"
                    open(out_path, "w", encoding="utf-8").write(
                        html_report.render_story_html(s["root"], records, title=f"story #{s['root']['id']}")
                    )
                    print(f"wrote {out_path}", file=sys.stderr)
            return 0
        if args.story == "__no_id__":
            print("--story requires a ROOT_ID, or pass --last N", file=sys.stderr)
            return 2
        root_id = int(args.story)
        result = cmd_story(records, root_id)
        if result is None:
            print(f"no root #{root_id} in this capture", file=sys.stderr)
            return 1
        print(result["text"])
        if args.html:
            open(args.html, "w", encoding="utf-8").write(
                html_report.render_story_html(result["root"], records, title=f"story #{root_id}")
            )
            print(f"wrote {args.html}", file=sys.stderr)
        return 0

    if args.anomalies:
        anomalies = cmd_anomalies(records)
        if not anomalies:
            print("(no anomalies)")
        for a in anomalies:
            print(f"{a.unix_ms}  {a.kind:<16} root=#{a.root_id}  {a.detail}")
        if args.html:
            open(args.html, "w", encoding="utf-8").write(html_report.render_anomalies_html(anomalies))
            print(f"wrote {args.html}", file=sys.stderr)
        return 0

    return 2


if __name__ == "__main__":
    sys.exit(main())
