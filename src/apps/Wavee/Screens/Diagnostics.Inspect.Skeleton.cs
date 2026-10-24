// ── Screens/Diagnostics.Inspect.Skeleton.cs — the `skeleton` verb: a page's loading skeleton against the page loaded, in DIP ───────────
// `skeleton {loaded, pending?, max?, items?, tolerance?}`
//
// Role: SHELL (UI thread; drives the headless shell and reads the engine's public scene inspection, like `crossing`).
// Spec: handoff/audit-2026-10-23/FIX-PLAN.md lane A3 item 3 ("a skeleton-vs-loaded compare verb"); the case is visual.md 11: Zune's artist
// skeleton at Compact is 67 DIP shorter than the loaded page ("popular" at y395 in the skeleton, y462 loaded), so the page jumps on load.
//
// THE SEQUENCE. Go to the PENDING page (a fixture the fake catalogue never answers, so it stays on its skeleton: `arzzqx`, `shzzqx`,
// `epzzqx`, the default per kind of `loaded`) and settle with `loops=skeletons` (the shimmer does not hold the settle); remember every shown
// keyed node of the content card and every `Skel.Region` (a SkeletonRegion node), keys with the pending id read as the loaded one. Go to the
// LOADED page and settle; take the same. A key both show is compared: its top moved (dy) and its height changed (dh). The regions' own height
// change is the skeleton's error; the first key that moved down (and how far) is the jump a person sees when the data lands.
// Report-only: no gate reads it.

using System.Text.Json;
using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace Wavee;

public static partial class Diagnostics
{
    public sealed partial class InspectSession
    {
        /// <summary>The pending fixture per kind: what the fake catalogue never answers (Entities.Fake.Home.cs).</summary>
        static string PendingFor(string loaded) => loaded.Split(':') is [_, string kind, _] ? kind switch
        {
            "artist" => "spotify:artist:arzzqx",
            "show" => "spotify:show:shzzqx",
            "episode" => "spotify:episode:epzzqx",
            _ => "",
        } : "";

        /// <summary>Every shown keyed node under the content card (repeated keys left out: rows compare by count, not by place), and every
        /// skeleton region by its key or path; keys read with <paramref name="from"/> replaced by <paramref name="to"/>.</summary>
        static (Dictionary<string, RectF> Keyed, Dictionary<string, RectF> Regions) SkeletonRects(Shell.HeadlessShell shell, string from, string to)
        {
            SceneStore scene = shell.Host.Scene;
            var keyed = new Dictionary<string, RectF>(StringComparer.Ordinal);
            var twice = new HashSet<string>(StringComparer.Ordinal);
            var regions = new Dictionary<string, RectF>(StringComparer.Ordinal);
            NodeHandle card = shell.Find("shell.content.card");
            NodeHandle root = card.IsNull ? scene.Root : card;
            foreach (NodeHandle n in InspectKit.Subtree(scene, root))
            {
                if (!scene.IsShown(n)) continue;
                RectF r = scene.AbsoluteRect(n);
                if (r.W <= 0f || r.H <= 0f) continue;
                if (NodeDescriber.ElementTypeName(scene.ElementTypeId(n)) == "SkeletonRegion")
                    regions[Normal(InspectKit.KeyOf(scene, n), from, to)] = r;
                if (scene.DebugKeyOf(n) is not { Length: > 0 } key) continue;
                key = Normal(key, from, to);
                if (!keyed.TryAdd(key, r)) twice.Add(key);
            }
            foreach (string key in twice) keyed.Remove(key);
            return (keyed, regions);
        }

        static string Normal(string key, string from, string to) => from.Length > 0 ? key.Replace(from, to, StringComparison.Ordinal) : key;

