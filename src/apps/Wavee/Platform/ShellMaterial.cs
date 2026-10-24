// ── Platform/ShellMaterial.cs ──────────────────────────────────────────────────────────────────────────────────────
// the shell MATERIAL channel: the types the shell publishes, the hand-over rule, the veil's settlement decision
//
// Role: CORE (themes plan T4c). These lived in `Platform/Design.cs` §16 and §17 until T4c. The CHANNEL is core: the page
// that claims the slot, the owner token and the hand-over protocol (`TintOwnership`) decide, and the data the pages publish
// is `ShellMaterialState`. What DRAWS it is the structure's: `Shell.Material` (the layer under the chrome column) and the four
// cover leaves (`Shell.Material.PageTone`, `.ArtistBlend`, `.ArtistVeil`, `.TintBinder`), Classic's in
// `Themes\Classic\Structure\Views\Material\ClassicMaterial.cs`. The pages mount the leaves through the `Palette.Host.cs` helpers,
// which mount the view keys.

using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace Wavee;

/// <summary>One shell wash resolved from its WINDOW-relative ellipse to the CLIPPED box the shell actually paints, plus
/// that ellipse re-expressed relative to the clipped box (which is the space a radial gradient's centre/radius live in).
/// <para><see cref="W"/>/<see cref="H"/> are window fractions; <see cref="AnchorRight"/>/<see cref="AnchorBottom"/> say
/// which window edge the box hangs off (a clamped ellipse always touches at least one), so the layer is placed by ZStack
/// self-alignment and never needs a transform. <see cref="Center"/>/<see cref="Radius"/> are node-relative CONSTANTS:
/// the box is a fraction of the window and the ellipse scales with it, so a resize only re-scales the box.</para></summary>
public readonly record struct ShellWashPlacement(
    float W, float H, bool AnchorRight, bool AnchorBottom, Point2 Center, Point2 Radius, float FadeOffset);

/// <summary>One artwork-derived radial wash: the colour at the wash's origin plus the ARTWORK KEY it was graded from.
/// The key is the wash's IDENTITY — two washes with the same resolved colour but different art are different layers, and
/// it is what the shell keys its layer nodes on so an artwork change REMOUNTS (and therefore cross-fades) the layer.
/// Gradients carry no brush-fade channel, so a wash can only cross-fade BY MOUNT.</summary>
public readonly record struct WashLayer(ColorF Color, string? ArtworkKey);

/// <summary>Home's three-wash composition, in STACKING order. Any leg may be null — a module whose artwork has not been
/// graded yet simply contributes no layer.</summary>
public readonly record struct HomeWash(WashLayer? Hero, WashLayer? Weekly, WashLayer? Mix);

/// <summary>The published shell-material state: an OWNER token plus the two mutually-exclusive material forms — a flat
/// <see cref="Tint"/> (detail pages) or a three-layer radial <see cref="Wash"/> (Home). Both null ⇒ the neutral ground.
/// <para>The owner makes nav transitions race-free: only the page that last CLAIMED the slot may refresh it.</para></summary>
public readonly record struct ShellMaterialState(object? Owner, ColorF? Tint, HomeWash? Wash);

/// <summary>The shell-owned, page-scoped MATERIAL channel. The shell publishes one signal at the root and paints it as
/// the layer directly above the ground that backs ALL chrome — title bar, toolbar, sidebar, player dock. The active page
/// CLAIMS it, so the window's chrome carries the album/playlist/Home colour; a page without a colour of its own is
/// claimed neutral by the content host.
/// <para>This IS a Mica scrim: the chrome is Mica-passthrough, so the material composites over the live window material
/// and carries the page's hue into it, while the content pane above keeps its own surface so the PAGE never depends on
/// the wallpaper. No native interop, no GPU pass — one flat rect, or up to three clipped radial-gradient rects.</para></summary>
public static class ShellMaterial
{
    /// <summary>Context slot — the shell provides its material signal here. Null when no shell is mounted (headless
    /// tests), in which case a consumer simply no-ops.</summary>
    public static readonly Context<Signal<ShellMaterialState>?> Slot = new(null);

