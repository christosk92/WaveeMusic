// ── Shell/OverlayService.cs — the app's transient popups, closable as a set ─────────────────────────────────────────
//
// Role: CORE (themes plan §5.1, PR C8). The overlay HOST is the engine's: `IOverlayService.CloseAll` closes everything,
// modal dialogs included. A theme transaction (§4.2 step 2) must close the TRANSIENT popups only (a menu, the notification
// panel, the search flyout, a teaching tip), and never the one hosting the control that started it. The shell sites that
// open a transient hand its handle to `Track`; `CloseTransients(except)` is the verb. Nothing calls it yet (the theme
// transaction lands in P3), so opening and closing behave exactly as before.
//
// UI thread only, like every overlay handle. The set is ROOT-LIFETIME (RootScope.cs).

using FluentGpu.Controls;

namespace Wavee;

/// <summary>The set of open transient popups.</summary>
public sealed class OverlayService
{
    readonly List<OverlayHandle> _transients = new();

    static readonly RootState<OverlayService> s_shared = new("Shell.TransientOverlays", static () => new OverlayService());

    /// <summary>The set the shell's popup sites register with. ROOT-LIFETIME state (<see cref="RootScope"/>): the handles belong
    /// to the mounted root's overlay host, so a remounted root starts with an empty set and never closes (or holds) a handle of
    /// the frozen tree.</summary>
    public static OverlayService Shared => s_shared.Value;

    /// <summary>Register a transient popup's handle (see <see cref="Add"/>) on the shared set.</summary>
    public static void Track(OverlayHandle handle) => Shared.Add(handle);

    /// <summary>The tracked popups still open.</summary>
    public int OpenCount
    {
        get
        {
            Prune();
            return _transients.Count;
        }
    }

    /// <summary>Remember a transient popup so <see cref="CloseTransients"/> can close it. Closed handles fall out of the set
    /// on the next call, so a popup that closed by itself costs nothing.</summary>
    public void Add(OverlayHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        Prune();
        if (!_transients.Contains(handle)) _transients.Add(handle);
    }

    /// <summary>Close every tracked transient except <paramref name="except"/> (the popup hosting the control that asked).</summary>
    public void CloseTransients(OverlayHandle? except = null)
    {
        var open = _transients.ToArray();
        for (int i = 0; i < open.Length; i++)
        {
            var handle = open[i];
            if (ReferenceEquals(handle, except)) continue;
            if (handle.IsOpen) handle.Close();
        }
        Prune();
    }

    void Prune() => _transients.RemoveAll(static h => !h.IsOpen);
}
