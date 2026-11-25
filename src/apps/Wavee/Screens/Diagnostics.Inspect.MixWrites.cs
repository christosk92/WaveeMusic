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
            if (show == "migrate") return MixMigrate(id, name, a);
            if (show == "coldstart") return MixColdStart(id, name, p, uri, wireId);

            Entities.FakeMixServer.Clear();
            var steps = new List<Action<Utf8JsonWriter>>();
            var outcome = Spotify.Mixes.Outcome.Refused;
            bool done;

            // 1. Mix off for everyone, 2. Mix on again (the recipes come back).
            int before = MixData.Count(slot);
            Run("mix off", settled => Spotify.Mixes.TurnOff(p, settled));
            Note("mix off", outcome, "on=" + p.MixOn + " storedTransitionsKept=" + MixData.Count(slot) + "/" + before);
            Run("mix on", settled => Spotify.Mixes.TurnOn(p, settled));
            Note("mix on", outcome, "on=" + p.MixOn + " storedTransitions=" + MixData.Count(slot) + " etag=" + Entities.Strings.Resolve(p.MixLensEtagId));

            // 3-6. The seam card's writes, through MixEdit exactly as its Done and Reset buttons call them: a save over an Auto pair
            // (f3 = the Auto baseline), a save over a stored pair (f4 = its stored bytes, f5 = its uri), "No transition", Reset.
            Save("save over an Auto pair", AutoPair(), (int)MixStyle.Lift, 8);
            Save("save over a stored pair", StoredPair(), (int)MixStyle.Lift, 0);
            Save("no transition", AutoPair(), (int)MixStyle.NoMix, 0);
            Reset("reset to auto", 0);

            // 7-8. "Listen without Mix", synced to the account: the opt-out and its removal (the mirror, and the reply's shape logged).
            string key = MixOptOut.KeyOf(p);
            long mark = LogMark();
            Run("listen without mix", settled => Spotify.Mixes.SetListenWithoutMix(p, true, settled));
            Note("listen without mix (mix-opt-out)", outcome, "optedOut=" + MixOptOut.Is(p) + " pending=" + MixOptOut.IsPending(key) + " log: " + string.Join(" | ", MixLogSince(mark)));
            mark = LogMark();
            Run("listen with mix", settled => Spotify.Mixes.SetListenWithoutMix(p, false, settled));
            Note("listen with mix (mix-remove-opt-out)", outcome, "optedOut=" + MixOptOut.Is(p) + " pending=" + MixOptOut.IsPending(key) + " log: " + string.Join(" | ", MixLogSince(mark)));

            var recorded = Entities.FakeMixServer.Log();
            var server = Entities.FakeMixServer.StateOf(wireId);
            return Inspect.Reply.Ok(id, name, w =>
            {
                w.WriteString("uri", uri);
                w.WriteStartArray("steps");
                foreach (var step in steps) step(w);
                w.WriteEndArray();
                WriteRequests(w, recorded);
                w.WriteStartObject("server");
                w.WriteBoolean("mixed", server.Mixed);
                w.WriteBoolean("optOut", server.OptOut);
                w.WriteNumber("recipes", server.Recipes);
                w.WriteString("etag", server.Etag);
                w.WriteEndObject();
            });

            // One Spotify.Mixes write, its reply pumped through the frames until it settles.
            void Run(string label, Action<Action<Spotify.Mixes.Outcome>> write)
            {
                outcome = Spotify.Mixes.Outcome.Refused;
                Pump(label, fin => write(o => { outcome = o; fin(); }));
            }

            void Pump(string label, Action<Action> write)
            {
                done = false;
                write(() => done = true);
                PumpUntil(() => done, label);
            }

            void Note(string label, Spotify.Mixes.Outcome settled, string after) => steps.Add(w =>
            {
                w.WriteStartObject();
                w.WriteString("write", label);
                w.WriteString("outcome", settled.ToString());
                w.WriteString("after", after);
                w.WriteEndObject();
            });

            // The first pair with no stored recipe that the planner places an Auto transition for, and which the user may save.
            int AutoPair()
            {
                int rows = Entities.Current.Edges.PlaylistTracks.Targets(slot).Length;
                for (int i = 0; i + 1 < rows; i++)
                {
                    var pr = MixEdit.Read(slot, i);
                    if (pr.Valid && pr.Writes && pr.Auto.Mix && !pr.Local && !MixData.TryGet(slot, pr.ItemA, out _)) return i;
                }
                return -1;
            }

            // The first stored pair whose recipe carries fields Wavee does not model (curve overrides, unknown fields), else any stored pair.
            int StoredPair()
            {
                int rows = Entities.Current.Edges.PlaylistTracks.Targets(slot).Length, any = -1;
                for (int i = 0; i + 1 < rows; i++)
                {
                    var pr = MixEdit.Read(slot, i);
                    if (!pr.Valid || !pr.Writes || !MixData.TryGet(slot, pr.ItemA, out var t) || t.Recipe.IsNoTransition
                        || (t.Recipe.ToItemId != 0 && t.Recipe.ToItemId != pr.ItemB)) continue;
                    if (t.Recipe.CurveOverrides != 0) return i;
                    if (any < 0) any = i;
                }
                return any;
            }

            // A seam card save of `style` on pair `index` (beats 0: the pair's current overlap + 2 beats), then what went out against
            // what the card built: the template echoed, the recipe sent = the edit, every untouched field byte-identical, the previous.
            void Save(string label, int index, int style, int beats)
            {
                if (index < 0) { Note(label, Spotify.Mixes.Outcome.Refused, "no such pair in this list"); return; }
                var pair = MixEdit.Read(slot, index);
                if (beats == 0 && style != (int)MixStyle.NoMix) beats = MixEdit.BeatsOf(in pair, (pair.Now.Mix ? pair.Now : pair.Auto).OutDurationMs) + 2;
                MixTransition? stored = MixData.TryGet(slot, pair.ItemA, out var t) ? t : null;
                byte[] template = MixData.Signals(slot) is { } sg ? sg.TransitionTemplate(pair.ItemA).ToArray() : [];
                MixEdit.Build(in pair, slot, style, beats, out var edit, out byte[] replaced);
                var baseline = MixEdit.AutoBaseline(in pair);
                int sent = Entities.FakeMixServer.Log().Length;
                var state = MixSaveState.Failed;
                Pump(label, fin => MixEdit.Save(in pair, slot, style, beats, s => { state = s; fin(); }));
                var request = LastPost(sent);
                MixData.TryGet(slot, pair.ItemA, out var after);
                string etag = Entities.Strings.Resolve(p.MixLensEtagId);
                steps.Add(w =>
                {
                    w.WriteStartObject();
                    w.WriteString("write", label);
                    w.WriteNumber("from", index);
                    w.WriteNumber("to", index + 1);
                    w.WriteString("state", state.ToString());
                    w.WriteString("edit", edit.ToString());
                    if (request is not null) WriteSaveCheck(w, request.Body, template, edit, replaced, stored, baseline);
                    w.WriteString("after", "stored=" + (after is not null) + " storedEqualsSent=" + (after is not null && request is not null
                        && SentRecipe(request.Body) is { } sentB64 && sentB64 == after.Base64) + " transitionUri=" + (after?.TransitionUri ?? "")
                        + " etag=" + etag + " local=" + MixOverrides.TryGet(uri, pair.ItemA, pair.ItemB, out _));
                    w.WriteEndObject();
                });
            }

            // Reset to Auto on pair `index`: set-transition with no payload.
            void Reset(string label, int index)
            {
                var pair = MixEdit.Read(slot, index);
                bool had = MixData.TryGet(slot, pair.ItemA, out _);
                byte[] template = MixData.Signals(slot) is { } sg ? sg.TransitionTemplate(pair.ItemA).ToArray() : [];
                int sent = Entities.FakeMixServer.Log().Length;
                var state = MixSaveState.Failed;
                Pump(label, fin => MixEdit.Reset(in pair, slot, s => { state = s; fin(); }));
                var request = LastPost(sent);
                bool still = MixData.TryGet(slot, pair.ItemA, out _);
                string etag = Entities.Strings.Resolve(p.MixLensEtagId);
                steps.Add(w =>
                {
                    w.WriteStartObject();
                    w.WriteString("write", label);
                    w.WriteNumber("from", index);
                    w.WriteNumber("to", index + 1);
                    w.WriteString("state", state.ToString());
                    if (request is not null)
                    {
                        var (signal, payload, hasPayload) = SignalOf(request.Body);
                        w.WriteBoolean("templateVerbatim", template.Length > 0 && signal.AsSpan().StartsWith(template) && signal.Length == template.Length);
                        w.WriteBoolean("payload", hasPayload);
                        _ = payload;
                    }
                    w.WriteString("after", "hadStored=" + had + " storedNow=" + still + " etag=" + etag);
                    w.WriteEndObject();
                });
            }

            Entities.FakeMixServer.Request? LastPost(int from)
            {
                var all = Entities.FakeMixServer.Log();
                for (int i = all.Length - 1; i >= from; i--) if (all[i].Verb == "POST") return all[i];
                return null;
            }
        }

        /// <summary>A signals body's signal (f2) and its payload (f2.3), when it has one.</summary>
        static (byte[] Signal, byte[] Payload, bool HasPayload) SignalOf(ReadOnlySpan<byte> body)
        {
            byte[] signal = [], payload = [];
            bool has = false;
            for (var r = new Spotify.Decode.ProtoReader(body); r.Next();)
            {
                if (r.Field == 2 && r.Wire == 2) signal = r.Bytes().ToArray();
                else r.Skip();
            }
            // The signal minus its f3: the template the client echoed.
            var head = new List<byte>(signal.Length);
            for (var r = new Spotify.Decode.ProtoReader(signal); ;)
            {
                int start = r.Position;
                if (!r.Next()) break;
                if (r.Field == 3 && r.Wire == 2) { payload = r.Bytes().ToArray(); has = true; continue; }
                r.Skip();
                head.AddRange(signal.AsSpan(start, r.Position - start).ToArray());
            }
            return (head.ToArray(), payload, has);
        }

        /// <summary>The base64 recipe a save sent (payload f1), or null.</summary>
        static string? SentRecipe(ReadOnlySpan<byte> body)
        {
            var (_, payload, has) = SignalOf(body);
            if (!has) return null;
            var b64 = new Spotify.Decode.ProtoReader(payload).Bytes(1);
            return b64.IsEmpty ? null : System.Text.Encoding.ASCII.GetString(b64);
        }

        /// <summary>A save's request against what the card built: the template echoed verbatim, the sent recipe (payload f1) decoded back
        /// to the edit and equal to the patching encoder's bytes, the field-by-field diff against the replaced recipe (only what the edit
        /// changed may differ), and the previous transition (f3 the Auto baseline, or f4 the stored bytes + f5 its uri).</summary>
        static void WriteSaveCheck(Utf8JsonWriter w, byte[] body, byte[] template, in MixRecipe edit, byte[] replaced, MixTransition? stored,
            MixRecipe? baseline)
        {
            var (signal, payload, has) = SignalOf(body);
            w.WriteBoolean("templateVerbatim", template.Length > 0 && signal.AsSpan().SequenceEqual(template));
            w.WriteBoolean("payload", has);
            if (!has) return;
            var fields = new Spotify.Decode.ProtoReader(payload);
            string? f1 = null, f3 = null, f4 = null, f5 = null;
            var present = new List<int>();
            for (; fields.Next();)
            {
                present.Add(fields.Field);
                if (fields.Wire != 2) { fields.Skip(); continue; }
                string text = System.Text.Encoding.ASCII.GetString(fields.Bytes());
                switch (fields.Field) { case 1: f1 = text; break; case 3: f3 = text; break; case 4: f4 = text; break; case 5: f5 = text; break; }
            }
            w.WriteString("payloadFields", string.Join(",", present));
            byte[] sent = f1 is null ? [] : Convert.FromBase64String(f1);
            bool decodes = MixRecipe.TryDecode(sent, out var back);
            w.WriteBoolean("decodesToEdit", decodes && back.ToString() == edit.ToString());
            w.WriteBoolean("equalsPatchedEncode", sent.AsSpan().SequenceEqual(MixRecipe.EncodeToArray(in edit, replaced)));
            w.WriteNumber("sentBytes", sent.Length);
            if (replaced.Length > 0)
            {
                var changed = new List<string>();
                int same = FieldDiff(replaced, sent, "", 0, changed);
                w.WriteNumber("replacedBytes", replaced.Length);
                w.WriteString("changedFields", string.Join(",", changed));
                w.WriteNumber("identicalFields", same);
                w.WriteString("replacedHex", Convert.ToHexString(replaced).ToLowerInvariant());
                w.WriteString("sentHex", Convert.ToHexString(sent).ToLowerInvariant());
            }
            else w.WriteString("sentHex", Convert.ToHexString(sent).ToLowerInvariant());
            if (f3 is not null)
            {
                bool baselineDecodes = MixRecipe.TryDecodeBase64(System.Text.Encoding.ASCII.GetBytes(f3), out var auto, out _);
                w.WriteString("previous", "f3 auto");
                w.WriteString("f3", baselineDecodes ? auto.ToString() : "undecodable");
                w.WriteBoolean("f3EqualsPlannerBaseline", baseline is { } b && baselineDecodes && auto.ToString() == b.ToString());
            }
            if (f4 is not null)
            {
                w.WriteString("previous", "f4 user-defined + f5 uri");
                w.WriteBoolean("f4EqualsStoredBytes", stored is not null && f4 == stored.Base64);
                w.WriteBoolean("f5EqualsStoredUri", stored is not null && f5 == stored.TransitionUri);
                w.WriteString("f5", f5 ?? "");
            }
        }

        /// <summary>Field by field, two TransitionRecipe byte strings (into the Overlap, f1, and the Preset, f2): the paths whose bytes
        /// differ, gained or lost go to <paramref name="changed"/>; returns how many fields are byte-identical.</summary>
        static int FieldDiff(ReadOnlySpan<byte> before, ReadOnlySpan<byte> after, string prefix, int depth, List<string> changed)
        {
            var a = Entries(before);
            var b = Entries(after);
            var keys = new SortedSet<(int Field, int Occurrence)>();
            foreach (var e in a) keys.Add((e.Field, e.Occurrence));
            foreach (var e in b) keys.Add((e.Field, e.Occurrence));
            int same = 0;
            foreach (var key in keys)
            {
                var ea = a.Find(e => e.Field == key.Field && e.Occurrence == key.Occurrence);
                var eb = b.Find(e => e.Field == key.Field && e.Occurrence == key.Occurrence);
                string path = prefix + key.Field.ToString(CultureInfo.InvariantCulture) + (key.Occurrence > 0 ? "#" + key.Occurrence : "");
                if (ea.Entry is null) { changed.Add("+" + path); continue; }
                if (eb.Entry is null) { changed.Add("-" + path); continue; }
                if (ea.Entry.AsSpan().SequenceEqual(eb.Entry)) { same++; continue; }
                if (depth == 0 && key.Field is 1 or 2 && ea.Wire == 2 && eb.Wire == 2) same += FieldDiff(ea.Payload, eb.Payload, path + ".", 1, changed);
                else changed.Add(path);
            }
            return same;

            static List<(int Field, int Occurrence, int Wire, byte[] Entry, byte[] Payload)> Entries(ReadOnlySpan<byte> proto)
            {
                var list = new List<(int, int, int, byte[], byte[])>();
                var seen = new Dictionary<int, int>();
                var r = new Spotify.Decode.ProtoReader(proto);
                while (true)
                {
                    int start = r.Position;
                    if (!r.Next()) break;
                    int field = r.Field, wire = r.Wire;
                    byte[] payload = [];
                    if (wire == 2) payload = r.Bytes().ToArray(); else r.Skip();
                    int occurrence = seen.GetValueOrDefault(field);
                    seen[field] = occurrence + 1;
                    list.Add((field, occurrence, wire, proto[start..r.Position].ToArray(), payload));
                }
                return list;
            }
        }

        /// <summary>Frames until <paramref name="done"/> (5 s of wall clock at most): a write's reply crosses an api thread and lands on
        /// a frame.</summary>
        void PumpUntil(Func<bool> done, string label)
        {
            long until = Environment.TickCount64 + 5_000;
            while (!done() && Environment.TickCount64 < until) { RunFrames(1); if (!done()) Thread.Sleep(2); }
            RunFrames(2);
            if (!done()) Log.Warn("mix", "inspector: " + label + " did not settle");
        }

        /// <summary>The newest log entry's sequence: the mark <see cref="MixLogSince"/> counts from.</summary>
        static long LogMark() { var all = Log.Snapshot(); return all.Length > 0 ? all[^1].Sequence : 0; }

        /// <summary>The `mix` and `library` log lines written after <paramref name="mark"/>.</summary>
        static List<string> MixLogSince(long mark)
        {
            var lines = new List<string>();
            foreach (var e in Log.Snapshot())
                if (e.Sequence > mark && e.Category is "mix" or "library") lines.Add(e.Category + ": " + e.Message);
            return lines;
        }

        /// <summary>`mix migrate`: what a session start does with "Listen without Mix" (MixOptOut.OnSessionReady): the per-PC list
        /// (seed= fills it first, as a build before M2 left it) moved into the account's mirror and sent as opt-outs, the one log
        /// line, and the per-PC key after. Run it again for "the next start": the key is gone and nothing is sent.</summary>
        string MixMigrate(int id, string name, Inspect.Args a)
        {
            string before = MixOptOut.LegacyValue;
            if (a.Has("seed")) MixOptOut.SeedLegacy(a.Str("seed"));
            string seeded = MixOptOut.LegacyValue;
            Entities.FakeMixServer.Clear();
            long mark = LogMark();
            string? line = null;
            MixOptOut.OnSessionReady(l => line = l);
            PumpUntil(() => line is not null, "migrate");
            var recorded = Entities.FakeMixServer.Log();
            var logged = MixLogSince(mark);
            string after = MixOptOut.LegacyValue;
            return Inspect.Reply.Ok(id, name, w =>
            {
                w.WriteString("perPcBefore", before);
                w.WriteString("perPcSeeded", seeded);
                w.WriteString("perPcAfter", after);
                w.WriteString("migrationLine", line ?? "(did not settle)");
                w.WriteStartArray("mirror");
                foreach (var (playlist, optedOut, pending) in MixOptOut.Entries())
                    w.WriteStringValue(playlist + " optedOut=" + optedOut + " pending=" + pending);
                w.WriteEndArray();
                w.WriteStartArray("log");
                foreach (var l in logged) w.WriteStringValue(l);
                w.WriteEndArray();
                WriteRequests(w, recorded);
            });
        }

        /// <summary>`mix coldstart`: the persistence of a mixed list's Mix half, through the calls the store makes. The blob the list
        /// write keeps (MixPersist.Encode, what Store.SaveList puts in list_head.mix); then a launch: the playlist's Mix half forgotten
        /// (this process holds none of it before the disk read); then the disk read's staging (MixPersist.Stage, what
        /// Store.StageDiskList calls) committed: the recipes, templates, etag and lens are back and no lens read is owed (the log
        /// shows none). Last, the cache identity (Api.MixListStands, the provider's revalidation) against the fake server's /diff
        /// answer: the held etag stands, an etag moved by an edit elsewhere reads the list in full. --fake keeps no store, so the
        /// SQLite write and read of the column themselves are not run here.</summary>
        string MixColdStart(int id, string name, Playlist p, string uri, string wireId)
        {
            int slot = p.Slot;
            var table = Entities.Current.Playlists;
            byte[]? blob = MixPersist.Encode(Entities.Current, slot);
            if (blob is null) return Inspect.Reply.Fail(id, name, "the playlist holds no Mix half to persist (not mixed, or not read through the lens)");
            var before = Snap();

            // A launch: nothing of the list's Mix half is in memory before the disk read lands.
            MixData.Replace(slot, null);
            MixData.ReplaceSignals(slot, null);
            table.MixFlags[slot] = 0;
            table.ClearText(ref table.MixLensEtag, slot);
            MixData.SetHeldEtag(wireId, null);
            table.Bump(slot);
            RunFrames(1);
            var forgotten = Snap();

            long mark = LogMark();
            int rereads = Entities.MixLensRereads;
            var staging = Staging.Rent();
            bool staged;
            try
            {
                staging.Epoch = Entities.Current.Epoch;
                staged = MixPersist.Stage(staging, uri, blob);
                if (staged) { Entities.Commit(staging); Entities.Publish(); }
            }
            finally { Staging.Return(staging); }
            RunFrames(2);
            var restored = Snap();
            var logged = MixLogSince(mark);
            int owed = Entities.MixLensRereads - rereads;

            // The cache identity: the held revision and etag against the server's /diff answer, unchanged and then moved.
            string revision = Entities.Strings.Resolve(table.Revision[slot]);
            var baseline = Store.SnapshotList(Entities.Current, EdgeRelation.PlaylistTracks, slot);
            string held = Entities.Strings.Resolve(table.MixLensEtag[slot]);
            byte[] same = Entities.FakeMixServer.DiffAnswer(wireId) ?? [];
            bool standsSame = Spotify.Api.MixListStands(true, same, revision, baseline, held, out string? etagSame);
            Entities.FakeMixServer.EditElsewhere(wireId);
            byte[] moved = Entities.FakeMixServer.DiffAnswer(wireId) ?? [];
            bool standsMoved = Spotify.Api.MixListStands(true, moved, revision, baseline, held, out string? etagMoved);
            string blobFields = ProtoFields(blob, 0);

            return Inspect.Reply.Ok(id, name, w =>
            {
                w.WriteNumber("blobBytes", blob.Length);
                w.WriteString("blobFields", blobFields.Length > 700 ? blobFields[..700] + "…" : blobFields);
                WriteSnap(w, "persisted", before);
                WriteSnap(w, "launch", forgotten);
                w.WriteBoolean("staged", staged);
                WriteSnap(w, "restored", restored);
                w.WriteNumber("lensReadsOwed", owed);
                w.WriteStartArray("log");
                foreach (var l in logged) w.WriteStringValue(l);
                w.WriteEndArray();
                w.WriteStartObject("identity");
                w.WriteString("heldRevision", revision);
                w.WriteString("heldEtag", held);
                w.WriteString("diffSame", Convert.ToHexString(same).ToLowerInvariant());
                w.WriteString("answerEtagSame", etagSame ?? "");
                w.WriteString("verdictSame", standsSame ? "stands" : "full-read");
                w.WriteString("diffMoved", Convert.ToHexString(moved).ToLowerInvariant());
                w.WriteString("answerEtagMoved", etagMoved ?? "");
                w.WriteString("verdictMoved", standsMoved ? "stands" : "full-read");
                w.WriteEndObject();
            });

            string[] Snap()
            {
                var signals = MixData.Signals(slot);
                var raws = new System.Text.StringBuilder();
                foreach (var t in MixData.All(slot))
                    raws.Append(t.Recipe.FromItemId.ToString("x16", CultureInfo.InvariantCulture)).Append(':').Append(t.Raw.Length).Append("B ");
                return
                [
                    MixData.Count(slot).ToString(CultureInfo.InvariantCulture), raws.ToString().Trim(),
                    (signals?.Count ?? 0).ToString(CultureInfo.InvariantCulture), (signals?.Bytes ?? 0).ToString(CultureInfo.InvariantCulture),
                    signals?.MixState is { } ms ? DescribeSignal(ms) : "", Entities.Strings.Resolve(table.MixLensEtag[slot]),
                    ((PlaylistMixFlags)table.MixFlags[slot]).ToString(),
                    PlaylistMixRules.OwesLensRead((PlaylistMixFlags)table.MixFlags[slot]) ? "true" : "false",
                ];
            }

            static void WriteSnap(Utf8JsonWriter w, string key, string[] s)
            {
                w.WriteStartObject(key);
                w.WriteString("storedTransitions", s[0]);
                w.WriteString("recipes", s[1]);
                w.WriteString("signalTemplates", s[2]);
                w.WriteString("signalTemplateBytes", s[3]);
                w.WriteString("mixStateSignal", s[4]);
                w.WriteString("lensEtag", s[5]);
                w.WriteString("flags", s[6]);
                w.WriteString("owesLensRead", s[7]);
                w.WriteEndObject();
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
            if (a.Has("tier"))
            {
                // M8: the account read as Free (the Premium upsell where Mix would be) or Premium again.
                Spotify.MixLists.FakeFree = a.Str("tier") == "free";
                Entities.Current.Playlists.Bump(p.Slot);
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
                w.WriteBoolean("premium", Spotify.MixLists.Premium);
                w.WriteBoolean("canReorder", Spotify.MixLists.CanReorder(p));
                w.WriteBoolean("canDuplicate", Spotify.MixLists.CanDuplicate(p));
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
                if (r.Body.Length > 0 && r.ContentType == "application/json")
                {
                    w.WriteNumber("bodyBytes", r.Body.Length);
                    w.WriteString("json", System.Text.Encoding.UTF8.GetString(r.Body));
                }
                else if (r.Body.Length > 0)
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
