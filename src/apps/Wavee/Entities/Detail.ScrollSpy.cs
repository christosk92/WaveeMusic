namespace Wavee;

// Wave E (podcast-ui-repair plan §5.1): the episode page's section switcher stops being a filter-word tab rail
// (Controls.Words.Rail) and becomes a Detail.Pivot scroll-spy over one stacked scroll, the same shape Artist.Page.cs
// (BandBar / ResolveSpy) and Detail.UI.Hero.cs use. Artist's own spy answer comes from Detail.BandLayout.ActiveSection,
// which folds in the sticky compact-band collapse (a bandBottom offset, an "at scroll end" snap). The episode reader
// has no such collapsing band — its pivot sits in a plain header row above a single ScrollView — so this is a smaller,
// ENGINE-FREE twin: given each section's content-relative top offset, the live scroll offset and the viewport height,
// which section is "current". Pure — no FluentGpu type crosses this boundary, so it is unit-tested directly
// (ScrollSpySectionTests.cs) without a live scene.
public static partial class Detail
{
    public static class ScrollSpy
    {
        /// <summary>A section becomes current once its top has scrolled to this fraction down the viewport — the same
        /// "upper quarter" rung <see cref="BandLayout.SpyViewportFraction"/> uses, so a page that later grows a sticky
        /// band can adopt <see cref="BandLayout.ActiveSection"/> without the felt threshold moving.</summary>
        public const float ViewportFraction = BandLayout.SpyViewportFraction;

        /// <summary>Which section is "here": the LAST one whose content-relative top offset has crossed the spy line
        /// (<paramref name="viewportOffset"/> plus a quarter of <paramref name="viewportHeight"/>). <paramref
        /// name="anchorOffsets"/> is ordered top-to-bottom; a <c>NaN</c> entry (a section not yet realized/measured)
        /// stops the scan there, same as <see cref="BandLayout.ActiveSection"/>. An empty span, or a NaN first entry,
        /// answers −1 — NO ANSWER, so the caller holds whatever section it already had (D40) rather than snapping to 0
        /// while the page is still laying out.</summary>
        public static int ActiveSectionOf(ReadOnlySpan<float> anchorOffsets, float viewportOffset, float viewportHeight)
        {
            if (anchorOffsets.Length == 0 || float.IsNaN(anchorOffsets[0])) return -1;
            float line = viewportOffset + MathF.Max(0f, viewportHeight) * ViewportFraction;
            int active = 0;
            for (int i = 0; i < anchorOffsets.Length; i++)
            {
                float top = anchorOffsets[i];
                if (float.IsNaN(top)) break;
                if (top <= line) active = i; else break;
            }
            return active;
        }
    }
}
