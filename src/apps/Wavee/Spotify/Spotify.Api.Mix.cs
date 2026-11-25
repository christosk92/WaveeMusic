// ── Spotify/Spotify.Api.Mix.cs — #48 Mix, everything Wavee writes to Spotify for Mix (full-support plan §2.1.4, M2) ─────────
//
// Every Mix write is ONE signal POSTed to /playlist/v2/playlist/{id}/signals (protocol §3), body protobuf under the playlist
// gateway's form-urlencoded content type:
//
//   {1 <24-byte revision>, 2 signal {1 identifier, 2 args, 3 payload?}}
//
// The signal's fields 1 and 2 are the TEMPLATE a lens read handed over (MixSignals: the list's contents.5 `mix-state` entry,
// an item's attribute 14 `set-transition`), copied byte for byte; field 3 is the write's payload, absent where the write has
// none (Mix off, Reset to Auto, the opt-outs). A write whose template no read carries (Mix on while Mix is off: the off read
// has no mix-state entry) builds {1 identifier, 2 {1 "mix"}}, which is what every captured template holds.
//
//   write             signal               payload                          route (lens header, sync-reason)
//   Mix off           mix-state            none                             auto,     CA8QAQ== [wire]
//   Mix on            mix-state            {1: 1}                           mix,auto, CBAQAQ== [wire]
//   save a transition set-transition (*)   {1 new, 3 Auto | 4 old + 5 uri}  mix,auto, CA8QAQ== [wire]
//   Reset to Auto     set-transition (*)   none                             mix,auto, CA8QAQ== [client]
//
//   (*) signal f1-f2 = the from-item's attribute 14, verbatim. Which previous a save sends is the one open switch
//       (Mixes.PreviousFor); the new recipe is the patching encoder's output over the recipe it replaces (Mix.Editor.cs).
//
// Which route each write takes is ONE table (Mixes.RouteOf). Signals neither move the revision nor cause a push (protocol
// §3.4): the reply is the whole list read through the requested lenses, folded as a list read (the Tune path), and it is
// the only way this PC learns the new mix lens etag. The revision a write sends is the one the list is held at; with none
// held (or when the server refuses the held one) the list's head is read first. --fake answers through a recording server
// (Entities.Fake.MixServer.cs) in Library.Net.PlaylistSignals' place.

namespace Wavee;

public static partial class Spotify
{
    public static partial class Api
    {
        /// <summary>The ApplyPlaylistSignals body: f1 the revision, f2 ONE signal = <paramref name="template"/>'s bytes verbatim (its
        /// f1 identifier and f2 args, as the server handed them over) and, when <paramref name="hasPayload"/>, f3 the payload message.
        /// PURE.</summary>
        public static byte[] SignalBody(ReadOnlySpan<byte> revision, ReadOnlySpan<byte> template, ReadOnlySpan<byte> payload, bool hasPayload)
        {
            var buffer = new byte[revision.Length + template.Length + payload.Length + 32];
            var w = new Decode.ProtoWriter(buffer);
            w.Utf8Always(1, revision);
            int signal = w.Open(2);
            w.Raw(template);
            if (hasPayload) w.Utf8Always(3, payload);
            w.Close(signal);
            return buffer.AsSpan(0, w.Length).ToArray();
        }

        /// <summary>A signal template no read handed over: <c>{1 identifier, 2 {1 lens}}</c> (every captured Mix template has this
        /// shape; protocol §2, §3.3). PURE.</summary>
        public static byte[] SignalTemplate(string identifier, string lens = "mix")
        {
            var buffer = new byte[identifier.Length + lens.Length + 16];
            var w = new Decode.ProtoWriter(buffer);
            w.StrAlways(1, identifier);
            int args = w.Open(2);
            w.StrAlways(1, lens);
            w.Close(args);
            return buffer.AsSpan(0, w.Length).ToArray();
        }

        /// <summary>The `mix-state` payload that turns Mix on: <c>{1: 1}</c> (the client's <c>{1 playlistMixed}</c>); off sends
        /// none.</summary>
        public static ReadOnlySpan<byte> MixOnPayload => [0x08, 0x01];

