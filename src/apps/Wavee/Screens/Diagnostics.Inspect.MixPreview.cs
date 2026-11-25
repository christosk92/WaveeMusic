// ── Screens/Diagnostics.Inspect.MixPreview.cs ─────────────────────────────────────────────────────────────────────────
// `mix show=preview` (#48 M5, Playback.MixPreview): Hear it on a pair of the mixed list, through the live deck and the pump.
//   do=run      start the preview (`from=`, `to=`, `preset=` an unsaved edit: the pair's stored recipe with that preset), then step frames
//               (`every=`, `max=`) until it restored; replies the deck samples, the preview's timeline and the deck before and after
//   do=start | stop (`resume=`) | status | wait (`max=` wall-clock frames, then status)
//   do=mark     the deck and queue now, for a preview started elsewhere (the editor's Hear it, clicked); do=verify then replies
//               `restoredExactly` / `queueRestored` against the mark once the preview has stopped and restored
//   do=envelope the `signal` body this editor would send the owner (another device playing) for the pair, and its parameters decoded back
// `mix show=remote`: the deck's own PutState player half, read back as a foreign owner's cluster (Decode.Cluster): the fades a controller
// reads and the queue line and bar label it then shows (Playback.Mix.NoteRemote), forgotten afterwards.
//
// Role: SHELL (UI thread)

using System.Globalization;

namespace Wavee;

public static partial class Diagnostics
{
    public sealed partial class InspectSession
    {
        /// <summary>`do=mark`'s deck and queue (null: none taken).</summary>
        (Playback.State Deck, int[] Packed, QueueEdge[] Rows)? _previewMark;

