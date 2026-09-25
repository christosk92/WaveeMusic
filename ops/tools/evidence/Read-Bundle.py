#!/usr/bin/env python3
"""Read Wavee evidence bundles (docs/plans/evidence-diagnostics-implementation.md §B/§C, in ..\\fluent-gpu).

A bundle is a folder written by Diagnostics > Evidence or `wavee://diag?cmd=bundle`: meta.json, frame.png, items.tsv,
placements.tsv, stale.tsv, ledger.tsv, walks.tsv, scroll.csv, census.json, nodes.tsv, keyed.tsv, vps.tsv,
pixel-<x>-<y>.tsv, log-tail.txt. Every subcommand prints plain text (and --json for machine use):

  summary  <bundle...>          the invariants per bundle (stale / exposed missing / scratch refused), items, stale rows
  band     <bundle...>          issues #1/#2/#3a/#3b over the artist band sweep (one bundle per scroll offset)
  pixels   <bundle...>          every pixel query in the bundles, item by item (feathers, tiles, stale)
  edgecue  <bundle...>          issue #5: the pixel column under a list's top edge — one item, the analytic feather
  lyrics   <bundle...>          issue #4: stale tiles, refused scratches, degraded items per bundle
  turns    <bundle|scroll.csv>  issue #6: record/build/submit/GPU percentiles, spikes joined to walks.tsv, notch latency
  backsteps <scroll-*.csv...>   issue #7: poses stepping against an advancing touchpad contact, flings after a rested lift
  drag     <bundle...> --key K  G: every presented pose of list K vs the rows realized then (clamped / uncovered frames)

Standard library only (Python 3.9+). Numbers are invariant-culture.
"""
import argparse
import csv
import glob
import io
import json
import math
import os
import sys
from collections import defaultdict

# ── reading ─────────────────────────────────────────────────────────────────────────────────────────────────────


def read_tsv(path):
    """A bundle TSV → list of dicts ('#' comment lines skipped)."""
    if not os.path.exists(path):
        return []
    with open(path, encoding="utf-8") as f:
        lines = [l.rstrip("\n").rstrip("\r") for l in f if l.strip() and not l.startswith("#")]
    if not lines:
        return []
    head = lines[0].split("\t")
    rows = []
    for l in lines[1:]:
        cells = l.split("\t")
        rows.append({head[i]: (cells[i] if i < len(cells) else "") for i in range(len(head))})
    return rows


def read_json(path):
    if not os.path.exists(path):
        return {}
    with open(path, encoding="utf-8") as f:
        return json.load(f)


class Bundle:
    def __init__(self, folder):
        self.folder = folder
        self.name = os.path.basename(os.path.normpath(folder))
        self.meta = read_json(os.path.join(folder, "meta.json"))
        self.census = read_json(os.path.join(folder, "census.json"))
        self.items = read_tsv(os.path.join(folder, "items.tsv"))
        self.placements = read_tsv(os.path.join(folder, "placements.tsv"))
        self.stale = read_tsv(os.path.join(folder, "stale.tsv"))
        self.ledger = read_tsv(os.path.join(folder, "ledger.tsv"))
        self.walks = read_tsv(os.path.join(folder, "walks.tsv"))
        self.nodes = read_tsv(os.path.join(folder, "nodes.tsv"))
        self.keyed = read_tsv(os.path.join(folder, "keyed.tsv"))
        self.pixels = {}
        for p in sorted(glob.glob(os.path.join(folder, "pixel-*.tsv"))):
            self.pixels[os.path.basename(p)[len("pixel-"):-len(".tsv")]] = read_tsv(p)

    @property
    def scale(self):
        return float(self.meta.get("scale") or 1.0)

    def viewport(self, prefix):
        for v in self.meta.get("viewports", []):
            key = v.get("key") or ""
            tail = key.split("/", 1)[1] if "/" in key else key
            if key.startswith(prefix) or tail.startswith(prefix):
                return v
        return None

    def tiles(self, key):
        return int(self.census.get("tiles", {}).get(key, 0))

    def device(self, key):
        return int(self.census.get("device", {}).get(key, 0))


