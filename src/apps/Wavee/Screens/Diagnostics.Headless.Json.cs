// ── Screens/Diagnostics.Headless.Json.cs ───────────────────────────────────────────────────────────────────────────
// the headless JSON-lines writers (boot, session, state, status, reply, step, stats, seek, gapless, underrun, end,
// verdict, fault, echo)
//
// Role: CORE
// Owner: X (split by owner S, Wave 6 — a pure move out of Diagnostics.Headless.cs, gap G-016)
// Wave: now
// Budget: 250 lines
// Spec: docs/plans/wavee/wavee-0.3-headless-implementation.md §2.5
//
// The named partial `Diagnostics.Headless.cs` needed (1,145 against its 700): the writers moved here verbatim, and the
// shape is still pinned by HeadlessTests. One reusable Utf8JsonWriter per thread, no DOM, no reflection.

using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Wavee;

public static partial class Diagnostics
{
    public static partial class Headless
    {
        // ── 6. JSON lines (§2.5) — the shape is pinned by a test, the writer never builds a DOM ─────────────────────
        public static class JsonLine
        {
            static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            [ThreadStatic] static ArrayBufferWriter<byte>? t_buffer;
            [ThreadStatic] static Utf8JsonWriter? t_writer;

            static Utf8JsonWriter Begin(long t, string kind)
            {
                ArrayBufferWriter<byte> buffer = t_buffer ??= new ArrayBufferWriter<byte>(512);
                buffer.ResetWrittenCount();
                Utf8JsonWriter w = t_writer ??= new Utf8JsonWriter(buffer, WriterOptions);
                w.Reset(buffer);
                w.WriteStartObject();
                w.WriteNumber("t", t);
                w.WriteString("kind", kind);
                return w;
            }

            static string End(Utf8JsonWriter w)
            {
                w.WriteEndObject();
                w.Flush();
                return Encoding.UTF8.GetString(t_buffer!.WrittenSpan);
            }

            static float Finite(float v) => float.IsFinite(v) ? v : 0f;

            /// <summary>The account is REDACTED here, whatever the caller passed (`Platform.Redact`); the credential is its
            /// scheme tag only.</summary>
            public static string Boot(long t, string profile, string scheme, string account, bool store, string endpoint)
            {
                var w = Begin(t, "boot");
                w.WriteString("profile", profile ?? "");
                w.WriteString("credential", scheme ?? "");
                w.WriteString("account", Platform.Redact(account));
                w.WriteBoolean("store", store);
                w.WriteString("endpoint", endpoint ?? "");
                return End(w);
            }

            public static string Session(long t, string phase, string tier = "", string country = "", string fault = "")
            {
                var w = Begin(t, "session");
                w.WriteString("phase", phase ?? "");
                if (!string.IsNullOrEmpty(tier)) w.WriteString("tier", tier);
                if (!string.IsNullOrEmpty(country)) w.WriteString("country", country);
                if (!string.IsNullOrEmpty(fault) && fault != "None") w.WriteString("fault", fault);
                return End(w);
            }

            public static string State(long t, in StatusSnapshot s)
            {
                var w = Begin(t, "state");
                WritePlayback(w, in s);
                return End(w);
            }

            /// <summary>The `status` answer: the session and the playback in one line (go-librespot's `/status` shape).</summary>
            public static string Status(long t, in StatusSnapshot s, int id = -1)
            {
                var w = Begin(t, "status");
                if (id >= 0) w.WriteNumber("id", id);
                w.WriteString("session", s.SessionPhase ?? "");
                if (!string.IsNullOrEmpty(s.SessionFault) && s.SessionFault != "None") w.WriteString("sessionFault", s.SessionFault);
                w.WriteString("tier", s.Tier ?? "");
                w.WriteString("country", s.Country ?? "");
                WritePlayback(w, in s);
                w.WriteString("owner", s.Owner ?? "");
                w.WriteNumber("volume", Finite(s.Volume));
                w.WriteNumber("loadEpoch", s.LoadEpoch);
                w.WriteBoolean("prefetched", s.PrepareArmed);
                return End(w);
            }

            static void WritePlayback(Utf8JsonWriter w, in StatusSnapshot s)
            {
                w.WriteString("phase", s.Phase ?? "");
                w.WriteString("track", s.TrackUri ?? "");
                w.WriteNumber("pos", s.PositionMs);
                w.WriteNumber("dur", s.DurationMs);
                w.WriteString("format", s.Format ?? "");
                w.WriteBoolean("buffering", s.Buffering);
                if (!string.IsNullOrEmpty(s.Fault) && s.Fault != "None") w.WriteString("fault", s.Fault);
            }

            public static string Reply(long t, int id, bool ok, string cmd, string error = "")
            {
                var w = Begin(t, "reply");
                w.WriteNumber("id", id);
                w.WriteBoolean("ok", ok);
                w.WriteString("cmd", cmd ?? "");
                if (!string.IsNullOrEmpty(error)) w.WriteString("error", error);
                return End(w);
            }

            public static string Step(long t, int n, bool ok, string cmd, long elapsedMs, int code, string reason = "", bool soft = false, int line = 0)
            {
                var w = Begin(t, "step");
                w.WriteNumber("n", n);
                if (line > 0) w.WriteNumber("line", line);
                w.WriteBoolean("ok", ok);
                if (soft) w.WriteBoolean("soft", true);
                w.WriteString("cmd", cmd ?? "");
                w.WriteNumber("elapsedMs", elapsedMs);
                if (!ok) w.WriteNumber("code", code);
                if (!string.IsNullOrEmpty(reason)) w.WriteString("reason", reason);
                return End(w);
            }

