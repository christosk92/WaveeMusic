"""Summarize a `Wavee.exe --video-probe` run: one waterfall per round (a `mark`/click/commit reset), UI stalls, frame gaps.

usage: python -I tools/video-probe-report.py <profile-or-log> [--all]

Reads the profile's logs/wavee-*.log (or the given file). Every line carries `t=<unix ms>`; rounds start at `[vtime] reset`.
A round lists the video stages (`[vtime]`, the engine's `[video] vtime`, and the `[video]` budget lines) on ms from its
start, then the UI stalls (`[vprobe] ui.stall`) and frame gaps that fell inside it.
"""
import glob, os, re, sys

LINE = re.compile(r"^seq=\d+ tid=(\d+) t=(\d+) \S+ pid=\d+ (\w) \[([^\]]+)\] (.*)$")
KEEP_VIDEO = re.compile(r"\[video\] (switch\.begin|switch\.budget|license\.budget|open\.ok|first\.frame|vtime|attach|metadata|canplay|"
                        r"prepared\.|runtime\.|session\.|start\.timeout|feed\.|fault|state pump stalled|start watchdog|audio\.cut|seek\.)")

def lines(path):
    files = sorted(glob.glob(os.path.join(path, "logs", "wavee-*.log"))) if os.path.isdir(path) else [path]
    for f in files:
        with open(f, encoding="utf-8", errors="replace") as fh:
            for raw in fh:
                m = LINE.match(raw.rstrip("\n"))
                if m: yield int(m.group(2)), m.group(1), m.group(4), m.group(5)

def main():
    if len(sys.argv) < 2: print(__doc__); return 64
    show_all = "--all" in sys.argv
    rounds, cur = [], None
    for t, tid, cat, msg in lines(sys.argv[1]):
        if cat == "vtime" and msg.startswith("reset"):
            label = msg.split(" - ", 1)[1].split(" since=")[0] if " - " in msg else "?"
            # a click's own reset follows its mark within ms: fold it into the mark's round
            if cur and t - cur["t0"] < 400:
                cur["label"] += " / " + label
                continue
            cur = {"t0": t, "label": label, "events": [], "stalls": [], "gaps": []}
            rounds.append(cur)
            continue
        if cur is None: continue
        rel = t - cur["t0"]
        if cat == "vprobe":
            if msg.startswith("ui.stall"):
                ms = float(re.search(r"ms=([\d.]+)", msg).group(1)); cur["stalls"].append((rel, ms))
            elif msg.startswith("frame.gap"):
                ms = float(re.search(r"ms=([\d.]+)", msg).group(1)); cur["gaps"].append((rel, ms))
            elif msg.startswith("step ") or msg.startswith("input."):
                cur["events"].append((rel, "probe", msg[:140]))
            continue
        if cat == "vtime":
            cur["events"].append((rel, "ui" if "thread=ui" in msg else "t" + tid, re.sub(r" elapsed=\d+ms", "", msg)[:170]))
        elif cat in ("video", "engine") and (show_all or KEEP_VIDEO.search(msg)):
            cur["events"].append((rel, "t" + tid, msg[:220]))
        elif cat == "engine" and "[video" in msg and show_all:
            cur["events"].append((rel, "t" + tid, msg[:220]))
    for r in rounds:
        print(f"\n=== {r['label']}")
        for rel, who, msg in r["events"]:
            print(f"  {rel:7d} ms  {who:6s} {msg}")
        if r["stalls"]:
            tot = sum(ms for _, ms in r["stalls"]); worst = max(r["stalls"], key=lambda s: s[1])
            print(f"  UI stalls >=50ms: {len(r['stalls'])}, total {tot:.0f} ms, worst {worst[1]:.0f} ms at +{worst[0]} ms")
            for rel, ms in r["stalls"]:
                if ms >= 100: print(f"      stall {ms:6.0f} ms ending at +{rel} ms")
        if r["gaps"]:
            worst = max(r["gaps"], key=lambda s: s[1])
            print(f"  frame gaps >=50ms between rendered frames: {len(r['gaps'])}, worst {worst[1]:.0f} ms at +{worst[0]} ms")
    return 0

if __name__ == "__main__":
    sys.exit(main())
