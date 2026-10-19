"""gen-gallery-maps.py - writes gallery-map.<theme>.json from one table of families.

    python -I tools/themes/gen-gallery-maps.py            # zune + winui
    python -I tools/themes/gen-gallery-maps.py zune

A FAMILY is a page or surface that exists at several tiers. Its app targets (a mount + extra inspect verbs) are the same at every
tier; only the window size changes (the board's own size from canvas.json). A theme names its boards differently, so each family
carries a stem per theme: board = stem + "", "-Compact", "-Small", "-Huge", "-Huge4K" (when the canvas has it). `std` overrides
the Standard board's name. The generator only emits boards that exist on the theme's canvas, and lists every other board of the
canvas under "unmapped" with its reason (a spec sheet, or a surface no route reaches yet).

Edit the table here, rerun, commit both. The maps are plain JSON; gallery.py reads them (see gallery.py's header).
"""

import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
SUFFIX = [("Compact", "-Compact"), ("Small", "-Small"), ("Standard", ""), ("Huge", "-Huge"), ("Huge4K", "-Huge4K")]

PLAY = ["play uri=spotify:track:tr3 next=4", "frames 30"]
CLICK = lambda key: ["click key=%s settle=false" % key, "frames 40"]

# target helpers: (label, mount args, verbs)
def T(label="", verbs=None, tiers=None, **mount):
    """tiers: {tier: {overrides}} (verbs/label/skip) for a tier whose keys differ (Compact has no queue button, ...)."""
    d = {"label": label}
    if tiers:
        d["_tiers"] = tiers
    d.update(mount)
    if verbs:
        d["verbs"] = verbs
    return d

ALBUM = "spotify:album:al2"
ARTIST = "spotify:artist:ar0"
SHOW = "spotify:show:sh0"
EPISODE = "spotify:episode:sh0e0"
CONCERT = "spotify:concert:cc0n0"
USER = "spotify:user:christos"

