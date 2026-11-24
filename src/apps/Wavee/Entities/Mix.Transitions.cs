// ── Entities/Mix.Transitions.cs — #48 Mix, the Transitions list (plan §3.10 "The Transitions view") ─────────────────────
//
// Every pair of a mixed playlist as one compact row: the two titles, a tiny overlap picture (the outgoing tail and the incoming
// head as two bars that overlap by the transition's share; apart for No mix), the style and length, and its state (Auto · Edited ·
// This PC · No mix). It opens in the list bar's eased slot under the bar (the one Tune uses: a playlist the user can mix has no
// Tune), from the Mix menu or a seam's click, and shuts the same way: the rows below slide, nothing jumps. Virtualized
// (ItemsView.CreateBound over a fixed row extent), so a long playlist costs only the rows on screen. Clicking a row (or opening
// from a seam) shows that pair's seam card under the list (Entities/Mix.Card.cs, phase 7).

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;

namespace Wavee;

/// <summary>The Transitions list's state on one playlist page: open or shut, and the pair it opened on.</summary>
internal sealed class MixTransitionsSession
{
    readonly Signal<bool> _open = new(false);
    Playlist _p;
    int _focus = -1;

    /// <summary>The list while open, else null. A tracked read (the slot follows it).</summary>
    public readonly Func<Element?> Strip;
    /// <summary>Open on a pair (a seam's click; −1 = no pair).</summary>
    public readonly Action<int> Open;
    public readonly Action Toggle;
    public bool IsOpen => _open.Peek();

    public MixTransitionsSession()
    {
        Strip = () => _open.Value && _p.IsValid ? Embed.Comp(() => new MixTransitionsPanel(_p, _focus, () => _open.Value = false)) with { Key = "mix:transitions" } : null;
        Open = i => { _focus = i; _open.Value = true; };
        Toggle = () => _open.Value = !_open.Peek();
    }

    /// <summary>The page shows <paramref name="p"/> now: another playlist starts shut. Called from an effect, never a render.</summary>
    public void Bind(Playlist p)
    {
        if (p.Slot == _p.Slot) return;
        _p = p;
        _focus = -1;
        _open.Value = false;
    }
}

internal sealed class MixTransitionsPanel(Playlist playlist, int focus, Action close) : Component
{
    readonly Playlist _p = playlist;
    readonly int _focus = focus;
    readonly Action _close = close;
    Func<RowScope, Element>? _row;
    Signal<int>? _selected;
    Action<int>? _select;

    /// <summary>How many rows the panel shows before it scrolls.</summary>
    const int VisibleRows = 6;

    public override Element Render()
    {
        int count = UseComputed(() =>
        {
            _ = Entities.Current.Edges.PlaylistTracks.Changed.Value;
            return Math.Max(0, Entities.Current.Edges.PlaylistTracks.Targets(_p.Slot).Length - 1);
        }).Value;
        float rowH = Ui.Body("").LineHeight + Spacing.L;
        // The pair whose seam card is open (−1: none). A seam's click opens the panel on its pair, card included.
        var selected = UseSignal(_focus);
        _selected = selected;
        _select ??= i => _selected!.Value = _selected.Peek() == i ? -1 : i;
        _row ??= scope => Embed.Comp(() => new MixTransitionRow(_p, scope, _selected!, _select));
        int open = selected.Value;
        // The card is THE disclosure motion (Design.Reveal.Drawer): it mounts to open and unmounts to close, its layout lands once and the
        // presented height springs, so the rows below ride it instead of taking one long first step. The host stays while the card moves
        // between pairs (the inner card is keyed by its pair, so a new pair starts a fresh draft and the host only resizes).
        Element? card = open >= 0 && open < count
            ? new BoxEl
            {
                Key = "mix:card:host", Direction = 1, MinWidth = 0f, ClipToBounds = true, Layout = Design.Reveal.Drawer,
                Children =
                [
                    Embed.Comp(() => new MixSeamCard(_p.Slot, open, count, i => { if (i >= 0 && i < count) _selected!.Value = i; }, () => _selected!.Value = -1))
                        with { Key = "mix:card:" + open.ToString(CultureInfo.InvariantCulture) },
                ],
            }
            : null;
        var header = new BoxEl
        {
            Key = "mix:transitions:head", Direction = 0, AlignItems = FlexAlign.Center, Padding = new Edges4(0f, 0f, 0f, Spacing.XS),
            Children =
            [
                Ui.BodyStrong(Loc.Get(Strings.Mix.Transitions)) with { Grow = 1f, Shrink = 1f, MinWidth = 0f, MaxLines = 1 },
                Button.Subtle(Loc.Get(Strings.Mix.Close), _close) with { Key = "mix:transitions:close" },
            ],
        };
        var list = new BoxEl
        {
            Key = "mix:transitions:list", Direction = 1, Height = rowH * Math.Clamp(count, 1, VisibleRows), MinWidth = 0f, ClipToBounds = true,
            Children = [ItemsView.CreateBound(count, _row, RepeatLayout.Stack(rowH))],
        };
        return new BoxEl
        {
            Key = "mix:transitions:panel", Direction = 1, MinWidth = 0f, Padding = new Edges4(Spacing.M, Spacing.S, Spacing.M, Spacing.S),
            Fill = Tok.FillCardDefault, Corners = Radii.ControlAll, BorderColor = Tok.StrokeDividerDefault, BorderWidth = 1f,
            Children = card is null ? [header, list] : [header, list, card],
        };
    }
}

