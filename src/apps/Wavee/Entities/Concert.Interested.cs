// ── Entities/Concert.Interested.cs — the concerts the listener is "interested" in (a LOCAL set) ──────────────────────────────────────
//
// Role: CORE
//
// Z-Concert draws an "interested" ring on the concert page and in the band. THE CONCERT API HAS NO SUCH STATE: the concert query's wire
// (`Spotify.Decode.Concert.cs`) carries a concert's facts, offers, lineup and related shows, and the only user-saved thing in the concert ops
// is the saved PLACE (`saveConcertLocation`). So "interested" is a local set: the concert uris, newest first, in ONE capped settings blob
// (`Platform.Keys.ConcertInterested`), with a version signal the concert page subscribes to. It is cleared on sign-out (an account's mark).
//
// The pure half (`Parse`, `Contains`, `Toggle`) is what the tests pin; the live half is the signal and the settings read.

using FluentGpu.Signals;

namespace Wavee;

/// <summary>The "interested" set of concerts: pure rules over the stored blob, and the live door (UI thread, C1).</summary>
public static class ConcertInterest
{
    /// <summary>The most concerts the set keeps; marking one more drops the oldest mark.</summary>
    public const int Cap = 200;

    static readonly Signal<int> s_version = new(0);

    /// <summary>Bumped on every change; a page that draws the mark subscribes to it.</summary>
    public static IReadSignal<int> Version => s_version;

    // ── the pure half ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The blob's uris, newest first. Blank and over-cap entries are dropped.</summary>
    public static string[] Parse(string? blob)
    {
        if (string.IsNullOrWhiteSpace(blob)) return [];
        var parts = blob.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length <= Cap ? parts : parts[..Cap];
    }

    /// <summary>Is <paramref name="uri"/> in the blob?</summary>
    public static bool Contains(string? blob, string uri)
        => uri.Length > 0 && Array.IndexOf(Parse(blob), uri) >= 0;

    /// <summary>The blob after toggling <paramref name="uri"/>: marked becomes unmarked, unmarked goes to the front (the oldest falls off the cap).</summary>
    public static string Toggle(string? blob, string uri, out bool nowInterested)
    {
        var list = new List<string>(Parse(blob));
        nowInterested = false;
        if (uri.Length == 0) return string.Join(' ', list);
        if (!list.Remove(uri))
        {
            list.Insert(0, uri);
            nowInterested = true;
            if (list.Count > Cap) list.RemoveRange(Cap, list.Count - Cap);
        }
        return string.Join(' ', list);
    }

    // ── the live half ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Is this concert marked? A plain read (a caller that must repaint on a change subscribes to <see cref="Version"/>).</summary>
    public static bool IsInterested(string uri) => Contains(Platform.Settings.Get(Platform.Keys.ConcertInterested), uri);

    /// <summary>Flip the concert's mark and announce it.</summary>
    public static void Toggle(string uri)
    {
        if (uri.Length == 0) return;
        Platform.Settings.Set(Platform.Keys.ConcertInterested, Toggle(Platform.Settings.Get(Platform.Keys.ConcertInterested), uri, out _));
        s_version.Value++;
    }

    /// <summary>Every marked concert's uri, newest first.</summary>
    public static string[] All() => Parse(Platform.Settings.Get(Platform.Keys.ConcertInterested));

    /// <summary>Forget every mark (a sign-out).</summary>
    public static void Clear()
    {
        if (Platform.Settings.Get(Platform.Keys.ConcertInterested).Length == 0) return;
        Platform.Settings.Set(Platform.Keys.ConcertInterested, "");
        s_version.Value++;
    }
}