# family -> {"zune": stem, "winui": stem, "std": {theme: name}, "targets": [...], "tiers": limit or None}
FAMILIES = [
    ("Frame", {"zune": "Z-Frame", "winui": "W-Shell", "std": {"zune": "Z-Frame-Home"}, "targets": [T("home", route="home")]}),
    ("Frame-Panel", {"zune": "Z-Frame-Panel", "targets": [T("album with the queue panel docked", uri=ALBUM, verbs=PLAY + CLICK("queue"), tiers={"Compact": {"label": "album, playing (panel sheet needs the mini bar: no direct verb yet)", "verbs": PLAY}, "Small": {"label": "album, playing (the panel opens from the overflow menu: no direct verb yet)", "verbs": PLAY}})]}),
    ("Frame-Narrow", {"zune": "Z-Frame-Narrow", "targets": [T("narrow window", route="home")]}),
    ("Shell-Short", {"zune": "Z-Shell-Short", "winui": "W-Shell-Short", "targets": [T("minimum height", route="home", h=330)]}),
    ("PlayBar", {"zune": "Z-PlayBar", "targets": [
        T("music playing", route="home", verbs=PLAY),
        T("podcast episode playing", route="home", verbs=["play uri=spotify:episode:sh0e0", "frames 30"]),
        T("volume flyout", route="home", verbs=PLAY + CLICK("volume"), tiers={"Compact": {"skip": True}})]}),
    ("Overlays", {"zune": "Z-Overlays", "targets": [
        T("search suggestions", route="home", verbs=CLICK("zune-search-pill") + ["type late", "frames 20"], tiers={"Compact": {"verbs": CLICK("zune-search-icon-host") + ["type late", "frames 20"]}, "Small": {"verbs": CLICK("zune-search-icon-host") + ["type late", "frames 20"]}}),
        T("account menu", route="home", verbs=CLICK("zchip")),
        T("device picker", route="home", verbs=PLAY + CLICK("devices"), tiers={"Compact": {"skip": True}}),
        T("notifications", route="home", verbs=CLICK("zune-bell"), tiers={"Compact": {"skip": True}, "Small": {"skip": True}})]}),
    ("Home", {"zune": "Z-Home", "winui": "W-Home", "targets": [T("home", route="home")]}),
    ("Home-Podcasts", {"zune": "Z-Home-Podcasts", "targets": [T("podcasts facet, an episode playing", route="home", verbs=CLICK("zune-band-view:podcasts-chip") + ["play uri=spotify:episode:sh0e0", "frames 30"], tiers={"Small": {"verbs": ["play uri=spotify:episode:sh0e0", "frames 30"], "label": "home (no facet row at Small: the podcasts facet has no direct verb), episode playing"}})]}),
    ("Browse", {"zune": "Z-Browse", "winui": "W-Browse", "targets": [T("browse", route="browse")]}),
    ("Browse-Category", {"zune": "Z-Browse-Category", "winui": "W-BrowseCategory", "targets": [T("category", route="browse:spotify:page:0JQ5DAqbMKFEC4WFtoNRpw")]}),
    ("Search", {"zune": "Z-Search", "winui": "W-Search", "targets": [T("search results", route="search", arg="late")]}),
    ("Artist", {"zune": "Z-Artist", "winui": "W-Artist", "targets": [T("artist", uri=ARTIST)]}),
    ("Discography", {"winui": "W-Discography", "targets": [T("discography", route="disco:1:" + ARTIST)]}),
    ("Album", {"zune": "Z-Album", "winui": "W-Album", "targets": [T("album", uri=ALBUM)]}),
    ("Playlist", {"zune": ["Z-Playlist-Owned-Followed", "Z-Playlist-Followed"], "winui": ["W-Playlist", "W-PlaylistFollowed"],
                  "targets": [T("owned playlist", uri="spotify:playlist:pl0"), T("followed playlist", uri="spotify:playlist:hp0")]}),
    ("Show", {"zune": "Z-Show", "winui": "W-Show", "targets": [T("show", uri=SHOW)]}),
    ("Episode", {"zune": "Z-Episode", "winui": "W-Episode", "targets": [T("episode", uri=EPISODE)]}),
    ("Concert", {"zune": "Z-Concert", "winui": "W-Concert", "targets": [T("concert", uri=CONCERT)]}),
    ("Profile", {"zune": "Z-Profile", "winui": "W-Profile", "targets": [T("profile", uri=USER)]}),
    ("Liked", {"zune": "Z-Liked", "winui": "W-LikedSongs", "targets": [T("liked songs", route="liked")]}),
    ("Library-Albums", {"zune": "Z-Library-Albums", "winui": "W-Library", "targets": [T("albums", route="albums")]}),
    ("Library-Artists", {"zune": "Z-Library-Artists", "targets": [T("artists", route="artists")]}),
    ("Library-Playlists", {"zune": "Z-Library-Playlists", "winui": "W-LibraryList", "targets": [T("playlists", route="playlists")]}),
    ("Library-Podcasts", {"zune": "Z-Library-Podcasts", "targets": [T("podcasts", route="podcasts")]}),
    ("Library-Audiobooks", {"zune": "Z-Library-Audiobooks", "targets": [T("audiobooks", route="audiobooks")]}),
    ("Recents", {"zune": "Z-Recents", "winui": "W-Recents", "targets": [T("recents", route="recents")]}),
    ("Queue-Page", {"zune": "Z-Queue-Page", "winui": "W-Queue", "targets": [T("up next", route="home", stage="queue", gallery="off", verbs=PLAY + ["key f11", "frames 60"])]}),
    ("NowPlaying", {"zune": "Z-NowPlaying", "winui": "W-Stage", "targets": [T("now playing stage", route="home", stage="visualizer", gallery="off", verbs=PLAY + ["key f11", "frames 60"])]}),
    ("NowPlaying-Card", {"zune": "Z-NowPlaying-Card", "targets": [T("card layout", route="home", stage="visualizer", gallery="off", layout="card", verbs=PLAY + ["key f11", "frames 60"])]}),
    ("NowPlaying-LargeArt", {"zune": "Z-NowPlaying-LargeArt", "targets": [T("large art layout", route="home", stage="visualizer", gallery="off", layout="large", verbs=PLAY + ["key f11", "frames 60"])]}),
    ("NowPlaying-Centered", {"zune": "Z-NowPlaying-Centered", "winui": "W-StageCentered", "targets": [T("centered layout", route="home", stage="visualizer", gallery="off", layout="centered", verbs=PLAY + ["key f11", "frames 60"])]}),
    ("NowPlaying-Artist", {"zune": "Z-NowPlaying-Artist", "winui": "W-StageArtist", "targets": [T("artist layout", route="home", stage="visualizer", gallery="off", layout="artist", verbs=PLAY + ["key f11", "frames 60"])]}),
    ("Lyrics", {"zune": "Z-Lyrics", "winui": "W-Lyrics", "targets": [T("lyrics stage", route="home", stage="lyrics", gallery="off", verbs=PLAY + ["key f11", "frames 90"])]}),
    ("Video", {"zune": "Z-Video", "winui": "W-Video", "targets": [T("fullscreen video", route="home", video="fullscreen", chrome="shown")]}),
    ("Setup", {"zune": "Z-Setup", "winui": "W-Setup", "targets": [T("setup wizard", route="home", setup=True, frames=120)]}),
    ("ReleaseNotes", {"zune": "Z-ReleaseNotes", "winui": "W-ReleaseNotes", "targets": [T("release notes", route="whatsnew")]}),
    ("Dialogs", {"zune": "Z-Dialogs", "winui": "W-Dialog", "targets": [
        T("report a problem", route="settings", verbs=["dialog kind=report", "frames 30"]),
        T("crash prompt", route="home", verbs=["dialog kind=crash", "frames 30"]),
        T("hang / recovery prompt", route="home", verbs=["dialog kind=hang", "frames 30"])]}),
    ("Settings-Appearance", {"zune": "Z-Settings-Appearance", "winui": "W-Settings", "targets": [T("appearance", route="settings", arg="appearance")]}),
    ("Settings-Playback", {"zune": "Z-Settings-Playback", "targets": [T("playback", route="settings", arg="playback")]}),
    ("Settings-Sections", {"zune": "Z-Settings-Sections", "winui": "W-SettingsSections", "targets": [T("storage", route="settings", arg="storage")]}),
    ("Feedback", {"zune": "Z-Feedback", "winui": "W-Feedback", "targets": [T("feedback and help", route="settings", arg="feedback")]}),
    ("Theme-Options", {"zune": "Z-Theme-Options", "winui": "W-ThemeOptions", "targets": [T("theme options (appearance)", route="settings", arg="appearance")]}),
    ("State-Loading", {"zune": "Z-State-Loading", "winui": "W-SkeletonList", "targets": [T("loading", route="home", verbs=["state kind=loading"])]}),
    ("State-Empty", {"zune": "Z-State-Empty", "winui": "W-EmptyLibrary", "targets": [T("empty", route="albums", verbs=["state kind=empty"]), T("no results", route="home", verbs=["state kind=nomatch"])]}),
    ("State-Error", {"zune": "Z-State-Error", "winui": "W-Offline", "targets": [T("error", route="home", verbs=["state kind=error"]), T("offline", route="home", verbs=["state kind=offline"])]}),
    ("Light-Home", {"zune": "Z-Light-Home", "winui": "W-HomeLight", "targets": [T("home, light", route="home", kind="light")]}),
    ("Light-Album", {"zune": "Z-Light-Album", "winui": "W-AlbumLight", "targets": [T("album, light", uri=ALBUM, kind="light")]}),
    ("Light-PlayBar", {"zune": "Z-Light-PlayBar", "winui": "W-PlayerLight", "targets": [T("play bar, light", route="home", kind="light", verbs=PLAY)]}),
]

