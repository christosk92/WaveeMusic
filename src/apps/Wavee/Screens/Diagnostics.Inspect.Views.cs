// ── Screens/Diagnostics.Inspect.Views.cs ───────────────────────────────────────────────────────────────────────────────
// the `views` verb (which template draws every view key of a theme, and how many are a stand-in's) and the slot templates
// `dump` and `find` print beside a node
//
// Role: SHELL (UI thread; reads the theme catalogue and the engine's slot inspection, AppHost.CollectViewSlots)
// Spec: handoff/audit-2026-10-23/FIX-PLAN.md lane A2 item 1 (winui.md §1, §5.1).
//
// THE COUNT is Theming/Composition/ThemeViewInspection.cs (identity reads live in the composition layer): the theme's registry
// with and without its stand-in. `standins` counts view and style keys (WinUI on 2026-10-23: 52 view keys + 2 style keys = 54;
// Classic: 0). The template a slot resolved comes from the engine (`AppHost.CollectViewSlots`).

using System.Text.Json;
using FluentGpu.Foundation;
using FluentGpu.Scene;
using FluentGpu.Theming;
using Wavee.Theming;

namespace Wavee;

public static partial class Diagnostics
{
    /// <summary>The `views` report and the slot-template lookup `dump`/`find` use. UI thread.</summary>
    public static class InspectViews
    {
        /// <summary>`theme, layer, counts{views, styles, standinViews, standinStyles}, standins, views:[{key, templateId, layer, standin}],
        /// styles:[{key, layer, standin}]`; <paramref name="filter"/> `standins` lists only the stand-ins, `own` only the theme's own,
        /// <paramref name="prefix"/> only keys starting with it (the counts cover every key).</summary>
        public static void Write(Utf8JsonWriter w, ThemeViewInspection.Report r, string filter, string prefix)
        {
            w.WriteString("theme", r.Theme);
            w.WriteString("layer", r.Layer);
            w.WriteNumber("standins", r.StandInViews + r.StandInStyles);
            w.WriteStartObject("counts");
            w.WriteNumber("views", r.Views.Count);
            w.WriteNumber("styles", r.Styles.Count);
            w.WriteNumber("standinViews", r.StandInViews);
            w.WriteNumber("standinStyles", r.StandInStyles);
            w.WriteEndObject();
            w.WriteStartArray("views");
            foreach (ThemeViewInspection.Entry e in r.Views)
            {
                if (!Listed(e, filter, prefix)) continue;
                w.WriteStartObject();
                w.WriteString("key", e.Key);
                w.WriteString("templateId", e.TemplateId);
                w.WriteString("layer", e.Layer);
                if (e.StandIn) w.WriteBoolean("standin", true);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("styles");
            foreach (ThemeViewInspection.Entry e in r.Styles)
            {
                if (!Listed(e, filter, prefix)) continue;
                w.WriteStartObject();
                w.WriteString("key", e.Key);
                w.WriteString("layer", e.Layer);
                if (e.StandIn) w.WriteBoolean("standin", true);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            static bool Listed(in ThemeViewInspection.Entry e, string filter, string prefix)
                => (filter == "all" || (filter == "standins") == e.StandIn) && (prefix.Length == 0 || e.Key.StartsWith(prefix, StringComparison.Ordinal));
        }

        /// <summary>Every live slot anchor of the mount with the template it resolved (`dump` prints it on the anchor, `find` the nearest one above a node).</summary>
        public static Dictionary<NodeHandle, ViewSlotInfo> Slots(Shell.HeadlessShell shell)
        {
            var list = new List<ViewSlotInfo>();
            shell.Host.CollectViewSlots(list);
            var map = new Dictionary<NodeHandle, ViewSlotInfo>(list.Count);
            foreach (ViewSlotInfo s in list) map[s.Anchor] = s;
            return map;
        }

        /// <summary>The slot whose template drew <paramref name="node"/>: the nearest slot anchor at or above it, or false.</summary>
        public static bool TryDrawnBy(SceneStore scene, Dictionary<NodeHandle, ViewSlotInfo> slots, NodeHandle node, out ViewSlotInfo slot)
        {
            for (NodeHandle n = node; !n.IsNull && scene.IsLive(n); n = scene.Parent(n))
                if (slots.TryGetValue(n, out slot)) return true;
            slot = default;
            return false;
        }
    }

    public sealed partial class InspectSession
    {
        /// <summary>`views {theme?, filter?, prefix?}`: which template draws every view key of the theme (the active one by default) and which
        /// layer registered every view and style key; `standins` counts the keys a stand-in fills.</summary>
        static string Views(int id, string name, Inspect.Args a)
        {
            string themeId = a.Str("theme", InspectKit.ActiveThemeId);
            if (ThemeViewInspection.Of(themeId) is not { } report) return Inspect.Reply.Fail(id, name, "no theme '" + themeId + "' is installed");
            string filter = a.Str("filter", "all");
            string prefix = a.Str("prefix");
            return Inspect.Reply.Ok(id, name, w => InspectViews.Write(w, report, filter, prefix));
        }
    }
}
