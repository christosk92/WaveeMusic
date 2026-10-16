// ── Themes/Classic/Structure/Views/Detail/ClassicDetailKit.Helpers.cs — the page-facing builders the detail views and the library panes share (themes plan 6.1, T10c2) ──
//
// `Detail.UI.cs` kept these as the seam while the pages moved: the hero's satellite, the rail's icon action (`Fab`), the More button (its flyout is built
// lazily AT OPEN from the newest menu factory: ONE menu host, so a surface never grows a second one), the billed-artist attribution line, the pane's and the
// reader's command circle (from `Album.UI.cs`) and the save target / share action a view draws from a `Detail.Identity`. The last page moved with T10c2, so
// they live with the structure that draws them.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;

namespace Wavee.Themes.Classic;

internal static partial class ClassicDetailKit
{
    // ══ T10c2: THE SHARED ICON ACTIONS ══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The hero's quiet secondary action: the standard 32/r4 <see cref="Controls.IconAction"/> (its own 16-DIP
    /// glyph rung), and a tooltip naming it.</summary>
    public static Element Satellite(string glyph, string tooltip, Action? onClick, float size = ClassicDetailKitLayout.SatelliteSize)
        => ToolTip.Wrap(Controls.IconAction(glyph, onClick, size: size), tooltip);

    /// <summary>The rail's icon action, on the standard 32/r4 ladder (<see cref="Controls.IconAction"/>). <paramref name="size"/>
    /// is the rail's edge by default; the library panes ask for their own 32 so the ⋯ matches the standard icon buttons
    /// beside it instead of standing apart.
    /// <para>Workstream B: was a round FAB (the Emphatic scale tier, the Subtle interaction fill); every non-media FAB
    /// on the rail is now this same standard icon button — the round plate stays reserved for media exceptions
    /// (<see cref="Controls.PlayFab"/>, <see cref="Controls.CoverActionFab"/>).</para></summary>
    public static BoxEl Fab(string glyph, Action? onClick, float size = Detail.RailLayout.FabSize)
        => Controls.IconAction(glyph, onClick, size: size);

    /// <summary>The pane's and the reader's secondary verb: the standard 32/r4 <see cref="Controls.IconAction"/>, named
    /// by its tooltip.
    /// <para><c>// Workstream B</c>: was a 36-px subtle CIRCLE — the detail rail's round FAB shape. Every non-media icon
    /// affordance is now this same square ladder, so a pane's Shuffle and the rail's satellites read as one grammar.</para></summary>
    public static Element CommandCircle(string glyph, string name, Action tap, float size = 0f)
        => Controls.Named(size > 0f ? Controls.IconAction(glyph, tap, size: size) : Controls.IconAction(glyph, tap), name);

    // ══ T10c2: THE MORE BUTTON ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The More button: its flyout is built lazily AT OPEN from the newest menu factory. The library's panes (<c>Album.Pane</c>, <c>Artist.Reader</c>) head
    /// their command rows with the SAME ⋯ — one menu host, so a surface never grows a second one (library rework §5.0).</summary>
    public static Element MoreButton(Func<ContextMenuModel?> menu, float size)
        => Embed.Comp(new MoreProps(size, menu), static () => new MoreHost());

    sealed record MoreProps(float Size, Func<ContextMenuModel?> Menu)
    {
        public bool Equals(MoreProps? o) => o is not null && Size == o.Size;
        public override int GetHashCode() => Size.GetHashCode();
    }

    static readonly ContextMenuOptions s_moreMenu = new()
    {
        PointerPlacement = FlyoutPlacement.BottomEdgeAlignedRight,
        KeyboardPlacement = FlyoutPlacement.BottomEdgeAlignedRight,
    };

    sealed class MoreHost : Component, IPropsHost
    {
        MoreProps? _latest;
        readonly Signal<MoreProps?> _props = new(null);
        readonly Func<ContextMenuModel?> _menu;

        public MoreHost() => _menu = () => _latest?.Menu();

        public void ApplyProps(object props)
        {
            _latest = (MoreProps)props;
            _props.Value = _latest;
        }

        public override Element Render()
        {
            _ = _props.Value;
            var p = _latest!;
            var overlay = UseContext(Overlay.Service);
            // Workstream B: the ⋯ is the standard 32/r4 IconAction everywhere.
            BoxEl button = Controls.IconAction(Icons.More, null, requestsContext: true, size: p.Size);
            if (!Controls.IsNullOverlay(overlay)) button = ContextSheets.Attach(button, overlay, _menu, s_moreMenu);
            return ToolTip.Wrap(button, Loc.Get(Strings.Common.More));
        }
    }

    // ══ T10c2: THE BILLED-ARTIST ATTRIBUTION LINE ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>The billed artists as one comma-joined run of 14/20/600 links (accent on hover), each one shrinkable and
    /// clipped so a long bill yields instead of pushing the meta line out. An empty bill is an EMPTY BOX, never a blank
    /// row of air: the header's gap must not open for an attribution that does not exist. A detail-frame piece, shared by
    /// the library's album pane, the show pane's publisher slot and the artist reader (library rework §5.5).</summary>
    public static Element ArtistLine(IReadOnlyList<Controls.Face>? artists)
    {
        if (artists is not { Count: > 0 }) return new BoxEl();
        var kids = new Element[artists.Count * 2 - 1];
        for (int i = 0, k = 0; i < artists.Count; i++)
        {
            if (i > 0) kids[k++] = new TextEl(", ") { Size = ClassicDetailKitLayout.ArtistLineSize, LineHeight = ClassicDetailKitLayout.ArtistLineHeight, Weight = 600, Color = Tok.TextSecondary };
            var face = artists[i];
            var label = new TextEl(face.Name)
            {
                Size = ClassicDetailKitLayout.ArtistLineSize, LineHeight = ClassicDetailKitLayout.ArtistLineHeight, Weight = 600, Color = Tok.TextSecondary, HoverColor = Tok.AccentTextPrimary,
                MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            };
            kids[k++] = face.OnClick is { } go
                ? new BoxEl { Role = AutomationRole.Hyperlink, Focusable = true, Cursor = CursorId.Hand, OnClick = go, Shrink = 1f, MinWidth = 0f, Children = [label] }
                : label;
        }
        return new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, ClipToBounds = true, Children = kids };
    }

    // ══ T10c2: THE SAVE TARGET AND THE SHARE ACTION ═════════════════════════════════════════════════════════════════════════════

    /// <summary>The uri the heart saves: the identity's own save target, else its subject.</summary>
    public static EntityUri SaveTargetOf(Detail.Identity id) => id.SaveTarget.IsValid ? id.SaveTarget : id.Subject;

    /// <summary>Copy the page's web link with the confirmation toast — only when a clipboard seam exists.</summary>
    public static Action? ShareActionFor(Detail.Identity id)
    {
        if (id.ShareUrl is not { Length: > 0 } url) return null;
        return () =>
        {
            if (Actions.Services.Clipboard is not { } copy) return;
            copy(url);
            _ = Notify.Say(Loc.Get(Strings.Menu.LinkCopied), InfoBarSeverity.Success);
        };
    }
}
