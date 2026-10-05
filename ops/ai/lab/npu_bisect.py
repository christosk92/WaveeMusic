"""Exports pieces of wav2vec2 and tries each on the NPU, to find the part QNN cannot finalize."""
import sys, torch
import numpy as np
from transformers import Wav2Vec2ForCTC
import npu_bench as b

name = sys.argv[1] if len(sys.argv) > 1 else "wav2vec2-base-960h"
secs = int(sys.argv[2]) if len(sys.argv) > 2 else 5
m = Wav2Vec2ForCTC.from_pretrained(f"models/{name}").eval()
w = m.wav2vec2
N = 16000 * secs
with torch.no_grad():
    feats = w.feature_extractor(torch.zeros(1, N))                         # [1, C, T]
    hid, _ = w.feature_projection(feats.transpose(1, 2))                    # [1, T, H]
T, H = hid.shape[1], hid.shape[2]


class Wrap(torch.nn.Module):
    def __init__(self, mod, fn):
        super().__init__(); self.mod = mod; self.fn = fn
    def forward(self, x): return self.fn(self.mod, x)


def run_layers(layers, h):
    for layer in layers: h = layer(h)[0]
    return h

def run_convs(convs, h):
    for c in convs: h = c(h)
    return h

parts = {
    "fe_conv0": (Wrap(w.feature_extractor.conv_layers[0], lambda m, x: m(x[:, None])), torch.zeros(1, N)),
    "feature_extractor": (Wrap(w.feature_extractor, lambda m, x: m(x)), torch.zeros(1, N)),
    "pos_conv": (Wrap(w.encoder.pos_conv_embed, lambda m, x: m(x)), torch.zeros(1, T, H)),
    "one_layer": (Wrap(w.encoder.layers[0], lambda m, x: m(x)[0]), torch.zeros(1, T, H)),
    "all_layers": (Wrap(w.encoder.layers, run_layers), torch.zeros(1, T, H)),
}
with torch.no_grad():
    x0 = torch.zeros(1, N)[:, None]
    acts = [x0]
    for c in w.feature_extractor.conv_layers: acts.append(c(acts[-1]))
for i in range(1, len(w.feature_extractor.conv_layers)):
    parts[f"fe_conv{i}"] = (Wrap(w.feature_extractor.conv_layers[i], lambda m, x: m(x)), acts[i])
parts["fe_conv1_6"] = (Wrap(w.feature_extractor.conv_layers[1:], run_convs), acts[1])
only = sys.argv[3:] or list(parts)
for k in only:
    mod, x = parts[k]
    path = f"models/_bisect_{k}.onnx"
    torch.onnx.export(mod, (x,), path, input_names=["x"], output_names=["y"], opset_version=17, dynamo=False)
    print(f"{k} {tuple(x.shape)}:", flush=True)
    b.bench(path, {"x": x.numpy()}, {}, runs=3)