def bundles(paths):
    out = []
    for p in paths:
        for q in sorted(glob.glob(p)) or [p]:
            if os.path.isdir(q) and os.path.exists(os.path.join(q, "meta.json")):
                out.append(Bundle(q))
    return out


def pct(values, q):
    if not values:
        return float("nan")
    v = sorted(values)
    k = (len(v) - 1) * q
    lo, hi = math.floor(k), math.ceil(k)
    return v[lo] if lo == hi else v[lo] + (v[hi] - v[lo]) * (k - lo)


def f(x, nd=3):
    try:
        return ("{:." + str(nd) + "f}").format(float(x))
    except (TypeError, ValueError):
        return str(x)


def note_fields(note):
    d = {}
    for kv in (note or "").split(";"):
        if "=" in kv:
            k, v = kv.split("=", 1)
            d[k] = v
    return d


# ── summary ─────────────────────────────────────────────────────────────────────────────────────────────────────


def cmd_summary(args):
    rows = []
    for b in bundles(args.bundles):
        rows.append({
            "bundle": b.name, "publishSeq": b.meta.get("publishSeq"), "frame": b.meta.get("tableFrame"),
            "captured": b.meta.get("captured"), "items": len(b.items), "placements": len(b.placements),
            "stale": b.tiles("staleTiles"), "exposedMissing": b.tiles("exposedMissing"),
            "scratchRefused": b.device("scratchRefused"), "staleRows": [
                "slice {slice} tile {tx},{ty} node {nodeKey} want {want} have {have} rasterFrame {rasterFrame}".format(**s) for s in b.stale],
        })
    emit(args, rows, ["bundle", "publishSeq", "frame", "captured", "items", "placements", "stale", "exposedMissing", "scratchRefused"])
    for r in rows:
        for s in r["staleRows"]:
            print("  STALE", r["bundle"], s)


# ── band (#1, #2, #3a, #3b) ─────────────────────────────────────────────────────────────────────────────────────


def cmd_band(args):
    out = []
    for b in bundles(args.bundles):
        vp = b.viewport(args.viewport)
        s = b.scale
        clip_top = round(args.clip_top_dip * s)   # the band line in WINDOW DIP: the viewport top + 56
        cut = []
        for it in b.items:
            clip = it.get("clip", "-")
            if clip == "-":
                continue
            x, y, w, h = (int(v) for v in clip.split(","))
            if abs(y - clip_top) <= 1 and it.get("feather1", "-") == "-":
                cut.append("{}#{}".format(it.get("nodeKey"), it.get("index")))
        magazine = [it for it in b.items if (it.get("nodeKey") or "").startswith("artist-under-band")]
        mag_feather = [it.get("feather1") for it in magazine if it.get("feather1") not in ("-", "")]
        pix = {}
        for name, hits in b.pixels.items():
            pix[name] = ["{kind}:{nodeKey} a={alpha} f1={feather1} stale={stale}".format(**h) for h in hits]
        out.append({
            "bundle": b.name, "offset": vp.get("offset") if vp else None, "stale": b.tiles("staleTiles"),
            "staleRows": ["{nodeKey} ({tx},{ty}) rasterFrame {rasterFrame}".format(**r) for r in b.stale],
            "cutAt56NoFeather": cut, "magazineFeather": mag_feather[:1], "pixels": pix,
        })
    if args.json:
        print(json.dumps(out, indent=2))
        return
    for r in out:
        print("{:<40} offset={:<9} stale={:<3} cut@56(no feather)={} magazine f1={}".format(
            r["bundle"], f(r["offset"], 1) if r["offset"] is not None else "?", r["stale"],
            ",".join(r["cutAt56NoFeather"]) or "-", ",".join(r["magazineFeather"]) or "-"))
        for s in r["staleRows"]:
            print("    STALE", s)
        for name, hits in sorted(r["pixels"].items()):
            print("    pixel", name, "|", " || ".join(hits))


# ── pixels / edgecue ────────────────────────────────────────────────────────────────────────────────────────────


def cmd_pixels(args):
    for b in bundles(args.bundles):
        for name, hits in sorted(b.pixels.items()):
            print(b.name, "pixel", name)
            for h in hits:
                print("   {order:>2} {kind:<7} {nodeKey:<44} a={alpha} f1={feather1} f2={feather2} cov={coverage} tile={tile} rf={rasterFrame} stale={stale}".format(**h))


