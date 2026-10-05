"""One aligner language for the AI lyrics pack: a Hugging Face wav2vec2 CTC model -> the five NPU graphs in fp16
(align-<lang>.<stage>.onnx) and its vocabulary (align-<lang>.vocab.json, the model's vocab.json byte for byte).

    export_pack.py <model dir under models/> <lang> <out dir>            export
    export_pack.py --verify <model dir> <lang> <out dir> <audio> [secs]   PyTorch fp32 vs the fp16 graphs (CPU and NPU)

The graphs are w2v_npu.export at 10 s (the app's Aligner.ChunkSeconds), then onnxconverter_common's
convert_float_to_float16(keep_io_types=True): fp32 in and out, fp16 inside. This reproduces the shipped align-es.*
byte for byte from wav2vec2-large-xlsr-53-spanish.

The app's Vocab (AiLyrics.Align.cs) takes the blank from "<pad>" or "[PAD]" and the word separator from "|"; the export
refuses a model whose pad token is neither, or whose lm_head width differs from the vocabulary."""
import json, shutil, sys, time
from pathlib import Path
import numpy as np

LAB = Path(__file__).parent
SECS = 10
SR = 16000


def check_vocab(model_dir: Path) -> dict:
    vocab = json.loads((model_dir / "vocab.json").read_text(encoding="utf-8"))
    cfg = json.loads((model_dir / "config.json").read_text(encoding="utf-8"))
    blank = vocab.get("<pad>", vocab.get("[PAD]"))
    assert blank is not None, "no <pad>/[PAD] token: the app's Vocab would fall back to blank 0"
    assert blank == cfg["pad_token_id"], f"pad token {blank} != config pad_token_id {cfg['pad_token_id']} (the CTC blank)"
    assert "|" in vocab, "no '|' word separator"
    assert max(vocab.values()) + 1 == cfg["vocab_size"] == len(vocab), "vocab.json does not cover the lm_head"
    return {"classes": cfg["vocab_size"], "blank": blank, "separator": vocab["|"], "upper": "E" in vocab}


def export(name: str, lang: str, out: Path):
    import onnx
    from onnxconverter_common import float16
    import w2v_npu
    model_dir = LAB / "models" / name
    info = check_vocab(model_dir)
    print(f"{name}: {info}", flush=True)
    base = LAB / "models" / f"{name}.{SECS}s"
    if not Path(f"{base}.layers_b.onnx").exists():
        t0 = time.perf_counter(); w2v_npu.export(name, SECS); print(f"  fp32 export {time.perf_counter() - t0:.0f} s", flush=True)
    out.mkdir(parents=True, exist_ok=True)
    for st in w2v_npu.STAGES:
        h = float16.convert_float_to_float16(onnx.load(f"{base}.{st}.onnx"), keep_io_types=True)
        dst = out / f"align-{lang}.{st}.onnx"
        dst.write_bytes(h.SerializeToString())
        print(f"  {dst.name}: {dst.stat().st_size} bytes", flush=True)
    shutil.copyfile(model_dir / "vocab.json", out / f"align-{lang}.vocab.json")


def load_audio(path: str, secs: float, offset: float):
    import librosa
    a, _ = librosa.load(path, sr=SR, mono=True, offset=offset, duration=secs)
    return a.astype(np.float32)


