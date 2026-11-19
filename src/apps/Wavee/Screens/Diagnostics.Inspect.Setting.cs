// ── Screens/Diagnostics.Inspect.Setting.cs ─────────────────────────────────────────────────────────────────────────────
// the `setting` verb: list the app's Settings dials with their values, or set one through the dial's own writer (the Settings path)
//
// Role: SHELL (UI thread; reads SettingsSpec, the settings registry)
// Spec: handoff/audit-2026-10-23/FIX-PLAN.md lane A2 item 6 (motion.md §6 tooling 5: no verb set app settings such as the lyrics blur);
// handoff/settings-2026-11-18/DESIGN.md phase 1 (dials by their registry id and by their old row id).
//
// THE PATH. Every setting is a `SettingsDial` (Screens/SettingsSpec.cs) with ONE writer, the verb a Settings view calls: a toggle's
// bool, a choice's index, a slider's number, an action's press. `setting id=<dial> value=<v>` calls that writer, and when it answers
// "re-read" bumps the page epoch as a view does (`Settings.Bump`), so a mounted Settings page, the lyrics stage or the bar reads the new
// value exactly as after a click. A dial answers to its registry id (`playback.crossfade`) and to its old row id (`crossfade`); `tab=`
// (an old tab slug) picks between old ids two tabs shared. The active theme's options are dials too (`appearance.option.<qualifiedId>`);
// `option` stays the verb that writes a theme option by its own grammar.

using System.Globalization;
using System.Text.Json;

namespace Wavee;

public static partial class Diagnostics
{
    public sealed partial class InspectSession
    {
        /// <summary>`setting {category?, tab?, id?, value?, + the settle args}`: no `id` lists every dial (of `category` or old `tab`), else
        /// reads or sets one.</summary>
        string Setting(int id, string name, Inspect.Args a)
        {
            Settings.Tab? tab = null;
            if (a.Has("tab"))
            {
                int t = Array.FindIndex(Settings.TabSlugs, s => string.Equals(s, a.Str("tab"), StringComparison.OrdinalIgnoreCase));
                if (t < 0) return Inspect.Reply.Fail(id, name, "unknown tab '" + a.Str("tab") + "' (" + string.Join("|", Settings.TabSlugs) + ")");
                tab = (Settings.Tab)t;
            }
            SettingsCategory? only = null;
            if (a.Has("category"))
            {
                if (!SettingsSpec.TryCategory(a.Str("category"), out var c))
                    return Inspect.Reply.Fail(id, name, "unknown category '" + a.Str("category") + "' (" + string.Join("|", SettingsSpec.Categories.Select(static k => k.Slug)) + ", or an old slug)");
                only = c;
            }
            if (!a.Has("id"))
            {
                return Inspect.Reply.Ok(id, name, w =>
                {
                    int count = 0, live = 0;
                    w.WriteStartArray("dials");
                    foreach (var category in SettingsSpec.Categories)
                    {
                        if (only is not null && !ReferenceEquals(category, only)) continue;
                        foreach (var group in category.Groups)
                        {
                            foreach (var dial in group.Dials)
                            {
                                if (tab is { } t && !(SettingsDoc.TryAddress(dial, out var was, out _) && was == t)) continue;
                                WriteDial(w, dial, category, group);
                                count++;
                            }
                            if (group.Live is { } liveDials && tab is null)
                                foreach (var dial in liveDials())
                                {
                                    WriteDial(w, dial, category, group, live: true);
                                    live++;
                                }
                        }
                    }
                    w.WriteEndArray();
                    w.WriteNumber("count", count);
                    w.WriteNumber("live", live);
                });
            }
            string dialName = a.Str("id");
            SettingsDial target;
            if (tab is { } oldTab && SettingsDoc.TryRow(oldTab, dialName, out var onTab)) target = onTab;
            else if (!SettingsSpec.TryResolve(dialName, out target, out var matches))
            {
                if (matches.Count == 0)
                    return Inspect.Reply.Fail(id, name, "no Settings dial '" + dialName + "'" + (tab is null ? "" : " on " + Settings.TabSlugs[(int)tab]) + " (setting lists them)");
                return Inspect.Reply.Fail(id, name, "'" + dialName + "' is the old id of several dials: name one (" + string.Join("|", matches.Select(static d => d.Id)) + ") or add tab=");
            }
            if (!a.Has("value") && target.Kind != SettingsRowKind.Action)   // `setting id=<dial>`: read it
                return Inspect.Reply.Ok(id, name, w => WriteDial(w, target, named: "dial"));
            SettingsWriter writer = target.Writer;
            if (writer.IsReadOnly || writer.IsOwned)
                return Inspect.Reply.Fail(id, name, "'" + target.Id + "' has no writer of its own (" + (writer.IsOwned ? "its card's verbs are " + writer.Verb + "'s" : "read-only") + ")");
            bool reread;
            Inspect.Value raw = a.TryGet("value", out Inspect.Value v) ? v : default;
            // What the next mount puts back (a write on a live mount is that mount's; one with nothing mounted is for the next mount).
            if (_shell is not null && target.Kind != SettingsRowKind.Action && !_settingsWritten.Exists(e => ReferenceEquals(e.Dial, target)))
                _settingsWritten.Add((target, target.Current));
            if (_shell is null) _settingsForNext = true;
            using var source = SettingsWrites.From(SettingsSurfaces.Inspector);   // Recently changed says "from the inspector"
            switch (target.Kind)
            {
                case SettingsRowKind.Action:
                    reread = writer.Run!();
                    break;
                case SettingsRowKind.Toggle when writer.Toggle is { } toggle:
                    {
                        if (raw.Kind == Inspect.ValueKind.Bool) reread = toggle(raw.Bool);
                        else if (raw.ToString() is "true" or "on" or "1" or "false" or "off" or "0") reread = toggle(raw.ToString() is "true" or "on" or "1");
                        else return Inspect.Reply.Fail(id, name, "'" + target.Id + "' is a toggle: value=true|false");
                        break;
                    }
                case SettingsRowKind.Slider when writer.Slide is { } slide:
                    {
                        if (!a.TryNum("value", out double f)) return Inspect.Reply.Fail(id, name, "'" + target.Id + "' is a slider: value=<number>");
                        reread = slide((float)f);
                        break;
                    }
                default:
                    {
                        if (writer.Choose is not { } choose) return Inspect.Reply.Fail(id, name, "'" + target.Id + "' (" + target.Kind + ") takes no value here");
                        if (!a.TryNum("value", out double n) || n != Math.Floor(n) || n < 0) return Inspect.Reply.Fail(id, name, "'" + target.Id + "' is a " + target.Kind.ToString().ToLowerInvariant() + ": value=<index>");
                        reread = choose((int)n);
                        break;
                    }
            }
            if (reread) Settings.Bump();   // as a Settings view does after a writer that asks the page to re-read
            Action<Utf8JsonWriter> body = w =>
            {
                WriteDial(w, target, named: "dial");
                w.WriteBoolean("reread", reread);
            };
            if (_shell is null) return Inspect.Reply.Ok(id, name, body);   // nothing mounted: the store holds it for the next mount
            RunFrames(1);
            return Settled(id, name, a, body);
        }

