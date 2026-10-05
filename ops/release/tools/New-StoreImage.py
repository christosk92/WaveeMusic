"""Render one Microsoft Store listing screenshot in the What's new look (store-frame.html) -> a 2560x1440 PNG.

The window is a Store SHOT: a folder written by the capture verb (wavee://diag?cmd=shot, Screens/StoreShot.cs) holding
shot.png (the window's own back buffer, straight alpha: Mica stays see-through) and shot.json (the scale and every shown
keyed element's rect). Callouts are placed on those keys, so they always sit on the real element:

    python New-StoreImage.py --shot shots/lyrics --out final/01-lyrics.png --layout hero \\
        --eyebrow "On-device AI" --headline "Lyrics that follow\\nevery word" \\
        --spot rail:lyrics --lens "ai.status|Runs on your NPU|60,430,560|shots/ai-zoom" \\
        --caption "Word-by-word lyrics, timed on your Copilot+ PC's NPU. Your music never leaves the PC."

  --spot KEY            ring the element and dim the rest of the window around it
  --lens KEY|LABEL|LEFT,TOP,WIDTH[|SHOT]
                        a glass card at LEFT,TOP (canvas CSS px, 1280x720) WIDTH wide showing the element magnified:
                        cropped from SHOT when given (a zoom pass: cmd=shot&zoom=250, the same element at 2.5x the
                        pixels) else from --shot; a ring marks it on the window when --shot has the key
  KEY                   a shot.json key, exact or prefix; "#n" picks the n-th match (0-based), "+pad" adds DIP padding,
                        "[a:b]" keeps that horizontal fraction of the element
  --layout grid --panel SHOT|KEY|LABEL|GROW[|POPOUT] --frame JPG
                        up to four labelled crops in a 2x2 grid (the video placements): each centred on KEY, the
                        DRM-protected video's hole filled with a real frame of the same video
  --caption TEXT        written beside the PNG as <out>.caption.txt (Partner Center: 200 characters per screenshot)

A plain PNG still works for --shot (no keys: no --spot/--lens). Store rules honoured: PNG, 2560x1440 (>= 1366x768),
headline and callouts in the top two-thirds, the selling sentence in the caption, not on the image. Dev-box tool:
Microsoft Edge + Python 3 + Pillow; the output is uploaded in Partner Center by hand and never committed.
"""
import argparse, html, json, os, pathlib, re, shutil, subprocess, sys, tempfile, time
from PIL import Image, ImageFilter

