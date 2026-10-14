// ── Wavee.Tests/FrameBitsTests.cs — the decisions behind the frame's small views (themes plan 6.0, T4c) ───────────────────
//
// T4c moved the masthead, the material and its cover leaves, the narrow drawer, the command palette, the history menu, the
// file-drop cue, the drag chip, the setup scrim and the frame-rate HUD into the structure. The pictures are mounted facts
// (`Wavee.ShellTests\FrameBitsMountedTests.cs`); what stayed in the core, and what the move had to add, is pure and lives here:
//
//   the masthead chrome   `Shell.ChromeFor`: the title, the trail as navigable crumbs (the current page is not clickable), the tool state
//   the material port     the write side of the shell material a props record can carry: one port per slot, a null slot writes nothing,
//                         and publishing through it is the hand-over (`TintOwnership`): a claim, an owner-only refresh, never a clear
//   the mounts            the pages' mount helpers (`Palette.PageTonePlane`, ...) keep the page's own `Key` on the slot they mount

using FluentGpu.Foundation;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests;

public class FrameBitsTests
{
    static readonly ColorF Blue = new(0.1f, 0.2f, 0.7f, 0.14f);
    static readonly ColorF Red = new(0.8f, 0.1f, 0.1f, 0.14f);

    // ── the masthead chrome ──────────────────────────────────────────────────────────────────────────────────────────────

    static IReadOnlyList<Shell.Crumb> Trail() =>
    [
        new Shell.Crumb("Browse", new Shell.Route(Shell.RouteKind.Browse)),
        new Shell.Crumb("Pop", null),
    ];

    [Fact]
    public void The_chrome_carries_the_title_and_every_crumb_in_order()
    {
        var chrome = Shell.ChromeFor("Pop", Trail(), null);
        Assert.Equal("Pop", chrome.Title);
        Assert.Equal(new[] { "Browse", "Pop" }, chrome.Crumbs.Select(c => c.Label).ToArray());
    }

    [Fact]
    public void A_parent_crumb_navigates_and_the_current_page_does_not()
    {
        var chrome = Shell.ChromeFor("Pop", Trail(), null);
        Assert.NotNull(chrome.Crumbs[0].Go);
        Assert.Null(chrome.Crumbs[1].Go);
    }

    [Fact]
    public void A_trail_of_the_current_page_alone_has_no_parent_to_click()
    {
        var chrome = Shell.ChromeFor("Browse", [new Shell.Crumb("Browse", null)], null);
        Assert.Single(chrome.Crumbs);
        Assert.Null(chrome.Crumbs[0].Go);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void The_tool_state_follows_the_publication(bool visible, bool loading)
    {
        var chrome = Shell.ChromeFor("Pop", Trail(), new Shell.MastheadPublication("Pop", visible, loading));
        Assert.Equal(visible, chrome.ToolsVisible);
        Assert.Equal(loading, chrome.ToolsLoading);
    }

    [Fact]
    public void A_route_that_published_nothing_offers_no_tool()
    {
        var chrome = Shell.ChromeFor("Pop", Trail(), null);
        Assert.False(chrome.ToolsVisible);
        Assert.False(chrome.ToolsLoading);
    }

    // ── the material port ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_slot_has_one_port()
    {
        var slot = new Signal<ShellMaterialState>(default);
        Assert.Same(MaterialPort.Of(slot), MaterialPort.Of(slot));
        Assert.NotSame(MaterialPort.Of(slot), MaterialPort.Of(new Signal<ShellMaterialState>(default)));
    }

    [Fact]
    public void A_null_slot_is_a_port_that_writes_nothing()
    {
        var port = MaterialPort.Of(null);
        Assert.Same(port, MaterialPort.Of(null));
        port.Publish(new object(), isClaim: true, definite: true, Blue, wash: null);   // no shell mounted: a no-op, not a throw
    }

    [Fact]
    public void A_claim_through_the_port_takes_the_slot_with_its_colour()
    {
        var slot = new Signal<ShellMaterialState>(default);
        var page = new object();
        MaterialPort.Of(slot).Publish(page, isClaim: true, definite: false, Blue, wash: null);
        Assert.Same(page, slot.Peek().Owner);
        Assert.Equal(Blue, slot.Peek().Tint);
    }

    [Fact]
    public void A_refresh_from_a_page_that_is_not_the_owner_never_lands()
    {
        var slot = new Signal<ShellMaterialState>(default);
        var owner = new object();
        var port = MaterialPort.Of(slot);
        port.Publish(owner, isClaim: true, definite: false, Blue, wash: null);

        port.Publish(new object(), isClaim: false, definite: false, Red, wash: null);   // the exit tail of a page navigated away from

        Assert.Same(owner, slot.Peek().Owner);
        Assert.Equal(Blue, slot.Peek().Tint);
    }

    [Fact]
    public void A_claim_without_a_colour_yet_keeps_what_is_showing_so_a_navigation_never_dips_to_neutral()
    {
        var slot = new Signal<ShellMaterialState>(default);
        var first = new object();
        var second = new object();
        var port = MaterialPort.Of(slot);
        port.Publish(first, isClaim: true, definite: false, Blue, wash: null);

        port.Publish(second, isClaim: true, definite: false, tint: null, wash: null);   // graded colour not known yet

        Assert.Same(second, slot.Peek().Owner);
        Assert.Equal(Blue, slot.Peek().Tint);
    }

    [Fact]
    public void A_page_that_decided_it_has_no_colour_publishes_the_neutral_ground()
    {
        var slot = new Signal<ShellMaterialState>(default);
        var page = new object();
        var port = MaterialPort.Of(slot);
        port.Publish(page, isClaim: true, definite: false, Blue, wash: null);

        port.Publish(page, isClaim: false, definite: true, tint: null, wash: null);

        Assert.Same(page, slot.Peek().Owner);
        Assert.Null(slot.Peek().Tint);
        Assert.Null(slot.Peek().Wash);
    }

    // ── the pages' mounts keep their key ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_cover_leaf_mounts_carry_the_pages_own_key()
    {
        Assert.Equal("detail-tone:x", Palette.PageTonePlane(null, null, 0f, 0f, heroOnly: false, key: "detail-tone:x").Key);
        Assert.Equal("artist-wash", Palette.ArtistBlendWash(null, 100f, 0.5f, key: "artist-wash").Key);
        Assert.Equal("artist-veil", Palette.ArtistHeroVeil(null, vertical: false, 100f, 100f, key: "artist-veil").Key);
        Assert.Equal("detail-tint", Palette.ShellTint(null, ready: false, apply: false, new object(), slot: null, key: "detail-tint").Key);
    }
}
