"""Streaming lyrics alignment, everything on the NPU: separation and wav2vec2 run block by block just ahead of the
playhead, and an online Viterbi over the whole lyric text commits each word once the audio processed so far is LAG
seconds past it. Nothing needs the whole song; nothing needs Spotify's line times.

Simulates playback starting the moment processing starts and records, for every line, when its word timings were
committed (wall clock) against when it is sung. Writes align.vocals.stream.json and stream.<id>.json."""
import json, re, sys, time
from pathlib import Path

import numpy as np
import torch
from scipy.signal import resample_poly

from align import DATA, LAB, LANG, SR, score
from aligner import FRAME_S, OnlineViterbi, build_graph, line_words
from npu_pipeline import CHUNK, GEN, TRIM, N_FFT, HOP, DIM_F, DIM_T, COMPENSATE, WINDOW, NpuSeparator, read_mix, ctc_for

SR44 = 44100
NEG = -1e30


class Stream:
    def __init__(self, sep: NpuSeparator, lang: str, lag_s=4.0, tag="stream", use_prior=True, shift_s=0.0):
        self.tag = tag; self.use_prior = use_prior; self.shift_s = shift_s; self.vocals_file = None
        self.lookahead_s = 1.5
        self.jit = False
        self.snap = True
        self.sep = sep
        self.ctc, self.vocab = ctc_for(lang)
        self.lag_s = lag_s

    def run(self, track: Path):
        meta = json.loads((track / "meta.json").read_text(encoding="utf-8"))
        ref_doc = json.loads((track / "lyrics.spotify.json").read_text(encoding="utf-8"))
        lines = [l for l in ref_doc["lines"] if l["text"].strip() and l["text"].strip() != "♪"]
        lw = [line_words(l["text"]) for l in lines]
        per_line = [[w for w, _ in ws] for ws in lw]
        tokens, owner, skips = build_graph(lw, self.vocab)

        t_start = time.perf_counter()                 # playback starts now
        mix = read_mix(track / "audio.ogg")           # in the app: the decoder's output, already buffered ahead
        n = mix.shape[1]
        windows = None
        if self.use_prior:
            starts_s = [l["startMs"] / 1000 + self.shift_s for l in lines] + [n / SR44]
            windows = [(starts_s[o[0]] - 2.0, starts_s[o[0] + 1] + 2.0) if o else None for o in owner]
            for k in range(len(windows)):                       # separators take the window of the word before
                if windows[k] is None: windows[k] = windows[k - 1]
        vit = OnlineViterbi(tokens, skips, windows=windows)
        word_time, word_ready = {}, {}
        voc_buf = np.zeros((2, n + 2 * GEN), np.float32)   # separated vocals (44.1 kHz), filled block by block
        voc_len = 0
        sep_pos = 0                                   # mix samples separated
        mp = np.concatenate((np.zeros((2, TRIM), np.float32), mix, np.zeros((2, GEN + TRIM), np.float32)), 1)
        ctc_n = SR * self.ctc.secs; ctx = SR; step = ctc_n - 2 * ctx; fps = 50
        ctc_pos = 0                                   # 16 kHz samples of emissions produced
        total16 = int(n / SR44 * SR)
        stages = {"separate": 0.0, "wav2vec2": 0.0, "viterbi": 0.0}
        first_ready = None

        def separate_block():
            nonlocal voc_len, sep_pos
            t0 = time.perf_counter()
            w = torch.from_numpy(mp[:, sep_pos:sep_pos + CHUNK])
            spec = torch.stft(w, N_FFT, HOP, window=WINDOW, center=True, return_complex=True)      # [2, bins, T]
            spec = torch.view_as_real(spec).permute(0, 3, 1, 2).reshape(1, 4, -1, DIM_T)[:, :, :DIM_F].contiguous()
            spec[:, :, :3] = 0
            pred = torch.from_numpy(self.sep.s.run(None, {"input": spec.numpy()})[0])
            pred = torch.nn.functional.pad(pred, (0, 0, 0, N_FFT // 2 + 1 - DIM_F))
            pred = pred.reshape(2, 2, -1, DIM_T).permute(0, 2, 3, 1).contiguous()
            wav = torch.istft(torch.view_as_complex(pred), N_FFT, HOP, window=WINDOW, center=True)
            blk = wav[:, TRIM:-TRIM].numpy() * COMPENSATE
            voc_buf[:, voc_len:voc_len + blk.shape[1]] = blk; voc_len += blk.shape[1]
            sep_pos += GEN
            stages["separate"] += time.perf_counter() - t0

        while ctc_pos < total16:
            # vocals needed for this wav2vec2 chunk: up to ctc_pos + step + ctx (16 kHz) -> 44.1 kHz + resample margin
            need44 = min(n, int((ctc_pos + step + ctx) / SR * SR44) + 2048)
            if self.vocals_file and voc_len == 0:
                import soundfile as sf
                v, vsr = sf.read(str(track / self.vocals_file), dtype="float32", always_2d=True)
                m_ = min(n, v.shape[0]); voc_buf[:, :m_] = v.T[:2, :m_]; voc_len = m_; sep_pos = n
            while voc_len < need44 and sep_pos < n: separate_block()
            t0 = time.perf_counter()
            a0 = ctc_pos - ctx
            lo44 = max(0, int(a0 / SR * SR44) - 2048); hi44 = min(voc_len, int((a0 + ctc_n) / SR * SR44) + 2048)
            mono = voc_buf[:, lo44:hi44].mean(0)
            r = resample_poly(mono, 160, 441).astype(np.float32)
            off16 = int(round(lo44 / SR44 * SR))
            chunk = np.zeros(ctc_n, np.float32)
            s0 = a0 - off16
            seg = r[max(0, s0):max(0, s0) + ctc_n - max(0, -s0)]
            chunk[max(0, -s0):max(0, -s0) + len(seg)] = seg
            chunk = (chunk - chunk.mean()) / (chunk.std() + 1e-7)
            y = chunk[None]
            for st in self.ctc.stages: y = st.run(None, {"x": y})[0]
            lp = y[0][ctx * fps // SR: ctx * fps // SR + step * fps // SR]
            lp = lp[:max(0, int(total16 / SR * fps) - vit.t)]
            stages["wav2vec2"] += time.perf_counter() - t0
            ctc_pos += step

            t0 = time.perf_counter()
            vit.feed(lp)
            final = ctc_pos >= total16
            play_s = time.perf_counter() - t_start                  # the simulated playhead
            deadline = int((play_s + self.lookahead_s) * fps)         # words this close to the playhead commit now
            if self.jit:
                new = vit.commit(10**9, final=final, deadline_frame=deadline)       # commit only what the playhead needs
                if "--debug" in sys.argv:
                    print(f"  dbg play {play_s:5.1f}s frontier {vit.t / fps:6.1f}s deadline {deadline / fps:5.1f}s committed {vit.committed}/{len(tokens)} new {len(new)}", flush=True)
            else:
                new = (vit.commit(10**9 if self.lag_s == float("inf") else int(self.lag_s * fps), final=final, deadline_frame=None if self.lag_s == float("inf") else deadline) if (final or self.lag_s != float("inf")) else [])
            now = time.perf_counter() - t_start
            for k in new:
                o = owner[k]
                if o is None or vit.spans[k] is None: continue
                f0, f1 = vit.spans[k]
                s, e = f0 * FRAME_S, (f1 + 1) * FRAME_S
                word_time[o] = (min(word_time[o][0], s), max(word_time[o][1], e)) if o in word_time else (s, e)
                word_ready.setdefault(o, now)
            stages["viterbi"] += time.perf_counter() - t0
            if first_ready is None and word_ready: first_ready = now

        # word starts: snap back to the rise out of silence (CTC marks a letter after its sound has started)
        from refine import refine_line, HOP as EHOP
        mono44 = voc_buf[:, :voc_len].mean(0)
        h = int(SR44 * EHOP); ne = len(mono44) // h
        env = 20 * np.log10(np.sqrt((mono44[:ne * h].reshape(ne, h) ** 2).mean(1) + 1e-12))
        env = np.convolve(env, np.ones(3) / 3, mode="same")
        quiet = np.percentile(env, 10) + 15
        if self.snap:
            last_end = None
            for li, ws in enumerate(per_line):
                keys = [(li, wi) for wi in range(len(ws)) if (li, wi) in word_time]
                if not keys: continue
                fixed = refine_line([{"s": word_time[k][0] * 1000, "e": word_time[k][1] * 1000} for k in keys], env, quiet,
                                    0.40, 0.0, prev_end=last_end)
                for k, f in zip(keys, fixed): word_time[k] = (f["s"] / 1000, word_time[k][1])
                last_end = word_time[keys[-1]][1]

        # results
        out, starts, timeline = [], [], []
        for li, (l, ws) in enumerate(zip(lines, per_line)):
            wt = [(w, word_time.get((li, wi)), word_ready.get((li, wi))) for wi, w in enumerate(ws)]
            got = [x for x in wt if x[1]]
            first = got[0][1][0] if got else None
            starts.append(first)
            if got:
                ready = max(x[2] for x in got)                     # the whole line's words are known
                timeline.append({"line": li, "sungAt": first, "readyAt": ready, "marginS": first - ready})
            out.append({"text": l["text"], "refStartMs": l["startMs"],
                        "words": [{"w": w, "s": round(t[0] * 1000), "e": round(t[1] * 1000)} for w, t, _ in got]})
        (track / f"align.vocals.{self.tag}.json").write_text(json.dumps(out, ensure_ascii=False, indent=1), encoding="utf-8")
        wall = time.perf_counter() - t_start
        margins = np.array([x["marginS"] for x in timeline])
        wm = np.array([word_time[o][0] - word_ready[o] for o in word_time])        # per word: sung - committed
        res = {"track": f'{meta["title"]} - {meta["artists"]}', "durationS": round(n / SR44, 1), "wallS": round(wall, 1),
               "speed": round(n / SR44 / wall, 1), "firstWordsReadyS": round(first_ready or -1, 2),
               "linesReadyBeforeSung": f"{(margins > 0).sum()}/{len(margins)}",
               "wordsReadyBeforeSung": f"{(wm > 0).sum()}/{len(wm)}",
               "minMarginS": round(float(margins.min()), 2) if len(margins) else None,
               "stages_s": {k: round(v, 1) for k, v in stages.items()}} | score(starts, [l["startMs"] / 1000 for l in lines])
        (track / f"{self.tag}.json").write_text(json.dumps(res | {"timeline": timeline}, indent=1), encoding="utf-8")
        print(json.dumps(res), flush=True)
        return res


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    lag = float(next((a.split("=")[1] for a in sys.argv[1:] if a.startswith("--lag=")), 4.0))
    tag = next((a.split("=")[1] for a in sys.argv[1:] if a.startswith("--tag=")), "stream")
    use_prior = "--no-prior" not in sys.argv
    shift = float(next((a.split("=")[1] for a in sys.argv[1:] if a.startswith("--shift=")), 0.0))
    tracks = [DATA / a for a in args] or sorted(p for p in DATA.iterdir() if (p / "audio.ogg").exists())
    sep = NpuSeparator()
    streams = {}
    for tr in tracks:
        lang = LANG.get(tr.name, "English")
        if lang not in streams:
            streams[lang] = Stream(sep, lang, lag, tag, use_prior, shift)
            streams[lang].vocals_file = next((a.split("=")[1] for a in sys.argv[1:] if a.startswith("--vocals=")), None)
            streams[lang].jit = "--jit" in sys.argv
            streams[lang].snap = "--no-snap" not in sys.argv
            streams[lang].lookahead_s = float(next((a.split("=")[1] for a in sys.argv[1:] if a.startswith("--lookahead=")), 1.5))
        streams[lang].run(tr)


if __name__ == "__main__":
    main()
