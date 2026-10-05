"""Absolute timing check against the audio itself: for lines that start after >= 0.6 s of vocal silence, find where the
separated vocal energy rises, and compare our line start (and Spotify's) with that onset."""
import json, sys
from pathlib import Path
import numpy as np
import soundfile as sf

DATA = Path(__file__).parent / "data"
HOP_S = 0.01


def envelope(path: Path):
    x, sr = sf.read(str(path), dtype="float32")
    x = x.mean(1) if x.ndim > 1 else x
    hop = int(sr * HOP_S)
    n = len(x) // hop
    rms = np.sqrt((x[:n * hop].reshape(n, hop) ** 2).mean(1) + 1e-12)
    db = 20 * np.log10(rms)
    return np.convolve(db, np.ones(3) / 3, mode="same")


def onset_near(db, t_s, floor, search=(-1.0, 1.0)):
    """The first frame in the search window where the level rises above floor + 15 dB, preceded by 0.6 s below it."""
    lo = max(0, int((t_s + search[0]) / HOP_S)); hi = min(len(db), int((t_s + search[1]) / HOP_S))
    quiet = floor + 15
    for f in range(max(lo, 60), hi):
        if db[f] > quiet and db[f - 60:f].max() <= quiet:
            return f * HOP_S
    return None


def main(variant):
    rows = []
    for t in sorted(DATA.iterdir()):
        wav = t / "vocals.npu.wav"
        al = t / f"align.{variant}.json"
        if not wav.exists() or not al.exists(): continue
        db = envelope(wav)
        floor = np.percentile(db, 10)
        title = json.loads((t / "meta.json").read_text(encoding="utf-8"))["title"]
        ours, spot = [], []
        for l in json.loads(al.read_text(encoding="utf-8")):
            if not l["words"]: continue
            s = l["words"][0]["s"] / 1000
            on = onset_near(db, s, floor)
            if on is None: continue
            ours.append(s - on); spot.append(l["refStartMs"] / 1000 - on)
        if ours:
            o, sp = np.array(ours) * 1000, np.array(spot) * 1000
            rows.append((title, len(o), np.median(o), np.median(np.abs(o)), np.median(sp)))
            print(f"{title[:28]:28} lines {len(o):3}  ours-onset median {np.median(o):+5.0f} ms (|.| {np.median(np.abs(o)):3.0f})   spotify-onset median {np.median(sp):+5.0f} ms")
    return rows


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "vocals.stream")
