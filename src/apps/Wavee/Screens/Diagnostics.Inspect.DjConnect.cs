// ── Screens/Diagnostics.Inspect.DjConnect.cs ──────────────────────────────────────────────────────────────────────────
// the `dj` verb's Connect half (AI DJ, plan-dj D4): what Wavee sends and reads about a DJ over Connect, headless.
//
//   dj do=envelope [uri=]   the `play` body a DJ start (or any context's start) sends the device that owns playback, through the
//                           one send's own shaping and writer, with fixed ids and clock (nothing is sent)
//   dj do=cluster file=     ClusterUpdates (base64 per line, gzipped or not; `#` comments) folded as dealer pushes, one per drain,
//                           then the mirror: the owner, its context, the toggle locks and the tips the buttons wear, may seek, may
//                           transfer, its narration (flag, slot, caption); `try=1` then asks for shuffle, repeat and "play here" and
//                           reads back that nothing changed
//   dj do=transfer file=    a controller's `transfer` of a DJ into Wavee (the command body, JSON with its base64 TransferState),
//                           handed to the host as the dealer glue does; waits for the session, then the `dj` readout
//   dj do=restore track= [ms=] [uri=]   the launch restore of a deck left on the DJ list (the queue forgotten first, as at boot; the
//                           point's shuffle on and repeat all, to show the DJ holding them); waits for the seed, then the readout
//   dj do=bodies            the --fake session answer and its first page sent through the lexicon decode (Playback.Dj.Decode) as an
//                           identity body, a gzip body, a zstd frame with its content size, a streamed zstd frame without one, and a
//                           broken zstd frame: what each decodes to (or that it is refused)
//   dj do=signal            a controller's "Let DJ pick" arriving: a made-up `signal` request (`signal_id: jump`, from an invented
//                           phone) decoded and routed as the dealer glue routes it (no ack); waits for the jump, then the cursor,
//                           the rows and the `dj` readout. Beside it, `outbound`: the `signal` body this device sends as the controller
//                           of another device's DJ (fixed ids and clock)
//
// Role: SHELL (UI thread)

using System.Text.Json;

namespace Wavee;

