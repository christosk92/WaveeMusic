// ── AiLyrics/AiLyrics.Models.cs ──────────────────────────────────────────────────────────────────────────────────────
// Separator, Aligner
//
// Role: HOST (owned by the AI lyrics worker thread)
//
// The two NPU models behind small, fixed-size calls:
//
//   Separator  MDX-Net (Kim_Vocal_2): a 261,120-sample stereo window at 44.1 kHz -> its STFT (7680/1024, 3072 bins x
//              256 frames, real/imag per channel) -> the model -> inverse STFT -> 253,440 samples of vocals (the
//              window minus the 3,840-sample centre padding on both sides). One call per 5.75 s of audio.
//   Aligner    wav2vec2 CTC as five graphs (plan §6.3): 10 s of 16 kHz mono in, 499 frames of letter
//              log-probabilities out. The worker slides it by 8 s and keeps the middle 400 frames (1 s of context on
//              both sides).

namespace Wavee;

public static partial class AiLyrics
{
    public sealed class Separator : IDisposable
    {
        public const int NFft = 7680, Hop = 1024, DimF = 3072, DimT = 256, SampleRate = 44100;
        public const int Trim = NFft / 2;                              // 3840
        public const int Window = Hop * (DimT - 1);                    // 261,120
        public const int Block = Window - 2 * Trim;                    // 253,440 samples of vocals per call
        const float Compensate = 1.009f;

        readonly OrtSession _s;
        // one STFT plan and scratch per channel: the two channels transform on two cores at once
        readonly Dsp.Stft[] _stft = [new(NFft, Hop), new(NFft, Hop)];
        readonly float[] _input = new float[4 * DimF * DimT];
        float[] _output = new float[4 * DimF * DimT];
        readonly float[][] _re = [new float[DimF * DimT], new float[DimF * DimT]], _im = [new float[DimF * DimT], new float[DimF * DimT]];
        readonly float[][] _wave = [new float[Window], new float[Window]];
        static readonly long[] s_shape = [1, 4, DimF, DimT];

        public Separator(OrtSession session) { _s = session; }

        /// <summary>Separates one window. <paramref name="left"/>/<paramref name="right"/> are exactly
        /// <see cref="Window"/> samples; <paramref name="vocalsL"/>/<paramref name="vocalsR"/> receive
        /// <see cref="Block"/> samples (the window without its <see cref="Trim"/> edges).</summary>
        public void Run(float[] left, float[] right, float[] vocalsL, float[] vocalsR)
        {
            int plane = DimF * DimT;
            Parallel.For(0, 2, c =>
            {
                _stft[c].Forward(c == 0 ? left : right, _re[c], _im[c], DimF);
                _re[c].AsSpan().CopyTo(_input.AsSpan((2 * c) * plane, plane));
                _im[c].AsSpan().CopyTo(_input.AsSpan((2 * c + 1) * plane, plane));
                _input.AsSpan((2 * c) * plane, 3 * DimT).Clear();               // the lowest three bins, as trained
                _input.AsSpan((2 * c + 1) * plane, 3 * DimT).Clear();
            });
            _s.Run(_input, s_shape, ref _output);
            float[] output = _output;
            Parallel.For(0, 2, c =>
            {
                output.AsSpan((2 * c) * plane, plane).CopyTo(_re[c]);
                output.AsSpan((2 * c + 1) * plane, plane).CopyTo(_im[c]);
                _stft[c].Inverse(_re[c], _im[c], DimT, DimF, _wave[c]);
                var dst = c == 0 ? vocalsL : vocalsR;
                var src = _wave[c].AsSpan(Trim, Block);
                for (int i = 0; i < Block; i++) dst[i] = src[i] * Compensate;
            });
        }

        public void Dispose() => _s.Dispose();
    }

    public sealed class Aligner : IDisposable
    {
        public const int SampleRate = 16000, ChunkSeconds = 10, Samples = SampleRate * ChunkSeconds;
        public const int ContextSamples = SampleRate, StepSamples = Samples - 2 * ContextSamples;   // 8 s per step
        public const int FramesPerSecond = 50, ContextFrames = FramesPerSecond, StepFrames = StepSamples / (SampleRate / FramesPerSecond);
        public static readonly string[] StageNames = ["conv0", "conv1", "convs", "layers_a", "layers_b"];

