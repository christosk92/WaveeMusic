// ── Screens/Diagnostics.Inspect.Hint.cs ───────────────────────────────────────────────────────────────────────────────
// the `hint` verb (every hint with its budget, force one for a mount, reset, the persisted blob) and the `recent` verb (Recently changed)
//
// Role: SHELL (UI thread; reads Hints, HintState, SettingsHome)
// Spec: handoff/settings-2026-11-18/DESIGN.md 8 phase 3 (the inspector: `hint list`, `hint show id=<hint>`, `hint reset`).
//
//   hint list            every hint of the table, in order, with its row (target, surface, action, priority, budget) and its state
//                        (shows, last-shown day, snoozed-until day, dismissals, retired) and the budget's verdict, in the app and on
//                        the Settings home (`Hints.Judge`: qualifies, waiting, notDrawn, tipsOff, retired, snoozed, ...).
//   hint show id=<hint>  forces the hint's trigger true for ONE mount: the slot of its surface mounts it (the stand-in until its theme
//                        draws it), whatever its trigger and budget say, and records no show. Shown now when a mount is up (the reply
//                        says whether a slot took it); with nothing mounted, for the next mount. The mount after that clears it.
//   hint reset           forgets every hint's budget, the use facts and the session (as a fresh launch with no blob); Recently changed stays.
//   hint state [blob=]   the persisted blob (`hints.state`); with `blob=` it is written to the store and read back as a launch reads it,
//                        which is how a run carries the blob over a server restart (the server's settings live in memory).
//   recent [clear=true]  the last five dial writes, newest first: dial, title, value now, source and its words, when.

using System.Globalization;
using System.Text.Json;
using FluentGpu.Localization;

namespace Wavee;

public static partial class Diagnostics
{
    public sealed partial class InspectSession
    {
        bool _hintForNext;   // `hint show` with nothing mounted: for the next mount, which keeps it

        /// <summary>The next mount after the one a forced hint showed on clears it (one mount); called by <c>ClearLeaks</c>.</summary>
        void RestoreHints()
        {
            if (_hintForNext) { _hintForNext = false; return; }
            Hints.ClearForced();
        }

        /// <summary>`hint {do, id?, blob?}`.</summary>
        string Hint(int id, string name, Inspect.Args a)
        {
            string verb = a.Str("do", "list");
            switch (verb)
            {
                case "list":
                    return Inspect.Reply.Ok(id, name, WriteHints);
                case "reset":
                    HintState.ResetHints();
                    HintState.ResetFacts();
                    Hints.ResetSession();
                    if (_shell is not null) RunFrames(1);
                    return Inspect.Reply.Ok(id, name, WriteHints);
                case "state":
                    if (a.Has("blob"))
                    {
                        Platform.Settings.Set(Platform.Keys.HintsState, a.Str("blob"));
                        HintState.Reload();
                        Hints.Bump();
                    }
                    return Inspect.Reply.Ok(id, name, w => w.WriteString("blob", HintState.Blob()));
                case "show":
                {
                    if (!a.Has("id")) return Inspect.Reply.Fail(id, name, "hint show needs id=<hint> (hint list names them)");
                    if (Hints.Find(a.Str("id")) is not { } def)
                        return Inspect.Reply.Fail(id, name, "no hint '" + a.Str("id") + "' (" + string.Join("|", Hints.All.Select(static h => h.Id)) + ")");
                    Hints.Force(def);
                    Action<Utf8JsonWriter> body = w =>
                    {
                        w.WriteString("id", def.Id);
                        w.WriteString("surface", def.Surface);
                        w.WriteString("key", "hint:" + def.Id);
                        if (_shell is not null) w.WriteBoolean("shown", string.Equals(Hints.OnScreen, def.Id, StringComparison.Ordinal) || def.IsState);
                        else w.WriteString("note", "nothing mounted: the next mount shows it on its surface");
                    };
                    if (_shell is null)
                    {
                        _hintForNext = true;
                        return Inspect.Reply.Ok(id, name, body);
                    }
                    RunFrames(1);
                    return Settled(id, name, a, body);
                }
                default:
                    return Inspect.Reply.Fail(id, name, "hint do=" + verb + "? (list|show|reset|state)");
            }
        }

