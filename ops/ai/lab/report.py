"""align.<variant>.json in every track dir -> results.md: line-start error against Spotify's lines, per method."""
import json
from pathlib import Path
import numpy as np

LAB = Path(__file__).parent
DATA = LAB / "data"
VARIANTS = [("mix.v1", "Qwen, whole song, mix"), ("vocals.v1", "Qwen, whole song, vocals"),
            ("vocals.text", "Qwen, 45 s windows, vocals"), ("vocals.anchored", "Qwen, line-anchored, vocals"),
            ("vocals.ctc", "wav2vec2 CTC, vocals (full precision)"), ("vocals.npu", "wav2vec2 CTC, vocals, all on NPU"), ("vocals.jit", "NPU streaming (just-in-time)"), ("vocals.jit.sil", "NPU streaming + silence snap"), ("vocals.final", "Final: NPU streaming, just-in-time, silence snap")]


def errors(path: Path):
    d = json.loads(path.read_text(encoding="utf-8"))
    return np.array([l["words"][0]["s"] - l["refStartMs"] for l in d if l["words"]], float), len(d)


def stats(e, n_lines):
    a = np.abs(e)
    lead = float(np.median(e))
    c = np.abs(e - lead)
    missing = n_lines - len(e)
    return (f"{np.median(a):.0f} ms | {np.percentile(a, 90):.0f} ms | {lead:+.0f} ms | {(a <= 300).mean() * 100:.0f}% | "
            f"{(a <= 500).mean() * 100:.0f}% | {(a <= 1000).mean() * 100:.0f}% | {(a > 2000).sum()}{f' (+{missing} unplaced)' if missing else ''} | "
            f"{np.median(c):.0f} ms | {(c <= 300).mean() * 100:.0f}%")


HEAD = ("| Median | p90 | Lead | <=300 ms | <=500 ms | <=1 s | >2 s off | Median, lead removed | <=300 ms, lead removed |",
        "|---|---|---|---|---|---|---|---|---|")


def main():
    tracks = sorted((p for p in DATA.iterdir() if (p / "meta.json").exists()),
                    key=lambda p: json.loads((p / "meta.json").read_text(encoding="utf-8"))["title"])
    out = ["## All tracks pooled", "", "| Method | Lines " + HEAD[0], "|---|---" + HEAD[1]]
    for key, label in VARIANTS:
        es, ns, cs, tr = [], 0, [], 0
        for t in tracks:
            f = t / f"align.{key}.json"
            if not f.exists(): continue
            e, n = errors(f)
            es.append(e); cs.append(e - np.median(e)); ns += n; tr += 1
        if not es: continue
        e = np.concatenate(es)
        out.append(f"| {label} ({tr} tracks) | {ns} | " + stats(e, ns) + " |")
    out += ["", "Lead removed: each track's median signed error is subtracted first (a constant shift a player can apply).", ""]
    for t in tracks:
        m = json.loads((t / "meta.json").read_text(encoding="utf-8"))
        out += [f"## {m['title']} - {m['artists']}", "", "| Method | Lines " + HEAD[0], "|---|---" + HEAD[1]]
        for key, label in VARIANTS:
            f = t / f"align.{key}.json"
            if f.exists():
                e, n = errors(f)
                out.append(f"| {label} | {n} | " + stats(e, n) + " |")
        out.append("")
    (LAB / "results.md").write_text("\n".join(out) + "\n", encoding="utf-8")
    print("\n".join(out))


if __name__ == "__main__":
    main()
