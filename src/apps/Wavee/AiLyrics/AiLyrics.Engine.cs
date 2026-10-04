// ── AiLyrics/AiLyrics.Engine.cs ──────────────────────────────────────────────────────────────────────────────────────
// IPcmSource, TrackJob, JobProgress
//
// Role: HOST (runs on the AI lyrics worker thread; never on the UI thread)
//
// One song, streamed (plan §6.2; port of the lab's `stream.py`). Audio is pulled block by block, the separator and the
// aligner run on the NPU just ahead of what is needed, and the online Viterbi commits each word shortly before the
// playhead reaches it. Every time more lines are complete, the job hands back a document in which those lines are word
// by word and the rest still carry the provider's line timing.
//
// Memory is bounded: the mix and the vocals are sliding windows; what grows with the song is the 10 ms envelope, the
// log-probabilities (32 floats per 20 ms) and the Viterbi backpointers (2 bits per state per frame).

using System.Diagnostics;

namespace Wavee;

public static partial class AiLyrics
{
    /// <summary>Interleaved stereo float PCM at 44.1 kHz, pulled by the job. <see cref="Read"/> returns frames read, 0 at
    /// the end, negative on an error.</summary>
    public interface IPcmSource
    {
        int Read(Span<float> interleavedStereo);
    }

    public readonly record struct JobProgress(int LinesReady, int LineCount, double ProcessedSeconds, bool Final);

    public sealed class TrackJob
    {
        const int Sr44 = Separator.SampleRate, Sr16 = Aligner.SampleRate, Fps = Aligner.FramesPerSecond;
        const double LookaheadSeconds = 8.0, PriorPadSeconds = 2.0;

        readonly Lyrics.Doc _src;
        readonly Separator _sep;
        readonly Aligner _aln;
        readonly IPcmSource _pcm;
        readonly Func<double> _playhead;
        readonly string _language;
        readonly bool _linePrior;

        readonly List<List<Align.LyricWord>> _words = new();
        readonly Align.Graph _graph;
        readonly Align.OnlineViterbi _vit;
        readonly int[] _lineLastToken;               // last token index of each line, -1 when the line has no tokens
        readonly Dsp.Envelope _env = new(Sr44);
        readonly Dsp.Resampler _rs = new(160, 441);

        public Stopwatch Elapsed { get; } = new();
        public double SeparateSeconds { get; private set; }
        public double AlignSeconds { get; private set; }

        public TrackJob(Lyrics.Doc src, string language, Separator sep, Aligner aln, IPcmSource pcm, Func<double> playheadSeconds)
        {
            _src = src; _language = language; _sep = sep; _aln = aln; _pcm = pcm; _playhead = playheadSeconds;
            _linePrior = src.IsSynced && src.Sync != Lyrics.SyncKind.Unsynced;
            foreach (var l in src.Lines) _words.Add(Align.Words(l.Text));
            _graph = Align.Graph.Build(_words, aln.Vocab);
            _lineLastToken = new int[src.Lines.Count];
            Array.Fill(_lineLastToken, -1);
            for (int k = 0; k < _graph.Length; k++) if (_graph.OwnerLine[k] >= 0) _lineLastToken[_graph.OwnerLine[k]] = k;

            IReadOnlyList<(double, double)>? windows = null;
            if (_linePrior && _graph.Length > 0)
            {
                var starts = new double[src.Lines.Count + 1];
                for (int i = 0; i < src.Lines.Count; i++) starts[i] = src.Lines[i].StartMs / 1000.0;
                starts[^1] = starts[^2] + 30;                                   // after the last line: open-ended
                var w = new (double, double)[_graph.Length];
                for (int k = 0; k < _graph.Length; k++)
                {
                    int li = _graph.OwnerLine[k];
                    if (li < 0) { w[k] = k > 0 ? w[k - 1] : (starts[0] - PriorPadSeconds, starts[1] + PriorPadSeconds); continue; }
                    w[k] = (starts[li] - PriorPadSeconds, starts[li + 1] + PriorPadSeconds);
                }
                windows = w;
            }
            _vit = new Align.OnlineViterbi(_graph, aln.Vocab.Blank, windows);
        }

        public int LineCount => _src.Lines.Count;

