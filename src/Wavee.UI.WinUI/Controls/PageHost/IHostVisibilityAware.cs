namespace Wavee.UI.WinUI.Controls.PageHost;

/// <summary>
/// Implemented by controls that drive a continuous GPU render loop (e.g. a Win2D
/// <c>CanvasAnimatedControl</c>) which must STOP while the control's host page is
/// not the visible active page.
///
/// <para>
/// Under the Files-app tab model every visited page stays fully resident (visual
/// tree + GPU surfaces + bindings) until its tab closes, so a collapsed cached
/// page keeps its <c>Loaded</c> state — and a Win2D animated control keeps spinning
/// its swap-chain render loop even though nothing is on screen. With no page cap,
/// those accumulate and a single tab can hold many of them; re-showing such a tab
/// resumes them all at once, which can exhaust the GPU (device-removed / hang).
/// </para>
///
/// <para>
/// <see cref="PageHost"/> calls <see cref="OnHostVisibilityChanged"/> when it makes
/// a cached page visible / collapses it. Implementers also re-check their own
/// ancestor visibility on <c>Loaded</c> (covers the case where a whole tab's
/// <c>ContentHost</c> re-attaches and every cached page loads at once). This only
/// pauses/resumes the animation — surfaces and bindings stay resident, so switching
/// back is still instant.
/// </para>
/// </summary>
public interface IHostVisibilityAware
{
    void OnHostVisibilityChanged(bool isVisible);
}
