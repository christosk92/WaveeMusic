"""Lyrics alignment lab: separate vocals, force-align the reference lyric text, compare with Spotify's line timings.

Per track directory (written by Wavee.LyricsLab): meta.json, lyrics.<source>.json, audio.ogg.
Writes into the same directory: vocals.wav, align.<input>.json; and a summary across tracks to results.md.

The aligner only gets the TEXT of Spotify's lyrics, never its timings. Spotify's line starts are then the reference
the output is scored against (they are themselves human-made, so they carry their own error).
"""
import json, math, os, subprocess, sys, time, types
from pathlib import Path

sys.modules.setdefault("nagisa", types.ModuleType("nagisa"))   # Japanese tokenizer; no win-arm64 wheel, unused here

import numpy as np
import soundfile as sf
import torch

LAB = Path(__file__).parent
DATA = LAB / "data"
MODELS = LAB / "models"
SR = 16000
LANG = {"6habFhsOp2NvshLv26DqMb": "Spanish"}

torch.set_num_threads(os.cpu_count() or 8)


def decode(ogg: Path, wav: Path, sr: int | None, mono: bool):
    args = ["ffmpeg", "-v", "error", "-y", "-i", str(ogg)]
    if mono: args += ["-ac", "1"]
    if sr: args += ["-ar", str(sr)]
    subprocess.run(args + [str(wav)], check=True)


def separate(track: Path) -> tuple[Path, float]:
    out = track / "vocals.wav"
    if out.exists():
        return out, 0.0
    mix = track / "mix44.wav"
    decode(track / "audio.ogg", mix, None, False)
    from audio_separator.separator import Separator
    import logging
    t0 = time.perf_counter()
    s = Separator(str(mix), log_level=logging.WARNING, model_name="Kim_Vocal_2",
                  model_file_dir=str(MODELS / "uvr"), output_dir=str(track),
                  primary_stem_path=str(out), secondary_stem_path=str(track / "instrumental.wav"),
                  denoise_enabled=False, output_single_stem="Vocals")
    s.separate()
    dt = time.perf_counter() - t0
    mix.unlink(missing_ok=True)
    # audio-separator names the file itself in some versions; find what it wrote.
    if not out.exists():
        cands = sorted(track.glob("*Vocals*.wav"))
        if not cands: raise RuntimeError("separator wrote no vocals file")
        cands[0].rename(out)
    return out, dt


def load16k(path: Path) -> np.ndarray:
    tmp = path.with_suffix(".16k.wav")
    decode(path, tmp, SR, True)
    a, _ = sf.read(str(tmp), dtype="float32")
    tmp.unlink(missing_ok=True)
    return a


_aligner = None
def aligner():
    global _aligner
    if _aligner is None:
        from qwen_asr.inference.qwen3_forced_aligner import Qwen3ForcedAligner
        _aligner = Qwen3ForcedAligner.from_pretrained(str(MODELS / "Qwen3-ForcedAligner-0.6B"), dtype=torch.float32)
    return _aligner


def line_words(lines, lang):
    """Word list per line with the aligner's own tokenizer, so output items map back to lines by index."""
    proc = aligner().aligner_processor
    return [proc.encode_timestamp(l["text"], lang)[0] if l["text"].strip() else [] for l in lines]


def _align(audio, start_s, end_s, words, lang):
    chunk = audio[int(start_s * SR):int(end_s * SR)]
    res = aligner().align(audio=(chunk, SR), text=" ".join(words), language=lang)[0]
    items = [(it.start_time + start_s, it.end_time + start_s) for it in res.items]
    if len(items) != len(words):
        raise RuntimeError(f"aligner returned {len(items)} items for {len(words)} words")
    return items


def _good_prefix(items, end_s, last):
    """How many leading items look trustworthy: stop at the first pile-up (3 words in a row with no duration on one
    timestamp), at a jump backwards in time, or, unless this is the last window, near the window's end."""
    k = 0
    while k < len(items):
        s, e = items[k]
        if k + 2 < len(items) and all(items[j][1] - items[j][0] < 0.001 and abs(items[j][0] - s) < 0.001 for j in (k, k + 1, k + 2)):
            break
        if k > 0 and s < items[k - 1][0] - 0.5:
            break
        if not last and e > end_s - 3.0:
            break
        k += 1
    return k


def align_text(audio: np.ndarray, words: list[str], lang: str, window_s=45.0):
    """Text only: no reference timing. Each 45 s window gets the next words (capped at 5 per second); the trustworthy
    prefix is kept and the next window starts where it ended. A window that keeps nothing moves on by 10 s, which is
    how an instrumental section is crossed."""
    total_s = len(audio) / SR
    out = []
    start_s, i = 0.0, 0
    while i < len(words):
        end_s = min(total_s, start_s + window_s)
        last = end_s >= total_s - 0.01
        batch = words[i:i + int(window_s * 5)]
        items = _align(audio, start_s, end_s, batch, lang)
        k = len(items) if last else _good_prefix(items, end_s, last)
        if last:
            out += items
            break
        if k == 0:
            start_s += 10.0
            continue
        out += items[:k]
        i += k
        start_s = max(start_s + 1.0, out[-1][1])
    return out