        /// <summary>Runs the song to the end. <paramref name="publish"/> is called (on this thread) whenever more lines
        /// are complete, and once at the end with the final document.</summary>
        public Lyrics.Doc Run(Action<Lyrics.Doc, JobProgress> publish, CancellationToken ct)
        {
            Elapsed.Start();
            // mix: a sliding window over the decoded audio, in mix-sample coordinates [mixStart, mixStart + mixLen)
            var mixL = new float[Separator.Window * 4]; var mixR = new float[Separator.Window * 4];
            long mixStart = 0; int mixLen = 0;
            bool eof = false;
            var pcm = new float[8192 * 2];

            // vocals: a sliding window too, [vocStart, vocStart + vocLen)
            var vocL = new float[Separator.Block * 6]; var vocR = new float[Separator.Block * 6];
            long vocStart = 0; int vocLen = 0;
            var blockL = new float[Separator.Block]; var blockR = new float[Separator.Block];
            var winL = new float[Separator.Window]; var winR = new float[Separator.Window];
            long sepWindow = 0;                                                  // windows separated so far
            long totalMix = long.MaxValue;                                       // known at EOF

            var chunk = new float[Aligner.Samples];
            var logp = new float[Aligner.StepFrames * _aln.Vocab.Classes];   // Korean has 1,205 classes
            long ctcPos = 0;                                                     // 16 kHz samples covered by emissions
            int linesReady = 0;

            void Pull(long upTo)
            {
                while (!eof && mixStart + mixLen < upTo)
                {
                    int frames = _pcm.Read(pcm);
                    if (frames <= 0) { eof = true; totalMix = mixStart + mixLen; break; }
                    EnsureCap(ref mixL, ref mixR, mixLen + frames);
                    for (int i = 0; i < frames; i++) { mixL[mixLen + i] = pcm[2 * i]; mixR[mixLen + i] = pcm[2 * i + 1]; }
                    mixLen += frames;
                }
            }

            bool SeparateNext()
            {
                long w0 = sepWindow * Separator.Block - Separator.Trim;          // window start in mix coordinates
                Pull(w0 + Separator.Window);
                if (eof && w0 + Separator.Trim >= totalMix) return false;       // nothing new to separate
                for (int i = 0; i < Separator.Window; i++)
                {
                    long m = w0 + i - mixStart;
                    bool inside = w0 + i >= 0 && w0 + i < Math.Min(totalMix, mixStart + mixLen);
                    winL[i] = inside ? mixL[m] : 0f; winR[i] = inside ? mixR[m] : 0f;
                }
                var t0 = Stopwatch.GetTimestamp();
                _sep.Run(winL, winR, blockL, blockR);
                SeparateSeconds += Stopwatch.GetElapsedTime(t0).TotalSeconds;
                long b0 = sepWindow * Separator.Block;                          // the block's first vocal sample
                int n = (int)Math.Min(Separator.Block, Math.Max(0, Math.Min(totalMix, long.MaxValue) - b0));
                EnsureCap(ref vocL, ref vocR, vocLen + n);
                Array.Copy(blockL, 0, vocL, vocLen, n); Array.Copy(blockR, 0, vocR, vocLen, n);
                vocLen += n;
                var monoArr = new float[n];
                for (int i = 0; i < n; i++) monoArr[i] = 0.5f * (blockL[i] + blockR[i]);
                _env.Append(monoArr);
                sepWindow++;
                // drop mix the next window no longer needs
                long keepFrom = sepWindow * Separator.Block - Separator.Trim;
                Compact(mixL, mixR, ref mixStart, ref mixLen, keepFrom);
                return true;
            }

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                long a0 = ctcPos - Aligner.ContextSamples;                       // chunk start, 16 kHz
                long need44 = (long)Math.Ceiling((a0 + Aligner.Samples) * (double)Sr44 / Sr16) + 4096;
                while (vocStart + vocLen < need44 && SeparateNext()) ct.ThrowIfCancellationRequested();
                long total16 = totalMix == long.MaxValue ? long.MaxValue : totalMix * Sr16 / Sr44;
                if (ctcPos >= total16) break;

                // resample the vocals this chunk covers (plus filter margin) and cut the chunk out
                var t0 = Stopwatch.GetTimestamp();
                long lo44 = Math.Max(vocStart, (long)Math.Floor(a0 * (double)Sr44 / Sr16) - 2048);
                long hi44 = Math.Min(vocStart + vocLen, (long)Math.Ceiling((a0 + Aligner.Samples) * (double)Sr44 / Sr16) + 2048);
                int span44 = (int)Math.Max(0, hi44 - lo44);
                var mono44 = new float[span44];
                for (int i = 0; i < span44; i++) { long v = lo44 + i - vocStart; mono44[i] = 0.5f * (vocL[v] + vocR[v]); }
                var r = new float[_rs.OutLength(span44)];
                _rs.Process(mono44, r);
                long off16 = (long)Math.Round(lo44 * (double)Sr16 / Sr44);
                Array.Clear(chunk);
                for (int i = 0; i < Aligner.Samples; i++)
                {
                    long j = a0 + i - off16;
                    if (j >= 0 && j < r.Length && a0 + i >= 0) chunk[i] = r[j];
                }
                double mean = 0; foreach (float s in chunk) mean += s; mean /= chunk.Length;
                double var2 = 0; foreach (float s in chunk) var2 += (s - mean) * (s - mean);
                float inv = (float)(1.0 / (Math.Sqrt(var2 / chunk.Length) + 1e-7));
                for (int i = 0; i < chunk.Length; i++) chunk[i] = (float)((chunk[i] - mean) * inv);
                int classes = _aln.Run(chunk, logp);
                AlignSeconds += Stopwatch.GetElapsedTime(t0).TotalSeconds;

                int frames = Aligner.StepFrames;
                if (total16 != long.MaxValue) frames = (int)Math.Max(0, Math.Min(frames, total16 * Fps / Sr16 - _vit.Frames));
                _vit.Feed(logp.AsSpan(0, frames * classes), classes);
                ctcPos += Aligner.StepSamples;

                // drop vocals the next chunk no longer needs
                long keep44 = (long)Math.Floor((ctcPos - Aligner.ContextSamples) * (double)Sr44 / Sr16) - 4096;
                Compact(vocL, vocR, ref vocStart, ref vocLen, keep44);

                bool final = total16 != long.MaxValue && ctcPos >= total16;
                int deadline = (int)((_playhead() + LookaheadSeconds) * Fps);
                int added = _vit.Commit(int.MaxValue / 2, final, final ? null : deadline);
                int ready = ReadyLines(final);
                if (final) break;
                if (added > 0 && ready > linesReady)
                {
                    linesReady = ready;
                    publish(BuildDoc(ready, final: false), new JobProgress(ready, LineCount, ctcPos / (double)Sr16, false));
                }
            }

