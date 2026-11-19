// ── Screens/SettingsWrites.cs — every dial write, from wherever it came: Recently changed and retire-on-use ──────────────────────
//
// Role: CORE (settings rework phase 3, handoff/settings-2026-11-18/DESIGN.md 3.3, 6.2). The ONE place a dial write is seen. Every value
// writer of the registry is wrapped when it is built (`SettingsWriter.OfToggle` / `OfChoice` / `OfSlider`), so a write from a Settings
// view, a panel's dials, a hint's inline action or the inspector lands here under its dial's id, and here only:
//   · Recently changed records (dial id, source surface, time) in the hint blob's ring (`HintState`, the last five dials, persisted);
//   · the hints about that dial retire (`Hints.OnDialWritten`: "retired when the dial is written from anywhere").
// The SOURCE is ambient for the span of a write: `using var _ = SettingsWrites.From(surface)` around the call (what
// `SettingsDial.WriteToggle(on, surface)` and friends do); a write with no source is the Settings page's. `Quiet()` silences the record
// for writes that are not the user's (the inspector putting back the values a mount changed).
//
// COST. A record is a ring slot update and, at most once per dial per `HintRules.RecentCoalesceMs`, one blob write: a slider dragged
// through its writer per pointer move moves the ring's head in place and allocates nothing.

namespace Wavee;

/// <summary>The record of every dial write (see the file header).</summary>
public static class SettingsWrites
{
    static string? s_from;
    static int s_quiet;

    /// <summary>The source of the writes inside the scope (UI thread). Nested scopes restore the outer source.</summary>
    public static Scope From(string surface)
    {
        string? outer = s_from;
        s_from = surface;
        return new Scope(outer, quiet: false);
    }

    /// <summary>The writes inside the scope are not the user's: nothing is recorded and no hint retires.</summary>
    public static Scope Quiet()
    {
        s_quiet++;
        return new Scope(s_from, quiet: true);
    }

    /// <summary>The source the current write records ("settings" outside any scope).</summary>
    public static string Source => s_from ?? SettingsSurfaces.Settings;

    /// <summary>A value writer of <paramref name="writer"/> ran; passes its answer (whether the page re-reads) through.</summary>
    internal static bool Recorded(SettingsWriter writer, bool reread)
    {
        if (s_quiet == 0 && writer.DialId is { } dial)
        {
            HintState.RecordWrite(dial, Source, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            Hints.OnDialWritten(dial);
        }
        return reread;
    }

    /// <summary>A source scope; disposing it puts the outer source back.</summary>
    public readonly ref struct Scope
    {
        readonly string? _outer;
        readonly bool _quiet;

        internal Scope(string? outer, bool quiet)
        {
            _outer = outer;
            _quiet = quiet;
        }

        public void Dispose()
        {
            if (_quiet) s_quiet--;
            else s_from = _outer;
        }
    }
}
