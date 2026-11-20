// ── Entities/Track.UnavailableBar.cs — the lists whose unavailable-songs bar was closed this session ─────────────────────
//
// The bar over a list with songs that can't be played (boards C-Unavailable) offers "Hide them"; its ✕ closes it on THAT
// list until the app restarts (by the list's context uri, in memory only). Another list still shows its own bar, and every
// launch shows them all again: the bar is information, and closing it is "not now", never "never".

using FluentGpu.Signals;

namespace Wavee;

/// <summary>The lists whose unavailable-songs bar the user closed this session. UI THREAD.</summary>
public static class UnavailableBarDismissals
{
    /// <summary>Moves on every close, so a mounted bar that read <see cref="Has"/> hears it.</summary>
    public static readonly Signal<int> Epoch = new(0);

    static readonly HashSet<string> s_closed = new(StringComparer.Ordinal);

    /// <summary>Was the bar closed on the list of <paramref name="context"/> (its context uri) this session? Subscribes <see cref="Epoch"/>.</summary>
    public static bool Has(string context)
    {
        _ = Epoch.Value;
        return context.Length > 0 && s_closed.Contains(context);
    }

    /// <summary>Close the bar on the list of <paramref name="context"/> until the app restarts.</summary>
    public static void Add(string context)
    {
        if (context.Length == 0 || !s_closed.Add(context)) return;
        Epoch.Value = Epoch.Peek() + 1;
    }
}