def verify(name: str, lang: str, out: Path, audio: str, offset: float = 30.0):
    """Ten seconds of real audio through the PyTorch model and through the fp16 graphs on the CPU and on the NPU:
    max |logprob| difference on frames the reference is confident about, greedy (argmax) agreement, and the
    forced-alignment token boundaries of the reference's own greedy transcript."""
    import torch, onnxruntime as ort
    from transformers import Wav2Vec2ForCTC
    import w2v_npu
    x = load_audio(audio, SECS, offset)
    x = np.pad(x, (0, SECS * SR - len(x)))
    x = (x - x.mean()) / (x.std() + 1e-7)
    m = Wav2Vec2ForCTC.from_pretrained(LAB / "models" / name).eval()
    with torch.no_grad(): ref = torch.log_softmax(m(torch.from_numpy(x)[None]).logits, -1)[0].numpy()
    info = check_vocab(LAB / "models" / name)
    res = {}
    for dev in ("cpu", "npu"):
        if dev == "cpu":
            sess = [ort.InferenceSession(str(out / f"align-{lang}.{st}.onnx"), providers=["CPUExecutionProvider"]) for st in w2v_npu.STAGES]
        else:
            # compiled in a side folder: the Python QNN plugin's EP context must not land next to the pack files
            side = out.parent / f"_verify-{lang}"
            side.mkdir(exist_ok=True)
            for st in w2v_npu.STAGES:
                if not (side / f"align-{lang}.{st}.onnx").exists(): shutil.copyfile(out / f"align-{lang}.{st}.onnx", side / f"align-{lang}.{st}.onnx")
            t0 = time.perf_counter()
            sess = [w2v_npu.npu_session(str(side / f"align-{lang}.{st}.onnx")) for st in w2v_npu.STAGES]
            res["npu_load_s"] = round(time.perf_counter() - t0, 1)
        y = x[None]
        t0 = time.perf_counter()
        for s in sess: y = s.run(None, {"x": y})[0]
        dt = time.perf_counter() - t0
        lp = y[0]
        conf = ref.max(-1) > np.log(0.5)
        d = np.abs(lp - ref)
        a_ref, a_lp = ref.argmax(-1), lp.argmax(-1)
        nonblank = a_ref != info["blank"]
        res[dev] = {"frames": int(len(lp)), "max_abs_conf": float(d[conf].max()), "mean_abs": float(d.mean()),
                    "argmax_agree_pct": float((a_ref == a_lp).mean() * 100),
                    "argmax_agree_nonblank_pct": float((a_ref[nonblank] == a_lp[nonblank]).mean() * 100) if nonblank.any() else None,
                    "fa_boundary_max_frames": fa_diff(ref, lp, info["blank"]), "run_s": round(dt, 3)}
    print(json.dumps({"model": name, "lang": lang, "audio": audio, "offset_s": offset, **res}, indent=1))


def fa_diff(ref, lp, blank):
    """Force-align the reference's greedy token sequence with both log-prob matrices; max |start frame| difference."""
    g = ref.argmax(-1)
    toks = [int(t) for i, t in enumerate(g) if t != blank and (i == 0 or t != g[i - 1])]
    if not toks: return None
    a, b = ctc_starts(ref, toks, blank), ctc_starts(lp, toks, blank)
    return int(np.abs(a - b).max())


def ctc_starts(lp, toks, blank):
    """CTC Viterbi forced alignment (blank, t0, blank, t1, ..., blank); the first frame of each token."""
    T, L = len(lp), len(toks)
    S = 2 * L + 1
    lab = np.array([blank if s % 2 == 0 else toks[s // 2] for s in range(S)])
    skip = np.zeros(S, bool)
    for k in range(1, L): skip[2 * k + 1] = toks[k] != toks[k - 1]
    dp = np.full(S, -np.inf); dp[0] = lp[0, lab[0]]; dp[1] = lp[0, lab[1]]
    bp = np.zeros((T, S), np.int8)
    for t in range(1, T):
        c0 = dp; c1 = np.r_[-np.inf, dp[:-1]]; c2 = np.where(skip, np.r_[-np.inf, -np.inf, dp[:-2]], -np.inf)
        st = np.stack([c0, c1, c2]); arg = st.argmax(0)
        dp = st[arg, np.arange(S)] + lp[t, lab]; bp[t] = arg
    s = S - 1 if dp[S - 1] >= dp[S - 2] else S - 2
    first = np.zeros(L, int)
    for t in range(T - 1, -1, -1):
        if s % 2 == 1: first[s // 2] = t
        s -= int(bp[t, s])
    return first


if __name__ == "__main__":
    a = sys.argv[1:]
    if a and a[0] == "--verify":
        verify(a[1], a[2], Path(a[3]), a[4], float(a[5]) if len(a) > 5 else 30.0)
    else:
        export(a[0], a[1], Path(a[2]))
