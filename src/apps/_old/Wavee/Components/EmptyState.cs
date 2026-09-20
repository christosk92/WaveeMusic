using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;

namespace Wavee;

/// <summary>THE empty-state grammar. Every "there is nothing here" surface in the app is this component at one of its
/// two scales, and the app has no second way to say it.
///
/// <para>THE GRAMMAR — three parts, in this order, and nothing else:</para>
/// <list type="number">
/// <item><b>A display-face HEADLINE.</b> <see cref="WaveeType.PageHero"/> (28 / 36 / 600) at page scale, the Subtitle
/// rung (20 / 28 / 600) in a rail. Big type IS the empty state: it turns a hole in the page into a deliberate,
/// composed thing, which is the whole Zune-editorial move this app is aiming at. Nothing else on an empty surface has
/// to carry it.</item>
/// <item><b>One optional CAPTION line</b> — what to do about it, in one sentence, at the metadata rung.</item>
/// <item><b>At most one QUIET action</b> — <c>Button.Standard</c>, never <c>Button.Accent</c>. This is the accent-budget
/// rule made structural (see <c>WaveeAccent</c>): AccentAction is scarce and belongs to the page's real primary. An
/// empty page's "Browse" is a recovery route, not the app's most important verb, and accenting it meant an empty
/// library shouted louder than a full one.</item>
/// </list>
///
/// <para>NO GLYPH. The old grammar opened with a 32-DIP muted icon, and six surfaces had grown their own variants
/// around it (SearchPage, HistoryPage, FriendsPanel, NowPlayingPanel, QueuePanel, LibraryPage) — each with its own
/// glyph size, its own gap, its own heading rung. A decorative pictogram above a heading adds no information the
/// heading does not already carry, and it was the part every rogue copy diverged on first. It is gone, and the
/// <c>glyph</c> parameter with it: a compile error at the call site is how a removed part of a grammar should
/// land.</para></summary>
public static class EmptyState
{
    /// <summary>PAGE scale: a <see cref="WaveeType.PageHero"/> headline. For a content region — a page body, a shelf
    /// region, a dialog. Use <see cref="Compact"/> in anything narrower than ~340 DIP.</summary>
    public static Element Build(string title, string? subtitle = null,
        string? actionLabel = null, Action? onAction = null)
        => Compose(WaveeType.PageHero(title), subtitle, actionLabel, onAction);

    /// <summary>RAIL scale: the same grammar with the Subtitle rung (20 / 28 / 600) as its headline, for the ~340-DIP
    /// and narrower panels — the queue rail, the friends rail, the now-playing panel, the sidebar's own empty states.
    /// The ONE sanctioned variant: 28/36 wraps to three ragged lines at 240 DIP, which is not big type, it is a
    /// paragraph. Everything below the headline is identical, so a rail's empty state and a page's still read as the
    /// same sentence spoken at two volumes.</summary>
    public static Element Compact(string title, string? subtitle = null,
        string? actionLabel = null, Action? onAction = null)
        => Compose(Ui.Subtitle(title), subtitle, actionLabel, onAction);

    static Element Compose(TextEl headline, string? subtitle, string? actionLabel, Action? onAction)
    {
        // Wrap, but no text-align: the engine's TextEl has no alignment knob, so centring is the CONTAINER's job
        // (Centered's AlignItems) and each run is its own centred box.
        var kids = new List<Element>(4)
        {
            headline with { Wrap = TextWrap.Wrap },
        };
        if (subtitle is not null)
            kids.Add(WaveeType.TrackMeta(subtitle) with { Wrap = TextWrap.Wrap });
        if (actionLabel is not null && onAction is not null)
        {
            kids.Add(new BoxEl { Height = Spacing.L });
            // Standard, never Accent — see the grammar note above.
            kids.Add(Button.Standard(actionLabel, onAction));
        }
        return Centered(kids);
    }

    public static Element Default() => Build(Loc.Get(Strings.Common.EmptyTitle), Loc.Get(Strings.Common.EmptySubtitle));

    internal static Element Centered(List<Element> kids) => new BoxEl
    {
        Direction = 1, Grow = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Gap = Spacing.XS, Padding = Edges4.All(Spacing.XXL), Children = kids.ToArray(),
    };
}
