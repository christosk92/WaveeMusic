// ── Spotify/Spotify.Decode.Commands.cs ──────────────────────────────────────────────────────────────────────────────
// the controller verbs whose BODY carries more than a verb: set_options → the option verbs the reducer already folds;
// set_queue and update_context → a RemoteQueue over a pooled ClusterBuffer (gap batch B3b, G-074 decode half)
//
// Role: CORE
// Owner: E
// Wave: gap batch B3b
// Budget: 220 lines
// Spec: gap register G-074 — a named partial of Spotify.Decode.cs (Spotify.Decode.Connect.cs is already past its budget,
//       and Spotify.Decode.Remote.cs owns the play/transfer body this file sits beside)
//
// `ConnectCommand` folds every REQUEST to an endpoint ordinal, a message id and one argument, and B3's reducer acts on
// that. Three endpoints said more than the ordinal could carry, so the reducer could only claim for them:
//
//     { command: { endpoint: "set_options",                                              OptionVerbs
//         shuffling_context: bool, repeating_context: bool, repeating_track: bool } }  ─▶ set_shuffling_context(b),
//                                                                                          set_repeating_track(true) |
//                                                                                          set_repeating_context(b)
//     { command: { endpoint: "set_queue", queue_revision: <uint64 as a bare number>,     RemoteQueue
//         prev_tracks: [ … ], next_tracks: [ … ] } }                                    ─▶ Revision, Prev*, Next*
//     { command: { endpoint: "update_context", context: { uri, url, pages: [ … ] } } }  ─▶ ContextUri/Url, Track*
//
// set_options needs NO new reducer input: it is exactly the two verbs a controller can already send one at a time, so it
// is folded to them here (0.2.9 `HandleSetOptionsAsync`'s precedence: repeating_track wins, then repeating_context, and
// either stated with the other absent means off). The two row-carrying verbs are decoded under the ClusterBuffer
// discipline (text in the arena, rows as RANGES) — the same rows a cluster or a context resolve produces, so whoever
// applies them reads one shape. queue_revision is a uint64 the wire writes as a bare JSON number past Int64 (captured:
// 17146072722624078579), so it is kept as its DIGITS and never parsed.

using System.Text.Json;

namespace Wavee;

public static partial class Spotify
{
    public static partial class Decode
    {
        // ── set_options ──────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The most verbs one <c>set_options</c> folds to: a shuffle verb and one repeat verb.</summary>
        public const int MaxOptionVerbs = 3;

        /// <summary>A <c>set_options</c> body → the verbs the reducer already folds, written into <paramref name="into"/>
        /// (at least <see cref="MaxOptionVerbs"/> long). Each verb is <paramref name="command"/> with its own kind, argument
        /// and dedupe key, so it keeps the controller's message id (the PUT it causes is attributed to it, G-073). Answers
        /// the count: 0 when <paramref name="command"/> is not a <see cref="RemoteCmd.SetOptions"/> or its body states no
        /// option. A truncated body acts on what it read.</summary>
        public static int OptionVerbs(ReadOnlySpan<byte> payload, in RemoteCommand command, Span<RemoteCommand> into)
        {
            if (command.Kind != RemoteCmd.SetOptions || into.Length < MaxOptionVerbs) return 0;
            sbyte shuffle = -1, repeatTrack = -1, repeatContext = -1;
            float speed = 0;
            var r = new Utf8JsonReader(payload);
            try
            {
                if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return 0;
                for (int root = r.CurrentDepth; Next(ref r, root);)
                {
                    if (!r.ValueTextEquals("command"u8)) { SkipValue(ref r); continue; }
                    for (int c = Fields(ref r); Next(ref r, c);)
                    {
                        if (r.ValueTextEquals("shuffling_context"u8)) shuffle = Tri(ref r);
                        else if (r.ValueTextEquals("repeating_track"u8)) repeatTrack = Tri(ref r);
                        else if (r.ValueTextEquals("repeating_context"u8)) repeatContext = Tri(ref r);
                        else if (r.ValueTextEquals("playback_speed"u8))
                        {
                            r.Read();
                            if (r.TokenType == JsonTokenType.Number && r.TryGetSingle(out float value) && float.IsFinite(value) && value is >= .5f and <= 3f) speed = value;
                        }
                        else SkipValue(ref r);
                    }
                }
            }
            catch (JsonException)
            {
                // A truncated body: the options read before the fault still stand.
            }

            int n = 0;
            if (shuffle >= 0) into[n++] = OptionVerb(in command, RemoteCmd.SetShufflingContext, shuffle == 1);
            if (repeatTrack == 1) into[n++] = OptionVerb(in command, RemoteCmd.SetRepeatingTrack, true);
            else if (repeatContext >= 0 || repeatTrack == 0) into[n++] = OptionVerb(in command, RemoteCmd.SetRepeatingContext, repeatContext == 1);
            if (speed > 0) into[n++] = command with
            {
                Kind = RemoteCmd.SetPlaybackSpeed,
                SeekToMs = BitConverter.SingleToInt32Bits(speed),
                DedupeKey = Fnv(Fnv(command.SenderHash, (ulong)(uint)command.MessageId), (ulong)RemoteCmd.SetPlaybackSpeed),
            };
            return n;
        }