def cmd_edgecue(args):
    for b in bundles(args.bundles):
        print(b.name)
        for name, hits in sorted(b.pixels.items(), key=lambda kv: int(kv[0].split("-")[1]) if "-" in kv[0] else 0):
            tiles = [h for h in hits if h["kind"] in ("Tiles", "Region")]
            painted = [h for h in hits if h["kind"] not in ("Tiles", "Region", "Group")]
            top = hits[-1] if hits else None
            verdict = "ok" if len(tiles) >= 1 and top and top["kind"] in ("Tiles", "Region") and not painted else "CHECK"
            print("   {:<14} items={} tilesItems={} top={} f1={} cov={} {}".format(
                name, len(hits), len(tiles), top["nodeKey"] if top else "-", top["feather1"] if top else "-",
                top["coverage"] if top else "-", verdict))


# ── lyrics (#4) ─────────────────────────────────────────────────────────────────────────────────────────────────


def cmd_lyrics(args):
    for b in bundles(args.bundles):
        refused = [e for e in b.ledger if e.get("scratchRefused") == "1"]
        unfaithful = [e for e in b.ledger if e.get("faithful") == "0"]
        degraded = [it for it in b.items if "degraded" in (it.get("flags") or "")]
        blurred = [it for it in b.items if it.get("blur") not in ("", "0", None)]
        print("{:<40} stale={} scratchRefused(device)={} ledgerScratchRefused={} unfaithful={} degradedItems={} blurItems={}".format(
            b.name, b.tiles("staleTiles"), b.device("scratchRefused"), len(refused), len(unfaithful), len(degraded), len(blurred)))
        for e in refused[-5:]:
            print("    REFUSED frame {frame} {nodeKey} ({tx},{ty}) reason {reason}".format(**e))


# ── turns (#6) ──────────────────────────────────────────────────────────────────────────────────────────────────


def read_scroll_csv(path):
    rows = []
    with open(path, encoding="utf-8") as fh:
        text = [l for l in fh if not l.startswith("#")]
    for r in csv.DictReader(io.StringIO("".join(text))):
        rows.append(r)
    return rows


def cmd_turns(args):
    src = args.source
    walks = []
    if os.path.isdir(src):
        walks = read_tsv(os.path.join(src, "walks.tsv"))
        src = os.path.join(src, "scroll.csv")
    rows = read_scroll_csv(src)
    costs = [(float(r["qpc_ms"]), note_fields(r["note"])) for r in rows if r["kind"] == "turn_cost"]
    turns = [(float(r["qpc_ms"]), note_fields(r["note"])) for r in rows if r["kind"] == "turn"]
    work_by_tick = {n.get("tick"): float(n.get("work_ms", "0") or 0) for _, n in turns}
    def series(key, only=None):
        return [float(n.get(key, "0") or 0) for _, n in costs if only is None or only(n)]
    print("turn_cost rows: {}  turn rows: {}".format(len(costs), len(turns)))
    for key in ("record_ms", "build_ms", "submit_ms", "gpu_ms"):
        v = series(key)
        print("  {:<10} p50={} p95={} p99={} max={}".format(key, f(pct(v, .5)), f(pct(v, .95)), f(pct(v, .99)), f(max(v) if v else float('nan'))))
    co = [float(n.get("build_ms", 0)) + float(n.get("submit_ms", 0)) for _, n in costs if n.get("composite_only") == "1"]
    print("  composite-only CPU (build+submit) p50={} p95={} max={} (design: <= 1.5 ms)".format(f(pct(co, .5)), f(pct(co, .95)), f(max(co) if co else float('nan'))))
    by_pass = defaultdict(list)
    for w in walks:
        by_pass[w.get("frame")].append(w)
    spikes = [(t, n) for t, n in costs if work_by_tick.get(n.get("tick"), 0) > args.spike_ms]
    print("spikes (turn work > {} ms): {}".format(args.spike_ms, len(spikes)))
    for t, n in spikes[: args.max_spikes]:
        ws = by_pass.get(n.get("pass"), [])
        print("  t={} tick={} work={} record={} build={} submit={} gpu={} tiles={} walked={} pass={}".format(
            f(t, 1), n.get("tick"), f(work_by_tick.get(n.get("tick"), 0)), n.get("record_ms"), n.get("build_ms"),
            n.get("submit_ms"), n.get("gpu_ms"), n.get("tiles"), n.get("walked"), n.get("pass")))
        for w in ws[:8]:
            print("      walk {nodeKey} why={why} bytes={bytes}".format(**w))
    # notch → plan → first moved present
    notches = [float(r["qpc_ms"]) for r in rows if r["kind"] == "notch"]
    plans = [float(r["qpc_ms"]) for r in rows if r["kind"] == "state_changed" and (r["note"] or "").startswith("plan_kind=")]
    frames = sorted(float(r["qpc_ms"]) for r in rows if r["kind"] == "frame" and r["delta"] not in ("", "0", "0.0"))
    refresh = next((float(r["note"].split("refresh_ms=")[1].split(";")[0]) for r in rows if r["kind"] == "present" and "refresh_ms=" in (r["note"] or "")), 16.7)
    late, lat = 0, []
    for t in notches:
        p = next((x for x in plans if x >= t), None)
        m = next((x for x in frames if x >= t), None)
        if p is None or p - t > refresh:
            late += 1
        if m is not None:
            lat.append(m - t)
    print("notches={} withoutPlanWithinOneRefresh={} (must be 0)  notch->first moved present p50={} p95={} max={} ms".format(
        len(notches), late, f(pct(lat, .5), 1), f(pct(lat, .95), 1), f(max(lat) if lat else float('nan'), 1)))


