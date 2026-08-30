"""Compose a Microsoft Store listing screenshot the way the polished listings do it (Files, the Microsoft apps):
a 1920x1080 PNG, the app window (a Capture-WaveeWindow.ps1 PNG, shadow-free) placed on a soft blurred
Windows-11-style backdrop with a drop shadow, and ONE large headline beside or above it.

    python New-StoreScreenshot.py --shot src/home.png --out listing/01-home.png                 # plain, no headline
    python New-StoreScreenshot.py --shot src/artist.png --out listing/02.png --headline "Immersive artist pages" --side left
    python New-StoreScreenshot.py --shot src/liked.png  --out listing/03.png --headline "Your library, with the facts" --side right
    python New-StoreScreenshot.py --shot src/album.png  --out listing/04.png --headline "Albums with credits and video" --side top \
        --overlay frame.png@812,240,540,304     # paste a real video frame over the DRM-black player rect (capture px)

Store rules honoured: PNG, 1920x1080 (>= 1366x768), key content in the top two-thirds, no logos or extra marketing
text beyond the one headline the Store's own listings use. Headline face: Inter SemiBold (fonts/, SIL OFL) - a display
sans that reads as a poster, where Segoe at 70 px reads as a dialog. Needs Python 3 + Pillow (dev box only, like the
other tools here); the output is uploaded by hand in Partner Center and never committed.
"""
import argparse, os, random
from PIL import Image, ImageDraw, ImageFilter, ImageFont

W, H = 1920, 1080
HERE = os.path.dirname(os.path.abspath(__file__))
FONT = os.path.join(HERE, "fonts", "Inter-SemiBold.ttf")
MARGIN = 96          # canvas edge to headline / window
GAP = 56             # headline column to window


