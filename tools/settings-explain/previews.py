"""previews.py - the theme picker stills (Settings > Appearance > Theme and the setup wizard's Theme step): each shipped theme's
Home in light and in dark, rendered from the real app by the headless inspector and composed to read at the size of a picker card.

    python -I tools/settings-explain/previews.py --exe C:/WAVEE/scratch/<lane>/run/Wavee.exe            # all six, into the assets
    python -I tools/settings-explain/previews.py --exe <exe> --themes zune --modes light
    python -I tools/settings-explain/previews.py --exe <exe> --out C:/WAVEE/scratch/<lane>/previews       # anywhere but the assets
    python -I tools/settings-explain/previews.py --check                     # size, format and pixels of the committed stills

Outputs  src/apps/Wavee/assets/themes/<theme>-<mode>.jpg (or under --out), the paths each theme's manifest names
         (`ThemePreview`); in --work (default %TEMP%/wavee-theme-previews) the renders and sheet.png: every still at the sizes the
         cards draw it (look at it).

How a still is made: one fresh inspector process per theme x mode (the inspector rule: one process per configuration): `mount` at
1440x960 and scale 2 on Home with the fake catalogue and photographs for covers (`images=real`; never account data), `frames`, `render`.
The greeting follows the local clock of the run.

The composition (owner, 2026-11-19: "needs better images"; the explainer clips' frame, "framed in a beautiful way but not too much", is
the reference): a whole window shrunk to a card is noise, so a still is the window's TOP-LEFT corner, the same region in every theme
(CROP_W DIP wide: the navigation, the page's title and Home's first row with its play command), inset on the theme's quiet backdrop with
the theme's corner radius, a hairline and a soft shadow, and bleeding off the right and the bottom. Colours, backdrop kind, radius and
shadow come from the clips' frame (clips.json `defaults.frame`). The cards are not one shape (WinUI's fluid cards 16:9, Classic's
2.2 : 1, Zune's tile 1.4 : 1, WinUI's Small row 48 x 32), and each covers its box from the still's top-left corner
(`ThemePreviews.Layer`): every shape keeps the window's corner and loses only window that bleeds off anyway.

Format: JPEG (quality 90, no chroma subsampling) at 832 x 468, twice the pickers' 416 decode width, under the app's budget
(`ThemePreviewRules.MaxBytes`); the encode steps the quality down when a still would not fit. Never a WebP: Windows needs an extension
to decode one, and a card draws no stand-in. A Release build checks the committed set (`Wavee.exe --check-theme-previews`).

Standard library plus Pillow. Run with `python -I`.
"""

import argparse
import importlib.util
import io
import json
import os
import sys
import tempfile
import threading

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))                       # WaveeMusic
ASSETS = os.path.join(ROOT, "src", "apps", "Wavee", "assets", "themes")
THEMES = ("classic", "winui", "zune")
MODES = ("dark", "light")

WINDOW = (1440, 960)          # the mount (DIP): every theme at its Standard tier
RENDER_SCALE = 2              # rendered at 2x and sampled down: crisp type at card size
CROP_W = 960                  # the window region (DIP, from the top-left corner) the still shows, the same in every theme
SIZE = (832, 468)             # the still (px): 16:9, twice the pickers' 416 decode width
INSET = 0.05                  # the backdrop's margin above and left of the window's corner, as a share of the still's width
BACKDROP_GAIN = 2.5           # the clips' backdrop strength is tuned for a wide margin round a large shot; this margin is a few DIP on a card
SHADOW_BLUR, SHADOW_DY = 8, 3  # px: the clips' shadow, scaled to the smaller margin
BORDER_ALPHA = 0.10           # the window's hairline
QUALITIES = (90, 87, 84, 80)  # JPEG, tried in turn until the still fits the budget
BUDGET = 64 * 1024            # ThemePreviewRules.MaxBytes (Theming/Composition/ThemePreviews.cs)

# The boxes the cards cover with a still (DIP), from each theme's Settings rules, for the contact sheet (drawn at 150 %).
CARD_BOXES = (
    ("WinUI card (Standard)", 207, 116),     # WinUiSettingsTiers: PickerCardMaxW 223 - 2 x CardPad, 16:9 (PreviewAspect)
    ("Classic card (Standard)", 206, 92),    # ClassicSettingsTiers: PickerCardW 208 - the 1-DIP inset, PickerThumbH 92
    ("Zune tile (Standard)", 142, 100),      # ZuneSettingsTiers: PickerCardW 150 - (SelectRing + SelectGap) x 2, PickerThumbH 100
    ("Compact grid", 160, 72),               # Classic and WinUI PickerThumbH 72 (Zune 88), half a 400-wide window
    ("Classic strip (Small)", 188, 72),      # ClassicSettingsTiers: PickerCardW 190, PickerThumbH 72
    ("WinUI row (Small)", 48, 32),           # WinUiSettingsRules.RowThumbW x PickerThumbH
)
SHEET_SCALE = 1.5