        /// <summary>`{tipsOn, today, session?, onScreen?, forced?, hints:[{id, target, surface, action, priority, maxShows, snoozeDays,
        /// dismissRetires?, drawn, shows, lastDay, snoozedUntil, dismissals, retired, trigger, verdict, home}], suggestions:[ids]}`.</summary>
        static void WriteHints(Utf8JsonWriter w)
        {
            w.WriteBoolean("tipsOn", Hints.TipsOn);
            w.WriteNumber("today", DateOnly.FromDateTime(DateTime.Now).DayNumber);
            if (Hints.SessionHint is { } session) w.WriteString("session", session);
            if (Hints.OnScreen is { } onScreen) w.WriteString("onScreen", onScreen);
            if (Hints.Forced is { } forced) w.WriteString("forced", forced);
            w.WriteStartArray("hints");
            var all = Hints.All;
            for (int i = 0; i < all.Count; i++)
            {
                HintDef def = all[i];
                w.WriteStartObject();
                w.WriteString("id", def.Id);
                w.WriteString("target", def.Target);
                w.WriteString("surface", def.Surface);
                w.WriteString("action", Camel(def.Action.ToString()));
                w.WriteNumber("priority", def.Priority);
                if (def.IsState) w.WriteBoolean("state", true);
                else
                {
                    w.WriteNumber("maxShows", def.MaxShows);
                    w.WriteNumber("snoozeDays", def.SnoozeDays);
                    if (def.DismissRetires) w.WriteBoolean("dismissRetires", true);
                }
                w.WriteBoolean("drawn", def.Drawn);
                w.WriteNumber("shows", HintState.Shows(i));
                w.WriteNumber("lastDay", HintState.LastDay(i));
                w.WriteNumber("snoozedUntil", HintState.SnoozedUntil(i));
                w.WriteNumber("dismissals", HintState.Dismissals(i));
                w.WriteBoolean("retired", HintState.Retired(i));
                w.WriteBoolean("trigger", def.Applies() && def.Trigger());
                w.WriteString("verdict", Camel(Hints.Judge(def, inApp: true).ToString()));
                w.WriteString("home", Camel(Hints.Judge(def, inApp: false).ToString()));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("suggestions");
            foreach (HintDef def in Hints.Suggestions()) w.WriteStringValue(def.Id);
            w.WriteEndArray();
        }

        /// <summary>`recent {clear?}`: `{count, writes:[{dial, title?, value, surface, from?, atMs, agoMs}]}`.</summary>
        string Recent(int id, string name, Inspect.Args a)
        {
            if (a.Bool("clear", false)) HintState.ClearRecent();
            return Inspect.Reply.Ok(id, name, w =>
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var rows = SettingsHome.Recent();
                w.WriteNumber("count", rows.Count);
                w.WriteStartArray("writes");
                foreach (var row in rows)
                {
                    w.WriteStartObject();
                    w.WriteString("dial", row.DialId);
                    if (row.TitleKey is { } title) w.WriteString("title", Loc.Get(title));
                    w.WritePropertyName("value");
                    switch (row.Value)
                    {
                        case null: w.WriteNullValue(); break;
                        case bool b: w.WriteBooleanValue(b); break;
                        case int n: w.WriteNumberValue(n); break;
                        case float f: w.WriteNumberValue(float.IsFinite(f) ? Math.Round(f, 3) : 0d); break;
                        case object o: w.WriteStringValue(Convert.ToString(o, CultureInfo.InvariantCulture)); break;
                    }
                    w.WriteString("surface", row.Surface);
                    if (row.FromKey is { } from) w.WriteString("from", Loc.Get(from));
                    w.WriteNumber("atMs", row.AtMs);
                    w.WriteNumber("agoMs", Math.Max(0, now - row.AtMs));
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            });
        }

        static string Camel(string pascal) => pascal.Length == 0 ? pascal : char.ToLowerInvariant(pascal[0]) + pascal[1..];
    }
}
