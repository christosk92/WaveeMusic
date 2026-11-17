"""Write a labelled set of seek-preview sheets for `mount ... video=... seekPreview=on` (docs/plans/wavee/video-seek-preview-implementation.md).

The inspector's fixture source reads its sheets from files beside the exe:

    <run>/assets/seekpanels/0/<panel>.jpg     6 x 6 cells of 180 x 100   (the coarse sheets)
    <run>/assets/seekpanels/1/<panel>.jpg     3 x 3 cells of 452 x 254   (the fine sheets)

one frame a second for 245 s: the geometry a real music video's manifest carries. Nothing is bundled with the app, so run this
once against the COPY of the build you launch:

    python -I tools/seekpanels.py --out C:/WAVEE/scratch/<lane>/run/assets/seekpanels

Each frame is a slow push across one of the bundled cover photographs (a new one every 20 s) and carries its own time in a
corner. That label is the point: in a render the time INSIDE the picture must equal the time the tip prints under it, on both
sheets, or the lookup is wrong. Like a real set, the last sheet of each size is cropped to the rows it uses.

Needs Pillow.
"""
import argparse
import glob
import os

from PIL import Image, ImageDraw, ImageFont

SECONDS = 245
SIZES = (("0", 180, 100, 6, 6), ("1", 452, 254, 3, 3))
SHOT_SECONDS = 20


def font(px):
    for name in ("segoeuib.ttf", "arialbd.ttf"):
        try:
            return ImageFont.truetype(name, px)
        except OSError:
            continue
    return ImageFont.load_default()


def frame(covers, second, width, height):
    """One frame: cover (second // 20), pushed in and panned a little further every second."""
    cover = covers[(second // SHOT_SECONDS) % len(covers)]
    t = (second % SHOT_SECONDS) / float(SHOT_SECONDS)
    aspect = width / float(height)
    zoom = 1.0 + 0.35 * t
    crop_w = cover.width / zoom
    crop_h = crop_w / aspect
    if crop_h > cover.height:
        crop_h = cover.height
        crop_w = crop_h * aspect
    x = (cover.width - crop_w) * (0.15 + 0.7 * t)
    y = (cover.height - crop_h) * 0.5
    shot = cover.crop((int(x), int(y), int(x + crop_w), int(y + crop_h))).resize((width, height), Image.LANCZOS)
    draw = ImageDraw.Draw(shot, "RGBA")
    label = "%d:%02d" % (second // 60, second % 60)
    size = max(11, height // 5)
    face = font(size)
    box = draw.textbbox((0, 0), label, font=face)
    pad = max(2, size // 4)
    w, h = box[2] - box[0] + 2 * pad, box[3] - box[1] + 2 * pad
    draw.rectangle((pad, height - h - pad, pad + w, height - pad), fill=(0, 0, 0, 170))
    draw.text((2 * pad - box[0], height - h - box[1]), label, font=face, fill=(255, 255, 255, 255))
    return shot


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--out", required=True, help="<run>/assets/seekpanels")
    parser.add_argument("--covers", default=os.path.join(here, "..", "src", "apps", "Wavee", "assets", "covers"),
                        help="the photographs the frames are cut from (default: the app's bundled covers)")
    args = parser.parse_args()

    paths = sorted(glob.glob(os.path.join(args.covers, "cover*.jpg")))
    if not paths:
        raise SystemExit("no cover*.jpg under " + args.covers)
    covers = [Image.open(p).convert("RGB") for p in paths]
    frames = SECONDS + 1
    for variant, fw, fh, rows, cols in SIZES:
        per = rows * cols
        folder = os.path.join(args.out, variant)
        os.makedirs(folder, exist_ok=True)
        panels = (frames + per - 1) // per
        for panel in range(panels):
            count = min(per, frames - panel * per)
            used_rows = (count + cols - 1) // cols
            sheet = Image.new("RGB", (cols * fw, used_rows * fh), (0, 0, 0))
            for cell in range(count):
                sheet.paste(frame(covers, panel * per + cell, fw, fh), ((cell % cols) * fw, (cell // cols) * fh))
            sheet.save(os.path.join(folder, "%d.jpg" % panel), quality=82)
        print("variant %s: %d sheets of %dx%d cells (%dx%d) -> %s" % (variant, panels, cols, rows, fw, fh, folder))


main()