        /// <summary>The `mix-state` body that turns Mix on, with the built template (protocol §3.1, 50 bytes). PURE.</summary>
        public static byte[] MixStateOnBody(ReadOnlySpan<byte> revision)
            => SignalBody(revision, SignalTemplate("mix-state"), MixOnPayload, hasPayload: true);

        /// <summary>The `set-transition` payload (reference §6, the client's names): f1 <c>transition</c> = the new recipe, base64
        /// (the server stores exactly this); the transition it replaces as f3 <c>auto</c> (base64) or as f4 <c>userDefined</c>
        /// (base64) with f5 <c>userDefinedUri</c>. f2 <c>completed</c> never appeared on the wire and is not sent. PURE.</summary>
        public static byte[] SetTransitionPayload(string transition, string? auto, string? userDefined, string? userDefinedUri)
        {
            var buffer = new byte[transition.Length + (auto?.Length ?? 0) + (userDefined?.Length ?? 0) + (userDefinedUri?.Length ?? 0) * 3 + 32];
            var w = new Decode.ProtoWriter(buffer);
            w.StrAlways(1, transition);
            w.Str(3, auto);
            w.Str(4, userDefined);
            w.Str(5, userDefinedUri);
            return buffer.AsSpan(0, w.Length).ToArray();
        }
    }

    public static class Mixes
    {
        /// <summary>The writes this file sends (protocol §3).</summary>
        public enum Write : byte { Off, On, SaveTransition, ResetToAuto, OptOut, RemoveOptOut }

        /// <summary>How a write settled. <see cref="Offline"/>: nothing was sent (no session that can send).</summary>
        public enum Outcome : byte { Landed, Failed, Offline, Refused }

        /// <summary>The signal identifier a write sends.</summary>
        public static string SignalOf(Write w) => w switch
        {
            Write.SaveTransition or Write.ResetToAuto => "set-transition",
            Write.OptOut => "mix-opt-out",
            Write.RemoveOptOut => "mix-remove-opt-out",
            _ => "mix-state",
        };

        /// <summary>THE ROUTE TABLE: the lens header and the sync-reason each write travels with (protocol §1.1, §3.1). One
        /// place, so a route the owner's live check corrects is one line.</summary>
        public static SignalRoute RouteOf(Write w) => w switch
        {
            Write.On => SignalRoute.MixOn,          // mix,auto + CBAQAQ== [wire]
            Write.SaveTransition => SignalRoute.MixLens,   // mix,auto + CA8QAQ== [wire: both saves, sent after Mix on]
            Write.ResetToAuto => SignalRoute.MixLens,      // the save's route [client: the same signal with no payload]
            // OPEN (protocol §3.3, §13 #14): the opt-outs were never seen on the wire. They take the mix-state route's values, each
            // with the direction it resembles: opting out like Mix off, removing the opt-out like Mix on. Change them here.
            Write.OptOut => SignalRoute.Edit,       // auto + CA8QAQ== [as Mix off]
            Write.RemoveOptOut => SignalRoute.MixOn,       // mix,auto + CBAQAQ== [as Mix on]
            _ => SignalRoute.Edit,                  // Mix off: auto + CA8QAQ== [wire]
        };

        /// <summary>What a save sends as the transition it replaces (reference §6).</summary>
        public enum Previous : byte { None, Auto, UserDefined }

        /// <summary>THE OPEN QUESTION (protocol §3.2, reference §6), in one switch: the client's logic sends a stored user-defined
        /// recipe as f4 (with its uri as f5) and an Auto transition as f3 — but the one captured save over a stored recipe sent f3
        /// with the Auto baseline. True follows the client's logic; false sends the Auto baseline always, as captured. Flip it after
        /// the owner's live check if Spotify refuses f4 or shows the save wrong.</summary>
        const bool StoredRecipeGoesInField4 = true;

