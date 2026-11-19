"""record.py - the Settings explanation-badge clips (settings rework DESIGN 6.5): record them from the real app, encode
them at the budget, and check what is committed.

A badge beside a visual or audible setting opens a tip with a short looping clip: the before state, a hold, the change,
a hold on the after state. Every clip is recorded from the real app through the headless inspector (`Wavee.exe
--inspect`, driven through tools/inspect.py), once per theme (classic, winui, zune) and app mode (dark, light); theme
options only in their own theme. Sound dials get an animated diagram drawn here in the theme's own colours (read from the
app), never a recording and never a sound.

    python -I tools/settings-explain/record.py --exe C:/WAVEE/scratch/<lane>/run/Wavee.exe              # every recordable clip
    python -I tools/settings-explain/record.py --exe <exe> --only lyrics.blur playback.crossfade --modes dark
    python -I tools/settings-explain/record.py --exe <exe> --out C:/WAVEE/scratch/<lane>/clips             # anywhere but the assets
    python -I tools/settings-explain/record.py --check                     # sizes and format of the committed clips vs the budget
    python -I tools/settings-explain/record.py --check --json --strict     # what the release validator reads; a missing clip fails
    python -I tools/settings-explain/record.py --list                      # the manifest expanded: clip x theme x mode, and status

Inputs   tools/settings-explain/clips.json (the manifest; its "_doc" says what each field means).
Outputs  src/apps/Wavee/assets/settings-explain/<theme>/<mode>/<clipId>.webp (or under --out); in --work (default
         %TEMP%/wavee-settings-explain): per clip a contact sheet (sheets/<theme>-<mode>-<clipId>.png, every output frame
         with its duration: look at it) and results.json.

How a clip is captured: one fresh inspector process per clip x theme x mode (the inspector rule: one process per
configuration). `mount` at the entry's size and scale (2 by default, so a 320x180 DIP region is 640x360 px), the setup
lines, the before value through `setting id=<dial> value=<v>` (the dial's own writer, as a Settings view calls it), a
rest, `render` of the before frame; then the change (the after value written with settle=false, a slider swept through its
values, or an action such as a click) and `frames <step>` + `render` per captured frame on the inspector's 16 ms clock until
the window holds still (renders of a still scene are identical) or `capture` seconds pass; the region keys are found
(`find`) in both states and their union, grown to 16:9, is the crop; the after hold is the frame the change ended on.
Captured frames are resampled to the output fps, cropped, scaled to 640x360 and encoded with libwebp's WebPAnimEncoder
(Pillow), lossy with mixed frames; WebPAnimEncoder writes sub-rectangle frames for the changed area by itself. ffmpeg's
libwebp_anim is not used: its files decoded several times slower in the engine (ops/release/wavee/README.md, "Motion
highlights"). Over the budget, the encode retries at lower quality, then at 12 fps.

Standard library plus Pillow. Run with `python -I`.
"""

import argparse
import collections
import colorsys
import importlib.util
import io
import json
import math
import os
import queue
import shutil
import sys
import tempfile
import threading
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))                       # WaveeMusic
MANIFEST = os.path.join(HERE, "clips.json")
ASSETS = os.path.join(ROOT, "src", "apps", "Wavee", "assets", "settings-explain")
THEMES = ("classic", "winui", "zune")
MODES = ("dark", "light")
FRAME_MS = 16                                                       # the inspector's fake frame clock
QUALITIES = (62, 55)                                                # after the entry's quality, at its fps; then 12 fps from 55 down
QUALITIES_12 = (55, 48, 40, 32, 25)


class ClipError(Exception):
    pass


# ───────────────────────────── manifest ─────────────────────────────

def read_json(path):
    with open(path, "r", encoding="utf-8") as fh:
        return json.load(fh)


def per_theme(value, theme):
    """A field may be a dict keyed by theme ({"winui": ..., "default": ...}); anything else applies to every theme."""
    if isinstance(value, dict) and value and set(value) <= set(THEMES) | {"default"}:
        return value.get(theme, value.get("default"))
    return value


def entry_themes(entry, manifest):
    themes = entry.get("themes", "all")
    return list(manifest.get("themes", THEMES)) if themes == "all" else list(themes)


def expand(manifest, only=(), themes=None, modes=None):
    """Every clip x theme x mode the manifest asks for, filtered."""
    items = []
    for entry in manifest["clips"]:
        cid = entry["clip"]
        if only and not any(cid == o or (o.endswith("*") and cid.startswith(o[:-1])) for o in only):
            continue
        for t in entry_themes(entry, manifest):
            if themes and t not in themes:
                continue
            for m in entry.get("modes", manifest.get("modes", MODES)):
                if modes and m not in modes:
                    continue
                items.append((entry, t, m))
    return items


def setting_for(entry, theme, key, defaults):
    v = per_theme(entry.get(key), theme)
    return defaults.get(key) if v is None else v


def fmt_value(v):
    if isinstance(v, bool):
        return "true" if v else "false"
    if isinstance(v, float):
        return str(int(v)) if v.is_integer() else "%.2f" % v
    s = str(v)
    return '"%s"' % s if " " in s else s


def pending_part(entry):
    p = entry.get("pending")
    return p.get("part") if isinstance(p, dict) else None


def waits(entry):
    """Skipped until it lands: a surface or a dial on another branch, something the inspector cannot stage yet, or an effect the
    app does not show in one of the modes (a badge needs both)."""
    return pending_part(entry) in ("surface", "dial", "inspector", "effect")


# ───────────────────────────── the inspector ─────────────────────────────