HERE = pathlib.Path(__file__).resolve().parent
EDGE = [r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", r"C:\Program Files\Microsoft\Edge\Application\msedge.exe"]
CAPTION_MAX = 200


def url(p) -> str:
    return pathlib.Path(p).resolve().as_uri()


class Shot:
    """A capture folder (shot.png + shot.json) or a bare PNG."""

    def __init__(self, path: str):
        p = pathlib.Path(path)
        if p.is_dir():
            self.png, meta = p / "shot.png", json.loads((p / "shot.json").read_text(encoding="utf-8"))
            self.scale, self.keys = float(meta["scale"]), meta["keys"]
        else:
            self.png, self.scale, self.keys = p, 1.0, []
        self.image = Image.open(self.png).convert("RGBA")

    def find(self, spec: str):
        """KEY[#n][+pad][[a:b]] -> (x, y, w, h) in this shot's PIXELS, or None. [a:b] keeps that horizontal fraction of
        the element (a settings row's text without its switch: [0:0.66])."""
        m = re.fullmatch(r"(.+?)(?:#(\d+))?(?:\+(\d+(?:\.\d+)?))?(?:\[(\d*\.?\d+):(\d*\.?\d+)\])?", spec)
        key, nth, pad = m.group(1), int(m.group(2) or 0), float(m.group(3) or 0)
        fa, fb = float(m.group(4) or 0), float(m.group(5) or 1)
        hits = [k for k in self.keys if k["key"] == key] or [k for k in self.keys if k["key"].startswith(key)]
        if nth >= len(hits):
            return None
        k = hits[nth]
        s = self.scale
        x, y = (k["x"] + k["w"] * fa - pad) * s, (k["y"] - pad) * s
        w, h = (k["w"] * (fb - fa) + 2 * pad) * s, (k["h"] + 2 * pad) * s
        W, H = self.image.size
        x0, y0 = max(0, round(x)), max(0, round(y))
        x1, y1 = min(W, round(x + w)), min(H, round(y + h))
        return (x0, y0, x1 - x0, y1 - y0)


def mica(img: Image.Image, tints) -> Image.Image:
    """The window's see-through regions over a Mica-like plate: the dark base the app paints on, tinted toward the
    field's colours the way Mica tints toward the wallpaper (a smooth top-to-bottom wash, no wallpaper detail)."""
    W, H = img.size

    def mix(c, base=(28, 28, 32), t=0.16):
        rgb = tuple(int(c[i:i + 2], 16) for i in (1, 3, 5))
        return tuple(round(base[i] * (1 - t) + rgb[i] * t) for i in range(3))

    top, bottom = mix(tints[0]), mix(tints[2])
    column = Image.new("RGBA", (1, 256))
    column.putdata([tuple(round(top[i] * (1 - f / 255) + bottom[i] * f / 255) for i in range(3)) + (255,) for f in range(256)])
    plate = column.resize((W, H), Image.BILINEAR)
    plate.alpha_composite(img)
    return plate.convert("RGB")


def cover(frame: Image.Image, w: int, h: int) -> Image.Image:
    """The frame scaled to cover w x h and centre-cropped (object-fit: cover)."""
    fw, fh = frame.size
    k = max(w / fw, h / fh)
    im = frame.resize((max(1, round(fw * k)), max(1, round(fh * k))), Image.LANCZOS)
    x, y = (im.width - w) // 2, (im.height - h) // 2
    return im.crop((x, y, x + w, y + h))


def hole_box(img: Image.Image, r, transparent: bool):
    """Inside rect r of img: the bounding box of the DRM hole - the transparent pixels of a frame capture (the video
    surface is a separate swapchain the back buffer never holds) or the black pixels of a screen grab (protected
    content composes as black). None when there is no hole."""
    x, y, w, h = r
    region = img.crop((x, y, x + w, y + h))
    if transparent:
        mask = region.getchannel("A").point(lambda a: 255 if a < 16 else 0)
    else:
        mask = region.convert("L").point(lambda v: 255 if v < 7 else 0)
    bb = mask.getbbox()
    if bb is None or (bb[2] - bb[0]) * (bb[3] - bb[1]) < 0.25 * w * h:
        return None
    return (x + bb[0], y + bb[1], bb[2] - bb[0], bb[3] - bb[1])


def with_frame(shot: "Shot", key: str, frame: Image.Image, tints) -> Image.Image:
    """The shot over its Mica plate with a real frame of the video under the hole at KEY (controls drawn over the
    video stay on top). RGBA out."""
    r = shot.find(key)
    img = shot.image
    W, H = img.size
    base = mica(Image.new("RGBA", (W, H), (0, 0, 0, 0)), tints).convert("RGBA")
    box = hole_box(img, r, transparent=True) if r else None
    if box:
        x, y, w, h = box
        base.paste(cover(frame, w, h).convert("RGBA"), (x, y))
    base.alpha_composite(img)
    return base


def popout_scene(main: "Shot", pop_dir: str, frame: Image.Image, tints) -> tuple:
    """The main window with the own-window video (a screen grab, Capture-StoreShots.ps1 Grab-PopOut) over its lower
    right, the grab's black video area filled with the frame. Returns (RGBA image, popout rect)."""
    meta = json.loads((pathlib.Path(pop_dir) / "shot.json").read_text(encoding="utf-8"))
    pop = Image.open(pathlib.Path(pop_dir) / "shot.png").convert("RGB")
    pop = pop.crop((2, 2, pop.width - 2, pop.height - 2))       # the frame edge, where the desktop shows through
    box = hole_box(pop.convert("RGBA"), (0, 0, pop.width, pop.height), transparent=False)
    if box:
        # The window's own controls are drawn OVER the protected picture, so they sit in the black: keep every pixel
        # brighter than the black (soft, so anti-aliased glyph edges blend) and put the frame under the rest.
        x, y, w, h = box
        region = pop.crop((x, y, x + w, y + h))
        keep = region.convert("L").point(lambda v: 0 if v < 6 else min(255, (v - 6) * 9))
        pop.paste(Image.composite(region, cover(frame, w, h), keep), (x, y))
    win = mica(main.image, tints).convert("RGBA")
    # Parked in the app's lower right, overlapping it, as an own window usually sits: where it happened to be on the
    # capturing desktop (meta offsetX/offsetY) is often off past the app's edge, which crops as empty space.
    ox = round(win.width - pop.width - 0.05 * win.width)
    oy = round(win.height - pop.height - 0.12 * win.height)
    L, T = min(0, ox), min(0, oy)
    R, B = max(win.width, ox + pop.width), max(win.height, oy + pop.height)
    canvas = Image.new("RGBA", (R - L, B - T), (0, 0, 0, 0))
    canvas.paste(win, (-L, -T))
    # the own window's drop shadow, so it reads as a window over the app
    sh = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
    sh.paste(Image.new("RGBA", pop.size, (0, 0, 0, 150)), (ox - L + 6, oy - T + 14))
    canvas.alpha_composite(sh.filter(ImageFilter.GaussianBlur(18)))
    canvas.paste(pop.convert("RGBA"), (ox - L, oy - T))
    return canvas, (ox - L, oy - T, pop.width, pop.height)


def crop_around(img: Image.Image, r, aspect: float, grow: float) -> Image.Image:
    """An aspect-ratio crop centred on rect r, `grow` times its size, kept inside the image."""
    x, y, w, h = r
    cw = min(img.width, max(w * grow, h * grow * aspect))
    ch = cw / aspect
    if ch > img.height:
        ch = img.height
        cw = ch * aspect
    cx, cy = x + w / 2, y + h / 2
    x0 = min(max(0, cx - cw / 2), img.width - cw)
    y0 = min(max(0, cy - ch / 2), img.height - ch)
    return img.crop((round(x0), round(y0), round(x0 + cw), round(y0 + ch)))


def render(edge: str, page: str, work: pathlib.Path) -> Image.Image:
    frame = work / "frame.html"
    frame.write_text(page, encoding="utf-8")
    png = work / "render.png"
    proc = subprocess.Popen([edge, "--headless=new", "--disable-gpu", "--hide-scrollbars", "--no-first-run",
                             "--no-default-browser-check", f"--user-data-dir={work / 'profile'}",
                             "--force-device-scale-factor=2", "--window-size=1280,720",
                             "--allow-file-access-from-files", "--virtual-time-budget=3000",
                             f"--screenshot={png}", frame.as_uri()],
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    # msedge re-launches itself and can linger after writing the screenshot: wait for a complete file, then stop every
    # msedge that runs on THIS render's private profile (never the user's browser).
    last = -1
    for _ in range(600):
        size = png.stat().st_size if png.exists() else 0
        if size > 0 and size == last:
            break
        last = size
        time.sleep(0.2)
    proc.kill()
    subprocess.run(["powershell", "-NoProfile", "-Command",
                    "Get-CimInstance Win32_Process -Filter \"Name='msedge.exe'\" | Where-Object { $_.CommandLine -like '*"
                    + work.name + "*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }"],
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
    if not png.exists():
        sys.exit("Edge wrote no screenshot")
    im = Image.open(png).convert("RGB")
    if im.size != (2560, 1440):
        sys.exit(f"Edge rendered {im.size}, expected (2560, 1440)")
    return im


def main() -> int:
    a = argparse.ArgumentParser()
    a.add_argument("--shot", required=True)
    a.add_argument("--shot2", default="")
    a.add_argument("--out", required=True)
    a.add_argument("--layout", choices=["hero", "stack", "center", "clean", "grid"], default="hero")
    a.add_argument("--eyebrow", default="")
    a.add_argument("--headline", default="")
    a.add_argument("--sub", default="")
    a.add_argument("--chip", default="", help="text of the pill; rendered after a sparkle")
    a.add_argument("--tints", default="#3d5a8f,#6b3a63,#24506e", help="three #rrggbb mesh tints: a,b,c")
    a.add_argument("--base", default="#151517")
    a.add_argument("--spot", action="append", default=[], help="ring this key and dim the window around it")
    a.add_argument("--lens", action="append", default=[], help="KEY|LABEL|LEFT,TOP,WIDTH[|SHOT]")
    a.add_argument("--panel", action="append", default=[],
                   help="grid layout: SHOT|KEY|LABEL|GROW[|POPOUT]: a crop around KEY (the video) GROW times its size, "
                        "the DRM hole filled with --frame; POPOUT = the own-window grab laid over SHOT")
    a.add_argument("--frame", action="append", default=[], help="a real frame of the playing video (one per panel, cycled)")
    a.add_argument("--caption", default="")
    a.add_argument("--css", default="", help="extra CSS appended to the frame, e.g. 'body[data-layout] .s1{ top:420px; }'")
    o = a.parse_args()

    edge = next((e for e in EDGE if os.path.exists(e)), None)
    if not edge:
        sys.exit("Microsoft Edge not found")
    if len(o.caption) > CAPTION_MAX:
        sys.exit(f"caption is {len(o.caption)} characters; Partner Center takes {CAPTION_MAX}")
    tints = [t.strip() for t in o.tints.split(",")]
    shot = Shot(o.shot)
    W, H = shot.image.size

    work = pathlib.Path(tempfile.mkdtemp(prefix="wavee-store-"))
    try:
        main_png = work / "s1.png"
        mica(shot.image, tints).save(main_png)
        back_png = main_png
        if o.shot2:
            back_png = work / "s2.png"
            mica(Shot(o.shot2).image, tints).save(back_png)

        marks, lenses = [], []

        def mark(r, cls):
            x, y, w, h = r
            return (f'<div class="{cls}" style="left:{x / W * 100:.3f}%;top:{y / H * 100:.3f}%;'
                    f'width:{w / W * 100:.3f}%;height:{h / H * 100:.3f}%"></div>')

        for spec in o.spot:
            r = shot.find(spec)
            if r is None:
                sys.exit(f"--spot {spec}: no such key in {o.shot}")
            marks.append(mark(r, "mark spot"))

        for i, spec in enumerate(o.lens):
            parts = spec.split("|")
            if len(parts) < 3:
                sys.exit(f"--lens {spec}: want KEY|LABEL|LEFT,TOP,WIDTH[|SHOT]")
            key, label = parts[0], parts[1]
            left, top, width = (float(v) for v in parts[2].split(","))
            src_path = parts[3] if len(parts) > 3 and parts[3] else o.shot
            src = Shot(src_path) if src_path != o.shot else shot
            r = src.find(key)
            if r is None:
                sys.exit(f"--lens {key}: no such key in {src_path}")
            crop = work / f"lens{i}.png"
            x, y, w, h = r
            mica(src.image.crop((x, y, x + w, y + h)), tints).save(crop)
            inner = width - 20
            lenses.append(f'<div class="lens" style="left:{left}px;top:{top}px;width:{width}px">'
                          + (f'<div class="tag">{html.escape(label)}</div>' if label else "")
                          + f'<img class="px" src="{url(crop)}" style="width:{inner}px;height:{inner * h / w:.2f}px"></div>')
            onw = shot.find(key)
            if onw is not None and not o.spot:
                marks.append(mark(onw, "mark"))

        panels = []
        frames = [Image.open(f).convert("RGB") for f in o.frame]
        for i, spec in enumerate(o.panel):
            parts = spec.split("|")
            pshot = Shot(parts[0])
            key, label, grow = parts[1], parts[2], float(parts[3])
            frame = frames[i % len(frames)] if frames else Image.new("RGB", (16, 9))
            if len(parts) > 4 and parts[4]:
                scene, focus = popout_scene(pshot, parts[4], frame, tints)
            else:
                scene = with_frame(pshot, key, frame, tints)
                focus = pshot.find(key)
                if focus is None:
                    sys.exit(f"--panel {key}: no such key in {parts[0]}")
            cp = work / f"panel{i}.png"
            crop_around(scene, focus, 1.6, grow).save(cp)
            panels.append(f'<div class="panel"><img src="{url(cp)}" alt=""><div class="ptag">{html.escape(label)}</div></div>')

        chip = f'<span class="spark">\u2726</span>{html.escape(o.chip)}' if o.chip else ""
        page = (HERE / "store-frame.html").read_text(encoding="utf-8")
        for k, v in {
            "__LAYOUT__": o.layout, "__SHOT2__": url(back_png), "__SHOT__": url(main_png),
            "__BASE__": o.base, "__TINT_A__": tints[0], "__TINT_B__": tints[1], "__TINT_C__": tints[2],
            "__EYEBROW__": html.escape(o.eyebrow), "__HEADLINE__": html.escape(o.headline).replace("\\n", "<br>"),
            "__SUB__": html.escape(o.sub), "__CHIP__": chip, "__FONTS__": (HERE / "fonts").as_uri(),
            "__EXTRA_CSS__": o.css, "__MARKS__": "".join(marks), "__LENSES__": "".join(lenses),
            "__PANELS__": "".join(panels),
        }.items():
            page = page.replace(k, v)

        im = render(edge, page, work)
        out = pathlib.Path(o.out)
        out.parent.mkdir(parents=True, exist_ok=True)
        im.save(out, "PNG", optimize=True)
        if o.caption:
            out.with_suffix(".caption.txt").write_text(o.caption + "\n", encoding="utf-8")
        print(f"{out}  {out.stat().st_size // 1024} KB  {im.size[0]}x{im.size[1]}")
    finally:
        shutil.rmtree(work, ignore_errors=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
