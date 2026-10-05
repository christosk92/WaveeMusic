"""Word-start refinement against the separated vocals: CTC marks a letter once it is confident, which is after the sound
starts. Each word start moves back to where its sound begins: the rise out of silence, or the energy dip that
separates it from the previous syllable. Usage: refine.py <in variant> <out variant> [max_back_ms] [dip_db]"""
import json, sys
from pathlib import Path
import numpy as np
import soundfile as sf

DATA = Path(__file__).parent / "data"
HOP = 0.01


def envelope(path: Path):
    x, sr = sf.read(str(path), dtype="float32")
    x = x.mean(1) if x.ndim > 1 else x
    h = int(sr * HOP); n = len(x) // h
    db = 20 * np.log10(np.sqrt((x[:n * h].reshape(n, h) ** 2).mean(1) + 1e-12))
    return np.convolve(db, np.ones(3) / 3, mode="same")


def refine_line(words, db, quiet, max_back=0.30, dip_db=4.0, min_gap=0.0, prev_end=None):
    """prev_end: the end (s) of the previous line's last word; a start never moves before it."""
    out = []
    for w in words:
        s, e = w["s"] / 1000, w["e"] / 1000
        lo = s - max_back if prev_end is None else max(prev_end + min_gap, s - max_back)
        f_lo, f_s = max(0, int(lo / HOP)), int(s / HOP)
        f_hi = min(len(db) - 1, f_s + 5)
        new = s
        if f_hi > f_lo:
            seg = db[f_lo:f_hi + 1]
            q = np.where(seg < quiet)[0]
            if len(q):                                          # rises out of silence inside the window
                last_q = q[-1]
                if last_q + 1 < len(seg): new = (f_lo + last_q + 1) * HOP
            elif dip_db > 0:                                    # a syllable boundary: the energy dip
                m = int(np.argmin(seg[:max(1, f_s - f_lo + 1)]))
                if seg[m] <= db[min(f_s, len(db) - 1)] - dip_db: new = (f_lo + m + 1) * HOP
        new = min(new, s)
        if prev_end is not None: new = max(new, prev_end)
        out.append(dict(w, s=round(new * 1000)))
        prev_end = e
    return out


def main(src, dst, max_back=0.30, dip_db=4.0):
    for t in sorted(DATA.iterdir()):
        a, wav = t / f"align.{src}.json", t / "vocals.npu.wav"
        if not a.exists() or not wav.exists(): continue
        db = envelope(wav)
        quiet = np.percentile(db, 10) + 15
        lines = json.loads(a.read_text(encoding="utf-8"))
        for l in lines: l["words"] = refine_line(l["words"], db, quiet, max_back, dip_db)
        (t / f"align.{dst}.json").write_text(json.dumps(lines, ensure_ascii=False, indent=1), encoding="utf-8")


if __name__ == "__main__":
    src, dst = sys.argv[1], sys.argv[2]
    mb = float(sys.argv[3]) / 1000 if len(sys.argv) > 3 else 0.30
    dd = float(sys.argv[4]) if len(sys.argv) > 4 else 4.0
    main(src, dst, mb, dd)
