// ── AiLyrics/AiLyrics.Ort.cs ─────────────────────────────────────────────────────────────────────────────────────────
// Ort, OrtSession, OrtException
//
// Role: HOST (native interop; owned by the AI lyrics worker thread)
//
// A minimal binding over the ONNX Runtime C API, the same way Wavee reaches every other native API: function pointers,
// no managed wrapper, nothing the NativeAOT/trim build has to keep alive by reflection. The runtime is not shipped with
// the app: it is part of the AI download (onnxruntime.dll 1.30.0 + the QNN plugin execution provider), loaded from
// `ai\lyrics\runtime\` by absolute path.
//
// The API table is versioned: `OrtGetApiBase()->GetApi(30)` returns the 1.30 table or null for an older runtime. The
// indices below are the member positions in `struct OrtApi` of onnxruntime_c_api.h at v1.30.0 (ORT only appends to
// that struct, so they never move).

using System.Runtime.InteropServices;
using System.Text;

namespace Wavee;

public static partial class AiLyrics
{
    public sealed class OrtException(string message) : Exception(message);

    public sealed unsafe class Ort : IDisposable
    {
        const uint ApiVersion = 30;

        // struct OrtApi member indices (onnxruntime_c_api.h, v1.30.0)
        const int I_GetErrorMessage = 2, I_CreateEnv = 3, I_CreateSession = 7, I_Run = 9, I_CreateSessionOptions = 10,
            I_SetGraphOptimizationLevel = 23, I_SetIntraOpNumThreads = 24, I_CreateTensorWithData = 49,
            I_GetTensorMutableData = 51, I_GetDimensionsCount = 61, I_GetDimensions = 62, I_GetTensorTypeAndShape = 65,
            I_CreateCpuMemoryInfo = 69, I_ReleaseEnv = 92, I_ReleaseStatus = 93, I_ReleaseMemoryInfo = 94,
            I_ReleaseSession = 95, I_ReleaseValue = 96, I_ReleaseTensorTypeAndShapeInfo = 99, I_ReleaseSessionOptions = 100,
            I_AddFreeDimensionOverrideByName = 124, I_AddSessionConfigEntry = 130, I_RegisterExecutionProviderLibrary = 301,
            I_GetEpDevices = 303, I_AppendExecutionProviderV2 = 304, I_HardwareDeviceType = 307, I_HardwareDeviceVendor = 309,
            I_EpDeviceEpName = 312, I_EpDeviceDevice = 316;

        static readonly int[] s_used = [I_GetErrorMessage, I_CreateEnv, I_CreateSession, I_Run, I_CreateSessionOptions,
            I_SetGraphOptimizationLevel, I_SetIntraOpNumThreads, I_CreateTensorWithData, I_GetTensorMutableData,
            I_GetDimensionsCount, I_GetDimensions, I_GetTensorTypeAndShape, I_CreateCpuMemoryInfo, I_ReleaseEnv,
            I_ReleaseStatus, I_ReleaseMemoryInfo, I_ReleaseSession, I_ReleaseValue, I_ReleaseTensorTypeAndShapeInfo,
            I_ReleaseSessionOptions, I_AddFreeDimensionOverrideByName, I_AddSessionConfigEntry,
            I_RegisterExecutionProviderLibrary, I_GetEpDevices, I_AppendExecutionProviderV2, I_HardwareDeviceType,
            I_HardwareDeviceVendor, I_EpDeviceEpName, I_EpDeviceDevice];

        const int LogWarning = 2, OrtDeviceAllocator = 0, OrtMemTypeDefault = 0, TensorFloat = 1, HardwareNpu = 2;
        public const string QnnProvider = "QNNExecutionProvider";

        readonly nint _lib;
        readonly void** _api;
        nint _env, _memInfo;
        nint _npu;                                 // const OrtEpDevice* for QNN on the NPU
        public string Version { get; }
        public string NpuVendor { get; private set; } = "";

        /// <summary>Every device ONNX Runtime listed at registration ("provider/type/vendor"), for the error detail when
        /// no NPU shows up.</summary>
        public IReadOnlyList<string> Devices { get; private set; } = [];

        Ort(nint lib, void** api, string version) { _lib = lib; _api = api; Version = version; }

