// ── Entities/Detail.UI.Hero.cs ─────────────────────────────────────────────────────────────────────────────────────
// THE VERTICAL HERO SYSTEM, CORE HALF (ch 03 §0.3, §0.6-§0.8, §0.14-§0.18; W6-W9, W12-W14, W27-W28): VerticalSpec + HeroParts (the
// table's view of the vertical arm), the presence flags every arm shares (FlagsOf), HeroBandHeight (the known title's plan, else the
// pessimistic null-title one), `Hero` (the table's item 0: a plain box around the `Detail.Hero` view slot) and the band helpers
// Band / BandTitle / BandByline / BandHairline / Pivot the artist, profile and episode pages also consume.
//
// T9a (themes plan 6.1): the expanded presentation, the 56-DIP context band with its scroll binds and input handoff, the hero's
// actions and attribution and the loading band (the skeleton) are CLASSIC'S (`Themes\Classic\Structure\Views\Detail\ClassicDetailHero.cs`).
// The loading band reaches the table as a style (`StyleKeys.DetailHero`) because the skeleton deriver cannot run a view slot.
//
// Role: UI (the arithmetic and the band helpers are core; the picture is the structure's)
// Owner: M
// Wave: 4.5
// Spec: ch 03 §9
//
// ── WHO CALLS WHAT ───────────────────────────────────────────────────────────────────────────────────────────────────
//
// The TABLE (Track.Table.cs) owns the vertical list: it calls Hero(spec, parts) for item 0 (pinning it with Sticky(0) and
// collapsing its presented height with Collapse) and the structure's hero skeleton for its shimmer, and pins its chrome with
// Sticky(56, engaged:). The hero never scrolls anything: the expanded presentation binds TransY/Opacity to the NEAREST
// scroller, the band reveals over the last 44 DIP of the collapse, and the chrome's engaged edge the table publishes
// (CompactInteractive) moves hit-testing between the two.
//
// ── THE THREE NUMBERS THAT MUST AGREE (D49) ──────────────────────────────────────────────────────────────────────────
//
// The skeleton's reserved band, the pre-measure collapse height and the loaded hero are all derived from ONE bucketed
// width (BucketW, once, at the top), ONE presence-flag set (FlagsOf) and ONE title plan (VerticalLayout.TitleTypeFor over
// the identity's title — pure, so all three agree whenever the title is known): every block the hero adds has its flag,
// and the flow decision is read off the raw column width in all three (424 enter / 400 leave). These rules stay here, core
// and unit-tested; the structure's pictures call them.
//
// NAME NOTE: `Text`, `Skeleton` and `Config` are nested Detail classes here — text runs are `new TextEl`/`Ui.*`.

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
using static FluentGpu.Dsl.Ui;
using Contracts = Wavee.Theming.Contracts;
using ViewSlots = FluentGpu.Theming.Slots;

namespace Wavee;

public static partial class Detail
{
    // ══ 1. THE TABLE'S VIEW OF THE VERTICAL ARM ══════════════════════════════════════════════════════════════════════

    /// <summary>The vertical arm, as the TABLE sees it. Equality is DATA only (identity, config, verb/slot presence); the
    /// accent is a late-bound thunk (reading it subscribes the caller to the frame's accent signal).</summary>
    public sealed record VerticalSpec(Identity Identity, Config Config, FrameActions Actions, FrameSlots Slots, Func<ColorF> Accent)
    {
        /// <summary>The frame's play-all trampoline (the table's visible-order cell, else the context). Set by the frame
        /// only; not part of the table contract and not compared. Null ⇒ the hero plays the context.</summary>
        internal Action? PlayAll { get; init; }

        /// <summary>── INSIGHTS SHEET (additive) ── The frame's sheet toggle, when this arm hosts the facts bento in a
        /// sheet (<see cref="InsightsSheet.ShowsToggle"/>). Set by the frame only — a stable per-host object, so like
        /// <see cref="PlayAll"/> it is not compared: its OPEN state travels by signal, never by a re-push. Null ⇒ this
        /// page has no facts and the toolbar row carries no toggle.</summary>
        internal InsightsToggle? Insights { get; init; }

        public bool Equals(VerticalSpec? o)
        {
            if (ReferenceEquals(this, o)) return true;
            return o is not null && Identity.Equals(o.Identity) && Config == o.Config
                && Actions.Equals(o.Actions) && Slots.Equals(o.Slots)
                // ── INSIGHTS SHEET (additive) ── PRESENCE, like every slot: the toggle appears when the page's facts
                //    arrive and goes when they do not, and the hero must re-render for exactly that (its open STATE is
                //    a signal the button binds, so opening the sheet still costs no render here).
                && (Insights is null) == (o.Insights is null);
        }

