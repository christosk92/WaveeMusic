// ── Screens/Diagnostics.Inspect.Setting.cs ─────────────────────────────────────────────────────────────────────────────
// the `setting` verb: list the app's Settings rows with their values, or set one through the row's own writer (the Settings path)
//
// Role: SHELL (UI thread; reads SettingsDoc, the Settings page as data)
// Spec: handoff/audit-2026-10-23/FIX-PLAN.md lane A2 item 6 (motion.md §6 tooling 5: no verb set app settings such as the lyrics blur).
//
// THE PATH. Every Settings row is a `SettingsRow` (Screens/SettingsDoc.cs) with ONE writer, the verb a Settings view calls: a toggle's
// bool, a choice's index, a slider's number, an action's press. `setting id=<row> value=<v>` calls that writer, and when it answers
// "re-read" bumps the page epoch as a view does (`Settings.Bump`), so a mounted Settings page, the lyrics stage or the bar reads the new
// value exactly as after a click. Theme options (`option`) are a theme's own; this verb is the app's settings.

using System.Globalization;
using System.Text.Json;

namespace Wavee;

public static partial class Diagnostics
{
    public sealed partial class InspectSession
    {
        /// <summary>`setting {tab?, id?, value?, + the settle args}`: no `id` lists the rows (of `tab`), else sets one.</summary>
        string Setting(int id, string name, Inspect.Args a)
        {
            Settings.Tab? tab = null;
            if (a.Has("tab"))
            {
                int t = Array.FindIndex(Settings.TabSlugs, s => string.Equals(s, a.Str("tab"), StringComparison.OrdinalIgnoreCase));
                if (t < 0) return Inspect.Reply.Fail(id, name, "unknown tab '" + a.Str("tab") + "' (" + string.Join("|", Settings.TabSlugs) + ")");
                tab = (Settings.Tab)t;
            }
            if (!a.Has("id"))
            {
                return Inspect.Reply.Ok(id, name, w =>
                {
                    w.WriteStartArray("rows");
                    foreach (SettingsRow row in SettingsDoc.AllRows)
                    {
                        if (tab is { } only && row.Tab != only) continue;
                        if (!row.Visible(SettingsFacts.Live)) continue;
                        WriteRow(w, row);
                    }
                    w.WriteEndArray();
                });
            }
            string rowId = a.Str("id");
            var matches = new List<SettingsRow>();
            foreach (SettingsRow row in SettingsDoc.AllRows)
                if ((tab is not { } only || row.Tab == only) && string.Equals(row.Id, rowId, StringComparison.OrdinalIgnoreCase)) matches.Add(row);
            if (matches.Count == 0) return Inspect.Reply.Fail(id, name, "no Settings row '" + rowId + "'" + (tab is null ? "" : " on " + Settings.TabSlugs[(int)tab]) + " (setting lists them)");
            if (matches.Count > 1) return Inspect.Reply.Fail(id, name, "'" + rowId + "' is a row of several tabs: add tab= (" + string.Join("|", matches.Select(static r => Settings.TabSlugs[(int)r.Tab])) + ")");
            SettingsRow target = matches[0];
            if (!a.Has("value") && target.Kind != SettingsRowKind.Action)   // `setting id=<row>`: read it
                return Inspect.Reply.Ok(id, name, w => WriteRow(w, target, named: "row"));
            SettingsWriter writer = target.Writer;
            if (writer.IsReadOnly || writer.IsOwned)
                return Inspect.Reply.Fail(id, name, "'" + target.Id + "' has no writer of its own (" + (writer.IsOwned ? "its card's verbs are " + writer.Verb + "'s" : "read-only") + ")");
            bool reread;
            Inspect.Value raw = a.TryGet("value", out Inspect.Value v) ? v : default;
            // What the next mount puts back (a write on a live mount is that mount's; one with nothing mounted is for the next mount).
            if (_shell is not null && target.Kind != SettingsRowKind.Action && !_settingsWritten.Exists(e => ReferenceEquals(e.Row, target)))
                _settingsWritten.Add((target, target.Current));
            if (_shell is null) _settingsForNext = true;
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
                WriteRow(w, target, named: "row");
                w.WriteBoolean("reread", reread);
            };
            if (_shell is null) return Inspect.Reply.Ok(id, name, body);   // nothing mounted: the store holds it for the next mount
            RunFrames(1);
            return Settled(id, name, a, body);
        }

        /// <summary>The rows `setting` wrote on a live mount, with the value each had before: the next mount puts them back.</summary>
        readonly List<(SettingsRow Row, object? Was)> _settingsWritten = [];
        bool _settingsForNext;   // `setting` wrote with nothing mounted: for the next mount, which keeps it

        /// <summary>Put back the rows `setting` wrote on the last mount, through their own writers (a toggle, a slider, a choice by index), unless
        /// a `setting` with nothing mounted asked the next mount to keep its values.</summary>
        void RestoreSettings()
        {
            bool keep = _settingsForNext;
            _settingsForNext = false;
            if (keep) { _settingsWritten.Clear(); return; }
            bool reread = false;
            foreach (var (row, was) in _settingsWritten)
            {
                SettingsWriter w = row.Writer;
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

        /// <summary>`{tab, section, id, kind, value, writer, readOnly?, enabled?, reason?}` (or as the member <paramref name="named"/>).</summary>
        static void WriteRow(Utf8JsonWriter w, SettingsRow row, string? named = null)
        {
            if (named is null) w.WriteStartObject();
            else w.WriteStartObject(named);
            w.WriteString("tab", Settings.TabSlugs[(int)row.Tab]);
            w.WriteString("section", row.Section);
            w.WriteString("id", row.Id);
            w.WriteString("kind", row.Kind.ToString().ToLowerInvariant());
            w.WritePropertyName("value");
            switch (row.Current)
            {
                case null: w.WriteNullValue(); break;
                case bool b: w.WriteBooleanValue(b); break;
                case int i: w.WriteNumberValue(i); break;
                case float f: w.WriteNumberValue(float.IsFinite(f) ? Math.Round(f, 3) : 0d); break;
                case double d: w.WriteNumberValue(double.IsFinite(d) ? Math.Round(d, 3) : 0d); break;
                case object o: w.WriteStringValue(Convert.ToString(o, CultureInfo.InvariantCulture)); break;
            }
            w.WriteString("writer", row.Writer.Verb);
            if (row.Writer.IsReadOnly) w.WriteBoolean("readOnly", true);
            SettingsEnablement enabled = row.Enabled(SettingsFacts.Live);
            if (!enabled.Enabled)
            {
                w.WriteBoolean("enabled", false);
                if (enabled.ReasonKey is { } reason) w.WriteString("reason", reason);
            }
            w.WriteEndObject();
        }
    }
}
