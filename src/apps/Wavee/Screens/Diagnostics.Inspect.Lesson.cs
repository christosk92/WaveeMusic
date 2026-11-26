// ── Screens/Diagnostics.Inspect.Lesson.cs ────────────────────────────────────────────────────────────────────────────
// the `lesson` verb (#48 Mix, "Build a transition together", Entities/Mix.Lesson.cs): the lesson's state and its audio on demand.
//
//   lesson [wait=5000]
//       step, values, targets reached, listens heard, the comparison, what plays, the playhead; every take (its view key, rendered,
//       its length and start, its peak, a failure's message); wait= first blocks (wall time) until the takes in flight are done, so a
//       play pressed headless sounds on the next frames (the inspector's frames run faster than the wall clock)
//
// Role: SHELL (diagnostics; needs no mount)

namespace Wavee;

public static partial class Diagnostics
{
    public sealed partial class InspectSession
    {
        /// <summary>`lesson {wait}`.</summary>
        string LessonVerb(int id, string name, Inspect.Args a)
        {
            int wait = a.Int("wait", 0);
            if (wait > 0) MixLessonAudio.WaitForTakes(wait);
            var takes = MixLessonAudio.Takes();
            var v = MixLesson.Values.Peek();
            return Inspect.Reply.Ok(id, name, w =>
            {
                w.WriteNumber("step", MixLesson.Step.Peek());
                w.WriteString("playing", MixLesson.Playing.Peek());
                w.WriteBoolean("sounding", MixLessonAudio.Sounding);
                w.WriteBoolean("pending", MixLessonAudio.Pending);
                w.WriteNumber("playheadMs", float.IsNaN(MixLesson.Playhead.Peek()) ? -1 : MixLesson.Playhead.Peek());
                w.WriteString("compare", MixLesson.Comparing.ToString());
                w.WriteBoolean("peeling", MixLesson.Peeling);
                w.WriteStartObject("values");
                w.WriteNumber("fadeStartBar", Math.Round(MixLesson.BarOf(v.FadeStart), 2));
                w.WriteNumber("swapBar", Math.Round(MixLesson.BarOf(v.SwapAt), 2));
                w.WriteNumber("cutoffHz", Math.Round(v.Cutoff, 1));
                w.WriteNumber("repeats", v.Repeats);
                w.WriteNumber("bpm", v.Bpm);
                w.WriteEndObject();
                w.WriteStartArray("layers");
                for (int l = 0; l < MixLesson.LayerCount; l++)
                {
                    var layer = (MixLessonLayer)l;
                    w.WriteStartObject();
                    w.WriteString("id", MixLesson.Id(layer));
                    w.WriteBoolean("hit", MixLesson.IsHit(layer));
                    w.WriteBoolean("setForYou", MixLesson.IsFilled(layer));
                    w.WriteString("phase", MixLesson.PhaseOf(layer).ToString());
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteStartArray("stepsDone");
                for (int i = 1; i < MixLesson.StepCount; i++) if (MixLesson.StepDone(i)) w.WriteNumberValue(i);
                w.WriteEndArray();
                w.WriteStartArray("takes");
                foreach (var t in takes)
                {
                    w.WriteStartObject();
                    w.WriteString("key", t.Key);
                    w.WriteString("state", t.State);
                    if (t.Seconds > 0)
                    {
                        w.WriteNumber("seconds", Math.Round(t.Seconds, 3));
                        w.WriteNumber("startS", Math.Round(t.Start, 3));
                        w.WriteNumber("peak", Math.Round(t.Peak, 3));
                        w.WriteNumber("renderMs", t.RenderMs);
                    }
                    if (t.Error.Length > 0) w.WriteString("error", t.Error);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            });
        }
    }
}
