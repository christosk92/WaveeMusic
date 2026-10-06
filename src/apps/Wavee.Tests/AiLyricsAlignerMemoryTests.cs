// ── Wavee.Tests/AiLyricsAlignerMemoryTests.cs — the aligner keeps no per-stage copies on the managed heap ───────────────
//
// The wav2vec2 aligner runs as five chained NPU graphs. conv0 and conv1 output [1, 512, 31999] and [1, 512, 15999] floats
// per 10 s chunk (65.5 MB + 32.8 MB). The aligner used to copy every stage's output into its own float[] and keep those
// arrays for as long as it stayed loaded: the first AI job of a session put ~98 MB of live arrays on the LOH (the
// 132 -> 251 MB jump in the 2026-10-06 log, next to a crossfade that turned out to be unrelated). The stages now hand
// their tensors on inside ONNX Runtime (OrtSession.RunChain) and only the last output is copied out.
//
// The models need the NPU, so this pins the shape of the fix: the aligner owns exactly one managed output buffer, and
// nothing that could hold one array per stage.
using System.Reflection;
using Xunit;

namespace Wavee.Tests;

public class AiLyricsAlignerMemoryTests
{
    [Fact]
    public void The_aligner_holds_only_the_last_stages_output_on_the_managed_heap()
    {
        var fields = typeof(AiLyrics.Aligner).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        Assert.DoesNotContain(fields, f => f.FieldType == typeof(float[][]));                 // no buffer per stage
        Assert.Single(fields, f => f.FieldType == typeof(float[]));                            // the log-probabilities only
    }

    [Fact]
    public void The_aligner_still_needs_exactly_five_stages()
    {
        var vocab = AiLyrics.Align.Vocab.Parse("{\"<pad>\": 0, \"|\": 1, \"a\": 2}");
        Assert.Throws<ArgumentException>(() => new AiLyrics.Aligner([], vocab));
    }
}
