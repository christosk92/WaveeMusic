"""Record-Feature.ps1's second half: the frame accounting, the MP4s and the contact sheet of a take.

    python -I record_feature_post.py --out <take folder> --fps 120 [--scale 1.5] [--width 1600 --height 1000]

Reads <out>/take.mkv (the lossless, variable-rate capture: one frame per window update, each with its own timestamp),
<out>/take.wav (optional: the app's own audio) and <out>/drive/ (the app's frames.tsv and summary.txt), and writes:

  frames.tsv       every captured frame: index, time (s), gap (ms), gap in display periods
  take-<fps>.mp4   H.264 High, constant <fps>, near-transparent quality (crf 14), the audio as AAC when present
  share-60.mp4     60 fps (when the take is faster), at most 1920 wide, two-pass to fit 50 MB
  sheet.png        a contact sheet: 12 stills spread over the take, each labelled with its time
  report.txt       the accounting below

THE ACCOUNTING. A window capture delivers a frame only when the window presents a new one, so a still UI is no drop. A
frame is MISSING when, inside continuous motion (the gaps on both sides are one display period), one gap spans two or more
periods: the window did not present on a vblank it was presenting on before and after. The capture's own timestamps give
`capture missing`; the app's own present log (drive/frames.tsv, every frame it rendered) gives `app missing` on the same
rule. Equal counts mean the app hitched; more on the capture side means the recorder lost frames. The app's present
statistics over the take (presents dropped, vblanks repeated, motion ticks missed) come from drive/summary.txt.
"""
import argparse
import json
import os
import subprocess
import sys


def run(args, **kw):
    r = subprocess.run(args, capture_output=True, text=True, **kw)
    if r.returncode != 0:
        sys.stderr.write(r.stderr[-4000:])
        raise SystemExit(f"{args[0]} failed ({r.returncode})")
    return r.stdout


