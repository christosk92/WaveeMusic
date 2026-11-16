// ── Screens/Diagnostics.Inspect.Home.cs ──────────────────────────────────────────────────────────────────────────────
// the pinned Home's verbs: `home do=…` (status, doc, plan, verify, edit, pin, unpin, add, remove, size, move, open, close,
// option, surface, search, undo, redo, reset), `hold`, `focus`, `said`, and the mount args `home=` / `homedoc=`
//
// Role: SHELL (UI thread)
// Spec: C:/WAVEE/scratch/pins/pinned-home-tech.md section 3.
//
// The acting `home` verbs call the same verbs the themes call, through the board the host registered
// (HomeBoardInspect.Current). With no board mounted they fall back to the store: the model and its undo are proved, the
// motion and the view state are not (the reply says `via: "store"`). The document the mount starts from is `homedoc=`
// (default: the default document), written into the profile's home file and loaded, so every mount starts the same.

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using FluentGpu.Foundation;
using FluentGpu.Controls;
using FluentGpu.Hooks;
using FluentGpu.Pal;
using Wavee.HomeUi;

namespace Wavee;

public static partial class Diagnostics
{
    public sealed partial class InspectSession
    {
        // ══ announcements (said) ════════════════════════════════════════════════════════════════════════════════════

        readonly List<(string Text, bool Assertive, int Frame)> _said = new();
        int _saidVerbMark, _saidReadMark;
        Action<string, bool>? _announce;

        /// <summary>Points the host's announcement hook at the ring (every verb: a mount can replace the hooks).</summary>
        void CaptureAnnouncements()
        {
            _announce ??= new Action<string, bool>((text, assertive) => _said.Add((text ?? "", assertive, (int)_frames)));
            var hooks = InputHooks.Current.Default;
            if (!ReferenceEquals(hooks.Announce, _announce)) hooks.Announce = _announce;
            _saidVerbMark = _said.Count;
        }

