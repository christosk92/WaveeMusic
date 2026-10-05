"""CTC forced alignment (wav2vec2) of Spotify's lyric text, over the whole song in one monotonic Viterbi pass.

Variants per track, written as align.<name>.json next to the Qwen ones:
  vocals.ctc       wav2vec2 on the separated vocals, one pass over the whole song
  mix.ctc          the same on the full mix
  vocals.ctc.snap  vocals.ctc, then each line start pushed forward to the next onset of singing when it sits in silence
"""
import json, re, sys, time, unicodedata
from pathlib import Path

import numpy as np
import torch
from transformers import Wav2Vec2ForCTC, Wav2Vec2Processor

from align import DATA, LAB, LANG, MODELS, SR, load16k, score

MODEL = {"English": "wav2vec2-large-960h-lv60-self", "Spanish": "wav2vec2-large-xlsr-53-spanish"}
FRAME_S = 0.02            # wav2vec2: one frame per 320 samples at 16 kHz
CHUNK_S = 30.0

_models = {}
def model(lang):
    if lang not in _models:
        d = MODELS / MODEL[lang]
        _models[lang] = (Wav2Vec2Processor.from_pretrained(d), Wav2Vec2ForCTC.from_pretrained(d).eval())
    return _models[lang]


@torch.inference_mode()
def emissions(audio: np.ndarray, lang: str) -> np.ndarray:
    """Log-probabilities per 20 ms frame, computed in 30 s chunks (the frames are local, so chunking is safe)."""
    proc, m = model(lang)
    out = []
    step = int(CHUNK_S * SR)
    for i in range(0, len(audio), step):
        chunk = audio[i:i + step]
        if len(chunk) < SR // 10: break
        x = proc(chunk, sampling_rate=SR, return_tensors="pt").input_values
        out.append(torch.log_softmax(m(x).logits[0], dim=-1).numpy())
    return np.concatenate(out)


def word_tokens(word: str, vocab: dict) -> list[int]:
    upper = "E" in vocab
    w = word.upper() if upper else word.lower()
    ids = []
    for ch in w:
        if ch not in vocab:
            ch = "".join(c for c in unicodedata.normalize("NFKD", ch) if not unicodedata.combining(c))
        if ch in vocab and ch not in ("|",): ids.append(vocab[ch])
    return ids


def viterbi(em: np.ndarray, tokens: list[int], blank: int = 0):
    """Standard CTC forced alignment: states are blank, t1, blank, t2, ..., blank. Returns per token (first, last) frame."""
    T, L = len(em), len(tokens)
    S = 2 * L + 1
    lab = np.full(S, blank); lab[1::2] = tokens
    skip = np.zeros(S, bool)
    skip[3::2] = np.array(tokens[1:]) != np.array(tokens[:-1])     # may jump over a blank between different tokens
    NEG = -1e30
    dp = np.full(S, NEG); dp[0] = em[0, blank]; dp[1] = em[0, lab[1]]
    bp = np.zeros((T, S), np.int8)
    for t in range(1, T):
        stay = dp
        one = np.concatenate(([NEG], dp[:-1]))
        two = np.where(skip, np.concatenate(([NEG, NEG], dp[:-2])), NEG)
        best = np.maximum(stay, np.maximum(one, two))
        bp[t] = np.where(best == stay, 0, np.where(best == one, 1, 2))
        dp = best + em[t, lab]
    s = S - 1 if dp[S - 1] >= dp[S - 2] else S - 2
    spans = [[-1, -1] for _ in range(L)]
    for t in range(T - 1, -1, -1):
        if s % 2 == 1:
            k = s // 2
            spans[k][0] = t
            if spans[k][1] < 0: spans[k][1] = t
        s -= int(bp[t, s])
    return spans