        /// <summary>Loads onnxruntime.dll from <paramref name="runtimeDir"/> and creates the environment. Throws
        /// <see cref="OrtException"/> when the library cannot load or is older than API 30.</summary>
        public static Ort Load(string runtimeDir)
        {
            string path = Path.Combine(runtimeDir, "onnxruntime.dll");
            if (!NativeLibrary.TryLoad(path, out nint lib)) throw new OrtException("onnxruntime.dll could not be loaded from " + runtimeDir);
            if (!NativeLibrary.TryGetExport(lib, "OrtGetApiBase", out nint baseFn)) throw new OrtException("onnxruntime.dll has no OrtGetApiBase export");
            void** apiBase = ((delegate* unmanaged<void**>)baseFn)();
            void** api = ((delegate* unmanaged<uint, void**>)apiBase[0])(ApiVersion);
            string version = Utf8(((delegate* unmanaged<byte*>)apiBase[1])());
            if (api is null) throw new OrtException("ONNX Runtime " + version + " is older than the API this build needs (" + ApiVersion + ")");
            // every member this binding calls must be in the table: a null slot would be a jump to address zero
            foreach (int i in s_used)
                if (api[i] is null) throw new OrtException("ONNX Runtime " + version + " lacks API member " + i);
            var ort = new Ort(lib, api, version);
            try
            {
                nint env;
                fixed (byte* id = "wavee-ai-lyrics\0"u8) ort.Check(((delegate* unmanaged<int, byte*, nint*, nint>)api[I_CreateEnv])(LogWarning, id, &env));
                ort._env = env;
                nint mi;
                ort.Check(((delegate* unmanaged<int, int, nint*, nint>)api[I_CreateCpuMemoryInfo])(OrtDeviceAllocator, OrtMemTypeDefault, &mi));
                ort._memInfo = mi;
                return ort;
            }
            catch { ort.Dispose(); throw; }                                // the environment must not outlive a failed load
        }

        /// <summary>Registers the QNN plugin execution provider and finds its NPU device. Returns false when the
        /// provider loads but lists no NPU (driver missing or too old).</summary>
        public bool UseQnnNpu(string providerDllPath)
        {
            // Plugin devices report the REGISTRATION name as their provider name, so register under the provider's own.
            fixed (byte* name = "QNNExecutionProvider\0"u8)
            fixed (char* p = providerDllPath)
                Check(((delegate* unmanaged<nint, byte*, char*, nint>)_api[I_RegisterExecutionProviderLibrary])(_env, name, p));
            nint* devices; nuint count;
            Check(((delegate* unmanaged<nint, nint**, nuint*, nint>)_api[I_GetEpDevices])(_env, &devices, &count));
            var seen = new List<string>();
            for (nuint i = 0; i < count; i++)
            {
                nint dev = devices[i];
                string ep = Utf8(((delegate* unmanaged<nint, byte*>)_api[I_EpDeviceEpName])(dev));
                nint hw = ((delegate* unmanaged<nint, nint>)_api[I_EpDeviceDevice])(dev);
                int type = ((delegate* unmanaged<nint, int>)_api[I_HardwareDeviceType])(hw);
                string vendor = Utf8(((delegate* unmanaged<nint, byte*>)_api[I_HardwareDeviceVendor])(hw));
                seen.Add(ep + "/" + (type switch { 0 => "CPU", 1 => "GPU", 2 => "NPU", _ => type.ToString() }) + "/" + vendor);
                if (_npu == 0 && ep == QnnProvider && type == HardwareNpu) { _npu = dev; NpuVendor = vendor; }
            }
            Devices = seen;
            return _npu != 0;
        }

