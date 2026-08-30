#!/usr/bin/env python3
"""Build the WaveeIcons OTF/CFF icon font from the app's custom SVG paths.

Why OTF/CFF (not TTF): the source SVGs use cubic beziers + elliptical arcs; CFF (Type2)
charstrings are cubic-native, so parse_path -> T2CharStringPen transfers the outlines with
NO quadratic conversion (which would round-trip the arcs through an approximation).

Coordinate mapping: the SVG viewBox is 24x24 with y pointing DOWN; a font em is 1000 units
with y pointing UP. S = 1000/24 scales viewBox -> em; the Transform(S,0,0,-S,0,S*24) matrix
flips y (the -S in the d-slot) and shifts the origin up by one em-box height (e = S*24 = 1000)
so the top-left SVG corner lands at (0, 1000) = the em-box top-left in font space.

Glyphs:
  0xE900 playNext  -> "Play next"  (play-on-top mark)
  0xE901 playAfter -> "Play after" (play-on-bottom / add-to-queue mark)
  0xE902 lyrics     -> lyrics/chat bubble

Run:  python build-wavee-icons.py     (writes wavee-icons.otf next to this file)
Requires fonttools (4.63 present).
"""

import os

from fontTools.fontBuilder import FontBuilder
from fontTools.misc.transform import Transform
from fontTools.pens.boundsPen import BoundsPen
from fontTools.pens.t2CharStringPen import T2CharStringPen
from fontTools.pens.transformPen import TransformPen
from fontTools.svgLib.path import parse_path
from fontTools.ttLib import TTFont

# ── Constants ────────────────────────────────────────────────────────────────────────────
UPM = 1000                      # units per em
GLYPH_ORDER = [".notdef", "playNext", "playAfter", "lyrics"]
CMAP = {0xE900: "playNext", 0xE901: "playAfter", 0xE902: "lyrics"}

# Spotify's real "Play next" / "Add to queue" marks (24x24 viewBox).
PATHS = {
    "playNext": "M6 2.86V5H3a1 1 0 00-1 1v12a1 1 0 102 0V7h2v2.137a.5.5 0 00.748.434L13 5.998 6.748 2.426A.5.5 0 006 2.86ZM21 5h-5a1 1 0 100 2h5a1 1 0 100-2Zm0 6H9a1 1 0 000 2h12a1 1 0 000-2Zm0 6H9a1 1 0 000 2h12a1 1 0 000-2Z",
    "playAfter": "M21 6.998a1 1 0 100-2H9a1 1 0 000 2h12ZM6 21.138a.5.5 0 00.748.434L13 18l-6.252-3.573A.5.5 0 006 14.86V17H4V6a1 1 0 00-2 0v12a1 1 0 001 1h3v2.138Zm15-8.14a1 1 0 000-2H9a1 1 0 000 2h12Zm0 6a1 1 0 000-2h-5a1 1 0 000 2h5Z",
    "lyrics": "m9.67 13.982-2.43 2.474c-.472.471-.79.675-1.145.675-.479 0-.623-.314-.623-1.012v-2.137H5.26c-1.406 0-1.915-.146-2.429-.42a2.877 2.877 0 0 1-1.192-1.192c-.274-.514-.421-1.024-.421-2.429V6.464c0-1.405.147-1.915.421-2.428a2.872 2.872 0 0 1 1.192-1.192c.514-.275 1.023-.421 2.429-.421h7.68c1.406 0 1.915.146 2.429.421a2.86 2.86 0 0 1 1.192 1.192c.274.513.421 1.023.421 2.428v3.477c0 1.405-.147 1.915-.421 2.429a2.866 2.866 0 0 1-1.192 1.192c-.514.274-1.023.42-2.429.42H9.67Zm-.974-.957c.257-.261.608-.408.974-.408h3.27c1.076 0 1.426-.068 1.785-.26.276-.147.484-.356.631-.632.192-.358.26-.709.26-1.784V6.464c0-1.075-.068-1.426-.26-1.784a1.49 1.49 0 0 0-.631-.631c-.359-.192-.709-.26-1.785-.26H5.26c-1.075 0-1.425.068-1.785.26a1.5 1.5 0 0 0-.631.631c-.192.358-.26.709-.26 1.784v3.477c0 1.075.068 1.426.26 1.784.148.276.356.485.631.632.36.192.71.26 1.785.26h.212c.754 0 1.365.611 1.365 1.365v.934l1.859-1.891ZM5.422 8.01c0-.821.67-1.383 1.554-1.383.976 0 1.599.726 1.599 1.634 0 1.73-1.46 2.084-2.242 2.084-.222 0-.381-.148-.381-.329 0-.173.084-.294.372-.364.502-.12 1.005.028 1.274-.491h-.056c-.185.208-.483.242-.771.242-.837 0-1.349-.614-1.349-1.393Zm4.204 0c0-.821.669-1.383 1.553-1.383.976 0 1.6.726 1.6 1.634 0 1.73-1.46 2.084-2.242 2.084-.223 0-.381-.148-.381-.329 0-.173.084-.294.372-.364.502-.12 1.004.028 1.274-.491h-.056c-.186.208-.483.242-.772.242-.837 0-1.348-.614-1.348-1.393Z",
}

