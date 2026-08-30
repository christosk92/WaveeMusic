"""Cut a Microsoft Store trailer from the composed listing shots: each still gets a slow push-in (Ken Burns), the cuts
are crossfades, the hero art opens and closes it. Output is what Partner Center asks for - MP4/H.264 1920x1080, 30 fps,
under 60 s - plus the 1920x1080 PNG thumbnail it wants alongside.

    python New-StoreTrailer.py                           # from artifacts/store: listing/trailer.mp4 + trailer-thumb.png
    python New-StoreTrailer.py --hold 3.2 --fade 0.7     # pacing

Silent by default (a Store trailer needs no music, and a licensed track is a separate decision); an AAC silence track is
muxed in so every player treats it as a normal video. Needs ffmpeg 6+ on PATH (winget Gyan.FFmpeg).
"""
import argparse, os, subprocess

# the sequence: (file, seconds); the first/last are the title and end cards
SEQUENCE = [
    ("listing/hero-1920x1080.png", 2.6),
    ("listing/01-home.png", 3.0),
    ("listing/02-artist.png", 3.0),
    ("listing/03-liked.png", 3.0),
    ("listing/04-playlist.png", 3.0),
    ("listing/07-album.png", 3.0),
    ("listing/12c-video-strips.png", 3.4),
    ("listing/13-queue.png", 3.0),
    ("listing/08-concerts.png", 3.0),
    ("listing/10-search.png", 3.0),
    ("listing/09-customize.png", 3.0),
    ("listing/hero-1920x1080.png", 2.6),
]
FPS = 30


def build(out: str, thumb: str, hold_scale: float, fade: float, zoom_to: float):
    seq = [(f, d * hold_scale) for f, d in SEQUENCE]
    for f, _ in seq:
        if not os.path.exists(f):
            raise SystemExit(f"missing {f} - compose the listing shots first")
    args = ["ffmpeg", "-y", "-hide_banner", "-loglevel", "error", "-stats"]
    for f, d in seq:
        args += ["-loop", "1", "-framerate", str(FPS), "-t", f"{d:.3f}", "-i", f]
    args += ["-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=48000"]

    parts = []
    for i, (f, d) in enumerate(seq):
        frames = int(round(d * FPS))
        # zoompan works in whole pixels; pushing in on a 6x upscale keeps the motion sub-pixel smooth
        step = (zoom_to - 1.0) / frames
        parts.append(
            f"[{i}:v]scale=5760:-1,zoompan=z='min(1+{step:.6f}*on,{zoom_to})':d={frames}"
            f":x='iw/2-(iw/zoom/2)':y='ih/2-(ih/zoom/2)':s=1920x1080:fps={FPS},"
            f"format=yuv420p,setsar=1[v{i}]"
        )
    # chain the crossfades; each offset is the running length minus the fades already spent
    prev, t = "v0", 0.0
    for i in range(1, len(seq)):
        t += seq[i - 1][1] - fade
        parts.append(f"[{prev}][v{i}]xfade=transition=fade:duration={fade}:offset={t:.3f}[x{i}]")
        prev = f"x{i}"
    parts.append(f"[{prev}]format=yuv420p[vout]")          # xfade promotes to 4:4:4; High profile wants 4:2:0
    total = sum(d for _, d in seq) - fade * (len(seq) - 1)

    args += ["-filter_complex", ";".join(parts), "-map", "[vout]", "-map", f"{len(seq)}:a",
             "-c:v", "libx264", "-preset", "slow", "-crf", "18", "-profile:v", "high", "-level", "4.1",
             "-r", str(FPS), "-c:a", "aac", "-b:a", "96k", "-t", f"{total:.3f}", "-movflags", "+faststart", out]
    print(f"{len(seq)} shots, {total:.1f} s")
    subprocess.run(args, check=True)
    # the thumbnail: the home shot (the Store shows it before the trailer plays)
    subprocess.run(["ffmpeg", "-y", "-hide_banner", "-loglevel", "error", "-i", "listing/01-home.png",
                    "-frames:v", "1", thumb], check=True)
    print(out, thumb)


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default="listing/trailer.mp4")
    ap.add_argument("--thumb", default="listing/trailer-thumb.png")
    ap.add_argument("--hold", type=float, default=1.0, help="multiply every hold (1.0 = the table above)")
    ap.add_argument("--fade", type=float, default=0.6)
    ap.add_argument("--zoom", type=float, default=1.06, help="push-in end scale per shot")
    a = ap.parse_args()
    build(a.out, a.thumb, a.hold, a.fade, a.zoom)
