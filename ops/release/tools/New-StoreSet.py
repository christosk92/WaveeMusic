"""Compose the whole Microsoft Store screenshot set from the captures Capture-StoreShots.ps1 wrote.

    python ops/release/tools/New-StoreSet.py                       # artifacts/store-shots/shots -> .../final
    python ops/release/tools/New-StoreSet.py --only 01-home 02-lyrics

One row per listing image, in Store order: the app as a whole first (the Store's listing preview shows the first
images), then one image each for the AI lyrics, music videos and the visualizers, then the everyday app. The image carries at most
an eyebrow and a short headline in its top band; the selling sentence is the CAPTION (Partner Center, <= 200 characters
per screenshot), written beside each PNG as <name>.caption.txt. Callouts name shot.json keys, so a recapture moves them
with the element. Never put the word "Spotify" or its logo on an image: the listing describes the account, the
pictures show Wavee.
"""
import argparse, pathlib, subprocess, sys

HERE = pathlib.Path(__file__).resolve().parent
REPO = HERE.parents[2]

# name: (shot, options, caption)
SET = [
    ("01-home", "home", dict(
        layout="hero", eyebrow="Wavee for Windows", headline="Your music,\nbuilt for Windows",
        tints="#3d5fd8,#c06a3a,#22306e"),
     "A fast, native music app for Windows 11: your Home, playlists, artists and podcasts, with synced lyrics beside them."),
    ("02-lyrics", "lyrics", dict(
        layout="hero", eyebrow="On-device AI", headline="Lyrics that follow\nevery word",
        spot=["ll1:0:7"], lens=["appearance.ai-lyrics[0:0.86]|Timed on your NPU|68,446,500|ai-settings-zoom"],
        tints="#6d4bd8,#d9774a,#3b2a7a"),
     "Word-by-word lyrics on a Copilot+ PC: Wavee times every word on the NPU while the song plays. The song and its lyrics never leave your PC."),
    ("03-video", "video-docked", dict(
        layout="grid", eyebrow="Music videos", headline="Watch it\nyour way",
        frame=["boaf-1.jpg", "boaf-2.jpg", "boaf-3.jpg", "boaf-2.jpg"],
        panel=["video-docked|video-area|Docked|2.2", "video-floating|player:1|Mini player|2.0",
               "video-detached|x|Own window|1.5|video-popout", "video-full|video-area|Full screen|1.0"],
        tints="#3b6fb0,#9a5a8a,#1a2a4a"),
     "Music videos play docked beside your music, as a mini player, in a window of their own, or full screen."),
    ("04-visualizers", "visualizer", dict(
        layout="hero", eyebrow="Full screen", headline="Nineteen\nvisualizers",
        tints="#1f8a9a,#3a6fb0,#14465a"),
     "A true full-screen view with nineteen visualizers, from calm ribbons to a spectrum round the cover, coloured by the album art."),
    ("05-playlist", "playlist", dict(
        layout="hero", eyebrow="Library", headline="Every playlist,\nand what plays next",
        tints="#3a7bd5,#2bb08a,#1e3a6e"),
     "Browse playlists, albums and Liked Songs with fast, artwork-forward track lists, and keep the queue open beside them."),
    ("06-album", "album", dict(
        layout="hero", eyebrow="Lossless", headline="Hear every\ndetail",
        lens=["stage:chips|Lossless FLAC|68,430,300|album-stage-zoom"],
        tints="#2f6fb0,#1f9ac9,#152a4a"),
     "Albums with release facts and credits, and lossless FLAC playback where your plan includes it."),
    ("07-search", "search", dict(
        layout="hero", eyebrow="Search", headline="Find anything,\nas you type",
        tints="#c08a3a,#3d6fd8,#3a2a1a"),
     "One search for songs, artists, albums, playlists, podcasts and audiobooks, with the top result first."),
    ("08-artist", "artist", dict(
        layout="hero", eyebrow="Artists", headline="Artists,\nup close",
        tints="#6a9a3a,#3b7a9a,#2e5a2a"),
     "Artist pages with top tracks, the artist's pick, biography and releases, tinted by the artwork."),
    ("09-podcasts", "podcast", dict(
        layout="hero", eyebrow="Podcasts", headline="Podcasts that\nremember your place",
        tints="#1f6fd0,#0a9ad0,#20304a"),
     "Follow shows, pick up episodes where you left off, and see what is new since your last visit."),
]


def main() -> int:
    a = argparse.ArgumentParser()
    a.add_argument("--shots", default=str(REPO / "artifacts" / "store-shots" / "shots"))
    a.add_argument("--out", default=str(REPO / "artifacts" / "store-shots" / "final"))
    a.add_argument("--frames", default=str(REPO / "artifacts" / "store-shots" / "frames"),
                   help="real frames of the music video the video scene plays (its picture is DRM-protected)")
    a.add_argument("--only", nargs="*", default=[])
    o = a.parse_args()
    shots, out = pathlib.Path(o.shots), pathlib.Path(o.out)
    for name, shot, opt, caption in SET:
        if o.only and name not in o.only:
            continue
        cmd = [sys.executable, str(HERE / "New-StoreImage.py"), "--shot", str(shots / shot), "--out", str(out / f"{name}.png"),
               "--caption", caption]
        for k, v in opt.items():
            for item in (v if isinstance(v, list) else [v]):
                if k == "lens":
                    parts = item.split("|")
                    if len(parts) > 3:
                        parts[3] = str(shots / parts[3])
                    item = "|".join(parts)
                elif k == "panel":
                    parts = item.split("|")
                    parts[0] = str(shots / parts[0])
                    if len(parts) > 4:
                        parts[4] = str(shots / parts[4])
                    item = "|".join(parts)
                elif k == "frame":
                    item = str(pathlib.Path(o.frames) / item)
                cmd += [f"--{k}", item.replace("\n", "\\n") if k == "headline" else item]
        subprocess.run(cmd, check=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
