"""wav2vec2 CTC on the NPU as two QNN graphs: the first feature conv alone, then everything after it (one graph with
both fails to finalize on the HTP with QNN_COMMON_ERROR_MEM_ALLOC). Export, load and run with fixed-size chunks."""
import sys, time
from pathlib import Path
import numpy as np
import onnxruntime as ort
import onnxruntime_qnn as qnn

LAB = Path(__file__).parent
SR = 16000
STAGES = ("conv0", "conv1", "convs", "layers_a", "layers_b")
if not any(d.ep_name == qnn.EP_NAME for d in ort.get_ep_devices()):
    ort.register_execution_provider_library(qnn.EP_NAME, qnn.get_library_path())


def stages(m):
    """The model as a list of (name, module) stages; each one becomes its own QNN graph."""
    import torch
    w = m.wav2vec2
    enc = w.encoder
    stable = type(enc).__name__.endswith("StableLayerNorm")
    n = len(enc.layers)

    class Conv0(torch.nn.Module):
        """The first conv (1 -> C, kernel 10, stride 5) as framing + matmul: the HTP computes a single-input-channel
        strided Conv wrongly (~96% relative error), while the same math as a MatMul is exact to fp16. Framing is a
        reshape: with hop 5 and width 10, frame t is samples [5t, 5t+10) = rows t and t+1 of the [N/5, 5] view."""
        def __init__(self):
            super().__init__()
            c0 = w.feature_extractor.conv_layers[0]
            self.c0 = c0
            self.lin = torch.nn.Linear(10, c0.conv.out_channels, bias=c0.conv.bias is not None)
            self.lin.weight.data = c0.conv.weight.detach()[:, 0, :].clone()
            if c0.conv.bias is not None: self.lin.bias.data = c0.conv.bias.detach().clone()
        def forward(self, x):
            r = x.reshape(1, -1, 5)
            f = torch.cat([r[:, :-1], r[:, 1:]], dim=2)                          # [1, N/5 - 1, 10]
            h = self.lin(f)
            if hasattr(self.c0, "layer_norm"):
                if isinstance(self.c0.layer_norm, torch.nn.GroupNorm):            # base models: GroupNorm over time
                    h = self.c0.layer_norm(h.transpose(1, 2)).transpose(1, 2)
                else:
                    h = self.c0.layer_norm(h)
            return self.c0.activation(h).transpose(1, 2)                          # [1, C, T]

    class Conv(torch.nn.Module):
        def __init__(self, i): super().__init__(); self.c = w.feature_extractor.conv_layers[i]
        def forward(self, f): return self.c(f)

    class Convs(torch.nn.Module):
        def __init__(self): super().__init__(); self.convs = w.feature_extractor.conv_layers[2:]; self.proj = w.feature_projection
        def forward(self, f):
            for c in self.convs: f = c(f)
            return self.proj(f.transpose(1, 2))[0]

    class Layers(torch.nn.Module):
        def __init__(self, lo, hi, first, last):
            super().__init__(); self.layers = enc.layers[lo:hi]; self.first = first; self.last = last
            self.pos = enc.pos_conv_embed; self.norm = enc.layer_norm; self.lm = m.lm_head
        def forward(self, h):
            if self.first:
                h = h + self.pos(h)
                if not stable: h = self.norm(h)
            for layer in self.layers: h = layer(h)[0]
            if self.last:
                if stable: h = self.norm(h)
                return torch.log_softmax(self.lm(h), dim=-1)
            return h

    half = n // 2
    return [("conv0", Conv0()), ("conv1", Conv(1)), ("convs", Convs()), ("layers_a", Layers(0, half, True, False)), ("layers_b", Layers(half, n, False, True))]


def export(name: str, secs: int):
    import torch
    from transformers import Wav2Vec2ForCTC
    m = Wav2Vec2ForCTC.from_pretrained(LAB / "models" / name).eval()
    x = torch.zeros(1, SR * secs)
    base = LAB / "models" / f"{name}.{secs}s"
    for sname, mod in stages(m):
        torch.onnx.export(mod, (x,), f"{base}.{sname}.onnx", input_names=["x"], output_names=["y"], opset_version=17, dynamo=False)
        with torch.no_grad(): x = mod(x)
    return base


def npu_session(path: str, free_dims: dict | None = None):
    """An ONNX Runtime session on the Hexagon NPU. The compiled HTP graph is cached next to the model (EP context),
    so only the first load pays the finalize cost."""
    devs = [d for d in ort.get_ep_devices() if d.ep_name == qnn.EP_NAME and d.device.type.name == "NPU"]
    so = ort.SessionOptions()
    for k, v in (free_dims or {}).items(): so.add_free_dimension_override_by_name(k, v)
    so.add_provider_for_devices(devs, {"htp_performance_mode": "burst"})
    ctx = Path(f"{path[:-5]}.ctx.onnx")
    if not ctx.exists():
        so.add_session_config_entry("ep.context_enable", "1")
        so.add_session_config_entry("ep.context_file_path", str(ctx))
        return ort.InferenceSession(path, so)
    return ort.InferenceSession(str(ctx), so)


class NpuCtc:
    def __init__(self, name: str, secs: int):
        base = LAB / "models" / f"{name}.{secs}s"
        if not Path(f"{base}.layers_b.onnx").exists(): export(name, secs)
        self.secs = secs
        t0 = time.perf_counter()
        self.stages = [npu_session(f"{base}.{n}.onnx") for n in STAGES]
        self.load_s = time.perf_counter() - t0

    def logprobs(self, audio: np.ndarray) -> np.ndarray:
        """Per-frame log-probabilities over the whole song: fixed chunks with 1 s of context on each side, trimmed."""
        n = SR * self.secs
        ctx = SR                                   # 1 s overlap each side
        step = n - 2 * ctx
        fps = 50                                   # 20 ms frames
        out = []
        pos = 0
        while pos < len(audio):
            a0 = pos - ctx
            chunk = np.zeros(n, np.float32)
            src = audio[max(0, a0):a0 + n]
            chunk[max(0, -a0):max(0, -a0) + len(src)] = src
            chunk = (chunk - chunk.mean()) / (chunk.std() + 1e-7)       # the feature extractor's normalization
            y = chunk[None]
            for st in self.stages: y = st.run(None, {"x": y})[0]
            lp = y[0]
            keep = lp[ctx * fps // SR: ctx * fps // SR + step * fps // SR]
            out.append(keep)
            pos += step
        total = int(len(audio) / SR * fps)
        return np.concatenate(out)[:total]


if __name__ == "__main__":
    name = sys.argv[1] if len(sys.argv) > 1 else "wav2vec2-large-960h-lv60-self"
    secs = int(sys.argv[2]) if len(sys.argv) > 2 else 10
    t0 = time.perf_counter()
    m = NpuCtc(name, secs)
    print(f"{name} {secs}s: export+load {time.perf_counter() - t0:.1f} s (load {m.load_s:.1f} s)", flush=True)
    x = (np.random.default_rng(0).standard_normal(SR * 60) * 0.1).astype(np.float32)
    m.logprobs(x[:SR * 12])                        # warm-up
    t0 = time.perf_counter(); lp = m.logprobs(x); dt = time.perf_counter() - t0
    print(f"60 s of audio -> {lp.shape} in {dt:.2f} s ({60 / dt:.0f}x real time)")
