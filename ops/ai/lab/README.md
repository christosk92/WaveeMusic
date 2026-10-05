# AI lyrics lab

The Python prototype behind the on-device AI lyrics feature (`src/apps/Wavee/AiLyrics/`). It is where the method was
chosen and measured; the C# engine is a port of `stream.py`, `aligner.py`, `refine.py` and `to_lrc.py`.
Design: `docs/plans/wavee/ai-lyrics-sync-implementation.md` and `docs/plans/wavee/ai-lyrics-system-design.html`.

Only the scripts and the measured results live here. Song audio, lyrics, models, the virtual environment and any
profile copy stay out of the repository.

## Setup (Snapdragon / Windows on ARM)

```powershell
pip install --user uv
uv python install cpython-3.12-windows-aarch64-none
uv venv --python cpython-3.12-windows-aarch64-none .venv
uv pip install --python .venv\Scripts\python.exe torch --index-url https://download.pytorch.org/whl/cpu
uv pip install --python .venv\Scripts\python.exe --no-deps qwen-asr
uv pip install --python .venv\Scripts\python.exe "transformers==4.57.6" "accelerate==1.12.0" soynlp librosa soundfile numpy scipy huggingface_hub onnx onnxconverter-common onnxscript audio-separator onnxruntime-qnn
```

`onnxruntime-qnn` is the Qualcomm plugin execution provider; the scripts register it themselves.

## Data

`data/<trackId>/` holds `meta.json`, `lyrics.<source>.json` and `audio.ogg`, dumped by
`src/apps/Wavee.LyricsLab` (`--headless --profile <scratch copy> --out data --tracks ...`). Never point it at the live
profile: copy `store.json` (and the PlayPlay runtime store) into a scratch folder first.

## Scripts, in the order they were written

| Script | What it does |
|---|---|
| `align.py` | First attempt: vocal separation (UVR MDX-Net) + Qwen3-ForcedAligner over the whole song. Fails after ~2 minutes (timestamp pile-up). |
| `ctc.py` | wav2vec2 CTC forced alignment over the whole song on separated vocals: the method that works. |
| `npu_bench.py`, `npu_bisect.py` | ONNX Runtime + QNN on the Hexagon NPU: timings, and the bisection that found which wav2vec2 parts fail on the NPU. |
| `conv0_fix.py` | Shows the HTP computes the first single-channel strided Conv wrong (~96% error) and that framing + MatMul is exact. |
| `w2v_npu.py` | Exports wav2vec2 as the five NPU graphs (conv0 as framing + MatMul, conv1, convs, layers_a, layers_b). |
| `export_pack.py` | One pack language: `w2v_npu` export at 10 s + fp16 (`keep_io_types`) -> `align-<lang>.<stage>.onnx` and `align-<lang>.vocab.json`; reproduces `align-es.*` byte for byte. `--verify` compares PyTorch fp32 with the fp16 graphs on the CPU and the NPU. |
| `npu_pipeline.py` | Separator and aligner both on the NPU, whole song. |
| `aligner.py` | The alignment core: graph with optional parenthesised words, online Viterbi, line-time prior, commits. |
| `stream.py` | The streaming engine: block-by-block separation and alignment, just-in-time commits, silence snap. |
| `refine.py`, `onsets.py` | Word-start refinement against the vocal level, and the onset check. |
| `eval_words.py`, `report.py` | Word-level scoring against word-synced providers; line-level report (`results.md`). |
| `to_lrc.py` | Exports enhanced LRC; `Wavee.LyricsLab --check-lrc` parses it with Wavee's own parser. |

## Results

`results.md` (line starts against Spotify's line timing, per method and song) and `words.md` (word starts against
NetEase, Kugou, QQ and AMLL, offset-free, plus the human-vs-human noise floor).
