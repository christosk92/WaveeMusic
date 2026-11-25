// ── Screens/Diagnostics.Inspect.Transport.cs ─────────────────────────────────────────────────────────────────────────────
// the `transport` verb: drive the playback sequencer the way the transport buttons, the natural end and the queue verbs do, and read
// back what the deck is on and what will play next
//
// Role: SHELL (UI thread; posts the same inputs the player bar and the queue verbs post)
// Spec: issue #215 (skip / previous / repeat / shuffle / end of context / autoplay). Diagnostics only: no assertion lives here, the
// agent compares the readout against the rule table itself.
//
// THE READOUT. Every reply carries the deck (`deck`, its cursor), the phase, repeat, shuffle and the Autoplay setting, then the queue
// as the reducer sees it (`rows`: one token per row, `<bucket><provider>:<id>`, bucket H history · > the deck · U user queue · N next up,
// provider c context · q queued · a autoplay, `!` for a row the deck may not land on) and as the queue panel lists it (`upNext`, the
// panel model's rows in display order, `<section>:<id>`, section q · n · a). A sequence of `do=next` or `do=end` replies IS the
// sequence of track ids played, which is what the case table in the lane's CASES.md records.

using System.Text.Json;

namespace Wavee;

public static partial class Diagnostics
{
    public sealed partial class InspectSession
    {
        /// <summary>`transport {do, value?, uri?, + the settle args}`.</summary>
        string Transport(int id, string name, Inspect.Args a)
        {
            string verb = a.Str("do").ToLowerInvariant();
            string value = a.Str("value").ToLowerInvariant();
            switch (verb)
            {
                case "status": break;
                case "next": Playback.Next("inspect"); break;
                case "prev": Playback.Previous("inspect"); break;
                case "end": Playback.EndNow(); break;
                case "pause": Playback.Pause("inspect"); break;
                case "resume": Playback.Resume("inspect"); break;
                case "seek": Playback.SeekTo(Math.Max(0, a.Int("ms", 0))); break;
                case "shuffle": Playback.SetShuffle(value is "on" or "true" or "1"); break;
                case "repeat":
                    {
                        Spotify.Decode.RepeatMode mode = value switch
                        {
                            "all" or "context" => Spotify.Decode.RepeatMode.Context,
                            "one" or "track" => Spotify.Decode.RepeatMode.Track,
                            _ => Spotify.Decode.RepeatMode.Off,
                        };
                        Playback.SetRepeat(mode);
                        break;
                    }
                case "add":
                case "playnext":
                    {
                        if (!EntityId.TryParse(a.Str("uri").AsSpan(), out EntityId track)) return Inspect.Reply.Fail(id, name, "add/playnext want uri=spotify:track:...");
                        Span<EntityRef> one = [Entities.Ref(track)];
                        var cursor = Playback.Snap().Cursor;
                        if (verb == "add") global::Wavee.Queue.AddToQueue(one, in cursor);
                        else global::Wavee.Queue.PlayNext(one, in cursor);
                        break;
                    }
                case "clear":
                    {
                        var cursor = Playback.Snap().Cursor;
                        global::Wavee.Queue.ClearUserQueue(in cursor);
                        break;
                    }
                case "duration":
                    // The decoder reports the playing row's real length (Playback.ReportDuration on the current load): the same row's duration
                    // changes, what a music video's length replacing the song's does to the seek rail (its rescale ease).
                    Playback.ReportDuration(Playback.Snap().LoadEpoch, Math.Max(1, a.Int("ms", 0)));
                    break;
                case "dead":
                    {
                        // The catalog rules a track terminally unavailable (withdrawn, region-locked), as an extended-metadata
                        // envelope with no entity does: the walk then steps past it.
                        if (!EntityId.TryParse(a.Str("uri").AsSpan(), out EntityId track)) return Inspect.Reply.Fail(id, name, "dead wants uri=spotify:track:...");
                        Entities.RuleUnplayable(track);
                        break;
                    }
                case "metadata":
                    {
                        // A CAPTURED extended-metadata response (the raw protobuf body, decompressed), committed through the real decoder
                        // as the wire's answer would land, then every TRACK_V4 entity's verdict read back two ways: the row's (what the
                        // list greys and states) and the ladder's (what a play attempt does, Spotify.Audio.WithheldIn). `market=` stands in
                        // for the session's market while it decodes (the fake session has none).
                        string file = a.Str("file");
                        if (file.Length == 0 || !File.Exists(file)) return Inspect.Reply.Fail(id, name, "metadata wants file=<an extended-metadata response body (protobuf)>");
                        byte[] body = File.ReadAllBytes(file);
                        string market = a.Str("market").ToUpperInvariant(), was = Spotify.Api.Market;
                        Spotify.Api.Market = market;
                        try { Entities.CommitMetadata(body); }
                        finally { Spotify.Api.Market = was; }
                        RunFrames(2);
                        long now = Track.NowUnix;
                        return Settled(id, name, a, w =>
                        {
                            w.WriteString("do", verb);
                            w.WriteString("market", market);
                            w.WriteStartArray("tracks");
                            if (body.Length > 0 && body[0] == (byte)'{')
                            {
                                // a Pathfinder answer: every track node that carries a `playability`, in document order (no ladder: no TRACK_V4)
                                var seen = new HashSet<string>(StringComparer.Ordinal);
                                using var doc = JsonDocument.Parse(body);
                                foreach (string uri in PlayabilityUris(doc.RootElement))
                                    if (seen.Add(uri)) WriteVerdict(w, uri, null, now);
                            }
                            else
                            {
                                var parsed = Wavee.Protocol.ExtendedMetadata.BatchedExtensionResponse.Parser.ParseFrom(body);
                                foreach (var array in parsed.ExtendedMetadata)
                                {
                                    if (array.ExtensionKind != Wavee.Protocol.ExtendedMetadata.ExtensionKind.TrackV4) continue;
                                    foreach (var entity in array.ExtensionData)
                                    {
                                        Spotify.Audio.Withheld ladder = entity.ExtensionData is { } any
                                            ? Spotify.Audio.WithheldIn(Wavee.Protocol.Metadata.Track.Parser.ParseFrom(any.Value), market)
                                            : Spotify.Audio.Withheld.None;
                                        WriteVerdict(w, entity.EntityUri, ladder.ToString(), now);
                                    }
                                }
                            }
                            w.WriteEndArray();
                        });
                    }
                case "audiobook":
                    {
                        // The catalogue classifies a show as a book (its list header's is_audiobook), as no fake show is.
                        if (!EntityId.TryParse(a.Str("uri").AsSpan(), out EntityId show)) return Inspect.Reply.Fail(id, name, "audiobook wants uri=spotify:show:...");
                        Entities.RuleAudiobook(show);
                        break;
                    }
                case "putstate":
                    {
                        // The connect-state PUT body this deck would send now (Playback.SnapshotForConnect through the real encoder), as
                        // base64 for an offline decode (ops/tools/capture/proto_dump.py: PUT_STATE_REQUEST). Nothing is sent: the inspector
                        // has no Connect session.
                        Playback.Snapshot snap = Playback.SnapshotForConnect(Playback.PublishReason.PlayerStateChanged);
                        byte[] body = new byte[Spotify.Decode.PutStateCapacity(in snap)];
                        int n = Spotify.Decode.PutState(in snap, body, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - Environment.TickCount64);
                        string b64 = Convert.ToBase64String(body, 0, Math.Max(0, n));
                        return Settled(id, name, a, w =>
                        {
                            w.WriteString("do", verb);
                            w.WriteNumber("bytes", n);
                            w.WriteBoolean("audiobook", snap.Wire.Audiobook);
                            w.WriteString("putState", b64);
                            WriteTransport(w);
                        });
                    }
                case "inbound":
                    {
                        // A controller's Connect `play` / `transfer` REQUEST body (the JSON a dealer message carries, e.g. one from the #240
                        // capture), decoded by the app's own decoders and handed to the host exactly as the dealer glue does
                        // (Spotify.Connect.Commands.HandOffLoad). No dealer, no ack: the inspector has no Connect session.
                        string file = a.Str("file");
                        if (file.Length == 0 || !File.Exists(file)) return Inspect.Reply.Fail(id, name, "inbound wants file=<a play/transfer command body (JSON)>");
                        byte[] body = File.ReadAllBytes(file);
                        Spotify.Decode.RemoteCommand command = Spotify.Decode.ConnectCommand(body);
                        if (command.Kind is not (Spotify.Decode.RemoteCmd.Play or Spotify.Decode.RemoteCmd.Transfer))
                            return Inspect.Reply.Fail(id, name, "inbound: the body is not a play or transfer command (" + command.Kind + ")");
                        Spotify.Decode.ClusterBuffer buffer = Spotify.Decode.ClusterBuffer.Rent();
                        Spotify.Decode.RemoteLoad load = Spotify.Decode.ConnectLoad(body, buffer);
                        Playback.RemoteLoadArrived(in command, in load, buffer, Spotify.Current.Epoch);
                        break;
                    }
                case "telemetry":
                    {
                        // The RawCoreStream and the closing RawCoreStreamSegment the playing row's registration would be reported as
                        // (Spotify.Telemetry's own builders over the host's live registration), as base64 for an offline decode. Nothing is
                        // sent: `--fake` sends no telemetry (Spotify.Telemetry.Enqueue).
                        Spotify.Telemetry.Registration r = Playback.RegistrationForInspect;
                        byte[] stream = r.Open ? Google.Protobuf.MessageExtensions.ToByteArray(Spotify.Telemetry.BuildRawCoreStream(in r, "trackdone", isAssumedPremium: true)) : [];
                        byte[] segment = r.Open ? Google.Protobuf.MessageExtensions.ToByteArray(Spotify.Telemetry.BuildSegment(in r, 1000, isPause: false, isLast: true, "trackdone", 1000, 0)) : [];
                        return Settled(id, name, a, w =>
                        {
                            w.WriteString("do", verb);
                            w.WriteBoolean("open", r.Open);
                            w.WriteString("contentUri", r.ContentUri ?? "");
                            w.WriteString("localContentUri", r.LocalContentUri ?? "");
                            w.WriteString("playContext", r.PlayContext ?? "");
                            w.WriteString("audioFormat", r.AudioFormatName ?? "");
                            w.WriteString("rawCoreStream", Convert.ToBase64String(stream));
                            w.WriteString("segment", Convert.ToBase64String(segment));
                            WriteTransport(w);
                        });
                    }
                default:
                    return Inspect.Reply.Fail(id, name, "transport wants do=status|next|prev|end|pause|resume|seek|shuffle|repeat|add|playnext|clear|dead|duration|audiobook|putstate|telemetry|inbound|metadata, not '" + verb + "'");
            }
            // traced (`trace=N`): the trace's first frames are the change's own (the frame the queue and the deck land in), so nothing runs before it
            bool traced = a.Has("trace");
            if (!traced) RunFrames(2);
            string phase = traced ? "traced" : AwaitPlaybackLoad(PlayLoadBudgetMs);
            return Settled(id, name, a, w =>
            {
                w.WriteString("do", verb);
                w.WriteString("phase", phase);
                WriteTransport(w);
            });
        }

