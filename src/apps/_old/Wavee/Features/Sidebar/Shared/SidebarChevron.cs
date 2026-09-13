using System;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// R3.1.7a — THE ONE disclosure chevron for every sidebar surface.
//
// It used to be a GLYPH SWAP (ChevronUp ⇄ ChevronDown on a section header, ChevronRight ⇄ ChevronDown on a folder row):
// two different glyphs traded on a state flip, which cannot animate — the mark simply teleports, and a section that opens
// with a 150ms reveal below a hard-cut chevron reads as two unrelated events. This is ONE glyph whose ROTATION rides
// MotionTok.ControlFast through AnimScheduler.SeedValue, so the token owns the dynamics AND the reduced-motion policy
// (never a Motion.ReducedMotion branch here — that global is a hook-order hazard, see AnimScheduler.Structural).
//
// WHY A COMPONENT. The rotation lives on an AnimEngine track, which needs the node handle (UseRef) and an edge-triggered
// effect (UseEffect) — hooks a static row builder cannot own, and hooks a RECYCLING row slot must not grow conditionally.
// Its own component keeps the hook order fixed and scopes the re-render to 10 DIP of chrome.
//
// PROPS FREEZE AT MOUNT, so `open` is a Func, not a bool: the delegate is invoked inside THIS component's Render, so
// whatever signals it reads subscribe this component. Callers therefore pass a closure that reads the live state
// (a section-collapse epoch, a folder-expansion version) rather than a captured snapshot.
sealed class SidebarChevron : Component
{
    readonly Func<bool> _open;
    readonly string _glyph;
    readonly float _size;
    readonly float _openDeg;

    SidebarChevron(Func<bool> open, string glyph, float size, float openDeg)
    {
        _open = open; _glyph = glyph; _size = size; _openDeg = openDeg;
    }

    /// <summary>A SECTION header's chevron: ChevronDown at rest, flipped 180° when the section is open (which is the
    /// ChevronUp the swap used to draw, arrived at by rotating).</summary>
    public static Element Section(Func<bool> open, float size = 10f)
        => Embed.Comp(() => new SidebarChevron(open, Icons.ChevronDown, size, 180f));

    /// <summary>A DISCLOSURE chevron (a rootlist folder, a pinned folder, a V3 tree row): ChevronRight at rest, rotated
    /// 90° when expanded (which is the ChevronDown the swap used to draw).</summary>
    public static Element Disclosure(Func<bool> open, float size = 10f)
        => Embed.Comp(() => new SidebarChevron(open, Icons.ChevronRight, size, 90f));

    public override Element Render()
    {
        // The delegate's own signal reads ARE this component's subscription (see the class remarks).
        bool open = _open();
        float target = open ? _openDeg : 0f;

        var node = UseRef<NodeHandle>(default);
        var seeded = UseRef(false);

        UseEffect(() =>
        {
            var anim = Context.Anim;
            var scene = Context.Scene;
            if (anim is null || scene is null || node.Value.IsNull || !scene.IsLive(node.Value)) return;
            if (!seeded.Value)
            {
                // First mount (and every RECYCLE into a different section's state): place the resting angle with no
                // visible motion — a realize must not animate a chevron that was never toggled.
                seeded.Value = true;
                anim.SeedValue(node.Value, AnimChannel.Rotation, target, MotionTokenId.DisclosureChevron, from: target);
                return;
            }
            // A mid-flight toggle retargets from the LIVE angle (from: null) instead of restarting.
            anim.SeedValue(node.Value, AnimChannel.Rotation, target, MotionTokenId.DisclosureChevron);
        }, target);

        return new BoxEl
        {
            Width = _size, Height = _size, Shrink = 0f,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            HitTestVisible = false,
            OnRealized = h => node.Value = h,
            Children = [Icon(_glyph, _size, Tok.TextTertiary)],
        };
    }
}