class PreviewError(Exception):
    pass


def load_module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def frame_config():
    with open(os.path.join(HERE, "clips.json"), "r", encoding="utf-8") as fh:
        return json.load(fh)["defaults"]["frame"]


# ───────────────────────────── render ─────────────────────────────

def render_home(rec, inspect_mod, exe, theme, mode, path):
    """One fresh inspector process: Home in `theme` and `mode`, rendered to `path` at RENDER_SCALE."""
    sess = rec.Session(inspect_mod, exe)
    try:
        sess.cmd("mount w=%d h=%d theme=%s kind=%s route=home images=real scale=%d" % (WINDOW[0], WINDOW[1], theme, mode, RENDER_SCALE))
        sess.cmd("frames 120")
        sess.render(path)
    except rec.ClipError as e:
        raise PreviewError("%s %s: %s" % (theme, mode, e))
    finally:
        sess.close()
    return path


# ───────────────────────────── compose ─────────────────────────────

def compose(rec, cfg, theme, mode, render_path):
    """The still: the window's top-left corner inset on the theme's backdrop (see the module doc)."""
    from PIL import Image, ImageDraw, ImageFilter
    W, H = SIZE
    dark = mode == "dark"
    ground, accent = rec.hexrgb(cfg["ground"][theme][mode]), rec.hexrgb(cfg["accent"][theme][mode])
    bd = cfg["backdrop"][theme]
    base = rec.backdrop(bd["kind"], ground, accent, float(bd["strength"][mode]) * BACKDROP_GAIN, (W, H)).convert("RGBA")

    x = y = int(round(W * INSET))
    w, h = W - x, H - y
    src = Image.open(render_path).convert("RGB")
    k = w / float(CROP_W * RENDER_SCALE)                     # still px per render px
    region = src.crop((0, 0, int(round(w / k)), int(round(h / k)))).resize((w, h), Image.LANCZOS)
    radius = float(rec.per_theme(cfg["radius"], theme)) * k * RENDER_SCALE
    bleed = int(radius) + SHADOW_BLUR * 4                    # the window goes on past the still's edges: no corner there

    sh = cfg["shadow"]
    shadow = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    ImageDraw.Draw(shadow).rounded_rectangle((x, y + SHADOW_DY, x + w + bleed, y + h + bleed), radius=radius,
                                             fill=(0, 0, 0, int(round(255 * float(sh["alpha"][mode])))))
    base = Image.alpha_composite(base, shadow.filter(ImageFilter.GaussianBlur(SHADOW_BLUR)))

    mask = rec.aa_mask((w + bleed, h + bleed), radius).crop((0, 0, w, h))
    img = base.convert("RGB")
    img.paste(region, (x, y), mask)

    ink = (255, 255, 255) if dark else (0, 0, 0)
    ring = rec.aa_mask((w + bleed, h + bleed), radius, outline=1).crop((0, 0, w, h)).point(lambda a: int(round(a * BORDER_ALPHA)))
    layer = Image.new("RGBA", (w, h), ink + (0,))
    layer.putalpha(ring)
    out = img.convert("RGBA")
    out.alpha_composite(layer, (x, y))
    return out.convert("RGB")


def encode(img):
    """JPEG bytes at the first quality that fits the budget, and that quality."""
    data, q = b"", None
    for q in QUALITIES:
        buf = io.BytesIO()
        img.save(buf, "JPEG", quality=q, subsampling=0, optimize=True)
        data = buf.getvalue()
        if len(data) <= BUDGET:
            return data, q
    raise PreviewError("over the budget at quality %d: %d bytes (budget %d)" % (q, len(data), BUDGET))


# ───────────────────────────── look ─────────────────────────────

def cover_top_left(img, bw, bh):
    """As a card draws a still: cover the box, from the still's top-left corner."""
    from PIL import Image
    w, h = img.size
    s = max(bw / float(w), bh / float(h))
    r = img.resize((max(bw, int(round(w * s))), max(bh, int(round(h * s)))), Image.LANCZOS)
    return r.crop((0, 0, bw, bh))


