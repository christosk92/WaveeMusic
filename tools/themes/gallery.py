"""gallery.py - the board-vs-app gallery for a theme (P6 lane WG).

For every board that the mount map knows, put the design board (a PNG of the .dc.html) next to what the real app draws
(`Wavee.exe --inspect`: mount + render) at the same size and the same scale, one HTML page per family (rows = tiers) plus an
index.html. Every app render also carries its `lint min=warn` findings.

    python -I tools/themes/gallery.py --theme zune
    python -I tools/themes/gallery.py --theme zune --only Z-Home Z-Album          # board-name substrings
    python -I tools/themes/gallery.py --theme zune --exe C:\\path\\Wavee.exe --jobs 4 --out C:\\WAVEE\\scratch\\gallery\\zune
    python -I tools/themes/gallery.py --theme zune --html-only                    # rebuild the pages and sheets from results.json
    python -I tools/themes/gallery.py --theme winui --map tools/themes/gallery-map.winui.json

Inputs
  canvas.json   docs/themes/boards/<theme>/project/canvas.json: the board list (order, size, title, page).
  the map       tools/themes/gallery-map.<theme>.json (see its "_doc" entry): board -> [{tier, w, h, route|uri|..., verbs}].
  board PNGs    <board-png-dir> (default C:\\WAVEE\\scratch\\board-png-<theme>); a missing one is rendered with board_shots.py
                (headless Edge) from a staged copy of the boards plus support.js.
Outputs (default C:\\WAVEE\\scratch\\gallery\\<theme>): index.html, <family>.html, boards/*.png, app/*.png, results.json (per
entry: ok, lint, warnings, seconds), sheets/<theme>-<family>-<n>.png.

GPU: every mount passes `gpu=hardware` (the offscreen D3D12 surface on the real adapter) unless --gpu warp says otherwise or
an entry sets its own `gpu`: WARP software rendering pegs the CPU, and hardware gives the same lint counts and pixels within
anti-aliasing, 1.5-5x faster.

Timing: before the run it prints an estimate (an entry's own time from the last results.json in --out when that run used the
same gpu, else a per-tier guess: EST_SECONDS); each entry's line carries its own time and the time left; the end prints the
total. results.json keeps each entry's seconds, its estimate and the gpu it ran on.

Contact sheets: per family, every board beside its app renders at one common scale, labelled (board, tier, render label, lint,
seconds, verb warnings), packed into PNGs at most SHEET_MAX px on a side, so an agent's image reader can open them whole.
They need PIL (Pillow); without it the run still writes the pages and says so. Everything else is the standard library.
The server client is tools/inspect.py, loaded by path.
"""

import argparse
import html
import importlib.util
import json
import os
import queue
import re
import shutil
import struct
import subprocess
import sys
import threading
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))          # WaveeMusic\
TIER_ORDER = ["Compact", "Small", "Standard", "Huge", "Huge4K"]
TIER_SIZE = {"Compact": (400, 860), "Small": (800, 600), "Standard": (1440, 960), "Huge": (2560, 1440), "Huge4K": (3840, 2160)}
SUPPORT_JS_CANDIDATES = [
    r"C:\Users\ChristosKarapasias\Downloads\Polaris dashboard redesign\support.js",
    r"C:\WAVEE\scratch\boards-zune\support.js",
]
ENTRY_TIMEOUT = 240      # seconds one command may take before the watchdog kills its server
FINDINGS_KEPT = 120      # findings stored per render
# The per-entry guess when results.json has no time of the entry's own (from a run on the same gpu): one server, a fresh
# process per entry (start, mount, verbs, render, lint), measured on the owner's machine beside other lanes' work (2026-10-24).
# EST_STAGE is added for a stage (`key f11`) entry. With gpu=hardware the tiers hardly differ; WARP draws 2560 and 3840 wide
# frames, and the stage's layers, on the CPU.
EST_SECONDS = {"hardware": {"Compact": 5, "Small": 5, "Standard": 6, "Huge": 7, "Huge4K": 8},
               "warp": {"Compact": 12, "Small": 12, "Standard": 14, "Huge": 26, "Huge4K": 48}}
EST_STAGE = {"hardware": {"Compact": 1, "Small": 1, "Standard": 1, "Huge": 1, "Huge4K": 2},
             "warp": {"Compact": 25, "Small": 25, "Standard": 30, "Huge": 70, "Huge4K": 120}}
SHEET_MAX = 1960         # a contact sheet's longest side, px
SHEET_ROW_H = 540        # the height a sheet group's images aim at, px (less when the group is wide)
SHEET_MIN_SCALE = 0.4    # below this a board and its renders stop sitting side by side and wrap into a grid


# ───────────────────────────── helpers ─────────────────────────────

