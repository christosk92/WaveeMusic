// ── Screens/Diagnostics.Inspect.MixWrites.cs ──────────────────────────────────────────────────────────────────────────
// The `mix` verb's write half (#48 Mix M2, full-support plan §2.6): `mix writes` performs every Mix write on the fake mixed
// playlist through the REAL write path (Spotify.Mixes: the template echo, the body, the route table, the fold of the reply),
// with --fake's recording server in Library.Net's place, and prints each request as it left — method, path, the three
// headers (Content-Type, spotify-apply-lenses, spotify-playlist-sync-reason) and the body as a field dump and in hex — beside
// how it settled and what the list holds after. `mix fake` shows or switches the fake server (offline, failing, the playlist
// read as someone else's); `mix requests` prints what the server recorded.
//
// Role: SHELL (UI thread; drives frames while a write's reply crosses the api thread)

using System.Globalization;
using System.Text.Json;

namespace Wavee;

public static partial class Diagnostics
{
    public sealed partial class InspectSession
    {
        /// <summary>`mix writes|fake|requests`.</summary>
        string MixWritesVerb(int id, string name, string show, Inspect.Args a)
        {
            if (!Platform.Args.Fake) return Inspect.Reply.Fail(id, name, "show=" + show + " needs --fake (the recording server)");
            string uri = a.Str("uri", Entities.LibrarySeed.MixUri);
            var eid = EntityId.Parse(uri.AsSpan());
            if (eid.Kind != EntityKind.Playlist || !Entities.Current.Playlists.TryGetSlot(eid, out int slot))
                return Inspect.Reply.Fail(id, name, "no playlist row for '" + uri + "'");
            var p = new Playlist(slot);
            string wireId = new(EntityUri.IdOf(uri.AsSpan()));
            if (show == "fake") return MixFake(id, name, p, wireId, a);
            if (show == "requests")
            {
                var log = Entities.FakeMixServer.Log();
                return Inspect.Reply.Ok(id, name, w => { WriteRequests(w, log); });
            }
            if (_shell is null) return Inspect.Reply.Fail(id, name, "mount first: a write's reply lands on the UI thread's frames");

            Entities.FakeMixServer.Clear();
            var steps = new List<(string Label, Spotify.Mixes.Outcome Outcome, int From, int To, string Note)>();
            var outcome = Spotify.Mixes.Outcome.Refused;
            bool done;

            // 1. Mix off for everyone, 2. Mix on again (the recipes come back).
            int before = MixData.Count(slot);
            Run("mix off", settled => Spotify.Mixes.TurnOff(p, settled));
            steps.Add(("mix off", outcome, -1, -1, "on=" + p.MixOn + " storedTransitionsKept=" + MixData.Count(slot) + "/" + before));
            Run("mix on", settled => Spotify.Mixes.TurnOn(p, settled));
            steps.Add(("mix on", outcome, -1, -1, "on=" + p.MixOn + " storedTransitions=" + MixData.Count(slot) + " etag=" + Entities.Strings.Resolve(p.MixLensEtagId)));

            var recorded = Entities.FakeMixServer.Log();
            var server = Entities.FakeMixServer.StateOf(wireId);
            return Inspect.Reply.Ok(id, name, w =>
            {
                w.WriteString("uri", uri);
                w.WriteStartArray("steps");
                foreach (var (label, settled, from, to, note) in steps)
                {
                    w.WriteStartObject();
                    w.WriteString("write", label);
                    w.WriteString("outcome", settled.ToString());
                    if (from >= 0) { w.WriteNumber("from", from); w.WriteNumber("to", to); }
                    w.WriteString("after", note);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                WriteRequests(w, recorded);
                w.WriteStartObject("server");
                w.WriteBoolean("mixed", server.Mixed);
                w.WriteBoolean("optOut", server.OptOut);
                w.WriteNumber("recipes", server.Recipes);
                w.WriteString("etag", server.Etag);
                w.WriteEndObject();
            });

            // One write, its reply pumped through the frames until it settles (5 s of wall clock at most).
            void Run(string label, Action<Action<Spotify.Mixes.Outcome>> write)
            {
                done = false;
                outcome = Spotify.Mixes.Outcome.Refused;
                write(o => { outcome = o; done = true; });
                long until = Environment.TickCount64 + 5_000;
                while (!done && Environment.TickCount64 < until) { RunFrames(1); if (!done) Thread.Sleep(2); }
                RunFrames(2);
                if (!done) Log.Warn("mix", "inspector: " + label + " did not settle");
            }
        }

        /// <summary>`mix fake`: switch the fake server (fake=ok|offline|fail|clear) or read the fake playlist as someone else's
        /// (owner=false), then print the server's state of the list.</summary>
        string MixFake(int id, string name, Playlist p, string wireId, Inspect.Args a)
        {
            if (a.Has("fake"))
            {
                switch (a.Str("fake"))
                {
                    case "offline": Entities.FakeMixServer.Now = Entities.FakeMixServer.Mode.Offline; break;
                    case "fail": Entities.FakeMixServer.Now = Entities.FakeMixServer.Mode.Fail; break;
                    case "clear": Entities.FakeMixServer.Clear(); break;
                    default: Entities.FakeMixServer.Now = Entities.FakeMixServer.Mode.Ok; break;
                }
            }
            if (a.Has("owner"))
            {
                var table = Entities.Current.Playlists;
                byte caps = a.Bool("owner", true)
                    ? (byte)(PlaylistCaps.CanView | PlaylistCaps.CanEditItems | PlaylistCaps.CanEditMetadata | PlaylistCaps.IsOwner)
                    : (byte)PlaylistCaps.CanView;
                if (table.Caps[p.Slot] != caps) { table.Caps[p.Slot] = caps; table.Bump(p.Slot); }
            }
            RunFramesIfMounted(2);
            var state = Entities.FakeMixServer.StateOf(wireId);
            return Inspect.Reply.Ok(id, name, w =>
            {
                w.WriteString("mode", Entities.FakeMixServer.Now.ToString());
                w.WriteBoolean("owner", p.IsOwner);
                w.WriteBoolean("canWrite", Spotify.Mixes.CanWrite(p));
                w.WriteBoolean("canToggle", Spotify.Mixes.CanToggle(p));
                w.WriteBoolean("known", state.Known);
                w.WriteBoolean("mixed", state.Mixed);
                w.WriteBoolean("optOut", state.OptOut);
                w.WriteNumber("recipes", state.Recipes);
                w.WriteString("etag", state.Etag);
                w.WriteNumber("recorded", Entities.FakeMixServer.Log().Length);
            });
        }

        void RunFramesIfMounted(int n) { if (_shell is not null) RunFrames(n); }

        /// <summary>Every recorded request: method, path, the three headers, status, the body as fields and as hex.</summary>
        static void WriteRequests(Utf8JsonWriter w, Entities.FakeMixServer.Request[] recorded)
        {
            w.WriteStartArray("requests");
            foreach (var r in recorded)
            {
                w.WriteStartObject();
                w.WriteString("method", r.Verb);
                w.WriteString("path", r.Path);
                w.WriteStartObject("headers");
                if (r.ContentType is { } contentType) w.WriteString("Content-Type", contentType);
                if (r.Lenses is { } lenses) w.WriteString("spotify-apply-lenses", lenses);
                w.WriteString("spotify-playlist-sync-reason", r.SyncReason);
                if (r.ListPlayer) w.WriteString("spotify-list-player-enabled", "true");
                w.WriteEndObject();
                w.WriteNumber("status", r.Status);
                w.WriteNumber("replyBytes", r.ReplyBytes);
                if (r.Body.Length > 0)
                {
                    w.WriteNumber("bodyBytes", r.Body.Length);
                    w.WriteString("fields", ProtoFields(r.Body, 0));
                    w.WriteString("hex", Convert.ToHexString(r.Body).ToLowerInvariant());
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }

        /// <summary>A protobuf message as one line, field by field: <c>{1: &lt;24 B 00000007…&gt;, 2: {1: "mix-state", 2: {1: "mix"}}}</c>.
        /// A length-delimited field reads as text when it is all printable, as a message when it parses whole, else as hex;
        /// fixed32 / fixed64 as floats.</summary>
        static string ProtoFields(ReadOnlySpan<byte> proto, int depth)
        {
            var sb = new System.Text.StringBuilder("{");
            bool first = true;
            for (var r = new Spotify.Decode.ProtoReader(proto); r.Next();)
            {
                if (!first) sb.Append(", ");
                first = false;
                sb.Append(r.Field.ToString(CultureInfo.InvariantCulture)).Append(": ");
                switch (r.Wire)
                {
                    case 0: sb.Append(((long)r.Varint()).ToString(CultureInfo.InvariantCulture)); break;
                    case 5: sb.Append("f32 ").Append(BitConverter.UInt32BitsToSingle(r.Fixed32()).ToString("R", CultureInfo.InvariantCulture)); break;
                    case 1: sb.Append("f64 ").Append(r.Double().ToString("R", CultureInfo.InvariantCulture)); break;
                    case 2:
                        var value = r.Bytes();
                        if (Printable(value)) sb.Append('"').Append(System.Text.Encoding.ASCII.GetString(value)).Append('"');
                        else if (depth < 8 && IsMessage(value)) sb.Append(ProtoFields(value, depth + 1));
                        else sb.Append('<').Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(" B ")
                               .Append(Convert.ToHexString(value).ToLowerInvariant()).Append('>');
                        break;
                    default: r.Skip(); sb.Append('?'); break;
                }
            }
            return sb.Append('}').ToString();

            static bool Printable(ReadOnlySpan<byte> value)
            {
                if (value.IsEmpty) return false;
                foreach (byte b in value) if (b is < 0x20 or > 0x7E) return false;
                return true;
            }
        }

        /// <summary>Does <paramref name="value"/> parse as a protobuf message to its last byte (every tag a real field and wire type)?</summary>
        static bool IsMessage(ReadOnlySpan<byte> value)
        {
            if (value.IsEmpty) return false;
            int at = 0;
            while (at < value.Length)
            {
                if (!Varint(value, ref at, out ulong tag)) return false;
                int wire = (int)(tag & 7);
                if (tag >> 3 == 0 || tag >> 3 > 536_870_911) return false;
                switch (wire)
                {
                    case 0: if (!Varint(value, ref at, out _)) return false; break;
                    case 1: at += 8; break;
                    case 5: at += 4; break;
                    case 2:
                        if (!Varint(value, ref at, out ulong length) || length > (ulong)(value.Length - at)) return false;
                        at += (int)length;
                        break;
                    default: return false;
                }
                if (at > value.Length) return false;
            }
            return true;

            static bool Varint(ReadOnlySpan<byte> v, ref int at, out ulong result)
            {
                result = 0;
                for (int shift = 0; shift < 64 && at < v.Length; shift += 7)
                {
                    byte b = v[at++];
                    result |= (ulong)(b & 0x7F) << shift;
                    if ((b & 0x80) == 0) return true;
                }
                return false;
            }
        }
    }
}
