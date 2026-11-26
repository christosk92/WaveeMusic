// ── Entities/Mix.Reorder.cs — #48 Mix M8: Smart reorder (inline), and the Premium upsell where Mix would be ──────────────────────
//
// Smart reorder (UX direction rev 2 "Smart reorder", the owner's inline-over-dialogs rule): the Mix menu's "Smart reorder" opens a
// panel in the list bar's eased slot (the one the Transitions list uses), never a dialog. It asks Spotify for its order through the
// harmonize lens (Spotify.MixLists.ReadSmartOrder) and shows the playlist's rows in that order, each with its tempo · key and the
// place it comes from. The rows are there from the first frame in the list's own order and only change their words when the answer
// lands, and the sentence above them keeps two lines, so nothing moves while Spotify answers. "Reorder" writes the order as ONE
// playlist edit (Spotify.MixLists.Reorder), closes the panel and says "Reordered 7 songs" with Undo, which writes the previous order
// back the same way. Owners and collaborators only, on Premium, while Mix is on.
//
// The upsell (the official client's "Play and create mixed playlists with Premium"): a Free account's Mix button opens it instead of
// the menu or a write (Track.Table.Chrome.cs's MixSplitButton), so Wavee never offers a Mix action it cannot perform.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;

namespace Wavee;

/// <summary>The Smart reorder panel's state on one playlist page: open or shut (a fresh read each time it opens).</summary>
internal sealed class MixReorderSession
{
    readonly Signal<int> _opened = new(0);
    Playlist _p;

    /// <summary>The panel while open, else null. A tracked read (the slot follows it).</summary>
    public readonly Func<Element?> Strip;
    public readonly Action Open;
    public readonly Action Close;
    public bool IsOpen => _opened.Peek() > 0;

    public MixReorderSession()
    {
        Close = () => _opened.Value = 0;
        Strip = () =>
        {
            int generation = _opened.Value;
            return generation > 0 && _p.IsValid
                ? Embed.Comp(() => new MixReorderPanel(_p, Close)) with { Key = "mix:reorder:" + generation.ToString(CultureInfo.InvariantCulture) }
                : null;
        };
        Open = () => _opened.Value = _opened.Peek() + 1;
    }

    /// <summary>The page shows <paramref name="p"/> now: another playlist starts shut. Called from an effect, never a render.</summary>
    public void Bind(Playlist p)
    {
        if (p.Slot == _p.Slot) return;
        _p = p;
        _opened.Value = 0;
    }
}

internal sealed class MixReorderPanel(Playlist playlist, Action close) : Component
{
    enum Phase : byte { Asking, Proposal, Same, NoOrder, Offline, Failed }

    readonly Playlist _p = playlist;
    readonly Action _close = close;
    Func<RowScope, Element>? _row;
    Signal<int[]?>? _map;
    ulong[]? _order;

    /// <summary>How many rows the panel shows before it scrolls (the Transitions list's count).</summary>
    const int VisibleRows = 6;

