using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace Wavee;

/// <summary>One page's ambient, content-derived accent — an ink/fill pair a page resolves from whatever sits at the
/// top of its viewport (a cover, a hero image) and shares with the small set of shared components that colour
/// themselves off "whatever this page is currently about". <see cref="Key"/> identifies the SOURCE the pair was
/// resolved from (e.g. an item id) so a consumer can key its own transition/memo on provenance, not merely on the
/// resolved colour — two different sources that happen to land on the same colour are still a different accent.</summary>
public readonly record struct PageAccent(ColorF Ink, ColorF Fill, string Key);

/// <summary>
/// The shared, page-scoped ACCENT channel. Modeled on <see cref="ShellMaterial"/>: a page that derives a live accent
/// from its own content (Recents' viewport-following cover accent is the first consumer) provides one signal at its
/// root; any shared component that wants to tint itself with "this page's accent" reads it with
/// <c>UseContext(WaveeAccentCtx.Slot)</c> instead of knowing which page it is embedded in.
/// <para>Default is <see langword="null"/> — a page that publishes no accent leaves every consumer rendering its
/// ordinary token colour. <c>RecentsPage</c> is the one provider today (it wraps its whole page in this slot). Per
/// <c>docs/design/subsystems/component-props-contract.md</c>, a consumer reads the
/// context live inside <c>Render</c> (never captures it at construction), so a later provider lighting up repaints
/// every mounted consumer for free.</para>
/// </summary>
public static class WaveeAccentCtx
{
    /// <summary>Context slot — a page provides its resolved accent signal here; consumers read it with
    /// <c>UseContext(WaveeAccentCtx.Slot)</c>. Null (the default, and what every page other than Recents leaves in
    /// place) means no page publishes an accent, in which case a consumer falls back to its ordinary token colour.</summary>
    public static readonly Context<IReadSignal<PageAccent>?> Slot = new(null);
}