        /// <summary>Which previous transition a save sends: the pair's stored recipe (f4 + f5), else the Auto baseline the planner
        /// computes (f3), else none (the planner could not place an Auto transition for the pair). Logged with every save.</summary>
        public static Previous PreviousFor(bool hadStoredRecipe, bool autoBaselineKnown)
            => hadStoredRecipe && StoredRecipeGoesInField4 ? Previous.UserDefined
             : autoBaselineKnown ? Previous.Auto : Previous.None;

        /// <summary>One transition save: the pair (its from and to item), the new recipe and its bytes (patched over the recipe it
        /// replaces when there was one, Mix.Editor.cs), and the pair's Auto transition as the baseline a save over an Auto pair sends
        /// (no item ids; reference §5.2), null when the planner has none.</summary>
        public sealed record TransitionSave(ulong FromItem, ulong ToItem, MixRecipe Recipe, byte[] Encoded, MixRecipe? AutoBaseline);

        /// <summary>Save a transition to Spotify: `set-transition` with signal f2 = the from-item's attribute 14 template VERBATIM and
        /// the payload {1 new recipe, 3 Auto | 4 stored + 5 its uri} (<see cref="PreviousFor"/>). Optimistic: the new recipe is the
        /// pair's stored transition at once; the reply (the list through the mix lens: the recipe as stored, the new
        /// <c>transition_uri</c>, the new lens etag) is folded; a refusal puts the previous one back. Refused (nothing sent) without
        /// the template: the server did not offer this item's transition for editing.</summary>
        public static void SaveTransition(Playlist p, TransitionSave save, Action<Outcome> settled)
        {
            if (!CanWrite(p) || MixData.Signals(p.Slot) is not { } signals || signals.TransitionTemplate(save.FromItem).IsEmpty)
            {
                Log.Info("mix", "set-transition refused: no template for the from-item (or not the owner)");
                settled(Outcome.Refused);
                return;
            }
            int slot = p.Slot;
            byte[] template = signals.TransitionTemplate(save.FromItem).ToArray();
            MixTransition? stored = MixData.TryGet(slot, save.FromItem, out var t) ? t : null;
            var previous = PreviousFor(stored is not null, save.AutoBaseline is not null);
            string fresh = Convert.ToBase64String(save.Encoded);
            string? auto = null;
            if (previous == Previous.Auto)
            {
                var baseline = save.AutoBaseline!.Value;
                auto = Convert.ToBase64String(MixRecipe.EncodeToArray(in baseline, default));
            }
            byte[] payload = Api.SetTransitionPayload(fresh, auto, previous == Previous.UserDefined ? stored!.Base64 : null,
                                                      previous == Previous.UserDefined ? stored!.TransitionUri : null);
            MixData.SetTransition(slot, save.FromItem, new MixTransition(save.Recipe, save.Encoded, fresh, stored?.TransitionUri ?? ""));
            Entities.Current.Playlists.Bump(slot);
            string detail = "from=" + save.FromItem.ToString("x16", System.Globalization.CultureInfo.InvariantCulture) + " previous="
                          + (previous switch { Previous.UserDefined => "user-defined (f4+f5)", Previous.Auto => "auto (f3)", _ => "none" })
                          + (save.Recipe.IsNoTransition ? " recipe=none" : " preset=" + save.Recipe.Preset);
            Send(p, Write.SaveTransition, revision => Api.SignalBody(revision, template, payload, hasPayload: true), fold: true, detail, outcome =>
            {
                if (outcome != Outcome.Landed) PutBack(slot, save.FromItem, stored);
                settled(outcome);
            });
        }

