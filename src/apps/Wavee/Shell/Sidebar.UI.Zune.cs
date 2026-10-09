// ── Shell/Sidebar.UI.Zune.cs ───────────────────────────────────────────────────────────────────────────────────────
// The Zune navigation style's band (design NAV 3): the big pivots across the top, Library's sub-pivots, the pin tiles
// beside the pivots, and the band's right-click menu (Layout ▸ · Pins in the title bar · Reset everything)
//
// Role: UI · Spec: sidebar-rework-implementation.md §P11 (NAV 3 UI) · the rules are Sidebar.Zune.cs (ZuneNavRules)

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
    /// <summary>The Zune band: zero height unless the nav style is Zune. Mounted in the frame's column under the chrome row.</summary>
    public static Element ZuneBand() => Embed.Comp(static () => new ZuneBandView()) with { Key = "zune-band" };

    internal sealed class ZuneBandView : Component
    {
        readonly List<SidebarPin> _tiles = new(ZuneNavRules.MaxPins);

        public override Element Render()
        {
            // Every hook runs before the early return: a Zune switch must not change the hook count between renders.
            var overlay = UseContext(Overlay.Service);
            var vp = UseContextSignal(Viewport.Size);
            bool pinsShown = UseComputed(() => ZuneNavRules.ShowsPins(ZunePins.Value, vp.Value.Width)).Value;
            if (NavStyle.Value != ShellNavStyle.Zune) return new BoxEl { Height = 0f, Shrink = 0f };

            string name = Shell.NameOf(Shell.Current.Value);
            string? top = ZuneNavRules.TopOf(name);
            // The band re-renders when the pin list changes and when the binder resolves the entries that give tiles their covers.
            _ = (Binder?.Entries ?? Entries).Version.Value + PinsVersion.Value;

            var pivots = new Element[ZuneNavRules.Top.Length];
            for (int i = 0; i < pivots.Length; i++)
            {
                string key = ZuneNavRules.Top[i];
                pivots[i] = Pivot(key, Title(key), key == top, ZuneNavRules.PivotSize, 300);
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
                    var tiles = new Element[_tiles.Count];
                    for (int i = 0; i < tiles.Length; i++) tiles[i] = PinTile(_tiles[i]);
                    row.Add(new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = ZuneNavRules.PinGap, Shrink = 0f, Children = tiles });
                }
            }

            var children = new List<Element>(3)
            {
                new BoxEl
                {
                    Direction = 0, Height = ZuneNavRules.PivotRowHeight, AlignItems = FlexAlign.Center, Gap = Spacing.M, Shrink = 0f,
                    Children = [.. row],
                },
            };
            if (top == ZuneNavRules.LibraryPivot) children.Add(SubRow(name));

            return new BoxEl
            {
                Direction = 1, Shrink = 0f, Padding = new Edges4(ZuneNavRules.InsetX, 0f, Spacing.L, 0f), Children = [.. children],
            }.WithContextMenu(overlay, () => ZuneMenu(overlay));
        }

        /// <summary>Library's sub-pivots: one row under the top pivots, only while a Library page is on screen.</summary>
        static Element SubRow(string name)
        {
            var pages = ZuneNavRules.LibraryPages;
            var subs = new Element[pages.Length];
            for (int i = 0; i < pages.Length; i++)
            {
                string page = pages[i];
                subs[i] = Pivot(page, Shell.Dest(Shell.Parse(page)).Title.ToLower(CultureInfo.CurrentCulture), page == name,
                    ZuneNavRules.SubPivotSize, 400);
            }
            return ScrollView(new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = ZuneNavRules.SubPivotGap, MinWidth = 0f, Children = subs,
            }, horizontal: true) with
            {
                Grow = 1f, Shrink = 1f, MinWidth = 0f, Height = ZuneNavRules.SubRowHeight, AutoEdgeFade = true,
                SuppressScrollBar = true, ScrollKey = "zune.sub",
            };
        }

        /// <summary>A pivot: its text is the target. The selected one reads heavier and brighter (600 against the rest weight).</summary>
        static Element Pivot(string key, string title, bool on, float size, ushort rest) => new BoxEl
        {
            Key = "zune:" + key, Role = AutomationRole.NavigationItem, Focusable = true, Cursor = CursorId.Hand, Shrink = 0f,
            AlignItems = FlexAlign.Center, OnClick = () => Shell.GoTo(Shell.Parse(ZuneNavRules.LandingOf(key))),
            Children =
            [
                new TextEl(title)
                {
                    Size = size, Weight = on ? (ushort)600 : rest, Color = on ? Tok.TextPrimary : Tok.TextSecondary,
                    HoverColor = Tok.TextPrimary, MaxLines = 1,
                },
            ],
        }.Interactive(Interaction.Subtle);

        static string Title(string key) => (key == ZuneNavRules.LibraryPivot ? Loc.Get("sidebar.library.title")
            : Shell.Dest(Shell.Parse(key)).Title).ToLower(CultureInfo.CurrentCulture);

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
