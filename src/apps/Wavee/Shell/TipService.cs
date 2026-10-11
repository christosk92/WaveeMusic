// ── Shell/TipService.cs — the teaching-tip presenter and its one slot ───────────────────────────────────────────────
//
// Role: CORE (themes plan §5.1, PR C8; extraction X22). The A10 presenter used to sit at the bottom of
// `Shell.Overlays.UI.cs` with its slot (`s_tipHandle`) as a static of the Shell. The rules are `TipsCore`, the persisted set
// and the per-session arm are `Shell.Tips`; this is the imperative half that draws one tip on the overlay host.
//
// ONE tip at a time, once per launch, never again once acknowledged; it never scrims and never steals focus; a double post
// lets the page paint first; the anchor must still be live AND not parked when it opens; an inert handle (a host-less mount)
// frees the slot at once. The X IS "don't show again".

using FluentGpu.Controls;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Scene;

namespace Wavee;

/// <summary>The teaching-tip presenter.</summary>
public static class TipService
{
    // ROOT-LIFETIME (RootScope.cs): the slot holds the open tip's handle, which belongs to the mounted root's overlay host.
    // `Shell.Tips.Active` ("one tip at a time") is process state, so a retiring root takes its tip down without acknowledging.
    static readonly RootState<OverlayHandle?> s_tipHandleState = new("Shell.TipHandle");
    static OverlayHandle? s_tipHandle { get => s_tipHandleState.Value; set => s_tipHandleState.Value = value; }

    /// <summary>Arm tip <paramref name="tipId"/> against <paramref name="anchor"/>, scheduled for after the next PAINTED
    /// frame. Returns true when the gate passed. Safe to call every render: the per-launch latch makes repeats no-ops.</summary>
    public static bool TryShow(IOverlayService? overlay, Action<Action>? post, string tipId, Func<NodeHandle> anchor,
        Func<SceneStore?> scene, string titleKey, string bodyKey,
        TeachingTip.PlacementMode placement = TeachingTip.PlacementMode.Bottom)
    {
        // A tip whose acknowledgement cannot be persisted or that has nowhere to draw is never shown (TipsCore: canPresent).
        if (overlay is not { } host || post is not { } schedule) return false;
        if (!Shell.Tips.Arm(tipId, canPresent: true)) return false;

        schedule(() => schedule(() =>
        {
            if (!string.Equals(Shell.Tips.Active.Peek(), tipId, StringComparison.Ordinal) || s_tipHandle is not null) return;
            var node = anchor();
            var sc = scene();
            if (sc is null || node.IsNull || !sc.IsLive(node) || (sc.Flags(node) & NodeFlags.Parked) != 0)
            {
                Shell.Tips.Dismiss(tipId);
                return;
            }
            var handle = TeachingTip.Show(host, anchor, tip =>
            {
                tip.Title = Loc.Get(titleKey);
                tip.Subtitle = Loc.Get(bodyKey);
                tip.PreferredPlacement = placement;
                tip.IsLightDismissEnabled = false;
                tip.CloseButtonClick = () => Shell.Tips.Acknowledge(tipId);
            });
            if (!handle.IsOpen) { Shell.Tips.Dismiss(tipId); return; }
            s_tipHandle = handle;
            RootScope.Current.OnDispose(() => Shell.Tips.Dismiss(tipId));
            OverlayService.Track(handle);
            // Every close path — the X, Close, the host's orphaned-anchor prune — releases the one slot.
            handle.ClosedAction = () =>
            {
                if (ReferenceEquals(s_tipHandle, handle)) s_tipHandle = null;
                Shell.Tips.Dismiss(tipId);
            };
        }));
        return true;
    }

    /// <summary>Take the tip down WITHOUT acknowledging it (navigation away, the anchor evicted).</summary>
    public static void Close(string tipId)
    {
        if (!string.Equals(Shell.Tips.Active.Peek(), tipId, StringComparison.Ordinal)) return;
        var handle = s_tipHandle;
        s_tipHandle = null;
        Shell.Tips.Dismiss(tipId);
        handle?.Close();
    }

    /// <summary>The user used the taught affordance: acknowledge (durable) and take the tip down.</summary>
    public static void Acknowledge(string tipId)
    {
        if (!string.Equals(Shell.Tips.Active.Peek(), tipId, StringComparison.Ordinal)) return;
        var handle = s_tipHandle;
        s_tipHandle = null;
        Shell.Tips.Acknowledge(tipId);
        handle?.Close();
    }
}