public static partial class Diagnostics
{
    public sealed partial class InspectSession
    {
        /// <summary>The Connect sub-verbs' names, as the verb table lists them.</summary>
        internal const string DjConnectVerbs = "envelope|cluster|transfer|restore|bodies|signal";

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
                case "transfer":
                    {
                        if (_shell is null) return Inspect.Reply.Fail(id, name, "dj do=transfer needs a mount");
                        string file = a.Str("file");
                        if (file.Length == 0 || !File.Exists(file)) return Inspect.Reply.Fail(id, name, "dj do=transfer wants file=<a transfer command body (JSON)>");
                        byte[] body = File.ReadAllBytes(file);
                        Spotify.Decode.RemoteCommand command = Spotify.Decode.ConnectCommand(body);
                        if (command.Kind != Spotify.Decode.RemoteCmd.Transfer) return Inspect.Reply.Fail(id, name, "dj do=transfer: the body is " + command.Kind);
                        Spotify.Decode.ClusterBuffer buffer = Spotify.Decode.ClusterBuffer.Rent();
                        Spotify.Decode.RemoteLoad load = Spotify.Decode.ConnectLoad(body, buffer);
                        Playback.RemoteLoadArrived(in command, in load, buffer, Spotify.Current.Epoch);
                        long until = Environment.TickCount64 + PlayLoadBudgetMs;
                        while (Playback.Dj.Current is null && Environment.TickCount64 < until) { RunFrames(1); Thread.Sleep(2); }
                        string phase = AwaitPlaybackLoad(PlayLoadBudgetMs);
                        return Settled(id, name, a, w => { w.WriteString("do", verb); w.WriteString("phase", phase); WriteDjSession(w); });
                    }
                case "restore":
                    {
                        if (_shell is null) return Inspect.Reply.Fail(id, name, "dj do=restore needs a mount");
                        if (!EntityId.TryParse(uri.AsSpan(), out EntityId list)) return Inspect.Reply.Fail(id, name, "not a uri: " + uri);
                        if (!EntityId.TryParse(a.Str("track").AsSpan(), out EntityId track) || !track.IsPlayable)
                            return Inspect.Reply.Fail(id, name, "dj do=restore wants track=spotify:track:...");
                        global::Wavee.Queue.Clear();                 // as at boot: no queue yet, the seed decides it
                        Playback.Restore(new Playback.RestorePoint(track, list, -1, Math.Max(0, a.Int("ms", 30_000)), 0, Shuffle: true,
                            Repeat: Spotify.Decode.RepeatMode.Context));
                        long until = Environment.TickCount64 + PlayLoadBudgetMs;
                        while ((Playback.Dj.Current is null || global::Wavee.Queue.State == EdgeState.Unknown) && Environment.TickCount64 < until)
                        { RunFrames(1); Thread.Sleep(2); }
                        RunFrames(2);
                        return Settled(id, name, a, w => { w.WriteString("do", verb); w.WriteString("track", track.Text); WriteDjSession(w); });
                    }
                case "bodies":
                    {
                        string sessionPath = Playback.RemotePlan.PageRoute(Entities.LibrarySeed.DjLexiconUrl) + "&reason=interactive";
                        byte[]? session = Entities.FakeLexicon(sessionPath);
                        if (session is null) return Inspect.Reply.Fail(id, name, "dj do=bodies: the fake lexicon service did not answer");
                        Spotify.Decode.ClusterBuffer probe = Spotify.Decode.ClusterBuffer.Rent();
                        string pageUrl;
                        try { pageUrl = System.Text.Encoding.UTF8.GetString(probe.Utf8(Spotify.Decode.ContextResolve(session, probe).NextPageUrl)); }
                        finally { Spotify.Decode.ClusterBuffer.Return(probe); }
                        byte[]? page = Entities.FakeLexicon(Playback.RemotePlan.PageRoute(pageUrl));
                        if (page is null) return Inspect.Reply.Fail(id, name, "dj do=bodies: the fake lexicon service did not answer the page");
                        return Inspect.Reply.Ok(id, name, w =>
                        {
                            w.WriteString("do", verb);
                            w.WriteStartArray("bodies");
                            foreach ((string answer, byte[] plain, int djPage) in new[] { ("session", session, 0), ("page 1", page, 1) })
                                foreach ((string sent, byte[] body) in Encodings(plain))
                                    WriteBodyDecode(w, answer, sent, body, djPage);
                            w.WriteEndArray();
                        });
                    }
                case "signal":
                    {
                        if (_shell is null) return Inspect.Reply.Fail(id, name, "dj do=signal needs a mount");
                        // The request as the dealer delivers it; every id is invented (no capture, no device of the account).
                        const string request = "{\"message_id\":91,\"sent_by_device_id\":\"fa4ed00d00000000000000000000phone\","
                            + "\"message_ident\":\"hm://connect-state/v1/player/command\",\"command\":{\"endpoint\":\"signal\",\"signal_id\":\"jump\","
                            + "\"logging_params\":{\"command_id\":\"00000000000000000000000000000091\"}}}";
                        byte[] body = System.Text.Encoding.UTF8.GetBytes(request);
                        Spotify.Decode.RemoteCommand command = Spotify.Decode.ConnectCommand(body);
                        if (command.Kind != Spotify.Decode.RemoteCmd.Signal) return Inspect.Reply.Fail(id, name, "dj do=signal: the body decoded as " + command.Kind);
                        Playback.State before = Playback.Snap();
                        int jumps = Playback.Dj.Jumps;
                        Spotify.Connect.RouteCommand(in command, body, Spotify.Current.Epoch);
                        long until = Environment.TickCount64 + PlayLoadBudgetMs;
                        while (Playback.Dj.Jumps == jumps && Environment.TickCount64 < until) { RunFrames(1); Thread.Sleep(2); }
                        AwaitPlaybackLoad(PlayLoadBudgetMs);
                        RunFrames(4);
                        Playback.State after = Playback.Snap();
                        var outbound = new System.Buffers.ArrayBufferWriter<byte>(512);
                        Spotify.Connect.SignalBody(outbound, Spotify.Decode.PlayerSignal.Jump, "inspect-device", "00000000000000000000000000000001",
                            "00000000000000000000000000000002", 1_700_000_000_000);
                        return Settled(id, name, a, w =>
                        {
                            w.WriteString("do", verb);
                            w.WriteString("signal", Spotify.Decode.SignalId(command.Signal));
                            w.WriteBoolean("jumped", Playback.Dj.Jumps > jumps);
                            w.WriteNumber("cursorBefore", before.Cursor.Index);
                            w.WriteNumber("cursorAfter", after.Cursor.Index);
                            w.WritePropertyName("outbound");
                            using (JsonDocument doc = JsonDocument.Parse(outbound.WrittenMemory)) doc.RootElement.WriteTo(w);
                            WriteDjSession(w);
                        });
                    }
                default:
                    return null;
            }
        }

        /// <summary>One answer as each body a server may send: as it is, gzip, a zstd frame with its content size (the one-shot form), a
        /// streamed zstd frame without one, and a zstd frame cut short.</summary>
        static IEnumerable<(string Sent, byte[] Body)> Encodings(byte[] plain)
        {
            yield return ("identity", plain);
            using (var gz = new MemoryStream())
            {
                using (var z = new System.IO.Compression.GZipStream(gz, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true)) z.Write(plain);
                yield return ("gzip", gz.ToArray());
            }
            byte[] sized;
            using (var compressor = new ZstdSharp.Compressor()) sized = compressor.Wrap(plain).ToArray();
            yield return ("zstd (content size)", sized);
            using (var streamed = new MemoryStream())
            {
                using (var z = new ZstdSharp.CompressionStream(streamed, leaveOpen: true)) z.Write(plain);
                yield return ("zstd (streamed, no content size)", streamed.ToArray());
            }
            yield return ("zstd (cut short)", sized[..Math.Max(5, sized.Length / 2)]);
        }

        /// <summary>One body through the lexicon decode: refused, or what it decoded to.</summary>
        static void WriteBodyDecode(Utf8JsonWriter w, string answer, string sent, byte[] body, int djPage)
        {
            Spotify.Decode.ClusterBuffer buffer = Spotify.Decode.ClusterBuffer.Rent();
            try
            {
                w.WriteStartObject();
                w.WriteString("answer", answer);
                w.WriteString("sent", sent);
                w.WriteString("form", Playback.Dj.BodyForm(body));
                w.WriteNumber("bytes", body.Length);
                bool ok = Playback.Dj.Decode(body, buffer, djPage, "dj bodies " + answer, out Spotify.Decode.ContextPage page);
                w.WriteBoolean("decoded", ok);
                if (ok)
                {
                    w.WriteString("uri", System.Text.Encoding.UTF8.GetString(buffer.Utf8(page.Uri)));
                    w.WriteBoolean("dj", page.Dj);
                    w.WriteString("noShuffle", System.Text.Encoding.UTF8.GetString(buffer.Utf8(page.NoShuffle)));
                    w.WriteNumber("tracks", page.TrackCount);
                    int djKeys = 0;
                    foreach (ref readonly Spotify.Decode.ClusterTrack t in buffer.Tracks(page.TrackStart, page.TrackCount)) djKeys += t.DjCount;
                    w.WriteNumber("djKeys", djKeys);
                    w.WriteString("segment", System.Text.Encoding.UTF8.GetString(buffer.Utf8(page.Segment)));
                    w.WriteBoolean("nextPage", !page.NextPageUrl.IsEmpty);
                }
                w.WriteEndObject();
            }
            finally { Spotify.Decode.ClusterBuffer.Return(buffer); }
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