        /// <summary>Reset a pair to Auto on Spotify: `set-transition` with NO payload ([client]; never seen on the wire). Optimistic:
        /// the pair's stored transition goes at once; the reply is folded; a refusal puts it back.</summary>
        public static void ResetToAuto(Playlist p, ulong fromItem, Action<Outcome> settled)
        {
            if (!CanWrite(p) || MixData.Signals(p.Slot) is not { } signals || signals.TransitionTemplate(fromItem).IsEmpty)
            {
                Log.Info("mix", "set-transition (reset) refused: no template for the from-item (or not the owner)");
                settled(Outcome.Refused);
                return;
            }
            int slot = p.Slot;
            byte[] template = signals.TransitionTemplate(fromItem).ToArray();
            MixTransition? stored = MixData.TryGet(slot, fromItem, out var t) ? t : null;
            MixData.SetTransition(slot, fromItem, null);
            Entities.Current.Playlists.Bump(slot);
            string detail = "from=" + fromItem.ToString("x16", System.Globalization.CultureInfo.InvariantCulture) + " reset to auto (no payload)";
            Send(p, Write.ResetToAuto, revision => Api.SignalBody(revision, template, default, hasPayload: false), fold: true, detail, outcome =>
            {
                if (outcome != Outcome.Landed) PutBack(slot, fromItem, stored);
                settled(outcome);
            });
        }

        static void PutBack(int slot, ulong fromItem, MixTransition? stored)
        {
            MixData.SetTransition(slot, fromItem, stored);
            Entities.Current.Playlists.Bump(slot);
        }

        /// <summary>"auto CA8QAQ==" — a route as its two header values, for the log lines and the inspector.</summary>
        public static string RouteText(SignalRoute route)
        {
            Span<char> path = stackalloc char[96];
            var request = Build(RequestKind.PlaylistSignals, new RequestArgs { Id = "x", Number = (long)route }, path);
            return Api.ApplyLensesValue(request.Headers) + " " + new string(request.SyncReason);
        }

        /// <summary>Who may write a playlist's Mix: its owner, or a collaborator who may edit its rows (UX direction S1, S15).</summary>
        public static bool CanWrite(Playlist p) => p.IsValid && (p.IsOwner || p.Editable);

        /// <summary>May Mix be switched for everyone: <see cref="CanWrite"/>, and — once a lens read has carried the list's signal
        /// templates — the server offered the `mix-state` signal (its contents.5 entry is the "can toggle" answer, plan §2.1.3).</summary>
        public static bool CanToggle(Playlist p)
            => CanWrite(p) && (MixData.Signals(p.Slot) is not { } signals || signals.MixState is not null || !p.MixOn);

        /// <summary>Turn Mix on for <paramref name="p"/>: `mix-state` with the payload <c>{1: 1}</c>; the reply (the list through the
        /// mix lens: its attributes, every item's set-transition template and the stored recipes, which Mix off kept) is folded.</summary>
        public static void TurnOn(Playlist p, Action<Outcome>? settled)
        {
            if (!CanWrite(p)) { settled?.Invoke(Outcome.Refused); return; }
            string lensId = Entities.MixPlaylistId(p.Slot);
            if (lensId.Length > 0) MixData.SetLens(lensId, true);
            byte[] template = MixData.Signals(p.Slot)?.MixState ?? Api.SignalTemplate("mix-state");
            int slot = p.Slot;
            Send(p, Write.On, revision => Api.SignalBody(revision, template, Api.MixOnPayload, hasPayload: true), fold: true, "", outcome =>
            {
                // The XM answers would say "mixed" now too (225 mix state, 170 auto lens): set them as Mix off clears them.
                if (outcome == Outcome.Landed)
                    SetFlags(slot, Entities.Current.Playlists.MixFlags[slot] | (uint)(PlaylistMixFlags.XmMixState | PlaylistMixFlags.XmAutoLens));
                else if (lensId.Length > 0) MixData.SetLens(lensId, false);
                settled?.Invoke(outcome);
            });
        }