def backdrop(seed: int, dark: bool) -> Image.Image:
    """Soft blurred blobs in the app's blue/violet palette on a light or dark ground - the Win11 'Bloom' feel,
    generated so every shot in a set shares one look (same seed -> same backdrop)."""
    rnd = random.Random(seed)
    base = (18, 20, 34) if dark else (232, 228, 240)
    img = Image.new("RGB", (W // 4, H // 4), base)
    palette = [(20, 136, 219), (10, 108, 192), (122, 92, 232), (206, 120, 200), (86, 190, 240)]
    for _ in range(7):
        c = rnd.choice(palette)
        r = rnd.randint(120, 260)
        x, y = rnd.randint(-60, W // 4 + 60), rnd.randint(-60, H // 4 + 60)
        a = 170 if dark else 110
        layer = Image.new("RGBA", img.size, (0, 0, 0, 0))
        ImageDraw.Draw(layer).ellipse((x - r, y - r, x + r, y + r), fill=c + (a,))
        img = Image.alpha_composite(img.convert("RGBA"), layer).convert("RGB")
    img = img.filter(ImageFilter.GaussianBlur(70)).resize((W, H), Image.LANCZOS)
    noise = Image.effect_noise((W, H), 6).convert("L")   # a whisper of grain so the gradient does not band
    return Image.blend(img, Image.merge("RGB", (noise, noise, noise)), 0.035)


def overlay_frame(win: Image.Image, spec: str) -> Image.Image:
    """`path@x,y,w,h` (capture pixels): cover-fit the frame into the rect - the DRM-protected player paints black in a
    screen capture, so the layout is real and only the video surface is substituted with a frame of the same video."""
    path, rect = spec.split("@")
    x, y, w, h = (int(v) for v in rect.split(","))
    frame = Image.open(path).convert("RGB")
    s = max(w / frame.width, h / frame.height)
    frame = frame.resize((round(frame.width * s), round(frame.height * s)), Image.LANCZOS)
    fx, fy = (frame.width - w) // 2, (frame.height - h) // 2
    win = win.copy()
    win.paste(frame.crop((fx, fy, fx + w, fy + h)), (x, y))
    return win


def shadowed(win: Image.Image, radius: int = 12) -> Image.Image:
    """The window with rounded corners and a soft drop shadow, on a transparent canvas with `pad` around it."""
    w, h = win.size
    pad = 90
    mask = Image.new("L", (w, h), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, w - 1, h - 1), radius, fill=255)
    canvas = Image.new("RGBA", (w + 2 * pad, h + 2 * pad), (0, 0, 0, 0))
    sh = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
    ImageDraw.Draw(sh).rounded_rectangle((pad, pad + 18, pad + w, pad + h + 18), radius, fill=(0, 0, 0, 120))
    canvas = Image.alpha_composite(canvas, sh.filter(ImageFilter.GaussianBlur(28)))
    win_rgba = win.convert("RGBA")
    win_rgba.putalpha(mask)
    canvas.alpha_composite(win_rgba, (pad, pad))
    return canvas, pad


def fit(win: Image.Image, max_w: int, max_h: int) -> Image.Image:
    """Scale DOWN to the box only - a capture is never upscaled (it would soften the text)."""
    s = min(max_w / win.width, max_h / win.height, 1.0)
    if s == 1.0:
        return win
    return win.resize((round(win.width * s), round(win.height * s)), Image.LANCZOS)


def headline_block(text: str, max_w: int, color, size: int, align: str = "left") -> Image.Image:
    font = ImageFont.truetype(FONT, size)
    lines, cur = [], ""
    for wd in text.split():
        t = (cur + " " + wd).strip()
        if font.getlength(t) > max_w and cur:
            lines.append(cur); cur = wd
        else:
            cur = t
    lines.append(cur)
    lh = int(size * 1.12)
    widest = max(int(font.getlength(ln)) for ln in lines)
    img = Image.new("RGBA", (widest + 4, lh * len(lines) + 12), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    for i, ln in enumerate(lines):
        x = 0 if align == "left" else (widest - int(font.getlength(ln))) if align == "right" else (widest - int(font.getlength(ln))) // 2
        d.text((x, i * lh), ln, font=font, fill=color)
    return img


def compose(shot: str, out: str, headline: str | None, side: str, dark: bool, seed: int, overlay: str | None, fit_all: bool = False):
    bg = backdrop(seed, dark).convert("RGBA")
    win = Image.open(shot).convert("RGB")
    if overlay:
        win = overlay_frame(win, overlay)
    ink = (245, 245, 250, 255) if dark else (24, 24, 30, 255)
    if not headline:
        win = fit(win, 1840, 1040)   # native size when the capture is smaller; the window may kiss the edges
        card, pad = shadowed(win)
        bg.alpha_composite(card, ((W - card.width) // 2, (H - card.height) // 2 - 6))
    elif side in ("left", "right"):
        # The reference listings (Files) keep the headline column and let the window run off the far edge instead of
        # shrinking it: the window is shown at (near) capture size and the far ~15% is cropped by the canvas.
        win = fit(win, 1900, 1040)   # native size; whatever passes the far edge is cropped by the canvas
        card, pad = shadowed(win)
        col_w = 470
        text = headline_block(headline, col_w, ink, 66, align="left" if side == "left" else "right")
        cy = (H - card.height) // 2
        ty = (H - text.height) // 2 - 24
        wx = MARGIN + col_w + GAP            # the window's near edge; the far edge falls off the canvas
        if side == "left":
            bg.alpha_composite(card, (wx - pad, cy))
            bg.alpha_composite(text, (MARGIN, ty))
        else:
            bg.alpha_composite(card, (W - wx - win.width - pad, cy))
            bg.alpha_composite(text, (W - MARGIN - text.width, ty))
    else:  # top: headline above; the window runs off the bottom edge, or with --fit sits whole below the headline
        text = headline_block(headline, 1500, ink, 62, align="center")
        top = 60
        win_top = top + text.height + (28 if fit_all else 94)   # the window's own top edge (the shadow pad sits above it)
        win = fit(win, 1840, (H - win_top - 40) if fit_all else 1100)
        card, pad = shadowed(win)
        bg.alpha_composite(text, ((W - text.width) // 2, top))
        bg.alpha_composite(card, ((W - card.width) // 2, win_top - pad))
    bg.convert("RGB").save(out, "PNG", optimize=True)
    print(out, bg.size, "window", win.size)


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--shot", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--headline", default=None)
    ap.add_argument("--side", default="left", choices=["left", "right", "top"])
    ap.add_argument("--dark", action="store_true")
    ap.add_argument("--seed", type=int, default=7)
    ap.add_argument("--overlay", default=None, help="frame.png@x,y,w,h in capture pixels")
    ap.add_argument("--fit", action="store_true", help="top layout: scale the window so nothing is cropped (for bottom-anchored flyouts)")
    a = ap.parse_args()
    compose(a.shot, a.out, a.headline, a.side, a.dark, a.seed, a.overlay, a.fit)
