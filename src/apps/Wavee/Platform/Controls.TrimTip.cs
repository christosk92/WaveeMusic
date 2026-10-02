// ── Platform/Controls.TrimTip.cs ───────────────────────────────────────────────────────────────────────────────────
// TrimTip — a tooltip that exists ONLY while its text is trimmed in the slot it was given
//
// Role: UI
// Owner: L
// Wave: 0c (the surface plan's shared pieces; consumed by the Artists reader's band and album heads, and Album.UI.cs's
//       PaneCommands link)
// Budget: 180 lines
// Spec: library-reader-narrow-heads-implementation.md §3.5 (the control) and §6 risk 3 (the face);
//       shared-media-surface-implementation.md wave 0c; TextFit is `Surface.Rules.cs` (wave 0a)
//
// ── WHY IT MEASURES ──────────────────────────────────────────────────────────────────────────────────────────────────
//
// The engine reports no "this run was trimmed" fact: `TextMetrics` has no flag and `TextEl` no callback. The decision is
// therefore made from the two things that DO exist — the slot's arranged width (`UseMeasuredWidth`) and one seam
// measurement of the text under the style the caller already drew it with. Attaching a tooltip to text that fits is
// noise (WinUI's own guidance), so the wrapper is the BOUND `ToolTip.Wrap` overload: a null text mounts NO trigger
// wiring, and the same ToolTip component lights up with no remount the moment the text starts to be trimmed.
//
// ── WHERE TO PUT IT ──────────────────────────────────────────────────────────────────────────────────────────────────
//
// In a COLUMN, as a child the flex algorithm sizes (a cross-stretched slot): the ToolTip wrapper is `Shrink 0` and can
// never be a shrinking ROW child (the fluentgpu skill's rule 11). The width it measures is the SLOT's — the host's
// rendered root — so a link wrapped around the text with its own horizontal padding leaves the text up to that padding
// less room than the slot (the album head's link is 4 DIP narrower on the text side). `UseMeasuredWidth` rounds to
// 4 DIP, so a name within ~2 DIP of the edge may be decided either way; neither is a layout error.
//
// ── ZERO ALLOCATION AFTER MOUNT ──────────────────────────────────────────────────────────────────────────────────────
//
// One seam query per (text, style, slot width), cached in the host; a re-render at an unchanged width reads the cache.
// Nothing runs per frame.

using System.Runtime.CompilerServices;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using FluentGpu.Text;

namespace Wavee;

public static partial class Controls
{
    /// <summary>Wrap <paramref name="target"/> (which contains the one <see cref="TextEl"/> that <paramref name="style"/>
    /// describes, drawing <paramref name="text"/>) in a tooltip that exists ONLY while that text is trimmed in its slot.
    /// Put it where the flex algorithm sizes the slot — a COLUMN child, never a shrinking ROW child (the ToolTip wrapper is
    /// `Shrink 0`). <paramref name="style"/> is read, not drawn: pass the SAME element the target contains so the
    /// measurement mirrors the paint (size, weight, tracking, line height, wrap, line cap, auto-fit floor, face).</summary>
    public static Element TrimTip(Element target, string text, TextEl style)
        => Embed.Comp(new TrimTipProps(target, text, style.Size, style.ResolvedWeight, style.CharSpacing, style.LineHeight,
                                       style.Wrap, style.MaxLines, style.MinSize, style.FontFamily),
                      static () => new TrimTipHost());

