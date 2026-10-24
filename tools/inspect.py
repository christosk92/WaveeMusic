"""inspect.py - a tiny client for `Wavee.exe --inspect` (docs/guide/agent-inspect.md).

Starts the server (stdin/stdout, or a named pipe with --pipe), waits for its `ready` line, sends each command as one line
and prints the one reply line it gets back. Standard library only; runs under `python -I`.

    python -I tools/inspect.py -c "mount w=400 h=860 theme=zune" -c "tiers" -c "dump root=zune-band keysOnly=true"
    python -I tools/inspect.py -f audit.jsonl --pretty
    python -I tools/inspect.py --findings -c "mount w=400 h=860 theme=zune" -c "lint min=warn"
    python -I tools/inspect.py --trace -c "mount w=1440 h=900 theme=zune" -c "goto album:spotify:album:x settle=false trace=90"
    python -I tools/inspect.py --gate error -f lint.txt   # exit 1 on a failed gate-rule lint error, a jump or a rebuild; 2 on any ok:false or no lint
    python -I tools/inspect.py --gate warn --gate-todo -f lint.txt   # the todo rules (align, space, placeholder, keys, duplicate text, inkless targets) count too
    python -I tools/inspect.py --findings --gate error -c "mount w=1920 h=960 theme=zune route=home" -c "resize 1904 960" -c "crossing from=1904x960 to=1888x960"
    python -I tools/inspect.py                      # interactive: type commands, Ctrl+Z / Ctrl+D to end

Commands are the server's own grammar: a JSON object {"cmd","args","id"} or a text line `verb key=value k="a b" k=[a,b] ...`
(`help`, `help verb=<name>` and `help schema=json` describe every verb and arg).
"""

import argparse
import glob
import json
import os
import subprocess
import sys
import threading
import time


def find_exe(explicit):
    if explicit:
        return explicit
    here = os.path.dirname(os.path.abspath(__file__))
    root = os.path.dirname(here)
    pattern = os.path.join(root, "src", "apps", "Wavee", "bin", "*", "*", "Wavee.exe")
    found = sorted(glob.glob(pattern), key=os.path.getmtime, reverse=True)
    if not found:
        sys.exit("inspect.py: no Wavee.exe under src/apps/Wavee/bin (build it, or pass --exe)")
    return found[0]


class Server:
    def __init__(self, exe, pipe=None, profile=None, text=None, extra=None):
        argv = [exe, "--inspect"] + list(extra or [])
        if pipe:
            argv += ["--pipe", pipe]
        if profile:
            argv += ["--profile", profile]
        if text:
            argv += ["--text", text]
        self.proc = subprocess.Popen(argv, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                     bufsize=0)
        self.out = self.proc.stdout
        self.inp = self.proc.stdin
        self.pipe = None
        # The server writes diagnostics to stderr; an undrained pipe fills up and blocks the UI thread.
        self.err = bytearray()
        threading.Thread(target=self._drain_stderr, daemon=True).start()
        if pipe:
            line = self._read_line()   # {"kind":"listening","pipe":"\\\\.\\pipe\\name"}
            path = json.loads(line)["pipe"]
            for _ in range(100):
                try:
                    self.pipe = open(path, "r+b", buffering=0)
                    break
                except OSError:
                    time.sleep(0.05)
            if self.pipe is None:
                sys.exit("inspect.py: could not open " + path)
            self.out = self.pipe
            self.inp = self.pipe
        self.ready = json.loads(self._read_line())
        if self.ready.get("kind") != "ready":
            sys.exit("inspect.py: the server did not come up: " + json.dumps(self.ready))

    def _drain_stderr(self):
        try:
            for chunk in iter(lambda: self.proc.stderr.read(4096), b""):
                self.err += chunk
                del self.err[:-65536]
        except (OSError, ValueError):
            pass

    def _read_line(self):
        buf = bytearray()
        while True:
            ch = self.out.read(1)
            if not ch:
                if self.proc.poll() is None:
                    try:
                        self.proc.wait(timeout=2)
                    except subprocess.TimeoutExpired:
                        pass
                err = bytes(self.err).decode("utf-8", "replace") if self.proc.poll() is not None else ""
                sys.exit("inspect.py: the server closed the stream. " + err)
            if ch == b"\n":
                return buf.decode("utf-8").rstrip("\r")
            buf += ch

    def send(self, line):
        line = line.strip()
        if not line or line.startswith("#"):
            return None
        self.inp.write((line + "\n").encode("utf-8"))
        self.inp.flush()
        while True:
            reply = self._read_line()
            obj = _object(reply)
            if obj is None or "kind" not in obj or "cmd" in obj:
                return reply
            # An event line ({"kind":"bye"}, {"kind":"fault"}) is not this command's reply.
            if obj.get("kind") == "bye":
                self.ended = True
                sys.exit("inspect.py: the server ended before it replied to: " + line)
            sys.stderr.write("inspect.py: event: " + reply + "\n")

    ended = False   # the server ran `quit` (or said bye): nothing more can be sent

    def close(self):
        if not self.ended and self.proc.poll() is None:
            try:
                self.send('{"cmd":"quit"}')
            except (SystemExit, OSError):
                pass
        try:
            self.proc.wait(timeout=15)
        except subprocess.TimeoutExpired:
            self.proc.kill()


