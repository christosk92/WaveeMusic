// ── Shell/WindowTiers.cs — the core resolves the window tiers (themes plan 3.2, 3.3; T3b) ─────────────────────────────
//
// Role: CORE. WHO MEASURES: the core. The frame root hands `WindowTiers.Binding` the layout viewport (after app zoom) and
// the content width the shell computes synchronously (`EstimateContentWidth`: the window less the sidebar column and an
// inline rail, the pattern of `Detail.Breakpoints.EstimatePageWidthFromViewport`, generalised), and the binding resolves them
// through the ACTIVE STRUCTURE's `TierPolicy` ladders with their hysteresis, on the engine's `UseTier` (E22).
//
// THE TIMING CONTRACT (plan 3.3), which `UseTier` implements and this file only feeds:
//   · A first mount resolves in its own frame: the sources are same-frame (`Viewport.Size`, the seeded sidebar signals), so
//     the first frame of a launch, a navigation and a theme switch is already in its final tier.
//   · A crossing found AFTER mount is a PENDING value in the host (`AppHost.CommitPendingTiers`), committed on the first
//     frame that is not resized, right before FLIP capture. So a resized frame still publishes the OLD tier (the old tier's
//     layout is laid out at the new size and snapped, exactly as before), and the new tier crosses on the next frame as an
//     ordinary animated update: moved nodes FLIP, swapped subtrees cross-fade.
//
// WHAT IT PUBLISHES: `ThemeHost.Facts.Tiers` (core models) and, through `ThemeFactsContext`, `ctx.Tier()` (templates). The
// fact is republished when a tier, a rung or `Resizing` changes, never once per resized frame.
//
// EDGE CASES. A non-positive or non-finite size (a minimised window reports 0, a headless host before its first publish)
// resolves to NaN and the ladder keeps the previous tier, so restoring from minimised returns to the tier the window left,
// with no flash of Compact. A window below the compact floor is Compact. An exact edge size lands in the upper tier on a
// fresh resolve and holds per the edge's direction after.

using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee;

/// <summary>Resolves the window and content tiers for the active structure.</summary>
public static class WindowTiers
{
    // ── the pure rules (engine-free: the tests drive them with plain numbers) ───────────────────────────────────────────

    /// <summary>The size a ladder resolves: the value itself, or NaN (which a ladder reads as "keep the previous tier") for a
    /// non-positive or non-finite size.</summary>
    public static float UsableSize(float value) => float.IsFinite(value) && value > 0f ? value : float.NaN;

    /// <summary>The content region's width from the window's: the window less the sidebar column and the rail's inline
    /// reservation (its 8-DIP gap and its width, only while it is open AND fits beside the pane and the content floor). NaN
    /// where the window width is unusable, so the content ladder keeps its tier too. Synchronous: every input is known before
    /// the first layout.</summary>
    public static float EstimateContentWidth(float viewportWidth, float sidebarColumnWidth, bool railOpen, float railWidth)
    {
        float vp = UsableSize(viewportWidth);
        if (float.IsNaN(vp)) return float.NaN;
        float pane = float.IsFinite(sidebarColumnWidth) ? MathF.Max(0f, sidebarColumnWidth) : 0f;
        bool fits = Shell.CanFitRail(vp, pane, railWidth);
        float rail = Shell.FrameRules.RailGapWidth(railOpen, fits) + Shell.FrameRules.RailReservedWidth(railOpen, fits, railWidth);
        return MathF.Max(0f, vp - pane - rail);
    }

    /// <summary>The tiers of a window of <paramref name="viewportWidth"/> by <paramref name="viewportHeight"/> whose content
    /// region is <paramref name="contentWidth"/> wide, through <paramref name="policy"/>. With no <paramref name="previous"/>
    /// it is a FRESH resolve (each edge at its <c>At</c>: a launch, a navigation, a first mount); with one, each ladder holds
    /// per its edges' directions. What the view harness uses for a view mounted without a frame, and what the tests compare
    /// the live binding against.</summary>
    public static TierFacts Resolve(TierPolicy policy, float viewportWidth, float viewportHeight, float contentWidth,
        bool resizing = false, TierFacts? previous = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        float w = UsableSize(viewportWidth), h = UsableSize(viewportHeight), c = UsableSize(contentWidth);
        // Hysteresis only continues a resolve against the SAME policy; a different one (a theme switch) starts fresh.
        TierFacts? prev = previous is { } p && ReferenceEquals(p.Policy, policy) ? p : null;
        int window = policy.Window.Resolve(w, prev is { } a ? (int)a.Window : -1);
        int height = policy.Height.Resolve(h, prev is { } b ? (int)b.Height : -1);
        int content = policy.Content.Resolve(c, prev is { } d ? (int)d.Content : -1);
        ulong rungs = 0UL;
        for (int i = 0; i < policy.Rungs.Length; i++)
        {
            NamedRung rung = policy.Rungs[i];
            float axisValue = rung.Axis switch { RungAxis.Content => c, RungAxis.Height => h, _ => w };
            int was = prev is { } e ? (int)((e.Rungs >> i) & 1UL) : -1;
            if (RungLadder(rung).Resolve(axisValue, was) != 0) rungs |= 1UL << i;
        }
        return new TierFacts((WidthTier)window, (HeightTier)height, (WidthTier)content,
            float.IsNaN(c) ? 0f : c, float.IsNaN(w) ? 0f : w, float.IsNaN(h) ? 0f : h, resizing, rungs, policy);
    }