        void WriteSaid(Utf8JsonWriter w, string name, int from)
        {
            w.WriteStartArray(name);
            for (int i = from; i < _said.Count; i++)
            {
                w.WriteStartObject();
                w.WriteString("text", _said[i].Text);
                w.WriteBoolean("assertive", _said[i].Assertive);
                w.WriteNumber("frame", _said[i].Frame);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }

        /// <summary>`said {clear?}`: the announcements since the last `said` (clear=false keeps them for the next read).</summary>
        string Said(int id, string name, Inspect.Args a)
        {
            int from = Math.Min(_saidReadMark, _said.Count);
            string reply = Inspect.Reply.Ok(id, name, w => { WriteSaid(w, "said", from); w.WriteNumber("count", _said.Count - from); });
            if (a.Bool("clear", true)) _saidReadMark = _said.Count;
            return reply;
        }

        // ══ focus ═══════════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>`focus key=<k> nth=<n>`: keyboard focus to that node (the focus ring shows, as after Tab), no click.</summary>
        string Focus(int id, string name, Inspect.Args a)
        {
            var shell = _shell!;
            string key = a.Str("key");
            var found = InspectDump.Exact(shell, key);
            if (found.Count == 0) return Inspect.Reply.Fail(id, name, NoNodeKeyed(key));
            InspectDump.InReadingOrder(shell.Host.Scene, found);
            int nth = a.Int("nth", 0);
            if (nth < 0 || nth >= found.Count) return Inspect.Reply.Fail(id, name, "only " + found.Count + " nodes keyed '" + key + "' (nth counts from 0, in reading order)");
            shell.Host.Input.SetFocus(found[nth].Node, visual: true);
            RunFrames(1);
            return Settled(id, name, a, w => { w.WriteString("key", key); w.WriteNumber("matches", found.Count); });
        }

        // ══ hold ════════════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>`hold {key | x,y, ms, pointer, then, dx, dy, steps}`: a press that stays down for `ms` on the frame clock (inside the
        /// slop), then a release where it pressed, or a drag of dx,dy over `steps` frames and the release there.</summary>
        string Hold(int id, string name, Inspect.Args a)
        {
            int ms = Math.Clamp(a.Int("ms", 600), 16, 10_000);
            bool touch = a.Str("pointer", "mouse") == "touch";
            bool drag = a.Str("then", "release") == "drag";
            float dx = a.Float("dx", 0f), dy = a.Float("dy", 0f);
            if (drag && dx == 0f && dy == 0f) return Inspect.Reply.Fail(id, name, "then=drag wants dx or dy (DIP the pointer travels after the hold)");
            int steps = Math.Clamp(a.Int("steps", 8), 1, 600);
            if (!TryAim(id, name, a, wheel: false, out Aim aim, out string refusal)) return refusal;
            var kind = touch ? PointerKind.Touch : PointerKind.Mouse;
            Point2 from = aim.Point;
            if (!touch) { Move(from); RunFrames(1); }
            _hit = HitKey(from);
            Queue(new InputEvent(InputKind.PointerDown, from, 0, 0, Pointer: kind, TimestampMs: _ms, PointerId: touch ? 1u : 0u));
            RunFrames(Math.Max(1, (ms + 15) / 16));
            Point2 at = from;
            if (drag)
                for (int i = 1; i <= steps; i++)
                {
                    at = new Point2(from.X + dx * i / steps, from.Y + dy * i / steps);
                    Queue(new InputEvent(InputKind.PointerMove, at, 0, 0, Pointer: kind, TimestampMs: _ms, PointerId: touch ? 1u : 0u));
                    RunFrames(1);
                }
            Queue(new InputEvent(InputKind.PointerUp, at, 0, 0, Pointer: kind, TimestampMs: _ms, PointerId: touch ? 1u : 0u));
            if (!Tracing(a)) RunFrames(1);
            if (!touch) _pointer = at;
            Point2 end = at;
            return Settled(id, name, a, w =>
            {
                WriteAim(w, in aim);
                w.WriteNumber("ms", ms);
                w.WriteString("pointer", touch ? "touch" : "mouse");
                w.WriteString("then", drag ? "drag" : "release");
                if (drag)
                {
                    w.WriteStartObject("to");
                    Inspect.Reply.Num(w, "x", end.X);
                    Inspect.Reply.Num(w, "y", end.Y);
                    w.WriteEndObject();
                }
            });
        }

        // ══ homedoc= / home= (the mount's starting document and fixture) ════════════════════════════════════════════

        public static readonly string[] HomeDocNames = ["default", "keep", "pins0", "pins200", "gonepin", "custom", "mixed", "v2hidden", "toonew", "corrupt", "unknown"];

        /// <summary>Before the mount: the starting document (`homedoc=`, default `default`). The fixture (`home=`) is staged by the mount
        /// itself (Entities.StageHomeFixture), before this runs.</summary>
        bool StageHome(Inspect.Args a, out string error)
        {
            error = "";
            string doc = a.Str("homedoc", "default").ToLowerInvariant();
            if (Array.IndexOf(HomeDocNames, doc) < 0) { error = "homedoc is " + string.Join("|", HomeDocNames); return false; }
            StageHomeDoc(doc);
            return true;
        }

        const string GoneAlbumUri = "spotify:album:gone0";

        /// <summary>After the mount, for `homedoc=gonepin`: the gone album's own ask goes through the scripted transport and is answered as a
        /// removed entity is (a 404), so the pin tile reads the row's real gone state (Entities.Fake.Answer.cs).</summary>
        void LandHomeDoc(Inspect.Args a)
        {
            if (a.Str("homedoc", "default").ToLowerInvariant() != "gonepin") return;
            const string uri = GoneAlbumUri;
            for (int i = 0; i < 30 && Entities.ScriptedHeld == 0; i++) RunFrames(1);
            int held = Entities.ScriptedHeld;
            int settled = Entities.LandScripted(Entities.ScriptedAnswer.Fail);
            Entities.EndScripted();
            RunFrames(2);
            Log.Info("home", "inspect.gonepin held=" + held + " settled=" + settled + " state=" + HomePinState.StateText("album:" + uri));
        }

        /// <summary>Write the named document into the profile's home file and load it (the store's view state goes with it).</summary>
        static void StageHomeDoc(string name)
        {
            var store = HomeBoardStore.Instance;
            store.Quiesce();
            string path = store.FilePath.Length > 0 ? store.FilePath : Path.Combine(HomeBoardStore.Folder, HomeBoardStore.FileNameOf(""));
            string dir = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            if (name == "keep") { store.Load(path); return; }
            foreach (var p in new[] { path, path + ".bak", path + ".corrupt", path + ".tmp", Path.Combine(dir, "home-layout.json") })
                try { if (File.Exists(p)) File.Delete(p); } catch (IOException) { }
            switch (name)
            {
                case "v2hidden":
                    // No v3 file; a v2 file whose order is not the default and which hides recents, Made for you and the radio.
                    File.WriteAllText(Path.Combine(dir, "home-layout.json"),
                        "{\"version\":2,\"updatedAtMs\":1,\"zones\":[{\"kind\":\"radio\",\"visible\":false},{\"kind\":\"daylist\"},{\"kind\":\"recents\",\"visible\":false},{\"kind\":\"madeForYou\",\"visible\":false}]}");
                    store.Load(path);
                    store.ResolveLegacy(HomeProjection.AnswerOnHand(""));
                    return;
                case "toonew":
                    File.WriteAllText(path, "{\n  \"version\": 4,\n  \"rev\": 9,\n  \"groups\": [ { \"id\": \"later\", \"items\": [] } ],\n  \"layoutEngine\": { \"kind\": \"canvas\" }\n}\n");
                    store.Load(path);
                    return;
                case "corrupt":
                    File.WriteAllText(path, "{ \"version\": 3, \"groups\": [ { \"id\": \"today\", \"items\": [ ");
                    store.Load(path);
                    return;
                case "unknown":
                    File.WriteAllText(path, UnknownDocText());
                    store.Load(path);
                    return;
            }
            var doc = name switch
            {
                "pins0" => WithGroupItems(HomeDocs.Default(), HomeDocs.Pinned, []),
                "pins200" => Pins200(),
                "gonepin" => WithPins(HomeDocs.Default(), [new PinSource("album:spotify:album:gone0", "A gone album", "")]),
                "mixed" => Mixed(),
                _ => HomeDocs.Default(),
            };
            File.WriteAllBytes(path, HomeDocWire.Write(doc));
            store.Load(path);
            // gonepin: the scripted transport takes the gone album's provider BEFORE the board mounts, so the pin tile's own ask is sent and
            // held there; LandHomeDoc answers it after the mount as a removed entity is answered.
            if (name == "gonepin") Entities.BeginScripted(GoneAlbumUri);
            if (name is "custom" or "mixed") Customize(store, mixed: name == "mixed");
        }

        static readonly ImmutableSortedDictionary<string, string> s_noOptions = ImmutableSortedDictionary<string, string>.Empty;

        static HomeDoc WithGroupItems(HomeDoc doc, string groupId, ImmutableArray<HomeItem> items)
        {
            int g = doc.GroupIndex(groupId);
            return g < 0 ? doc : doc with { Groups = doc.Groups.SetItem(g, doc.Groups[g] with { Items = items }) };
        }

        static HomeDoc WithPins(HomeDoc doc, IReadOnlyList<PinSource> pins, HomeSize size = HomeSize.Auto)
        {
            int g = doc.GroupIndex(HomeDocs.Pinned);
            var items = doc.Groups[g].Items;
            var add = ImmutableArray.CreateBuilder<HomeItem>();
            for (int i = 0; i < pins.Count; i++) add.Add(new HomeItem("ipin" + i.ToString("x3", CultureInfo.InvariantCulture), pins[i], size, s_noOptions));
            return WithGroupItems(doc, HomeDocs.Pinned, items.InsertRange(0, add.ToImmutable()));
        }

        /// <summary>The fake catalogue's pinnable entities, then synthetic albums the fake never answers (their tiles show the unavailable face).</summary>
        static PinSource FakePin(int i) => i switch
        {
            < 13 => new PinSource("album:spotify:album:al" + i, "", ""),
            < 20 => new PinSource("pl:spotify:playlist:pl" + (i - 13), "", ""),
            < 32 => new PinSource("artist:spotify:artist:ar" + (i - 20), "", ""),
            _ => new PinSource("album:spotify:album:alx" + i.ToString(CultureInfo.InvariantCulture), "Album " + i.ToString(CultureInfo.InvariantCulture), ""),
        };

        static HomeDoc Pins200()
        {
            var doc = HomeDocs.Default();
            var pins = new List<PinSource>();
            for (int i = 0; doc.ItemCount + pins.Count < HomeDoc.MaxItems; i++) pins.Add(FakePin(i));
            return WithPins(doc, pins);
        }

        /// <summary>Every size of every MVP source: widgets in Today, pins and the recents in Pinned, the releases in From Spotify
        /// (the sections get theirs once the answer is on hand: <see cref="Customize"/>).</summary>
        static HomeDoc Mixed()
        {
            var doc = HomeDocs.Default();
            int n = 0;
            HomeItem W(string widget, HomeSize size) => new("imix" + (n++).ToString("x2", CultureInfo.InvariantCulture), new WidgetSource(widget, null), size, s_noOptions);
            HomeItem P(int i, HomeSize size) => new("imix" + (n++).ToString("x2", CultureInfo.InvariantCulture), FakePin(i), size, s_noOptions);
            doc = WithGroupItems(doc, HomeDocs.Today,
            [
                W("nowPlaying", HomeSize.S), W("nowPlaying", HomeSize.M), W("nowPlaying", HomeSize.L),
                W("upNext", HomeSize.S), W("upNext", HomeSize.M), W("upNext", HomeSize.L),
                W("daylist", HomeSize.M), W("daylist", HomeSize.L), W("daylist", HomeSize.Full),
            ]);
            doc = WithGroupItems(doc, HomeDocs.Pinned,
            [
                P(0, HomeSize.S), P(13, HomeSize.M), P(20, HomeSize.L), P(1, HomeSize.Auto),
                new HomeItem(HomeDocs.FillId, new FillSource(HomeDocs.ShortcutsFill, HomeDocs.FillMax), HomeSize.Auto, s_noOptions),
                W("recents", HomeSize.S), W("recents", HomeSize.M), W("recents", HomeSize.L), W("recents", HomeSize.Full),
            ]);
            doc = WithGroupItems(doc, HomeDocs.Spotify,
            [
                W("releases", HomeSize.M), W("releases", HomeSize.L), W("releases", HomeSize.Full),
                new HomeItem(HomeDocs.FeedId, new SpotifyFeedSource(""), HomeSize.Auto, s_noOptions),
            ]);
            return doc;
        }

        /// <summary>`custom`: the feed taken control of, two sections hidden, mixed sizes. `mixed`: the first three sections at m, l and full.
        /// Through the store's own commands over the answer on hand, then the ring is cleared (the mount starts with nothing to undo).</summary>
        static void Customize(HomeBoardStore store, bool mixed)
        {
            var shown = HomeProjection.Live(store.Doc.Peek(), store.Sources);
            var feed = new List<BoardEntry>();
            foreach (var e in shown.Entries) if (e.Kind == BoardEntryKind.FeedSection && !e.Hidden) feed.Add(e);
            if (feed.Count == 0) return;
            HomeSize[] sizes = mixed ? [HomeSize.M, HomeSize.L, HomeSize.Full] : [HomeSize.L, HomeSize.M];
            int done = 0;
            foreach (var e in feed)
            {
                if (done >= sizes.Length) break;
                if (e.Source is { } def && def.Declares(sizes[done]))
                {
                    store.Dispatch(new HomeCommand.SetSize(e.Id, sizes[done]));
                    done++;
                }
            }
            if (!mixed)
            {
                var after = HomeProjection.Live(store.Doc.Peek(), store.Sources);
                int hidden = 0;
                for (int i = after.Entries.Length - 1; i >= 0 && hidden < 2; i--)
                    if (after.Entries[i].Kind == BoardEntryKind.Item && after.Entries[i].Raw is SpotifySectionSource && !after.Entries[i].Hidden)
                    {
                        store.Dispatch(new HomeCommand.Remove(after.Entries[i].Id));
                        hidden++;
                        i--;   // every other one from the end
                    }
                store.Dispatch(new HomeCommand.SetSize(HomeDocs.RecentsId, HomeSize.L));
            }
            store.Load(store.FilePath.Length > 0 ? WriteThrough(store) : store.FilePath);
        }

        /// <summary>Writes the store's document now and hands back its path (the reload clears the ring the staging filled).</summary>
        static string WriteThrough(HomeBoardStore store)
        {
            store.Quiesce();
            File.WriteAllBytes(store.FilePath, HomeDocWire.Write(store.Doc.Peek()));
            return store.FilePath;
        }

        /// <summary>The default document with an unknown source kind, an unknown option key, unknown item and file members.</summary>
        static string UnknownDocText() => """
            {
              "version": 3,
              "rev": 3,
              "updatedAtMs": 1794844800000,
              "groups": [
                { "id": "today", "titleKey": "home.board.group.today",
                  "items": [ { "id": "d-daylist", "source": { "kind": "widget", "widget": "daylist" }, "size": "auto", "options": { "glow": "on", "live": "1" } },
                             { "id": "ix-stack", "source": { "kind": "stack", "of": ["i1", "i2"], "turn": { "everyMs": 8000, "mode": "cycle" } }, "size": "m", "pinnedAt": 1794800000000 } ] },
                { "id": "pinned", "titleKey": "home.board.group.pinned",
                  "items": [ { "id": "d-fill", "source": { "kind": "fill", "of": "spotify-shortcuts", "max": 8 }, "size": "auto" },
                             { "id": "d-recents", "source": { "kind": "widget", "widget": "recents" }, "size": "full" } ] },
                { "id": "spotify", "titleKey": "home.board.group.spotify",
                  "items": [ { "id": "d-feed", "source": { "kind": "spotifyFeed", "facet": "" }, "size": "auto" } ] } ],
              "hidden": [],
              "seen": [],
              "legacyHidden": [],
              "seededFromSidebar": false,
              "views": [ { "id": "v1", "query": "rock" } ]
            }
            """;

        // ══ the `home` verb ═════════════════════════════════════════════════════════════════════════════════════════

        public static readonly string[] HomeDos = ["status", "doc", "plan", "verify", "edit", "pin", "unpin", "add", "remove", "size", "move", "open", "close", "option", "surface", "search", "undo", "redo", "reset"];

        string Home(int id, string name, Inspect.Args a)
        {
            string op = a.Str("do", "status").ToLowerInvariant();
            var store = HomeBoardStore.Instance;
            var board = HomeBoardInspect.Current;
            string via = board is null ? "store" : "board";
            BoardModel Shown() => board?.Model ?? store.ShownNow();
            string Item() { string s = a.Str("item"); return s.StartsWith("home:tile:", StringComparison.Ordinal) ? s["home:tile:".Length..] : s; }
            string? missing = null;
            bool NeedItem() { if (a.Has("item")) return true; missing = "do=" + op + " wants item=<entry id> (home do=status lists them)"; return false; }
            string Fail(string why) => Inspect.Reply.Fail(id, name, why);
            HomeApplied last = default;
            void Run(HomeCommand c, string? tag = null) => last = store.Dispatch(c, tag);
            string Acted(Action<Utf8JsonWriter>? more = null) => Settled(id, name, a, w =>
            {
                w.WriteString("via", via);
                w.WriteBoolean("changed", last.Changed || board is not null);
                if (last.ItemId is not null) w.WriteString("item", last.ItemId);
                w.WriteNumber("rev", store.Doc.Peek().Rev);
                w.WriteNumber("undoDepth", store.UndoDepth);
                more?.Invoke(w);
            });

            switch (op)
            {
                case "status":
                    return Inspect.Reply.Ok(id, name, w => WriteHomeStatus(w, store, board, Shown()));
                case "doc":
                {
                    var current = store.Doc.Peek();
                    if (a.Has("path")) File.WriteAllText(a.Str("path"), HomeDocWire.WriteText(current));
                    string text = HomeDocWire.WriteText(current, indented: false);   // one reply line
                    return Inspect.Reply.Ok(id, name, w =>
                    {
                        using var parsed = JsonDocument.Parse(text);   // re-written, so raw members kept with their own line breaks stay on the line
                        w.WritePropertyName("doc");
                        parsed.RootElement.WriteTo(w);
                        WriteFile(w, store);
                    });
                }
                case "plan":
                    return Inspect.Reply.Ok(id, name, w => WritePlan(w, a.Str("facet", ""), store));
                case "page":
                {
                    // A paged section (the preview cards' pages, GridFill.Pagers): `key=` its zone key or any unique part of it (the section uri),
                    // `page=` a page from 1, or `dir=right` (the next) / `dir=left` (the previous). Settles (or traces with `trace=N`) after the slide.
                    string want = a.Str("key", "");
                    IPager? pager = null;
                    string? found = null;
                    foreach (var (k, c) in GridFill.Pagers)
                    {
                        if (want.Length > 0 && !k.Contains(want, StringComparison.Ordinal)) continue;
                        if (found is not null) return Fail("key '" + want + "' names more than one paged section (" + found + ", " + k + ")");
                        found = k; pager = c;
                    }
                    if (pager is null) return Fail(GridFill.Pagers.Count == 0 ? "no paged section is mounted" : "no paged section's key holds '" + want + "'");
                    int before = pager.Page.Peek();
                    string dir = a.Str("dir", "");
                    if (dir == "right") pager.Next();
                    else if (dir == "left") pager.Prev();
                    else pager.GoTo(Math.Max(0, a.Int("page", 1) - 1));
                    var hit = pager;
                    string at = found!;
                    return Settled(id, name, a, w =>
                    {
                        w.WriteString("key", at);
                        w.WriteNumber("from", before + 1);
                        w.WriteNumber("page", hit.Page.Peek() + 1);
                        w.WriteNumber("pages", hit.PageCount.Peek());
                    });
                }
                case "verify":
                {
                    if (board is null) return Fail("no board is mounted (route=home on a theme that draws the board)");
                    var faults = new List<string>();
                    int n = board.Verify(faults);
                    return Inspect.Reply.Ok(id, name, w =>
                    {
                        w.WriteNumber("columns", board.Columns);
                        w.WriteNumber("count", n);
                        w.WriteStartArray("faults");
                        foreach (var f in faults) w.WriteStringValue(f);
                        w.WriteEndArray();
                    });
                }
                case "edit":
                {
                    if (board is null) return Fail("no board is mounted");
                    if (board.Edit(a.Bool("on", true), a.Has("item") ? Item() : null) is { } refused) return Fail(refused);
                    return Acted();
                }
                case "pin":
                case "unpin":
                {
                    string raw = a.Str("uri");
                    if (raw.Length == 0) return Fail("do=" + op + " wants uri=<entity uri or route key>");
                    if (HomePinId.Canonical(raw) is not { } pinId) return Fail("'" + raw + "' cannot be pinned to Home");
                    if (op == "unpin")
                    {
                        if (board is not null) board.Unpin(pinId); else Run(new HomeCommand.Unpin(pinId));
                        return Acted(w => w.WriteString("pinId", pinId));
                    }
                    string pinName = HomePinVerbs.NameOf(pinId);
                    string? group = a.Has("group") ? a.Str("group") : null;
                    if (board is not null) board.Pin(pinId, pinName, "", group);
                    else Run(new HomeCommand.Pin(pinId, pinName, "", group));
                    var (pg, pi) = HomeCommands.FindPin(store.Doc.Peek(), pinId);
                    if (pg >= 0) last = last with { ItemId = store.Doc.Peek().Groups[pg].Items[pi].Id };
                    return Acted(w => w.WriteString("pinId", pinId));
                }
                case "add":
                {
                    string source = a.Str("source");
                    if (source.Length == 0) return Fail("do=add wants source=<catalogue id> (widget.upNext, pin:<pin id>, section:<key>)");
                    var all = HomeCatalog.All(store.Doc.Peek(), Shown(), store.Sources);
                    HomeCandidate? c = null;
                    foreach (var x in all) if (x.Id == source) { c = x; break; }
                    if (c is null && source.StartsWith("pin:", StringComparison.Ordinal) && HomePinId.Canonical(source[4..]) is { } pid)
                        c = new HomeCandidate("pin:" + pid, HomeCatalogGroup.Library, new PinSource(pid, HomePinVerbs.NameOf(pid), ""),
                            HomePinVerbs.NameOf(pid), "", HomeDocs.Pinned, false, null, false);
                    if (c is null) return Fail("no candidate '" + source + "' (home do=search q=… lists ids)");
                    string group = a.Str("group", c.TargetGroupId);
                    HomeSize size = HomeSize.Auto;
                    if (a.Has("size") && !HomeDocWire.TryParseSize(a.Str("size"), out size)) return Fail("size is auto|s|m|l|full");
                    int at = a.Int("at", -1);
                    if (board is not null)
                    {
                        int entry = -1;
                        if (at >= 0)
                        {
                            var m = board.Model;
                            int head = m.IndexOf(group);
                            entry = head < 0 ? -1 : head + 1 + at;
                        }
                        board.Add(c with { TargetGroupId = group }, entry, size);
                    }
                    else Run(new HomeCommand.Add(c.Source, group, at, size));
                    return Acted(w => w.WriteString("source", c.Id));
                }
                case "remove":
                    if (!NeedItem()) return Fail(missing!);
                    if (board is not null) board.Remove(Item()); else Run(new HomeCommand.Remove(Item()));
                    return Acted();
                case "size":
                {
                    if (!NeedItem()) return Fail(missing!);
                    string s = a.Str("size", "next");
                    if (s == "next") { if (board is not null) board.CycleSize(Item()); else Run(new HomeCommand.CycleSize(Item())); }
                    else
                    {
                        if (!HomeDocWire.TryParseSize(s, out var size)) return Fail("size is s|m|l|full|next");
                        if (board is not null) board.SetSize(Item(), size); else Run(new HomeCommand.SetSize(Item(), size));
                    }
                    return Acted();
                }
                case "move":
                {
                    if (!NeedItem()) return Fail(missing!);
                    string item = Item();
                    if (a.Has("by"))
                    {
                        int by = a.Int("by", 0);
                        if (board is not null) board.MoveBy(item, by); else Run(new HomeCommand.MoveBy(item, by), "step:" + item);
                    }
                    else if (a.Has("to"))
                    {
                        bool start = a.Str("to") == "start";
                        if (board is not null) board.MoveToEdge(item, start); else Run(new HomeCommand.MoveToEdge(item, start));
                    }
                    else if (a.Has("onto"))
                    {
                        string onto = a.Str("onto");
                        if (onto.StartsWith("home:tile:", StringComparison.Ordinal)) onto = onto["home:tile:".Length..];
                        else if (onto.StartsWith("home:group:", StringComparison.Ordinal)) onto = onto["home:group:".Length..];
                        if (board is not null) board.MoveOnto(item, onto); else Run(new HomeCommand.MoveOnto(item, onto));
                    }
                    else if (a.Has("dir"))
                    {
                        (int dx, int dy) = a.Str("dir") switch { "left" => (-1, 0), "right" => (1, 0), "up" => (0, -1), _ => (0, 1) };
                        if (board is not null) board.MoveToward(item, dx, dy);
                        else if (dy != 0) return Fail("dir=up|down needs the mounted board's pack (no board is mounted)");
                        else Run(new HomeCommand.MoveBy(item, dx), "step:" + item);
                    }
                    else return Fail("do=move wants by=±n, to=start|end, onto=<item> or dir=left|right|up|down");
                    return Acted();
                }
                case "open":
                    if (!NeedItem()) return Fail(missing!);
                    if (board is null) return Fail("no board is mounted");
                    board.Open(Item());
                    return Acted();
                case "close":
                    if (board is null) return Fail("no board is mounted");
                    board.Close(a.Bool("refocus", true));
                    return Acted();
                case "option":
                    if (board is null) return Fail("no board is mounted");
                    if (!a.Has("key") || !a.Has("value")) return Fail("do=option wants key= and value=");
                    board.SetOption(a.Str("key"), a.Str("value"));
                    return Acted();
                case "surface":
                {
                    if (board is null) return Fail("no board is mounted");
                    if (board.Surface(a.Bool("open", true), a.Str("kind", "band"), a.Has("arg") ? a.Str("arg") : null) is { } refused) return Fail(refused);
                    return Acted(w => WriteCandidates(w, "results", board.Results, 40));
                }
                case "search":
                {
                    string q = a.Str("q");
                    IReadOnlyList<HomeCandidate> hits;
                    string spoken;
                    if (board is not null) (hits, spoken) = board.Search(q);
                    else
                    {
                        var all = HomeCatalog.All(store.Doc.Peek(), Shown(), store.Sources);
                        hits = q.Trim().Length == 0 ? all : HomeCatalog.Find(all, q);   // an empty query restores the whole list
                        spoken = q.Trim().Length == 0 ? "" : HomeCatalog.Spoken(q.Trim(), hits.Count);
                    }
                    var counts = HomeCatalog.Count(hits);
                    return Acted(w =>
                    {
                        w.WriteString("q", q);
                        w.WriteString("spoken", spoken);
                        w.WriteStartObject("counts");
                        w.WriteNumber("total", counts.Total);
                        w.WriteNumber("widgets", counts.Widgets);
                        w.WriteNumber("spotify", counts.Spotify);
                        w.WriteNumber("library", counts.Library);
                        w.WriteNumber("extensions", counts.Extensions);
                        w.WriteNumber("onHome", counts.OnHome);
                        w.WriteNumber("toAdd", counts.ToAdd);
                        w.WriteEndObject();
                        WriteCandidates(w, "hits", hits, a.Int("items", 40));
                    });
                }
                case "undo":
                    if (board is not null) board.Undo(); else last = last with { Changed = store.Undo() };
                    return Acted(w => WriteUndo(w, store));
                case "redo":
                    if (board is not null) board.Redo(); else last = last with { Changed = store.Redo() };
                    return Acted(w => WriteUndo(w, store));
                case "reset":
                {
                    var scope = a.Str("scope", "page") == "everything" ? HomeResetScope.Everything : HomeResetScope.Page;
                    if (board is not null) board.Reset(scope, a.Bool("confirm", false)); else Run(new HomeCommand.Reset(scope));
                    return Acted();
                }
            }
            return Fail("do is " + string.Join("|", HomeDos));
        }

        static void WriteUndo(Utf8JsonWriter w, HomeBoardStore store)
        {
            var u = store.UndoState.Peek();
            w.WriteStartObject("undo");
            w.WriteBoolean("can", u.CanUndo);
            w.WriteString("label", u.LabelKey);
            w.WriteString("at", u.At);
            w.WriteNumber("depth", store.UndoDepth);
            w.WriteEndObject();
            w.WriteStartObject("redo");
            w.WriteBoolean("can", u.CanRedo);
            w.WriteNumber("depth", store.RedoDepth);
            w.WriteEndObject();
        }

        static void WriteFile(Utf8JsonWriter w, HomeBoardStore store)
        {
            w.WriteStartObject("file");
            string path = store.FilePath;
            w.WriteString("path", path);
            w.WriteString("outcome", store.LoadOutcome);
            bool exists = path.Length > 0 && File.Exists(path);
            w.WriteBoolean("exists", exists);
            if (exists)
            {
                byte[] bytes;
                try { bytes = File.ReadAllBytes(path); } catch (IOException) { bytes = []; }
                w.WriteNumber("bytes", bytes.Length);
                ulong h = 14695981039346656037UL;
                foreach (byte b in bytes) { h ^= b; h *= 1099511628211UL; }
                w.WriteString("fnv", h.ToString("x16", CultureInfo.InvariantCulture));
            }
            w.WriteBoolean("bak", path.Length > 0 && File.Exists(path + ".bak"));
            w.WriteBoolean("corrupt", path.Length > 0 && File.Exists(path + ".corrupt"));
            w.WriteEndObject();
        }

        static void WriteCandidates(Utf8JsonWriter w, string name, IReadOnlyList<HomeCandidate> list, int max)
        {
            w.WriteStartArray(name);
            for (int i = 0; i < list.Count && i < max; i++)
            {
                var c = list[i];
                w.WriteStartObject();
                w.WriteString("id", c.Id);
                w.WriteString("group", c.Group.ToString().ToLowerInvariant());
                w.WriteString("title", c.Title);
                w.WriteString("line", c.Line);
                w.WriteBoolean("onHome", c.OnHome);
                w.WriteString("target", c.TargetGroupId);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteNumber(name + "Count", list.Count);
        }

        static string SourceText(HomeSource? s) => s switch
        {
            WidgetSource wd => "widget:" + wd.Widget,
            PinSource p => "pin:" + p.PinId,
            SpotifySectionSource sec => "section:" + sec.Ref.Key,
            SpotifyFeedSource => "feed",
            FillSource => "fill",
            UnknownSource u => "unknown:" + u.RawKind,
            null => "",
            _ => s.Kind,
        };

        void WriteHomeStatus(Utf8JsonWriter w, HomeBoardStore store, IHomeBoardInspect? board, BoardModel model)
        {
            w.WriteString("via", board is null ? "store" : "board");
            w.WriteString("theme", board?.Theme ?? InspectKit.ActiveThemeId);
            w.WriteBoolean("editing", board?.Editing ?? false);
            if (board?.SelectedId is { } sel) w.WriteString("selected", sel); else w.WriteNull("selected");
            if (board?.OpenId is { } open) w.WriteString("open", open); else w.WriteNull("open");
            w.WriteStartObject("add");
            w.WriteString("surface", board?.AddSurface ?? "closed");
            w.WriteString("kind", board?.AddKind ?? "");
            w.WriteString("query", board?.Query ?? "");
            w.WriteNumber("results", board?.Results.Count ?? 0);
            w.WriteEndObject();
            w.WriteNumber("columns", board?.Columns ?? 0);
            WriteUndo(w, store);
            w.WriteBoolean("readOnly", store.ReadOnly);
            var doc = store.Doc.Peek();
            w.WriteNumber("rev", doc.Rev);
            w.WriteNumber("items", doc.ItemCount);
            w.WriteNumber("hidden", doc.Hidden.Length);
            w.WriteStartArray("legacyHidden");
            foreach (var l in doc.LegacyHidden) w.WriteStringValue(l);
            w.WriteEndArray();
            w.WriteBoolean("seededFromSidebar", doc.SeededFromSidebar);
            WriteFile(w, store);
            w.WriteStartArray("groups");
            bool inGroup = false;
            foreach (var e in model.Entries)
            {
                if (e.Kind == BoardEntryKind.GroupHead)
                {
                    if (inGroup) { w.WriteEndArray(); w.WriteEndObject(); }
                    w.WriteStartObject();
                    w.WriteString("id", e.Id);
                    w.WriteString("title", e.Title);
                    w.WriteStartArray("items");
                    inGroup = true;
                    continue;
                }
                if (!inGroup) continue;
                w.WriteStartObject();
                w.WriteString("id", e.Id);
                w.WriteString("key", "home:tile:" + e.Id);
                w.WriteString("kind", e.Kind.ToString());
                w.WriteString("source", SourceText(e.Raw));
                w.WriteString("title", e.Title);
                w.WriteString("size", HomeDocWire.SizeName(e.Size));
                w.WriteBoolean("auto", e.Size == HomeSize.Auto);
                if (e.Template != SectionTemplate.None) w.WriteString("template", e.Template.ToString());
                if (e.Hidden) w.WriteBoolean("hidden", true);
                if (!e.Members.IsDefaultOrEmpty) w.WriteNumber("members", e.Members.Length);
                if (board is not null)
                {
                    if (board.TrySpan(e.Id, out int cols, out float rows, out string form))
                    {
                        w.WriteString("form", form);
                        w.WriteStartObject("span");
                        w.WriteNumber("cols", cols);
                        Inspect.Reply.Num(w, "rows", rows);
                        w.WriteEndObject();
                    }
                    if (board.TryRect(e.Id, out RectF r)) Inspect.Reply.Rect(w, "rect", r);
                    w.WriteBoolean("realized", board.IsRealized(e.Id));
                }
                w.WriteBoolean("hasOptions", e.HasOptions);
                w.WriteBoolean("live", e.Live);
                w.WriteBoolean("paused", e.Paused);
                if (e.UserPin) w.WriteBoolean("userPin", true);
                if (e.Raw is PinSource pin && HomePinState.StateText(pin.PinId) is { Length: > 0 } pinState) w.WriteString("pinState", pinState);
                w.WriteEndObject();
            }
            if (inGroup) { w.WriteEndArray(); w.WriteEndObject(); }
            w.WriteEndArray();
        }

        /// <summary>`home do=plan facet=`: the planner dump. Per section of the facet's answer (SectionChooser.Plan): index, key, fp, family,
        /// typename, counts, template, zone, reason, then the board entry it became and whether the document placed or hid it; then the
        /// identity log's readings and lines (HomeIdentLog).</summary>
        static void WritePlan(Utf8JsonWriter w, string facet, HomeBoardStore store)
        {
            var answer = HomeProjection.AnswerOnHand(facet);
            var doc = store.Doc.Peek();
            var shown = facet.Length == 0 ? store.ShownNow() : BoardModel.Empty;
            var plan = SectionChooser.Plan(answer, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            w.WriteString("facet", facet);
            w.WriteNumber("sections", answer.Count);
            w.WriteStartArray("plan");
            for (int i = 0; i < plan.Count; i++)
            {
                var p = plan[i];
                var r = new SectionRef(p.Key, p.Fp, p.Family);
                w.WriteStartObject();
                w.WriteNumber("index", p.Index);
                w.WriteString("key", p.Key);
                w.WriteString("fp", p.Fp.ToString("x16", CultureInfo.InvariantCulture));
                w.WriteString("family", p.Family);
                w.WriteString("typename", p.TypeName);
                w.WriteString("title", answer[i].Title ?? "");
                w.WriteStartObject("counts");
                w.WriteNumber("cards", p.Cards);
                w.WriteNumber("usable", p.Usable);
                w.WriteNumber("unsupported", p.Unsupported);
                w.WriteNumber("duplicates", p.Duplicates);
                w.WriteNumber("total", p.Total);
                w.WriteEndObject();
                w.WriteString("template", p.Template.ToString());
                w.WriteString("zone", p.Zone.ToString());
                w.WriteString("reason", p.Reason);
                string? entry = null;
                foreach (var e in shown.Entries)
                {
                    if (e.Raw is SpotifySectionSource ss && ss.Ref.Key == p.Key) { entry = e.Id; break; }
                    if (!e.Members.IsDefaultOrEmpty && e.Members.Contains(r)) { entry = e.Id; break; }
                }
                if (entry is not null) w.WriteString("entry", entry); else w.WriteNull("entry");
                bool placed = false;
                foreach (var g in doc.Groups) foreach (var it in g.Items) if (it.Source is SpotifySectionSource ss && ss.Ref.Key == p.Key) placed = true;
                w.WriteBoolean("placed", placed);
                w.WriteBoolean("hidden", HomeCommands.IsHidden(doc, r));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("identity");
            foreach (var f in HomeIdentLog.Readings())
            {
                w.WriteStartObject();
                w.WriteString("reading", f.ToString());
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteNumber("identityLines", HomeIdentLog.Lines().Count);
        }
    }
}
