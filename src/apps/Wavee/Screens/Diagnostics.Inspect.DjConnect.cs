// ── Screens/Diagnostics.Inspect.DjConnect.cs ──────────────────────────────────────────────────────────────────────────
// the `dj` verb's Connect half (AI DJ, plan-dj D4): what Wavee sends and reads about a DJ over Connect, headless.
//
//   dj do=envelope [uri=]   the `play` body a DJ start (or any context's start) sends the device that owns playback, through the
//                           one send's own shaping and writer, with fixed ids and clock (nothing is sent)
//   dj do=cluster file=     ClusterUpdates (base64 per line, gzipped or not; `#` comments) folded as dealer pushes, one per drain,
//                           then the mirror: the owner, its context, the toggle locks and the tips the buttons wear, may seek, may
//                           transfer, its narration (flag, slot, caption); `try=1` then asks for shuffle, repeat and "play here" and
//                           reads back that nothing changed
//
// Role: SHELL (UI thread)

using System.Text.Json;

namespace Wavee;

public static partial class Diagnostics
{
    public sealed partial class InspectSession
    {
        /// <summary>The Connect sub-verbs' names, as the verb table lists them.</summary>
        internal const string DjConnectVerbs = "envelope|cluster";

        /// <summary>The Connect sub-verbs of `dj`; null when <paramref name="verb"/> is not one of them.</summary>
        string? DjConnectVerb(int id, string name, string verb, string uri, Inspect.Args a)
        {
            switch (verb)
            {
                case "envelope":
                    {
                        if (!EntityId.TryParse(uri.AsSpan(), out EntityId context)) return Inspect.Reply.Fail(id, name, "not a uri: " + uri);
                        string body = Playback.ForwardEnvelopeForInspect(context);
                        return Inspect.Reply.Ok(id, name, w =>
                        {
                            w.WriteString("do", verb);
                            w.WriteString("uri", uri);
                            w.WritePropertyName("envelope");
                            using JsonDocument doc = JsonDocument.Parse(body);
                            doc.RootElement.WriteTo(w);
                        });
                    }
                case "cluster":
                    {
                        string file = a.Str("file");
                        if (file.Length == 0 || !File.Exists(file)) return Inspect.Reply.Fail(id, name, "dj do=cluster wants file=<base64 ClusterUpdate per line>");
                        int folded = 0;
                        foreach (string line in File.ReadAllLines(file))
                        {
                            if (line.Trim().Length == 0 || line.StartsWith('#')) continue;
                            byte[] raw = Convert.FromBase64String(line.Trim());
                            if (raw.Length > 1 && raw[0] == 0x1f && raw[1] == 0x8b)
                            {
                                using var gz = new System.IO.Compression.GZipStream(new MemoryStream(raw), System.IO.Compression.CompressionMode.Decompress);
                                using var plain = new MemoryStream();
                                gz.CopyTo(plain);
                                raw = plain.ToArray();
                            }
                            Playback.FoldClusterForInspect(raw);
                            RunFrames(2);
                            folded++;
                        }
                        RunFrames(2);
                        Playback.State before = Playback.Snap();
                        if (a.Has("try"))
                        {
                            Playback.SetShuffle(!before.Shuffle);
                            Playback.SetRepeat(Spotify.Decode.RepeatMode.Context);
                            Playback.TakeOver();
                            RunFrames(3);
                        }
                        return Inspect.Reply.Ok(id, name, w =>
                        {
                            w.WriteString("do", verb);
                            w.WriteNumber("folded", folded);
                            WriteDjMirror(w, "mirror", before);
                            if (a.Has("try")) WriteDjMirror(w, "afterTry", Playback.Snap());
                        });
                    }
                default:
                    return null;
            }
        }

        /// <summary>What Wavee shows of a foreign owner's DJ (the reducer's mirror and the signals the surfaces read).</summary>
        static void WriteDjMirror(Utf8JsonWriter w, string key, Playback.State s)
        {
            w.WriteStartObject(key);
            w.WriteString("owner", s.Owner.ToString());
            w.WriteString("context", s.Context.IsEmpty ? "" : s.Context.Text);
            w.WriteString("deck", s.CurrentId.IsEmpty ? "" : s.CurrentId.Text);
            w.WriteBoolean("shuffle", s.Shuffle);
            w.WriteString("repeat", s.Repeat.ToString());
            w.WriteString("locks", s.LiveLocks.ToString());
            w.WriteString("lockReason", s.LiveLockReason.ToString());
            w.WriteString("shuffleTip", PlayerVerbs.ShuffleLock(track: false) ?? "");
            w.WriteString("repeatTip", PlayerVerbs.RepeatLock(track: false) ?? "");
            w.WriteBoolean("canSeek", Playback.CanSeek.Peek());
            w.WriteBoolean("canTransfer", Playback.CanTransfer.Peek());
            w.WriteBoolean("remoteNarrating", s.RemoteNarrating);
            Playback.Dj.NarrationFacts n = Playback.Dj.RemoteNarration.Peek();
            w.WriteStartObject("narration");
            w.WriteBoolean("active", n.Active);
            w.WriteString("slot", n.Slot ?? "");
            w.WriteString("ssml", n.Ssml ?? "");
            w.WriteString("text", n.Text);
            w.WriteEndObject();
            w.WriteEndObject();
        }
    }
}