    /// <summary>The two-tier ladder of one rung: 0 below its edge, 1 at or above it.</summary>
    private static TierLadder RungLadder(NamedRung rung) => TierLadder.Of(rung.Edge);

    // ── the live binding ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A binding on the ACTIVE structure's tier policy (the root's first render). Standard everywhere where no theme is
    /// installed.</summary>
    public static Binding ForActiveStructure(IReadSignal<Size2> viewport) => new(ThemeHost.Facts.Peek().Policies.Tiers, viewport);

    /// <summary>The frame root's tier hooks: one per root, created with the root's first render (a theme switch mounts a new
    /// root, so a new binding on the new structure's ladders). Call <see cref="Begin"/> and <see cref="Complete"/> from the
    /// root's <c>Render</c>, every render, in that order, with the sidebar's first values seeded in between: the window tier
    /// is what seeds the sidebar's band, and the first publish (in <see cref="Complete"/>) then sees the seeded sidebar.</summary>
    public sealed class Binding
    {
        private readonly TierPolicy _policy;
        private readonly IReadSignal<Size2> _viewport;
        private readonly Func<float> _windowWidth;
        private readonly Func<float> _windowHeight;
        private readonly Func<float> _contentWidth;
        private readonly TierLadder[] _rungLadders;
        private readonly Func<float>[] _rungSources;
        private readonly Action _publishEffect;

        private IReadSignal<int>? _window;
        private IReadSignal<int>? _height;
        private IReadSignal<int>? _content;
        private IReadSignal<int>[] _rungs = [];
        private IReadSignal<bool>? _modal;
        private int _logicalWindow = -1;   // the window tier the viewport resolves to now (pending crossing included), for the content estimate

        /// <summary>Binds <paramref name="policy"/> to <paramref name="viewport"/> (the layout viewport in DIP, after app
        /// zoom). Throws for a policy that cannot be bound (a ladder with more edges than its enum has bands).</summary>
        public Binding(TierPolicy policy, IReadSignal<Size2> viewport)
        {
            ArgumentNullException.ThrowIfNull(policy);
            ArgumentNullException.ThrowIfNull(viewport);
            var problems = policy.Validate();
            if (problems.Count > 0)
                throw new ArgumentException("The tier policy cannot be bound: " + string.Join("; ", problems), nameof(policy));
            _policy = policy;
            _viewport = viewport;
            _windowWidth = () => UsableSize(_viewport.Value.Width);
            _windowHeight = () => UsableSize(_viewport.Value.Height);
            _contentWidth = () => ContentWidth(track: true);
            _publishEffect = ObserveAndPublish;
            _rungLadders = new TierLadder[policy.Rungs.Length];
            _rungSources = new Func<float>[policy.Rungs.Length];
            for (int i = 0; i < _rungLadders.Length; i++)
            {
                NamedRung rung = policy.Rungs[i];
                _rungLadders[i] = RungLadder(rung);
                _rungSources[i] = rung.Axis switch { RungAxis.Content => _contentWidth, RungAxis.Height => _windowHeight, _ => _windowWidth };
            }
        }

        /// <summary>The policy this binding resolves against.</summary>
        public TierPolicy Policy => _policy;

        /// <summary>The window width tier as a <see cref="WidthTier"/> index (changes only when the host commits a crossing).
        /// Valid after <see cref="Begin"/>.</summary>
        public IReadSignal<int> WindowTier => _window ?? throw new InvalidOperationException("WindowTiers.Binding.Begin has not run.");

        /// <summary>The window width tier (tracked read). Valid after <see cref="Begin"/>.</summary>
        public WidthTier Window => (WidthTier)WindowTier.Value;

        /// <summary>The window width tier without subscribing. Valid after <see cref="Begin"/>.</summary>
        public WidthTier WindowNow => (WidthTier)WindowTier.Peek();

