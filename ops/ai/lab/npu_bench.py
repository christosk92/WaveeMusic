"""Times one inference of an ONNX model on CPU, the Hexagon NPU and the Adreno GPU (ONNX Runtime + QNN plugin EP),
and checks the NPU/GPU output against the CPU output."""
import sys, time
import numpy as np
import onnxruntime as ort
import onnxruntime_qnn as qnn

if not any(d.ep_name == qnn.EP_NAME for d in ort.get_ep_devices()):
    ort.register_execution_provider_library(qnn.EP_NAME, qnn.get_library_path())


def session(path, target, free_dims, opts_extra=None):
    so = ort.SessionOptions()
    for k, v in free_dims.items(): so.add_free_dimension_override_by_name(k, v)
    if target == "cpu":
        return ort.InferenceSession(path, so, providers=["CPUExecutionProvider"])
    devs = [d for d in ort.get_ep_devices() if d.ep_name == qnn.EP_NAME and d.device.type.name == target.upper()]
    opts = {"htp_performance_mode": "burst"} if target == "npu" else {}
    opts.update(opts_extra or {})
    so.add_provider_for_devices(devs, opts)
    so.add_session_config_entry("session.disable_cpu_ep_fallback", "0")
    return ort.InferenceSession(path, so)


def bench(path, feeds, free_dims, targets=("npu",), runs=5, opts_extra=None):
    ref = None
    for t in targets:
        try:
            t0 = time.perf_counter()
            s = session(path, t, free_dims, opts_extra)
            load = time.perf_counter() - t0
            out = s.run(None, feeds)[0]                 # warm-up (graph finalize on the accelerator)
            ts = []
            for _ in range(runs):
                t0 = time.perf_counter(); out = s.run(None, feeds)[0]; ts.append(time.perf_counter() - t0)
            err = ""
            if ref is None: ref = out
            else:
                rel = np.linalg.norm(out - ref) / (np.linalg.norm(ref) + 1e-12)
                err = f" rel.err vs cpu {rel:.4f}"
            print(f"  {t}: {np.median(ts) * 1000:.0f} ms/run (load {load:.1f} s){err}", flush=True)
        except Exception as ex:
            print(f"  {t}: failed: {type(ex).__name__}: {str(ex)[:300]}", flush=True)


if __name__ == "__main__":
    which = sys.argv[1] if len(sys.argv) > 1 else "sep"
    rng = np.random.default_rng(0)
    if which == "sep":
        x = rng.standard_normal((1, 4, 3072, 256)).astype(np.float32)
        print("Kim_Vocal_2 (one ~6 s chunk):")
        bench("models/uvr/Kim_Vocal_2.onnx", {"input": x}, {"batch_size": 1})
    elif which == "w2v":
        n = int(sys.argv[3]) if len(sys.argv) > 3 else 480000
        x = (rng.standard_normal((1, n)) * 0.1).astype(np.float32)
        print("wav2vec2-large (one 30 s chunk):")
        bench(sys.argv[2], {"input_values": x}, {})