    /// <summary>THE one way anything writes the material — a HAND-OVER, never a clear. A page CLAIMS the slot on its
    /// first publish and on a KeepAlive reactivation; any other publish is a refresh that lands only while that page is
    /// still the owner, so the exit tail of a page that was navigated away from can never paint over its successor. A
    /// page that parks or unmounts writes NOTHING: its material stays until the next page claims. A claim whose colour is
    /// not graded yet is the neutral ground (Classic tuning D15): the claim is the new page's, so the previous page's
    /// colour never sits under the new page's skeleton; the grading cross-fades in when it lands.
    /// <para>The shell paints the slot as of the page on screen (<c>Shell.PresentedMaterial</c>), so a claim lands with the
    /// page swap: the chrome changes colour once per navigation when the colour is known, never under the leaving page.</para></summary>
    /// <param name="definite">The page has DECIDED it carries no colour (washes off, or this layout applies no tint) —
    /// as opposed to simply not having graded a cover YET, which is transient.</param>
    public static void Publish(Signal<ShellMaterialState>? slot, object owner, bool isClaim, bool definite,
                               ColorF? tint, HomeWash? wash)
    {
        if (slot is null) return;
        var cur = slot.Peek();
        // `hasColor` counts a WASH leg too, so Home's three-radial form participates in the same protocol as a detail
        // page's flat tint.
        bool hasColor = tint.HasValue
                        || (wash is { } w && (w.Hero.HasValue || w.Weekly.HasValue || w.Mix.HasValue));
        switch (TintOwnership.Resolve(cur.Owner, new TintOwnership.Request(owner, isClaim, definite, hasColor)))
        {
            case TintOwnership.Outcome.WriteKnownColor: slot.Value = new ShellMaterialState(owner, tint, wash); break;
            case TintOwnership.Outcome.WriteNeutral: slot.Value = new ShellMaterialState(owner, null, null); break;
        }
    }
}

/// <summary>The material hand-over protocol, as a pure decision table. Two shipped bugs and one tuning item are encoded here
/// and none is re-derivable from the call sites:
/// <list type="number">
/// <item><b>The neutral dip.</b> A page that deactivated used to CLEAR the slot, so every navigation dipped to "no colour"
/// and then popped to the real one, even between two pages whose colours were known. The fix is a HAND-OVER: a
/// deactivating page never writes at all (there is no "clear" request in this API); the claiming page writes its own
/// colour, and the shell paints the slot as of the page on screen, so a known colour replaces the old one in one fade.</item>
/// <item><b>The borrowed tone (Classic tuning D15).</b> A claim that does not know its colour yet used to HOLD the previous
/// page's, so an album's skeleton wore the album before it and its own tone faded in after the page had landed. A claim
/// is the new page's: without a colour it is the neutral ground, and the grading cross-fades in when it lands.</item>
/// <item><b>The two-publisher race.</b> KeepAlive keeps an outgoing page mounted and drawing for the length of its exit
/// transition, so its effects can still fire — and unconditionally re-publish its own colour — WHILE the incoming page
/// is already current. The fix is single ownership: once a page has CLAIMED, every other page's ordinary publish is
/// checked against the CURRENT owner and dropped if it does not match, regardless of that page's own parked/active
/// state.</item>
/// </list></summary>
public static class TintOwnership
{
    /// <summary>One publish attempt from a binder leaf.</summary>
    /// <param name="Owner">The publishing page's identity token (REFERENCE identity only — never compared by value).</param>
    /// <param name="IsClaim">True at the two edges that make a page "become current": its own first mount, and a
    /// KeepAlive reactivation. False for an ordinary re-render.</param>
    /// <param name="Definite">True when the page has DECIDED it carries no colour — as opposed to simply not having
    /// graded a cover YET, which is transient.</param>
    /// <param name="HasColor">True when the page currently has a real, graded colour to show.</param>
    public readonly record struct Request(object Owner, bool IsClaim, bool Definite, bool HasColor);

    /// <summary>What the caller should do with the slot. <see cref="NoWrite"/> means "touch nothing" — the write must
    /// not happen at all, not merely happen to reproduce the current value, because a no-op write would still need
    /// somewhere to source its colour from and this decision is precisely that a stray publisher gets none.</summary>
    public enum Outcome
    {
        /// <summary>Leave the slot exactly as it is.</summary>
        NoWrite,
        /// <summary>Write the caller's own known colour as this page's tint.</summary>
        WriteKnownColor,
        /// <summary>Write the neutral ground: this page has DEFINITELY decided it carries no colour, or it claims before its
        /// colour is known.</summary>
        WriteNeutral,
    }

    /// <summary>The one decision every shell-tint publish goes through.</summary>
    public static Outcome Resolve(object? currentOwner, in Request req)
    {
        bool amOwner = req.Owner is not null && ReferenceEquals(currentOwner, req.Owner);
        // Not the current owner and not claiming ⇒ a superseded/stray publish. Never lands.
        if (!req.IsClaim && !amOwner) return Outcome.NoWrite;

        if (req.Definite) return req.HasColor ? Outcome.WriteKnownColor : Outcome.WriteNeutral;
        if (req.HasColor) return Outcome.WriteKnownColor;

        // Colour genuinely not known yet: a claim is the new page's, so it shows the neutral ground (never the previous
        // page's colour, D15); an ordinary refresh from the already-current owner has nothing new to say, so it writes
        // nothing rather than repeat itself.
        return req.IsClaim ? Outcome.WriteNeutral : Outcome.NoWrite;
    }
}