# ── drag (G, 2026-09-25) ────────────────────────────────────────────────────────────────────────────────────────


def drag_verdict(frames, coverage, inset, viewport):
    """PURE. Every presented pose of the list against the coverage it had at that instant. `frames` are
    (t, offset, clamped) rows of ONE viewport, `coverage` its (t, start, end) rows (emitted on change); a pose is
    UNCOVERED when the rows realized at that time do not span the visible band [offset + inset, offset + viewport).
    Returns (moving, clamped, uncovered, examples)."""
    cov = sorted(coverage)
    moving = clamped = uncovered = 0
    examples = []
    last_off = None
    ci = -1
    for t, off, cl in sorted(frames):
        while ci + 1 < len(cov) and cov[ci + 1][0] <= t:
            ci += 1
        if last_off is not None and abs(off - last_off) > 0.01:
            moving += 1
        last_off = off
        if cl:
            clamped += 1
        if ci < 0:
            continue
        _, start, end = cov[ci]
        lo, hi = off + inset, off + viewport
        if start > lo + 0.5 or end < hi - 0.5:
            uncovered += 1
            if len(examples) < 8:
                examples.append((t, off, start, end))
    return moving, clamped, uncovered, examples


def cmd_drag(args):
    for b in bundles(args.bundles):
        vp = b.viewport(args.key)
        if vp is None:
            print(b.name, "no viewport with key", args.key)
            continue
        node = str(vp.get("node"))
        path = os.path.join(b.folder, "scroll.csv")
        rows = read_scroll_csv(path) if os.path.exists(path) else []
        frames, coverage = [], []
        for r in rows:
            if r.get("vp") != node:
                continue
            n = note_fields(r.get("note"))
            if r["kind"] == "frame":
                frames.append((float(r["qpc_ms"]), float(r["offset"] or 0), n.get("clamped") == "1"))
            elif r["kind"] == "coverage":
                coverage.append((float(r["qpc_ms"]), float(n.get("start", "nan")), float(n.get("end", "nan"))))
        moving, clamped, uncovered, ex = drag_verdict(frames, coverage, args.inset, float(vp.get("viewport") or 0))
        offs = [o for _, o, _ in frames]
        print("{:<34} vp={} frames={} moving={} offset=[{},{}] clamped={} uncovered={} exposedMissing={} stale={} (all must be 0 but frames/moving)".format(
            b.name, node, len(frames), moving, f(min(offs), 0) if offs else "-", f(max(offs), 0) if offs else "-",
            clamped, uncovered, b.tiles("exposedMissing"), b.tiles("staleTiles")))
        for t, off, s, e in ex:
            print("    UNCOVERED t={} offset={} coverage=[{},{}]".format(f(t, 1), f(off, 1), f(s, 1), f(e, 1)))
        for name, hits in sorted(b.pixels.items(), key=lambda kv: kv[0]):
            top = hits[-1] if hits else None
            print("    pixel {:<14} items={} top={}:{} cov={} stale={}".format(
                name, len(hits), top["kind"] if top else "-", top["nodeKey"] if top else "NOTHING",
                top["coverage"] if top else "-", top["stale"] if top else "-"))