        public override int GetHashCode() => HashCode.Combine(Identity, Config, Actions, Slots);
    }

    /// <summary>The pieces the table builds for the vertical arm, handed to <see cref="Hero"/>.</summary>
    public readonly record struct HeroParts(
        Element? Toolbar,                         // the command bar host under the identity (null ⇒ a reserved blank row)
        Element BandActions,                      // Find · Filter · Play (plateless words)
        Element? SelectionBar,                    // the band's selection arm
        IReadSignal<bool> SelectionVisible,
        IReadSignal<bool> CompactInteractive,     // the onStuck input handoff (ch 03 §0.18)
        Element? SearchField,                     // takes the band title's slot while expanded
        IReadSignal<bool> SearchExpanded,
        float ColumnWidth,                        // measured (or seeded) list width — bucketed ONCE inside (BucketW)
        float CompactLeft,                        // gutter: RowMetrics.PadXFor(tier) (+48 chips, +36 lens)
        float HeroHeight,                         // the band height the table's Collapse bind uses (measured, else HeroBandHeight)
        Action<float>? OnHeroMeasured);

    // ══ 2. THE PRESENCE FLAGS — one set for the hero, its skeleton and its reserved height ═══════════════════════════

    internal readonly record struct HeroFlags(bool Eyebrow, bool Attribution, bool Meta, bool Pulse, bool Chart, bool Description);

    /// <summary>The hero's emit predicates for THIS spec — exactly what <see cref="HeroHost"/> branches on, so the reserved
    /// band holds the blocks the hero will compose (an album's eyebrow, a playlist's owner row, a chart caption, W28).</summary>
    internal static HeroFlags FlagsOf(VerticalSpec spec)
    {
        var id = spec.Identity;
        var slots = spec.Slots;
        return new HeroFlags(
            Eyebrow: id.Eyebrow.Length > 0,
            Attribution: (slots.Attribution is not null && id.Kind != DetailKind.Album)
                || (id.Collaborators is { Count: > 0 } c && Text.ShowCollaborators(c.Count, id.Collaborative))
                || id.OwnerName is { Length: > 0 }
                || id.Artists is { Count: > 0 },
            Meta: id.MetaLoading || id.Meta is { Length: > 0 },
            Pulse: id.DaylistExpiresAtMs > 0 && slots.Pulse is not null,
            Chart: id.ChartNewEntries > 0,
            Description: (id.EditableMetadata && slots.Description is not null) || id.DescriptionHtml is { Length: > 0 });
    }

    /// <summary>The band height the skeleton reserves and the loaded hero's collapse binds assume before the first
    /// measure, at the bucketed width: the KNOWN title's plan — the very plan the hero draws — else the PESSIMISTIC
    /// null-title plan (a one-line title at the fluid cap).</summary>
    public static float HeroBandHeight(VerticalSpec spec, float columnWidth)
    {
        float w = columnWidth > 0f ? columnWidth : VerticalLayout.FallbackW;
        bool rowFlow = VerticalLayout.RowFlow(w);
        float bw = VerticalLayout.BucketW(w);
        var f = FlagsOf(spec);
        return VerticalLayout.HeroBandHeight(bw, rowFlow, f.Eyebrow, f.Attribution, f.Meta, f.Description,
                                             pulse: f.Pulse, chart: f.Chart, title: spec.Identity.Title);
    }

    // ══ 3. THE HERO ══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The vertical arm's item 0: the expanded presentation (artwork · eyebrow · title · rule · attribution ·
    /// meta · pulse · chart · actions · description, then the toolbar) and the 56-DIP context band, scroll-bound. The picture
    /// is the structure's (<c>Detail.Hero</c>: Classic's <c>ClassicDetailFrame.HeroTemplate</c>); this is the plain box the
    /// TABLE pins with <c>Sticky</c> and collapses with <c>Collapse</c> — kept a box, not a slot, so those binds land on the
    /// same node they always did — around the slot. The loading band is a STYLE the table reads (<c>StyleKeys.DetailHero</c>:
    /// the skeleton deriver cannot run a slot).</summary>
    public static Element Hero(VerticalSpec spec, in HeroParts parts)
        => new BoxEl
        {
            Key = "vhero:root", Direction = 1,
            Children = [ViewSlots.View(Contracts.ViewKeys.DetailHero, new Contracts.DetailHeroProps(spec, parts))],
        };


    // ══ 5. THE BAND HELPERS (the detail band and the artist band are ONE band) ═══════════════════════════════════════

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