def contact_sheet(stills, path):
    """Every still (rows) at every card box (columns), at SHEET_SCALE, on a mid grey."""
    from PIL import Image, ImageDraw
    pad, label_w = 12, 110
    cells = [(int(round(bw * SHEET_SCALE)), int(round(bh * SHEET_SCALE))) for _, bw, bh in CARD_BOXES]
    row_h = max(ch for _, ch in cells) + pad
    width = label_w + sum(cw + pad for cw, _ in cells) + pad
    sheet = Image.new("RGB", (width, pad + 16 + row_h * len(stills)), (128, 128, 128))
    d = ImageDraw.Draw(sheet)
    x = label_w + pad
    for (name, _, _), (cw, _) in zip(CARD_BOXES, cells):
        d.text((x, pad // 2), name, fill=(0, 0, 0))
        x += cw + pad
    y = pad + 16
    for label, img in stills:
        d.text((4, y), label, fill=(0, 0, 0))
        x = label_w + pad
        for cw, ch in cells:
            sheet.paste(cover_top_left(img, cw, ch), (x, y))
            x += cw + pad
        y += row_h
    sheet.save(path)
    return path


# ───────────────────────────── check ─────────────────────────────

def check(root):
    """The committed stills: present, a JPEG of SIZE, inside the budget. Returns the problems."""
    from PIL import Image
    problems = []
    for t in THEMES:
        for m in MODES:
            p = os.path.join(root, "%s-%s.jpg" % (t, m))
            if not os.path.exists(p):
                problems.append("missing  %s" % p)
                continue
            n = os.path.getsize(p)
            with Image.open(p) as im:
                fmt, size = im.format, im.size
            line = "%s-%s.jpg  %s %dx%d  %d bytes" % (t, m, fmt, size[0], size[1], n)
            print(line)
            if fmt != "JPEG":
                problems.append("format   %s is %s, not JPEG" % (p, fmt))
            if size != SIZE:
                problems.append("size     %s is %dx%d, not %dx%d" % (p, size[0], size[1], SIZE[0], SIZE[1]))
            if n > BUDGET:
                problems.append("too big  %s  %d bytes (budget %d)" % (p, n, BUDGET))
    return problems


# ───────────────────────────── main ─────────────────────────────

def main():
    ap = argparse.ArgumentParser(description="Render and compose the theme picker stills.")
    ap.add_argument("--exe", help="a Wavee.exe copy to drive (fg.py orient prints the lane's)")
    ap.add_argument("--out", default=ASSETS, help="where the stills go (default: the app's assets/themes)")
    ap.add_argument("--work", default=os.path.join(tempfile.gettempdir(), "wavee-theme-previews"), help="renders and the contact sheet")
    ap.add_argument("--themes", nargs="*", default=list(THEMES), choices=THEMES)
    ap.add_argument("--modes", nargs="*", default=list(MODES), choices=MODES)
    ap.add_argument("--check", action="store_true", help="check the committed stills and exit")
    args = ap.parse_args()

    if args.check:
        problems = check(args.out)
        for p in problems:
            print("problem  " + p)
        sys.exit(1 if problems else 0)
    if not args.exe:
        ap.error("--exe is required (a copy of the lane's Wavee.exe)")

    rec = load_module("wavee_settings_explain_record", os.path.join(HERE, "record.py"))
    inspect_mod = rec.load_inspect()
    cfg = frame_config()
    os.makedirs(args.work, exist_ok=True)
    os.makedirs(args.out, exist_ok=True)

    todo = [(t, m) for t in args.themes for m in args.modes]
    renders, errors = {}, []
    lock = threading.Lock()

    def run(t, m):
        try:
            path = render_home(rec, inspect_mod, args.exe, t, m, os.path.join(args.work, "home.%s.%s.png" % (t, m)))
            with lock:
                renders[(t, m)] = path
        except PreviewError as e:
            with lock:
                errors.append(str(e))

    threads = [threading.Thread(target=run, args=tm) for tm in todo]
    for th in threads:
        th.start()
    for th in threads:
        th.join()
    for e in errors:
        print("error  " + e)

    stills = []
    for t, m in todo:
        if (t, m) not in renders:
            continue
        img = compose(rec, cfg, t, m, renders[(t, m)])
        data, q = encode(img)
        out = os.path.join(args.out, "%s-%s.jpg" % (t, m))
        with open(out, "wb") as fh:
            fh.write(data)
        print("%s  %dx%d  quality %d  %d bytes" % (out, SIZE[0], SIZE[1], q, len(data)))
        stills.append(("%s %s" % (t, m), img))
    if stills:
        print("sheet  " + contact_sheet(stills, os.path.join(args.work, "sheet.png")))
    sys.exit(1 if errors else 0)


if __name__ == "__main__":
    main()
