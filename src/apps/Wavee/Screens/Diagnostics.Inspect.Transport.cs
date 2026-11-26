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
                            WriteQuality(w, "quality", PutQuality(body.AsSpan(0, Math.Max(0, n))));   // read back off the encoded bytes
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
                        // #48 M5: an editor's Mix preview signal, and the pause / resume it wraps them in, as the dealer glue routes them
                        // (Spotify.Connect.RouteCommand → Playback.MixPreview; the verb then folds as a mailbox item does).
                        if (command.Kind == Spotify.Decode.RemoteCmd.Signal)
                        {
                            var signal = Spotify.Decode.MixPreviewCommand(body, out var preview, out bool resume);
                            if (signal == Spotify.Decode.MixPreviewSignal.Preview && preview is not null) Playback.MixPreview.Receive(preview);
                            else if (signal == Spotify.Decode.MixPreviewSignal.Stop) Playback.MixPreview.ReceiveStop(resume);
                            else return Inspect.Reply.Fail(id, name, "inbound: a signal that is not a Mix preview");
                            break;
                        }
                        if (command.Kind is Spotify.Decode.RemoteCmd.Pause or Spotify.Decode.RemoteCmd.Resume)
                        {
                            Playback.MixPreview.OnRemoteCommand(command.Kind);
                            Playback.Post(Playback.Input.Controller(in command, Playback.FrameNowMs()));
                            break;
                        }
                        if (command.Kind is not (Spotify.Decode.RemoteCmd.Play or Spotify.Decode.RemoteCmd.Transfer))
                            return Inspect.Reply.Fail(id, name, "inbound: the body is not a play or transfer command (" + command.Kind + ")");
                        Spotify.Decode.ClusterBuffer buffer = Spotify.Decode.ClusterBuffer.Rent();
                        Spotify.Decode.RemoteLoad load = Spotify.Decode.ConnectLoad(body, buffer);
                        Playback.RemoteLoadArrived(in command, in load, buffer, Spotify.Current.Epoch);
                        break;
                    }
                case "cluster":
                    {
                        // CAPTURED connect-state cluster pushes (one base64 `ClusterUpdate` per line, as the dealer's `payloads[0]`, gunzipped
                        // or not) through the real decoder (Spotify.Decode.ClusterUpdate): per frame, what the ACTIVE device says it plays
                        // (playback_quality), where it plays (audio_output_device_info) and the words the device picker's footer and card
                        // would show for it (Shell.DevicePicker.RemoteQuality). Nothing is folded: the deck and the roster stay as they are.
                        // No device id is written, only the active device's kind and position in the roster.
                        string file = a.Str("file");
                        if (file.Length == 0 || !File.Exists(file)) return Inspect.Reply.Fail(id, name, "cluster wants file=<base64 ClusterUpdate per line>");
                        string[] lines = File.ReadAllLines(file);
                        return Settled(id, name, a, w =>
                        {
                            w.WriteString("do", verb);
                            w.WriteStartArray("decoded");
                            foreach (string line in lines)
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
                                Spotify.Decode.ClusterBuffer buffer = Spotify.Decode.ClusterBuffer.Rent();
                                try { WriteClusterFrame(w, buffer, Spotify.Decode.ClusterUpdate(raw, default, buffer)); }
                                finally { Spotify.Decode.ClusterBuffer.Return(buffer); }
                            }
                            w.WriteEndArray();
                        });
                    }
                case "remotequality":
                    {
                        // `--fake`'s active Connect device states another playback_quality (after a transfer to the demo phone): value=hifi |
                        // hifi24 | veryhigh | high | normal | none — the Lossless badge and the picker's words follow, as a real push re-stating it.
                        var q = value switch
                        {
                            "hifi" or "lossless" => new Spotify.Decode.PlaybackQuality(Spotify.Decode.BitrateLevel.Hifi, Spotify.Decode.BitrateStrategy.BestMatching,
                                Spotify.Decode.BitrateLevel.Hifi, true, Spotify.Decode.HiFiStatus.On, Spotify.Decode.BitrateLevel.Hifi, 1),
                            "hifi24" => new Spotify.Decode.PlaybackQuality(Spotify.Decode.BitrateLevel.Hifi24, Spotify.Decode.BitrateStrategy.BestMatching,
                                Spotify.Decode.BitrateLevel.Hifi, true, Spotify.Decode.HiFiStatus.On, Spotify.Decode.BitrateLevel.Hifi24, 1),
                            "veryhigh" => new Spotify.Decode.PlaybackQuality(Spotify.Decode.BitrateLevel.VeryHigh, Spotify.Decode.BitrateStrategy.BestMatching,
                                Spotify.Decode.BitrateLevel.VeryHigh, true, Spotify.Decode.HiFiStatus.Off, Spotify.Decode.BitrateLevel.Hifi, 1),
                            "high" => new Spotify.Decode.PlaybackQuality(Spotify.Decode.BitrateLevel.High, Spotify.Decode.BitrateStrategy.BestMatching,
                                Spotify.Decode.BitrateLevel.High, true, Spotify.Decode.HiFiStatus.Off, 0, 1),
                            "normal" => new Spotify.Decode.PlaybackQuality(Spotify.Decode.BitrateLevel.Normal, Spotify.Decode.BitrateStrategy.BestMatching,
                                Spotify.Decode.BitrateLevel.Normal, true, Spotify.Decode.HiFiStatus.Off, 0, 1),
                            _ => default(Spotify.Decode.PlaybackQuality) with { Present = false },
                        };
                        if (!Playback.Devices.RestateActiveQualityForInspect(q)) return Inspect.Reply.Fail(id, name, "remotequality: no Connect device is active (mount connect=nearby, then transfer)");
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
                    return Inspect.Reply.Fail(id, name, "transport wants do=status|next|prev|end|pause|resume|seek|shuffle|repeat|add|playnext|clear|dead|duration|audiobook|putstate|cluster|remotequality|telemetry|inbound|metadata, not '" + verb + "'");
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

        /// <summary>One decoded cluster (`transport do=cluster`): the active device's quality, its output and the picker's words for it.</summary>
        static void WriteClusterFrame(Utf8JsonWriter w, Spotify.Decode.ClusterBuffer buffer, Spotify.Decode.ClusterDelta d)
        {
            ReadOnlySpan<Spotify.Decode.ClusterDevice> devices = buffer.Devices(d.DeviceStart, d.DeviceCount);
            ReadOnlySpan<byte> active = buffer.Utf8(d.ActiveDeviceId);
            int slot = -1;
            for (int i = 0; i < devices.Length; i++) if (!active.IsEmpty && buffer.Utf8(devices[i].Id).SequenceEqual(active)) { slot = i; break; }
            w.WriteStartObject();
            w.WriteNumber("reason", d.UpdateReason);
            w.WriteNumber("activeSlot", slot);
            WriteQuality(w, "quality", d.Quality);
            if (slot >= 0)
            {
                ref readonly Spotify.Decode.ClusterDevice dev = ref devices[slot];
                w.WriteString("activeKind", dev.Kind.ToString());
                w.WriteNumber("outputType", dev.OutputType);
                var row = new Playback.Devices.Row
                {
                    Kind = dev.Kind, Name = System.Text.Encoding.UTF8.GetString(buffer.Utf8(dev.Name)), Quality = d.Quality, OutputType = dev.OutputType,
                    OutputName = dev.OutputName.IsEmpty ? null : System.Text.Encoding.UTF8.GetString(buffer.Utf8(dev.OutputName)),
                };
                w.WriteString("footer", Shell.DevicePicker.RemoteQuality(in row));
                w.WriteBoolean("cardNamesOutput", Shell.DevicePicker.RemoteOutput(in row) is not null);
                if (Shell.DevicePicker.RemoteLossless(in row) is { } badge) w.WriteString("badge", badge);   // the bar's Lossless badge, its tooltip
                else w.WriteNull("badge");
            }
            w.WriteEndObject();
        }

        /// <summary>A <c>playback_quality</c> as the JSON view spells it (lower-case enum names), or null when absent.</summary>
        static void WriteQuality(Utf8JsonWriter w, string key, Spotify.Decode.PlaybackQuality q)
        {
            if (!q.Present) { w.WriteNull(key); return; }
            w.WriteStartObject(key);
            if (q.Level != 0) w.WriteString("bitrate_level", Spotify.Decode.LevelName(q.Level));
            if (q.Strategy != 0) w.WriteString("strategy", q.Strategy switch
            {
                Spotify.Decode.BitrateStrategy.BestMatching => "best_matching",
                Spotify.Decode.BitrateStrategy.BackendAdvised => "backend_advised",
                Spotify.Decode.BitrateStrategy.OfflinedFile => "offlined_file",
                Spotify.Decode.BitrateStrategy.CachedFile => "cached_file",
                Spotify.Decode.BitrateStrategy.LocalFile => "local_file",
                _ => ((int)q.Strategy).ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
            if (q.Target != 0) w.WriteString("target_bitrate_level", Spotify.Decode.LevelName(q.Target));
            if (q.TargetAvailable) w.WriteBoolean("target_bitrate_available", true);
            if (q.Hifi != 0) w.WriteString("hifi_status", q.Hifi switch
            {
                Spotify.Decode.HiFiStatus.On => "on",
                Spotify.Decode.HiFiStatus.Off => "off",
                _ => ((int)q.Hifi).ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
            if (q.Highest != 0) w.WriteString("highest_available_quality", Spotify.Decode.LevelName(q.Highest));
            if (q.FileOrigin != 0) w.WriteString("file_origin", q.FileOrigin switch
            {
                1 => "internal",
                3 => "local",
                _ => q.FileOrigin.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
            w.WriteEndObject();
        }

        /// <summary>A PutStateRequest's <c>device.player_state.playback_quality</c>, read back off the encoded bytes (not Present when absent).</summary>
        static Spotify.Decode.PlaybackQuality PutQuality(ReadOnlySpan<byte> put)
        {
            ReadOnlySpan<byte> device = new Spotify.Decode.ProtoReader(put).Bytes(2);
            ReadOnlySpan<byte> player = new Spotify.Decode.ProtoReader(device).Bytes(2);
            var r = new Spotify.Decode.ProtoReader(player);
            while (r.Next())
            {
                if (r.Field == 32 && r.Wire == 2) return Spotify.Decode.ReadQuality(r.Bytes());
                r.Skip();
            }
            return default(Spotify.Decode.PlaybackQuality) with { Present = false };
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
                char provider = (QueueProvider)rows[i].Provider switch { QueueProvider.Queue => 'q', QueueProvider.Autoplay => 'a', QueueProvider.Narration => 'n', _ => 'c' };
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