# the older row boards on the Zune canvas
LEGACY_ZUNE = {
    "Album": ("Legacy-Album", [T("album", uri=ALBUM)]),
    "Artist": ("Legacy-Artist", [T("artist", uri=ARTIST)]),
    "Search": ("Legacy-Search", [T("search results", route="search", arg="late")]),
    "Liked": ("Legacy-Liked", [T("liked songs", route="liked")]),
    "Playlist-Page": ("Legacy-Playlist", [T("playlist", uri="spotify:playlist:pl0")]),
    "Album-Page": ("Legacy-Album-Panorama", [T("album", uri=ALBUM)]),
}

UNMAPPED = {
    "zune": {
        "Main": "row study: the playlist table row in isolation (no page route draws it alone)",
        "Compare": "row study: Modern vs Classic vs Zune side by side, not an app screen",
        "Queue": "row study: the 420 wide queue panel as a fragment; Frame-Panel renders the docked panel",
        "Z-Tokens": "spec sheet: tokens, type ramp, shapes (no screen)",
        "Z-Sizes-Breakpoints": "spec sheet: the tier table (no screen)",
        "Z-Part-TitleBar": "part sheet: the title bar alone (the frames show it in place)",
        "Z-Part-Band": "part sheet: the pivot band alone (the frames show it in place)",
        "Z-Part-PlayBar": "part sheet: the play bar alone, six tiers in one strip (PlayBar renders it in place)",
        "Z-Part-Panel": "part sheet: the right panel alone (Frame-Panel renders it in place)",
        "Z-Mini": "no route: the floating mini video player; add a mount that docks the video at 1000x620",
        "Z-State-Empty-Playlist": "no route: a brand-new empty playlist (the fake catalogue has none)",
        "Z-State-Empty-Playlist-Compact": "no route: a brand-new empty playlist (the fake catalogue has none)",
        "Z-State-Rows": "no verb recipe yet: hover, focus, selected, multi-select, playing, dragging rows together",
        "Z-State-Rows-Compact": "no verb recipe yet: hover, focus, selected, multi-select, playing, dragging rows together",
    },
    "winui": {},
}


