// ── Shell/Sidebar.UI.Zune.cs ───────────────────────────────────────────────────────────────────────────────────────
// The Zune navigation style's band (design NAV 3): the big pivots across the top, row 2 under them (Library's sub-pivots,
// or the page's published views and trailing control), the pin tiles beside the pivots, and the band's right-click menu
// (Layout ▸ · Pins in the title bar · Reset everything)
//
// Role: UI · Spec: sidebar-rework-implementation.md §P11 (NAV 3 UI) · the rules are Sidebar.Zune.cs (ZuneNavRules)
//
// LAYOUT STABILITY. The band root is ONE keyed node in every nav style. Its Height is ZuneNavRules.BandHeight(NavStyle)
// (84 under Zune on every route, 0 otherwise) and it carries Shell.ZuneBandAnim: a Size REVEAL on the content card's own
// tween, never a Reflow. The column therefore lays out once at the final height. The whole content region
// (Shell.ContentRegionAnim: a Position FLIP relative to the frame column plus a Height Relayout) eases down in the same
// tween, so the card's top travels with the band's revealed bottom edge, its bottom edge stays on the dock, and a nav-style
// switch moves the card one time. Row 2 is ALWAYS laid out (an empty route's row is just as tall), so a navigation never
// moves the card. The inset is DERIVED (FrameRules.ZuneBandInset: the card's x, 0 under Zune where the page bleeds to the window edge, plus the page gutter the pages read), so the first
// pivot word and the page title share an x. Band HEIGHT follows NavStyle (the frame commit); row 2's views WORDS follow
// Shell.Ui.PresentedNavStyle (the later, quiet hoist commit): until then the page head still draws them, and they would
// otherwise show twice.
//
// ROLES. The pivots carry Role Tab, like the stock SelectorBar items (it was NavigationItem). They stay a band-private item
// builder rather than an engine SelectorBar because SelectorBar auto-selects on focus entry with no selection, which
// would navigate. The pin tiles keep Role Button.

