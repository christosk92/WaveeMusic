// ── Themes/Classic/Structure/Views/Detail/ClassicDetailBand.cs — the context band's pieces (themes plan 6.1, T10c2) ─────────────────────────────────────
//
// The band (the 56-DIP identity row), its title, byline and ONE hairline, and the section pivot are the structure's: the detail frame's hero, the artist page,
// the profile page and the episode reader all build their context band from these. They lived in `Detail` as the seam until the last page moved (T10c2).
// The band's NUMBERS and the scroll-spy arithmetic (`Detail.BandLayout`) stay core: the artist and profile hosts read them.

using System.Globalization;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scroll.Effects;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using BandLayout = Wavee.Detail.BandLayout;

namespace Wavee.Themes.Classic;

/// <summary>The context band's shared pieces.</summary>
internal static class ClassicDetailBand
{
    /// <summary>The brush budget every accent consumer shares: a VALUE under reduced motion (0 is an instant swap).</summary>
    static float AccentTransitionMs => Design.Reduced ? 0f : Design.Motion.Standard;

    /// <summary>The identity row: 56 tall, gutter-padded, UNPAINTED (no fill, no edge — the caller places the one
    /// hairline), still hit-testable so its actions work and it swallows wheel/click meant for the rows behind it.</summary>
    public static Element Band(float width, float gutter, Element identity, Element actions) => new BoxEl
    {
        Direction = 0, Width = width, Height = BandLayout.Height,
        Padding = new Edges4(gutter, 0f, gutter, 0f),
        Gap = BandLayout.ClusterGap, AlignItems = FlexAlign.Center,
        HitTestVisible = true,
        Children =
        [
            identity,
            new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f, Height = 1f, HitTestVisible = false },
            actions,
        ],
    };

    /// <summary>The artist arm's row (0.2.9 ContextBand.Row): title · pivot · actions as cluster children, same geometry as Band.</summary>
    public static BoxEl Band(float width, float gutter, Element[] children) => new BoxEl
    {
        Direction = 0, Width = width, Height = BandLayout.Height, Padding = new Edges4(gutter, 0f, gutter, 0f),
        Gap = BandLayout.ClusterGap, AlignItems = FlexAlign.Center, HitTestVisible = true, Children = children,
    };

    /// <summary>The band title: BodyStrong 14/20/600, primary, one line, ellipsised — never wraps, never drops.</summary>
    public static Element BandTitle(string title) => Ui.BodyStrong(title) with
    {
        Color = Tok.TextPrimary, MinWidth = 0f, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
    };

    /// <summary>The band byline: Caption 12/16, tertiary, one line — context for the title, not a competing label.</summary>
    public static Element BandByline(string byline) => Ui.Caption(byline) with
    {
        Color = Tok.TextTertiary, MinWidth = 0f, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
    };

    /// <summary>The band's ONE lower edge, as a real laid-out row child — placed on the LAST stuck stratum only.</summary>
    public static Element BandHairline() => new BoxEl
    {
        Height = BandLayout.HairlineHeight, AlignSelf = FlexAlign.Stretch,
        Fill = Tok.StrokeDividerDefault, HitTestVisible = false,
    };

    /// <summary>The band's middle cluster (artist arm): the page's sections as text links, a 2-DIP accent underline under
    /// the active one — ALWAYS mounted, switching colour over 167 ms. <paramref name="active"/> is the caller's scroll-spy
    /// answer; the section set is re-pushed (it grows after mount), the signal instance freezes at mount.</summary>
    public static Element Pivot(IReadOnlyList<(string Label, Action OnClick)> sections, IReadSignal<int> active,
                                Func<ColorF> accent)
        => Embed.Comp(new PivotProps(sections, active, accent), static () => new PivotHost());

    sealed record PivotProps(IReadOnlyList<(string Label, Action OnClick)> Sections, IReadSignal<int> Active, Func<ColorF> Accent)
    {
        public bool Equals(PivotProps? o)
        {
            if (ReferenceEquals(this, o)) return true;
            if (o is null || !ReferenceEquals(Active, o.Active) || Sections.Count != o.Sections.Count) return false;
            for (int i = 0; i < Sections.Count; i++)
                if (!string.Equals(Sections[i].Label, o.Sections[i].Label, StringComparison.Ordinal)) return false;
            return true;
        }

        public override int GetHashCode() => HashCode.Combine(Sections.Count, Active);
    }

    sealed class PivotHost : Component, IPropsHost
    {
        /// <summary>A pivot is a glance-and-aim affordance; past this many words it is a menu.</summary>
        const int MaxItems = 16;

        PivotProps? _latest;
        readonly Signal<PivotProps?> _props = new(null);
        NodeHandle[] _tabNodes = [];
        Action<NodeHandle>[] _tabRealized = [];
        Action[] _tabClicks = [];
        Func<ColorF>[] _tabFills = [];
        NodeHandle _viewport;
        bool _seeded;
        int _revealIndex = -1;
        readonly Action<NodeHandle> _captureViewport;
        readonly Action _reveal;

        public PivotHost()
        {
            _captureViewport = h => _viewport = h;
            _reveal = RevealActive;
        }

        public void ApplyProps(object props)
        {
            _latest = (PivotProps)props;
            _props.Value = _latest;
        }

        public override Element Render()
        {
            _ = _props.Value;
            var p = _latest!;
            int shown = Math.Min(p.Sections.Count, MaxItems);
            EnsureSlots(shown);
            int active = p.Active.Value;                           // re-renders only on a boundary crossing
            int current = BandLayout.PivotCurrent(active, shown);
            _revealIndex = current;
            UseLayoutEffect(_reveal, DepKey.From(current, shown));
            if (shown == 0) return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };

            var kids = new Element[shown];
            for (int i = 0; i < shown; i++)
                kids[i] = PivotLink(p.Sections[i].Label, i == current, _tabFills[i], _tabClicks[i], _tabRealized[i]);
            return new ScrollEl
            {
                Horizontal = true, ContentSized = true,
                Grow = 1f, Basis = 0f, MinWidth = 0f, Height = BandLayout.Height,
                SuppressScrollBar = true, AutoEdgeFade = true, EdgeCues = ScrollEdgeCues.None,
                OnRealized = _captureViewport,
                Content = new BoxEl
                {
                    Direction = 0, Gap = BandLayout.PivotGap, Shrink = 0f, AlignItems = FlexAlign.Center,
                    Children = kids,
                },
            };
        }

        void EnsureSlots(int count)
        {
            if (_tabNodes.Length == count) return;
            int old = _tabNodes.Length;
            Array.Resize(ref _tabNodes, count);
            Array.Resize(ref _tabRealized, count);
            Array.Resize(ref _tabClicks, count);
            Array.Resize(ref _tabFills, count);
            for (int i = old; i < count; i++)
            {
                int index = i;
                _tabRealized[i] = h => _tabNodes[index] = h;
                _tabClicks[i] = () =>
                {
                    if (_latest is { } live && (uint)index < (uint)live.Sections.Count) live.Sections[index].OnClick();
                };
                // The underline's bound fill: the accent while this link is active, else transparent. Reads the active
                // signal and the accent inside the bind, so both changes cross-fade without a re-render.
                _tabFills[i] = () =>
                {
                    if (_latest is not { } live) return ColorF.Transparent;
                    int shown = Math.Min(live.Sections.Count, MaxItems);
                    int current = BandLayout.PivotCurrent(live.Active.Value, shown);
                    return index == current ? live.Accent() : ColorF.Transparent;
                };
            }
            _seeded = false;
        }

        void RevealActive()
        {
            int i = _revealIndex;
            if ((uint)i >= (uint)_tabNodes.Length || _tabNodes[i].IsNull || _viewport.IsNull || Context.Scene is not { } scene) return;
            scene.BringIntoView(_viewport, _tabNodes[i], float.NaN, _seeded && !Design.Reduced ? ScrollMove.Glide : ScrollMove.Immediate, Spacing.S);
            _seeded = true;
        }

        /// <summary>One link: the hover boundary is the LINK's own box, so the word under the pointer lights alone. A ZSTACK of
        /// <see cref="BandLayout.ItemHeight"/>: the word centred on the band's 20-DIP line (the same line as the title and
        /// the actions), the active mark an OVERLAY at <see cref="BandLayout.UnderlineY"/>.</summary>
        static Element PivotLink(string label, bool isActive, Func<ColorF> fill, Action go, Action<NodeHandle> realized) => new BoxEl
        {
            ZStack = true, Shrink = 0f,
            Height = BandLayout.ItemHeight,
            Padding = new Edges4(BandLayout.PivotPadX, 0f, BandLayout.PivotPadX, 0f),
            Corners = Radii.ControlAll,
            Role = AutomationRole.Tab, Focusable = true, Cursor = CursorId.Hand, OnClick = go, OnRealized = realized,
            Children =
            [
                new BoxEl
                {
                    Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestVisible = false,
                    Children =
                    [
                        new TextEl(label)
                        {
                            Size = Controls.TextActionSize, LineHeight = BandLayout.TextLine, Weight = Controls.TextActionWeight,
                            Color = isActive ? Tok.TextPrimary : Tok.TextSecondary,
                            HoverColor = Tok.TextPrimary,
                            MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
                        },
                    ],
                },
                new BoxEl
                {
                    Height = BandLayout.UnderlineHeight, AlignSelf = FlexAlign.Stretch,
                    Margin = new Edges4(0f, BandLayout.UnderlineY, 0f, 0f),
                    Fill = fill,
                    BrushTransitionMs = AccentTransitionMs,
                    HitTestVisible = false,
                },
            ],
        };
    }
}