def tier_boards(canvas_names, stems, std_override):
    out = []
    for stem in stems:
        for tier, suf in SUFFIX:
            name = (std_override if (tier == "Standard" and std_override) else stem + suf)
            if name in canvas_names and not any(name == o[1] for o in out):
                out.append((tier, name))
    return out


def build(theme):
    canvas = json.load(open(os.path.join(ROOT, "docs", "themes", "boards", theme, "project", "canvas.json"), encoding="utf-8"))
    names = [k[:-8] for k in canvas["boards"]]
    nameset = set(names)
    boards, claimed = {}, set()
    for family, spec in FAMILIES:
        if theme not in spec:
            continue
        stems = spec[theme] if isinstance(spec[theme], list) else [spec[theme]]
        for tier, name in tier_boards(nameset, stems, spec.get("std", {}).get(theme)):
            if name in claimed:
                continue
            claimed.add(name)
            renders = []
            for t in spec["targets"]:
                r = {"family": family, "tier": tier}
                r.update({k: v for k, v in t.items() if k != "_tiers"})
                ov = t.get("_tiers", {}).get(tier, {})
                if ov.get("skip"):
                    continue
                r.update(ov)
                renders.append(r)
            # the Followed boards render only the followed playlist (the Owned-Followed board shows both)
            if family == "Playlist" and "Followed" in name and "Owned" not in name:
                renders = [r for r in renders if r["label"].startswith("followed")]
            boards[name] = renders
    if theme == "zune":
        for name, (family, targets) in LEGACY_ZUNE.items():
            if name in nameset:
                boards[name] = [dict({"family": family, "tier": "Standard"}, **t) for t in targets]
                claimed.add(name)
    unmapped = {n: r for n, r in UNMAPPED.get(theme, {}).items() if n in nameset}
    return {
        "_doc": ("board -> [render]. A render is {family, tier, label, w?, h?, kind?, frames?, verbs?: [inspect verb lines run after the mount settles and "
                 "before render+lint], and every other key is a `mount` arg (route, arg, uri, stage, layout, video, setup, ...)}. w/h default to the board's "
                 "canvas.json size. unmapped: board -> reason (shown as board only). Generated by gen-gallery-maps.py."),
        "theme": theme, "defaults": {"frames": 90},
        "boards": boards, "unmapped": unmapped,
    }


def main():
    themes = sys.argv[1:] or ["zune", "winui"]
    for theme in themes:
        m = build(theme)
        path = os.path.join(HERE, "gallery-map.%s.json" % theme)
        with open(path, "w", encoding="utf-8", newline="\n") as fh:
            json.dump(m, fh, indent=1)
            fh.write("\n")
        print("%s: %d boards mapped (%d renders), %d unmapped -> %s" % (
            theme, len(m["boards"]), sum(len(v) for v in m["boards"].values()), len(m["unmapped"]), path))


if __name__ == "__main__":
    main()
