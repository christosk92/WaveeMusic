// ── Wavee.Tests/AiLyricsJobScratchTests.cs — the AI-lyrics jobs' working buffers outlive one job ────────────────────
//
// `AiLyrics.JobScratch` is the ~28 MB of large-object arrays a TrackJob runs on, owned by the worker and reused job to
// job. The job itself needs the NPU models, so the scratch's own contract is pinned here: the fixed buffers are the
// same arrays every job, a buffer a job grew (even a job that was cancelled) is what the next job starts from, and a
// language with more output classes grows the log-probability buffer instead of overrunning it.
using Xunit;

namespace Wavee.Tests;

public class AiLyricsJobScratchTests
{
    [Fact]
    public void A_second_job_reuses_the_first_jobs_buffers_and_grows_for_more_classes()
    {
        var resampler = new AiLyrics.Dsp.Resampler(160, 441);
        var scratch = new AiLyrics.JobScratch();

        // Job one: English-sized output, and its vocal window outgrows the start size before it is cancelled. Run's
        // `finally` hands the grown arrays back through Keep, cancelled or not.
        scratch.Prepare(classes: 32, resampler);
        float[] block = scratch.BlockL, chunk = scratch.Chunk, logp = scratch.Logp;
        Assert.True(logp.Length >= AiLyrics.Aligner.StepFrames * 32);
        float[] grownVocL = new float[scratch.VocL.Length * 2], grownVocR = new float[scratch.VocR.Length * 2];
        scratch.Keep(scratch.MixL, scratch.MixR, grownVocL, grownVocR, scratch.Mono44, scratch.Resampled);

        // Job two: a language with many more classes (Korean has 1,205).
        scratch.Prepare(classes: 1205, resampler);

        Assert.Same(block, scratch.BlockL);                       // the fixed buffers are the same arrays
        Assert.Same(chunk, scratch.Chunk);
        Assert.Same(grownVocL, scratch.VocL);                     // the grown window carried over, not re-allocated small
        Assert.Same(grownVocR, scratch.VocR);
        Assert.True(scratch.Logp.Length >= AiLyrics.Aligner.StepFrames * 1205);
        Assert.NotSame(logp, scratch.Logp);

        // Back to the small language: nothing shrinks, nothing is re-allocated.
        float[] bigLogp = scratch.Logp, mono44 = scratch.Mono44, resampled = scratch.Resampled;
        scratch.Prepare(classes: 32, resampler);
        Assert.Same(bigLogp, scratch.Logp);
        Assert.Same(mono44, scratch.Mono44);
        Assert.Same(resampled, scratch.Resampled);
        Assert.True(scratch.Resampled.Length >= resampler.OutLength(scratch.Mono44.Length));
    }
}
