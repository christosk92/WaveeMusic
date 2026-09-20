using Wavee.Features.Detail;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// WHERE THE PLAYLIST METADATA CARDS LIVE in the HERO system — the pure half of the decision.
///
/// <para>The facts bento ("This week" + the 12-week strip, the tempo curve, the top-artists ranking) has two homes and
/// they are not a style choice. In the two-column/rail arm it sits in the RAIL, beside the tracks, where it costs them
/// no vertical space. In the hero system there is no rail, and it used to be the last block of the hero's identity
/// column — which is the page's OPENING, so three stacked analytics cards there pushed the first track below the fold
/// and the page opened on charts instead of on its songs. It is now the page FOOTER: one virtual-list slot after the
/// last row.</para>
///
/// <para>Everything that can go wrong with that is an INDEX: a footer that lands inside the row band steals a track's
/// slot; one that lands past the item total is never realized (the cards silently vanish); one that shares an index
/// with the empty-list placeholder replaces the "no songs" message. So the placement is a pure function, walked here
/// over the whole range of list sizes rather than spot-checked. The rendering half (which element, what padding) is
/// engine-bound and belongs to the live app, not to a test.</para>
/// </summary>
public class DetailVerticalFooterTests
{
    const int LadderMax = 64;

    /// <summary>The footer is ONE extra item, and only when the page actually has facts to show.</summary>
    [Theory]
    [InlineData(0, 3, 4)]      // an empty/unloaded list still holds one placeholder slot
    [InlineData(1, 3, 4)]
    [InlineData(50, 52, 53)]
    [InlineData(10_000, 10_002, 10_003)]
    public void ItemCount_AddsExactlyOneSlotForTheFooter(int visible, int without, int with)
    {
        Assert.Equal(without, DetailVerticalLayout.ItemCount(visible, hasFacts: false));
        Assert.Equal(with, DetailVerticalLayout.ItemCount(visible, hasFacts: true));
    }

    /// <summary>The footer is the LAST slot — the very bottom of the page, under the last track.</summary>
    [Fact]
    public void FooterIsTheLastSlot_AtEveryListSize()
    {
        for (int visible = 0; visible <= LadderMax; visible++)
        {
            int last = DetailVerticalLayout.ItemCount(visible, hasFacts: true) - 1;
            Assert.Equal(last, DetailVerticalLayout.FooterIndex(visible));
            Assert.Equal(DetailVerticalItemRole.Footer,
                DetailVerticalLayout.ItemRole(last, visible, hasFacts: true));
        }
    }

    /// <summary>…and it never steals a row slot: every live track still maps to its expandable container, and the
    /// empty-list placeholder still owns the one slot an empty list keeps.</summary>
    [Fact]
    public void FooterNeverDisplacesARowOrThePlaceholder()
    {
        for (int visible = 0; visible <= LadderMax; visible++)
        {
            Assert.Equal(DetailVerticalItemRole.Hero, DetailVerticalLayout.ItemRole(0, visible, hasFacts: true));
            Assert.Equal(DetailVerticalItemRole.Chrome, DetailVerticalLayout.ItemRole(1, visible, hasFacts: true));
            for (int i = DetailVerticalLayout.PrefixCount; i < DetailVerticalLayout.PrefixCount + visible; i++)
                Assert.Equal(DetailVerticalItemRole.ExpandableTrack,
                    DetailVerticalLayout.ItemRole(i, visible, hasFacts: true));
            if (visible == 0)
                Assert.Equal(DetailVerticalItemRole.Empty,
                    DetailVerticalLayout.ItemRole(DetailVerticalLayout.PrefixCount, visible, hasFacts: true));
        }
    }

    /// <summary>Exactly one footer, ever — the defect the move has to avoid is the cards showing up twice (once in the
    /// hero column, once at the bottom) or once per recycled slot.</summary>
    [Fact]
    public void ExactlyOneFooterSlot_AndNoneWhenThePageHasNoFacts()
    {
        for (int visible = 0; visible <= LadderMax; visible++)
        {
            int withFooter = 0, withoutFooter = 0;
            for (int i = 0; i < DetailVerticalLayout.ItemCount(visible, hasFacts: true); i++)
                if (DetailVerticalLayout.ItemRole(i, visible, hasFacts: true) == DetailVerticalItemRole.Footer)
                    withFooter++;
            for (int i = 0; i < DetailVerticalLayout.ItemCount(visible, hasFacts: false); i++)
                if (DetailVerticalLayout.ItemRole(i, visible, hasFacts: false) == DetailVerticalItemRole.Footer)
                    withoutFooter++;
            Assert.Equal(1, withFooter);
            Assert.Equal(0, withoutFooter);
        }
    }

    /// <summary>The footer sits OUTSIDE the insertable sub-range the playlist drop destination declares
    /// (<c>Range = (TrackStart, View().Length)</c>), so a drag can never aim an insertion at it and the cards can
    /// never ride the drop gap down.</summary>
    [Fact]
    public void FooterIsOutsideTheInsertableRange()
    {
        for (int visible = 0; visible <= LadderMax; visible++)
        {
            int first = DetailVerticalLayout.PrefixCount;
            int lastInsertable = first + visible;   // exclusive end of the row band
            Assert.True(DetailVerticalLayout.FooterIndex(visible) >= lastInsertable);
        }
    }

    /// <summary>Turning the footer off cannot change where anything else lives — the rail arm and a facts-less page
    /// must map exactly as they did before the footer existed.</summary>
    [Fact]
    public void RolesWithoutFacts_AreUnchanged()
    {
        for (int visible = 0; visible <= LadderMax; visible++)
        {
            Assert.Equal(DetailVerticalLayout.PrefixCount + (visible == 0 ? 1 : visible),
                DetailVerticalLayout.ItemCount(visible, hasFacts: false));
            Assert.Equal(DetailVerticalItemRole.Empty,
                DetailVerticalLayout.ItemRole(DetailVerticalLayout.PrefixCount + visible, visible, hasFacts: false));
        }
    }
}
