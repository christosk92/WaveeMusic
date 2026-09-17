// ── Wavee.Tests/DetailVerticalFooterTests.cs — where the facts cards live in the hero system (the slot map) ────────
//
// Ported from _old/Wavee.Tests/DetailVerticalFooterTests.cs onto `Detail.VerticalLayout`'s slot map (Entities/Detail.cs;
// `RowSlots`/`FooterIndex`/`ItemCount`/`ItemRole` are public in 0.3). Every assertion is 0.2.9's.
//
// In the rail arm the facts bento sits BESIDE the tracks. In the hero system it is the page FOOTER: one virtual-list
// slot after the last row, never a block in the hero's identity column (the page's opening). Everything that can go
// wrong with that is an INDEX, so the placement is walked over the whole range of list sizes.

using Xunit;
using VerticalLayout = Wavee.Detail.VerticalLayout;

namespace Wavee.Tests;

public class DetailVerticalFooterTests
{
    const int LadderMax = 64;

    /// <summary>The footer is ONE extra item, and only when the page has facts to show.</summary>
    [Theory]
    [InlineData(0, 3, 4)]      // an empty/unloaded list still holds one placeholder slot
    [InlineData(1, 3, 4)]
    [InlineData(50, 52, 53)]
    [InlineData(10_000, 10_002, 10_003)]
    public void ItemCount_AddsExactlyOneSlotForTheFooter(int visible, int without, int with)
    {
        Assert.Equal(without, VerticalLayout.ItemCount(visible, hasFacts: false));
        Assert.Equal(with, VerticalLayout.ItemCount(visible, hasFacts: true));
    }

    /// <summary>The footer is the LAST slot — the very bottom of the page.</summary>
    [Fact]
    public void FooterIsTheLastSlot_AtEveryListSize()
    {
        for (int visible = 0; visible <= LadderMax; visible++)
        {
            int last = VerticalLayout.ItemCount(visible, hasFacts: true) - 1;
            Assert.Equal(last, VerticalLayout.FooterIndex(visible));
            Assert.Equal(VerticalItemRole.Footer, VerticalLayout.ItemRole(last, visible, hasFacts: true));
        }
    }

    /// <summary>…and it never steals a row slot or the empty-list placeholder's slot.</summary>
    [Fact]
    public void FooterNeverDisplacesARowOrThePlaceholder()
    {
        for (int visible = 0; visible <= LadderMax; visible++)
        {
            Assert.Equal(VerticalItemRole.Hero, VerticalLayout.ItemRole(0, visible, hasFacts: true));
            Assert.Equal(VerticalItemRole.Chrome, VerticalLayout.ItemRole(1, visible, hasFacts: true));
            for (int i = VerticalLayout.PrefixCount; i < VerticalLayout.PrefixCount + visible; i++)
                Assert.Equal(VerticalItemRole.ExpandableTrack, VerticalLayout.ItemRole(i, visible, hasFacts: true));
            if (visible == 0)
                Assert.Equal(VerticalItemRole.Empty,
                    VerticalLayout.ItemRole(VerticalLayout.PrefixCount, visible, hasFacts: true));
        }
    }

    /// <summary>Exactly one footer, ever — never twice, never once per recycled slot.</summary>
    [Fact]
    public void ExactlyOneFooterSlot_AndNoneWhenThePageHasNoFacts()
    {
        for (int visible = 0; visible <= LadderMax; visible++)
        {
            int withFooter = 0, withoutFooter = 0;
            for (int i = 0; i < VerticalLayout.ItemCount(visible, hasFacts: true); i++)
                if (VerticalLayout.ItemRole(i, visible, hasFacts: true) == VerticalItemRole.Footer)
                    withFooter++;
            for (int i = 0; i < VerticalLayout.ItemCount(visible, hasFacts: false); i++)
                if (VerticalLayout.ItemRole(i, visible, hasFacts: false) == VerticalItemRole.Footer)
                    withoutFooter++;
            Assert.Equal(1, withFooter);
            Assert.Equal(0, withoutFooter);
        }
    }

    /// <summary>The footer sits OUTSIDE the insertable row range, so a drag can never aim an insertion at it.</summary>
    [Fact]
    public void FooterIsOutsideTheInsertableRange()
    {
        for (int visible = 0; visible <= LadderMax; visible++)
        {
            int first = VerticalLayout.PrefixCount;
            int lastInsertable = first + visible;   // exclusive end of the row band
            Assert.True(VerticalLayout.FooterIndex(visible) >= lastInsertable);
        }
    }

    /// <summary>Turning the footer off cannot change where anything else lives.</summary>
    [Fact]
    public void RolesWithoutFacts_AreUnchanged()
    {
        for (int visible = 0; visible <= LadderMax; visible++)
        {
            Assert.Equal(VerticalLayout.PrefixCount + (visible == 0 ? 1 : visible),
                VerticalLayout.ItemCount(visible, hasFacts: false));
            Assert.Equal(VerticalItemRole.Empty,
                VerticalLayout.ItemRole(VerticalLayout.PrefixCount + visible, visible, hasFacts: false));
        }
    }

    /// <summary>RowSlots is the one number the other three share: an empty list keeps one slot, a real list its length.</summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(37, 37)]
    public void RowSlots_KeepsOnePlaceholderSlotForAnEmptyList(int visible, int expected)
        => Assert.Equal(expected, VerticalLayout.RowSlots(visible));
}