def missing(gaps_ms, period_ms, windows=None, starts=None):
    """Frames missing inside motion. A gap of k periods (1.5 < k) counts k - 1 frames when the window was moving around it:
    a short gap (up to 6 periods) whose neighbours are both one period (on-beat frames on each side), and any gap inside a
    known motion window (the driver's glides: the pointer moves on every frame, so the window must present
    on every vblank). `windows` are (start_ms, end_ms) on the same clock as `starts` (each frame's time, ms)."""
    n = 0
    events = []
    for i in range(1, len(gaps_ms)):
        g = gaps_ms[i]
        if g < 1.5 * period_ms:
            continue
        inside = False
        if windows and starts is not None:
            t0, t1 = starts[i] - g, starts[i]
            inside = any(a <= t0 and t1 <= b + period_ms for a, b in windows)
        # outside the glides: a short hole between two on-beat frames (the window was presenting every vblank around it); a
        # slow cadence (a 12 Hz progress tick) or a still UI that wakes up is not a dropped frame
        between_beats = 0 < i < len(gaps_ms) - 1 and gaps_ms[i - 1] < 1.25 * period_ms and gaps_ms[i + 1] < 1.25 * period_ms
        if (g <= 6 * period_ms and between_beats) or inside:
            k = int(round(g / period_ms)) - 1
            if k > 0:
                n += k
                events.append((i, round(g, 1), k))
    return n, events


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True)
    ap.add_argument("--fps", type=int, required=True)
    ap.add_argument("--panel", type=int, default=0, help="the display's refresh rate (the app presents at it; default --fps)")
    ap.add_argument("--scale", type=float, default=1.0)
    ap.add_argument("--width", type=int, default=0)
    ap.add_argument("--height", type=int, default=0)
    a = ap.parse_args()
    out = a.out
    mkv = os.path.join(out, "take.mkv")
    wav = os.path.join(out, "take.wav")
    has_audio = os.path.exists(wav) and os.path.getsize(wav) > 1024
    period = 1000.0 / a.fps
    app_period = 1000.0 / (a.panel or a.fps)

    # ── the capture's own timestamps ────────────────────────────────────────────────────────────────────────────────
    raw = run(["ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries", "frame=pts_time", "-of", "csv=p=0", mkv])
    vals = [x.strip().strip(",") for x in raw.split()]
    times = [float(x) for x in vals if x and x != "N/A"]
    if len(times) < 2:
        raise SystemExit("the capture holds fewer than 2 frames")
    gaps = [0.0] + [(times[i] - times[i - 1]) * 1000.0 for i in range(1, len(times))]
    with open(os.path.join(out, "frames.tsv"), "w", encoding="utf-8", newline="\n") as f:
        f.write("frame\tt_s\tgap_ms\tperiods\n")
        for i, t in enumerate(times):
            f.write(f"{i}\t{t:.6f}\t{gaps[i]:.3f}\t{gaps[i] / period:.2f}\n")
    # The driver's glides (steps.tsv), on the take's clock. The capture is lined up on the take's first move: the setup leaves the
    # pointer still, so the first new frame after the still lead-in is the first glide's first move.
    glides = []
    steps_path = os.path.join(out, "drive", "steps.tsv")
    if os.path.exists(steps_path):
        for l in open(steps_path, encoding="utf-8").readlines()[1:]:
            c = l.rstrip("\n").split("\t")
            if len(c) >= 4 and c[1] == "glide" and float(c[3]) - float(c[2]) > period:
                glides.append((float(c[2]), float(c[3])))
    first_move = next((i for i in range(1, len(gaps)) if gaps[i] > 200.0), 1)
    cap_starts = [(t - times[first_move]) * 1000.0 for t in times]
    cap_missing, cap_events = missing(gaps, period, glides, cap_starts)
    on_beat = sum(1 for g in gaps[1:] if g < 1.25 * period)
    duration = times[-1] - times[0]

    # ── the app's present log ───────────────────────────────────────────────────────────────────────────────────────
    app_missing, app_events, app_frames, summary = None, [], 0, {}
    drive = os.path.join(out, "drive")
    if os.path.isdir(drive):
        ft = os.path.join(drive, "frames.tsv")
        if os.path.exists(ft):
            rows = [l.rstrip("\n").split("\t") for l in open(ft, encoding="utf-8").readlines()[1:] if l.strip()]
            app_gaps = [float(r[2]) for r in rows]
            app_starts = [float(r[1]) for r in rows]
            app_frames = len(app_gaps)
            # UI frames: only the glides need one per vblank (the pointer is UI-placed); a scroll or a transition presents
            # from the render thread without a UI frame, so the app side is judged inside the glides only.
            gl = [i for i in range(len(rows)) if any(a <= app_starts[i] - app_gaps[i] and app_starts[i] <= b for a, b in glides)]
            sel_gaps = [0.0] + [app_gaps[i] for i in gl]
            app_missing = sum(int(round(g / app_period)) - 1 for g in sel_gaps[1:] if g >= 1.5 * app_period)
            app_events = [(gl[j - 1], round(sel_gaps[j], 1), int(round(sel_gaps[j] / app_period)) - 1) for j in range(1, len(sel_gaps)) if sel_gaps[j] >= 1.5 * app_period]
        st = os.path.join(drive, "summary.txt")
        if os.path.exists(st):
            for l in open(st, encoding="utf-8"):
                k, _, v = l.rstrip("\n").partition("\t")
                summary[k] = v

    # ── the MP4s ────────────────────────────────────────────────────────────────────────────────────────────────────
    full = os.path.join(out, f"take-{a.fps}.mp4")
    audio_in = []
    if has_audio:
        # line the audio up: its file starts at its own startQpc; the take's t = 0 (the drive's startQpc100ns) is the
        # capture's first-move frame. A positive offset delays the audio.
        offset = 0.0
        try:
            a0 = json.load(open(wav + ".json", encoding="utf-8"))["startQpc100ns"] / 1e7
            d0 = int(summary["startQpc100ns"]) / 1e7
            offset = (times[first_move] - times[0]) - (d0 - a0)
        except (OSError, KeyError, ValueError):
            pass
        audio_in = ["-itsoffset", f"{offset:.4f}", "-i", wav]
    audio_out = ["-c:a", "aac", "-b:a", "192k", "-shortest"] if has_audio else ["-an"]
    run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-i", mkv, *audio_in, "-vf", f"fps={a.fps}",
         "-c:v", "libx264", "-profile:v", "high", "-preset", "slow", "-crf", "14", "-pix_fmt", "yuv420p",
         "-movflags", "+faststart", *audio_out, full])

    share = os.path.join(out, "share-60.mp4")
    share_fps = min(60, a.fps)
    budget_bits = 50 * 1000 * 1000 * 8 * 0.93
    audio_bits = 160_000 if has_audio else 0
    kbps = int(min(16_000_000, budget_bits / max(1.0, duration) - audio_bits) / 1000)
    vf = f"fps={share_fps},scale='min(1920,iw)':-2:flags=lanczos"
    passlog = os.path.join(out, "share-pass")
    run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-i", mkv, "-vf", vf, "-c:v", "libx264", "-profile:v", "high",
         "-preset", "slow", "-b:v", f"{kbps}k", "-pix_fmt", "yuv420p", "-pass", "1", "-passlogfile", passlog, "-an", "-f", "null", "NUL"])
    a_share = ["-c:a", "aac", "-b:a", "160k", "-shortest"] if has_audio else ["-an"]
    run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-i", mkv, *audio_in, "-vf", vf, "-c:v", "libx264", "-profile:v", "high",
         "-preset", "slow", "-b:v", f"{kbps}k", "-maxrate", f"{int(kbps * 1.6)}k", "-bufsize", f"{kbps * 2}k", "-pix_fmt", "yuv420p",
         "-pass", "2", "-passlogfile", passlog, "-movflags", "+faststart", *a_share, share])
    for suffix in ("-0.log", "-0.log.mbtree"):
        try:
            os.remove(passlog + suffix)
        except OSError:
            pass

    # ── the contact sheet ───────────────────────────────────────────────────────────────────────────────────────────
    sheet = os.path.join(out, "sheet.png")
    n = 12
    step = duration / n
    font = "C\\:/Windows/Fonts/segoeui.ttf"
    picks = "+".join(f"eq(n\\,{int((step * (i + 0.5)) * a.fps)})" for i in range(n))
    run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-i", full, "-vf",
         f"select='{picks}',scale=640:-2,drawtext=fontfile='{font}':text='%{{pts\\:hms}}':x=10:y=10:fontsize=22:fontcolor=white:box=1:boxcolor=black@0.55:boxborderw=6,tile=4x3:padding=6:color=black",
         "-frames:v", "1", "-update", "1", sheet])

    # ── the report ──────────────────────────────────────────────────────────────────────────────────────────────────
    sizes = {p: os.path.getsize(os.path.join(out, p)) for p in (os.path.basename(full), "share-60.mp4")}
    lines = [
        f"take            {duration:.2f} s, {len(times)} captured frames at up to {a.fps} fps (one per window update; the panel runs at {a.panel or a.fps} Hz)",
        f"window          {a.width}x{a.height} DIP at x{a.scale} = {int(a.width * a.scale)}x{int(a.height * a.scale)} px",
        f"on the beat     {on_beat} of {len(times) - 1} gaps are one display period ({period:.2f} ms)",
        f"capture missing {cap_missing} frame(s) inside motion (aligned on the take's first move at capture frame #{first_move})" + (f": at frames {', '.join(f'#{i} ({g:.1f} ms)' for i, g, _ in cap_events[:20])}" if cap_events else ""),
    ]
    if app_missing is not None:
        lines.append(f"app missing     {app_missing} UI frame(s) at {round(1000 / app_period)} Hz inside the pointer glides ({len(glides)} glides, {app_frames} UI frames in all)"
                     + (f": at app frames {', '.join(f'#{i} ({g:.1f} ms)' for i, g, _ in app_events[:20])}" if app_events else ""))
    for k in ("result", "why", "presentsDisplayed", "presentsDropped", "vblanksRepeated", "renderMissedMotionTicks"):
        if k in summary:
            lines.append(f"app {k:<12} {summary[k]}")
    lines.append(f"audio           {'the app process only (WASAPI process loopback)' if has_audio else 'none (silent)'}")
    for p, s in sizes.items():
        lines.append(f"file            {p}  {s / 1e6:.1f} MB")
    lines.append(f"file            sheet.png, frames.tsv, take.mkv (lossless, {os.path.getsize(mkv) / 1e6:.0f} MB)")
    with open(os.path.join(out, "report.txt"), "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines) + "\n")
    with open(os.path.join(out, "report.json"), "w", encoding="utf-8") as f:
        json.dump({"duration": duration, "frames": len(times), "fps": a.fps, "captureMissing": cap_missing, "appMissing": app_missing,
                   "captureEvents": cap_events, "appEvents": app_events, "summary": summary, "audio": has_audio}, f, indent=1)


if __name__ == "__main__":
    main()