            public static string Step(long t, in StepResult r) => Step(t, r.N, r.Ok, r.Cmd, r.ElapsedMs, r.Code, r.Reason, r.Soft, r.Line);

            /// <summary>The counters, as DELTAS against <paramref name="mark"/> where a delta is the honest number (requests,
            /// xruns, gapless joins) and as the live value where it is a level (in flight, ping, throughput).</summary>
            public static string Stats(long t, in StatusSnapshot s, in StatusSnapshot mark, int id = -1)
            {
                var w = Begin(t, "stats");
                if (id >= 0) w.WriteNumber("id", id);
                w.WriteStartObject("cdn");
                w.WriteNumber("requests", s.CdnRequests - mark.CdnRequests);
                w.WriteNumber("cancelled", s.CdnCancelled - mark.CdnCancelled);
                w.WriteNumber("inflight", s.CdnInFlight);
                w.WriteNumber("peakInFlight", s.CdnPeakInFlight);
                w.WriteNumber("pingMs", s.CdnPingMs);
                w.WriteNumber("kbps", s.CdnBytesPerSecond * 8 / 1000);
                w.WriteNumber("headBytes", s.HeadBytes);
                w.WriteNumber("heads", s.CdnHeads - mark.CdnHeads);
                w.WriteNumber("resolves", s.CdnResolves - mark.CdnResolves);
                w.WriteNumber("cacheHits", s.CdnCacheHits - mark.CdnCacheHits);
                w.WriteEndObject();
                w.WriteStartObject("ring");
                w.WriteNumber("waits", s.RingWaits - mark.RingWaits);
                w.WriteNumber("starves", s.RingStarves - mark.RingStarves);
                w.WriteEndObject();
                w.WriteStartObject("audio");
                w.WriteNumber("xruns", s.Xruns - mark.Xruns);
                w.WriteNumber("firstAudioMs", s.FirstAudioMs);
                w.WriteString("firstAudioFrom", s.FirstAudioFromHead ? "head" : "body");
                w.WriteNumber("seeks", s.SeekCount - mark.SeekCount);
                w.WriteStartObject("lastSeek");
                w.WriteNumber("toMs", s.LastSeekMs);
                w.WriteNumber("latencyMs", s.LastSeekLatencyMs);
                w.WriteString("kind", SeekKindName(s.LastSeekKind));
                w.WriteEndObject();
                w.WriteStartObject("gapless");
                w.WriteNumber("exact", s.GaplessExact - mark.GaplessExact);
                w.WriteNumber("degraded", s.GaplessDegraded - mark.GaplessDegraded);
                w.WriteNumber("abandoned", s.GaplessAbandoned - mark.GaplessAbandoned);
                w.WriteEndObject();
                w.WriteNumber("decodeXRealtime", Finite(s.DecodeXRealtime));
                w.WriteEndObject();
                return End(w);
            }

            public static string Seek(long t, in StatusSnapshot s)
            {
                var w = Begin(t, "seek");
                w.WriteNumber("to", s.LastSeekMs);
                w.WriteNumber("latencyMs", s.LastSeekLatencyMs);
                w.WriteString("seekKind", SeekKindName(s.LastSeekKind));
                w.WriteString("track", s.TrackUri ?? "");
                return End(w);
            }

            public static string Gapless(long t, in StatusSnapshot now, in StatusSnapshot previous)
            {
                var w = Begin(t, "gapless");
                w.WriteNumber("exact", now.GaplessExact - previous.GaplessExact);
                w.WriteNumber("degraded", now.GaplessDegraded - previous.GaplessDegraded);
                w.WriteString("track", now.TrackUri ?? "");
                return End(w);
            }

            public static string Underrun(long t, int total, int delta)
            {
                var w = Begin(t, "underrun");
                w.WriteNumber("xruns", total);
                w.WriteNumber("new", delta);
                return End(w);
            }

            public static string EndOfQueue(long t, string track)
            {
                var w = Begin(t, "end");
                w.WriteString("track", track ?? "");
                return End(w);
            }

            public static string Verdict(long t, bool ok, int steps, int failed, int code, int softFailed = 0)
            {
                var w = Begin(t, "verdict");
                w.WriteBoolean("ok", ok);
                w.WriteNumber("steps", steps);
                w.WriteNumber("failed", failed);
                if (softFailed > 0) w.WriteNumber("softFailed", softFailed);
                w.WriteNumber("code", code);
                w.WriteString("meaning", ExitCode.Describe(code));
                return End(w);
            }

            public static string Fault(long t, string reason, string hint)
            {
                var w = Begin(t, "fault");
                w.WriteString("reason", reason ?? "");
                if (!string.IsNullOrEmpty(hint)) w.WriteString("hint", hint);
                return End(w);
            }

            /// <summary>An echoed Log ring entry (`--echo-log`): the `audio.*` timeline lines are the measurements until the
            /// counter seams carry every number (§2.7).</summary>
            public static string Echo(long t, string category, string line)
            {
                var w = Begin(t, "echo");
                w.WriteString("category", category ?? "");
                w.WriteString("line", line ?? "");
                return End(w);
            }
        }
    }
}