        /// <summary>Turn Mix off for everyone: `mix-state` with NO payload (protocol §3.1). Optimistic: the list reads unmixed at
        /// once and the reply (the list without the mix lens) is folded; a failure puts the flags back. Spotify keeps the recipes
        /// (on again brings them back byte for byte), and so does this PC (MixData is not cleared).</summary>
        public static void TurnOff(Playlist p, Action<Outcome>? settled)
        {
            if (!CanToggle(p) || !p.MixOn) { settled?.Invoke(Outcome.Refused); return; }
            var table = Entities.Current.Playlists;
            int slot = p.Slot;
            uint before = table.MixFlags[slot];
            // The XM answers (225 mix state, 170 auto lens) said "mixed" too: they go with the list's own bits, or On() would hold.
            const uint Mixed = (uint)(PlaylistMixFlags.ListMix | PlaylistMixFlags.LensApplied | PlaylistMixFlags.XmMixState | PlaylistMixFlags.XmAutoLens);
            SetFlags(slot, before & ~Mixed);
            string lensId = Entities.MixPlaylistId(slot);
            if (lensId.Length > 0) MixData.SetLens(lensId, false);
            byte[] template = MixData.Signals(slot)?.MixState ?? Api.SignalTemplate("mix-state");
            Send(p, Write.Off, revision => Api.SignalBody(revision, template, default, hasPayload: false), fold: true, "", outcome =>
            {
                if (outcome == Outcome.Landed)
                {
                    // The reply speaks for the list half only: the stale XM "mixed" bits stay off until the kinds are read again.
                    SetFlags(slot, table.MixFlags[slot] & ~(uint)(PlaylistMixFlags.XmMixState | PlaylistMixFlags.XmAutoLens));
                }
                else
                {
                    SetFlags(slot, before);
                    if (lensId.Length > 0) MixData.SetLens(lensId, PlaylistMixRules.On((PlaylistMixFlags)before) && !MixOptOut.Is(p));
                }
                settled?.Invoke(outcome);
            });
        }

        static void SetFlags(int slot, uint flags)
        {
            var table = Entities.Current.Playlists;
            if (slot <= Table.None || slot >= table.Count || table.MixFlags[slot] == flags) return;
            table.MixFlags[slot] = flags;
            table.Bump(slot);
        }

        /// <summary>"Listen without Mix", synced to the account (owner decision 2026-11-25): the choice lands in the local mirror at
        /// once (MixOptOut: this account plays and reads the playlist without the lens; turning it back on asks the rows again through
        /// the lens), then `mix-opt-out` / `mix-remove-opt-out` goes to Spotify. Offline or refused, the mirror keeps it pending and
        /// MixOptOut.OnSessionReady sends it at the next start.</summary>
        public static void SetListenWithoutMix(Playlist p, bool without, Action<Outcome>? settled = null)
        {
            string key = MixOptOut.KeyOf(p);
            string lensId = Entities.MixPlaylistId(p.Slot);
            MixOptOut.Set(key, without, pending: true);
            if (!without && lensId.Length > 0 && p.MixOn)
            {
                MixData.SetLens(lensId, true);
                Entities.RefreshEdge(FetchEdge.PlaylistTracks, p.Slot);
            }
            Entities.Current.Playlists.Bump(p.Slot);
            SendOptOut(key, without, settled, p);
        }

        /// <summary>One opt-out signal for a playlist id (`mix-opt-out` / `mix-remove-opt-out`, args {1 "mix"}, no payload; protocol
        /// §3.3). The reply is NOT folded — how it shows an opt-out is open — but its shape is logged (lenses, Mix attributes, list
        /// signals) for the owner's live check. A landed write clears the mirror's pending mark. <paramref name="p"/>: the loaded
        /// playlist (its held revision), or default for one this PC has not loaded (the migration: its head is read first).</summary>
        public static void SendOptOut(string playlistId, bool without, Action<Outcome>? settled, Playlist p = default)
        {
            var kind = without ? Write.OptOut : Write.RemoveOptOut;
            byte[] template = Api.SignalTemplate(SignalOf(kind));
            Action<Outcome> done = outcome =>
            {
                if (outcome == Outcome.Landed) MixOptOut.Landed(playlistId, without);
                settled?.Invoke(outcome);
            };
            Func<byte[], byte[]> body = revision => Api.SignalBody(revision, template, default, hasPayload: false);
            if (p.IsValid) Send(p, kind, body, fold: false, "", done);
            else SendCore(playlistId, "spotify:playlist:" + playlistId, null, kind, body, fold: false, "", done);
        }