def load_inspect():
    path = os.path.join(ROOT, "tools", "inspect.py")
    spec = importlib.util.spec_from_file_location("wavee_inspect_client", path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


class Session:
    """One `Wavee.exe --inspect` process (tools/inspect.py's Server) and the replies as objects."""

    def __init__(self, inspect_mod, exe):
        try:
            self.server = inspect_mod.Server(exe)
        except SystemExit as e:
            raise ClipError("the inspector did not start: %s" % e)
        self.scale = 1.0

    def cmd(self, line, ok=True):
        try:
            reply = self.server.send(line)
        except SystemExit as e:
            raise ClipError("the inspector ended at '%s': %s" % (line, e))
        try:
            obj = json.loads(reply)
        except ValueError:
            raise ClipError("not a reply to '%s': %s" % (line, reply[:200]))
        if ok and not obj.get("ok"):
            raise ClipError("%s -> %s" % (line, obj.get("error") or reply[:300]))
        return obj

    def close(self):
        try:
            self.server.close()
        except (SystemExit, OSError):
            pass

    def render(self, path):
        r = self.cmd("render path=%s" % path.replace("\\", "/"))
        self.scale = float(r.get("scale", self.scale))
        return path

    def rect(self, key):
        """The first visible match of `key` (`key#n`: the nth), as (x, y, w, h) DIP, or None."""
        name, _, nth = key.partition("#")
        r = self.cmd("find key=%s visibleOnly=true" % name, ok=False)
        nodes = [n for n in r.get("nodes", []) if n.get("shown", True) and n.get("onScreen", 1) > 0]
        i = int(nth) if nth else 0
        if len(nodes) <= i:
            return None
        rc = nodes[i]["rect"]
        return (rc["x"], rc["y"], rc["w"], rc["h"])


# ───────────────────────────── region ─────────────────────────────

def union(rects):
    rects = [r for r in rects if r and r[2] > 0 and r[3] > 0]
    if not rects:
        return None
    x0 = min(r[0] for r in rects)
    y0 = min(r[1] for r in rects)
    x1 = max(r[0] + r[2] for r in rects)
    y1 = max(r[1] + r[3] for r in rects)
    return (x0, y0, x1 - x0, y1 - y0)


def crop_box(region, found, focus, win, out_size, within=None):
    """The crop, in DIP: the union of the region's rects (+ pad), grown to the output aspect (contain, or `fit` width or
    height), at least `minWidth` DIP wide, centred on the focus key (or held to the union's `anchor` edge), kept inside `within` (a key's rect,
    where it fits), inside the union with clamp="keys", and always inside the window."""
    aspect = out_size[0] / float(out_size[1])
    if region.get("rect"):
        u = tuple(region["rect"])
    else:
        u = union(found)
    if u is None:
        raise ClipError("no region: none of %s was found on screen" % region.get("keys"))
    pad = float(region.get("pad", 16))
    x0, y0, x1, y1 = u[0] - pad, u[1] - pad, u[0] + u[2] + pad, u[1] + u[3] + pad
    w, h = x1 - x0, y1 - y0
    fit = region.get("fit", "contain")
    if fit == "width" or (fit == "contain" and w / h > aspect):
        cw, ch = w, w / aspect
    else:
        cw, ch = h * aspect, h
    min_w = float(region.get("minWidth", 320))
    if cw < min_w:
        cw, ch = min_w, min_w / aspect
    if cw > win[0]:
        cw, ch = win[0], win[0] / aspect
    if ch > win[1]:
        cw, ch = win[1] * aspect, win[1]
    cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
    if focus:
        fx, fy = focus[0] + focus[2] / 2, focus[1] + focus[3] / 2
        if fit != "width":
            cx = fx
        if fit != "height":
            cy = fy
    left, top = cx - cw / 2, cy - ch / 2
    anchor = region.get("anchor")
    if anchor == "left":
        left = x0
    elif anchor == "right":
        left = x1 - cw
    elif anchor == "top":
        top = y0
    elif anchor == "bottom":
        top = y1 - ch
    left += float(region.get("dx", 0))
    top += float(region.get("dy", 0))
    if region.get("clamp") == "keys":
        if cw <= w:
            left = min(max(left, x0), x1 - cw)
        if ch <= h:
            top = min(max(top, y0), y1 - ch)
    if within:
        if cw <= within[2]:
            left = min(max(left, within[0]), within[0] + within[2] - cw)
        if ch <= within[3]:
            top = min(max(top, within[1]), within[1] + within[3] - ch)
    left = min(max(left, 0), win[0] - cw)
    top = min(max(top, 0), win[1] - ch)
    return (left, top, cw, ch)


# ───────────────────────────── frames ─────────────────────────────

def signature(path, box_px):
    from PIL import Image
    with Image.open(path) as im:
        im = im.convert("L")
        if box_px:
            im = im.crop(box_px)
        return im.resize((320, 180))


def differs(a, b, fraction=0.0005, level=4):
    """Renders of a still scene are pixel-identical (the inspector's clock is fake and its draw deterministic), so any
    real change moves pixels: more than `fraction` of them by more than `level` is motion. A mean would call a slow
    sweep still, frame after frame."""
    from PIL import ImageChops
    if a is None or b is None:
        return True
    hist = ImageChops.difference(a, b).histogram()
    moved = sum(hist[level + 1:])
    return moved > fraction * a.size[0] * a.size[1]


class Recorder:
    """Captures one clip x theme x mode into a list of segments: ("hold", png) and ("run", [png...], step_ms)."""

    def __init__(self, sess, entry, theme, mode, defaults, work):
        self.s, self.e, self.theme, self.mode, self.d, self.work = sess, entry, theme, mode, defaults, work
        self.n = 0
        self.found = []                   # region rects seen in any state
        self.focus = None
        self.within = None
        self.still_box = None
        self.win = (0, 0)
        self.notes = []

    def get(self, key):
        return setting_for(self.e, self.theme, key, self.d)

    def region(self):
        """The entry's region with each field resolved for this theme."""
        return {k: per_theme(v, self.theme) for k, v in (self.get("region") or {}).items()}

    def frame(self):
        path = os.path.join(self.work, "f%04d.png" % self.n)
        self.n += 1
        return self.s.render(path)

    def mount(self):
        w, h = self.get("w"), self.get("h")
        self.win = (float(w), float(h))
        args = ["mount", "w=%s" % w, "h=%s" % h, "scale=%s" % self.get("scale"), "theme=" + self.theme, "kind=" + self.mode]
        route = self.get("route")
        if route:
            args.append("route=" + route)
        mount_args = dict(self.d.get("mountArgs") or {})              # the defaults (images=real: covers as the product shows them)
        mount_args.update(per_theme(self.e.get("mountArgs"), self.theme) or {})
        for k, v in mount_args.items():
            args.append("%s=%s" % (k, fmt_value(v)))
        r = self.s.cmd(" ".join(args))
        self.s.scale = float((r.get("size") or {}).get("scale", self.s.scale))
        for line in self.e.get("setup", []):
            line = per_theme(line, self.theme)
            if line:
                self.s.cmd(line)

    def write(self, value, settle=True):
        """The dial's value through its own writer. A dial this build does not have: recorded at the build's default when
        the entry says withoutDial=record (its pending part is the dial), else the clip fails."""
        dial = self.e.get("dial")
        if dial is None or value is None:
            return
        line = "setting id=%s value=%s" % (dial, fmt_value(value))
        if not settle:
            line += " settle=false"
        r = self.s.cmd(line, ok=False)
        if r.get("ok"):
            return
        err = str(r.get("error", ""))
        if err.startswith("no Settings dial") and self.e.get("withoutDial") == "record":
            if "no dial" not in self.notes:
                self.notes.append("no dial")
            return
        raise ClipError("%s -> %s" % (line, err))

    def rest(self):
        self.s.cmd("settle max=%d" % int(self.get("settleMax")))
        if int(self.get("rest")) > 0:
            self.s.cmd("frames %d" % int(self.get("rest")))

    def look(self):
        region = self.region()
        for key in region.get("keys", []):
            r = self.s.rect(key)
            if r:
                self.found.append(r)
        fkey = region.get("focus")
        if fkey and self.focus is None:
            self.focus = self.s.rect(fkey)
        w = region.get("within")
        if w and self.within is None:
            self.within = tuple(w) if isinstance(w, list) else self.s.rect(w)
        u = union(self.found)
        if u and self.still_box is None and self.get("watch") == "region":
            sc = self.s.scale
            self.still_box = tuple(int(round(v * sc)) for v in (u[0], u[1], u[0] + u[2], u[1] + u[3]))

    def run_until_still(self, act=None, values=None):
        """Capture every `step` frames until the window (watch="region": the region keys) holds still for `stillFrames`
        captures, or `capture` seconds pass. watch="none" never judges stillness: the whole `capture` is the change (a
        countdown whose digit and thin progress bar move too little to tell from a still frame). `values`: a sweep, one
        dial write per capture before its frames."""
        step = int(self.get("step"))
        max_n = int(math.ceil(float(self.get("capture")) * 1000 / (FRAME_MS * step)))
        min_n = int(math.ceil(float(self.get("minCapture")) * 1000 / (FRAME_MS * step)))
        still_need = int(self.get("stillFrames"))
        never_still = self.get("watch") == "none"
        paths, prev, still = [], None, 0
        if act:
            act()
        i = 0
        sweep_n = len(values) if values else 0
        while True:
            if i < sweep_n:
                self.write(values[i], settle=False)
            self.s.cmd("frames %d" % step)
            p = self.frame()
            paths.append(p)
            if never_still:
                moved = True
            else:
                sig = signature(p, self.still_box)
                moved = differs(sig, prev)
                prev = sig
            i += 1
            still = 0 if (moved or i <= sweep_n) else still + 1    # a sweep's frames are the change, never the hold
            if i >= sweep_n and i >= min_n and still >= still_need:
                break
            if i >= max_n + sweep_n:
                self.notes.append("capture cap")
                break
        last = paths[-1]
        if still:                                    # the still tail is the after hold, not part of the run
            paths = paths[:len(paths) - still]
        return paths, last

    def after_hold(self, segs, run, last, p0, p1):
        """The run, then the hold on the after state: the last captured frame (the still tail's, or where a change that never
        stops was cut), so the hold is the very frame the change ended on. A fresh frame later would jump: lyrics move on,
        a loop runs on. `p0`/`p1`: the before/after cue across the run (0 before, 1 after; see Framer)."""
        if run:
            segs.append(("run", run, run_ms(self), p0, p1))
        segs.append(("hold", last, p1))

    def record(self):
        kind = self.e["kind"]
        segs = []
        self.mount()
        if kind == "state":
            before, after = self.get("before"), self.get("after")
            self.write(before)
            self.rest()
            self.look()
            segs.append(("hold", self.frame(), 0.0))
            afters = after if isinstance(after, list) else [after]
            for k, value in enumerate(afters):
                sweep = float(self.get("sweep") or 0)
                if sweep and isinstance(value, (int, float)) and not isinstance(value, bool):
                    start = before if k == 0 else afters[k - 1]
                    n = max(2, int(round(sweep * 1000 / (FRAME_MS * int(self.get("step"))))))
                    vals = [start + (value - start) * ease(j / float(n)) for j in range(1, n + 1)]
                    run, last = self.run_until_still(values=vals)
                else:
                    run, last = self.run_until_still(act=lambda v=value: self.write(v, settle=False))
                self.after_hold(segs, run, last, 0.0 if k == 0 else 1.0, 1.0)
                self.look()
        elif kind == "motion":
            takes = self.e.get("takes") or [{"value": self.e.get("value")}]
            for k, take in enumerate(takes):
                if k > 0:
                    self.mount()
                self.write(per_theme(take.get("value"), self.theme))
                self.rest()
                self.look()
                # the cue: one take goes from before to after across its change; with takes, the first is "before", the last "after"
                p0 = 0.0 if len(takes) == 1 else k / float(len(takes) - 1)
                p1 = 1.0 if len(takes) == 1 else p0
                segs.append(("hold", self.frame(), p0))
                lines = [per_theme(a, self.theme) for a in (take.get("action") or self.e.get("action") or [])]
                lines = [ln if "settle=" in ln or ln.split()[0] == "frames" else ln + " settle=false" for ln in lines if ln]
                run, last = self.run_until_still(act=lambda: [self.s.cmd(ln) for ln in lines])
                self.after_hold(segs, run, last, p0, p1)
                self.look()
        else:
            raise ClipError("unknown kind %r" % kind)
        region = self.region()
        box = crop_box(region, self.found, self.focus, self.win, tuple(self.d["size"]), self.within)
        return segs, box


def ease(t):
    return t * t * (3 - 2 * t)


def run_ms(rec):
    return int(rec.get("step")) * FRAME_MS


# ───────────────────────────── timeline and encode ─────────────────────────────

def timeline(segs, fps, slow, hold_s, ramp_ms):
    """Segments -> [[source, ms, is_hold, cue]]: a hold is one frame of `hold_s`; a run is resampled to `fps` (slowed by
    `slow`). `cue` is the before/after cue's phase (0 before, 1 after): a run cross-fades it from its p0 to its p1 over its
    first `ramp_ms` (or all of it, when shorter)."""
    out = []
    frame_ms = 1000.0 / fps
    for seg in segs:
        if seg[0] == "hold":
            out.append([seg[1], hold_s * 1000.0, True, seg[2]])
            continue
        _, paths, step_ms, p0, p1 = seg
        dur = len(paths) * step_ms * slow
        n = max(1, int(round(dur / frame_ms)))
        ramp = max(frame_ms, min(ramp_ms, n * frame_ms))
        for k in range(n):
            src = paths[min(len(paths) - 1, int(round(k * frame_ms / slow / step_ms)))]
            out.append([src, frame_ms, False, p0 + (p1 - p0) * ease(min(1.0, (k + 1) * frame_ms / ramp))])
    return out


def merge_frames(frames, entries, loop_min, max_still=None):
    """Consecutive identical frames (the shots, before the frame) become one. A hold keeps exactly its own length: run
    frames that look like it (the reaction before a change starts, the still tail after it ends) fold into it without
    lengthening it. A still stretch inside the change is cut to `max_still` seconds when set (a marquee's start delay).
    Then the holds grow evenly until the loop is `loop_min` long. Returns the frames, their durations and each one's
    before/after cue (entry[3]): a hold's own, else the last run frame's merged into it, so the cue never keeps a still
    stretch from merging (it moves with the change)."""
    from PIL import ImageChops
    out = []                                              # [frame, run_ms, hold_ms, is_hold, cue]
    for f, entry in zip(frames, entries):
        ms, hold, cue = entry[1], entry[2], entry[3]
        if out and ImageChops.difference(out[-1][0], f).getbbox() is None:
            m = out[-1]
            if hold:
                m[2] += ms
                m[3] = True
                m[4] = cue
            else:
                m[1] += ms
                if not m[3]:
                    m[4] = cue
        else:
            out.append([f, 0.0 if hold else ms, ms if hold else 0.0, hold, cue])
    for m in out:
        if m[3]:
            m[1] = 0.0
        elif max_still:
            m[1] = min(m[1], max_still * 1000.0)
    total = sum(m[1] + m[2] for m in out)
    holds = [m for m in out if m[3]]
    if total < loop_min * 1000 and holds:
        extra = (loop_min * 1000 - total) / len(holds)
        for m in holds:
            m[2] += extra
    return [m[0] for m in out], integer_durations([m[1] + m[2] for m in out]), [m[4] for m in out]


def integer_durations(ms_list):
    """Whole milliseconds (WebP stores no fractions), the rounding error carried so the loop length stays exact."""
    out, carry = [], 0.0
    for ms in ms_list:
        v = ms + carry
        r = int(round(v))
        carry = v - r
        out.append(max(1, r))
    return out


def shot_of(path, box_dip, scale, size):
    """One capture cropped to the box (DIP) and scaled to the frame's shot size."""
    from PIL import Image
    left, top, w, h = (v * scale for v in box_dip)
    box = (int(round(left)), int(round(top)), int(round(left + w)), int(round(top + h)))
    with Image.open(path) as im:
        return im.convert("RGB").crop(box).resize(size, Image.LANCZOS)


def shots(entries, box_dip, scale, size, cache, fade=None):
    """Each timeline entry's capture, cropped to the box, scaled to the frame's shot size and faded at its cut edges."""
    out = []
    for e in entries:
        if e[0] not in cache:
            img = shot_of(e[0], box_dip, scale, size)
            cache[e[0]] = fade.apply(img) if fade else img
        out.append(cache[e[0]])
    return out


SIDES = ("top", "bottom", "left", "right")


class EdgeFade:
    """The shot's cut edges (owner, 2026-11-19: a crop that cuts content mid-word or mid-line reads unfinished). Where the content
    honestly runs on past the crop, that edge fades to the shot's own ground over `width` px instead of a hard cut: the lines
    above and below a lyric, a track list's trailing column. Planned once per clip (`plan`) from its first and last captures:
    a side is faded when the crop edge lies inside the window (a window edge is a real edge: the full screen, the mini
    player) and content touches it (pixels off the ground in the outermost rows or columns); its ground is the commonest
    colour of the band along it. The region's `fade` overrides it: a list of sides, or false for none."""

    def __init__(self, sides, size, width):
        from PIL import Image
        self.sides = sides
        self.layers = []
        w, h = size
        for side, rgb in sides:
            ramp = Image.new("L", (w, h), 0)
            px = ramp.load()
            n = int(width)
            for k in range(n):
                v = int(round(255 * (1.0 - ease((k + 0.5) / n))))
                if side in ("top", "bottom"):
                    y = k if side == "top" else h - 1 - k
                    for x in range(w):
                        px[x, y] = max(px[x, y], v)
                else:
                    x = k if side == "left" else w - 1 - k
                    for y in range(h):
                        px[x, y] = max(px[x, y], v)
            self.layers.append((Image.new("RGB", (w, h), rgb), ramp))

    def apply(self, img):
        from PIL import Image
        for ground, ramp in self.layers:
            img = Image.composite(ground, img, ramp)
        return img

    @staticmethod
    def band(img, side, depth):
        w, h = img.size
        return img.crop({"top": (0, 0, w, depth), "bottom": (0, h - depth, w, h),
                         "left": (0, 0, depth, h), "right": (w - depth, 0, w, h)}[side])

    @staticmethod
    def ground(band):
        """The commonest colour of a band (quantised, so anti-aliasing and lossy noise do not split it)."""
        q = band.point(lambda v: (v // 6) * 6)
        colours = q.getcolors(q.size[0] * q.size[1]) or [(1, (0, 0, 0))]
        return max(colours, key=lambda c: c[0])[1]

    @classmethod
    def touches(cls, img, side, ground, level=24, share=0.006):
        edge = cls.band(img, side, 2)
        n = 0
        pixels = edge.get_flattened_data() if hasattr(edge, "get_flattened_data") else edge.getdata()
        for px in pixels:
            if max(abs(px[0] - ground[0]), abs(px[1] - ground[1]), abs(px[2] - ground[2])) > level:
                n += 1
        return n > share * edge.size[0] * edge.size[1]

    @classmethod
    def plan(cls, setting, box, win, samples, width):
        """`setting`: "auto" (the default), a list of sides, or false. `box`/`win`: the crop and the window (DIP); `samples`: shots
        (first, last) at the shot size."""
        if setting is False or not samples:
            return None
        inside = {"left": box[0] > 0.5, "top": box[1] > 0.5,
                  "right": box[0] + box[2] < win[0] - 0.5, "bottom": box[1] + box[3] < win[1] - 0.5}
        sides = []
        for side in SIDES:
            if isinstance(setting, list):
                if side not in setting:
                    continue
            elif not inside[side]:
                continue
            g = cls.ground(cls.band(samples[0], side, 14))
            if isinstance(setting, list) or any(cls.touches(sh, side, g) for sh in samples):
                sides.append((side, g))
        return cls(sides, samples[0].size, width) if sides else None


def framed(merged, framer):
    """merge_frames' answer composed into the frame: (frames, durations)."""
    frames, durations, cues = merged
    return [framer.compose(f, cue) for f, cue in zip(frames, cues)], durations


def encode(frames, durations, quality):
    buf = io.BytesIO()
    frames[0].save(buf, format="WEBP", save_all=True, append_images=frames[1:], duration=durations, loop=0,
                   quality=quality, method=6, allow_mixed=True, minimize_size=True, kmin=0, kmax=0)
    return buf.getvalue()


def encode_at_budget(build, budget, fps0, q0):
    """build(fps) -> (frames, durations). Tries the quality ladder (`q0`, then down to 55 at `fps0`, then 12 fps from 55
    down to 25) until the bytes fit."""
    last = None
    cache = {}
    ladder = [(fps0, q) for q in (q0,) + tuple(q for q in QUALITIES if q < q0)] + [(min(12, fps0), q) for q in QUALITIES_12]
    for fps, q in ladder:
        if fps not in cache:
            cache[fps] = build(fps)
        frames, durations = cache[fps]
        data = encode(frames, durations, q)
        last = (data, fps, q, frames, durations)
        if len(data) <= budget:
            return last, True
    return last, False


def contact_sheet(frames, durations, path, label):
    from PIL import Image, ImageDraw
    cols = 6
    tw, th = 213, 120
    rows = (len(frames) + cols - 1) // cols
    sheet = Image.new("RGB", (cols * tw, rows * (th + 14) + 16), (24, 24, 24))
    d = ImageDraw.Draw(sheet)
    d.text((4, 2), label, fill=(230, 230, 230))
    t = 0
    for i, (f, ms) in enumerate(zip(frames, durations)):
        x, y = (i % cols) * tw, 16 + (i // cols) * (th + 14)
        sheet.paste(f.resize((tw - 2, th)), (x, y))
        d.text((x + 2, y + th), "#%d %dms @%dms" % (i, ms, t), fill=(200, 200, 200))
        t += ms
    os.makedirs(os.path.dirname(path), exist_ok=True)
    sheet.save(path)


# ───────────────────────────── diagrams (sound dials) ─────────────────────────────

def mix(c1, c2, t):
    return tuple(int(round(a + (b - a) * t)) for a, b in zip(c1, c2))


def hexrgba(s):
    s = s.lstrip("#")
    return tuple(int(s[i:i + 2], 16) for i in (0, 2, 4, 6)) if len(s) == 8 else tuple(int(s[i:i + 2], 16) for i in (0, 2, 4)) + (255,)


def over(c, bg):
    a = c[3] / 255.0
    return tuple(int(round(c[i] * a + bg[i] * (1 - a))) for i in range(3))


def luminance(c):
    def ch(v):
        v /= 255.0
        return v / 12.92 if v <= 0.03928 else ((v + 0.055) / 1.055) ** 2.4
    return 0.2126 * ch(c[0]) + 0.7152 * ch(c[1]) + 0.0722 * ch(c[2])


def contrast(a, b):
    la, lb = sorted((luminance(a), luminance(b)), reverse=True)
    return (la + 0.05) / (lb + 0.05)


def probe_palette(inspect_mod, exe, theme, mode, work, defaults):
    """The theme's own colours, read from the real app: the Settings page (Playback, crossfade on) rendered and dumped.
    bg = the commonest pixel of the content area; ink = the strongest text colour; ink2 = the commonest other text colour;
    accent = the commonest saturated fill or text colour (a switch that is on, the selected tab, a link)."""
    from PIL import Image
    sess = Session(inspect_mod, exe)
    try:
        sess.cmd("mount w=1440 h=900 theme=%s kind=%s route=settings arg=playback" % (theme, mode))
        sess.cmd("setting id=playback.crossfade value=true")
        sess.cmd("settle max=%d" % int(defaults["settleMax"]))
        png = os.path.join(work, "palette-%s-%s.png" % (theme, mode))
        sess.render(png)
        dump = sess.cmd("dump max=4000")
    finally:
        sess.close()
    with Image.open(png) as im:
        im = im.convert("RGB")
        w, h = im.size
        area = im.crop((int(w * 0.3), int(h * 0.15), int(w * 0.95), int(h * 0.85))).resize((260, 140))
        bg = area.getcolors(260 * 140)
        bg = max(bg, key=lambda c: c[0])[1]
    texts, sats = collections.Counter(), collections.Counter()
    for n in dump.get("nodes", []):
        t = n.get("text")
        if isinstance(t, dict) and t.get("string") and not str(t["string"]).startswith("glyph:") and t.get("color"):
            texts[t["color"]] += len(t["string"])
        for c, weight in ((n.get("fill"), n["rect"]["w"] * n["rect"]["h"]),
                          ((t or {}).get("color") if isinstance(t, dict) else None, 50)):
            if not c:
                continue
            rgba = hexrgba(c)
            hh, s, v = colorsys.rgb_to_hsv(*(x / 255.0 for x in rgba[:3]))
            if s > 0.35 and v > 0.3 and rgba[3] > 200:
                sats[c] += weight
    total = sum(texts.values()) or 1
    cands = [(over(hexrgba(c), bg), n) for c, n in texts.items() if n >= 0.08 * total]
    ink = max(cands, key=lambda cn: contrast(cn[0], bg))[0] if cands else ((255, 255, 255) if luminance(bg) < 0.4 else (0, 0, 0))
    others = [(c, n) for c, n in cands if c != ink and contrast(c, bg) >= 2.5]
    ink2 = max(others, key=lambda cn: cn[1])[0] if others else mix(ink, bg, 0.4)
    accent = over(hexrgba(sats.most_common(1)[0][0]), bg) if sats else ink
    return {"bg": bg, "ink": ink, "ink2": ink2, "accent": accent}


def wave_heights(n, seed):
    """A fixed pseudo-random waveform (the same on every run): loud enough everywhere that a fade reads as a fade."""
    out, x = [], seed
    for i in range(n):
        x = (x * 1103515245 + 12345) & 0x7FFFFFFF
        r = (x >> 8) / float(1 << 23)
        out.append(0.55 + 0.30 * (0.5 + 0.5 * math.sin(i * 0.7 + seed)) + 0.15 * r)
    return out


def draw_crossfade(t, pal, style, size):
    """Two songs on one timeline, the first in the quiet ink, the next in the accent. t=0 (crossfade off): the first ends
    at full volume, a short gap, the next starts at full volume. As t goes to 1 (crossfade on) the next slides under the
    first's end; across the overlap the first fades out while the next fades in."""
    from PIL import Image, ImageDraw
    W, H = size
    img = Image.new("RGB", size, pal["bg"])
    layer_b = Image.new("RGBA", size, (0, 0, 0, 0))
    da, db = ImageDraw.Draw(img), ImageDraw.Draw(layer_b)
    mid, amp = H * 0.5, H * 0.34
    pitch = W / 56.0
    bar = pitch * 0.55
    rad = min(style.get("radius", 2) * 1.5, bar / 2)
    a_end = W * 0.60
    b_start = W * (0.66 - 0.34 * t)
    fade = max(0.0, a_end - b_start)
    ha, hb = wave_heights(80, 7), wave_heights(80, 3)
    da.line((0, mid, W, mid), fill=mix(pal["ink2"], pal["bg"], 0.6), width=max(2, int(H / 240)))
    i, x = 0, a_end - pitch
    while x > -pitch:                                   # the first song, laid out back from its end
        env = 1.0 if fade <= 0 else max(0.0, min(1.0, (a_end - x - bar) / fade))
        hgt = max(bar * 0.35, amp * ha[i % len(ha)] * env)
        da.rounded_rectangle((x, mid - hgt, x + bar, mid + hgt), radius=rad, fill=pal["ink2"])
        x -= pitch
        i += 1
    i, x = 0, b_start
    while x < W + pitch:                                # the next song, from its start
        env = 1.0 if fade <= 0 else max(0.0, min(1.0, (x - b_start) / fade))
        hgt = max(bar * 0.35, amp * hb[i % len(hb)] * env)
        db.rounded_rectangle((x, mid - hgt, x + bar, mid + hgt), radius=rad, fill=pal["accent"] + (225,))
        x += pitch
        i += 1
    img.paste(layer_b, (0, 0), layer_b)
    return img


def draw_levels(levels, target, pal, style, size, show_target):
    """Loudness bars: one bar per song, a dashed line at the level they are brought to."""
    from PIL import Image, ImageDraw
    W, H = size
    img = Image.new("RGB", size, pal["bg"])
    d = ImageDraw.Draw(img)
    n = len(levels)
    base = H * 0.86
    top = H * 0.12
    span = base - top
    slot = W * 0.80 / n
    x0 = W * 0.10
    rad = style.get("radius", 2) * 2
    for i, lv in enumerate(levels):
        bx = x0 + slot * i + slot * 0.18
        bw = slot * 0.64
        d.rounded_rectangle((bx, base - span * lv, bx + bw, base), radius=min(rad, bw / 2), fill=pal["accent"])
    d.line((x0 - W * 0.04, base, x0 + slot * n + W * 0.04, base), fill=mix(pal["ink2"], pal["bg"], 0.35), width=max(2, int(H / 120)))
    if show_target is not None:
        y = base - span * target
        dash, x = W / 48.0, x0 - W * 0.04
        col = mix(pal["ink"], pal["bg"], 1.0 - show_target)
        while x < x0 + slot * n + W * 0.04:
            d.line((x, y, min(x + dash * 0.6, x0 + slot * n + W * 0.04), y), fill=col, width=max(2, int(H / 90)))
            x += dash
    return img


def diagram_frames(name, pal, style, fps, hold_s, change_s):
    """[(PIL image 1280x720, ms, is_hold)] for a sound dial's diagram, drawn large and scaled down into the frame's shot
    (smooth edges): a hold on the before state, the change at `fps`, a hold on the after state."""
    size2 = (1280, 720)
    frames = []

    def add(img, ms, hold=False):
        frames.append((img, ms, hold))

    n = max(2, int(round(change_s * fps)))
    step = 1000.0 / fps
    if name == "crossfade":
        add(draw_crossfade(0.0, pal, style, size2), hold_s * 1000, True)
        for k in range(1, n):
            add(draw_crossfade(ease(k / float(n)), pal, style, size2), step)
        add(draw_crossfade(1.0, pal, style, size2), hold_s * 1000, True)
    elif name == "normalization":
        loud = [0.92, 0.38, 0.70, 0.25, 0.84, 0.50]
        target = 0.58
        add(draw_levels(loud, target, pal, style, size2, None), hold_s * 1000, True)
        for k in range(1, n):
            e = ease(k / float(n))
            add(draw_levels([v + (target - v) * e for v in loud], target, pal, style, size2, min(1.0, e * 2)), step)
        add(draw_levels([target] * len(loud), target, pal, style, size2, 1.0), hold_s * 1000, True)
    elif name == "loudness":
        levels = (0.38, 0.58, 0.82)                       # the Quiet, Normal and Loud levels
        count = 6
        add(draw_levels([levels[0]] * count, levels[0], pal, style, size2, 1.0), hold_s * 1000, True)
        m = max(2, n // 2)
        for j, (a, b) in enumerate(((levels[0], levels[1]), (levels[1], levels[2]))):
            for k in range(1, m):
                v = a + (b - a) * ease(k / float(m))
                add(draw_levels([v] * count, v, pal, style, size2, 1.0), step)
            add(draw_levels([b] * count, b, pal, style, size2, 1.0), hold_s * 1000 * (0.6 if j == 0 else 1.0), True)
    else:
        raise ClipError("unknown diagram %r" % name)
    return frames


# ───────────────────────────── the frame ─────────────────────────────

def hexrgb(s):
    s = s.lstrip("#")
    return tuple(int(s[i:i + 2], 16) for i in (0, 2, 4))


def desaturate(c, keep):
    h, sat, v = colorsys.rgb_to_hsv(*(x / 255.0 for x in c))
    return tuple(int(round(x * 255)) for x in colorsys.hsv_to_rgb(h, sat * keep, v))


def backdrop(kind, ground, accent, strength, size):
    """The quiet ground behind the shot, drawn small and scaled up (a smooth gradient): `mica` (WinUI) a diagonal from the
    ground at the top left to a low-saturation tint of the accent at the bottom right; `wash` (Classic) the accent's
    desaturated tint washing down from the top edge, the way Classic's tinted surfaces sit under a page head; `glow` (Zune) the
    flat ground with one faint radial glow of the accent in the top right corner. `strength` is how far the ground moves
    toward the tint at most: barely visible."""
    from PIL import Image
    W, H = size
    sw, sh = 96, 54
    img = Image.new("RGB", (sw, sh))
    px = img.load()
    soft = desaturate(accent, 0.4)
    for j in range(sh):
        for i in range(sw):
            u, v = (i + 0.5) / sw, (j + 0.5) / sh
            if kind == "mica":
                t = ease(min(1.0, max(0.0, 0.6 * u + 0.4 * v)))
                c = mix(ground, soft, t * strength)
            elif kind == "wash":
                t = 1.0 - ease(min(1.0, v / 0.8))
                c = mix(ground, soft, t * strength)
            elif kind == "glow":
                d = math.hypot((1.0 - u) * W / float(H), v)
                t = max(0.0, 1.0 - d / 1.25) ** 2
                c = mix(ground, accent, t * strength)
            else:
                raise ClipError("unknown backdrop %r" % kind)
            px[i, j] = c
    return img.resize((W, H), Image.BICUBIC)


def aa_mask(size, radius, outline=0):
    """An anti-aliased rounded-rect mask (L), drawn at 4x and scaled down; `outline` > 0: only a ring that many px wide."""
    from PIL import Image, ImageDraw
    k = 4
    w, h = size
    m = Image.new("L", (w * k, h * k), 0)
    d = ImageDraw.Draw(m)
    box = (0, 0, w * k - 1, h * k - 1)
    if outline:
        d.rounded_rectangle(box, radius=radius * k, outline=255, width=outline * k)
    else:
        d.rounded_rectangle(box, radius=radius * k, fill=255)
    return m.resize(size, Image.LANCZOS)


class Framer:
    """The presentation frame every clip is composed into (owner, 2026-11-19: framed, "not too bland", but not too much),
    from the manifest's `defaults.frame`, per theme and app mode: the theme's quiet backdrop; the shot inset at 16:9 with
    the theme's corner radius, a 1 px hairline border and a soft shadow under it; and under the shot a before/after cue of
    two dots, the first filled during the before hold, the second during the after hold, cross-fading during the change.
    No text, no zoom, no bezel. The frame is static, so in an animated WebP it is paid for once, in the key frame."""

    def __init__(self, cfg, theme, mode, size):
        from PIL import Image, ImageDraw, ImageFilter
        W, H = self.size = tuple(size)
        x, y, w, h = (int(v) for v in cfg["shot"])
        self.pos, self.shot = (x, y), (w, h)
        dark = mode == "dark"
        ground, accent = hexrgb(cfg["ground"][theme][mode]), hexrgb(cfg["accent"][theme][mode])
        ink = (255, 255, 255) if dark else (0, 0, 0)
        radius = float(per_theme(cfg["radius"], theme))
        bd = cfg["backdrop"][theme]
        base = backdrop(bd["kind"], ground, accent, float(bd["strength"][mode]), (W, H)).convert("RGBA")
        sh = cfg["shadow"]
        shadow = Image.new("RGBA", (W, H), (0, 0, 0, 0))
        ImageDraw.Draw(shadow).rounded_rectangle((x, y + sh["dy"], x + w - 1, y + h - 1 + sh["dy"]), radius=radius,
                                                 fill=(0, 0, 0, int(round(255 * float(sh["alpha"][mode])))))
        base = Image.alpha_composite(base, shadow.filter(ImageFilter.GaussianBlur(float(sh["blur"]))))
        self.base = base.convert("RGB")
        self.mask = aa_mask((w, h), radius)
        ring = aa_mask((w, h), radius, outline=1).point(lambda a: int(round(a * float(cfg["border"][mode]))))
        self.overlay = Image.new("RGBA", (W, H), ink + (0,))
        layer = Image.new("RGBA", (w, h), ink + (0,))
        layer.putalpha(ring)
        self.overlay.alpha_composite(layer, (x, y))
        cue = cfg["cue"]
        self.ramp_ms = float(cue["rampMs"])
        n = int(cue["size"])
        dot = Image.new("L", (n * 4, n * 4), 0)
        ImageDraw.Draw(dot).ellipse((0, 0, n * 4 - 1, n * 4 - 1), fill=255)
        self.dot = dot.resize((n, n), Image.LANCZOS)
        pitch = n + int(cue["gap"])
        cy = int(cue["y"]) - n // 2
        self.dots = [(W // 2 - pitch // 2 - n // 2, cy), (W // 2 + pitch // 2 - n // 2, cy)]
        self.alpha = (float(cue["alpha"][0]), float(cue["alpha"][1]))
        self.ink = ink
        self._overlays = {}

    def overlay_for(self, cue):
        """The border and the two dots at cue phase `cue` (0 before, 1 after)."""
        from PIL import Image
        key = round(cue, 3)
        if key not in self._overlays:
            ov = self.overlay.copy()
            lo, hi = self.alpha
            for (dx, dy), a in zip(self.dots, (lo + (hi - lo) * (1.0 - key), lo + (hi - lo) * key)):
                layer = Image.new("RGBA", self.dot.size, self.ink + (0,))
                layer.putalpha(self.dot.point(lambda v, a=a: int(round(v * a))))
                ov.alpha_composite(layer, (dx, dy))
            self._overlays[key] = ov
        return self._overlays[key]

    def compose(self, shot, cue):
        """One output frame: the shot (already at `self.shot`) on the backdrop, inside its rounded mask, under the border and the cue."""
        img = self.base.copy()
        img.paste(shot, self.pos, self.mask)
        ov = self.overlay_for(cue)
        img.paste(ov, (0, 0), ov)
        return img


def diagram_cued(frames, ramp_ms):
    """A diagram's frames with the before/after cue: 0 on the first hold, cross-fading to 1 over the first change's first
    `ramp_ms` (the whole change when shorter), 1 from then on."""
    out, t, started = [], 0.0, False
    change = sum(ms for _, ms, hold in frames[1:] if not hold) or ramp_ms
    for k, (img, ms, hold) in enumerate(frames):
        if k == 0:
            cue = 0.0
        else:
            if not hold:
                started = True
            t += ms if started and not hold else 0.0
            cue = 1.0 if hold else ease(min(1.0, t / max(1.0, min(ramp_ms, change))))
        out.append((img, ms, hold, cue))
    return out


# ───────────────────────────── one item ─────────────────────────────

def record_item(inspect_mod, exe, entry, theme, mode, defaults, budget, out_root, work_root, palettes, keep):
    from PIL import Image
    cid = entry["clip"]
    started = time.time()
    tag = "%s-%s-%s" % (theme, mode, cid)
    work = os.path.join(work_root, "frames", tag)
    shutil.rmtree(work, ignore_errors=True)
    os.makedirs(work, exist_ok=True)
    res = {"clip": cid, "theme": theme, "mode": mode}
    fps0 = int(setting_for(entry, theme, "fps", defaults))
    q0 = int(setting_for(entry, theme, "quality", defaults))
    hold_s = float(setting_for(entry, theme, "hold", defaults))
    loop_min, loop_max = defaults["loop"]
    size = tuple(defaults["size"])
    framer = Framer(defaults["frame"], theme, mode, size)
    notes = []
    if entry["kind"] == "diagram":
        pal = palettes[(theme, mode)]
        style = (defaults.get("diagramStyle") or {}).get(theme, {})
        change_s = float(setting_for(entry, theme, "change", defaults))

        def build(fps):
            fr = diagram_cued(diagram_frames(entry["diagram"], pal, style, fps, hold_s, change_s), framer.ramp_ms)
            return framed(merge_frames([img.resize(framer.shot, Image.LANCZOS) for img, _, _, _ in fr], fr, loop_min), framer)
        res["palette"] = {k: "#%02X%02X%02X" % v for k, v in pal.items()}
    else:
        sess = Session(inspect_mod, exe)
        try:
            rec = Recorder(sess, entry, theme, mode, defaults, work)
            segs, box = rec.record()
            scale = sess.scale
            notes += rec.notes
        finally:
            sess.close()
        slow = float(setting_for(entry, theme, "slow", defaults))
        max_still = setting_for(entry, theme, "maxStill", defaults)
        res["crop"] = [round(v, 1) for v in box]
        cache = {}
        region = {k: per_theme(v, theme) for k, v in (setting_for(entry, theme, "region", defaults) or {}).items()}
        fade = EdgeFade.plan(region.get("fade", "auto"), box, rec.win,
                             [shot_of(segs[0][1], box, scale, framer.shot), shot_of(segs[-1][1], box, scale, framer.shot)],
                             float(defaults["frame"].get("fade", 16)))
        res["fade"] = [side for side, _ in fade.sides] if fade else []
        if fade:
            notes.append("fade " + "/".join(res["fade"]))

        def build(fps):
            tl = timeline(segs, fps, slow, hold_s, framer.ramp_ms)
            return framed(merge_frames(shots(tl, box, scale, framer.shot, cache, fade), tl, loop_min, max_still), framer)
    (data, fps, q, frames, durations), fits = encode_at_budget(build, budget, fps0, q0)
    loop_ms = sum(durations)
    if loop_ms > loop_max * 1000:
        notes.append("loop %.1f s is over %.0f s" % (loop_ms / 1000.0, loop_max))
    dest = os.path.join(out_root, theme, mode, cid + ".webp")
    os.makedirs(os.path.dirname(dest), exist_ok=True)
    with open(dest, "wb") as fh:
        fh.write(data)
    contact_sheet(frames, durations, os.path.join(work_root, "sheets", tag + ".png"),
                  "%s  %s/%s  %d frames  %.2f s  q%d %dfps  %.1f KB" % (cid, theme, mode, len(frames), loop_ms / 1000.0, q, fps, len(data) / 1024.0))
    if not keep:
        shutil.rmtree(work, ignore_errors=True)
    res.update({"ok": True, "bytes": len(data), "fits": fits, "quality": q, "fps": fps, "frames": len(frames),
                "loopMs": loop_ms, "path": dest, "seconds": round(time.time() - started, 1), "notes": notes})
    return res


# ───────────────────────────── check ─────────────────────────────

def webp_facts(path):
    """Size, frames and durations of an animated WebP, and its VP8X flags. The alpha flag is reported, never judged:
    WebPAnimEncoder sets it whenever a sub-rectangle frame leaves pixels transparent, as on the shipped What's new motions;
    what slowed decoding was alpha in the source frames, and the frames here are RGB."""
    from PIL import Image
    facts = {"bytes": os.path.getsize(path)}
    with open(path, "rb") as fh:
        head = fh.read(32)
    facts["webp"] = head[:4] == b"RIFF" and head[8:12] == b"WEBP"
    facts["alpha"] = head[12:16] == b"VP8X" and bool(head[20] & 0x10)
    facts["animated"] = head[12:16] == b"VP8X" and bool(head[20] & 0x02)
    try:
        with Image.open(path) as im:
            facts["size"] = list(im.size)
            durs = []
            for i in range(getattr(im, "n_frames", 1)):
                im.seek(i)
                im.load()                                          # a frame's duration is read when it decodes
                durs.append(int(im.info.get("duration", 0)))
            facts["frames"] = len(durs)
            facts["loopMs"] = sum(durs)
            facts["minFrameMs"] = min(durs) if durs else 0
    except Exception as e:                                          # a file Pillow cannot read is reported, not raised
        facts["error"] = str(e)
    return facts


def check(manifest, root, strict, as_json):
    budget = manifest["budget"]
    clip_max, total_max = int(budget["clipBytes"]), int(budget["totalBytes"])
    size = list(manifest["defaults"]["size"])
    loop_min, loop_max = manifest["defaults"]["loop"]
    max_fps = int(manifest["defaults"].get("maxFps", 15))
    rows, errors, warnings = [], [], []
    expected = set()
    for entry, theme, mode in expand(manifest):
        rel = "%s/%s/%s.webp" % (theme, mode, entry["clip"])
        expected.add(rel)
        path = os.path.join(root, theme, mode, entry["clip"] + ".webp")
        row = {"clip": entry["clip"], "theme": theme, "mode": mode, "path": rel}
        if not os.path.exists(path):
            row["status"] = "pending" if waits(entry) else "missing"
            if row["status"] == "missing" and strict:
                errors.append("%s: missing" % rel)
            rows.append(row)
            continue
        f = webp_facts(path)
        row.update(f)
        problems = []
        if f.get("error") or not f["webp"]:
            problems.append("not a readable WebP (%s)" % f.get("error", "header"))
        else:
            if f["bytes"] > clip_max:
                problems.append("%.1f KB is over the %.0f KB clip budget" % (f["bytes"] / 1024.0, clip_max / 1024.0))
            if f.get("size") != size:
                problems.append("%s is not %dx%d" % (f.get("size"), size[0], size[1]))
            if not f["animated"] or f.get("frames", 0) < 2:
                problems.append("not animated")
            if not (loop_min * 1000 - 50 <= f.get("loopMs", 0) <= loop_max * 1000 + 50):
                problems.append("loop %.2f s is outside %.0f-%.0f s" % (f.get("loopMs", 0) / 1000.0, loop_min, loop_max))
            if f.get("minFrameMs", 0) and f["minFrameMs"] < int(1000 / max_fps) - 1:
                problems.append("a %d ms frame is faster than %d fps" % (f["minFrameMs"], max_fps))
        row["status"] = "bad" if problems else "ok"
        row["problems"] = problems
        errors += ["%s: %s" % (rel, p) for p in problems]
        rows.append(row)
    orphans, total = [], 0
    if os.path.isdir(root):
        for dirpath, _, files in os.walk(root):
            for fn in files:
                p = os.path.join(dirpath, fn)
                total += os.path.getsize(p)
                rel = os.path.relpath(p, root).replace("\\", "/")
                if rel not in expected:
                    orphans.append(rel)
    for o in orphans:
        warnings.append("%s: not in the manifest" % o)
    if total > total_max:
        errors.append("total %.2f MB is over the %.1f MB budget" % (total / 1048576.0, total_max / 1048576.0))
    counts = collections.Counter(r["status"] for r in rows)
    report = {"root": root, "budget": {"clipBytes": clip_max, "totalBytes": total_max}, "totalBytes": total,
              "expected": len(rows), "counts": dict(counts), "clips": rows, "orphans": orphans,
              "errors": errors, "warnings": warnings, "ok": not errors}
    if as_json:
        print(json.dumps(report, indent=1))
    else:
        print("settings-explain clips under %s" % root)
        for r in rows:
            if r["status"] in ("ok", "bad"):
                print("  %-4s %6.1f KB  %5.2f s  %3d fr  %s%s" % (r["status"], r["bytes"] / 1024.0, r.get("loopMs", 0) / 1000.0,
                                                               r.get("frames", 0), r["path"], ("  " + "; ".join(r["problems"])) if r["problems"] else ""))
        missing = [r["path"] for r in rows if r["status"] in ("missing", "pending")]
        print("  %d ok, %d bad, %d missing, %d pending (a surface or dial on another branch, not stageable headless yet, or no effect in a mode)" % (
            counts.get("ok", 0), counts.get("bad", 0), counts.get("missing", 0), counts.get("pending", 0)))
        if missing and not strict:
            print("  not recorded yet: %d (--strict makes a missing clip an error)" % len(missing))
        print("  total %.2f MB of %.1f MB (%d files); budget per clip %.0f KB" % (
            total / 1048576.0, total_max / 1048576.0, sum(1 for r in rows if "bytes" in r) + len(orphans), clip_max / 1024.0))
        for w in warnings:
            print("  warning: " + w)
        for e in errors:
            print("  ERROR: " + e)
    return 0 if not errors else 1


# ───────────────────────────── main ─────────────────────────────

def main():
    ap = argparse.ArgumentParser(description="Record, encode and check the Settings explanation-badge clips.")
    ap.add_argument("--exe", help="Wavee.exe to drive (a copy, never one another build writes to)")
    ap.add_argument("--manifest", default=MANIFEST)
    ap.add_argument("--out", default=ASSETS, help="where clips go (default: the app's assets/settings-explain)")
    ap.add_argument("--work", default=os.path.join(tempfile.gettempdir(), "wavee-settings-explain"),
                    help="frames, contact sheets, results.json")
    ap.add_argument("--only", nargs="*", default=[], help="clip ids (a trailing * matches a prefix: appearance.option.*)")
    ap.add_argument("--themes", nargs="*", choices=THEMES)
    ap.add_argument("--modes", nargs="*", choices=MODES)
    ap.add_argument("--pending", action="store_true", help="also try entries whose surface is marked as on another branch")
    ap.add_argument("--jobs", type=int, default=2, help="parallel inspector processes (1-4)")
    ap.add_argument("--keep-frames", action="store_true", help="keep the captured PNGs under --work/frames")
    ap.add_argument("--check", action="store_true", help="report the committed clips against the budget and exit")
    ap.add_argument("--strict", action="store_true", help="with --check: a missing clip is an error")
    ap.add_argument("--json", action="store_true", help="with --check: print the report as JSON")
    ap.add_argument("--list", action="store_true", help="print the expanded manifest and exit")
    args = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    manifest = read_json(args.manifest)

    if args.check:
        sys.exit(check(manifest, args.out, args.strict, args.json))

    items = expand(manifest, args.only, args.themes, args.modes)
    if args.list:
        for entry, theme, mode in items:
            p = entry.get("pending")
            print("%-44s %-7s %-5s %-8s %s" % (entry["clip"], theme, mode, entry["kind"],
                                                ("pending %s (%s)" % (p.get("branch") or "-", p["part"])) if p else ""))
        print("%d clips" % len(items))
        return

    if not args.exe:
        sys.exit("record.py: pass --exe (a copy of the lane's build; fg.py orient prints the copy step)")
    try:
        import PIL  # noqa: F401
    except ImportError:
        sys.exit("record.py: needs Pillow (pip install pillow)")
    defaults = manifest["defaults"]
    budget = int(manifest["budget"]["clipBytes"])
    os.makedirs(args.work, exist_ok=True)
    lock = threading.Lock()

    def log(msg):
        with lock:
            print(msg, flush=True)

    todo, skipped = [], []
    for entry, theme, mode in items:
        if waits(entry) and not args.pending:
            skipped.append((entry, theme, mode))
        else:
            todo.append((entry, theme, mode))
    for entry, theme, mode in skipped:
        p = entry["pending"]
        log("skip %s %s/%s: pending %s (%s)" % (entry["clip"], theme, mode, p.get("branch") or p["part"], p.get("why", "")))
    inspect_mod = load_inspect()

    palettes = {}
    for theme, mode in sorted({(t, m) for e, t, m in todo if e["kind"] == "diagram"}):
        palettes[(theme, mode)] = probe_palette(inspect_mod, args.exe, theme, mode, args.work, defaults)
        log("palette %s/%s: %s" % (theme, mode, " ".join("%s #%02X%02X%02X" % ((k,) + v) for k, v in palettes[(theme, mode)].items())))

    work_q = queue.Queue()
    for i, it in enumerate(todo):
        work_q.put((i, it))
    results = []

    def worker():
        while True:
            try:
                i, (entry, theme, mode) = work_q.get_nowait()
            except queue.Empty:
                return
            try:
                res = record_item(inspect_mod, args.exe, entry, theme, mode, defaults, budget, args.out, args.work, palettes,
                                  args.keep_frames)
                log("[%d/%d] %s %s/%s: %.1f KB q%d %dfps %d frames %.2f s%s%s (%.0fs)" % (
                    i + 1, len(todo), entry["clip"], theme, mode, res["bytes"] / 1024.0, res["quality"], res["fps"], res["frames"],
                    res["loopMs"] / 1000.0, "" if res["fits"] else "  OVER BUDGET", ("  [" + ", ".join(res["notes"]) + "]") if res["notes"] else "",
                    res["seconds"]))
            except ClipError as e:
                res = {"clip": entry["clip"], "theme": theme, "mode": mode, "ok": False, "error": str(e)}
                log("[%d/%d] %s %s/%s: FAILED %s" % (i + 1, len(todo), entry["clip"], theme, mode, e))
            with lock:
                results.append(res)

    started = time.time()
    threads = [threading.Thread(target=worker) for _ in range(max(1, min(4, args.jobs)))]
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    results.sort(key=lambda r: (r["clip"], r["theme"], r["mode"]))
    with open(os.path.join(args.work, "results.json"), "w", encoding="utf-8") as fh:
        json.dump({"results": results, "skipped": [(e["clip"], t, m) for e, t, m in skipped]}, fh, indent=1)
    ok = [r for r in results if r.get("ok")]
    log("recorded %d of %d (%d failed, %d skipped as pending) in %.0fs; %.1f KB in this run; sheets: %s" % (
        len(ok), len(todo), len(results) - len(ok), len(skipped), time.time() - started,
        sum(r["bytes"] for r in ok) / 1024.0, os.path.join(args.work, "sheets")))
    sys.exit(0 if len(ok) == len(todo) and all(r["fits"] for r in ok) else 1)


if __name__ == "__main__":
    main()