using System.Globalization;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Input;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public static partial class Sidebar
{
    /// <summary>The Zune band: <see cref="ZuneNavRules.BandHeight"/> tall under Zune, zero height otherwise. Mounted in the
    /// frame's column under the chrome row.</summary>
    public static Element ZuneBand() => Embed.Comp(static () => new ZuneBandView()) with { Key = "zune-band" };

    internal sealed class ZuneBandView : Component
    {
        readonly List<SidebarPin> _tiles = new(ZuneNavRules.MaxPins);

        public override Element Render()
        {
            // Every hook runs before anything that depends on the style: a Zune switch must not change the hook count between renders.
            var overlay = UseContext(Overlay.Service);
            var vp = UseContextSignal(Viewport.Size);
            bool pinsShown = UseComputed(() => ZuneNavRules.ShowsPins(ZunePins.Value, vp.Value.Width)).Value;
            var style = NavStyle.Value;
            bool zune = style == ShellNavStyle.Zune;
            float gutter = Shell.Ui.PageGutter.Value;
            float cardX = Shell.FrameRules.ContentCardX(Sidebar.PresentedWidth.Value);

            Element[] rows = [];
            if (zune)
            {
                string name = Shell.NameOf(Shell.Current.Value);
                rows = [TopRow(name, pinsShown), SubRowFor(name)];
            }

            // Band HEIGHT follows the live style (the frame commit) and reveals on the card's own tween.
            return new BoxEl
            {
                Key = "zune:band", Direction = 1, Shrink = 0f, ClipToBounds = true,
                Height = ZuneNavRules.BandHeight(style), Animate = Shell.ZuneBandAnim,
                Padding = new Edges4(Shell.FrameRules.ZuneBandInset(cardX, gutter), 0f, Spacing.L, 0f), Children = rows,
            }.WithContextMenu(overlay, () => ZuneMenu(overlay));
        }

        /// <summary>Row 1: the top pivots, and the pin tiles beside them when the setting is on and the viewport is wide enough.</summary>
        Element TopRow(string name, bool pinsShown)
        {
            string? top = ZuneNavRules.TopOf(name);
            // The band re-renders when the pin list changes and when the binder resolves the entries that give tiles their covers.
            _ = (Binder?.Entries ?? Entries).Version.Value + PinsVersion.Value;

            var pivots = new Element[ZuneNavRules.Top.Length];
            for (int i = 0; i < pivots.Length; i++)
            {
                string key = ZuneNavRules.Top[i];
                pivots[i] = PivotItem(key, Title(key), key == top, sub: false, () => Shell.GoTo(Shell.Parse(ZuneNavRules.LandingOf(key))));
            }
            Element strip = ScrollView(new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = ZuneNavRules.PivotGap, MinWidth = 0f, Children = pivots,
            }, horizontal: true) with
            {
                Grow = 1f, Shrink = 1f, MinWidth = 0f, Height = ZuneNavRules.PivotRowHeight, AutoEdgeFade = true,
                SuppressScrollBar = true, ScrollKey = "zune.pivots",
            };

            var row = new List<Element>(2) { strip };
            if (pinsShown)
            {
                ZuneNavRules.PinTiles(Pins.Items, _tiles);
                if (_tiles.Count > 0)
                {
                    var items = new List<Element>(_tiles.Count + 1)
                    {
                        Design.Type.MicroMeta(Loc.Get("sidebar.zune.pinned")) with { Color = Tok.TextTertiary, Shrink = 0f },
                    };
                    for (int i = 0; i < _tiles.Count; i++) items.Add(PinColumn(_tiles[i]));
                    row.Add(new BoxEl
                    {
                        Direction = 0, AlignItems = FlexAlign.Center, Gap = ZuneNavRules.PinGap, Shrink = 0f, Children = [.. items],
                    });
                }
            }

            return new BoxEl
            {
                Key = "zune:top", Direction = 0, Height = ZuneNavRules.PivotRowHeight, AlignItems = FlexAlign.Center, Gap = Spacing.M, Shrink = 0f,
                Children = [.. row], Enter = PageHead.FadeIn, Exit = PageHead.FadeOut, Transition = s_viewsFade,
            };
        }

        /// <summary>Row 2: ALWAYS a <see cref="ZuneNavRules.SubRowHeight"/> box, whatever the route carries, so a navigation or a
        /// page publishing its views never moves the content card. Its content is keyed by kind and route, so pivoting
        /// cross-fades the words while the row's height never changes. A ZStack, so an outgoing and an incoming content share
        /// the row rather than sit side by side.</summary>
        static Element SubRowFor(string name)
        {
            var kind = ZuneNavRules.SubRowOf(name);
            Element? content = kind switch
            {
                ZuneSubRow.Library => LibraryRow(name),
                ZuneSubRow.PageViews => ViewsRow(name),
                _ => null,
            };
            return new BoxEl
            {
                Key = "zune:sub", Direction = 0, ZStack = true, Height = ZuneNavRules.SubRowHeight, Shrink = 0f, MinWidth = 0f,
                Enter = PageHead.FadeIn, Exit = PageHead.FadeOut, Transition = s_viewsFade,
                Children = content is null
                    ? [new BoxEl { Key = "zune:sub:none", Grow = 1f, Height = ZuneNavRules.SubRowHeight, HitTestVisible = false }]
                    : [content],
            };
        }

        /// <summary>Library's sub-pivots. Their words are the same on every Library page, so the content key carries no route:
        /// pivoting between Library pages changes ink and weight in place and never cross-fades the words.</summary>
        static Element LibraryRow(string name)
        {
            var pages = ZuneNavRules.LibraryPages;
            var subs = new Element[pages.Length];
            for (int i = 0; i < pages.Length; i++)
            {
                string page = pages[i];
                subs[i] = PivotItem(page, Shell.Dest(Shell.Parse(page)).Title.ToLower(CultureInfo.CurrentCulture), page == name, sub: true,
                    () => Shell.GoTo(Shell.Parse(ZuneNavRules.LandingOf(page))));
            }
            return ScrollView(new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = ZuneNavRules.SubPivotGap, MinWidth = 0f, Children = subs,
            }, horizontal: true) with
            {
                Key = "zune:sub:library", Grow = 1f, Shrink = 1f, MinWidth = 0f, Height = ZuneNavRules.SubRowHeight, AutoEdgeFade = true,
                SuppressScrollBar = true, ScrollKey = "zune.sub", Enter = PageHead.FadeIn, Exit = PageHead.FadeOut, Transition = s_viewsFade,
            };
        }

        /// <summary>A page's own views (Home's facet words) and its trailing control (Following), as the page published them
        /// under its route name. Until the page publishes (or the style is PRESENTED), the content is empty in the same
        /// reserved row; the words then fade in place. Its own component, so a facet change re-renders row 2's words, not the band.</summary>
        static Element ViewsRow(string name) => Embed.Comp(() => new ViewsRowView(name)) with { Key = "zune:views-comp:" + name };

        sealed class ViewsRowView(string name) : Component
        {
            public override Element Render() => ViewsContent(name);
        }

        static Element ViewsContent(string name)
        {
            // Drawn only once the style is PRESENTED (the page's own words are still on screen until then), so the words show in
            // exactly one place per commit and land in the quiet hoist commit with Home's lead reflow.
            var pub = Shell.Ui.PresentedNavStyle.Value == ShellNavStyle.Zune ? Shell.PageViews.For(name) : null;
            var kids = new List<Element>(2);
            if (pub is not null && pub.Labels.Count > 0)
            {
                var words = new Element[pub.Labels.Count];
                int selected = pub.Selected.Value;
                for (int i = 0; i < words.Length; i++)
                {
                    int at = i;
                    // A click resolves the LATEST delegate (a re-publish that only changes it does not bump the store's Version).
                    words[i] = PivotItem("view:" + i, pub.Labels[i].ToLower(CultureInfo.CurrentCulture), i == selected, sub: true,
                        () => Shell.PageViews.Peek(name)?.OnSelect(at));
                }
                kids.Add(ScrollView(new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = ZuneNavRules.SubPivotGap, MinWidth = 0f, Children = words,
                }, horizontal: true) with
                {
                    Height = ZuneNavRules.SubRowHeight, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                    AutoEdgeFade = true, SuppressScrollBar = true, ScrollKey = "zune.views",
                });
                if (pub.Trailing?.Invoke() is { } trailing)
                    kids.Add(new BoxEl
                    {
                        Key = "zune:view:trailing", Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center, Children = [trailing],
                    });
            }
            return new BoxEl
            {
                Key = "zune:sub:views:" + name, Direction = 0, Grow = 1f, Height = ZuneNavRules.SubRowHeight, AlignItems = FlexAlign.Center,
                MinWidth = 0f, Gap = Spacing.M, Children = [.. kids],
                Enter = PageHead.FadeIn, Exit = PageHead.FadeOut, Transition = s_viewsFade,
            };
        }

        static readonly MotionTokenDef s_viewsFade =
            MotionTokenDef.Eased(Design.Motion.Faster, Easing.FluentStandard, ReducedMotionPolicy.KeepFade);

        /// <summary>A pivot: its text is the target, so the item has no plate and no scale. The word carries the state in ink
        /// alone (secondary at rest, primary on hover and when selected, tertiary on press) and in weight (the selected one
        /// reads heavier, through the role). The box exists for the click, the cursor and the focus ring.</summary>
        static Element PivotItem(string key, string label, bool on, bool sub, Action go) => new BoxEl
        {
            Key = "zune:" + key, Role = AutomationRole.Tab, Focusable = true, Cursor = CursorId.Hand, Shrink = 0f,
            AlignItems = FlexAlign.Center, Corners = Radii.ControlAll, FocusVisualMargin = Design.FocusInsetRow, OnClick = go,
            Children =
            [
                (sub ? Design.Type.ZuneSubPivot(label, on) : Design.Type.ZunePivot(label, on)) with
                {
                    Color = on ? Tok.TextPrimary : Tok.TextSecondary, HoverColor = Tok.TextPrimary, PressedColor = Tok.TextTertiary,
                    BrushTransitionMs = Design.Motion.Faster,
                },
            ],
        };

        static string Title(string key) => (key == ZuneNavRules.LibraryPivot ? Loc.Get("sidebar.library.title")
            : Shell.Dest(Shell.Parse(key)).Title).ToLower(CultureInfo.CurrentCulture);

        /// <summary>A pin: its 32-DIP tile over the now-playing dot. The dot's box is ALWAYS laid out (4 DIP), so a pin starting
        /// to play only changes its ink. 32 + 2 + 4 = 38, inside the 52-DIP pivot row.</summary>
        static Element PinColumn(SidebarPin pin) => new BoxEl
        {
            // The top margin balances the dot hanging below the tile, so the TILE (not the column) sits on the row's centre line,
            // level with the caption and the pivot words.
            Direction = 1, AlignItems = FlexAlign.Center, Gap = ZuneNavRules.PinDotGap, Shrink = 0f,
            Margin = new Edges4(0f, ZuneNavRules.PinDotGap + ZuneNavRules.PinDot, 0f, 0f),
            Children =
            [
                PinTile(pin),
                Embed.Comp(() => new PinDotView(pin.Uri)) with { Key = "zune:dot:" + pin.Id },
            ],
        };

        /// <summary>A 32-DIP pin tile: the route's glyph for an app-route pin, the entry's cover once resolved, else the kind glyph.</summary>
        static Element PinTile(SidebarPin pin)
        {
            string route = pin.RouteKey;
            Element art = pin.Kind == SidebarEntryKind.AppRoute
                ? Icon(Shell.Dest(Shell.Parse(pin.Id)).Glyph, 16f, Tok.TextSecondary)
                : ResolvedPin(pin.Id) is { } entry ? Cover.ForEntry(in entry, ZuneNavRules.PinTile)
                : Icon(Icons.Library, 16f, Tok.TextSecondary);
            var tile = new BoxEl
            {
                Width = ZuneNavRules.PinTile, Height = ZuneNavRules.PinTile, Shrink = 0f, Corners = Radii.ControlAll,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Role = AutomationRole.Button, Focusable = true,
                Cursor = CursorId.Hand, OnClick = () => Shell.GoTo(Shell.Parse(route)), Children = [art],
            }.Interactive(Interaction.Subtle);
            return ToolTip.Wrap(tile, SidebarMenus.PinName(pin.Id));
        }

        /// <summary>The now-playing dot under a pin. Read COARSE-FIRST like the pane's rows (<c>RefreshPlayState</c>): an idle
        /// app never asks the per-pin relation. Its own component, so a track skip re-renders four-DIP dots, not the band.</summary>
        sealed class PinDotView(string uri) : Component
        {
            public override Element Render()
            {
                var seam = Controls.NowPlaying;
                bool active = seam is not null && seam.HasActiveContext.Value;
                bool lit = ZuneNavRules.PinShowsPlaying(uri, active, active && uri.Length > 0 && seam!.RelatesTo(uri));
                var dot = new BoxEl
                {
                    Width = ZuneNavRules.PinDot, Height = ZuneNavRules.PinDot, Shrink = 0f, Corners = Radii.FullAll,
                    HitTestVisible = false, BrushTransitionMs = Design.Motion.Faster,
                };
                return lit ? dot with { Fill = Tok.AccentDefault } : dot with { Fill = ColorF.Transparent };
            }
        }

        /// <summary>The projection's resolved row for a pin (its cover, its kind), or null until the projection has it.</summary>
        static SidebarLibraryEntry? ResolvedPin(string id)
        {
            var pins = Binder?.CurrentInput.Pins;
            if (pins is null) return null;
            for (int i = 0; i < pins.Count; i++) if (pins[i].Id == id) return pins[i];
            return null;
        }

        /// <summary>The band's menu: the pane menu's Zune rows, on the overlay host the band opened on.</summary>
        static ContextMenuModel? ZuneMenu(IOverlayService overlay)
        {
            SidebarMenus.Overlay = overlay;
            return new ContextMenuModel(SidebarMenus.Map(SidebarMenuModel.Pane(Layout.Peek(), State, Density.Peek(), Editing.Peek(),
                SidebarMenus.LockingNames(), ClassicCovers.Peek(), zune: true, zunePins: ZunePins.Peek())));
        }
    }
}