        // ── the one send ─────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>A session that can send: the account's scope and an online session; under <c>--fake</c> the recording server,
        /// unless the inspector took it offline.</summary>
        static bool Online(out Scope scope)
        {
            if (Platform.Args.Fake)
            {
                scope = Entities.Current;
                return !Entities.FakeMixServer.Offline;
            }
            return PlaylistEdits.CanWrite(out scope, out _);
        }

        /// <summary>The playlist id in the path: the bare base62 id (a fake text id is its uri's last part).</summary>
        static string WireId(Playlist p) => new(EntityUri.IdOf(p.Uri.Text.AsSpan()));

        /// <summary>The 24-byte head the list is held at, or null (none held, or not well formed). UI thread.</summary>
        static byte[]? HeldRevision(Playlist p)
        {
            string text = Entities.Strings.Resolve(Entities.Current.Playlists.Revision[p.Slot]);
            if (!ListWrite.IsWellFormedRevision(text)) return null;
            Span<byte> head = stackalloc byte[Encode.MaxRevisionBytes];
            int n = Encode.RevisionBytes(text, head);
            return n > 0 ? head[..n].ToArray() : null;
        }

        static byte[]? HeadOf(ReadOnlySpan<byte> selectedListContent)
        {
            Span<byte> head = stackalloc byte[Encode.MaxRevisionBytes];
            int n = Encode.ResultingRevision(selectedListContent, head);
            return n > 0 ? head[..n].ToArray() : null;
        }

        /// <summary>A list reply in one line, for a reply nobody folds: the applied lenses (f22), the list format attribute keys (f3.12),
        /// the item format attribute keys seen on any item (contents.3[].2.11), and the list signals (contents.5[].1). PURE.</summary>
        internal static string ReplyShape(ReadOnlySpan<byte> selectedListContent)
        {
            var lenses = new List<string>();
            var attrs = new SortedSet<string>(StringComparer.Ordinal);
            var itemAttrs = new SortedSet<string>(StringComparer.Ordinal);
            var signals = new List<string>();
            for (var r = new Decode.ProtoReader(selectedListContent); r.Next();)
            {
                if (r.Wire != 2) { r.Skip(); continue; }
                var value = r.Bytes();
                if (r.Field == 22) Lenses(value, lenses, 0);
                else if (r.Field == 3) Keys(value, 12, attrs);
                else if (r.Field == 5)
                {
                    for (var c = new Decode.ProtoReader(value); c.Next();)
                    {
                        if (c.Wire != 2) { c.Skip(); continue; }
                        var part = c.Bytes();
                        if (c.Field == 5) signals.Add(System.Text.Encoding.UTF8.GetString(new Decode.ProtoReader(part).Bytes(1)));
                        else if (c.Field == 3)
                            for (var item = new Decode.ProtoReader(part); item.Next();)
                            {
                                if (item.Field == 2 && item.Wire == 2) Keys(item.Bytes(), 11, itemAttrs);
                                else item.Skip();
                            }
                    }
                }
            }
            return "lenses=" + string.Join(",", lenses) + " attrs=" + string.Join(",", attrs) + " itemAttrs=" + string.Join(",", itemAttrs)
                 + " signals=" + string.Join(",", signals);

            static void Lenses(ReadOnlySpan<byte> entry, List<string> into, int depth)
            {
                var name = new Decode.ProtoReader(entry).Bytes(1);
                bool text = !name.IsEmpty && name.Length <= 32;
                foreach (byte b in name) if (b is < 0x20 or > 0x7E) { text = false; break; }
                if (text) { into.Add(System.Text.Encoding.ASCII.GetString(name)); return; }
                if (depth > 0) return;
                for (var h = new Decode.ProtoReader(entry); h.Next();)
                {
                    if (h.Wire == 2) Lenses(h.Bytes(), into, depth + 1);
                    else h.Skip();
                }
            }

            static void Keys(ReadOnlySpan<byte> attributes, int field, SortedSet<string> into)
            {
                for (var a = new Decode.ProtoReader(attributes); a.Next();)
                {
                    if (a.Field == field && a.Wire == 2) into.Add(System.Text.Encoding.UTF8.GetString(new Decode.ProtoReader(a.Bytes()).Bytes(1)));
                    else a.Skip();
                }
            }
        }

