// ── Screens/Diagnostics.Inspect.Crossings.cs ───────────────────────────────────────────────────────────────────────────
// `tiers` lists the theme's tier edges and holds (and the sizes a crossing of each uses); `crossings` runs every edge of the
// active theme, both ways, over a list of routes, in ONE server: one `crossing` per row, each reply kept whole
//
// Role: SHELL (UI thread)
// Spec: handoff/audit-2026-10-23/FIX-PLAN.md lane A2 item 2 (motion.md §6 tooling 1 and 2: 75 minutes of fresh servers before).
//
// THE SIZES (the motion audit's recipe, motion.md §1; the skill's "Jumps" recipe). An edge at `At` holds `NarrowHold` below it on
// narrowing and `WidenHold` above it on widening (`TierEdge`). A narrowing row mounts at `At + 64` (well inside the upper tier),
// resizes to the OLD side `At - NarrowHold + 8` (still held) and crosses to the NEW side `At - NarrowHold - 8`; a widening row
// mounts at `At - 64`, walks to `At + WidenHold - 8` and crosses to `At + WidenHold + 8`. A height edge does the same on the height
// at the fixed width `w`, a width edge at the fixed height `h`. Each crossing runs its own control step (`crossing`'s `control`, on unless
// `control=false` is passed on): its `counts.excess*` are what the edge adds over a plain resize of the same distance.
//
// THE REPLY. `crossings[]` holds every crossing's reply exactly as `crossing` writes it (the gate reads that shape), with a `row`
// object spliced in front: {theme, route, edge, axis, dir, mount, old, new, ms}. `table[]` is one line per row (the tiers
// before and after, contentCommitted and the counts), `failed` counts the rows whose crossing answered ok:false.

using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee;

public static partial class Diagnostics
{
    public sealed partial class InspectSession
    {
        /// <summary>One edge a batch crosses: its name (`W1008`, `H720`), axis and the engine edge.</summary>
        readonly record struct EdgeRow(string Name, bool Height, TierEdge Edge);

        /// <summary>The fixture page a `crossings` route word names (the motion audit's): album al2, artist ar3, playlist pl0; any other word
        /// is a route key, and a `spotify:` word a uri.</summary>
        static (string Arg, string Value) CrossingsRoute(string word) => word switch
        {
            "album" => ("uri", "spotify:album:al2"),
            "artist" => ("uri", "spotify:artist:ar3"),
            "playlist" => ("uri", "spotify:playlist:pl0"),
            "show" => ("uri", "spotify:show:sh0"),
            "episode" => ("uri", "spotify:episode:ep0"),
            _ when word.StartsWith("spotify:", StringComparison.Ordinal) => ("uri", word),
            _ => ("route", word),
        };

        /// <summary>The tier policy of <paramref name="themeId"/>: the published one when it is the active theme and a mount is up, else the
        /// theme's structure policy at its default options. Null when no such theme is installed.</summary>
        TierPolicy? PolicyOf(string themeId)
        {
            if (_shell is not null && string.Equals(themeId, InspectKit.ActiveThemeId, StringComparison.OrdinalIgnoreCase)
                && ThemeHost.Facts.Peek().Tiers.Policy is { } live) return live;
            if (ThemeInspection.Find(themeId) is not { } theme) return null;
            return theme.Policies(theme.Build(static lo => lo.Option.DefaultBoxed)).Tiers;
        }

        /// <summary>The window and height edges of <paramref name="policy"/> that can be crossed, in order (window first).</summary>
        static List<EdgeRow> EdgesOf(TierPolicy policy)
        {
            var edges = new List<EdgeRow>();
            for (int i = 0; i < policy.Window.EdgeCount; i++)
                if (policy.Window.EdgeAt(i) is { IsPresent: true } e) edges.Add(new EdgeRow("W" + Dip(e.At), false, e));
            for (int i = 0; i < policy.Height.EdgeCount; i++)
                if (policy.Height.EdgeAt(i) is { IsPresent: true } e) edges.Add(new EdgeRow("H" + Dip(e.At), true, e));
            return edges;
        }

