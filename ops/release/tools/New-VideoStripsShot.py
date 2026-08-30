"""Three video states as slanted stripes on one 1920x1080 listing shot: mini player | docked | own window.

Inputs (artifacts/store/src, captured with Capture-WaveeWindow.ps1 -KeepSize and the popout via a per-window screen
grab): video.png (mini player), video-docked.png, popout/win0.png (the video window) + popout/win1.png (main window),
and real frames of the videos in src/frames to fill the DRM-black rects. The rects/offsets below are those captures'
pixel coordinates - re-measure when recapturing. Run from artifacts/store: python ../../ops/release/tools/New-VideoStripsShot.py
"""
import importlib.util, os
from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageFont

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', '..'))
spec = importlib.util.spec_from_file_location('shot', os.path.join(ROOT, 'ops', 'release', 'tools', 'New-StoreScreenshot.py'))
shot = importlib.util.module_from_spec(spec); spec.loader.exec_module(shot)
os.chdir(os.path.join(ROOT, 'artifacts', 'store'))
W, H = shot.W, shot.H

# --- the three states, DRM rect filled with a real frame -------------------------------------------------
mini = shot.overlay_frame(Image.open('src/video.png').convert('RGB'), 'src/frames/boaf-calm.jpg@1053,553,851,423')
dock = shot.overlay_frame(Image.open('src/video-docked.png').convert('RGB'), 'src/frames/lmm-crop.png@1319,66,609,252')
main = Image.open('src/popout/win1.png').convert('RGB')
pop = shot.overlay_frame(Image.open('src/popout/win0.png').convert('RGB'), 'src/frames/lmm-crop.png@2,45,1004,581')
# both windows shadowed on a transparent ground: the stripe shows the backdrop around them, like a desktop
mc, pad = shot.shadowed(main); pc, _ = shot.shadowed(pop)
offx, offy = 1390 - 49, 160 - 40                          # the popout's DWM origin relative to the main window
own = Image.new('RGBA', (max(mc.width, offx + pc.width), max(mc.height, offy + pc.height)), (0, 0, 0, 0))
own.alpha_composite(mc, (0, 0)); own.alpha_composite(pc, (offx, offy))

# (image, focus point in capture px, scale, label)
STATES = [
    (mini, (1478, 700), 0.78, 'Mini player'),
    (dock, (1623, 260), 0.95, 'Docked'),
    (own,  (1390 - 49 + 90 + 504, 160 - 40 + 90 + 330), 0.58, 'Own window'),
]

# --- geometry ----------------------------------------------------------------------------------------------
TOP, BOT = 170, 1040
SLANT = 150                      # how far the dividers lean (px over the stripe height)
GAP = 10                         # backdrop showing between stripes
xs_top = [0, 760, 1400, W + SLANT]
def poly(i):
    x0t, x1t = xs_top[i], xs_top[i + 1]
    return [(x0t, TOP), (x1t, TOP), (x1t - SLANT, BOT), (x0t - SLANT, BOT)]

bg = shot.backdrop(7, False).convert('RGBA')
ink = (24, 24, 30, 255)
text = shot.headline_block('Video, three ways', 1500, ink, 62, align='center')
bg.alpha_composite(text, ((W - text.width) // 2, 56))

# one shadow under the whole band
band = Image.new('RGBA', (W, H), (0, 0, 0, 0))
ImageDraw.Draw(band).polygon([(0, TOP), (W, TOP), (W, BOT), (0, BOT)], fill=(0, 0, 0, 110))
bg.alpha_composite(band.filter(ImageFilter.GaussianBlur(26)).transform((W, H), Image.AFFINE, (1, 0, 0, 0, 1, -18)))

font = ImageFont.truetype(shot.FONT, 30)
for i, (img, (fx, fy), s, label) in enumerate(STATES):
    p = poly(i)
    minx, maxx = min(x for x, _ in p), max(x for x, _ in p)
    pw, ph = maxx - minx, BOT - TOP
    scaled = img.resize((round(img.width * s), round(img.height * s)), Image.LANCZOS)
    # crop so the focus point sits at ~45% width / 32% height of the stripe, clamped to the source
    cx, cy = round(fx * s - pw * 0.45), round(fy * s - ph * 0.32)
    cx = max(0, min(cx, scaled.width - pw)); cy = max(0, min(cy, scaled.height - ph))
    crop = scaled.crop((cx, cy, cx + pw, cy + ph))
    layer = Image.new('RGBA', (W, H), (0, 0, 0, 0)); layer.alpha_composite(crop.convert('RGBA'), (minx, TOP))
    mask = Image.new('L', (W, H), 0)
    # shrink the polygon by GAP/2 on the slanted edges so the backdrop shows between stripes
    q = [(x + (GAP // 2 if k in (0, 3) and i > 0 else 0) - (GAP // 2 if k in (1, 2) and i < 2 else 0), y) for k, (x, y) in enumerate(p)]
    ImageDraw.Draw(mask).polygon(q, fill=255)
    layer.putalpha(ImageChops.multiply(layer.getchannel('A'), mask))
    bg.alpha_composite(layer)
    # label pill at the bottom of the stripe
    tw = font.getlength(label)
    lx = (p[3][0] + p[2][0]) // 2 - int(tw) // 2 + SLANT // 2 - 8
    pill = Image.new('RGBA', (int(tw) + 40, 52), (0, 0, 0, 0))
    ImageDraw.Draw(pill).rounded_rectangle((0, 0, pill.width - 1, 51), 26, fill=(20, 22, 30, 200))
    ImageDraw.Draw(pill).text((20, 9), label, font=font, fill=(245, 245, 250, 255))
    bg.alpha_composite(pill, (max(minx + 30, lx), BOT - 84))

bg.convert('RGB').save('listing/12c-video-strips.png', 'PNG', optimize=True)
print('listing/12c-video-strips.png')