def _object(line):
    """The JSON object on a line, or None."""
    try:
        obj = json.loads(line)
    except ValueError:
        return None
    return obj if isinstance(obj, dict) else None


def _tiers(t):
    return "%s/%s/%s" % (t.get("window"), t.get("height"), t.get("content")) if isinstance(t, dict) else "?"


def crossing_lines(obj):
    """A crossing reply's head: the counts first (what the gate reads), then the remounts and the topmost moved nodes."""
    c = obj.get("counts", {})
    ctl = obj.get("control")
    if isinstance(ctl, dict):
        control = "over a control step from %sx%s (%s snapped, %s jumped there)" % (
            ctl["from"]["w"], ctl["from"]["h"], ctl.get("snapped"), ctl.get("jumped"))
    elif ctl == "off":
        control = "control=false: every move counts"
    else:
        control = "no control step: every move counts"
    lines = ["crossing %sx%s -> %sx%s %s -> %s (content committed: %s): EXCESS %s snapped, %s jumped %s; "
             "remounted %s outside a swap family, %s inside; all %s snapped, %s jumped, %s glided, %s unpainted; vanished %s, appeared %s" % (
                 obj["from"]["w"], obj["from"]["h"], obj["to"]["w"], obj["to"]["h"], _tiers(obj.get("tierBefore")), _tiers(obj.get("tierSettled")),
                 obj.get("contentCommitted"), c.get("excessSnapped", c.get("snapped")), c.get("excessJumped", c.get("jumped")), control,
                 c.get("remountedOutside", c.get("remounted")), c.get("remountedSwap", 0), c.get("snapped"), c.get("jumped"), c.get("glided"),
                 c.get("unpainted", 0), c.get("vanished"), c.get("appeared"))]
    if "late" in c or "excessOffscreen" in c:
        lines.append("  weights: %s of the excess moved wholly off the screen (below the fold, scrolled away); %s part%s reflowed late "
                     "(after the commit frame, nothing easing it; --gate-todo counts them)" % (
                         c.get("excessOffscreen", 0), c.get("late", 0), "" if c.get("late") == 1 else "s"))
        for item in obj.get("late", [])[:8]:
            lines.append("  late      %s  frame %s after the commit, d=%s" % (item["key"], item.get("frame"), item.get("d")))
    if obj.get("controlNote"):
        lines.append("  note: " + obj["controlNote"])
    if _tiers(obj.get("tierBefore")) == _tiers(obj.get("tierSettled")):
        lines.append("  note: the tiers before and after are the same: this crossing crossed no edge")
    for item in obj.get("remounted", []):
        fam = item.get("family")
        lines.append("  remounted %s  %s" % (item["key"], "(swap family %s)" % fam if fam else "(outside any swap family: a rebuilt part)"))
    for item in obj.get("vanished", []):
        lines.append("  vanished  %s" % item["key"])
    for kind in ("snapped", "jumped"):
        for item in obj.get(kind, []):
            under = item.get("under")
            lines.append("  %-9s %s  d=(%s, %s, %s, %s)%s%s" % (
                kind, item["key"], item["dx"], item["dy"], item["dw"], item["dh"],
                "  %s node%s under it (%s excess)" % (under, "" if under == 1 else "s", item.get("underExcess")) if under is not None else "",
                "" if item.get("excess", True) else "  [the control moved it too]"))
    return lines


