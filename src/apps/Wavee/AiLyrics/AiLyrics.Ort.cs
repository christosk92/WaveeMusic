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
            var ort = new Ort(lib, api, version);
            nint env;
            fixed (byte* id = "wavee-ai-lyrics\0"u8) ort.Check(((delegate* unmanaged<int, byte*, nint*, nint>)api[I_CreateEnv])(LogWarning, id, &env));
            ort._env = env;
            nint mi;
            ort.Check(((delegate* unmanaged<int, int, nint*, nint>)api[I_CreateCpuMemoryInfo])(OrtDeviceAllocator, OrtMemTypeDefault, &mi));
            ort._memInfo = mi;
            return ort;
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
                return new OrtSession(this, session);
            }
            finally { ((delegate* unmanaged<nint, void>)_api[I_ReleaseSessionOptions])(so); }
        }

        void Config(nint so, string key, string value)
        {
            fixed (byte* k = Z(key)) fixed (byte* v = Z(value))
                Check(((delegate* unmanaged<nint, byte*, byte*, nint>)_api[I_AddSessionConfigEntry])(so, k, v));
        }

        /// <summary>One float input -> one float output. <paramref name="output"/> is resized to the output's element
        /// count; its shape is returned.</summary>
        internal long[] Run(nint session, byte[] inName, byte[] outName, float[] input, ReadOnlySpan<long> shape, ref float[] output)
        {
            nint inVal = 0, outVal = 0;
            try
            {
                fixed (float* data = input)
                fixed (long* sh = shape)
                {
                    Check(((delegate* unmanaged<nint, void*, nuint, long*, nuint, int, nint*, nint>)_api[I_CreateTensorWithData])(
                        _memInfo, data, (nuint)(input.Length * sizeof(float)), sh, (nuint)shape.Length, TensorFloat, &inVal));
                    fixed (byte* inN = inName) fixed (byte* outN = outName)
                    {
                        byte* inNames = inN, outNames = outN;
                        nint inV = inVal;
                        Check(((delegate* unmanaged<nint, nint, byte**, nint*, nuint, byte**, nuint, nint*, nint>)_api[I_Run])(
                            session, 0, &inNames, &inV, 1, &outNames, 1, &outVal));
                    }
                }
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
                if (inVal != 0) ((delegate* unmanaged<nint, void>)_api[I_ReleaseValue])(inVal);
                if (outVal != 0) ((delegate* unmanaged<nint, void>)_api[I_ReleaseValue])(outVal);
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
            return _ort.Run(_s, _in, _out, input, shape, ref output);
        }

        public void Dispose() { if (_s != 0) { _ort.ReleaseSession(_s); _s = 0; } }
    }
}