VIEWBOXES = {"playNext": 24, "playAfter": 24, "lyrics": 18}


def build_charstring(d, viewbox):
    """Parse one SVG path 'd' into a scaled/flipped CFF T2 charstring."""
    scale = UPM / viewbox
    flip = Transform(scale, 0, 0, -scale, 0, UPM)
    pen = T2CharStringPen(UPM, {})            # advance = em width; no component glyphs
    tpen = TransformPen(pen, flip)            # apply the viewBox->em + y-flip on the fly
    parse_path(d, tpen)                       # handles M/L/V/H/a/Z incl. arcs -> cubics
    return pen.getCharString()


def build():
    here = os.path.dirname(os.path.abspath(__file__))
    out = os.path.join(here, "wavee-icons.otf")

    charstrings = {}
    # .notdef MUST be an empty charstring (an outline-less, valid glyph 0).
    notdef = T2CharStringPen(UPM, {})
    charstrings[".notdef"] = notdef.getCharString()
    for name, d in PATHS.items():
        charstrings[name] = build_charstring(d, VIEWBOXES[name])

    fb = FontBuilder(UPM, isTTF=False)
    fb.setupGlyphOrder(GLYPH_ORDER)
    fb.setupCharacterMap(CMAP)
    fb.setupCFF("WaveeIcons", {"FullName": "WaveeIcons"}, charstrings, {})
    fb.setupHorizontalMetrics({g: (UPM, 0) for g in GLYPH_ORDER})
    fb.setupHorizontalHeader(ascent=UPM, descent=0)
    fb.setupNameTable({"familyName": "WaveeIcons", "styleName": "Regular"})
    fb.setupOS2(sTypoAscender=UPM, sTypoDescender=0, usWinAscent=UPM, usWinDescent=0)
    fb.setupPost()
    fb.save(out)
    return out


def verify(path):
    """Reload the saved font and assert the cmap, outlines, and size are sane."""
    size = os.path.getsize(path)
    font = TTFont(path)
    cmap = font.getBestCmap()
    assert 0xE900 in cmap, "cmap missing 0xE900 (playNext)"
    assert 0xE901 in cmap, "cmap missing 0xE901 (playAfter)"
    assert 0xE902 in cmap, "cmap missing 0xE902 (lyrics)"
    assert cmap[0xE900] == "playNext", "0xE900 -> %r" % cmap[0xE900]
    assert cmap[0xE901] == "playAfter", "0xE901 -> %r" % cmap[0xE901]
    assert cmap[0xE902] == "lyrics", "0xE902 -> %r" % cmap[0xE902]
    # The glyphs must carry real outlines (the bounding-box check is the true
    # bounding-box check is the true "not broken/empty" guard; size is a coarse floor).
    gs = font.getGlyphSet()
    bounds = {}
    for name in ("playNext", "playAfter", "lyrics"):
        bp = BoundsPen(gs)
        gs[name].draw(bp)
        assert bp.bounds is not None, "%s has no outline" % name
        bounds[name] = tuple(round(v) for v in bp.bounds)
    assert size > 512, "font is only %d bytes (suspiciously empty)" % size
    return size, cmap, bounds


if __name__ == "__main__":
    out = build()
    size, cmap, bounds = verify(out)
    print("OK  wrote %s" % out)
    print("    size: %d bytes" % size)
    print("    cmap: 0xE900 -> %s, 0xE901 -> %s, 0xE902 -> %s" % (cmap[0xE900], cmap[0xE901], cmap[0xE902]))
    print("    bbox playNext : %s" % (bounds["playNext"],))
    print("    bbox playAfter: %s" % (bounds["playAfter"],))
    print("    bbox lyrics   : %s" % (bounds["lyrics"],))
