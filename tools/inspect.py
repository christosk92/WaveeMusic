"""inspect.py - a tiny client for `Wavee.exe --inspect` (docs/guide/agent-inspect.md).

Starts the server (stdin/stdout, or a named pipe with --pipe), waits for its `ready` line, sends each command as one line
and prints the one reply line it gets back. Standard library only; runs under `python -I`.

    python -I tools/inspect.py -c "mount w=400 h=860 theme=zune" -c "tiers" -c "dump root=zune-band keysOnly=true"
    python -I tools/inspect.py -f audit.jsonl --pretty
    python -I tools/inspect.py --findings -c "mount w=400 h=860 theme=zune" -c "lint min=warn"
    python -I tools/inspect.py --trace -c "mount w=1440 h=900 theme=zune" -c "goto album:spotify:album:x settle=false trace=90"
    python -I tools/inspect.py --gate error -f lint.txt   # exit 1 on a failed gate-rule lint error, 2 on any ok:false, plus a summary line
    python -I tools/inspect.py --gate warn --gate-todo -f lint.txt   # the todo rules (align, space, placeholder, keys) count too
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


def findings_text(reply):
    """A `lint` reply, or the lint inside a `crossing` reply, as text lines (None when the reply carries no findings)."""
    obj = json.loads(reply)
    lint = obj.get("lint") if isinstance(obj.get("lint"), dict) else obj
    if not isinstance(lint.get("findings"), list):
        return None
    lines = []
    if obj.get("cmd") == "crossing":
        counts = obj.get("counts", {})
        lines.append("crossing %sx%s -> %sx%s: committed=%s remounted=%s vanished=%s snapped=%s jumped=%s" % (
            obj["from"]["w"], obj["from"]["h"], obj["to"]["w"], obj["to"]["h"], obj.get("committed"),
            counts.get("remounted"), counts.get("vanished"), counts.get("snapped"), counts.get("jumped")))
        for kind in ("remounted", "vanished"):
            for item in obj.get(kind, []):
                lines.append("  %-9s %s" % (kind, item["key"]))
        for kind in ("snapped", "jumped"):
            for item in obj.get(kind, []):
                lines.append("  %-9s %s  d=(%s, %s, %s, %s)" % (kind, item["key"], item["dx"], item["dy"], item["dw"], item["dh"]))
    summary = lint.get("summary", {})
    outcomes, levels = lint.get("outcomes"), lint.get("levels")
    lines.append("lint %s %s %sx%s: %s error, %s warn, %s info over %s nodes%s%s" % (
        lint.get("theme", ""), lint.get("tier", ""), lint.get("size", {}).get("w"), lint.get("size", {}).get("h"),
        summary.get("error", 0), summary.get("warn", 0), summary.get("info", 0), summary.get("nodes", 0),
        "" if lint.get("guide", True) else " (no theme guide: gutter and right-edge not checked)",
        "; %s; %s" % (" ".join("%s=%s" % kv for kv in outcomes.items()), " ".join("%s=%s" % kv for kv in levels.items()))
        if isinstance(outcomes, dict) and isinstance(levels, dict) else ""))
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


def trace_text(reply):
    """A `trace` reply (or the `trace` object inside a goto/scroll/click... reply) as a summary: None when the reply carries no trace."""
    obj = json.loads(reply)
    t = obj.get("trace") if isinstance(obj.get("trace"), dict) else obj
    if not isinstance(t.get("summary"), dict) or "traced" not in t:
        return None
    s = t["summary"]
    mx = s.get("max", {})
    lines = ["trace %s: %s frames, settled at %s (last animating frame %s)" % (
        obj.get("cmd", ""), s.get("frames"), s.get("settledAt"), s.get("lastAnimatingFrame"))]
    lines.append("  relayout: rootSolveFrames=%s escapeFrames=%s fullLayoutInferred=%s" % (
        s.get("rootSolveFrames"), s.get("escapeFrames"), s.get("fullLayoutFramesInferred")))
    lines.append("  measure: total=%s max=%s@%s framesWithMeasure=%s components=%s (frames %s)" % (
        s.get("measureTotal"), s.get("maxMeasure"), s.get("maxMeasureFrame"), s.get("framesWithMeasure"),
        s.get("componentsTotal"), s.get("framesWithComponents")))
    lines.append("  max ms: " + "  ".join("%s=%s@%s" % (k, _ms(v["ms"]), v["frame"]) for k, v in mx.items())
                 + "   cpu>8.3ms: %s frames, cpu>16.7ms: %s frames" % (s.get("cpuOver8ms"), s.get("cpuOver16ms")))
    watch = t.get("watch", [])
    for kind in ("snaps", "jumps"):
        for m in s.get(kind, []):
            lines.append("  %-5s f%-3s %s  d=(%s, %s, %s, %s) prev=%s%s" % (
                kind[:-1], m["frame"], m["key"], m["dx"], m["dy"], m["dw"], m["dh"], m["prevMag"], " track" if m["track"] else ""))
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
    return chr(10).join(lines)


SEVERITY_RANK = {"info": 0, "warn": 1, "error": 2}


class Gate:
    """--gate <info|warn|error>: tallies every reply. Exit 2 when any reply was ok:false, else 1 when a lint (or a crossing's
    lint) listed a finding at or above the gate severity that is a verdict: outcome "fail" of a gate-level rule (with
    --gate-todo, of a todo-level rule too). A review (the rule could not decide: look at the render), a suppressed finding (an
    intent or a baseline matched) and a report line never count; they are tallied in the summary line. A finding without the
    outcome/level fields (an older server) counts as a gate-level fail. Else 0."""

    def __init__(self, level, todo=False):
        self.level = level
        self.todo = todo
        self.failed = []          # (cmd, error) of every ok:false reply
        self.lints = 0
        self.counts = {"error": 0, "warn": 0, "info": 0}
        self.skipped = {}         # why a listed finding did not count -> how many

    def see(self, reply):
        if reply is None:
            return
        obj = json.loads(reply)
        if not obj.get("ok", False):
            self.failed.append((obj.get("cmd", ""), obj.get("error", "")))
            return
        lint = obj.get("lint") if isinstance(obj.get("lint"), dict) else obj
        if not isinstance(lint.get("findings"), list):
            return
        self.lints += 1
        for f in lint["findings"]:
            outcome, level = f.get("outcome", "fail"), f.get("level", "gate")
            why = outcome if outcome != "fail" else ("todo" if level == "todo" and not self.todo else None)
            if why is not None:
                self.skipped[why] = self.skipped.get(why, 0) + 1
                continue
            sev = f.get("severity", "info")
            self.counts[sev] = self.counts.get(sev, 0) + 1

    def at_or_above(self):
        floor = SEVERITY_RANK[self.level]
        return sum(n for sev, n in self.counts.items() if SEVERITY_RANK.get(sev, 0) >= floor)

    def code(self):
        if self.failed:
            return 2
        return 1 if self.at_or_above() > 0 else 0

    def summary(self):
        code = self.code()
        skipped = ", ".join("%d %s" % (n, why) for why, n in sorted(self.skipped.items()))
        text = "gate %s%s: %d lint repl%s, %d error, %d warn, %d info failed (%d at or above %s%s); %d ok:false repl%s -> exit %d" % (
            self.level, " +todo" if self.todo else "", self.lints, "y" if self.lints == 1 else "ies",
            self.counts["error"], self.counts["warn"], self.counts["info"],
            self.at_or_above(), self.level, "; not counted: " + skipped if skipped else "",
            len(self.failed), "y" if len(self.failed) == 1 else "ies", code)
        if self.failed:
            text += " (first: %s: %s)" % self.failed[0]
        return text


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
                    help="a verdict: exit 1 when any lint finding at or above this severity is a failure of a gate-level rule "
                         "(outcome 'fail', level 'gate'; review, suppressed and report findings never count), 2 on any ok:false "
                         "reply, else 0; prints a one-line summary last")
    ap.add_argument("--gate-todo", action="store_true",
                    help="with --gate: failures of the todo-level rules (align, space, placeholder, keys) count too")
    args = ap.parse_args()

    server = Server(find_exe(args.exe), args.pipe, args.profile, args.text)
    show(json.dumps(server.ready), args.pretty)
    if args.gate_todo and not args.gate:
        ap.error("--gate-todo needs --gate <info|warn|error>")
    gate = Gate(args.gate, args.gate_todo) if args.gate else None
    code = 0
    try:
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
    except (SystemExit, OSError) as e:
        # The server died or closed the stream mid-run. Without --gate this ends the run as it always did; with --gate it is a
        # failed reply (exit 2), never a lint verdict, and the summary still prints.
        if gate is None:
            raise
        why = (str(e) or e.__class__.__name__).strip()
        gate.failed.append(("server", why if len(why) <= 300 else why[:300] + "..."))
        server.ended = True
    finally:
        server.close()
    if gate is not None:
        print(gate.summary())
        sys.stdout.flush()
        code = max(code, gate.code())
    sys.exit(code)


if __name__ == "__main__":
    main()
