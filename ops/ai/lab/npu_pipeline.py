"""The whole pipeline with every model on the Hexagon NPU: MDX-Net vocal separation, then wav2vec2 CTC (5 QNN graphs),
then the Viterbi forced alignment of Spotify's lyric text. Writes align.vocals.npu.json and times each stage."""
import json, re, subprocess, sys, time
from pathlib import Path

import numpy as np
import soundfile as sf
import torch

from align import DATA, LAB, LANG, SR, score
from ctc import FRAME_S, word_tokens, viterbi
from w2v_npu import NpuCtc, npu_session
from transformers import Wav2Vec2Processor

CTC_MODEL = {"English": "wav2vec2-large-960h-lv60-self", "Spanish": "wav2vec2-large-xlsr-53-spanish"}

# Kim_Vocal_2 (MDX-Net): the UVR model_data entry for this file.
N_FFT, HOP, DIM_F, DIM_T, COMPENSATE = 7680, 1024, 3072, 256, 1.009
TRIM = N_FFT // 2
CHUNK = HOP * (DIM_T - 1)
GEN = CHUNK - 2 * TRIM
WINDOW = torch.hann_window(N_FFT, periodic=True)


class NpuSeparator:
    def __init__(self):
        t0 = time.perf_counter()
        self.s = npu_session(str(LAB / "models" / "uvr" / "Kim_Vocal_2.onnx"), {"batch_size": 1})
        self.load_s = time.perf_counter() - t0

    def vocals(self, mix: np.ndarray) -> np.ndarray:
        """mix: [2, n] float32 at 44.1 kHz -> vocals [2, n]."""
        n = mix.shape[1]
        pad = GEN - n % GEN
        mp = np.concatenate((np.zeros((2, TRIM), np.float32), mix, np.zeros((2, pad), np.float32), np.zeros((2, TRIM), np.float32)), 1)
        out = []
        for i in range(0, n + pad, GEN):
            w = torch.from_numpy(mp[:, i:i + CHUNK].copy())
            spec = torch.stft(w, N_FFT, HOP, window=WINDOW, center=True, return_complex=False)   # [2, bins, T, 2]
            spec = spec.permute(0, 3, 1, 2).reshape(1, 4, -1, DIM_T)[:, :, :DIM_F]
            spec[:, :, :3] = 0
            pred = torch.from_numpy(self.s.run(None, {"input": spec.numpy()})[0])
            pred = torch.cat([pred, torch.zeros(1, 4, N_FFT // 2 + 1 - DIM_F, DIM_T)], 2)
            pred = pred.reshape(2, 2, -1, DIM_T).permute(0, 2, 3, 1)
            wav = torch.istft(torch.complex(pred[..., 0], pred[..., 1]), N_FFT, HOP, window=WINDOW, center=True)
            out.append(wav[:, TRIM:-TRIM].numpy())
        return np.concatenate(out, 1)[:, :n] * COMPENSATE


def read_mix(ogg: Path) -> np.ndarray:
    raw = subprocess.run(["ffmpeg", "-v", "error", "-i", str(ogg), "-f", "f32le", "-ac", "2", "-ar", "44100", "-"],
                         check=True, capture_output=True).stdout
    return np.frombuffer(raw, np.float32).reshape(-1, 2).T.copy()


def to16k_mono(x44: np.ndarray) -> np.ndarray:
    raw = subprocess.run(["ffmpeg", "-v", "error", "-f", "f32le", "-ac", "2", "-ar", "44100", "-i", "-", "-f", "f32le", "-ac", "1", "-ar", str(SR), "-"],
                         input=np.ascontiguousarray(x44.T).tobytes(), check=True, capture_output=True).stdout
    return np.frombuffer(raw, np.float32).copy()


_ctc = {}
def ctc_for(lang):
    if lang not in _ctc:
        name = CTC_MODEL[lang]
        _ctc[lang] = (NpuCtc(name, 10), Wav2Vec2Processor.from_pretrained(LAB / "models" / name).tokenizer.get_vocab())
    return _ctc[lang]


def run(track: Path, sep: NpuSeparator):
    meta = json.loads((track / "meta.json").read_text(encoding="utf-8"))
    ref_doc = json.loads((track / "lyrics.spotify.json").read_text(encoding="utf-8"))
    lines = [l for l in ref_doc["lines"] if l["text"].strip() and l["text"].strip() != "♪"]
    lang = LANG.get(meta["trackId"], "English")
    ref = [l["startMs"] / 1000 for l in lines]
    per_line = [re.findall(r"[\w'’]+", l["text"]) for l in lines]
    ctc, vocab = ctc_for(lang)

    t = {}
    t0 = time.perf_counter(); mix = read_mix(track / "audio.ogg"); t["decode"] = time.perf_counter() - t0
    t0 = time.perf_counter(); voc = sep.vocals(mix); t["separate (NPU)"] = time.perf_counter() - t0
    t0 = time.perf_counter(); v16 = to16k_mono(voc); t["resample"] = time.perf_counter() - t0
    t0 = time.perf_counter(); lp = ctc.logprobs(v16); t["wav2vec2 (NPU)"] = time.perf_counter() - t0

    t0 = time.perf_counter()
    tokens, owner = [], []
    for li, words in enumerate(per_line):
        for wi, w in enumerate(words):
            ids = word_tokens(w, vocab)
            if not ids: continue
            if tokens: tokens.append(vocab["|"]); owner.append(None)
            tokens += ids; owner += [(li, wi)] * len(ids)
    spans = viterbi(lp, tokens)
    times = {}
    for (f0, f1), o in zip(spans, owner):
        if o is None: continue
        s, e = f0 * FRAME_S, (f1 + 1) * FRAME_S
        times[o] = (min(times[o][0], s), max(times[o][1], e)) if o in times else (s, e)
    t["viterbi"] = time.perf_counter() - t0

    starts, out = [], []
    for li, (l, ws) in enumerate(zip(lines, per_line)):
        wt = [(w, times.get((li, wi))) for wi, w in enumerate(ws)]
        first = next((x[0] for _, x in wt if x), None)
        starts.append(first)
        out.append({"text": l["text"], "refStartMs": l["startMs"],
                    "words": [{"w": w, "s": round(x[0] * 1000), "e": round(x[1] * 1000)} for w, x in wt if x]})
    (track / "align.vocals.npu.json").write_text(json.dumps(out, ensure_ascii=False, indent=1), encoding="utf-8")
    sf.write(str(track / "vocals.npu.wav"), voc.T, 44100)
    res = {"track": f'{meta["title"]} - {meta["artists"]}', "durationS": meta["durationMs"] / 1000,
           "timing_s": {k: round(v, 2) for k, v in t.items()}, "total_s": round(sum(t.values()), 1)} | score(starts, ref)
    print(json.dumps(res), flush=True)
    return res


def main():
    tracks = [DATA / a for a in sys.argv[1:]] or sorted(p for p in DATA.iterdir() if (p / "audio.ogg").exists())
    t0 = time.perf_counter(); sep = NpuSeparator()
    print(f"separator load {sep.load_s:.1f} s", flush=True)
    results = [run(tr, sep) for tr in tracks]
    (LAB / "results.npu.json").write_text(json.dumps(results, indent=1), encoding="utf-8")


if __name__ == "__main__":
    main()