# ── backsteps (#7) ──────────────────────────────────────────────────────────────────────────────────────────────


def cmd_backsteps(args):
    total_back = total_fling = 0
    for path in sorted(p for pat in args.csvs for p in (glob.glob(pat) or [pat])):
        rows = read_scroll_csv(path)
        samples = [(float(r["qpc_ms"]), float(r["delta"] or 0)) for r in rows
                   if r.get("kind") == "raw_wheel" and r.get("device") == "Touchpad"]
        poses = defaultdict(list)
        for r in rows:
            if r.get("kind") == "frame" and r.get("vp"):
                poses[r.get("vp")].append((float(r["qpc_ms"]), float(r["offset"] or 0)))
        # contacts: runs of touchpad samples with gaps <= 120 ms
        contacts, cur = [], []
        for t, d in samples:
            if cur and t - cur[-1][0] > 120:
                contacts.append(cur)
                cur = []
            cur.append((t, d))
        if cur:
            contacts.append(cur)
        back = fling = 0
        for c in contacts:
            t0, t1 = c[0][0], c[-1][0]
            direction = 1 if sum(d for _, d in c) > 0 else -1
            for vp, ps in poses.items():
                seg = [p for p in ps if t0 <= p[0] <= t1]
                for (ta, pa), (tb, pb) in zip(seg, seg[1:]):
                    advanced = any(ta < t <= tb and d * direction > 0 for t, d in c)
                    if advanced and (pb - pa) * direction < -0.01:
                        back += 1
                # a lift after the finger rested (no movement in the last 100 ms of the contact) must not fling
                rested = all(abs(d) < 1e-6 for t, d in c if t > t1 - 100)
                after = [p for p in ps if t1 < p[0] <= t1 + 300]
                if rested and len(after) >= 2 and abs(after[-1][1] - after[0][1]) > 2.0:
                    fling += 1
        total_back += back
        total_fling += fling
        print("{}: contacts={} backsteps={} flingsAfterRest={}".format(os.path.basename(path), len(contacts), back, fling))
    print("TOTAL backsteps={} (must be 0) flingsAfterRest={} (must be 0)".format(total_back, total_fling))


def emit(args, rows, cols):
    if args.json:
        print(json.dumps(rows, indent=2))
        return
    print("\t".join(cols))
    for r in rows:
        print("\t".join(str(r.get(c)) for c in cols))


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--json", action="store_true")
    sub = ap.add_subparsers(dest="cmd", required=True)
    for name in ("summary", "pixels", "edgecue", "lyrics"):
        p = sub.add_parser(name)
        p.add_argument("bundles", nargs="+")
    p = sub.add_parser("band")
    p.add_argument("bundles", nargs="+")
    p.add_argument("--viewport", default="artist")
    p.add_argument("--clip-top-dip", type=float, default=56.0)
    p = sub.add_parser("turns")
    p.add_argument("source")
    p.add_argument("--spike-ms", type=float, default=8.0)
    p.add_argument("--max-spikes", type=int, default=20)
    p = sub.add_parser("backsteps")
    p.add_argument("csvs", nargs="+")
    p = sub.add_parser("drag")
    p.add_argument("bundles", nargs="+")
    p.add_argument("--key", required=True, help="the list viewport's key (or its tail after the tab prefix)")
    p.add_argument("--inset", type=float, default=0.0, help="the list's ItemClipTopInset, DIP")
    args = ap.parse_args(argv)
    {"summary": cmd_summary, "band": cmd_band, "pixels": cmd_pixels, "edgecue": cmd_edgecue, "lyrics": cmd_lyrics,
     "turns": cmd_turns, "backsteps": cmd_backsteps, "drag": cmd_drag}[args.cmd](args)


if __name__ == "__main__":
    main()