        static string Dip(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        /// <summary>The sizes one crossing of <paramref name="e"/> uses on its axis: where it mounts, the old side and the new side.</summary>
        static (float Mount, float Old, float New) CrossingSizes(in TierEdge e, bool narrow)
            => narrow ? (e.At + 64f, e.At - e.NarrowHold + 8f, e.At - e.NarrowHold - 8f)
                      : (e.At - 64f, e.At + e.WidenHold - 8f, e.At + e.WidenHold + 8f);

        static void WriteEdge(Utf8JsonWriter w, in TierEdge e, string[] tierNames, int index)
        {
            w.WriteStartObject();
            Inspect.Reply.Num(w, "at", e.At);
            Inspect.Reply.Num(w, "narrowHold", e.NarrowHold);
            Inspect.Reply.Num(w, "widenHold", e.WidenHold);
            if (tierNames.Length > 0)
            {
                w.WriteString("below", index < tierNames.Length ? tierNames[index] : "#" + index);
                w.WriteString("above", index + 1 < tierNames.Length ? tierNames[index + 1] : "#" + (index + 1));
            }
            w.WriteEndObject();
        }

        static void WriteLadder(Utf8JsonWriter w, string name, TierLadder ladder, string[] tierNames)
        {
            w.WriteStartArray(name);
            for (int i = 0; i < ladder.EdgeCount; i++)
                if (ladder.EdgeAt(i) is { IsPresent: true } e) WriteEdge(w, in e, tierNames, i);
            w.WriteEndArray();
        }

        static readonly string[] s_widthTiers = Enum.GetNames<WidthTier>();
        static readonly string[] s_heightTiers = Enum.GetNames<HeightTier>();

        /// <summary>`policy {theme, window[], content[], height[], rungs[], ladders[], crossings[]}`: every edge with its holds and the tiers either
        /// side, the rungs and the named ladders, and per window and height edge the sizes `crossings` uses each way.</summary>
        static void WritePolicy(Utf8JsonWriter w, string themeId, TierPolicy policy)
        {
            w.WriteStartObject("policy");
            w.WriteString("theme", themeId);
            WriteLadder(w, "window", policy.Window, s_widthTiers);
            WriteLadder(w, "content", policy.Content, s_widthTiers);
            WriteLadder(w, "height", policy.Height, s_heightTiers);
            w.WriteStartArray("rungs");
            foreach (NamedRung r in policy.Rungs)
            {
                w.WriteStartObject();
                w.WriteString("id", r.Id);
                w.WriteString("axis", r.Axis.ToString().ToLowerInvariant());
                Inspect.Reply.Num(w, "at", r.Edge.At);
                Inspect.Reply.Num(w, "narrowHold", r.Edge.NarrowHold);
                Inspect.Reply.Num(w, "widenHold", r.Edge.WidenHold);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("ladders");
            foreach (NamedLadder l in policy.Ladders)
            {
                w.WriteStartObject();
                w.WriteString("id", l.Id);
                WriteLadder(w, "edges", l.Ladder, []);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("crossings");
            foreach (EdgeRow e in EdgesOf(policy))
            {
                w.WriteStartObject();
                w.WriteString("edge", e.Name);
                w.WriteString("axis", e.Height ? "h" : "w");
                foreach (bool narrow in (ReadOnlySpan<bool>)[true, false])
                {
                    var (mount, old, @new) = CrossingSizes(e.Edge, narrow);
                    w.WriteStartObject(narrow ? "narrow" : "widen");
                    Inspect.Reply.Num(w, "mount", mount);
                    Inspect.Reply.Num(w, "old", old);
                    Inspect.Reply.Num(w, "new", @new);
                    w.WriteEndObject();
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }

        /// <summary>`tiers {theme?}`: the mounted window's tier facts (as before) and the theme's `policy`; with `theme=` and nothing
        /// mounted, the policy alone.</summary>
        string Tiers(int id, string name, Inspect.Args a)
        {
            string themeId = a.Str("theme", InspectKit.ActiveThemeId);
            if (PolicyOf(themeId) is not { } policy) return Inspect.Reply.Fail(id, name, "no theme '" + themeId + "' is installed");
            bool facts = _shell is not null && string.Equals(themeId, InspectKit.ActiveThemeId, StringComparison.OrdinalIgnoreCase);
            return Inspect.Reply.Ok(id, name, w =>
            {
                if (facts) InspectDump.WriteTiers(w);
                WritePolicy(w, themeId, policy);
            });
        }

        /// <summary>`crossings {routes?, edges?, dirs?, control?, w?, h?, theme?, kind?, gpu?, scale?, images?, items?, max?, + the lint options}`:
        /// every edge of the theme both ways over every route, one `crossing` reply per row (see the file header).</summary>
        string Crossings(int id, string name, Inspect.Args a)
        {
            string theme = a.Str("theme", InspectKit.ActiveThemeId).ToLowerInvariant();
            if (!InspectKit.IsShipped(theme)) return Inspect.Reply.Fail(id, name, "unknown theme '" + theme + "' (" + string.Join("|", InspectKit.ThemeIds) + ")");
            if (!InspectLintScene.TryOptions(a, out _, out string lintError)) return Inspect.Reply.Fail(id, name, lintError);
            if (PolicyOf(theme) is not { } policy) return Inspect.Reply.Fail(id, name, "theme '" + theme + "' has no tier policy");
            List<string> routes = a.List("routes");
            if (routes.Count == 0) routes = ["home", "album", "artist", "playlist"];
            List<string> only = a.List("edges");
            string dirs = a.Str("dirs", "both");
            float fixedW = a.Float("w", 1440f), fixedH = a.Float("h", 960f);
            var edges = EdgesOf(policy);
            if (only.Count > 0) edges.RemoveAll(e => !only.Contains(e.Name, StringComparer.OrdinalIgnoreCase));
            if (edges.Count == 0) return Inspect.Reply.Fail(id, name, "no edge to cross (the theme's: " + string.Join(",", EdgesOf(policy).Select(static e => e.Name)) + ")");

            var replies = new List<string>();
            var table = new List<string>();
            int failed = 0;
            var clock = Stopwatch.StartNew();
            foreach (string route in routes)
            {
                var (navArg, navValue) = CrossingsRoute(route);
                foreach (EdgeRow edge in edges)
                {
                    foreach (bool narrow in (ReadOnlySpan<bool>)[true, false])
                    {
                        if (dirs == (narrow ? "widen" : "narrow")) continue;
                        long t0 = clock.ElapsedMilliseconds;
                        var (mount, old, @new) = CrossingSizes(edge.Edge, narrow);
                        (float W, float H) Size(float v) => edge.Height ? (fixedW, v) : (v, fixedH);
                        // A fresh mount inside the old tier for every row: the state one row leaves (a pane, a scroll) never reaches the next.
                        var mountArgs = new Inspect.Args();
                        mountArgs.Set("theme", new Inspect.Value(Inspect.ValueKind.String, theme));
                        mountArgs.Set(navArg, new Inspect.Value(Inspect.ValueKind.String, navValue));
                        foreach (string pass in (ReadOnlySpan<string>)["kind", "gpu", "scale", "images", "grade"])
                            if (a.TryGet(pass, out Inspect.Value v)) mountArgs.Set(pass, v);
                        var (mw, mh) = Size(mount);
                        string row(long ms) => RowJson(theme, route, edge, narrow, Size(mount), Size(old), Size(@new), ms);
                        if (!MountShell(mountArgs, mw, mh, out string error, out _))
                        {
                            failed++;
                            replies.Add(Splice(Inspect.Reply.Fail(id, "crossing", "the row's mount failed: " + error), row(clock.ElapsedMilliseconds - t0)));
                            continue;
                        }
                        Settle(a.Int("max", 600));
                        var (ow, oh) = Size(old);
                        var (nw, nh) = Size(@new);
                        string reply = Crossing(id, "crossing", CrossingArgs(a, ow, oh, nw, nh));
                        long elapsed = clock.ElapsedMilliseconds - t0;
                        replies.Add(Splice(reply, row(elapsed)));
                        if (reply.Contains("\"ok\":false", StringComparison.Ordinal)) failed++;
                        table.Add(TableRow(reply, route, edge.Name, narrow, elapsed));
                    }
                }
            }
            long total = clock.ElapsedMilliseconds;
            return Inspect.Reply.Ok(id, name, w =>
            {
                w.WriteString("theme", theme);
                w.WriteNumber("rows", table.Count);
                w.WriteNumber("failed", failed);
                w.WriteNumber("ms", total);
                w.WriteStartArray("table");
                foreach (string t in table) w.WriteRawValue(t, skipInputValidation: true);
                w.WriteEndArray();
                w.WriteStartArray("crossings");
                foreach (string r in replies) w.WriteRawValue(r, skipInputValidation: true);
                w.WriteEndArray();
            });
        }

        /// <summary>The args of one `crossing` over the live mount: from, to, frames 1, items, max and the batch's lint options.</summary>
        static Inspect.Args CrossingArgs(Inspect.Args batch, float fromW, float fromH, float toW, float toH)
        {
            var args = new Inspect.Args();
            args.Set("from", new Inspect.Value(Inspect.ValueKind.String, Dip(fromW) + "x" + Dip(fromH)));
            args.Set("to", new Inspect.Value(Inspect.ValueKind.String, Dip(toW) + "x" + Dip(toH)));
            args.Set("frames", new Inspect.Value(Inspect.ValueKind.Number, Num: 1));
            args.Set("items", new Inspect.Value(Inspect.ValueKind.Number, Num: batch.Int("items", 8)));
            args.Set("max", new Inspect.Value(Inspect.ValueKind.Number, Num: batch.Int("max", 600)));
            foreach (string n in batch.Names)
                if (Inspect.IsCrossingLintArg(n) && batch.TryGet(n, out Inspect.Value v)) args.Set(n, v);
            if (batch.TryGet("control", out Inspect.Value control)) args.Set("control", control);   // the crossing's own control step (on by default)
            if (!batch.Has("min")) args.Set("min", new Inspect.Value(Inspect.ValueKind.String, "error"));
            return args;
        }

        /// <summary>`reply` with `"row":{...}` spliced in as its first member (the reply's own shape is kept whole).</summary>
        static string Splice(string reply, string rowMember) => reply.Length > 1 && reply[0] == '{' ? "{" + rowMember + "," + reply[1..] : reply;

        /// <summary>`"row":{theme, route, edge, axis, dir, mount, old, new, ms}`.</summary>
        static string RowJson(string theme, string route, EdgeRow edge, bool narrow, (float W, float H) mount, (float W, float H) old, (float W, float H) @new,
            long ms)
            => Fragment(w =>
            {
                w.WriteStartObject("row");
                w.WriteString("theme", theme);
                w.WriteString("route", route);
                w.WriteString("edge", edge.Name);
                w.WriteString("axis", edge.Height ? "h" : "w");
                w.WriteString("dir", narrow ? "narrow" : "widen");
                w.WriteString("mount", Dip(mount.W) + "x" + Dip(mount.H));
                w.WriteString("old", Dip(old.W) + "x" + Dip(old.H));
                w.WriteString("new", Dip(@new.W) + "x" + Dip(@new.H));
                w.WriteNumber("ms", ms);
                w.WriteEndObject();
            });

        /// <summary>One `table` line read back from a crossing reply: route, edge, dir, the tiers before and after (window/height/content),
        /// contentCommitted, and the counts (the excess ones: what the edge adds over the crossing's control step; remounts outside a swap family).</summary>
        static string TableRow(string reply, string route, string edge, bool narrow, long ms)
        {
            using JsonDocument doc = JsonDocument.Parse(reply);
            JsonElement r = doc.RootElement;
            static string Tiers(JsonElement r, string name)
                => r.TryGetProperty(name, out JsonElement t)
                    ? t.GetProperty("window").GetString() + "/" + t.GetProperty("height").GetString() + "/" + t.GetProperty("content").GetString()
                    : "?";
            return Fragment(w =>
            {
                w.WriteStartObject("t");
                w.WriteString("route", route);
                w.WriteString("edge", edge);
                w.WriteString("dir", narrow ? "narrow" : "widen");
                w.WriteBoolean("ok", r.TryGetProperty("ok", out JsonElement ok) && ok.GetBoolean());
                w.WriteString("before", Tiers(r, "tierBefore"));
                w.WriteString("after", Tiers(r, "tierAfter"));
                if (r.TryGetProperty("contentCommitted", out JsonElement cc)) w.WriteString("contentCommitted", cc.GetString());
                if (r.TryGetProperty("counts", out JsonElement counts))
                    foreach (JsonProperty p in counts.EnumerateObject())
                        if (p.Name is "snapped" or "jumped" or "excessSnapped" or "excessJumped" or "glided" or "remounted" or "remountedOutside" or "vanished" or "appeared" or "unpainted")
                            w.WriteNumber(p.Name, p.Value.GetInt32());
                if (r.TryGetProperty("error", out JsonElement err)) w.WriteString("error", err.GetString());
                w.WriteNumber("ms", ms);
                w.WriteEndObject();
            }, member: false);
        }

        /// <summary>The JSON <paramref name="body"/> writes inside an object: with <paramref name="member"/> the member text (`"row":{...}`),
        /// else the one object value it wrote.</summary>
        static string Fragment(Action<Utf8JsonWriter> body, bool member = true)
        {
            var buffer = new ArrayBufferWriter<byte>(256);
            using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                w.WriteStartObject();
                body(w);
                w.WriteEndObject();
            }
            string text = Encoding.UTF8.GetString(buffer.WrittenSpan);
            string inner = text[1..^1];   // `"row":{...}`
            if (member) return inner;
            int colon = inner.IndexOf(':');
            return inner[(colon + 1)..];
        }
    }
}
