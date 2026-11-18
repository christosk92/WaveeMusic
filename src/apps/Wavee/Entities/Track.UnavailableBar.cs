// ── Entities/Track.UnavailableBar.cs — the lists whose unavailable-songs bar was closed ─────────────────────────────────────
//
// The bar over a list with songs that can't be played (boards C-Unavailable) offers "Hide them"; its ✕ closes it for good
// on THAT list, remembered across sessions by the list's context uri (`Platform.Keys.UnavailableBarDismissed`). Another
// list still shows its own bar: there it is news. The newest closes are kept when the set outgrows its cap.

using FluentGpu.Signals;

namespace Wavee;

/// <summary>The lists whose unavailable-songs bar the user closed. UI THREAD.</summary>
public static class UnavailableBarDismissals
{
    const int Cap = 512;

    /// <summary>Moves on every close, so a mounted bar that read <see cref="Has"/> hears it.</summary>
    public static readonly Signal<int> Epoch = new(0);

    static List<string>? s_order;
    static HashSet<string>? s_set;

    /// <summary>Was the bar closed on the list of <paramref name="context"/> (its context uri)? Subscribes <see cref="Epoch"/>.</summary>
    public static bool Has(string context)
    {
        _ = Epoch.Value;
        return context.Length > 0 && Load().Contains(context);
    }

    /// <summary>Close the bar on the list of <paramref name="context"/>, for good.</summary>
    public static void Add(string context)
    {
        if (context.Length == 0 || !Load().Add(context)) return;
        var order = s_order!;
        order.Add(context);
        while (order.Count > Cap) { s_set!.Remove(order[0]); order.RemoveAt(0); }
        Platform.Settings.Set(Platform.Keys.UnavailableBarDismissed, string.Join('\n', order));
        Epoch.Value = Epoch.Peek() + 1;
    }

    static HashSet<string> Load()
    {
        if (s_set is not null) return s_set;
        s_order = new List<string>(Platform.Settings.Get(Platform.Keys.UnavailableBarDismissed)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        s_set = new HashSet<string>(s_order, StringComparer.Ordinal);
        return s_set;
    }
}