/// <summary>The WRITE side of the shell material as a value a props record can carry. A props record may not hold a writable
/// <c>Signal&lt;T&gt;</c> (WVTH006), and the tint binder leaf (a theme's view) has to publish into the slot the page read from the
/// context, so the page hands it this port: one per slot (cached), compared by reference, a no-op for a null slot (headless
/// tests, no shell mounted). It carries no decision: <see cref="Publish"/> is <see cref="ShellMaterial.Publish"/>.</summary>
public sealed class MaterialPort
{
    static readonly MaterialPort s_none = new(null);
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Signal<ShellMaterialState>, MaterialPort> s_ports = new();

    readonly Signal<ShellMaterialState>? _slot;

    MaterialPort(Signal<ShellMaterialState>? slot) => _slot = slot;

    /// <summary>The port of <paramref name="slot"/> (the same instance every time); a null slot answers the no-op port.</summary>
    public static MaterialPort Of(Signal<ShellMaterialState>? slot)
        => slot is null ? s_none : s_ports.GetValue(slot, static s => new MaterialPort(s));

    /// <summary>The hand-over write (<see cref="ShellMaterial.Publish"/>): a claim, or a refresh that lands only while the owner is current.</summary>
    public void Publish(object owner, bool isClaim, bool definite, ColorF? tint, HomeWash? wash)
        => ShellMaterial.Publish(_slot, owner, isClaim, definite, tint, wash);
}

// ── the veil's settlement (pure) ───────────────────────────────────────────────────────────────────────────────────────

/// <summary>The pre-settlement Exit guard the Classic artist blend wash and the artist hero veil (<c>ClassicCoverBlend</c>, <c>ClassicCoverVeil</c>) share,
/// pulled out of both Render methods as a pure decision so it is unit-testable without a render loop (repo rule:
/// no source-text tests — extract the decision, test the extraction).
/// <para><b>The defect:</b> <c>Palette.TryScheme</c> enqueues on a miss, so the render right after mount routinely
/// resolves to a Payload/Default GUESS before the real grading lands a frame or two later. Both leaves re-key on
/// rung + colour (a Gradient can't cross-fade through <c>BrushTransitionMs</c>), so that guess-to-graded swap is a
/// keyed node replacement. If the OUTGOING (guessed) node animates its Exit the same way a genuine LATER re-key
/// does, its fade-out overlaps the INCOMING (graded) node's fade-in — two differently-hued, semi-transparent veils
/// summing over the artist photo for one <c>ControlNormal</c>, which reads as the lighter, more opaque wash this
/// exists to prevent.</para>
/// <para><b>The fix:</b> every swap before the ladder has produced a DEFINITIVE first answer (a real grading hit,
/// or Definite because nothing better can ever arrive — no url, or a url the endpoint cannot grade) exits INSTANTLY
/// (no Exit spec ⇒ immediate structural removal, no deferred fade) instead of animating. The incoming node can
/// still fade its Enter in on its own — with the outgoing one already gone, there is nothing left for it to
/// composite against, so at most one veil is ever live. Once settled, later changes (a live re-grading, a theme
/// flip) cross-fade normally, same as before this fix.</para>
/// <para>Public, not internal: this assembly has no <c>InternalsVisibleTo</c> (see <c>Controls.cs</c>), and the
/// invariant is pinned by a fact in <c>Wavee.Tests</c>.</para></summary>
public static class VeilSettlement
{
    /// <summary>Advances the leaf's settled state for this frame and says whether the node this frame's re-key
    /// REPLACES may animate its Exit. <paramref name="alreadySettled"/> is the leaf's state entering the frame;
    /// the returned <c>Settled</c> is what the leaf should hold for the NEXT frame. Settlement is one-way — once
    /// true it stays true for the life of the component instance (a fresh mount, e.g. navigating to a different
    /// artist, gets a fresh instance and starts unsettled again).</summary>
    public static (bool Settled, bool ExitAnimates) Advance(bool alreadySettled, AccentLadder.Rung rung, bool definite)
    {
        bool settled = alreadySettled || rung == AccentLadder.Rung.Graded || definite;
        return (settled, settled);
    }
}