            _vit.Commit(0, final: true);
            var doc = BuildDoc(LineCount, final: true);
            Elapsed.Stop();
            publish(doc, new JobProgress(LineCount, LineCount, ctcPos / (double)Sr16, true));
            return doc;
        }

        /// <summary>How many leading lines have all their words committed.</summary>
        int ReadyLines(bool final)
        {
            if (final) return LineCount;
            int n = 0;
            for (int li = 0; li < LineCount; li++)
            {
                int last = _lineLastToken[li];
                if (last >= 0 && !_vit.IsCommitted(last)) break;
                n++;
            }
            return n;
        }

        Lyrics.Doc BuildDoc(int readyLines, bool final)
        {
            int n = LineCount;
            var times = new Align.WordTime[n][];
            for (int li = 0; li < n; li++) { times[li] = new Align.WordTime[_words[li].Count]; Array.Fill(times[li], Align.WordTime.None); }
            Align.CollectWordTimes(_graph, _vit, times);
            Align.SnapToOnsets(times, _env.At, _env.Count, _env.QuietThreshold());

            var lines = new List<Lyrics.Line>(n);
            for (int li = 0; li < n; li++)
            {
                var src = _src.Lines[li];
                if (li >= readyLines || _words[li].Count == 0) { lines.Add(src); continue; }
                double? next = null;
                for (int j = li + 1; j < n; j++)
                {
                    var t = Array.Find(times[j], w => w.IsSet);
                    if (t.IsSet) { next = t.Start; break; }
                    if (_src.IsSynced) { next = _src.Lines[j].StartMs / 1000.0; break; }
                }
                double lineStart = _src.IsSynced ? src.StartMs / 1000.0 : (lines.Count > 0 ? (lines[^1].EndMs ?? lines[^1].StartMs) / 1000.0 : 0);
                if (!Align.Fill(times[li], lineStart, next)) { lines.Add(src); continue; }
                lines.Add(Align.WordSyncedLine(src, _words[li], times[li], next));
            }
            return _src with
            {
                Lines = lines,
                IsSynced = true,
                Sync = Lyrics.SyncKind.Syllable,
                Provider = ProviderId,
                Origin = _src.Provider,
                Language = _language,
                Generated = true,
                OffsetMsApplied = 0,
            };
        }

        static void EnsureCap(ref float[] a, ref float[] b, int need)
        {
            if (need <= a.Length) return;
            int cap = Math.Max(need, a.Length * 2);
            Array.Resize(ref a, cap); Array.Resize(ref b, cap);
        }

        static void Compact(float[] a, float[] b, ref long start, ref int len, long keepFrom)
        {
            long drop = Math.Clamp(keepFrom - start, 0, len);
            if (drop < 65536) return;                                            // not worth a copy yet
            int d = (int)drop;
            Array.Copy(a, d, a, 0, len - d); Array.Copy(b, d, b, 0, len - d);
            start += d; len -= d;
        }
    }
}