        /// <summary>The dials `setting` wrote on a live mount, with the value each had before: the next mount puts them back.</summary>
        readonly List<(SettingsDial Dial, object? Was)> _settingsWritten = [];
        bool _settingsForNext;   // `setting` wrote with nothing mounted: for the next mount, which keeps it

        /// <summary>Put back the dials `setting` wrote on the last mount, through their own writers (a toggle, a slider, a choice by index),
        /// unless a `setting` with nothing mounted asked the next mount to keep its values.</summary>
        void RestoreSettings()
        {
            bool keep = _settingsForNext;
            _settingsForNext = false;
            if (keep) { _settingsWritten.Clear(); return; }
            using var quiet = SettingsWrites.Quiet();   // putting values back is not a change anyone made: no Recently changed, no retirement
            bool reread = false;
            foreach (var (dial, was) in _settingsWritten)
            {
                SettingsWriter w = dial.Writer;
                switch (was)
                {
                    case bool b when w.Toggle is { } toggle: reread |= toggle(b); break;
                    case float f when w.Slide is { } slide: reread |= slide(f); break;
                    case int i when w.Slide is { } slide: reread |= slide(i); break;
                    case int i when w.Choose is { } choose: reread |= choose(i); break;
                }
            }
            _settingsWritten.Clear();
            if (reread) Settings.Bump();
        }

        /// <summary>`{id, legacy?, category, group, parent?, tier?, was?, kind, value, writer, readOnly?, enabled?, reason?, visible?, live?}`
        /// (or as the member <paramref name="named"/>). <c>was</c> is the old address (<c>tab/rowId</c>) of a dial the old pages drew.</summary>
        static void WriteDial(Utf8JsonWriter w, SettingsDial dial, SettingsCategory? category = null, SettingsGroup? group = null,
            bool live = false, string? named = null)
        {
            if (named is null) w.WriteStartObject();
            else w.WriteStartObject(named);
            w.WriteString("id", dial.Id);
            if (dial.LegacyIds.Count > 0)
            {
                w.WriteStartArray("legacy");
                foreach (string legacy in dial.LegacyIds) w.WriteStringValue(legacy);
                w.WriteEndArray();
            }
            if (category is null && SettingsSpec.TryPlace(dial, out var c, out var g)) (category, group) = (c, g);
            if (category is not null) w.WriteString("category", category.Slug);
            if (group is not null) w.WriteString("group", group.Id);
            if (dial.Parent is { } parent) w.WriteString("parent", parent);
            if (dial.Tier != SettingsTier.Common) w.WriteString("tier", dial.Tier.ToString().ToLowerInvariant());
            if (SettingsDoc.TryAddress(dial, out var tab, out var rowId)) w.WriteString("was", Settings.TabSlugs[(int)tab] + "/" + rowId);
            w.WriteString("kind", dial.Kind.ToString().ToLowerInvariant());
            w.WritePropertyName("value");
            switch (dial.Current)
            {
                case null: w.WriteNullValue(); break;
                case bool b: w.WriteBooleanValue(b); break;
                case int i: w.WriteNumberValue(i); break;
                case float f: w.WriteNumberValue(float.IsFinite(f) ? Math.Round(f, 3) : 0d); break;
                case double d: w.WriteNumberValue(double.IsFinite(d) ? Math.Round(d, 3) : 0d); break;
                case object o: w.WriteStringValue(Convert.ToString(o, CultureInfo.InvariantCulture)); break;
            }
            w.WriteString("writer", dial.Writer.Verb);
            if (dial.Writer.IsReadOnly) w.WriteBoolean("readOnly", true);
            SettingsEnablement enabled = dial.Enabled(SettingsFacts.Live);
            if (!enabled.Enabled)
            {
                w.WriteBoolean("enabled", false);
                if (enabled.ReasonKey is { } reason) w.WriteString("reason", reason);
            }
            if (!dial.Visible(SettingsFacts.Live)) w.WriteBoolean("visible", false);
            if (live) w.WriteBoolean("live", true);
            w.WriteEndObject();
        }
    }
}