        /// <summary>One committed track's availability verdict (`transport do=metadata`): the row's (what the list greys and states), the
        /// ladder's when the answer carried a TRACK_V4 (<paramref name="ladder"/>), and whether the deck may land on it.</summary>
        static void WriteVerdict(Utf8JsonWriter w, string uri, string? ladder, long now)
        {
            if (!EntityId.TryParse(uri.AsSpan(), out EntityId tid) || tid.Kind != EntityKind.Track) return;
            EntityRef r = Entities.Ref(tid);
            var t = new Track(r.Slot);
            w.WriteStartObject();
            w.WriteString("uri", uri);
            w.WriteString("title", t.IsValid ? t.ForDisplay.Title : "");
            w.WriteBoolean("unplayable", t.IsValid && t.Unplayable());
            w.WriteBoolean("notYetOut", t.IsValid && t.NotYetOut(now));
            w.WriteString("reason", t.IsValid ? t.UnplayableReason().ToString() : "-");
            w.WriteString("note", t.IsValid ? Track.WithheldNote(t, now) : "");
            if (ladder is not null) w.WriteString("ladder", ladder);
            w.WriteBoolean("rowPlayable", Playback.RowPlayable(r));
            w.WriteEndObject();
        }

        /// <summary>The <c>uri</c> of every JSON object that carries a <c>playability</c>, depth first (a Pathfinder answer's track nodes).</summary>
        static IEnumerable<string> PlayabilityUris(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Object)
            {
                if (e.TryGetProperty("playability", out _) && e.TryGetProperty("uri", out JsonElement u) && u.ValueKind == JsonValueKind.String)
                    yield return u.GetString() ?? "";
                foreach (JsonProperty p in e.EnumerateObject())
                    foreach (string inner in PlayabilityUris(p.Value)) yield return inner;
            }
            else if (e.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in e.EnumerateArray())
                    foreach (string inner in PlayabilityUris(item)) yield return inner;
            }
        }

        /// <summary>The deck, the modes and the queue, as the reducer and the queue panel see them.</summary>
        static void WriteTransport(Utf8JsonWriter w)
        {
            Playback.State s = Playback.Snap();
            w.WriteString("deck", Short(s.CurrentId));
            // A playlist's matched spotify:local: item (#240): the file it plays from, by name only (never the folder).
            if (LocalLibrary.PathOfItem(s.CurrentId) is { } file) w.WriteString("deckFile", Path.GetFileName(file));
            w.WriteNumber("cursor", s.Cursor.Index);
            w.WriteString("repeat", s.Repeat switch
            {
                Spotify.Decode.RepeatMode.Context => "all",
                Spotify.Decode.RepeatMode.Track => "one",
                _ => "off",
            });
            w.WriteBoolean("shuffle", s.Shuffle);
            w.WriteBoolean("autoplay", Platform.Settings.Get(Platform.Keys.AutoplayEnabled));
            w.WriteNumber("positionMs", s.PosMs);
            w.WriteStartArray("rows");
            ReadOnlySpan<QueueEdge> rows = global::Wavee.Queue.Rows;
            for (int i = 0; i < rows.Length; i++)
            {
                char bucket = i == s.Cursor.Index ? '>' : (QueueBucket)rows[i].Bucket switch
                {
                    QueueBucket.History => 'H',
                    QueueBucket.UserQueue => 'U',
                    QueueBucket.NowPlaying => '*',
                    _ => 'N',
                };
                char provider = (QueueProvider)rows[i].Provider switch { QueueProvider.Queue => 'q', QueueProvider.Autoplay => 'a', _ => 'c' };
                EntityRef row = global::Wavee.Queue.RefAt(i);
                w.WriteStringValue(string.Concat(bucket.ToString(), provider.ToString(), ":", Short(row.Id), Playback.RowPlayable(row) ? "" : "!"));
            }
            w.WriteEndArray();
            Theming.Contracts.QueueProps props = Shell.QueueModel.Read();
            w.WriteStartArray("upNext");
            foreach (Theming.Contracts.QueueRowProps row in props.Rows)
            {
                char section = row.Section switch { QueueSection.Queue => 'q', QueueSection.Autoplay => 'a', _ => 'n' };
                w.WriteStringValue(string.Concat(section.ToString(), ":", Short(row.Ref.Id)));
            }
            w.WriteEndArray();
        }

        static string Short(EntityId id)
        {
            if (id.IsEmpty) return "-";
            string text = id.Text;
            int colon = text.LastIndexOf(':');
            return colon < 0 ? text : text[(colon + 1)..];
        }
    }
}
