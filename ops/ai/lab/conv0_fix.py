"""Find an NPU-accurate form of wav2vec2's first conv layer (Conv1d 1->512, k=10, s=5, then LayerNorm + GELU)."""
import numpy as np, torch, onnxruntime as ort, soundfile as sf
from scipy.signal import resample_poly
from transformers import Wav2Vec2ForCTC
from w2v_npu import npu_session

m = Wav2Vec2ForCTC.from_pretrained("models/wav2vec2-large-960h-lv60-self").eval()
c0 = m.wav2vec2.feature_extractor.conv_layers[0]
print(type(c0).__name__, c0.conv, getattr(c0, "layer_norm", None))
W = c0.conv.weight.detach()            # [512, 1, 10]
B = c0.conv.bias

v, sr = sf.read("data/4CeeEOM32jQcH3eN9Q2dGj/vocals.wav", dtype="float32")
x = resample_poly(v.mean(1)[int(50 * sr):int(61 * sr)], 160, 441).astype(np.float32)[:160000]
x = ((x - x.mean()) / (x.std() + 1e-7))[None]
xt = torch.from_numpy(x)
with torch.no_grad(): ref = c0(xt[:, None]).numpy()                       # [1, 512, T]
T = ref.shape[-1]


def frames(x):                                  # [1, N] -> [1, T, 10], hop 5
    n = x.shape[1]
    idx = np.arange(T)[:, None] * 5 + np.arange(10)[None]
    return x[:, idx]


class ConvOnly(torch.nn.Module):
    def __init__(self): super().__init__(); self.c = c0.conv
    def forward(self, x): return self.c(x[:, None])

class Conv2d(torch.nn.Module):
    def __init__(self):
        super().__init__()
        self.c = torch.nn.Conv2d(1, 512, (1, 10), (1, 5), bias=B is not None)
        self.c.weight.data = W[:, :, None, :].clone()
        if B is not None: self.c.bias.data = B.detach().clone()
        self.c0 = c0
    def forward(self, x):
        h = self.c(x[:, None, None, :])[:, :, 0]                          # [1, 512, T]
        h = self.c0.layer_norm(h.transpose(-2, -1)).transpose(-2, -1)
        return self.c0.activation(h)

class Framed(torch.nn.Module):
    """Input already framed on the host: [1, T, 10] @ W^T -> LayerNorm over channels -> GELU, output [1, 512, T]."""
    def __init__(self):
        super().__init__()
        self.lin = torch.nn.Linear(10, 512, bias=B is not None)
        self.lin.weight.data = W[:, 0, :].clone()
        if B is not None: self.lin.bias.data = B.detach().clone()
        self.c0 = c0
    def forward(self, f):
        h = self.c0.layer_norm(self.lin(f))
        return self.c0.activation(h).transpose(1, 2)


def rel(a, b): return float(np.linalg.norm(a - b) / (np.linalg.norm(b) + 1e-9))

with torch.no_grad(): conv_ref = c0.conv(xt[:, None]).numpy()
tests = [("conv_only", ConvOnly(), x, conv_ref), ("conv2d+ln+gelu", Conv2d(), x, ref), ("framed_matmul+ln+gelu", Framed(), frames(x), ref)]
for name, mod, inp, want in tests:
    path = f"models/_c0_{name}.onnx"
    torch.onnx.export(mod, (torch.from_numpy(inp),), path, input_names=["x"], output_names=["y"], opset_version=17, dynamo=False)
    got = npu_session(path).run(None, {"x": inp})[0]
    print(f"{name:24} NPU rel.err {rel(got, want):.4f}", flush=True)