def align_anchored(audio: np.ndarray, per_line: list[list[str]], ref_starts: list[float], lang: str, group_s=25.0, pad_s=3.0):
    """Line-anchored: the existing line starts only cut the audio into ~25 s windows (padded by 3 s on both sides);
    the aligner places every word inside its window."""
    total_s = len(audio) / SR
    out = []
    n = len(per_line)
    g0 = 0
    while g0 < n:
        g1 = g0 + 1
        while g1 < n and ref_starts[g1] - ref_starts[g0] < group_s: g1 += 1
        words = [w for ws in per_line[g0:g1] for w in ws]
        start_s = max(0.0, ref_starts[g0] - pad_s)
        end_s = min(total_s, (ref_starts[g1] if g1 < n else total_s) + pad_s)
        if words: out += _align(audio, start_s, end_s, words, lang)
        g0 = g1
    return out


def score(pred: list[float | None], ref: list[float]):
    errs = np.array([(p - r) * 1000 for p, r in zip(pred, ref) if p is not None])   # seconds -> ms
    if len(errs) == 0: return {}
    a = np.abs(errs)
    return {
        "n": int(len(errs)),
        "median_abs_ms": round(float(np.median(a))),
        "mean_abs_ms": round(float(a.mean())),
        "bias_ms": round(float(np.median(errs))),
        "p90_abs_ms": round(float(np.percentile(a, 90))),
        "within_150": round(float((a <= 150).mean()) * 100),
        "within_300": round(float((a <= 300).mean()) * 100),
        "within_500": round(float((a <= 500).mean()) * 100),
        "within_1000": round(float((a <= 1000).mean()) * 100),
    }


def provider_line_starts(track: Path, ref_lines):
    """Line starts of the other providers, matched to the reference lines by normalized text, as a noise floor."""
    def norm(s): return "".join(ch for ch in s.lower() if ch.isalnum())
    ref_keys = [norm(l["text"]) for l in ref_lines]
    out = {}
    for f in track.glob("lyrics.*.json"):
        doc = json.loads(f.read_text(encoding="utf-8"))
        if doc["provider"] == "spotify" or doc["sync"] in ("None", "Unsynced"): continue
        pool = {}
        for l in doc["lines"]:
            pool.setdefault(norm(l["text"]), []).append(l["startMs"] / 1000)
        used = {}
        pred = []
        for k, rl in zip(ref_keys, ref_lines):
            cands = pool.get(k, [])
            j = used.get(k, 0)
            pred.append(cands[j] if j < len(cands) and k else None)
            used[k] = j + 1
        out[doc["provider"]] = pred
    return out


def run(track: Path):
    meta = json.loads((track / "meta.json").read_text(encoding="utf-8"))
    ref_doc = json.loads((track / "lyrics.spotify.json").read_text(encoding="utf-8"))
    lines = [l for l in ref_doc["lines"] if l["text"].strip() and l["text"].strip() != "♪"]
    lang = LANG.get(meta["trackId"], "English")
    ref = [l["startMs"] / 1000 for l in lines]
    result = {"track": f'{meta["title"]} - {meta["artists"]}', "durationS": meta["durationMs"] / 1000,
              "lang": lang, "lines": len(lines)}

    vocals, sep_s = separate(track)
    result["separation_s"] = round(sep_s, 1)
    per_line = line_words(lines, lang)
    flat = [w for ws in per_line for w in ws]

    audios = {"mix": load16k(track / "audio.ogg"), "vocals": load16k(vocals)}
    variants = (("mix.text", "mix", "text"), ("vocals.text", "vocals", "text"), ("vocals.anchored", "vocals", "anchored"))
    for name, src, mode in variants:
        audio = audios[src]
        t0 = time.perf_counter()
        times = align_text(audio, flat, lang) if mode == "text" else align_anchored(audio, per_line, ref, lang)
        dt = time.perf_counter() - t0
        starts, k, words_out = [], 0, []
        for l, ws in zip(lines, per_line):
            seg = times[k:k + len(ws)]
            starts.append(seg[0][0] if seg else None)
            words_out.append({"text": l["text"], "refStartMs": l["startMs"],
                              "words": [{"w": w, "s": round(s * 1000), "e": round(e * 1000)} for w, (s, e) in zip(ws, seg)]})
            k += len(ws)
        (track / f"align.{name}.json").write_text(json.dumps(words_out, ensure_ascii=False, indent=1), encoding="utf-8")
        result[name] = score(starts, ref) | {"align_s": round(dt, 1)}
        print(f"  {name}: {result[name]}", flush=True)

    result["providers"] = {p: score(pred, ref) for p, pred in provider_line_starts(track, lines).items()}
    return result


def main():
    tracks = [DATA / a for a in sys.argv[1:]] or sorted(p for p in DATA.iterdir() if (p / "audio.ogg").exists())
    results = []
    for t in tracks:
        if not (t / "lyrics.spotify.json").exists():
            print(f"{t.name}: no Spotify lyrics, skipped"); continue
        results.append(run(t))
    (LAB / "results.json").write_text(json.dumps(results, indent=1), encoding="utf-8")


if __name__ == "__main__":
    main()