        /// <summary>A refusal that a stale held revision may explain (a conflict or a bad request): read the head and send once more.
        /// Never for auth, a missing list or a rate limit.</summary>
        static bool MayBeStale(int status) => status is >= 400 and < 500 and not (401 or 403 or 404 or 429);

        /// <summary>THE send every Mix write shares. UI thread in and out: <paramref name="body"/> builds the request from the revision
        /// (called on the api thread; it must not read a table), the reply is folded as a list read when <paramref name="fold"/>, and
        /// <paramref name="settled"/> hears how it ended. One <c>mix</c> log line per write names the signal, its route, the status
        /// and <paramref name="detail"/>.</summary>
        static void Send(Playlist p, Write kind, Func<byte[], byte[]> body, bool fold, string detail, Action<Outcome> settled)
        {
            if (!p.IsValid) { settled(Outcome.Refused); return; }
            if (Platform.Args.Fake && !Entities.FakeMixServer.Offline) Entities.FakeMixServer.Adopt(p);
            SendCore(WireId(p), p.Uri.Text, HeldRevision(p), kind, body, fold, detail, settled);
        }

        /// <summary><see cref="Send"/> for a playlist by its id: <paramref name="held"/> its held head, or null to read it first.</summary>
        static void SendCore(string id, string uri, byte[]? held, Write kind, Func<byte[], byte[]> body, bool fold, string detail,
            Action<Outcome> settled)
        {
            string signal = SignalOf(kind);
            if (!Online(out var scope))
            {
                Log.Info("mix", signal + " not sent: offline" + (detail.Length > 0 ? " " + detail : ""));
                settled(Outcome.Offline);
                return;
            }
            if (id.Length == 0) { settled(Outcome.Refused); return; }
            var net = Library.Net;
            SignalRoute route = RouteOf(kind);
            string routeText = RouteText(route);
            if (!net.Run(() =>
                {
                    byte[]? revision = held;
                    Api.Result r = Api.Result.Transport;
                    if (revision is null)
                    {
                        Api.Result read = net.Playlist(id);
                        revision = read.Ok ? HeadOf(read.Bytes) : null;
                        if (revision is null) r = read.Ok ? Api.Result.Transport : read;
                    }
                    if (revision is not null)
                    {
                        r = net.PlaylistSignals(id, route, body(revision));
                        if (held is not null && MayBeStale(r.Status))
                        {
                            Api.Result read = net.Playlist(id);
                            byte[]? fresh = read.Ok ? HeadOf(read.Bytes) : null;
                            if (fresh is not null && !fresh.AsSpan().SequenceEqual(revision))
                            {
                                Log.Info("mix", signal + " refused at the held head (status " + r.Status + "); sending once more at the server's");
                                r = net.PlaylistSignals(id, route, body(fresh));
                            }
                        }
                    }
                    Staging? staging = null;
                    if (fold && r.Ok && r.Length > 0)
                    {
                        staging = Staging.Rent();
                        staging.Epoch = scope.Epoch;
                        Api.PlaylistAnswer(r.Bytes, uri, staging);
                    }
                    int status = r.Status, length = r.Length;
                    bool ok = r.Ok;
                    // A reply that is not folded (the opt-outs) is described instead: how it reads back is what the live check needs.
                    string shape = ok && !fold && length > 0 ? " shape=" + ReplyShape(r.Bytes) : "";
                    Log.Info("mix", signal + (detail.Length > 0 ? " " + detail : "") + " route=" + routeText + " status=" + status
                                    + " reply=" + length + "B" + (ok ? fold ? " folded" : "" : " failed") + shape);
                    Post(() =>
                    {
                        if (staging is not null && ReferenceEquals(scope, Entities.Current)) { Entities.Commit(staging); Entities.Publish(); }
                        if (staging is not null) Staging.Return(staging);
                        settled(ok ? Outcome.Landed : Outcome.Failed);
                    });
                }))
                settled(Outcome.Failed);
        }
    }
}