    public override Element Render()
    {
        var phase = UseSignal(Phase.Asking);
        var map = UseSignal<int[]?>(null);
        _map = map;
        int count = UseComputed(() =>
        {
            _ = Entities.Current.Edges.PlaylistTracks.Changed.Value;
            return Entities.Current.Edges.PlaylistTracks.Targets(_p.Slot).Length;
        }).Value;
        UseEffect(() => Spotify.MixLists.ReadSmartOrder(_p, (outcome, order) =>
        {
            if (outcome != Spotify.MixLists.Outcome.Landed || order is null)
            {
                phase.Value = outcome switch
                {
                    Spotify.MixLists.Outcome.Offline => Phase.Offline,
                    Spotify.MixLists.Outcome.NoOrder => Phase.NoOrder,
                    _ => Phase.Failed,
                };
                return;
            }
            var from = MapOf(_p, order);
            if (from is null) { phase.Value = Phase.NoOrder; return; }
            _order = order;
            bool same = true;
            for (int k = 0; k < from.Length && same; k++) same = from[k] == k;
            map.Value = from;
            phase.Value = same ? Phase.Same : Phase.Proposal;
        }), DepKey.Empty);

        _row ??= scope => Embed.Comp(() => new MixReorderRow(_p, scope, _map!));
        var now = phase.Value;
        int moved = 0;
        if (now == Phase.Proposal && map.Value is { } m) for (int k = 0; k < m.Length; k++) if (m[k] != k) moved++;
        string sentence = now switch
        {
            Phase.Asking => Loc.Get(Strings.Mix.ReorderAsking),
            Phase.Proposal => Strings.Mix.ReorderProposal(moved, count),
            Phase.Same => Loc.Get(Strings.Mix.ReorderSame),
            Phase.NoOrder => Loc.Get(Strings.Mix.ReorderNoOrder),
            Phase.Offline => Loc.Get(Strings.Mix.ReorderOffline),
            _ => Loc.Get(Strings.Mix.ReorderFailed),
        };
        float rowH = Ui.Body("").LineHeight + Spacing.L;
        float lineH = Ui.Caption("").LineHeight;
        var header = new BoxEl
        {
            Key = "mix:reorder:head", Direction = 0, AlignItems = FlexAlign.Center, Padding = new Edges4(0f, 0f, 0f, Spacing.XS),
            Children =
            [
                Ui.BodyStrong(Loc.Get(Strings.Mix.SmartReorder)) with { Grow = 1f, Shrink = 1f, MinWidth = 0f, MaxLines = 1 },
                Button.Subtle(Loc.Get(Strings.Mix.Close), _close, parts: MixUpsell.CommandCase()) with { Key = "mix:reorder:close" },
            ],
        };
        // Two lines held whatever the sentence says, so the answer landing never moves the rows.
        var line = new BoxEl
        {
            Key = "mix:reorder:line", Direction = 1, MinWidth = 0f, Height = lineH * 2f, Shrink = 0f, Margin = new Edges4(0f, 0f, 0f, Spacing.XS),
            Children = [Ui.Caption(sentence) with { Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis, Color = Tok.TextSecondary, MinWidth = 0f }],
        };
        var list = new BoxEl
        {
            Key = "mix:reorder:list", Direction = 1, Height = rowH * Math.Clamp(count, 1, VisibleRows), MinWidth = 0f, ClipToBounds = true,
            Children = [ItemsView.CreateBound(count, _row, RepeatLayout.Stack(rowH))],
        };
        var actions = new BoxEl
        {
            Key = "mix:reorder:actions", Direction = 0, Justify = FlexJustify.End, Gap = Spacing.S, Padding = new Edges4(0f, Spacing.S, 0f, 0f),
            Children =
            [
                Button.Accent(Loc.Get(Strings.Mix.ReorderApply), Apply, isEnabled: now == Phase.Proposal, parts: MixUpsell.CommandCase()) with { Key = "mix:reorder:apply" },
                Button.Standard(Loc.Get(Strings.Mix.ReorderCancel), _close, parts: MixUpsell.CommandCase()) with { Key = "mix:reorder:cancel" },
            ],
        };
        return new BoxEl
        {
            Key = "mix:reorder:panel", Direction = 1, MinWidth = 0f, Padding = new Edges4(Spacing.M, Spacing.S, Spacing.M, Spacing.S),
            Fill = Tok.FillCardDefault, Corners = Radii.ControlAll, BorderColor = Tok.StrokeDividerDefault, BorderWidth = 1f,
            Children = [header, line, list, actions],
        };
    }

    void Apply()
    {
        if (_order is not { } order) return;
        var p = _p;
        var before = Spotify.MixLists.OrderOf(p);
        _close();
        Spotify.MixLists.Reorder(p, order, (outcome, moved) =>
        {
            switch (outcome)
            {
                case Spotify.MixLists.Outcome.Landed:
                    Notify.Say(Strings.Mix.Reordered(moved), InfoBarSeverity.Success, Loc.Get(Strings.Mix.ReorderUndo),
                        () => Spotify.MixLists.Reorder(p, before, (back, _) => Notify.Say(Loc.Get(back == Spotify.MixLists.Outcome.Landed
                            ? Strings.Mix.ReorderUndone : Strings.Mix.ReorderWriteFailed),
                            back == Spotify.MixLists.Outcome.Landed ? InfoBarSeverity.Informational : InfoBarSeverity.Warning, dedupeKey: "mix.reorder")),
                        dedupeKey: "mix.reorder");
                    break;
                case Spotify.MixLists.Outcome.Same:
                    break;
                case Spotify.MixLists.Outcome.Changed:
                    Notify.Say(Loc.Get(Strings.Mix.ReorderChanged), InfoBarSeverity.Warning, dedupeKey: "mix.reorder");
                    break;
                case Spotify.MixLists.Outcome.Offline:
                    Notify.Say(Loc.Get(Strings.Mix.ReorderOffline), InfoBarSeverity.Warning, dedupeKey: "mix.reorder");
                    break;
                default:
                    Notify.Say(Loc.Get(Strings.Mix.ReorderWriteFailed), InfoBarSeverity.Warning, dedupeKey: "mix.reorder");
                    break;
            }
        });
    }