def load_inspect():
    path = os.path.join(ROOT, "tools", "inspect.py")
    spec = importlib.util.spec_from_file_location("wavee_inspect_client", path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def png_size(path):
    try:
        with open(path, "rb") as fh:
            head = fh.read(24)
        if head[:8] == b"\x89PNG\r\n\x1a\n":
            return struct.unpack(">II", head[16:24])
    except OSError:
        pass
    return None


def slug(text):
    return re.sub(r"[^A-Za-z0-9]+", "-", text).strip("-").lower() or "x"


def esc(text):
    return html.escape(str(text), quote=True)


def derive_tier(name):
    """Tier from a board name: ...-Compact / -Small / -Huge4K / -Huge, else Standard."""
    for suffix, tier in (("-Huge4K", "Huge4K"), ("-Huge", "Huge"), ("-Compact", "Compact"), ("-Small", "Small")):
        if name.endswith(suffix):
            return tier, name[: -len(suffix)]
    return "Standard", name


def read_json(path):
    with open(path, encoding="utf-8") as fh:
        return json.load(fh)


def write_text(path, text):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(text)


# ───────────────────────────── the plan ─────────────────────────────

class Plan:
    """canvas.json + the map -> the list of boards, the entries to render, the unmapped and unlisted boards."""

    def __init__(self, canvas, gmap, only):
        self.theme = gmap.get("theme", "")
        boards = canvas.get("boards", {})
        order = canvas.get("order") or list(boards)
        self.canvas_order = [b[:-8] if b.endswith(".dc.html") else b for b in order]
        self.canvas = {k[:-8] if k.endswith(".dc.html") else k: v for k, v in boards.items()}
        self.map = gmap.get("boards", {})
        self.unmapped = gmap.get("unmapped", {})
        self.gaps = gmap.get("gaps", {})
        self.defaults = gmap.get("defaults", {})
        self.entries = []        # one dict per app render
        self.board_rows = []     # one dict per board that appears in the gallery (mapped or not)
        for name in self.canvas_order:
            if name not in self.canvas:
                continue
            if only and not any(o in name for o in only):
                continue
            spec = self.canvas[name]
            renders = self.map.get(name)
            reason = self.unmapped.get(name)
            if renders is None and reason is None:
                continue
            tier0, family0 = derive_tier(name)
            if renders:
                tier0 = renders[0].get("tier", tier0)
                family0 = renders[0].get("family", family0)
            elif isinstance(reason, dict):
                tier0 = reason.get("tier", tier0)
                family0 = reason.get("family", family0)
                reason = reason.get("reason", "")
            row = {"board": name, "family": family0, "tier": tier0, "title": spec.get("title", ""),
                   "bw": spec.get("w"), "bh": spec.get("h"), "unmapped": reason, "gap": self.gaps.get(name), "renders": []}
            self.board_rows.append(row)
            for i, r in enumerate(renders or []):
                entry = self.make_entry(name, i, len(renders), r, spec, tier0, family0)
                row["renders"].append(entry["id"])
                self.entries.append(entry)
        self.unlisted = [n for n in self.canvas_order if n in self.canvas and n not in self.map and n not in self.unmapped]

    def make_entry(self, board, index, count, r, spec, tier, family):
        reserved = {"tier", "family", "w", "h", "verbs", "label", "frames", "kind", "scale"}
        mount = {k: v for k, v in r.items() if k not in reserved}
        entry_id = board if count == 1 else "%s.%d" % (board, index + 1)
        return {
            "id": entry_id, "board": board, "family": r.get("family", family), "tier": r.get("tier", tier),
            "label": r.get("label", ""), "w": int(r.get("w") or spec.get("w") or TIER_SIZE.get(tier, (1440, 960))[0]),
            "h": int(r.get("h") or spec.get("h") or TIER_SIZE.get(tier, (1440, 960))[1]),
            "kind": r.get("kind", self.defaults.get("kind")), "scale": r.get("scale"),
            "frames": int(r.get("frames", self.defaults.get("frames", 90))),
            "mount": mount, "verbs": list(r.get("verbs", [])),
        }


# ───────────────────────────── board PNGs ─────────────────────────────

def ensure_board_pngs(plan, boards_dir, png_dir, support_js, work_dir, log):
    missing = [r["board"] for r in plan.board_rows if not os.path.exists(os.path.join(png_dir, r["board"] + ".png"))]
    if not missing:
        return []
    shots = os.path.join(HERE, "board_shots.py")
    if not os.path.exists(support_js or ""):
        log("board PNGs missing for %d boards but support.js was not found (--support-js)" % len(missing))
        return missing
    stage = os.path.join(work_dir, "_boards-stage")
    os.makedirs(stage, exist_ok=True)
    for f in os.listdir(boards_dir):
        if f.endswith(".dc.html"):
            dst = os.path.join(stage, f)
            if not os.path.exists(dst) or os.path.getmtime(dst) < os.path.getmtime(os.path.join(boards_dir, f)):
                shutil.copy2(os.path.join(boards_dir, f), dst)
    shutil.copy2(support_js, os.path.join(stage, "support.js"))
    os.makedirs(png_dir, exist_ok=True)
    log("rendering %d missing board PNGs with board_shots.py" % len(missing))
    want = [m + ".dc.html" for m in missing]
    subprocess.run([sys.executable, "-I", shots, stage, png_dir] + want, check=False)
    return [m for m in missing if not os.path.exists(os.path.join(png_dir, m + ".png"))]


# ───────────────────────────── the app renders ─────────────────────────────

def entry_gpu(entry, gpu):
    return entry["mount"].get("gpu", gpu)


def estimate_seconds(entry, old, gpu="hardware"):
    """One entry's expected wall time: its own time in the last results.json when that run used the same gpu, else the per-tier
    guess for that gpu."""
    g = entry_gpu(entry, gpu)
    prev = (old or {}).get(entry["id"])
    if prev and prev.get("ok") and prev.get("seconds") and prev.get("gpu", "warp") == g:
        return float(prev["seconds"]), True
    table = EST_SECONDS.get(g, EST_SECONDS["warp"])
    tier = entry["tier"] if entry["tier"] in table else "Standard"
    secs = table[tier]
    if any(v.startswith("key f11") for v in entry["verbs"]):
        secs += EST_STAGE.get(g, EST_STAGE["warp"])[tier]
    return float(secs), False


def fmt_dur(secs):
    secs = int(round(secs))
    return "%dm%02ds" % (secs // 60, secs % 60) if secs >= 60 else "%ds" % secs


class Progress:
    """The shared run counter: entries done, seconds spent, the estimate left."""

    def __init__(self, todo, est_left, jobs):
        self.todo, self.done, self.est_left, self.jobs = todo, 0, est_left, max(1, jobs)
        self.t0 = time.time()


class Worker(threading.Thread):
    def __init__(self, inspect_mod, exe, theme, work_q, results, app_dir, log, lock, progress, gpu):
        super().__init__(daemon=True)
        self.gpu = gpu
        self.m, self.exe, self.theme, self.q = inspect_mod, exe, theme, work_q
        self.results, self.app_dir, self.log, self.lock = results, app_dir, log, lock
        self.progress = progress
        self.server = None

    def start_server(self):
        try:
            self.server = self.m.Server(self.exe)
        except SystemExit as exc:
            self.server = None
            raise RuntimeError("server did not start: %s" % exc)

    def stop_server(self, kill=False):
        s, self.server = self.server, None
        if s is None:
            return
        try:
            if kill:
                s.proc.kill()
            else:
                s.close()
        except Exception:
            pass

    def send(self, line):
        """One command, one reply, with a watchdog that kills a hung server (the read then ends in SystemExit)."""
        timer = threading.Timer(ENTRY_TIMEOUT, lambda: self.server.proc.kill())
        timer.start()
        try:
            return self.server.send(line)
        finally:
            timer.cancel()

    def run(self):
        while True:
            try:
                entry = self.q.get_nowait()
            except queue.Empty:
                break
            res = self.do_entry(entry)
            res["estimate"] = entry["est"]
            res["gpu"] = entry_gpu(entry, self.gpu)
            p = self.progress
            with self.lock:
                self.results[entry["id"]] = res
                p.done += 1
                p.est_left = max(0.0, p.est_left - entry["est"])
                done, left = p.done, p.est_left / p.jobs
            outcome = ("%dE %dW" % (res["lint"]["error"], res["lint"]["warn"])) if res.get("lint") and res["ok"] else res.get("error", "")[:90]
            if res["ok"] and res.get("warnings"):
                outcome += "  (%d verb warning%s)" % (len(res["warnings"]), "" if len(res["warnings"]) == 1 else "s")
            self.log("[%3d/%d] %-44s %s%-10s %6.1fs   ~%s left" % (done, p.todo, entry["id"], "ok " if res["ok"] else "FAILED ",
                                                                  outcome, res.get("seconds", 0.0), fmt_dur(left)))
        self.stop_server()

    def do_entry(self, e):
        png_rel = "app/%s.png" % e["id"]
        png = os.path.join(self.app_dir, e["id"] + ".png")
        res = {"id": e["id"], "board": e["board"], "family": e["family"], "tier": e["tier"], "label": e["label"],
               "w": e["w"], "h": e["h"], "png": png_rel, "ok": False, "error": "", "warnings": [], "lint": None,
               "findings": [], "mount": e["mount"], "verbs": e["verbs"]}
        t0 = time.time()
        try:
            if self.server is None or self.server.proc.poll() is not None:
                self.stop_server(kill=True)
                self.start_server()
            args = {"w": e["w"], "h": e["h"], "theme": self.theme, "gpu": self.gpu}     # an entry's own gpu (in mount) wins
            if e["kind"]:
                args["kind"] = e["kind"]
            if e["scale"]:
                args["scale"] = e["scale"]
            args.update(e["mount"])
            reply = json.loads(self.send(json.dumps({"cmd": "mount", "args": args, "id": 1})))
            if not reply.get("ok"):
                res["error"] = "mount: %s" % reply.get("error", reply)
                return res
            res["mountTier"] = reply.get("tiers", {}).get("window")
            if reply.get("networkLeaks"):
                res["warnings"].append("networkLeaks=%s" % reply["networkLeaks"])
            if os.path.exists(png):
                os.remove(png)
            os.makedirs(os.path.dirname(png), exist_ok=True)
            cmds = ["frames %d" % e["frames"]] + e["verbs"]
            for line in cmds:
                r = json.loads(self.send(line) or "{}")
                if not r.get("ok", True) and line.split()[0] in ("dialog", "click"):
                    self.send("frames 30")          # a host that is not bound yet: one retry
                    r = json.loads(self.send(line) or "{}")
                if not r.get("ok", True):
                    res["warnings"].append("%s: %s" % (line.split()[0], r.get("error", r)))
            r = json.loads(self.send('{"cmd":"render","args":{"path":%s}}' % json.dumps(png.replace("\\", "/"))))
            if not r.get("ok"):
                res["error"] = "render: %s %s" % (r.get("error", r), r.get("why", ""))
                return res
            lint = json.loads(self.send("lint min=warn limit=60"))
            if lint.get("ok"):
                s = lint.get("summary", {})
                res["lint"] = {"error": s.get("error", 0), "warn": s.get("warn", 0), "info": s.get("info", 0),
                               "tier": lint.get("tier"), "guide": lint.get("guide")}
                for f in lint.get("findings", [])[:FINDINGS_KEPT]:
                    res["findings"].append({"rule": f.get("rule"), "severity": f.get("severity"),
                                            "outcome": f.get("outcome", "fail"), "level": f.get("level", "gate"),
                                            "message": f.get("message"), "keys": f.get("keys", [])[:4]})
            else:
                res["warnings"].append("lint: %s" % lint.get("error", lint))
            res["ok"] = os.path.exists(png)
            if not res["ok"]:
                res["error"] = "render reported ok but no file"
        except SystemExit as exc:
            res["error"] = "server died: %s" % str(exc)[:200]
            self.stop_server(kill=True)
        except Exception as exc:     # a bad reply, a kill by the watchdog
            res["error"] = "%s: %s" % (type(exc).__name__, exc)
            self.stop_server(kill=True)
        finally:
            res["seconds"] = round(time.time() - t0, 1)
            # one fresh server per entry: the play queue, the open panel, the stage and the dialogs all outlive a mount
            self.stop_server()
        return res


def render_app(plan, exe, theme, out_dir, jobs, results, log, reuse, only, gpu="hardware"):
    """Renders the entries (all, or those whose board matches --only); returns (entries run, wall seconds, estimate seconds)."""
    inspect_mod = load_inspect()
    app_dir = os.path.join(out_dir, "app")
    os.makedirs(app_dir, exist_ok=True)
    work_q = queue.Queue()
    todo, est_total, known = [], 0.0, 0
    for e in plan.entries:
        if only and not any(o in e["board"] or o == e["id"] for o in only):
            continue
        old = results.get(e["id"])
        if reuse and old and old.get("ok") and os.path.exists(os.path.join(out_dir, old["png"])):
            continue
        e["est"], own = estimate_seconds(e, results, gpu)
        est_total += e["est"]
        known += own
        todo.append(e)
    jobs = max(1, min(jobs, len(todo) or 1))
    for e in todo:
        work_q.put(e)
    log("rendering %d of %d app entries with %d server%s" % (len(todo), len(plan.entries), jobs, "" if jobs == 1 else "s"))
    table, stage = EST_SECONDS.get(gpu, EST_SECONDS["warp"]), EST_STAGE.get(gpu, EST_STAGE["warp"])
    log("estimate: ~%s wall for %d entries on %d server%s (%d timed by their own last run on this gpu in results.json, the rest "
        "guessed per tier for gpu=%s: %s; a stage entry +%s)" % (
            fmt_dur(est_total / jobs), len(todo), jobs, "" if jobs == 1 else "s", known, gpu,
            ", ".join("%s %ds" % (t, s) for t, s in table.items()), "/".join("%d" % s for s in stage.values()) + "s"))
    lock = threading.Lock()
    progress = Progress(len(todo), est_total, jobs)
    workers = [Worker(inspect_mod, exe, theme, work_q, results, app_dir, log, lock, progress, gpu) for _ in range(jobs)]
    for w in workers:
        w.start()
    try:
        for w in workers:
            while w.is_alive():
                w.join(timeout=1)
    except KeyboardInterrupt:
        log("interrupted; stopping servers")
        for w in workers:
            w.stop_server(kill=True)
        raise
    return todo, time.time() - progress.t0, est_total / jobs


# ───────────────────────────── html ─────────────────────────────

CSS = """
:root{--z:1}
body{margin:0;background:#161616;color:#ddd;font:13px/1.45 'Segoe UI',system-ui,sans-serif}
header{position:sticky;top:0;z-index:5;background:#101010;border-bottom:1px solid #333;padding:8px 18px;display:flex;gap:18px;align-items:center;flex-wrap:wrap}
header h1{font-size:15px;margin:0;font-weight:600}
header a{color:#8ab4f8;text-decoration:none}
header label{margin-left:auto;color:#aaa}
main{padding:14px 18px 80px}
h2{font-size:14px;margin:26px 0 6px;color:#fff;border-bottom:1px solid #2c2c2c;padding-bottom:3px}
h2 small{color:#888;font-weight:400;margin-left:8px}
.row{display:flex;gap:14px;align-items:flex-start;flex-wrap:wrap;margin-bottom:8px}
figure{margin:0;background:#1f1f1f;border:1px solid #2e2e2e;padding:6px}
figure img{display:block;width:calc(var(--w) * var(--s) * var(--z) * 1px);height:auto;background:#000}
figcaption{margin-top:5px;color:#aaa;font-size:12px;max-width:calc(var(--w) * var(--s) * var(--z) * 1px);min-width:180px}
figcaption b{color:#eee;font-weight:600}
.chip{display:inline-block;border-radius:9px;padding:0 7px;margin-right:4px;font-size:11px;background:#2a2a2a}
.chip.e{background:#7a2222;color:#fff}.chip.w{background:#6f5a12;color:#fff}.chip.ok{background:#1f4d2a;color:#fff}.chip.bad{background:#7a2222;color:#fff}.chip.na{background:#333;color:#aaa}
details{margin-top:3px}summary{cursor:pointer;color:#8ab4f8}
ul.f{margin:3px 0 0;padding-left:16px;font-size:11.5px}ul.f li{margin:1px 0}
.sev-error{color:#ff8a8a}.sev-warn{color:#e8c25a}.sev-info{color:#9ab}
.missing{width:360px;min-height:90px;display:flex;align-items:center;justify-content:center;text-align:center;color:#888;border:1px dashed #444;padding:10px}
table{border-collapse:collapse;margin:6px 0 18px}td,th{border:1px solid #2e2e2e;padding:4px 8px;text-align:left;vertical-align:top}th{background:#202020;color:#bbb;font-weight:600}
td.c{text-align:center;min-width:92px}
a{color:#8ab4f8}
code{color:#c9b3ff}
"""

JS = """
function zoom(v){document.documentElement.style.setProperty('--z',v);var o=document.getElementById('zv');if(o)o.textContent=(+v).toFixed(2)+'x'}
"""


def page(title, body, back=False):
    nav = '<a href="index.html">index</a>' if back else ""
    return ("<!doctype html><meta charset=utf-8><title>%s</title><style>%s</style><script>%s</script>"
            "<header><h1>%s</h1>%s<label>zoom <input type=range min=0.3 max=3 step=0.05 value=1 oninput='zoom(this.value)'> <span id=zv>1.00x</span></label></header>"
            "<main>%s</main>") % (esc(title), CSS, JS, esc(title), nav, body)


def chips(res):
    if not res:
        return '<span class="chip na">not rendered</span>'
    if not res["ok"]:
        return '<span class="chip bad">FAILED</span>'
    l = res.get("lint")
    if not l:
        return '<span class="chip na">no lint</span>'
    out = []
    out.append('<span class="chip e">%d error</span>' % l["error"] if l["error"] else '<span class="chip ok">0 error</span>')
    out.append('<span class="chip w">%d warn</span>' % l["warn"] if l["warn"] else '<span class="chip ok">0 warn</span>')
    return "".join(out)


def fig_board(row, out_dir, scale):
    png = os.path.join(out_dir, "boards", row["board"] + ".png")
    size = png_size(png)
    cap = "<b>board</b> %s<br>%s" % (esc(row["board"]), esc(row["title"]))
    if not size:
        return '<figure><div class="missing">board PNG missing<br>%s</div><figcaption>%s</figcaption></figure>' % (esc(row["board"]), cap)
    return ('<figure style="--w:%d;--s:%.4f"><a href="boards/%s.png" target=_blank><img loading=lazy src="boards/%s.png"></a>'
            '<figcaption>%s<br>%dx%d</figcaption></figure>') % (size[0], scale, esc(row["board"]), esc(row["board"]), cap, size[0], size[1])


def fig_app(res, out_dir, scale, entry):
    label = esc(entry["label"] or "")
    head = "<b>app</b> %s%s" % (esc(entry["id"]), (" &middot; " + label) if label else "")
    if not res or not res["ok"]:
        err = esc((res or {}).get("error", "not rendered"))
        return ('<figure><div class="missing">app render failed<br><small>%s</small></div><figcaption>%s<br>%s</figcaption></figure>'
                % (err, head, chips(res)))
    size = png_size(os.path.join(out_dir, res["png"])) or (res["w"], res["h"])
    parts = ['<figure style="--w:%d;--s:%.4f"><a href="%s" target=_blank><img loading=lazy src="%s"></a><figcaption>%s<br>%dx%d (%s) %.1fs %s'
             % (size[0], scale, esc(res["png"]), esc(res["png"]), head, size[0], size[1], esc(res.get("mountTier") or ""),
                res.get("seconds", 0.0), chips(res))]
    for w in res.get("warnings", []):
        parts.append('<br><span class="sev-warn">verb: %s</span>' % esc(w))
    fl = res.get("findings", [])
    if fl:
        parts.append("<details><summary>lint (%d)</summary><ul class=f>" % len(fl))
        for f in fl:
            keys = (" &lt;" + esc(", ".join(f["keys"])) + "&gt;") if f.get("keys") else ""
            tags = [t for t in (f.get("outcome", "fail"), f.get("level", "gate")) if t not in ("fail", "gate")]
            tag = (" (" + esc(", ".join(tags)) + ")") if tags else ""
            parts.append('<li class="sev-%s">%s%s: %s%s</li>' % (esc(f["severity"]), esc(f["rule"]), tag, esc(f["message"]), keys))
        parts.append("</ul></details>")
    parts.append("</figcaption></figure>")
    return "".join(parts)


def tier_rank(t):
    return TIER_ORDER.index(t) if t in TIER_ORDER else len(TIER_ORDER)


def build_html(plan, results, out_dir, theme):
    families = {}
    for row in plan.board_rows:
        families.setdefault(row["family"], []).append(row)
    entries = {e["id"]: e for e in plan.entries}
    fam_pages = {}
    for fam, rows in families.items():
        rows = sorted(rows, key=lambda r: (tier_rank(r["tier"]), plan.canvas_order.index(r["board"])))
        body = []
        for row in rows:
            ids = row["renders"]
            ress = [results.get(i) for i in ids]
            widths = [row["bw"] or 0] + [(r["w"] if r else entries[i]["w"]) for r, i in zip(ress, ids)]
            png = png_size(os.path.join(out_dir, "boards", row["board"] + ".png"))
            if png:
                widths[0] = png[0]
            scale = min(1.0, 700.0 / max(1, max(widths)))
            body.append('<h2 id="%s">%s <small>%s &middot; %s</small></h2><div class="row">' % (
                esc(row["board"]), esc(row["tier"]), esc(row["board"]), esc(row["title"])))
            body.append(fig_board(row, out_dir, scale))
            if row["unmapped"]:
                body.append('<figure><div class="missing">not mapped to an app render<br><small>%s</small></div></figure>' % esc(row["unmapped"]))
            if row.get("gap"):
                body.append('<figure><div class="missing">part of this board no render covers<br><small>%s</small></div></figure>' % esc(row["gap"]))
            for i, r in zip(ids, ress):
                body.append(fig_app(r, out_dir, scale, entries[i]))
            body.append("</div>")
        fn = "family-%s.html" % slug(fam)
        fam_pages[fam] = fn
        write_text(os.path.join(out_dir, fn), page("%s: %s" % (theme, fam), "".join(body), back=True))
    write_text(os.path.join(out_dir, "index.html"), build_index(plan, results, families, fam_pages, theme))
    return fam_pages


def build_index(plan, results, families, fam_pages, theme):
    n_ok = sum(1 for e in plan.entries if results.get(e["id"], {}).get("ok"))
    failed = [e for e in plan.entries if e["id"] in results and not results[e["id"]]["ok"]]
    missing = [e for e in plan.entries if e["id"] not in results]
    tot_e = sum(results[e["id"]]["lint"]["error"] for e in plan.entries if results.get(e["id"], {}).get("ok") and results[e["id"]].get("lint"))
    tot_w = sum(results[e["id"]]["lint"]["warn"] for e in plan.entries if results.get(e["id"], {}).get("ok") and results[e["id"]].get("lint"))
    unmapped = [r for r in plan.board_rows if r["unmapped"]]
    body = ["<p>%s gallery: <b>%d</b> boards in %d families, <b>%d</b> app renders (%d ok, %d failed, %d not run), lint over all renders: "
            "<span class='chip e'>%d error</span><span class='chip w'>%d warn</span> &middot; generated %s</p>"
            % (esc(theme), len(plan.board_rows), len(families), len(plan.entries), n_ok, len(failed), len(missing), tot_e, tot_w,
               time.strftime("%Y-%m-%d %H:%M"))]
    body.append("<table><tr><th>family</th>" + "".join("<th>%s</th>" % t for t in TIER_ORDER) + "</tr>")
    first = {}
    for r in plan.board_rows:
        first.setdefault(r["family"], plan.canvas_order.index(r["board"]))
    for fam in sorted(families, key=lambda f: first[f]):
        cells = []
        by_tier = {}
        for r in families[fam]:
            by_tier.setdefault(r["tier"], []).append(r)
        for t in TIER_ORDER:
            rows = by_tier.get(t)
            if not rows:
                cells.append('<td class="c"><span class="chip na">-</span></td>')
                continue
            inner = []
            for r in rows:
                if not r["renders"]:
                    inner.append('<a href="%s#%s"><span class="chip na">board only</span></a>' % (fam_pages[fam], esc(r["board"])))
                    continue
                for i in r["renders"]:
                    inner.append('<a href="%s#%s">%s</a>' % (fam_pages[fam], esc(r["board"]), chips(results.get(i))))
            cells.append('<td class="c">%s</td>' % "<br>".join(inner))
        body.append('<tr><td><a href="%s">%s</a></td>%s</tr>' % (fam_pages[fam], esc(fam), "".join(cells)))
    body.append("</table>")
    body.append("<h2>Failures (%d)</h2>" % len(failed))
    if failed:
        body.append("<table><tr><th>render</th><th>error</th></tr>")
        for e in failed:
            body.append("<tr><td>%s</td><td>%s</td></tr>" % (esc(e["id"]), esc(results[e["id"]]["error"])))
        body.append("</table>")
    else:
        body.append("<p>none</p>")
    body.append("<h2>Boards without an app render (%d)</h2><table><tr><th>board</th><th>family / tier</th><th>why</th></tr>" % len(unmapped))
    for r in unmapped:
        body.append("<tr><td><a href='%s#%s'>%s</a></td><td>%s / %s</td><td>%s</td></tr>" % (
            fam_pages[r["family"]], esc(r["board"]), esc(r["board"]), esc(r["family"]), esc(r["tier"]), esc(r["unmapped"])))
    body.append("</table>")
    gapped = [r for r in plan.board_rows if r.get("gap")]
    if gapped:
        body.append("<h2>Parts of mapped boards no render covers (%d)</h2><table><tr><th>board</th><th>what is missing</th></tr>" % len(gapped))
        for r in gapped:
            body.append("<tr><td><a href='%s#%s'>%s</a></td><td>%s</td></tr>" % (fam_pages[r["family"]], esc(r["board"]), esc(r["board"]), esc(r["gap"])))
        body.append("</table>")
    if plan.unlisted:
        body.append("<h2>On the canvas but in neither list (%d)</h2><p>%s</p>" % (len(plan.unlisted), esc(", ".join(plan.unlisted))))
    return page("%s: boards vs app" % theme, "".join(body))


# ───────────────────────────── contact sheets ─────────────────────────────

def sheet_font(size):
    from PIL import ImageFont
    for name in ("segoeui.ttf", "arial.ttf", "DejaVuSans.ttf"):
        try:
            return ImageFont.truetype(name, size)
        except OSError:
            continue
    return ImageFont.load_default()


def fit_text(draw, text, font, width):
    """`text` cut with an ellipsis to fit `width` px."""
    if draw.textlength(text, font=font) <= width:
        return text
    while text and draw.textlength(text + "...", font=font) > width:
        text = text[:-1]
    return text + "..."


def sheet_group(row, results, entries, out_dir):
    """A board row -> its group: [(caption, png path or None, (w, h), is_board)], the board first, then each render."""
    bpng = os.path.join(out_dir, "boards", row["board"] + ".png")
    bsize = png_size(bpng) or (row["bw"] or 1440, row["bh"] or 960)
    group = [("board %s  %dx%d" % (row["board"], bsize[0], bsize[1]), bpng if os.path.exists(bpng) else None, bsize, True)]
    for i in row["renders"]:
        e, res = entries[i], results.get(i)
        suffix = i[len(row["board"]):]                     # "" or ".2"
        label = "app%s %s" % (suffix, e["label"])
        if not res:
            group.append((label + "  [not rendered]", None, (e["w"], e["h"]), False))
            continue
        if not res["ok"]:
            group.append((label + "  [FAILED: %s]" % res.get("error", ""), None, (e["w"], e["h"]), False))
            continue
        lint = res.get("lint") or {}
        tail = "  %dx%d  %dE %dW  %.0fs" % (res["w"], res["h"], lint.get("error", 0), lint.get("warn", 0), res.get("seconds", 0.0))
        if res.get("warnings"):
            tail += "  VERB WARNING: " + "; ".join(res["warnings"])
        png = os.path.join(out_dir, res["png"])
        group.append((label + tail, png if os.path.exists(png) else None, png_size(png) or (e["w"], e["h"]), False))
    return group


def group_layout(group, head_h, pad, cap_h):
    """(columns, scale) for a group drawn as a grid of equal cells (the largest image's size) at one scale: side by side when
    that keeps the scale at SHEET_MIN_SCALE or above, else the column count with the largest scale (ties: more columns). A row
    of cells is at most SHEET_ROW_H tall and the group at most SHEET_MAX on either side."""
    cw = max(g[2][0] for g in group)
    ch = max(g[2][1] for g in group)
    n = len(group)
    best = None
    for c in range(n, 0, -1):
        rows = -(-n // c)
        s = min(1.0, SHEET_ROW_H / ch, (SHEET_MAX - pad * (c + 1)) / (c * cw),
                (SHEET_MAX - head_h - rows * (cap_h + pad)) / (rows * ch))
        if s >= SHEET_MIN_SCALE:
            return c, s
        if best is None or s > best[1] + 1e-9:
            best = (c, s)
    return best


def draw_group(group, header, gap_note, fonts):
    """One group -> an RGB image: a header line (and the board's uncovered part), then the board and its renders in a grid of
    equal cells at one scale, each with its caption above."""
    from PIL import Image, ImageDraw
    f_head, f_cap = fonts
    pad, cap_h, head_h = 10, 22, 26 + (20 if gap_note else 0)
    cols, s = group_layout(group, head_h, pad, cap_h)
    cw = max(1, int(max(g[2][0] for g in group) * s))
    ch = max(1, int(max(g[2][1] for g in group) * s))
    rows = -(-len(group) // cols)
    used_w = max(int(g[2][0] * s) for g in group) if cols == 1 else cw
    W = cols * used_w + pad * (cols + 1)
    H = head_h + rows * (cap_h + ch + pad)
    img = Image.new("RGB", (W, H), (34, 34, 34))
    d = ImageDraw.Draw(img)
    d.text((pad, 4), fit_text(d, header, f_head, W - 2 * pad), font=f_head, fill=(255, 255, 255))
    if gap_note:
        d.text((pad, 26), fit_text(d, "not covered: " + gap_note, f_cap, W - 2 * pad), font=f_cap, fill=(255, 190, 90))
    for k, (cap, png, size, is_board) in enumerate(group):
        x = pad + (k % cols) * (used_w + pad)
        y = head_h + (k // cols) * (cap_h + ch + pad)
        w, h = max(1, int(size[0] * s)), max(1, int(size[1] * s))
        warn = "FAILED" in cap or "VERB WARNING" in cap or "not rendered" in cap
        color = (255, 120, 120) if warn else ((150, 200, 255) if is_board else (200, 255, 170))
        d.text((x, y + 2), fit_text(d, cap, f_cap, max(used_w, 120)), font=f_cap, fill=color)
        y += cap_h
        if png:
            try:
                with Image.open(png) as im:
                    img.paste(im.convert("RGB").resize((w, h), Image.LANCZOS), (x, y))
            except OSError:
                png = None
        if not png:
            d.rectangle([x, y, x + w - 1, y + h - 1], outline=(255, 120, 120), fill=(60, 30, 30))
            d.text((x + 6, y + 6), "no image", font=f_cap, fill=(255, 120, 120))
    return img


def build_sheets(plan, results, out_dir, theme, families_wanted, log):
    """Per family: every board row with renders, its groups packed left to right and top to bottom into PNGs at most SHEET_MAX
    on a side. Returns the paths written."""
    try:
        from PIL import Image
    except ImportError:
        log("contact sheets skipped: PIL (Pillow) is not installed for this python")
        return []
    fonts = (sheet_font(17), sheet_font(14))
    entries = {e["id"]: e for e in plan.entries}
    sheet_dir = os.path.join(out_dir, "sheets")
    os.makedirs(sheet_dir, exist_ok=True)
    families = {}
    for row in plan.board_rows:
        if row["renders"] and row["family"] in families_wanted:
            families.setdefault(row["family"], []).append(row)
    written = []
    for fam, rows in families.items():
        if not any(results.get(i) for r in rows for i in r["renders"]):
            continue
        prefix = "%s-%s-" % (theme, slug(fam))
        mine = re.compile(re.escape(prefix) + r"\d+\.png$")     # this family's sheets only ("nowplaying-" vs "nowplaying-card-")
        for old in os.listdir(sheet_dir):
            if mine.match(old):
                os.remove(os.path.join(sheet_dir, old))
        rows = sorted(rows, key=lambda r: (tier_rank(r["tier"]), plan.canvas_order.index(r["board"])))
        tiles = []
        for row in rows:
            if not any(results.get(i) for i in row["renders"]):
                continue                                    # nothing of this board rendered yet (a --only run elsewhere)
            header = "%s  %s  %s" % (row["tier"], row["board"], row["title"] or "")
            tiles.append(draw_group(sheet_group(row, results, entries, out_dir), header, row.get("gap"), fonts))
        # shelf packing: left to right while a row fits SHEET_MAX, a new sheet when the next shelf would pass it
        sheets, shelf, shelves, height = [], [], [], 0
        def flush_shelf():
            nonlocal shelf, height
            if shelf:
                shelves.append(shelf)
                height += max(t.height for t in shelf) + 6
                shelf = []
        for t in tiles:
            if shelf and sum(x.width + 6 for x in shelf) + t.width > SHEET_MAX:
                flush_shelf()
            if not shelf and shelves and height + t.height > SHEET_MAX:
                sheets.append(shelves)
                shelves, height = [], 0
            shelf.append(t)
        flush_shelf()
        if shelves:
            sheets.append(shelves)
        for n, sh in enumerate(sheets, 1):
            W = min(SHEET_MAX, max(sum(t.width + 6 for t in s) for s in sh))
            H = min(SHEET_MAX, sum(max(t.height for t in s) + 6 for s in sh))
            img = Image.new("RGB", (W, H), (12, 12, 12))
            y = 0
            for s in sh:
                x = 0
                for t in s:
                    img.paste(t, (x, y))
                    x += t.width + 6
                y += max(t.height for t in s) + 6
            path = os.path.normpath(os.path.join(sheet_dir, "%s%d.png" % (prefix, n)))
            img.save(path, optimize=True)
            written.append(path)
    return written


# ───────────────────────────── main ─────────────────────────────

def main():
    ap = argparse.ArgumentParser(description="Board-vs-app gallery for a theme.")
    ap.add_argument("--theme", required=True, help="zune | winui | classic ...")
    ap.add_argument("--map", help="mount map (default tools/themes/gallery-map.<theme>.json)")
    ap.add_argument("--boards", help="boards project dir (default docs/themes/boards/<theme>/project)")
    ap.add_argument("--board-png-dir", help=r"default C:\WAVEE\scratch\board-png-<theme>")
    ap.add_argument("--support-js", help="the design canvas runtime board_shots.py needs next to the boards")
    ap.add_argument("--exe", help="Wavee.exe (default: the newest build under src/apps/Wavee/bin)")
    ap.add_argument("--out", help=r"default C:\WAVEE\scratch\gallery\<theme>")
    ap.add_argument("--jobs", type=int, default=3, help="parallel --inspect servers (max 4)")
    ap.add_argument("--gpu", choices=["hardware", "warp"], default="hardware",
                    help="the mount's gpu= (default hardware: same lint and pixels as warp, a fraction of the CPU); an entry's own gpu wins")
    ap.add_argument("--only", nargs="*", default=[], help="render only boards whose name contains one of these, or the entries with exactly these ids (Z-Artist.2: the "
                         "second render of Z-Artist); the pages still cover everything")
    ap.add_argument("--reuse", action="store_true", help="skip entries whose render is already in results.json")
    ap.add_argument("--html-only", action="store_true", help="rebuild the pages and the contact sheets from results.json, render nothing")
    ap.add_argument("--no-sheets", action="store_true", help="write no contact sheets")
    ap.add_argument("--no-board-png", action="store_true", help="do not render missing board PNGs")
    args = ap.parse_args()

    theme = args.theme
    map_path = args.map or os.path.join(HERE, "gallery-map.%s.json" % theme)
    boards = args.boards or os.path.join(ROOT, "docs", "themes", "boards", theme, "project")
    png_dir = args.board_png_dir or r"C:\WAVEE\scratch\board-png-%s" % theme
    out = args.out or r"C:\WAVEE\scratch\gallery\%s" % theme
    jobs = max(1, min(4, args.jobs))
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

    def log(msg):
        print(msg, flush=True)

    plan = Plan(read_json(os.path.join(boards, "canvas.json")), read_json(map_path), [])   # the pages always cover the whole map; --only limits rendering
    os.makedirs(out, exist_ok=True)
    results_path = os.path.join(out, "results.json")
    results = read_json(results_path) if os.path.exists(results_path) else {}
    log("%s: %d boards, %d app entries, %d unmapped, %d unlisted" % (theme, len(plan.board_rows), len(plan.entries),
                                                                  sum(1 for r in plan.board_rows if r["unmapped"]), len(plan.unlisted)))

    if not args.html_only:
        if not args.no_board_png:
            support = args.support_js or next((p for p in SUPPORT_JS_CANDIDATES if os.path.exists(p)), None)
            still = ensure_board_pngs(plan, boards, png_dir, support, out, log)
            if still:
                log("board PNGs still missing: " + ", ".join(still))
        exe = args.exe
        if not exe:
            exe = load_inspect().find_exe(None)
        log("exe: " + exe)
        log("gpu: %s on every mount (--gpu %s overrides; an entry's own gpu wins)" % (args.gpu, "warp" if args.gpu == "hardware" else "hardware"))
        ran, wall, est = render_app(plan, exe, theme, out, jobs, results, log, args.reuse, args.only, args.gpu)
        ids = {e["id"] for e in plan.entries}
        for stale in [k for k in results if k not in ids]:      # an id the map no longer has (a renamed or removed render)
            del results[stale]
        write_text(results_path, json.dumps(results, indent=1))
        if ran:
            log("rendered %d entries in %s wall (estimate was ~%s; mean %.1fs per entry)" % (
                len(ran), fmt_dur(wall), fmt_dur(est), sum(results[e["id"]].get("seconds", 0.0) for e in ran) / len(ran)))

    os.makedirs(os.path.join(out, "boards"), exist_ok=True)
    for r in plan.board_rows:
        src = os.path.join(png_dir, r["board"] + ".png")
        dst = os.path.join(out, "boards", r["board"] + ".png")
        if os.path.exists(src) and (not os.path.exists(dst) or os.path.getmtime(dst) < os.path.getmtime(src)):
            shutil.copy2(src, dst)
    fam_pages = build_html(plan, results, out, theme)
    mine = [results[e["id"]] for e in plan.entries if e["id"] in results]
    ok = sum(1 for r in mine if r["ok"])
    log("")
    log("index:    " + os.path.join(out, "index.html"))
    log("families: %d   renders: %d ok, %d failed (of %d entries)" % (len(fam_pages), ok, len(mine) - ok, len(plan.entries)))
    for r in mine:
        if not r["ok"]:
            log("  FAILED %s: %s" % (r["id"], r["error"][:160]))
    um = [r["board"] for r in plan.board_rows if r["unmapped"]]
    log("unmapped boards (%d): %s" % (len(um), ", ".join(um)))
    gaps = [r["board"] for r in plan.board_rows if r.get("gap")]
    if gaps:
        log("boards with a part no render covers (%d): %s" % (len(gaps), ", ".join(gaps)))
    if plan.unlisted:
        log("on canvas but in neither list (%d): %s" % (len(plan.unlisted), ", ".join(plan.unlisted)))
    if not args.no_sheets:
        wanted = {r["family"] for r in plan.board_rows
                  if not args.only or any(o in r["board"] or o in r["renders"] for o in args.only)}
        sheets = build_sheets(plan, results, out, theme, wanted, log)
        if sheets:
            log("contact sheets (%d, board | app at one scale, at most %d px a side):" % (len(sheets), SHEET_MAX))
            for path in sheets:
                log("  " + path)


if __name__ == "__main__":
    main()