/// <summary>One pair (playlist row i → i + 1). Re-renders when the slot is recycled onto another index or the mix data moves.</summary>
internal sealed class MixTransitionRow(Playlist playlist, RowScope scope, Signal<int> selected, Action<int> select) : Component
{
    readonly Playlist _p = playlist;
    readonly RowScope _scope = scope;
    readonly Signal<int> _selected = selected;
    readonly Action<int> _select = select;

    readonly record struct Face(string From, string To, string Style, string State, float Overlap, bool NoMix);

    public override Element Render()
    {
        var face = UseComputed(() =>
        {
            int i = _scope.Index.Value;
            _ = Entities.Current.Playlists.Changed.Value;
            _ = Entities.Current.Edges.PlaylistTracks.Changed.Value;
            _ = Entities.Current.Edges.TrackBeats.Changed.Value;
            var edges = Entities.Current.Edges.PlaylistTracks;
            var targets = edges.Targets(_p.Slot);
            if (i < 0 || i + 1 >= targets.Length) return default(Face);
            string from = TitleAt(targets[i]), to = TitleAt(targets[i + 1]);
            var t = Playback.Mix.PlanListPair(_p.Slot, i, i + 1);
            if (!t.Mix) return new Face(from, to, Loc.Get(Strings.Mix.NoMix), t.WhyNot == MixWhyNot.PairOptOut ? Loc.Get(Strings.Mix.StateThisPc) : "", 0f, true);
            var payload = edges.Payload(_p.Slot);
            ulong a = MixRecipe.Hex64(Entities.Strings.Resolve(payload[i].ItemId)), b = MixRecipe.Hex64(Entities.Strings.Resolve(payload[i + 1].ItemId));
            string state = MixOverrides.TryGet(_p.Id.Text, a, b, out _) ? Loc.Get(Strings.Mix.StateThisPc)
                         : t.Source == MixSource.Saved ? Loc.Get(Strings.Mix.StateEdited) : Loc.Get(Strings.Mix.Auto);
            string style = (t.Preset == (byte)MixPreset.Rise ? "Rise" : "Fade") + " · "
                         + (t.OutDurationMs / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + " s";
            if (t.Preset is not ((byte)MixPreset.Fade or (byte)MixPreset.Rise)) style = Loc.Get(Strings.Mix.EditedInSpotify);
            // The picture's overlap share: the window against a 16 s scale (the longest a transition may be).
            return new Face(from, to, style, state, Math.Clamp(t.OutDurationMs / 16_000f, 0.05f, 1f), false);
        }).Value;

        float bar = Spacing.XXL * 2f, barH = Spacing.XS;
        float overlapW = face.NoMix ? -Spacing.XS : bar * 0.5f * face.Overlap;
        Element picture = new BoxEl
        {
            // The outgoing tail (left half) and the incoming head (right half) on one axis, overlapping by the transition's share.
            Key = "mix:t:pic", Direction = 1, Width = bar, Shrink = 0f, Gap = Spacing.XXS, Justify = FlexJustify.Center,
            Children =
            [
                new BoxEl { Width = bar * 0.5f + MathF.Max(0f, overlapW) * 0.5f, Height = barH, Fill = Tok.TextSecondary, Corners = Radii.ControlAll },
                new BoxEl
                {
                    Margin = new Edges4(bar * 0.5f - overlapW * 0.5f, 0f, 0f, 0f), Width = bar * 0.5f + MathF.Max(0f, overlapW) * 0.5f, Height = barH,
                    Fill = face.NoMix ? Tok.TextSecondary : Tok.TextPrimary, Corners = Radii.ControlAll,
                },
            ],
        };
        int index = _scope.Index.Value;
        bool focused = index >= 0 && _selected.Value == index;
        return new BoxEl
        {
            Key = "mix:t:row", Direction = 0, Grow = 1f, MinWidth = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.M,
            Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f), Corners = Radii.ControlAll,
            Fill = focused ? Tok.FillSubtleSecondary : ColorF.Transparent, HoverFill = Tok.FillSubtleSecondary,
            Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true, OnClick = () => _select(_scope.Index.Peek()),
            Children =
            [
                Ui.Body(face.From) with { Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Wrap = TextWrap.NoWrap },
                Ui.Caption("→"),
                Ui.Body(face.To) with { Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Wrap = TextWrap.NoWrap },
                picture,
                new BoxEl { Width = Spacing.XXL * 3f, Shrink = 0f, Children = [Ui.Caption(face.Style) with { MaxLines = 1 }] },
                new BoxEl { Width = Spacing.XXL * 2f, Shrink = 0f, Children = [Ui.Caption(face.State) with { MaxLines = 1 }] },
            ],
        };
    }

    static string TitleAt(int trackSlot)
    {
        var t = new Track(trackSlot);
        return t.IsValid ? t.Title : "";
    }
}