        /// <summary>The reader on a property name → -1 unstated (or not a boolean, whose value is skipped), 0 false, 1 true.</summary>
        static sbyte Tri(ref Utf8JsonReader r)
        {
            if (!r.Read()) return -1;
            if (r.TokenType == JsonTokenType.True) return 1;
            if (r.TokenType == JsonTokenType.False) return 0;
            if (r.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) r.Skip();
            return -1;
        }

        static RemoteCommand OptionVerb(in RemoteCommand command, RemoteCmd kind, bool on)
            => command with
            {
                Kind = kind,
                BoolArg = on,
                DedupeKey = Fnv(Fnv(command.SenderHash, (ulong)(uint)command.MessageId), (ulong)kind),
            };

        // ── set_queue / update_context ───────────────────────────────────────────────────────────────────────────────

        /// <summary>What a controller's <c>set_queue</c> or <c>update_context</c> asks for (G-074). Text and rows are
        /// RANGES into the <see cref="ClusterBuffer"/> the decode was given; return the buffer after they are read.</summary>
        public struct RemoteQueue
        {
            /// <summary><see cref="RemoteCmd.SetQueue"/> or <see cref="RemoteCmd.UpdateContext"/>;
            /// <see cref="RemoteCmd.Unknown"/> (and nothing staged) for any other body.</summary>
            public RemoteCmd Kind;
            /// <summary>set_queue: <c>queue_revision</c>'s digits as the wire spelled them (a uint64 past Int64).</summary>
            public TextRef Revision;
            /// <summary>set_queue: the controller's whole view of the rows behind and ahead of the current one, in wire
            /// order — user-queue rows (<c>provider: "queue"</c>), context rows, the autoplay tail and its
            /// <c>spotify:delimiter</c> marker alike. The current row is in neither.</summary>
            public int PrevStart, PrevCount, NextStart, NextCount;
            /// <summary>update_context: the context whose rows changed, and the rows its embedded pages carried.</summary>
            public TextRef ContextUri, ContextUrl;
            public int TrackStart, TrackCount;
        }

        /// <summary>The dealer REQUEST body of a <c>set_queue</c> / <c>update_context</c> → a <see cref="RemoteQueue"/>
        /// over <paramref name="into"/>. Any other endpoint answers <see cref="RemoteCmd.Unknown"/> and stages nothing; a
        /// malformed body answers what it read before the fault.</summary>
        public static RemoteQueue ConnectQueue(ReadOnlySpan<byte> payload, ClusterBuffer into)
        {
            int mark = into.TrackCount;
            var queue = default(RemoteQueue);
            queue.PrevStart = queue.NextStart = queue.TrackStart = mark;
            var r = new Utf8JsonReader(payload);
            try
            {
                if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return queue;
                for (int root = r.CurrentDepth; Next(ref r, root);)
                {
                    if (!r.ValueTextEquals("command"u8)) { SkipValue(ref r); continue; }
                    for (int c = Fields(ref r); Next(ref r, c);)
                    {
                        if (r.ValueTextEquals("endpoint"u8))
                        {
                            r.Read();
                            queue.Kind = Says(ref r, "set_queue") ? RemoteCmd.SetQueue
                                : Says(ref r, "update_context") ? RemoteCmd.UpdateContext : RemoteCmd.Unknown;
                        }
                        else if (r.ValueTextEquals("queue_revision"u8)) { r.Read(); queue.Revision = Digits(ref r, into); }
                        else if (r.ValueTextEquals("prev_tracks"u8))
                        {
                            queue.PrevStart = into.TrackCount;
                            JsonTracks(ref r, into);                              // Spotify.Decode.Remote.cs
                            queue.PrevCount = into.TrackCount - queue.PrevStart;
                        }
                        else if (r.ValueTextEquals("next_tracks"u8))
                        {
                            queue.NextStart = into.TrackCount;
                            JsonTracks(ref r, into);
                            queue.NextCount = into.TrackCount - queue.NextStart;
                        }
                        else if (r.ValueTextEquals("context"u8))
                        {
                            // The play body's context reader, into a scratch load: the same uri, url and page rows.
                            var context = default(RemoteLoad);
                            queue.TrackStart = into.TrackCount;
                            PlayContext(ref r, into, ref context);               // Spotify.Decode.Remote.cs
                            queue.TrackCount = into.TrackCount - queue.TrackStart;
                            queue.ContextUri = context.ContextUri;
                            queue.ContextUrl = context.ContextUrl;
                        }
                        else SkipValue(ref r);
                    }
                }
            }
            catch (JsonException)
            {
                // A truncated body: keep what was read.
            }
            if (queue.Kind != RemoteCmd.Unknown) return queue;
            into.TrackCount = mark;
            queue = default;
            queue.PrevStart = queue.NextStart = queue.TrackStart = mark;
            return queue;
        }

        /// <summary>A number or a string the reader is ON, as its text — a revision is an identity to echo, never a
        /// value to compute with, and it does not fit a long.</summary>
        static TextRef Digits(ref Utf8JsonReader r, ClusterBuffer into)
            => r.TokenType == JsonTokenType.Number && !r.HasValueSequence ? into.AddText(r.ValueSpan) : Text(ref r, into);
    }
}