        readonly OrtSession[] _stages;
        // Only the last stage's output (the log-probabilities, [1, 499, classes]) is copied to the managed heap. The stages
        // in between hand their tensors on inside ONNX Runtime (OrtSession.RunChain): conv0 and conv1 output 65.5 MB and
        // 32.8 MB per chunk, and a managed copy of each stayed live on the LOH for as long as the aligner was loaded.
        float[] _out = [];
        public readonly Align.Vocab Vocab;

        public Aligner(OrtSession[] stages, Align.Vocab vocab)
        {
            if (stages.Length != StageNames.Length) throw new ArgumentException("five stages expected", nameof(stages));
            _stages = stages; Vocab = vocab;
        }

        /// <summary>Runs one 10 s chunk (already normalised to zero mean, unit variance) and copies the middle
        /// <see cref="StepFrames"/> frames of log-probabilities into <paramref name="logp"/> ([frame * classes + class]).
        /// Returns the number of classes.</summary>
        public int Run(float[] chunk, Span<float> logp)
        {
            if (chunk.Length != Samples) throw new ArgumentException("one chunk is exactly " + Samples + " samples", nameof(chunk));
            long[] shape = OrtSession.RunChain(_stages, chunk, [1, Samples], ref _out);
            float[] x = _out;
            // x: [1, frames, classes]
            int frames = (int)shape[1], classes = (int)shape[2];
            int keep = Math.Min(StepFrames, frames - ContextFrames);
            x.AsSpan(ContextFrames * classes, keep * classes).CopyTo(logp);
            return classes;
        }

        public void Dispose() { foreach (var s in _stages) s.Dispose(); }
    }

    /// <summary>The runtime and the models, loaded once for the worker's lifetime. The ORT environment, the QNN
    /// registration and the separator stay for that lifetime; the aligner is swapped per language
    /// (<see cref="SwapAligner"/>) because every loaded language holds its own NPU buffers.</summary>
    public sealed class LoadedModels : IDisposable
    {
        readonly Dictionary<string, Aligner> _aligners = new(StringComparer.OrdinalIgnoreCase);

        public Ort Ort { get; }
        public Separator Separator { get; }

        /// <summary>The loaded aligners by language: the ones <see cref="Load"/> opened, then exactly the one the last
        /// <see cref="SwapAligner"/> opened (none when that swap failed).</summary>
        public IReadOnlyDictionary<string, Aligner> Aligners => _aligners;

        LoadedModels(Ort ort, Separator sep) { Ort = ort; Separator = sep; }

        /// <summary>Loads ONNX Runtime and the QNN provider from <paramref name="runtimeDir"/>, then one session per
        /// graph from <paramref name="modelsDir"/>. <paramref name="preparing"/>(done, total, cachedAlready) is called
        /// around each session: a graph without a compiled cache takes tens of seconds the first time. Cancellation is
        /// honoured between graphs; on any failure everything opened so far is released.</summary>
        /// <para><paramref name="compileOnly"/>: installed languages that are not needed now. A graph of theirs without a
        /// compiled cache is compiled (the one-time step happens at setup, not when a song in that language plays) and
        /// released at once: every loaded language holds its own NPU buffers.</para>
        public static LoadedModels Load(string runtimeDir, string modelsDir, IReadOnlyList<string> languages,
            Action<int, int, bool>? preparing, CancellationToken ct, IReadOnlyList<string>? compileOnly = null)
        {
            var ort = Ort.Load(runtimeDir);
            Separator? sep = null;
            LoadedModels? models = null;
            try
            {
                string qnn = Path.Combine(runtimeDir, "onnxruntime_providers_qnn.dll");
                if (!ort.UseQnnNpu(qnn)) throw new NoNpuException(string.Join(", ", ort.Devices));
                var compile = new List<string>();
                foreach (string lang in compileOnly ?? [])
                    foreach (string st in Aligner.StageNames)
                    {
                        string g = StagePath(modelsDir, lang, st);
                        if (!File.Exists(CtxPath(g))) compile.Add(g);
                    }
                int done = 0, total = 1 + languages.Count * Aligner.StageNames.Length + compile.Count;
                foreach (string g in compile)
                    OpenReporting(ort, g, separator: false, ref done, total, preparing, ct).Dispose();   // writes the cache; the session goes
                // WithNames moves the native session into a new wrapper; the old one is left holding nothing
                sep = new Separator(OpenReporting(ort, Path.Combine(modelsDir, "separator.onnx"), separator: true, ref done, total, preparing, ct)
                    .WithNames("input", "output"));
                models = new LoadedModels(ort, sep);
                foreach (string lang in languages)
                    models._aligners[lang] = OpenAligner(ort, modelsDir, lang, ref done, total, preparing, ct);
                return models;
            }
            catch
            {
                if (models is not null) models.Dispose();
                else { sep?.Dispose(); ort.Dispose(); }
                throw;
            }
        }