        /// <summary>Step one, from the root's <c>Render</c>: the window width and height tiers, resolved in this frame.</summary>
        public void Begin(RenderContext ctx)
        {
            ArgumentNullException.ThrowIfNull(ctx);
            _window = ctx.UseTier(_policy.Window, TierSource.Estimate(_windowWidth));
            _height = ctx.UseTier(_policy.Height, TierSource.Estimate(_windowHeight));
        }

        /// <summary>Step two, after the sidebar's first values are seeded: the content tier, the rungs, the resizing fact and
        /// the effect that republishes <c>ThemeHost.Facts.Tiers</c>. The first publish happens here, so the first layout of
        /// every view already reads the final tiers.</summary>
        public void Complete(RenderContext ctx)
        {
            ArgumentNullException.ThrowIfNull(ctx);
            _content = ctx.UseTier(_policy.Content, TierSource.Estimate(_contentWidth));
            if (_rungs.Length != _rungLadders.Length) _rungs = new IReadSignal<int>[_rungLadders.Length];
            for (int i = 0; i < _rungLadders.Length; i++)
                _rungs[i] = ctx.UseTier(_rungLadders[i], TierSource.Estimate(_rungSources[i]));   // one call site in a loop: the hook keys it by ordinal
            _modal = ctx.UseContextSignal(Viewport.InModalLoop);
            ctx.UseSignalEffect(_publishEffect);   // eager: runs now, and again at every committed crossing
        }

        /// <summary>The effect body: subscribes to the committed tiers and the modal edge, then republishes. It reads no size, so
        /// a resized frame that crosses nothing never wakes it.</summary>
        private void ObserveAndPublish()
        {
            _ = _window!.Value;
            _ = _height!.Value;
            _ = _content!.Value;
            for (int i = 0; i < _rungs.Length; i++) _ = _rungs[i].Value;
            _ = _modal!.Value;
            Publish();
        }

        private void Publish()
        {
            Size2 size = _viewport.Peek();
            float content = ContentWidth(track: false);
            ulong mask = 0UL;
            for (int i = 0; i < _rungs.Length; i++)
                if (_rungs[i].Peek() != 0) mask |= 1UL << i;
            var facts = new TierFacts(
                (WidthTier)_window!.Peek(), (HeightTier)_height!.Peek(), (WidthTier)_content!.Peek(),
                float.IsNaN(content) ? 0f : content, size.Width, size.Height, _modal!.Peek(), mask, _policy);
            ThemeHost.PublishTiers(in facts);
        }

        /// <summary>The content width from the live signals (<paramref name="track"/>: subscribe the running computation, and
        /// advance the logical window tier).
        /// <para>The sidebar column is the one the LOGICAL window tier presents (the tier the viewport resolves to now, the
        /// pending crossing included), not the column on screen: on a resized frame the old tier is still laid out at the new
        /// size, and sizing the content from that transient would make the content tier cross a frame after the window tier
        /// and remember a squeeze that never settles. Computed from the same inputs the frame's presentation effect uses
        /// (<c>SidebarPaneModeRules</c>), so once the crossing commits it equals what is on screen.</para></summary>
        private float ContentWidth(bool track)
        {
            Size2 size = track ? _viewport.Value : _viewport.Peek();
            float viewportWidth = UsableSize(size.Width);
            int logical = _policy.Window.Resolve(viewportWidth, _logicalWindow);
            if (track) _logicalWindow = logical;   // only the tracked pass owns the state; a publish just reads it

            var band = SidebarPaneModeRules.BandOf((WidthTier)logical);
            bool collapsed = track ? Sidebar.UserCollapsed.Value : Sidebar.UserCollapsed.Peek();
            bool editing = track ? Sidebar.Editing.Value : Sidebar.Editing.Peek();
            bool peek = track ? Sidebar.DragPeek.Value : Sidebar.DragPeek.Peek();
            float expanded = track ? Sidebar.Width.Value : Sidebar.Width.Peek();
            var mode = SidebarPaneModeRules.Resolve(band, collapsed, editing);
            float presented = SidebarPaneModeRules.PresentedWidth(mode, expanded, viewportWidth);
            bool railOpen = track ? Shell.Ui.RailOpen.Value : Shell.Ui.RailOpen.Peek();
            float railWidth = track ? Shell.Ui.RailWidth.Value : Shell.Ui.RailWidth.Peek();
            return EstimateContentWidth(viewportWidth, Shell.FrameRules.SidebarPaneWidth(peek, expanded, presented), railOpen, railWidth);
        }
    }
}