        string MixPreviewVerb(int id, string name, Inspect.Args a)
        {
            string uri = a.Str("uri", Entities.LibrarySeed.MixUri);
            var eid = EntityId.Parse(uri.AsSpan());
            if (eid.Kind != EntityKind.Playlist || !Entities.Current.Playlists.TryGetSlot(eid, out int slot))
                return Inspect.Reply.Fail(id, name, "no playlist row for '" + uri + "'");
            string verb = a.Str("do", "status");
            int from = a.Int("from", 0), to = a.Int("to", from + 1);
            MixRecipe? edit = null;
            byte[]? raw = null;
            if (a.Has("preset"))
            {
                // An unsaved edit: the pair's stored recipe (or a fresh one) with the preset picked, as the editor holds it before a save.
                var pair = MixEdit.Read(slot, from);
                var r = MixEdit.ReplacedRecipe(in pair, slot, out raw);
                if (r.IsEmpty && MixEdit.AutoBaseline(in pair) is { } auto) r = auto;
                r.Preset = (byte)a.Int("preset", 1);
                r.IsPresetIdOverride = true;
                edit = r;
            }
            var before = Playback.Snap();
            int[] packedBefore = global::Wavee.Queue.PackedRefs.ToArray();
            QueueEdge[] rowsBefore = global::Wavee.Queue.Rows.ToArray();
            string Deck(in Playback.State s) => s.HasCurrent
                ? s.CurrentId.Text + " @" + s.Position(Playback.FrameNowMs()) + " " + s.Phase.ToString().ToLowerInvariant() + " ctx=" + s.Context.Text + " cursor=" + s.Cursor.Index
                : "-";
            switch (verb)
            {
                case "mark":
                    {
                        _previewMark = (before, packedBefore, rowsBefore);
                        return Inspect.Reply.Ok(id, name, w => { w.WriteString("deck", Deck(in before)); w.WriteNumber("queueRows", rowsBefore.Length); WritePreview(w); });
                    }
                case "verify":
                    {
                        if (_previewMark is not { } mark) return Inspect.Reply.Fail(id, name, "do=mark first");
                        _previewMark = null;
                        return Inspect.Reply.Ok(id, name, w =>
                        {
                            w.WriteString("before", Deck(in mark.Deck));
                            w.WriteString("after", Deck(in before));
                            WriteRestored(w, in mark.Deck, in before, mark.Packed, mark.Rows);
                            WritePreview(w);
                        });
                    }
                case "start":
                    {
                        bool ok = Playback.MixPreview.Start(slot, from, to, edit, raw, a.Bool("loop", false));
                        RunFrames(4);
                        return Inspect.Reply.Ok(id, name, w => { w.WriteBoolean("started", ok); WritePreview(w); });
                    }
                case "wait":
                    {
                        // Wall-clock frames (a Connect preview's deck lands and plays on the wall clock), then where the preview is.
                        RunFramesInRealTime(Math.Clamp(a.Int("max", 120), 1, 6000));
                        return Inspect.Reply.Ok(id, name, w => { w.WriteString("deck", Deck(Playback.Snap())); WritePreview(w); });
                    }
                case "stop":
                    {
                        Playback.MixPreview.Stop(a.Has("resume") ? a.Bool("resume", true) : null);
                        RunFrames(30);
                        return Inspect.Reply.Ok(id, name, w => { w.WriteString("deck", Deck(Playback.Snap())); WritePreview(w); });
                    }
                case "envelope":
                    {
                        var targets = Entities.Current.Edges.PlaylistTracks.Targets(slot);
                        if ((uint)from >= (uint)targets.Length || (uint)to >= (uint)targets.Length) return Inspect.Reply.Fail(id, name, "no such pair");
                        string parameters = Playback.MixPreview.SendParameters(new Playlist(slot), from, to, new Track(targets[from]), new Track(targets[to]),
                            edit, raw, out int recipeBytes);
                        var body = new System.Buffers.ArrayBufferWriter<byte>(1024);
                        Spotify.Connect.MixPreviewSignalBody(body, Spotify.Decode.MixPreviewSignal.Preview, parameters, "<this device>", "<command id>",
                            "<intent id>", 0);
                        var stop = new System.Buffers.ArrayBufferWriter<byte>(512);
                        Spotify.Connect.MixPreviewSignalBody(stop, Spotify.Decode.MixPreviewSignal.Stop, "true", "<this device>", "<command id>", "<intent id>", 0);
                        var kind = Spotify.Decode.MixPreviewCommand(body.WrittenSpan, out var back, out _);
                        var stopKind = Spotify.Decode.MixPreviewCommand(stop.WrittenSpan, out _, out bool resume);
                        MixRecipe.TryDecode(back?.Recipe ?? [], out var recipe);
                        return Inspect.Reply.Ok(id, name, w =>
                        {
                            w.WriteString("body", System.Text.Encoding.UTF8.GetString(body.WrittenSpan));
                            w.WriteString("stopBody", System.Text.Encoding.UTF8.GetString(stop.WrittenSpan));
                            w.WriteNumber("recipeBytes", recipeBytes);
                            w.WriteString("decodedAs", kind.ToString());
                            w.WriteString("stopDecodedAs", stopKind + " resume=" + resume);
                            if (back is not null)
                            {
                                w.WriteString("trackA", back.TrackA);
                                w.WriteString("trackB", back.TrackB);
                                w.WriteString("mode", back.Mode);
                                w.WriteNumber("startPositionMs", back.StartPositionMs);
                                w.WriteBoolean("relativeStartPosition", back.RelativeStart);
                                w.WriteString("contextUri", back.ContextUri);
                                w.WriteNumber("itemSpeedA", back.ItemSpeedA);
                                w.WriteNumber("itemSpeedB", back.ItemSpeedB);
                                WriteRecipe(w, "recipe", in recipe);
                            }
                        });
                    }
                case "run":
                    {
                        bool ok = Playback.MixPreview.Start(slot, from, to, edit, raw, false);
                        if (!ok) return Inspect.Reply.Fail(id, name, "the pair cannot be previewed");
                        int every = Math.Max(1, a.Int("every", 60)), max = a.Int("max", 6000), ran = 0;
                        var samples = new List<string>();
                        string last = "";
                        while (ran < max)
                        {
                            RunFramesInRealTime(every);   // the fake deck's loads and reports land on the wall clock
                            ran += every;
                            var s = Playback.Snap();
                            string row = s.CurrentId.Text + " " + s.Phase.ToString().ToLowerInvariant();
                            string line = string.Create(CultureInfo.InvariantCulture, $"t={ran * 16}ms deck={s.CurrentId.Text} pos={s.Position(Playback.FrameNowMs())} dur={s.DurationMs} {s.Phase.ToString().ToLowerInvariant()} preview={Playback.MixPreview.State.Peek()}");
                            if (row != last || ran % (every * 4) == 0) samples.Add(line);
                            last = row;
                            if (Playback.MixPreview.State.Peek() == Playback.MixPreview.Phase.Idle && Playback.MixPreview.Timeline.Count > 0
                                && Playback.MixPreview.Timeline[^1].What.StartsWith("restored", StringComparison.Ordinal)) break;
                        }
                        var after = Playback.Snap();
                        return Inspect.Reply.Ok(id, name, w =>
                        {
                            w.WriteString("before", Deck(in before));
                            w.WriteString("after", Deck(in after));
                            WriteRestored(w, in before, in after, packedBefore, rowsBefore);
                            w.WriteNumber("frames", ran);
                            w.WriteStartArray("samples");
                            foreach (var l in samples) w.WriteStringValue(l);
                            w.WriteEndArray();
                            WritePreview(w);
                        });
                    }
                default:
                    return Inspect.Reply.Ok(id, name, w => { w.WriteString("deck", Deck(Playback.Snap())); WritePreview(w); });
            }
        }