        /// <summary>Make <paramref name="language"/>'s aligner the only loaded one: the current aligner(s) are released
        /// first (their NPU buffers go before the new ones are taken), then its vocabulary and five sessions are opened —
        /// a stage without a compiled cache is compiled now, reported through <paramref name="preparing"/>(done, 5,
        /// cachedAlready). The runtime and the separator are untouched. A no-op when it already is the only one. On
        /// failure (cancellation included) no aligner is loaded and the exception propagates; the models stay usable
        /// for the next swap.</summary>
        public void SwapAligner(string modelsDir, string language, Action<int, int, bool>? preparing, CancellationToken ct)
        {
            if (_aligners.Count == 1 && _aligners.ContainsKey(language)) return;
            foreach (var a in _aligners.Values) a.Dispose();
            _aligners.Clear();
            int done = 0;
            _aligners[language] = OpenAligner(Ort, modelsDir, language, ref done, Aligner.StageNames.Length, preparing, ct);
        }

        static string StagePath(string modelsDir, string language, string stage) => Path.Combine(modelsDir, $"align-{language}.{stage}.onnx");

        /// <summary>One language's aligner. The vocabulary is read first, so a missing or corrupt one fails before any
        /// NPU work; a stage that fails releases the stages already open.</summary>
        static Aligner OpenAligner(Ort ort, string modelsDir, string language, ref int done, int total,
            Action<int, int, bool>? preparing, CancellationToken ct)
        {
            var vocab = Align.Vocab.Parse(File.ReadAllText(Path.Combine(modelsDir, $"align-{language}.vocab.json")));
            var stages = new OrtSession[Aligner.StageNames.Length];
            int opened = 0;
            try
            {
                for (; opened < stages.Length; opened++)
                    stages[opened] = OpenReporting(ort, StagePath(modelsDir, language, Aligner.StageNames[opened]), separator: false, ref done, total, preparing, ct);
                return new Aligner(stages, vocab);
            }
            catch
            {
                for (int i = 0; i < opened; i++) stages[i].Dispose();
                throw;
            }
        }

        /// <summary>One session, with the cancellation check before it and the progress report around it.</summary>
        static OrtSession OpenReporting(Ort ort, string model, bool separator, ref int done, int total,
            Action<int, int, bool>? preparing, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            bool cached = File.Exists(CtxPath(model));
            preparing?.Invoke(done, total, cached);
            var session = Open(ort, model, separator);
            done++;
            preparing?.Invoke(done, total, cached);
            return session;
        }

        public static string CtxPath(string model) => Path.ChangeExtension(model, null) + ".ctx.onnx";

        /// <summary>A stale compiled cache (a new NPU driver or runtime) fails to load: drop it and compile again once.</summary>
        static OrtSession Open(Ort ort, string model, bool separator)
        {
            (string, long)[]? dims = separator ? [("batch_size", 1)] : null;
            try { return ort.CreateNpuSession(model, dims); }
            catch (OrtException) when (File.Exists(CtxPath(model)))
            {
                DeleteCompiled(model);
                return ort.CreateNpuSession(model, dims);
            }
        }

        public static void DeleteCompiled(string model)
        {
            string stem = Path.ChangeExtension(model, null);
            foreach (string f in Directory.EnumerateFiles(Path.GetDirectoryName(model)!, Path.GetFileName(stem) + ".ctx*"))
                try { File.Delete(f); } catch (IOException) { }
        }

        public void Dispose()
        {
            foreach (var a in _aligners.Values) a.Dispose();
            _aligners.Clear();
            Separator.Dispose();
            Ort.Dispose();
        }
    }

    /// <summary>ONNX Runtime loaded, but the QNN provider lists no NPU device (driver missing or too old).</summary>
    public sealed class NoNpuException(string devices) : Exception("The QNN execution provider found no NPU device (devices: " + devices + ").");
}