def _listed_counts(findings):
    """What --findings prints, per tag: gate fails by severity, then todo fails, reviews, reports and suppressions."""
    counts = {}
    for f in findings:
        outcome, level = f.get("outcome", "fail"), f.get("level", "gate")
        cat = outcome if outcome != "fail" else level
        counts[(cat, f.get("severity", "info"))] = counts.get((cat, f.get("severity", "info")), 0) + 1
    return counts


def lint_head(lint):
    """The lint head line: the gate's own totals (every fail, listed or not), then exactly what the lines below print."""
    size = lint.get("size", {})
    fails = lint.get("fails")
    if isinstance(fails, dict):
        g = fails.get("gate", {})
        gate = "gate %s error, %s warn, %s info" % (g.get("error", 0), g.get("warn", 0), g.get("info", 0))
    else:
        s = lint.get("summary", {})
        gate = "gate %s error, %s warn, %s info (an older server: counted at or above min)" % (s.get("error", 0), s.get("warn", 0), s.get("info", 0))
    listed = _listed_counts(lint.get("findings", []))
    parts = []
    for cat in ("gate", "todo", "review", "report", "suppressed"):
        for sev in ("error", "warn", "info"):
            n = listed.get((cat, sev), 0)
            if n:
                parts.append("%d %s%s" % (n, sev, "" if cat == "gate" else " " + cat))
    shown = sum(listed.values())
    route = lint.get("route", "")
    head = "lint %s %s %sx%s%s: %s; %s line%s below%s; %s nodes%s" % (
        lint.get("theme", ""), lint.get("tier", ""), size.get("w"), size.get("h"), " [" + route + "]" if route else "",
        gate, shown, "" if shown == 1 else "s", ": " + ", ".join(parts) if parts else "", lint.get("summary", {}).get("nodes", 0),
        "" if lint.get("guide", True) else " (no theme guide: gutter and right-edge not checked)")
    if lint.get("narrowed"):
        head += "\n  narrowed by " + ", ".join(lint["narrowed"])
    return head


def findings_text(reply):
    """A `lint` reply, or the lint inside a `crossing` reply, as text lines (None when the reply carries no findings). A `crossings`
    batch prints each row's crossing, headed by the row (theme route edge dir)."""
    obj = json.loads(reply)
    if obj.get("cmd") == "crossings" and isinstance(obj.get("crossings"), list):
        parts = []
        for row in obj["crossings"]:
            r = row.get("row", {})
            parts.append("== %s %s %s %s ==" % (r.get("theme", ""), r.get("route", ""), r.get("edge", ""), r.get("dir", "")))
            text = findings_text(json.dumps(row)) if row.get("ok", False) else "ok:false: " + row.get("error", "")
            if text:
                parts.append(text)
        return "\n".join(parts)
    lint = obj.get("lint") if isinstance(obj.get("lint"), dict) else obj
    if not isinstance(lint.get("findings"), list):
        return None
    lines = crossing_lines(obj) if obj.get("cmd") == "crossing" else []
    lines.append(lint_head(lint))
    for f in lint["findings"]:
        keys = ", ".join(f.get("keys", []))
        # a fail of a gate rule prints as it always did; anything else says what it is (review, suppressed, report; todo)
        tags = [t for t in (f.get("outcome", "fail"), f.get("level", "gate")) if t not in ("fail", "gate")]
        name = f["rule"] if f.get("id", f["rule"]) == f["rule"] else "%s/%s" % (f["rule"], f["id"])
        lines.append("[%-5s] %s%s: %s%s" % (f["severity"], name, " (" + ", ".join(tags) + ")" if tags else "",
                                            glyphs_named(f["message"]), "  <" + keys + ">" if keys else ""))
    return "\n".join(lines)