        /// <summary>Exact: the same row, context, queue index and play state, and the row landed where the preview found it (the timeline's
        /// "restore" names the kept position, "restored" the deck's when the row was back); the queue as it was.</summary>
        static void WriteRestored(System.Text.Json.Utf8JsonWriter w, in Playback.State before, in Playback.State after, int[] packedBefore, QueueEdge[] rowsBefore)
        {
            var tl = Playback.MixPreview.Timeline;
            var kept = tl.LastOrDefault(e => e.What.StartsWith("restore (", StringComparison.Ordinal));
            var landed = tl.LastOrDefault(e => e.What.StartsWith("restored", StringComparison.Ordinal));
            w.WriteBoolean("restoredExactly", landed.What is not null && kept.What is not null && after.CurrentId == before.CurrentId
                && after.Context == before.Context && after.Cursor.Index == before.Cursor.Index && after.Phase == before.Phase
                && Math.Abs(landed.PositionMs - kept.PositionMs) < 250);
            w.WriteBoolean("queueRestored", global::Wavee.Queue.PackedRefs.SequenceEqual(packedBefore) && global::Wavee.Queue.Rows.SequenceEqual(rowsBefore));
            w.WriteNumber("queueRows", rowsBefore.Length);
        }

        static void WritePreview(System.Text.Json.Utf8JsonWriter w)
        {
            w.WriteString("state", Playback.MixPreview.State.Peek().ToString());
            var t = Playback.MixPreview.Timing;
            w.WriteString("plan", t.Mix ? MixSeams.ChipText(in t) + " out=" + t.OutStartMs + " overlap=" + t.OutDurationMs + " in=" + t.InStartMs
                + " inDur=" + t.InDurationMs + " speedB=" + t.SpeedB.ToString("0.######", CultureInfo.InvariantCulture) : t.WhyNot.ToString());
            w.WriteStartArray("timeline");
            foreach (var e in Playback.MixPreview.Timeline)
                w.WriteStringValue(string.Create(CultureInfo.InvariantCulture, $"+{e.AtMs}ms {e.What} track={e.Track} pos={e.PositionMs} deck={e.Deck}"));
            w.WriteEndArray();
        }

        /// <summary>`mix show=remote`: the deck's PutState player half as a foreign owner's cluster.</summary>
        string MixRemoteVerb(int id, string name)
        {
            Playback.Snapshot snap = Playback.SnapshotForConnect(Playback.PublishReason.PlayerStateChanged);
            byte[] body = new byte[Spotify.Decode.PutStateCapacity(in snap)];
            int n = Spotify.Decode.PutState(in snap, body, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - Environment.TickCount64);
            ReadOnlySpan<byte> device = new Spotify.Decode.ProtoReader(body.AsSpan(0, Math.Max(0, n))).Bytes(2);
            ReadOnlySpan<byte> player = new Spotify.Decode.ProtoReader(device).Bytes(2);
            if (player.IsEmpty) return Inspect.Reply.Fail(id, name, "the deck publishes no player state (play a row of the mixed list first)");
            // Cluster { 2 active_device_id, 3 player_state }: the owner is a made-up device.
            byte[] cluster = new byte[player.Length + 64];
            var w0 = new Spotify.Decode.ProtoWriter(cluster);
            w0.Str(2, "fake-owner-device");
            w0.Tag(3, 2);
            w0.Var((ulong)player.Length);
            w0.Raw(player);
            var buffer = Spotify.Decode.ClusterBuffer.Rent();
            try
            {
                var d = Spotify.Decode.Cluster(cluster.AsSpan(0, w0.Length), "this-device"u8, buffer);
                EntityId track = d.HasTrack ? EntityId.Parse(buffer.Utf8(d.Track.Uri)) : default;
                Playback.Mix.NoteRemote(buffer, in d, track);
                string line = Playback.Mix.QueueLine(), label = Playback.Mix.LabelWhilePlanned() ?? "";
                var remote = Playback.Mix.Remote;
                Playback.Mix.ForgetRemote();
                return Inspect.Reply.Ok(id, name, w =>
                {
                    w.WriteString("track", track.Text);
                    w.WriteNumber("fadeOutStartMs", d.FadeOutStartMs);
                    w.WriteNumber("fadeOutDurationMs", d.FadeOutDurationMs);
                    w.WriteNumber("fadeInStartMs", d.FadeInStartMs);
                    w.WriteNumber("fadeInDurationMs", d.FadeInDurationMs);
                    w.WriteNumber("durationMs", d.DurationMs);
                    w.WriteString("mixOffered", ((d.MixOffered & 1) != 0 ? "automix-preview " : "") + ((d.MixOffered & 2) != 0 ? "stop-automix-preview" : ""));
                    w.WriteString("nextTitle", remote.NextTitle);
                    w.WriteString("queueLine", line);
                    w.WriteString("barLabel", label);
                    w.WriteString("queueLineAfterForget", Playback.Mix.QueueLine());
                });
            }
            finally { Spotify.Decode.ClusterBuffer.Return(buffer); }
        }
    }
}