    /// <summary>The ONE "title inside <see cref="TrimTip"/>" wrapper — every surface's trimmable title (the media surface's
    /// cards and rows, a text-first (B) panel's record title, a podcast door) goes through it, so the slot it measures and
    /// the skeleton it leaves cannot drift. The run sits in a column box that fills a stretched wrapper, so the slot the
    /// tooltip measures is the card's text column (never a shrink-wrapped run: a content-sized wrapper reads a fitting
    /// title, rounded to the 4-DIP grid, as overflowing); the skeleton deriver — which cannot see into the tooltip's
    /// component boundary — is handed the same column, so a derived shimmer keeps the title's bar. Put it where the flex
    /// algorithm sizes it: a COLUMN child, never a shrinking row child.
    /// <para><paramref name="run"/> is what is DRAWN when it is not <paramref name="style"/> itself (the search-highlight
    /// arm); the tooltip measures with <paramref name="style"/> either way. <paramref name="centred"/> centres the run in
    /// the column (a circular card's labels) instead of stretching it to the slot.</para></summary>
    internal static Element TrimmedTitle(TextEl style, string text, Element? run = null, bool centred = false)
    {
        var target = new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f,
            AlignItems = centred ? FlexAlign.Center : FlexAlign.Stretch,
            Children = [run ?? style],
        };
        Element tip = TrimTip(target, text, style);
        if (tip is ComponentEl c) tip = c with { SkeletonProxy = () => new BoxEl { Children = [target] } };
        return new BoxEl { Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, Children = [tip] };
    }

    /// <summary>The re-pushed props: the live target plus everything the seam needs to lay the text out. Equality is the
    /// target's REFERENCE (a new element each parent render, so a re-render re-wraps it — exactly ToolTip's own rule)
    /// plus the measured fields; <see cref="SameMeasure"/> is the cache key — the same fields without the target.</summary>
    sealed record TrimTipProps(Element Target, string Text, float Size, ushort Weight, float CharSpacing, float LineHeight,
                               TextWrap Wrap, int MaxLines, float MinSize, string? Family)
    {
        public bool Equals(TrimTipProps? o) => o is not null && ReferenceEquals(Target, o.Target) && SameMeasure(o);

        /// <summary>Everything the seam measures. Floats compare with <c>Equals</c>: <c>LineHeight</c> and <c>MinSize</c>
        /// are NaN when unset, and NaN must equal itself or the cache never hits.</summary>
        public bool SameMeasure(TrimTipProps? o) => o is not null && Text == o.Text && Size.Equals(o.Size) && Weight == o.Weight
            && CharSpacing.Equals(o.CharSpacing) && LineHeight.Equals(o.LineHeight) && Wrap == o.Wrap && MaxLines == o.MaxLines
            && MinSize.Equals(o.MinSize) && Family == o.Family;

        public override int GetHashCode() => HashCode.Combine(RuntimeHelpers.GetHashCode(Target), Text, Size, MaxLines);
    }

    /// <summary>Owns its props CHANNEL (<see cref="IPropsHost"/>, the app's host pattern): a re-push lands in a field and
    /// one equality-gated signal, so the render reads the CURRENT target without a frozen factory closure.</summary>
    sealed class TrimTipHost : Component, IPropsHost
    {
        TrimTipProps? _p;
        readonly Signal<TrimTipProps?> _props = new(null);
        TrimTipProps? _measuredFor; float _measuredW = float.NaN; bool _trimmed;

        public void ApplyProps(object props) { _p = (TrimTipProps)props; _props.Value = _p; }

        public override Element Render()
        {
            _ = _props.Value;
            float w = UseMeasuredWidth(4f).Value;                       // written during layout → this re-renders next frame
            var p = _p!;
            if (w > 0f && (w != _measuredW || !p.SameMeasure(_measuredFor)))
            {
                _measuredFor = p; _measuredW = w;
                _trimmed = Measure(p, w, Context.Scene?.Strings);
            }
            // Not measured yet (w == 0) or fits: a null tip mounts no wiring and leaves the target alone.
            string? tip = w > 0f && _trimmed ? p.Text : null;
            return ToolTip.Wrap(p.Target, (Prop<string?>)tip);
        }

        /// <summary>One seam query. A single-line run (NoWrap, or capped at one line) compares its natural width with the
        /// slot; a run capped at N &gt; 1 lines is laid out at the slot width and its line fragments are counted against
        /// the cap. An auto-fit run (<c>MinSize</c>, which the engine only applies to a wrapping, line-capped run) is
        /// measured at its FLOOR face — the smallest it ever shrinks to — because the engine ellipsizes only after even
        /// the floor fails to fit. An uncapped wrapping run grows instead of being cut, so it is never trimmed. The face
        /// is the TextEl's own (<c>Family</c> interned through the engine's table — the one the font system resolves
        /// through — so the display face measures as drawn); with no table or no seam the default face is measured, and
        /// with no seam at all (headless) the answer is "trimmed": a redundant tooltip beats a missing one.</summary>
        static bool Measure(TrimTipProps p, float w, StringTable? strings)
        {
            if (p.Text.Length == 0) return false;
            if (TextSeam.Default is not { } fonts) return true;
            bool wraps = p.Wrap != TextWrap.NoWrap;
            if (wraps && p.MaxLines <= 0) return false;
            float size = wraps && p.MinSize > 0f && p.MinSize < p.Size ? p.MinSize : p.Size;   // NaN fails both compares
            StringId family = p.Family is { } name && strings is not null ? strings.Intern(name) : default;
            Span<RectF> rects = stackalloc RectF[8];
            if (!wraps || p.MaxLines == 1)
            {
                var one = new TextStyle(family, size, p.Weight, TextWrap.NoWrap, TextTrim.None, 0, p.CharSpacing, p.LineHeight);
                int n = fonts.GetRangeRects(p.Text, in one, float.PositiveInfinity, 0, p.Text.Length, rects);
                float natural = 0f;
                for (int i = 0; i < n; i++) natural += rects[i].W;
                return TextFit.Overflows(natural, w);
            }
            var many = new TextStyle(family, size, p.Weight, p.Wrap, TextTrim.None, 0, p.CharSpacing, p.LineHeight);
            int lines = fonts.GetRangeRects(p.Text, in many, w, 0, p.Text.Length, rects);
            return TextFit.Trimmed(lines == rects.Length ? int.MaxValue : lines, p.MaxLines);   // a full buffer is "many"
        }
    }
}