    /// <summary>Spotify's order as the list's own indices (k → the row at k in Spotify's order), or null when the two do not hold the
    /// same rows (the list moved while the lens was read). UI thread.</summary>
    static int[]? MapOf(Playlist p, ulong[] order)
    {
        var payload = Entities.Current.Edges.PlaylistTracks.Payload(p.Slot);
        if (payload.Length != order.Length) return null;
        var at = new Dictionary<ulong, int>(payload.Length);
        for (int i = 0; i < payload.Length; i++)
            if (!at.TryAdd(MixRecipe.Hex64(Entities.Strings.Resolve(payload[i].ItemId)), i)) return null;
        var from = new int[order.Length];
        for (int k = 0; k < order.Length; k++) if (!at.TryGetValue(order[k], out from[k])) return null;
        return from;
    }
}

/// <summary>One row of the proposal: its new place, the song, its tempo · key, and the place it comes from.</summary>
internal sealed class MixReorderRow(Playlist playlist, RowScope scope, Signal<int[]?> map) : Component
{
    readonly Playlist _p = playlist;
    readonly RowScope _scope = scope;
    readonly Signal<int[]?> _map = map;

    readonly record struct Face(string Place, string Title, string Facts, string Was);

    public override Element Render()
    {
        var face = UseComputed(() =>
        {
            int k = _scope.Index.Value;
            var map = _map.Value;
            _ = Entities.Current.Edges.PlaylistTracks.Changed.Value;
            var targets = Entities.Current.Edges.PlaylistTracks.Targets(_p.Slot);
            int i = map is not null && k >= 0 && k < map.Length ? map[k] : k;
            if (i < 0 || i >= targets.Length) return default(Face);
            var t = new Track(targets[i]);
            string facts = "";
            if (t.IsValid && t.Tempo > 0)
            {
                facts = Track.Format.Bpm(t.Tempo / 10.0);
                if (Track.Format.CamelotLabel(t.Camelot) is { } key) facts += " · " + key;
            }
            return new Face((k + 1).ToString(CultureInfo.CurrentCulture), t.IsValid ? t.Title : "", facts,
                i != k ? Strings.Mix.ReorderWas((i + 1).ToString(CultureInfo.CurrentCulture)) : "");
        }).Value;
        return new BoxEl
        {
            Key = "mix:r:row", Direction = 0, Grow = 1f, MinWidth = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.M,
            Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
            Children =
            [
                new BoxEl { Width = Spacing.XXL, Shrink = 0f, Children = [Ui.Caption(face.Place) with { MaxLines = 1, Color = Tok.TextSecondary }] },
                Ui.Body(face.Title) with { Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Wrap = TextWrap.NoWrap },
                new BoxEl { Width = Spacing.XXL * 2f, Shrink = 0f, Children = [Ui.Caption(face.Facts) with { MaxLines = 1 }] },
                new BoxEl { Width = Spacing.XXL * 2f, Shrink = 0f, Children = [Ui.Caption(face.Was) with { MaxLines = 1, Color = Tok.TextSecondary }] },
            ],
        };
    }
}

/// <summary>The Premium upsell where Mix would be (the official client's popover, as a flyout from the Mix button).</summary>
internal static class MixUpsell
{
    /// <summary>A button's label in the style's command voice (Zune writes commands lowercase), for the buttons this file draws in every
    /// theme.</summary>
    public static TemplateParts CommandCase()
    {
        var parts = new TemplateParts();
        TextCase voice = Wavee.Theming.Roles.R.Case.For(Wavee.Theming.Roles.LabelKind.Command);
        parts.Set<TextEl>(Button.PartLabel, t => t with { Case = voice });
        return parts;
    }

    public static Element Card(Action close) => new BoxEl
    {
        Key = "mix:upsell", Direction = 1, Gap = Spacing.S, Width = Spacing.XXL * 10f, Padding = new Edges4(Spacing.L, Spacing.L, Spacing.L, Spacing.L),
        Children =
        [
            Ui.BodyStrong(Loc.Get(Strings.Mix.UpsellTitle)) with { Wrap = TextWrap.Wrap, MinWidth = 0f },
            Ui.Body(Loc.Get(Strings.Mix.UpsellBody)) with { MinWidth = 0f, Color = Tok.TextSecondary },
            new BoxEl
            {
                Direction = 0, Justify = FlexJustify.End, Padding = new Edges4(0f, Spacing.XS, 0f, 0f),
                Children = [Button.Accent(Loc.Get(Strings.Mix.UpsellCta), () => { close(); Shell.OpenBrowser(Setup.SpotifyPremiumUrl); }, parts: CommandCase()) with { Key = "mix:upsell:cta" }],
            },
        ],
    };
}