        /// <summary>A session on the NPU. The compiled graph is cached at <c>&lt;model&gt;.ctx.onnx</c>: the first
        /// call compiles (tens of seconds for the large graphs) and writes it, later calls load it.</summary>
        public OrtSession CreateNpuSession(string modelPath, IReadOnlyList<(string Name, long Value)>? freeDims = null)
        {
            if (_npu == 0) throw new OrtException("no NPU device registered");
            string ctx = Path.ChangeExtension(modelPath, null) + ".ctx.onnx";
            bool cached = File.Exists(ctx);
            nint so;
            Check(((delegate* unmanaged<nint*, nint>)_api[I_CreateSessionOptions])(&so));
            try
            {
                Check(((delegate* unmanaged<nint, int, nint>)_api[I_SetGraphOptimizationLevel])(so, 99));
                Check(((delegate* unmanaged<nint, int, nint>)_api[I_SetIntraOpNumThreads])(so, 1));
                if (freeDims is not null)
                    foreach (var (n, v) in freeDims)
                        fixed (byte* nb = Z(n)) Check(((delegate* unmanaged<nint, byte*, long, nint>)_api[I_AddFreeDimensionOverrideByName])(so, nb, v));
                if (!cached)
                {
                    Config(so, "ep.context_enable", "1");
                    Config(so, "ep.context_file_path", ctx);
                }
                byte[][] keys = [Z("htp_performance_mode")];
                byte[][] vals = [Z("power_saver")];
                fixed (byte* k0 = keys[0]) fixed (byte* v0 = vals[0])
                {
                    byte** kp = stackalloc byte*[1]; byte** vp = stackalloc byte*[1];
                    kp[0] = k0; vp[0] = v0;
                    nint dev = _npu;
                    Check(((delegate* unmanaged<nint, nint, nint*, nuint, byte**, byte**, nuint, nint>)_api[I_AppendExecutionProviderV2])(so, _env, &dev, 1, kp, vp, 1));
                }
                nint session;
                string load = cached ? ctx : modelPath;
                fixed (char* p = load) Check(((delegate* unmanaged<nint, char*, nint, nint*, nint>)_api[I_CreateSession])(_env, p, so, &session));
                if (!cached && File.Exists(ctx))
                {
                    // The compiling session keeps the source graph and QNN's compile buffers alive: about 3 GB of
                    // private memory for the English pack. Release it and run from the cache it just wrote (private
                    // memory then stays near 70 MB; the compiled weights are file-mapped and reclaimable).
                    ((delegate* unmanaged<nint, void>)_api[I_ReleaseSession])(session);
                    session = 0;
                    return CreateNpuSession(modelPath, freeDims);
                }
                // No cache written: this session still holds the source graph and the compile buffers, and every later
                // load compiles again. It works, so it is used, but never silently.
                if (!cached) Log.Warn("ai-lyrics", $"ai.ort.no-ctx model={Path.GetFileName(modelPath)}: ONNX Runtime wrote no compiled cache; running from the compiling session");
                return new OrtSession(this, session);
            }
            finally { ((delegate* unmanaged<nint, void>)_api[I_ReleaseSessionOptions])(so); }
        }

        void Config(nint so, string key, string value)
        {
            fixed (byte* k = Z(key)) fixed (byte* v = Z(value))
                Check(((delegate* unmanaged<nint, byte*, byte*, nint>)_api[I_AddSessionConfigEntry])(so, k, v));
        }

        /// <summary>One float input -> one float output through a chain of sessions: session i's output tensor is session
        /// i + 1's input as it is, in ONNX Runtime's memory, never copied to the managed heap. Only the last output is
        /// copied out: <paramref name="output"/> is resized to its element count, and its shape is returned.</summary>
        /// <remarks>The aligner is why: its first two stages output 65.5 MB and 32.8 MB per 10 s chunk ([1, 512, 31999] and
        /// [1, 512, 15999] floats). Copied out stage by stage, they lived on as ~98 MB of live LOH for as long as the aligner
        /// stayed loaded. Here each intermediate is released as soon as the next stage has consumed it.</remarks>
        internal long[] Run(ReadOnlySpan<nint> sessions, ReadOnlySpan<byte[]> inNames, ReadOnlySpan<byte[]> outNames,
            float[] input, ReadOnlySpan<long> shape, ref float[] output)
        {
            nint cur = 0;                                   // the current stage's input; after the loop, the last output
            try
            {
                fixed (float* data = input)                 // pinned until every stage ran: the first value wraps it
                fixed (long* sh = shape)
                {
                    Check(((delegate* unmanaged<nint, void*, nuint, long*, nuint, int, nint*, nint>)_api[I_CreateTensorWithData])(
                        _memInfo, data, (nuint)(input.Length * sizeof(float)), sh, (nuint)shape.Length, TensorFloat, &cur));
                    for (int i = 0; i < sessions.Length; i++)
                    {
                        nint next = 0;
                        fixed (byte* inN = inNames[i]) fixed (byte* outN = outNames[i])
                        {
                            byte* inName = inN, outName = outN;
                            nint inV = cur;
                            Check(((delegate* unmanaged<nint, nint, byte**, nint*, nuint, byte**, nuint, nint*, nint>)_api[I_Run])(
                                sessions[i], 0, &inName, &inV, 1, &outName, 1, &next));
                        }
                        ((delegate* unmanaged<nint, void>)_api[I_ReleaseValue])(cur);
                        cur = next;
                    }
                }
                nint outVal = cur;
                nint info;
                Check(((delegate* unmanaged<nint, nint*, nint>)_api[I_GetTensorTypeAndShape])(outVal, &info));
                long[] dims;
                try
                {
                    nuint n;
                    Check(((delegate* unmanaged<nint, nuint*, nint>)_api[I_GetDimensionsCount])(info, &n));
                    dims = new long[(int)n];
                    fixed (long* d = dims) Check(((delegate* unmanaged<nint, long*, nuint, nint>)_api[I_GetDimensions])(info, d, n));
                }
                finally { ((delegate* unmanaged<nint, void>)_api[I_ReleaseTensorTypeAndShapeInfo])(info); }
                long count = 1; foreach (long d in dims) count *= d;
                if (output.Length != count) output = new float[count];
                void* src;
                Check(((delegate* unmanaged<nint, void**, nint>)_api[I_GetTensorMutableData])(outVal, &src));
                new ReadOnlySpan<float>(src, (int)count).CopyTo(output);
                return dims;
            }
            finally
            {
                if (cur != 0) ((delegate* unmanaged<nint, void>)_api[I_ReleaseValue])(cur);
            }
        }

