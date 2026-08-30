namespace Wavee;

/// <summary>
/// THE MID-DRAG PARKING BAY for a sidebar plan stage.
///
/// <para>WHY. A rootlist organisation drag aims at the tree's ROWS. A re-projection that arrives mid-gesture (a dealer
/// push from another device, our own optimistic ack, a background revalidate) re-keys those rows under the pointer, and
/// the drop then lands somewhere the user did not aim — the sidebar's copy of the defect
/// <c>PlaylistReorderDefer</c> already fixed for the detail page's track list. So the newest stage is HELD here instead
/// of published, and applied on SESSION END (drop, cancel and Escape alike).</para>
///
/// <para>LAST WRITER WINS: a burst of publishes during one gesture converges to the newest stage, which is the only one
/// worth applying. A stage that arrives AFTER the session ended is never held — <see cref="TryHold"/> says so, and the
/// caller publishes it normally.</para>
///
/// <para>Generic and engine-free (System-free, even) so <c>SidebarDropFreezeTests</c> can drive the real state machine:
/// the pane's own <c>PlanStage</c> is a private nested record, and the rules here do not depend on what a stage IS.</para>
/// </summary>
sealed class SidebarStageHold<TStage> where TStage : class
{
    TStage? _held;

    /// <summary>Is a stage parked right now? Diagnostics and tests only — the pane never branches on it.</summary>
    public bool HasHeld => _held is not null;

    /// <summary>Hold <paramref name="stage"/> instead of publishing it, when <paramref name="sessionLive"/>.
    /// <para>Returns TRUE when the stage was parked — the caller must NOT publish. Returns FALSE when no session is
    /// live, which is the race that matters: a stage produced after the drag ended publishes on the spot rather than
    /// waiting for a flush that will never come.</para></summary>
    public bool TryHold(bool sessionLive, TStage stage)
    {
        if (!sessionLive) return false;
        _held = stage;
        return true;
    }

    /// <summary>Hand back the parked stage EXACTLY ONCE. The bay is emptied before the caller publishes, so a publish
    /// that re-enters this type (the pane discards on every publish) cannot flush the same stage twice.</summary>
    public bool TryFlush(out TStage? stage)
    {
        stage = _held;
        _held = null;
        return stage is not null;
    }

    /// <summary>Drop the parked stage without publishing it — the pane is going away (unmount), or a stage got published
    /// through another path and the parked one is now stale.</summary>
    public void Discard() => _held = null;
}