def glyphs_named(text):
    """An icon glyph (a private-use character) prints as nothing on a console: show its code point instead."""
    return "".join("glyph:U+%04X" % ord(c) if 0xE000 <= ord(c) <= 0xF8FF else c for c in text)


def _ms(v):
    return "%.2f" % v


TRACE_LIST_CAP = 30


def trace_text(reply):
    """A `trace` reply (or the `trace` object inside a goto/scroll/click... reply) as a summary: None when the reply carries no trace."""
    obj = json.loads(reply)
    t = obj.get("trace") if isinstance(obj.get("trace"), dict) else obj
    if not isinstance(t.get("summary"), dict) or "traced" not in t:
        return None
    s = t["summary"]
    mx = s.get("max", {})
    watch = t.get("watch", [])
    c = t.get("counts", {})
    lines = ["trace %s: %s frames, settled at %s (last animating frame %s)" % (
        obj.get("cmd", ""), s.get("frames"), s.get("settledAt"), s.get("lastAnimatingFrame"))]
    if not watch:
        lines.append("  WARNING: this trace watched no nodes, so its snaps, jumps and remounts are not measured (0 is no verdict): "
                     "pass prefix=<key family> or keys=[...] (and max=N above 20 for a page)")
    else:
        lines.append("  motion: %s watched key%s moved at once (%s snap%s with no track, %s instant: one frame carried a tracked run), "
                     "remounted %s outside a swap family, %s inside; %s jump%s listed (every eased frame over the jump DIP: no verdict)" % (
            c.get("movedAtOnce", "?"), "" if c.get("movedAtOnce") == 1 else "s", len(s.get("snaps", [])), "" if len(s.get("snaps", [])) == 1 else "s",
            len(s.get("instants", [])), c.get("remountedOutside", "?"), c.get("remountedSwap", "?"),
            len(s.get("jumps", [])), "" if len(s.get("jumps", [])) == 1 else "s"))
        if "remountedSwitch" in c:
            lines.append("  theme switch: frames %s held under its freeze; there %s remount%s and %s moved key%s are the switch's own (reported, not gated)" % (
                t.get("switchFrames"), c.get("remountedSwitch"), "" if c.get("remountedSwitch") == 1 else "s",
                c.get("movedInSwitch"), "" if c.get("movedInSwitch") == 1 else "s"))
    lines.append("  relayout: rootSolveFrames=%s escapeFrames=%s fullLayoutInferred=%s" % (
        s.get("rootSolveFrames"), s.get("escapeFrames"), s.get("fullLayoutFramesInferred")))
    lines.append("  measure: total=%s max=%s@%s framesWithMeasure=%s components=%s (frames %s)" % (
        s.get("measureTotal"), s.get("maxMeasure"), s.get("maxMeasureFrame"), s.get("framesWithMeasure"),
        s.get("componentsTotal"), s.get("framesWithComponents")))
    lines.append("  max ms: " + "  ".join("%s=%s@%s" % (k, _ms(v["ms"]), v["frame"]) for k, v in mx.items())
                 + "   cpu>8.3ms: %s frames, cpu>16.7ms: %s frames" % (s.get("cpuOver8ms"), s.get("cpuOver16ms")))
    for r in t.get("remounted", []):
        fam = r.get("family")
        lines.append("  remount f%-3s %s  %s" % (r["frame"], r["key"], "(the theme switch's own template swap)" if fam == "theme-switch"
                                                         else "(swap family %s)" % fam if fam else "(outside any swap family: a rebuilt part)"))
    for kind in ("snaps", "instants", "jumps"):
        moves = s.get(kind, [])
        for m in moves[:TRACE_LIST_CAP]:
            lines.append("  %-5s f%-3s %s  d=(%s, %s, %s, %s) prev=%s%s" % (
                kind[:-1], m["frame"], m["key"], m["dx"], m["dy"], m["dw"], m["dh"], m["prevMag"], " track" if m["track"] else ""))
        if len(moves) > TRACE_LIST_CAP:
            lines.append("  ... %d more %s (the reply has them all)" % (len(moves) - TRACE_LIST_CAP, kind))
    sc = s.get("scroll", {})
    d = sc.get("deltas", {})
    if d.get("count"):
        lines.append("  scroll deltas: n=%s mean=%s std=%s min=%s max=%s" % (d["count"], d["mean"], d["std"], d["min"], d["max"]))
    for o in sc.get("overshoots", []):
        lines.append("  overshoot          f%s-%s (%s frames) %s on %s, deepest %s at f%s" % (
            o["startFrame"], o["endFrame"], o["frames"], o["kind"], o["on"], o["maxOver"], o["maxOverFrame"]))
    for kind in ("reversals", "stalls", "uneven", "layoutDuringScroll"):
        for e in sc.get(kind, []):
            lines.append("  %-18s f%-3s on %s offset=%s prev=%s delta=%s max=%s" % (
                kind, e["frame"], e["on"], e["offset"], e["prev"], e["delta"], e["max"]))
    if watch:
        lines.append("  watched %d nodes: %s" % (len(watch), ", ".join(watch[:8]) + (" ..." if len(watch) > 8 else "")))
    after = obj.get("after")
    if isinstance(after, dict):
        overlays = ", ".join(o.get("key", "") + ("(" + o["first"] + ")" if o.get("first") else "") for o in after.get("overlays", []))
        lines.append("  after: route=%s focused=%s hit=%s overlays=[%s]" % (after.get("route"), after.get("focused"), after.get("hit"), overlays))
    return chr(10).join(lines)


