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
PLAY_EPISODE = ["play uri=spotify:episode:sh0e0", "frames 30"]
PLAY_VIDEO = ["play uri=spotify:track:tr26", "frames 30"]          # the rich album's first row: it has a music video
CLICK = lambda key: ["click key=%s settle=false" % key, "frames 40"]


def OVERFLOW_QUEUE(w, h):
    """Zune at the medium bar tier (800 wide): the queue panel opens from the bar's overflow menu, whose items are unkeyed.
    The menu opens upward from `more` with its right edge 12 DIP in from the window's, Queue its third row: a point click at
    (w - 80, h - 158) for the board's own size (checked at 800x600 and 800x640). The pointer then rests on the title bar's empty
    middle, so the queue row that opened under it draws no hover."""
    return ["click key=more", "frames 20", "click x=%d y=%d settle=false" % (w - 80, h - 158), "frames 40", "hover x=%d y=12" % (w // 2)]


# target helpers: (label, mount args, verbs)
def T(label="", verbs=None, tiers=None, only=None, real=False, **mount):
    """tiers: {tier: {overrides}} (verbs/label/skip/any mount arg) for a tier whose keys differ (Compact has no queue button, ...).
    only: the tiers this render exists at (the others skip it). real: also emit a copy with `images=real` (the bundled photographs
    instead of the fake decoder's gradients), for the hero and photo boards whose text sits over a picture; a dict instead of
    True is that copy's own overrides (another uri, label)."""
    d = {"label": label}
    if tiers:
        d["_tiers"] = tiers
    if only:
        d["_only"] = list(only)
    if real:
        d["_real"] = real if isinstance(real, dict) else {}
    d.update(mount)
    if verbs:
        d["verbs"] = verbs
    return d

ALBUM = "spotify:album:al2"
ARTIST = "spotify:artist:ar0"
SHOW = "spotify:show:sh0"
EPISODE = "spotify:episode:sh0e0"
CONCERT = "spotify:concert:cc3n0"          # cc0n0 never loads (ar0 has no concerts): an endless skeleton
USER = "spotify:user:christos"             # a followed user
OWN_USER = "spotify:user:wavee-listener"   # the own profile ("Open account", "Only visible to you")
EMPTY_PLAYLIST = "spotify:playlist:plx0"   # own, empty: "Nothing here yet", the inline search, Recommended

# Zune's Huge and Huge4K boards dock the right panel beside the page with a track playing: Huge in two panes (queue, then
# lyrics or friends as the board picks), Huge4K in three (queue, lyrics, friends). The app opens it from the bar's queue word;
# the second pane starts on lyrics, its friends pivot switches it. Families not listed draw no panel at Huge.
ZUNE_DOCK = {
    "lyrics": ["Home", "Browse", "Search", "Artist", "Album", "Concert", "Library-Playlists", "Library-Audiobooks", "ReleaseNotes",
               "Settings-Appearance", "Settings-Playback", "Feedback", "Theme-Options"],
    "friends": ["Frame", "Home-Podcasts", "Browse-Category", "Playlist", "Show", "Episode", "Profile", "Liked", "Library-Artists",
                "Library-Podcasts", "Recents", "Settings-Sections"],
}

# family -> {"zune": stem, "winui": stem, "std": {theme: name}, "targets": [...], "tiers": limit or None}
FAMILIES = [
    ("Frame", {"zune": "Z-Frame", "winui": "W-Shell", "std": {"zune": "Z-Frame-Home"}, "targets": [T("home", route="home")]}),
    ("Frame-Panel", {"zune": "Z-Frame-Panel", "targets": [T("album with the queue panel docked", uri=ALBUM, verbs=PLAY + CLICK("queue"), tiers={
        "Compact": {"label": "album, playing, the panel sheet on friends (the mini bar opens it)",
                    "verbs": PLAY + CLICK("zune-bar-left") + ["click key=zune-panel-pivot:Friends force=true settle=false", "frames 40"]},
        "Small": {"label": "album, playing, the queue panel over it (from the overflow menu)", "verbs": PLAY + OVERFLOW_QUEUE(800, 600)}})]}),
    ("Frame-Narrow", {"zune": "Z-Frame-Narrow", "targets": [T("narrow window, playing, the queue panel over home (from the overflow menu)",
                                                              route="home", verbs=PLAY + OVERFLOW_QUEUE(800, 640))]}),
    ("Shell-Short", {"zune": "Z-Shell-Short", "winui": "W-Shell-Short", "targets": [T("minimum height", route="home", h=330)]}),
    ("PlayBar", {"zune": "Z-PlayBar", "targets": [
        T("music playing", route="home", verbs=PLAY),
        T("podcast episode playing", route="home", verbs=PLAY_EPISODE),
        # the flyouts live at the medium tier (760-1239): at the full tier the volume glyph mutes beside its inline slider
        T("volume flyout (the medium tier: 800 wide)", route="home", w=800, verbs=PLAY + CLICK("volume"), only=["Standard", "Small"]),
        T("overflow menu (the medium tier: 800 wide)", route="home", w=800, verbs=PLAY + ["click key=more", "frames 30"], only=["Standard", "Small"]),
        T("speed flyout (the medium tier: 800 wide)", route="home", w=800, verbs=PLAY_EPISODE + CLICK("episode-speed"), only=["Standard", "Small"]),
        # the video word toggles video; its chevron, or a right click anywhere on the slot, opens the placement menu
        T("video menu", route="home", verbs=PLAY_VIDEO + ["click key=video button=right settle=false", "frames 40"], only=["Huge"]),
        T("device picker", route="home", verbs=PLAY + CLICK("devices"), only=["Huge"])]}),
    ("Overlays", {"zune": "Z-Overlays", "targets": [
        T("search suggestions", route="home", verbs=CLICK("zune-search-pill") + ["type late", "frames 20"], tiers={"Compact": {"verbs": CLICK("zune-search-icon-host") + ["type late", "frames 20"]}, "Small": {"verbs": CLICK("zune-search-icon-host") + ["type late", "frames 20"]}}),
        T("account menu", route="home", verbs=CLICK("zchip")),
        T("device picker", route="home", verbs=PLAY + CLICK("devices"), tiers={"Compact": {"skip": True}}),
        T("notifications", route="home", verbs=CLICK("zune-bell"), tiers={"Compact": {"skip": True}, "Small": {"skip": True}})]}),
    ("Home", {"zune": "Z-Home", "winui": "W-Home", "targets": [T("home", route="home")]}),
    # `arg=podcasts-chip` is the facet's route key (what the chip's click navigates to), so it works at Small too, where the
    # band shows no facet row to click
    ("Home-Podcasts", {"zune": "Z-Home-Podcasts", "targets": [T("podcasts facet, an episode playing", route="home", arg="podcasts-chip", verbs=PLAY_EPISODE)]}),
    ("Browse", {"zune": "Z-Browse", "winui": "W-Browse", "targets": [T("browse", route="browse")]}),
    ("Browse-Category", {"zune": "Z-Browse-Category", "winui": "W-BrowseCategory", "targets": [T("category", route="browse:spotify:page:0JQ5DAqbMKFEC4WFtoNRpw")]}),
    ("Search", {"zune": "Z-Search", "winui": "W-Search", "targets": [T("search results", route="search", arg="late")]}),
    ("Search-NoResults", {"winui": "W-NoResults", "targets": [T("search, no results", route="search", arg="xyzzy")]}),
    # the photo copy opens ar9 (The Longtime): its header is the bundled bright photograph (a white sky over a pale ground), the
    # worst case for text over a picture; ar0's header is a wash either way
    ("Artist", {"zune": "Z-Artist", "winui": "W-Artist", "targets": [T("artist", uri=ARTIST, real={"uri": "spotify:artist:ar9", "label": "artist ar9, the bright header photo"})]}),
    ("Discography", {"winui": "W-Discography", "targets": [T("discography", route="disco:1:" + ARTIST)]}),
    ("Album", {"zune": "Z-Album", "winui": "W-Album", "targets": [T("album", uri=ALBUM, real=True)]}),
    ("Playlist", {"zune": ["Z-Playlist-Owned-Followed", "Z-Playlist-Followed"], "winui": ["W-Playlist", "W-PlaylistFollowed"],
                  "targets": [T("owned playlist", uri="spotify:playlist:pl0"), T("followed playlist", uri="spotify:playlist:hp0")]}),
    ("Show", {"zune": "Z-Show", "winui": "W-Show", "targets": [T("show", uri=SHOW, real=True)]}),
    ("Episode", {"zune": "Z-Episode", "winui": "W-Episode", "targets": [T("episode", uri=EPISODE, real=True)]}),
    ("Concert", {"zune": "Z-Concert", "winui": "W-Concert", "targets": [T("concert", uri=CONCERT, real=True)]}),
    ("Profile", {"zune": "Z-Profile", "winui": "W-Profile", "targets": [T("profile", uri=USER, real=True)],
                 "targets_winui": [T("own profile", uri=OWN_USER, real=True)]}),
    ("Liked", {"zune": "Z-Liked", "winui": "W-LikedSongs", "targets": [T("liked songs", route="liked")]}),
    ("Library-Albums", {"zune": "Z-Library-Albums", "winui": "W-Library", "targets": [T("albums", route="albums")]}),
    # WinUI's Library list board is Artists: the list (A-Z on the board, recents in the app) with the detail pane on the first row
    ("Library-Artists", {"zune": "Z-Library-Artists", "winui": "W-LibraryList", "targets": [T("artists", route="artists")]}),
    ("Library-Playlists", {"zune": "Z-Library-Playlists", "targets": [T("playlists", route="playlists")]}),
    ("Library-Podcasts", {"zune": "Z-Library-Podcasts", "targets": [T("podcasts", route="podcasts")]}),
    ("Library-Audiobooks", {"zune": "Z-Library-Audiobooks", "targets": [T("audiobooks", route="audiobooks")]}),
    ("Recents", {"zune": "Z-Recents", "winui": "W-Recents", "targets": [T("recents", route="recents")]}),
    ("Queue-Page", {"zune": "Z-Queue-Page", "winui": "W-Queue", "targets": [T("up next", route="home", stage="queue", gallery="off", verbs=PLAY + ["key f11", "frames 60"])]}),
    ("NowPlaying", {"zune": "Z-NowPlaying", "winui": "W-Stage", "targets": [T("now playing stage", route="home", stage="visualizer", gallery="off", verbs=PLAY + ["key f11", "frames 60"], real=True)]}),
    ("NowPlaying-Card", {"zune": "Z-NowPlaying-Card", "targets": [T("card layout", route="home", stage="visualizer", gallery="off", layout="card", verbs=PLAY + ["key f11", "frames 60"], real=True)]}),
    ("NowPlaying-LargeArt", {"zune": "Z-NowPlaying-LargeArt", "targets": [T("large art layout", route="home", stage="visualizer", gallery="off", layout="large", verbs=PLAY + ["key f11", "frames 60"], real=True)]}),
    ("NowPlaying-Centered", {"zune": "Z-NowPlaying-Centered", "winui": "W-StageCentered", "targets": [T("centered layout", route="home", stage="visualizer", gallery="off", layout="centered", verbs=PLAY + ["key f11", "frames 60"], real=True)]}),
    ("NowPlaying-Artist", {"zune": "Z-NowPlaying-Artist", "winui": "W-StageArtist", "targets": [T("artist layout", route="home", stage="visualizer", gallery="off", layout="artist", verbs=PLAY + ["key f11", "frames 60"], real=True)]}),
    ("Lyrics", {"zune": "Z-Lyrics", "winui": "W-Lyrics", "targets": [T("lyrics stage", route="home", stage="lyrics", gallery="off", verbs=PLAY + ["key f11", "frames 90"], real=True)],
                "targets_winui": [T("lyrics stage, large cover layout", route="home", stage="lyrics", layout="large", gallery="off", verbs=PLAY + ["key f11", "frames 90"], real=True)]}),
    ("Video", {"zune": "Z-Video", "winui": "W-Video", "targets": [T("fullscreen video", route="home", video="fullscreen", chrome="shown")],
               # WinUI: the floating mini player; at Standard also its placement menu, which a right click on the bar's video
               # split opens (as its unkeyed chevron does) at any size; Huge docks the video in the rail. The board draws a
               # bound player with its controls: player=fake binds one, join=poster drops the offline "Loading..." ring and
               # chrome=shown asks for the transport at rest
               "targets_winui": [T("floating mini player", route="home", video="floating", player="fake", join="poster", chrome="shown", verbs=["frames 30"], tiers={
                   "Standard": {"label": "floating mini player + the placement menu", "verbs": ["frames 30", "click key=winui-bar-video button=right settle=false", "frames 40"]},
                   "Huge": {"label": "video docked in the rail", "video": "docked"}})]}),
    # chrome=shown draws the presenter's transport at rest, which mounts only with a player bound (player=fake)
    ("Mini", {"zune": "Z-Mini", "targets": [T("floating video mini player", route="home", video="floating", chrome="shown", player="fake"),
                                            T("video docked in the panel", route="home", video="docked", chrome="shown", player="fake")]}),
    ("Setup", {"zune": "Z-Setup", "winui": "W-Setup", "targets": [T("setup wizard", route="home", setup=True, frames=120)]}),
    ("ReleaseNotes", {"zune": "Z-ReleaseNotes", "winui": "W-ReleaseNotes", "targets": [T("release notes", route="whatsnew")]}),
    # Zune's board draws three panels: report a problem, the crash prompt and "couldn't start" (see GAPS). WinUI's crash prompt is
    # W-Crash; its W-Dialog is a different dialog (UNMAPPED).
    ("Dialogs", {"zune": "Z-Dialogs", "winui": "W-Crash", "targets": [
        T("report a problem", route="settings", verbs=["dialog kind=report", "frames 30"]),
        T("crash prompt", route="home", verbs=["dialog kind=crash", "frames 30"])],
        "targets_winui": [T("crash prompt", route="home", verbs=["dialog kind=crash", "frames 30"])]}),
    ("Settings-Appearance", {"zune": "Z-Settings-Appearance", "winui": "W-Settings", "targets": [T("appearance", route="settings", arg="appearance")]}),
    ("Settings-Playback", {"zune": "Z-Settings-Playback", "targets": [T("playback", route="settings", arg="playback")]}),
    ("Settings-Sections", {"zune": "Z-Settings-Sections", "winui": "W-SettingsSections", "targets": [T("storage", route="settings", arg="storage")]}),
    ("Feedback", {"zune": "Z-Feedback", "winui": "W-Feedback", "targets": [T("feedback and help", route="settings", arg="feedback")]}),
    ("Theme-Options", {"zune": "Z-Theme-Options", "winui": "W-ThemeOptions", "targets": [T("theme options (appearance)", route="settings", arg="appearance")]}),
    # `page=` draws the vacancy in a page's own shape (library: the navigator skeleton; podcasts: no followed show); an exe
    # built before 2026-10-24 refuses the arg
    ("State-Loading", {"zune": "Z-State-Loading", "winui": "W-SkeletonList", "targets": [T("loading", route="home", verbs=["state kind=loading"])],
                       "targets_winui": [T("library artists, loading", route="artists", verbs=["state kind=loading page=artists"])]}),
    ("State-Empty", {"zune": "Z-State-Empty", "winui": "W-EmptyLibrary", "targets": [T("empty", route="albums", verbs=["state kind=empty"]), T("no results", route="home", verbs=["state kind=nomatch"])],
                     "targets_winui": [T("library podcasts, empty", route="podcasts", verbs=["state kind=empty page=podcasts"])]}),
    ("State-Empty-Playlist", {"zune": "Z-State-Empty-Playlist", "winui": "W-EmptyPlaylist", "targets": [T("own empty playlist", uri=EMPTY_PLAYLIST)]}),
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
        "Z-State-Rows": "no verb recipe yet: hover, focus, selected, multi-select, playing, dragging rows together",
        "Z-State-Rows-Compact": "no verb recipe yet: hover, focus, selected, multi-select, playing, dragging rows together",
    },
    "winui": {
        "W-Dialog": ("no verb: the board's \"Set up local playback\" ContentDialog is, in the app, the setup wizard's Local playback page "
                     "(Setup.WizardPage.LocalPlayback), behind its terms and sign-in pages; `dialog` opens crash|hang|report|lightbox only"),
    },
}

# a part of a mapped board that no render covers: board family -> reason (shown beside the renders, not instead of them)
GAPS = {
    "zune": {
        "Dialogs": ("the \"wavee couldn't start\" panel is the engine-free recovery screen (Screens/Recovery.Win32.cs: a Win32 "
                    "TaskDialog shown before the engine boots, on --recovery or a boot loop): no inspect verb draws it. "
                    "`dialog kind=hang` is the crash prompt with the hang wording, not that panel"),
    },
}


def tier_boards(canvas_names, stems, std_override):
    out = []
    for stem in stems:
        for tier, suf in SUFFIX:
            name = (std_override if (tier == "Standard" and std_override) else stem + suf)
            if name in canvas_names and not any(name == o[1] for o in out):
                out.append((tier, name))
    return out


def dock(r, mode, tier):
    """Zune Huge/Huge4K: play (unless the render already plays something), open the queue panel, and at Huge switch the second
    pane to friends when the board shows it there (Huge4K's third pane is friends already)."""
    verbs = list(r.get("verbs", []))
    if not any(v.startswith("play ") for v in verbs):
        verbs = PLAY + verbs
    verbs += CLICK("queue")
    if mode == "friends" and tier == "Huge":
        verbs += CLICK("zune-panel-pivot:Friends")
    r["verbs"] = verbs
    r["label"] = (r.get("label") or "") + (", queue + lyrics + friends docked" if tier == "Huge4K" else ", queue + %s docked" % mode)
    return r


def build(theme):
    canvas = json.load(open(os.path.join(ROOT, "docs", "themes", "boards", theme, "project", "canvas.json"), encoding="utf-8"))
    names = [k[:-8] for k in canvas["boards"]]
    nameset = set(names)
    boards, claimed, gaps = {}, set(), {}
    dock_mode = {f: m for m, fams in ZUNE_DOCK.items() for f in fams} if theme == "zune" else {}
    for family, spec in FAMILIES:
        if theme not in spec:
            continue
        stems = spec[theme] if isinstance(spec[theme], list) else [spec[theme]]
        targets = spec.get("targets_" + theme, spec["targets"])
        for tier, name in tier_boards(nameset, stems, spec.get("std", {}).get(theme)):
            if name in claimed:
                continue
            claimed.add(name)
            renders = []
            for t in targets:
                if "_only" in t and tier not in t["_only"]:
                    continue
                r = {"family": family, "tier": tier}
                r.update({k: v for k, v in t.items() if not k.startswith("_")})
                ov = t.get("_tiers", {}).get(tier, {})
                if ov.get("skip"):
                    continue
                r.update(ov)
                if tier in ("Huge", "Huge4K") and family in dock_mode:
                    r = dock(r, dock_mode[family], tier)
                renders.append(r)
                if "_real" in t:
                    copy = dict(r, images="real")
                    copy.update(t["_real"])
                    copy["label"] = copy["label"] + " (images=real)"
                    renders.append(copy)
            # the Followed boards render only the followed playlist (the Owned-Followed board shows both)
            if family == "Playlist" and "Followed" in name and "Owned" not in name:
                renders = [r for r in renders if r["label"].startswith("followed")]
            boards[name] = renders
            if family in GAPS.get(theme, {}):
                gaps[name] = GAPS[theme][family]
    if theme == "zune":
        for name, (family, targets) in LEGACY_ZUNE.items():
            if name in nameset:
                boards[name] = [dict({"family": family, "tier": "Standard"}, **t) for t in targets]
                claimed.add(name)
    unmapped = {n: r for n, r in UNMAPPED.get(theme, {}).items() if n in nameset}
    return {
        "_doc": ("board -> [render]. A render is {family, tier, label, w?, h?, kind?, frames?, verbs?: [inspect verb lines run after the mount settles and "
                 "before render+lint], and every other key is a `mount` arg (route, arg, uri, stage, layout, video, setup, images, ...)}. w/h default to the board's "
                 "canvas.json size. unmapped: board -> reason (shown as board only). gaps: board -> a part of the board no render covers. "
                 "Generated by gen-gallery-maps.py: edit the table there and rerun."),
        "theme": theme, "defaults": {"frames": 90},
        "boards": boards, "unmapped": unmapped, "gaps": gaps,
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
