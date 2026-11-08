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
                case "next": Playback.Next(); break;
                case "prev": Playback.Previous(); break;
                case "end": Playback.EndNow(); break;
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
                case "dead":
                    {
                        // The catalog rules a track terminally unavailable (withdrawn, region-locked), as an extended-metadata
                        // envelope with no entity does: the walk then steps past it.
                        if (!EntityId.TryParse(a.Str("uri").AsSpan(), out EntityId track)) return Inspect.Reply.Fail(id, name, "dead wants uri=spotify:track:...");
                        Entities.RuleUnplayable(track);
                        break;
                    }
                default:
                    return Inspect.Reply.Fail(id, name, "transport wants do=status|next|prev|end|seek|shuffle|repeat|add|playnext|clear|dead, not '" + verb + "'");
            }
            RunFrames(2);
            string phase = AwaitPlaybackLoad(PlayLoadBudgetMs);
            return Settled(id, name, a, w =>
            {
                w.WriteString("do", verb);
                w.WriteString("phase", phase);
                WriteTransport(w);
            });
        }

        /// <summary>The deck, the modes and the queue, as the reducer and the queue panel see them.</summary>
        static void WriteTransport(Utf8JsonWriter w)
        {
            Playback.State s = Playback.Snap();
            w.WriteString("deck", Short(s.CurrentId));
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