SEVERITY_RANK = {"info": 0, "warn": 1, "error": 2}


class Gate:
    """--gate <info|warn|error>: a verdict over every reply, that no listing option can silence.
    Exit 2 when any reply was ok:false or no lint ran at all (a gate over nothing is no verdict). Else 1 when:
    - a lint (or a crossing's lint) has a fail of a gate-level rule at or above the gate severity, counted from the reply's `fails`
      (every fail of the rules that ran, listed or not: `min`, `outcomes` and `limit` choose only what is listed; with --gate-todo the
      todo-level rules count too). A review, a suppressed finding and a report never count;
    - a crossing moved more than --max-moved nodes (default 0) past its control step (`counts.excessSnapped + excessJumped`), or
      remounted a key outside a swap family (`counts.remountedOutside`);
    - a trace (`trace` or `trace=N` on an acting verb) moved more than --max-moved watched keys at once (`counts.movedAtOnce`: a snap
      with no track, or an instant run of tracked moves one frame carried), or remounted a watched key outside a swap family.
    Warnings (printed, not counted): a lint whose listing or judgement was narrowed (rules, min, outcomes, limit, root), a crossing that
    crossed no edge or ran without its control, a trace that watched no nodes. Else 0."""

    def __init__(self, level, todo=False, max_moved=0):
        self.level = level
        self.todo = todo
        self.max_moved = max_moved
        self.failed = []          # (cmd, error) of every ok:false reply
        self.lints = 0
        self.counts = {"error": 0, "warn": 0, "info": 0}
        self.listed_only = False  # an older server without `fails`: counted from the listed findings
        self.skipped = {}         # why a listed finding did not count -> how many (older servers only)
        self.motion = []          # why a crossing or a trace failed
        self.warnings = []

    def _lint(self, lint, where):
        self.lints += 1
        fails = lint.get("fails")
        if isinstance(fails, dict):
            for level in ("gate", "todo") if self.todo else ("gate",):
                for sev, n in fails.get(level, {}).items():
                    self.counts[sev] = self.counts.get(sev, 0) + n
        else:
            self.listed_only = True
            for f in lint["findings"]:
                outcome, level = f.get("outcome", "fail"), f.get("level", "gate")
                why = outcome if outcome != "fail" else ("todo" if level == "todo" and not self.todo else None)
                if why is not None:
                    self.skipped[why] = self.skipped.get(why, 0) + 1
                    continue
                sev = f.get("severity", "info")
                self.counts[sev] = self.counts.get(sev, 0) + 1
        if lint.get("narrowed"):
            self.warnings.append("%s was narrowed by %s: the gate counts every fail of the rules that ran, but %s" % (
                where, ", ".join(lint["narrowed"]),
                "only those rules ran" if any(n.startswith("rules=") for n in lint["narrowed"])
                else "only that subtree was judged" if any(n.startswith("root=") for n in lint["narrowed"])
                else "the lines above do not list them all"))
        if isinstance(fails, dict):
            floor = SEVERITY_RANK[self.level]
            levels = ("gate", "todo") if self.todo else ("gate",)
            counted = sum(n for level in levels for sev, n in fails.get(level, {}).items() if SEVERITY_RANK.get(sev, 0) >= floor)
            listed = sum(1 for f in lint["findings"] if f.get("outcome", "fail") == "fail" and f.get("level", "gate") in levels
                         and SEVERITY_RANK.get(f.get("severity"), 0) >= floor)
            if counted > listed:
                self.warnings.append("%s: %d of the %d fails the gate counts are not listed above (the lint's min, outcomes or limit): "
                                     "lint again with min=%s and no outcomes or limit to read them" % (where, counted - listed, counted, self.level))

    def _crossing(self, obj, where):
        c = obj.get("counts", {})
        moved = c.get("excessSnapped", c.get("snapped", 0)) + c.get("excessJumped", c.get("jumped", 0))
        if moved > self.max_moved:
            self.motion.append("%s moved %d node%s at once past its control (max %d)" % (where, moved, "" if moved == 1 else "s", self.max_moved))
        outside = c.get("remountedOutside", c.get("remounted", 0))
        if outside > 0:
            keys = [r["key"] for r in obj.get("remounted", []) if r.get("family") is None][:4]
            self.motion.append("%s remounted %d key%s outside a swap family (%s)" % (where, outside, "" if outside == 1 else "s", ", ".join(keys)))
        late = c.get("late", 0)
        if self.todo and late > self.max_moved:   # a weight: report-only until promoted, so only --gate-todo counts it
            keys = [r["key"] for r in obj.get("late", [])][:4]
            self.motion.append("%s reflowed %d part%s late, after the commit frame (todo: %s)" % (where, late, "" if late == 1 else "s", ", ".join(keys)))
        if not isinstance(obj.get("control"), dict):
            self.warnings.append("%s ran without a control step (%s): its excess is every move" % (where, obj.get("controlNote") or "control=false"))
        if _tiers(obj.get("tierBefore")) == _tiers(obj.get("tierSettled")):
            self.warnings.append("%s crossed no edge (the tiers before and after are the same)" % where)

    def _trace(self, t, where):
        if not t.get("watch"):
            self.warnings.append("%s watched no nodes: its motion is not judged (pass prefix= or keys=)" % where)
            return
        c = t.get("counts", {})
        keys = c.get("movedAtOnce", c.get("snappedKeys", len({m["key"] for m in t.get("summary", {}).get("snaps", [])})))
        if keys > self.max_moved:
            self.motion.append("%s moved %d watched key%s at once (a snap, or a tracked run one frame carried; max %d)" % (
                where, keys, "" if keys == 1 else "s", self.max_moved))
        outside = c.get("remountedOutside", 0)
        if outside > 0:
            names = [r["key"] for r in t.get("remounted", []) if r.get("family") is None][:4]
            self.motion.append("%s remounted %d watched key%s outside a swap family (%s)" % (where, outside, "" if outside == 1 else "s", ", ".join(names)))

    def see(self, reply):
        if reply is None:
            return
        obj = _object(reply)
        if obj is None:
            self.failed.append(("?", "not a JSON reply: " + (reply if len(reply) <= 200 else reply[:200] + "...")))
            return
        if not obj.get("ok", False):
            self.failed.append((obj.get("cmd", ""), obj.get("error", "")))
            return
        cmd = obj.get("cmd", "")
        if cmd == "crossings" and isinstance(obj.get("crossings"), list):
            # the batch: every row is a whole `crossing` reply, judged as one (its lint, its motion, an ok:false row)
            for row in obj["crossings"]:
                self.see(json.dumps(row))
            return
        lint = obj.get("lint") if isinstance(obj.get("lint"), dict) else obj
        n = self.lints + 1
        if isinstance(lint.get("findings"), list):
            self._lint(lint, "lint %d%s" % (n, " [" + lint.get("route", "") + "]" if lint.get("route") else ""))
        if cmd == "crossing" and isinstance(obj.get("counts"), dict):
            self._crossing(obj, "crossing %sx%s->%sx%s" % (obj["from"]["w"], obj["from"]["h"], obj["to"]["w"], obj["to"]["h"]))
        trace = obj.get("trace") if isinstance(obj.get("trace"), dict) else (obj if cmd == "trace" else None)
        if isinstance(trace, dict) and "traced" in trace:
            self._trace(trace, "trace on %s" % cmd)

    def at_or_above(self):
        floor = SEVERITY_RANK[self.level]
        return sum(n for sev, n in self.counts.items() if SEVERITY_RANK.get(sev, 0) >= floor)

    def code(self):
        if self.failed or self.lints == 0:
            return 2
        return 1 if self.at_or_above() > 0 or self.motion else 0

    def summary(self):
        code = self.code()
        lines = ["gate WARNING: " + w for w in self.warnings]
        lines += ["gate MOTION: " + m for m in self.motion]
        skipped = ", ".join("%d %s" % (n, why) for why, n in sorted(self.skipped.items()))
        text = "gate %s%s: %d lint repl%s, %d error, %d warn, %d info failed (%d at or above %s%s%s); %d motion failure%s; %d ok:false repl%s -> exit %d" % (
            self.level, " +todo" if self.todo else "", self.lints, "y" if self.lints == 1 else "ies",
            self.counts["error"], self.counts["warn"], self.counts["info"],
            self.at_or_above(), self.level, "; counted from the listed findings (an older server)" if self.listed_only else "",
            "; not counted: " + skipped if skipped else "", len(self.motion), "" if len(self.motion) == 1 else "s",
            len(self.failed), "y" if len(self.failed) == 1 else "ies", code)
        if self.failed:
            text += " (first: %s: %s)" % self.failed[0]
        elif self.lints == 0:
            text += " (no lint ran: a gate over nothing is no verdict; add `lint`, a crossing carries its own)"
        lines.append(text)
        return "\n".join(lines)