        internal void ReleaseSession(nint s) => ((delegate* unmanaged<nint, void>)_api[I_ReleaseSession])(s);

        void Check(nint status)
        {
            if (status == 0) return;
            string msg = Utf8(((delegate* unmanaged<nint, byte*>)_api[I_GetErrorMessage])(status));
            ((delegate* unmanaged<nint, void>)_api[I_ReleaseStatus])(status);
            throw new OrtException(msg);
        }

        static byte[] Z(string s) { var b = Encoding.UTF8.GetBytes(s + "\0"); return b; }

        static string Utf8(byte* p) => p is null ? "" : Marshal.PtrToStringUTF8((nint)p) ?? "";

        public void Dispose()
        {
            if (_memInfo != 0) { ((delegate* unmanaged<nint, void>)_api[I_ReleaseMemoryInfo])(_memInfo); _memInfo = 0; }
            if (_env != 0) { ((delegate* unmanaged<nint, void>)_api[I_ReleaseEnv])(_env); _env = 0; }
            // The library stays loaded: ONNX Runtime does not support unloading and reloading within one process.
        }
    }

    /// <summary>A loaded model on the NPU with one float input and one float output.</summary>
    public sealed class OrtSession : IDisposable
    {
        readonly Ort _ort;
        nint _s;
        readonly byte[] _in, _out;

        internal OrtSession(Ort ort, nint s, string input = "x", string output = "y")
        { _ort = ort; _s = s; _in = Encoding.UTF8.GetBytes(input + "\0"); _out = Encoding.UTF8.GetBytes(output + "\0"); }

        public OrtSession WithNames(string input, string output) => new(_ort, Detach(), input, output);

        nint Detach() { nint s = _s; _s = 0; return s; }

        public long[] Run(float[] input, ReadOnlySpan<long> shape, ref float[] output)
        {
            if (_s == 0) throw new ObjectDisposedException(nameof(OrtSession));
            return _ort.Run([_s], [_in], [_out], input, shape, ref output);
        }

        /// <summary>Runs <paramref name="stages"/> as one chain (each output is the next stage's input and stays in ONNX
        /// Runtime's memory) and copies only the last output into <paramref name="output"/>; its shape is returned.</summary>
        public static long[] RunChain(OrtSession[] stages, float[] input, ReadOnlySpan<long> shape, ref float[] output)
        {
            if (stages.Length == 0) throw new ArgumentException("at least one stage", nameof(stages));
            var handles = new nint[stages.Length];
            var ins = new byte[stages.Length][];
            var outs = new byte[stages.Length][];
            for (int i = 0; i < stages.Length; i++)
            {
                var s = stages[i];
                if (s._s == 0) throw new ObjectDisposedException(nameof(OrtSession));
                if (s._ort != stages[0]._ort) throw new ArgumentException("every stage must belong to one runtime", nameof(stages));
                handles[i] = s._s; ins[i] = s._in; outs[i] = s._out;
            }
            return stages[0]._ort.Run(handles, ins, outs, input, shape, ref output);
        }

        public void Dispose() { if (_s != 0) { _ort.ReleaseSession(_s); _s = 0; } }
    }
}