def align_ctc(audio: np.ndarray, per_line_words: list[list[str]], lang: str):
    proc, _ = model(lang)
    vocab = proc.tokenizer.get_vocab()
    em = emissions(audio, lang)
    sep = vocab["|"]
    tokens, owner = [], []                  # owner: (line, word) per token, -1 for separators
    for li, words in enumerate(per_line_words):
        for wi, w in enumerate(words):
            ids = word_tokens(w, vocab)
            if not ids: continue
            if tokens: tokens.append(sep); owner.append(None)
            tokens += ids; owner += [(li, wi)] * len(ids)
    spans = viterbi(em, tokens)
    times = {}
    for (f0, f1), o in zip(spans, owner):
        if o is None: continue
        s, e = f0 * FRAME_S, (f1 + 1) * FRAME_S
        if o in times: times[o] = (min(times[o][0], s), max(times[o][1], e))
        else: times[o] = (s, e)
    return times


def vocal_activity(vocals16k: np.ndarray):
    hop = int(FRAME_S * SR)
    n = len(vocals16k) // hop
    rms = np.sqrt(np.mean(vocals16k[:n * hop].reshape(n, hop) ** 2, axis=1) + 1e-12)
    db = 20 * np.log10(rms)
    db = np.convolve(db, np.ones(5) / 5, mode="same")             # 100 ms smoothing
    return db > np.percentile(db, 95) - 30


def snap(start_s: float, active: np.ndarray, limit_s: float = 1.5) -> float:
    f = int(start_s / FRAME_S)
    if f >= len(active) or active[f]: return start_s
    for g in range(f, min(len(active), f + int(limit_s / FRAME_S))):
        if active[g]: return g * FRAME_S
    return start_s


def run(track: Path):
    meta = json.loads((track / "meta.json").read_text(encoding="utf-8"))
    ref_doc = json.loads((track / "lyrics.spotify.json").read_text(encoding="utf-8"))
    lines = [l for l in ref_doc["lines"] if l["text"].strip() and l["text"].strip() != "♪"]
    lang = LANG.get(meta["trackId"], "English")
    ref = [l["startMs"] / 1000 for l in lines]
    per_line = [re.findall(r"[\w'’]+", l["text"]) for l in lines]
    result = {"track": f'{meta["title"]} - {meta["artists"]}', "lines": len(lines), "lang": lang}
    vocals = load16k(track / "vocals.wav")
    active = vocal_activity(vocals)
    inputs = [("vocals.ctc", vocals)] + ([("mix.ctc", load16k(track / "audio.ogg"))] if "--mix" in sys.argv else [])
    for name, audio in inputs:
        t0 = time.perf_counter()
        times = align_ctc(audio, per_line, lang)
        dt = time.perf_counter() - t0
        variants = [(name, lambda s: s)] + ([("vocals.ctc.snap", lambda s: snap(s, active))] if name == "vocals.ctc" else [])
        for vname, fix in variants:
            starts, out = [], []
            for li, (l, ws) in enumerate(zip(lines, per_line)):
                wt = [(w, times.get((li, wi))) for wi, w in enumerate(ws)]
                first = next((t[0] for _, t in wt if t), None)
                starts.append(fix(first) if first is not None else None)
                out.append({"text": l["text"], "refStartMs": l["startMs"],
                            "words": [{"w": w, "s": round((fix(t[0]) if i == 0 else t[0]) * 1000), "e": round(t[1] * 1000)}
                                      for i, (w, t) in enumerate((w, t) for w, t in wt if t)]})
            (track / f"align.{vname}.json").write_text(json.dumps(out, ensure_ascii=False, indent=1), encoding="utf-8")
            result[vname] = score(starts, ref) | {"align_s": round(dt, 1)}
            print(f"  {vname}: {result[vname]}", flush=True)
    return result


def main():
    tracks = [DATA / a for a in sys.argv[1:] if not a.startswith("--")] or sorted(p for p in DATA.iterdir() if (p / "vocals.wav").exists())
    results = []
    for t in tracks:
        print(t.name, flush=True)
        results.append(run(t))
    (LAB / "results.ctc.json").write_text(json.dumps(results, indent=1), encoding="utf-8")


if __name__ == "__main__":
    main()
