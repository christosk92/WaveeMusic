namespace Wavee.UI.WinUI.Controls.PageHost;

/// <summary>
/// Implemented by cached pages that can shed their live, UI-thread-coupled work
/// while they sit resident-but-off-screen <em>beyond</em> <see cref="PageHost"/>'s
/// hot window, then restore it when navigated back to.
///
/// <para>
/// Under the tab model every visited page stays resident (visual tree + compiled
/// x:Bind bindings) until its tab closes — a collapsed page never fires
/// <c>Unloaded</c>, so its bindings keep re-evaluating on every source
/// <c>PropertyChanged</c> and any per-page subscriptions keep firing on the UI
/// thread. Left unchecked, the set of live page trees grows with every navigation
/// and the UI thread does more work per shared-source tick (playback position,
/// library, theme) the longer the app runs — which is what makes navigation feel
/// progressively laggier over a session.
/// </para>
///
/// <para>
/// <see cref="PageHost"/> keeps the active page and the
/// <see cref="PageHost.HotCollapsedPageBudget"/> most-recently-used collapsed pages
/// fully live (so back/forward to them is instant) and calls
/// <see cref="Hibernate"/> on every page that falls outside that window.
/// <see cref="Rehydrate"/> is called just before the page goes live again. A page
/// only ever leaves the idle tier by being navigated to, so the existing
/// <c>OnEntered</c> reload path does the heavy lifting; <see cref="Rehydrate"/> only
/// needs to re-arm whatever <see cref="Hibernate"/> tore down. Both calls are
/// idempotent.
/// </para>
/// </summary>
public interface IHibernatingPage
{
    /// <summary>
    /// Disconnect the page (and its ViewModel) from live / high-frequency sources
    /// and pause any continuous work, so a resident-but-off-screen page imposes no
    /// per-tick UI-thread cost. Lightweight identity (id, header text, cover) may be
    /// kept so a quick re-show is possible. Must be safe to call repeatedly.
    /// </summary>
    void Hibernate();

    /// <summary>
    /// Re-arm the page before it becomes live again. Called just before
    /// <see cref="IPageHostAware.OnEntered"/> on a previously-hibernated page. Must
    /// be safe to call when the page was never hibernated.
    /// </summary>
    void Rehydrate();
}
