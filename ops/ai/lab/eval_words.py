"""Word-level timing: our word starts against every word-synced provider (AMLL, NetEase, Kugou, QQ), and those
providers against each other (the human noise floor).

Usage: eval_words.py [variant ...]   (default: vocals.npu). Prints per-track and pooled stats; writes words.md."""
import difflib, json, re, sys, unicodedata
from pathlib import Path
import numpy as np

LAB = Path(__file__).parent
DATA = LAB / "data"
WORD_SOURCES = ("amll", "netease", "kugou", "qq")


def norm(w: str) -> str:
    w = unicodedata.normalize("NFKD", w.lower())
    return "".join(c for c in w if c.isalnum())


def provider_doc(path: Path):
    doc = json.loads(path.read_text(encoding="utf-8"))
    return doc if doc["sync"] == "Syllable" else None


def provider_words(doc, shift_ms=0.0):
    """[(word, startMs)] from a syllable-synced document: syllables map to characters, a word starts at its first char.
    Words come from the line text when its characters match the syllables (AMLL syllables carry no spaces), else from
    the syllables joined as written (NetEase/Kugou put the space inside the syllable)."""
    out = []
    for l in doc["lines"]:
        sy = l["words"]
        if not sy: continue
        chars = []
        for s in sy:
            for c in s["text"]:
                if norm(c): chars.append(s["startMs"] - shift_ms)
        joined = "".join(s["text"] for s in sy)
        text = l["text"] if norm(l["text"]) == norm(joined) else joined
        k = 0
        for w in re.findall(r"[\w'’]+", text):
            n = norm(w)
            if not n: continue
            if k < len(chars): out.append((n, chars[k]))
            k += len(n)
    return out


def line_offset(doc, spotify_doc):
    """Median (provider line start - Spotify line start) over lines with the same text: the provider's own constant
    offset against the recording Spotify plays. Computed from the two documents only, never from our output."""
    pool = {}
    for l in spotify_doc["lines"]:
        pool.setdefault(norm(l["text"]), []).append(l["startMs"])
    used, d = {}, []
    for l in doc["lines"]:
        k = norm(l["text"])
        if not k or k not in pool: continue
        j = used.get(k, 0)
        if j < len(pool[k]): d.append(l["startMs"] - pool[k][j]); used[k] = j + 1
    return float(np.median(d)) if len(d) >= 5 else 0.0


def our_words(path: Path):
    d = json.loads(path.read_text(encoding="utf-8"))
    return [(norm(w["w"]), w["s"]) for l in d for w in l["words"] if norm(w["w"])]


def match(a, b):
    """Pairs (ta, tb) for words matched by text along the two sequences."""
    sm = difflib.SequenceMatcher(None, [x[0] for x in a], [x[0] for x in b], autojunk=False)
    pairs = []
    for blk in sm.get_matching_blocks():
        for i in range(blk.size):
            pairs.append((a[blk.a + i][1], b[blk.b + i][1]))
    return pairs


def stats(errs, offset_free=True):
    """offset_free: subtract the median signed error first (one constant per song and reference)."""
    e = np.array(errs, float)
    if len(e) == 0: return None
    bias = float(np.median(e))
    a = np.abs(e - bias) if offset_free else np.abs(e)
    return {"n": len(e), "median": float(np.median(a)), "bias": bias, "p90": float(np.percentile(a, 90)),
            "w50": float((a <= 50).mean() * 100), "w100": float((a <= 100).mean() * 100), "w200": float((a <= 200).mean() * 100),
            "w500": float((a <= 500).mean() * 100)}


def fmt(s):
    if not s: return "| - | - | - | - | - | - | - | - |"
    return (f"| {s['n']} | {s['median']:.0f} ms | {s['bias']:+.0f} ms | {s['p90']:.0f} ms | {s['w50']:.0f}% | {s['w100']:.0f}% | "
            f"{s['w200']:.0f}% | {s['w500']:.0f}% |")


HEAD = ["| Compared | vs | Words | Median (offset-free) | Song offset | p90 | <=50 ms | <=100 ms | <=200 ms | <=500 ms |",
        "|---|---|---|---|---|---|---|---|---|---|"]


def evaluate(variants):
    out = []
    pooled = {v: [] for v in variants}
    pooled["refs"] = []
    for t in sorted(DATA.iterdir()):
        if not (t / "meta.json").exists(): continue
        title = json.loads((t / "meta.json").read_text(encoding="utf-8"))["title"]
        spot = json.loads((t / "lyrics.spotify.json").read_text(encoding="utf-8"))
        refs = {}
        for s in WORD_SOURCES:
            f = t / f"lyrics.{s}.json"
            if f.exists():
                doc = provider_doc(f)
                if not doc: continue
                off = line_offset(doc, spot)
                w = provider_words(doc, 0.0)
                if len(w) > 30 and s not in ("qq",) or (s == "qq" and "kugou" not in refs and len(w) > 30):
                    refs[f"{s} ({off:+.0f})"] = w
        rows = []
        for v in variants:
            f = t / f"align.{v}.json"
            if not f.exists(): continue
            ours = our_words(f)
            for s, rw in refs.items():
                errs = [x - y for x, y in match(ours, rw)]
                if errs: pooled[v] += list(np.array(errs) - np.median(errs))
                rows.append(f"| {v} | {s} " + fmt(stats(errs)))
        names = list(refs)
        for i in range(len(names)):
            for j in range(i + 1, len(names)):
                errs = [x - y for x, y in match(refs[names[i]], refs[names[j]])]
                if errs: pooled["refs"] += list(np.array(errs) - np.median(errs))
                rows.append(f"| {names[i]} | {names[j]} " + fmt(stats(errs)))
        out += [f"### {title}", ""] + HEAD + rows + [""]
    summary = ["## Pooled word starts", ""] + HEAD
    for k, errs in pooled.items():
        summary.append(f"| {'providers vs each other' if k == 'refs' else k} | all " + fmt(stats(errs)))
    return summary + [""] + out


if __name__ == "__main__":
    variants = sys.argv[1:] or ["vocals.npu"]
    lines = evaluate(variants)
    (LAB / "words.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print("\n".join(lines[:4 + len(variants) + 1]))