def show(reply, pretty, findings=False, trace=False):
    if reply is None:
        return
    if trace:
        text = trace_text(reply)
        if text is not None:
            print(text)
            sys.stdout.flush()
            return
    if findings:
        text = findings_text(reply)
        if text is not None:
            print(text)
            sys.stdout.flush()
            return
    if pretty:
        print(json.dumps(json.loads(reply), indent=2, ensure_ascii=False))
    else:
        print(reply)
    sys.stdout.flush()


def main():
    # replies carry glyphs (icon code points, curly quotes) a legacy console code page cannot encode
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    ap = argparse.ArgumentParser(description="Drive Wavee.exe --inspect.")
    ap.add_argument("--exe", help="path to Wavee.exe (default: the newest build under src/apps/Wavee/bin)")
    ap.add_argument("--pipe", help="talk over \\\\.\\pipe\\<name> instead of stdin/stdout")
    ap.add_argument("--profile", help="scratch profile folder (default: a temp folder the server deletes)")
    ap.add_argument("--text", choices=["dwrite", "headless"], help="text metrics (default dwrite)")
    ap.add_argument("-c", "--command", action="append", default=[], help="a command line; repeatable")
    ap.add_argument("-f", "--file", help="a file of command lines (JSON lines or text lines)")
    ap.add_argument("--pretty", action="store_true", help="indent the replies")
    ap.add_argument("--findings", action="store_true", help="print a lint (or a crossing's lint) reply as one text line per finding")
    ap.add_argument("--trace", action="store_true", help="print a trace reply (or the trace inside a goto/scroll/click reply) as a summary")
    ap.add_argument("--fail-fast", action="store_true", help="stop at the first ok:false reply (exit 2)")
    ap.add_argument("--gate", choices=["info", "warn", "error"],
                    help="a verdict: exit 1 when a lint has a fail of a gate-level rule at or above this severity (counted from the reply's "
                         "`fails`: every fail, listed or not; review, suppressed and report findings never count), when a crossing moves more "
                         "than --max-moved nodes past its control or remounts a key outside a swap family, or when a trace moves more than "
                         "--max-moved watched keys at once or remounts one outside a swap family; 2 on any ok:false reply or when no lint ran; else 0. "
                         "Warns when a lint was narrowed (rules, root, outcomes, limit) or counted fails it did not list, a crossing crossed "
                         "nothing, a trace watched nothing")
    ap.add_argument("--gate-todo", action="store_true",
                    help="with --gate: failures of the todo-level rules (align, space, placeholder, keys, duplicate text, inkless targets, "
                         "target.size above Compact) count too")
    ap.add_argument("--max-moved", type=int, default=0, metavar="N",
                    help="with --gate: the most nodes a crossing may move at once past its control step (counts.excessSnapped + "
                         "excessJumped), and the most watched keys a trace may move at once (counts.movedAtOnce); default 0")
    args = ap.parse_args()

    if (args.gate_todo or args.max_moved) and not args.gate:
        ap.error("--gate-todo and --max-moved need --gate <info|warn|error>")
    if args.max_moved < 0:
        ap.error("--max-moved is 0 or more")
    gate = Gate(args.gate, args.gate_todo, args.max_moved) if args.gate else None
    server = None
    code = 0
    try:
        # Inside the try: under --gate a server that never comes up (no exe, a boot fault, a closed stream) is a failure, exit 2.
        server = Server(find_exe(args.exe), args.pipe, args.profile, args.text)
        show(json.dumps(server.ready), args.pretty)
        lines = list(args.command)
        if args.file:
            with open(args.file, encoding="utf-8") as fh:
                lines += fh.read().splitlines()
        source = lines if (lines or args.file) else sys.stdin
        left = 0
        for line in source:
            if server.ended:
                left += 1 if line.strip() and not line.strip().startswith("#") else 0
                continue
            reply = server.send(line)
            show(reply, args.pretty, args.findings, args.trace)
            if gate is not None:
                gate.see(reply)
            obj = _object(reply) if reply else None
            if obj is not None and obj.get("ok") and obj.get("cmd", "").lower() in ("quit", "exit"):
                server.ended = True   # the server is gone: the lines after `quit` are not sent
            if reply and args.fail_fast and not json.loads(reply).get("ok", False):
                code = 2
                break
        if left:
            sys.stderr.write("inspect.py: %d command line%s after quit not sent\n" % (left, "" if left == 1 else "s"))
    except (SystemExit, Exception) as e:
        # The server did not come up, died or closed the stream, or a reply did not parse. Without --gate this ends the run as it
        # always did; with --gate it is a failed reply (exit 2), never a lint verdict, and the summary still prints.
        if gate is None:
            raise
        why = (str(e) or e.__class__.__name__).strip()
        gate.failed.append(("server", why if len(why) <= 300 else why[:300] + "..."))
        if server is not None:
            server.ended = True
    finally:
        if server is not None:
            server.close()
    if gate is not None:
        print(gate.summary())
        sys.stdout.flush()
        code = max(code, gate.code())
    sys.exit(code)


if __name__ == "__main__":
    main()