        string Skeleton(int id, string name, Inspect.Args a, Shell.HeadlessShell shell)
        {
            string loaded = a.Str("loaded");
            string pending = a.Str("pending", PendingFor(loaded));
            if (loaded.Length == 0 || pending.Length == 0)
                return Inspect.Reply.Fail(id, name, "skeleton wants loaded=<spotify uri> and pending=<a uri the fake never answers> (artist, show and episode have a default)");
            int max = Math.Clamp(a.Int("max", 600), 1, 100_000), items = Math.Clamp(a.Int("items", 20), 1, 500);
            float tolerance = a.Float("tolerance", 1f);
            Shell.Route pendingRoute = Wavee.Shell.For(EntityUri.Parse(pending)), loadedRoute = Wavee.Shell.For(EntityUri.Parse(loaded));
            if (pendingRoute.Kind == Wavee.Shell.RouteKind.NotFound || loadedRoute.Kind == Wavee.Shell.RouteKind.NotFound)
                return Inspect.Reply.Fail(id, name, "no page for '" + (pendingRoute.Kind == Wavee.Shell.RouteKind.NotFound ? pending : loaded) + "'");
            string pendingId = pending[(pending.LastIndexOf(':') + 1)..], loadedId = loaded[(loaded.LastIndexOf(':') + 1)..];

            Wavee.Shell.GoTo(pendingRoute);
            InspectKit.SettleResult s1 = Settle(max, InspectKit.LoopMode.Skeletons);
            var (skelKeyed, skelRegions) = SkeletonRects(shell, pendingId, loadedId);
            Wavee.Shell.GoTo(loadedRoute);
            InspectKit.SettleResult s2 = Settle(max);
            var (loadKeyed, loadRegions) = SkeletonRects(shell, "", "");

            var moved = new List<(string Key, RectF Skel, RectF Load)>();
            int matched = 0;
            foreach (var (key, skel) in skelKeyed)
            {
                if (!loadKeyed.TryGetValue(key, out RectF load)) continue;
                matched++;
                if (MathF.Abs(load.Y - skel.Y) > tolerance || MathF.Abs(load.H - skel.H) > tolerance) moved.Add((key, skel, load));
            }
            moved.Sort(static (x, y) => x.Skel.Y.CompareTo(y.Skel.Y));
            var regions = new List<(string Key, RectF Skel, RectF Load)>();
            foreach (var (key, skel) in skelRegions)
                if (loadRegions.TryGetValue(key, out RectF load)) regions.Add((key, skel, load));
            (string Key, float Dy)? first = null;
            foreach (var (key, skel, load) in moved)
                if (load.Y - skel.Y > tolerance && skel.Y < shell.Window.ClientSizePx.Height / Math.Max(0.01f, shell.Window.Scale)) { first = (key, load.Y - skel.Y); break; }

            return Inspect.Reply.Ok(id, name, w =>
            {
                w.WriteString("pending", pending);
                w.WriteString("loaded", loaded);
                w.WriteBoolean("pendingSettled", s1.Settled);
                w.WriteBoolean("loadedSettled", s2.Settled);
                w.WriteStartObject("counts");
                w.WriteNumber("skeletonKeys", skelKeyed.Count);
                w.WriteNumber("loadedKeys", loadKeyed.Count);
                w.WriteNumber("matched", matched);
                w.WriteNumber("moved", moved.Count);
                w.WriteNumber("regions", regions.Count);
                w.WriteEndObject();
                if (first is { } f)
                {
                    w.WriteStartObject("firstMoved");   // what a person sees jump when the data lands: the first key on screen that moved down
                    w.WriteString("key", f.Key);
                    Inspect.Reply.Num(w, "dy", f.Dy);
                    w.WriteEndObject();
                }
                else w.WriteNull("firstMoved");
                w.WriteStartArray("regions");
                foreach (var (key, skel, load) in regions.Take(items))
                {
                    w.WriteStartObject();
                    w.WriteString("key", key);
                    Inspect.Reply.Rect(w, "skeleton", skel);
                    Inspect.Reply.Rect(w, "loaded", load);
                    Inspect.Reply.Num(w, "dh", load.H - skel.H);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteStartArray("moved");
                foreach (var (key, skel, load) in moved.Take(items))
                {
                    w.WriteStartObject();
                    w.WriteString("key", key);
                    Inspect.Reply.Rect(w, "skeleton", skel);
                    Inspect.Reply.Rect(w, "loaded", load);
                    Inspect.Reply.Num(w, "dy", load.Y - skel.Y);
                    Inspect.Reply.Num(w, "dh", load.H - skel.H);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            });
        }
    }
}
