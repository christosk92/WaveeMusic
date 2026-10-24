// ── Themes/Classic/Structure/Views/Player/ClassicPlayerBar.Credits.cs — Classic's credits line under the bar's title (leftovers P3) ──
//
// From `Shell.PlayerBar.UI.cs` (`BarArtistsLine`, `BarMetaLink`, `BarArtistOverflowChip`): per-name links (a track's credits) or ONE link
// (an episode's show); compact (marquee off), the first name, ellipsised, plus the "+N" chip when there is more than one. What the line
// states is the core's (`PlayerBarProps.Credits`: the FACE's row, the routes, the chip's menu rows built at open time) and the line
// re-renders only when its stamp moves (`PlayerBarProps.CreditsStamp`: the track's row folded with its credited artists' rows).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

internal static partial class ClassicPlayerBar
{
    /// <summary>The credits line: full names (the marquee's content) or <paramref name="compact"/> (the first name and the "+N" chip).</summary>
    private sealed class Credits(PlayerBarProps props, bool compact) : Component
    {
        readonly PlayerBarProps _props = props;
        readonly bool _compact = compact;

        public override Element Render()
        {
            _ = UseComputed(_props.CreditsStamp).Value;
            // The FACE's row: a held face keeps its credits, so the line never empties and the text block never
            // re-centres from two lines to one and back on a skip.
            var credits = _props.Credits();

            if (credits.Show is { } show)
                return new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f,
                    Children = [MetaLink(show, _compact)],
                };

            // A PARTIAL credit list never renders: the core waits for the whole group, and a playable with no
            // credits renders an EMPTY row that still spends the column's 2-DIP gap (W20).
            var artists = credits.Artists;
            if (artists.Length == 0) return new BoxEl { Direction = 0 };

            if (_compact && artists.Length > 1)
            {
                return new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, Gap = Spacing.XS,
                    Children =
                    [
                        MetaLink(artists[0], true),
                        Embed.Comp(() => new OverflowChip(credits.OverflowRows, artists.Length - 1)) with { Key = credits.OverflowKey },
                    ],
                };
            }

            var kids = new List<Element>(artists.Length * 2);
            for (int i = 0; i < artists.Length; i++)
            {
                var link = artists[i];
                if (link.Text.Length == 0) continue;
                if (kids.Count > 0) kids.Add(new TextEl(ClassicTransportLayout.CreditSeparator) { Size = ClassicTransportLayout.CreditSize, Color = Tok.TextSecondary });
                // A name with no route is INERT, not styled-and-dead.
                kids.Add(MetaLink(link, false));
            }
            return new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, Children = kids.ToArray() };
        }

        static Element MetaLink(PlayerCreditLink link, bool trim)
            => Embed.Comp(() => new MetaLinkView(link.Text, link.Navigate, trim)) with { Key = link.Key + ":" + link.Text };
    }

    /// <summary>A clickable now-playing meta link. It drives its OWN foreground: <c>TextEl.HoverColor</c> follows the
    /// ancestor hover path, which would light every link while the pointer is anywhere on the meta column.</summary>
    private sealed class MetaLinkView(string text, Action? navigate, bool trim) : Component
    {
        readonly string _text = text;
        readonly Action? _navigate = navigate;
        readonly bool _trim = trim;

        public override Element Render()
        {
            var hover = UseSignal(false);
            bool enabled = _navigate is not null;
            return new BoxEl
            {
                Cursor = enabled ? CursorId.Hand : null,
                OnClick = _navigate,
                OnHoverMove = enabled ? _ => { if (!hover.Peek()) hover.Value = true; } : null,
                OnPointerExit = enabled ? () => { if (hover.Peek()) hover.Value = false; } : null,
                ClipToBounds = true, Shrink = 1f, MinWidth = _trim ? 0f : float.NaN,
                Role = enabled ? AutomationRole.Hyperlink : AutomationRole.Text,
                Children =
                [
                    new TextEl(_text)
                    {
                        Size = ClassicTransportLayout.CreditSize, Color = hover.Value ? Tok.TextPrimary : Tok.TextSecondary,
                        MaxLines = _trim ? 1 : 0, Trim = _trim ? TextTrim.CharacterEllipsis : TextTrim.None,
                        MinWidth = _trim ? 0f : float.NaN,
                    },
                ],
            };
        }
    }

    /// <summary>The "+N" credits chip. Opens EVERY credited artist UPWARD from the dock (0.2.9 asked for
    /// BottomEdgeAlignedLeft from a bottom dock and relied on the overlay re-fit; ch 20 W20 asks 0.3 to anchor it up).
    /// Keyed by its artist set at the call site, so the frozen rows builder is the right one.</summary>
    private sealed class OverflowChip(Func<IReadOnlyList<MenuFlyoutItem>> rows, int more) : Component
    {
        readonly Func<IReadOnlyList<MenuFlyoutItem>> _rows = rows;
        readonly int _more = more;

        public override Element Render()
        {
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);
            var overlay = UseContext(Overlay.Service);
            var rows = _rows;

            void Toggle()
            {
                if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
                var items = rows();
                handle.Value = overlay.Open(
                    () => anchor.Value,
                    () => MenuFlyout.Create(items, () => handle.Value?.Close(), size: OverlayFit.MenuSize),
                    FlyoutPlacement.TopEdgeAlignedLeft,
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
                handle.Value.ClosedAction = () => handle.Value = null;
            }

            return new BoxEl
            {
                Shrink = 0f, Padding = new Edges4(Spacing.XXS, 0f, Spacing.XXS, 0f),
                OnRealized = h => anchor.Value = h,
                Role = AutomationRole.Button, Cursor = CursorId.Hand, OnClick = Toggle, BlocksDragArm = true,
                Children = [FluentGpu.Dsl.Ui.Caption("+" + _more) with { Weight = ClassicTransportLayout.CreditChipWeight, Color = Tok.TextTertiary }],
            };
        }
    }
}
