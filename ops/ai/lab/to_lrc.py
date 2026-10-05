"""align.<variant>.json -> enhanced LRC (word stamps), the word-synced format Wavee's Lyrics.Text.ParseLrc reads.
Each line: [mm:ss.xx]<mm:ss.xx>word <mm:ss.xx>word ... <mm:ss.xx> (the last stamp closes the final word).
A word without a time (an optional ad-lib the singer skipped) keeps the previous word's end as its start."""
import json, sys
from pathlib import Path

DATA = Path(__file__).parent / "data"


def ts(ms: float) -> str:
    ms = max(0, int(round(ms)))
    return f"{ms // 60000:02d}:{(ms // 1000) % 60:02d}.{(ms % 1000) // 10:02d}"


def fill(line, next_start_ms=None) -> list:
    """Every word of the line text, in order: timed words from the aligner, and each run of words it skipped (optional
    ad-libs) spread evenly over the gap between its timed neighbours. The gap never runs past the next line's start. A
    line with no timed word at all falls back to its line timestamp."""
    from aligner import line_words
    timed = list(line["words"])
    full, j = [], 0
    for w, _ in line_words(line["text"]):
        if j < len(timed) and timed[j]["w"] == w:
            full.append(dict(timed[j])); j += 1
        else:
            full.append({"w": w, "s": None, "e": None})
    if not full: return []
    i = 0
    while i < len(full):
        if full[i]["s"] is not None: i += 1; continue
        k = i
        while k < len(full) and full[k]["s"] is None: k += 1           # run [i, k) is untimed
        left = full[i - 1]["e"] if i > 0 else None
        right = full[k]["s"] if k < len(full) else None
        n = k - i
        if left is None and right is None:                             # nothing timed in the line
            left = line["refStartMs"]
            right = left + 400 * n                                     # a natural pace, not the whole gap
            if next_start_ms is not None: right = min(right, next_start_ms)
        elif left is None:
            left = max(right - 300 * n, line["refStartMs"] if line["refStartMs"] < right else right - 300 * n)
        elif right is None:
            right = left + 300 * n
            if next_start_ms is not None: right = min(right, next_start_ms)
        right = max(right, left)
        step = (right - left) / n
        for m in range(n):
            full[i + m]["s"] = left + m * step; full[i + m]["e"] = left + (m + 1) * step
        i = k
    return full


def to_lrc(lines) -> str:
    out = []
    for li, l in enumerate(lines):
        # the next line's start: its first timed word, or its line timestamp when nothing in it was heard
        nxt = next(((x["words"][0]["s"] if x["words"] else x["refStartMs"]) for x in lines[li + 1:] if x["text"].strip()), None)
        ws = fill(l, nxt)
        if not ws: continue
        parts = [f"[{ts(ws[0]['s'])}]"]
        for i, w in enumerate(ws):
            parts.append(f"<{ts(w['s'])}>{w['w']}" + (" " if i < len(ws) - 1 else ""))
        end = ws[-1]["e"] if nxt is None else min(ws[-1]["e"], nxt)        # never run into the next line
        parts.append(f"<{ts(max(end, ws[-1]['s']))}>")
        out.append("".join(parts))
    return "\n".join(out) + "\n"


if __name__ == "__main__":
    variant = sys.argv[1] if len(sys.argv) > 1 else "vocals.final"
    for t in sorted(DATA.iterdir()):
        f = t / f"align.{variant}.json"
        if f.exists():
            (t / f"{variant}.lrc").write_text(to_lrc(json.loads(f.read_text(encoding="utf-8"))), encoding="utf-8")
            print("wrote", t / f"{variant}.lrc")
