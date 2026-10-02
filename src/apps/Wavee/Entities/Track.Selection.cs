// ── Entities/Track.Selection.cs ────────────────────────────────────────────────────────────────────────────────────
// the ONE selection command lane: Thumbs · count · verbs · Select all · "…" · ✕, for a track, an episode or a mixed
// selection. The detail table, the show reader (and, later, the album drawer and Top tracks) all mount it as the
// `commands` of `Controls.SelectionBar`; the host supplies WHAT is selected and the two actions, the lane owns the rest.
//
// Role: UI (with pure statics)
// Owner: interaction-consistency WP2
// Spec: docs/plans/wavee/interaction-consistency-implementation.md §7 (WP2), owner decision D3
//
// ── WHY FUNCS, NOT LISTS ─────────────────────────────────────────────────────────────────────────────────────────────
// The lane's "…" is a component whose props freeze at mount, and its menu is built at OPEN so it names the selection as
// it stands when clicked. So the args carry PROVIDERS (Func) the lane calls at render and the menu calls again at open,
// never a snapshot list that would go stale inside the mounted component.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>When the selection bar shows (owner decision D3): two or more selected rows always; ONE selected row only
/// when multi-select is armed (the toolbar's Select toggle). Pure.</summary>
public static class SelectionBarRules
{
    /// <summary>Does the bar show for <paramref name="count"/> selected rows.</summary>
    public static bool Visible(int count, bool armed) => count >= 2 || (armed && count >= 1);

    /// <summary>Does the check lane show.</summary>
    public static bool ChecksVisible(int count, bool armed) => armed || count >= 2;
}

/// <summary>Which verb set / body the lane builds.</summary>
public enum SelectionLaneKind : byte { Tracks = 0, Episodes = 1, Mixed = 2 }

/// <summary>Everything the command lane reads from its host. Build one per lane render; the providers are called at
/// render and again when the "…" menu opens, so cache the delegates on the host (no per-render closure).</summary>
/// <param name="Kind">tracks (thumbs + the transport verbs + "…"), episodes (the episode verbs) or mixed (the three
/// verbs every kind agrees on). An explicit field, not derived from the lists, so a host can classify by ROW kind.</param>
/// <param name="Count">the selected count the bar shows (&gt; 0; the lane draws nothing at 0).</param>
/// <param name="Tracks">the selected tracks, in order (Tracks / Mixed).</param>
/// <param name="Episodes">the selected episodes, in order (Episodes).</param>
/// <param name="Host">the hosting playlist for the selected rows; <c>default</c> off a playlist (Tracks / Mixed).</param>
/// <param name="Exit">clear the selection AND disarm — every verb runs this after it acts, and the ✕ is bound to it.</param>
/// <param name="SelectAll">select every selectable row.</param>
/// <param name="WasVisible">was the bar already showing — false suppresses the count's enter animation on first show.</param>
public readonly record struct SelectionLaneArgs(
    SelectionLaneKind Kind,
    int Count,
    Func<IReadOnlyList<Track>> Tracks,
    Func<IReadOnlyList<Episode>> Episodes,
    Func<PlaylistHost> Host,
    Action Exit,
    Action SelectAll,
    bool WasVisible)
{
    static readonly Func<PlaylistHost> s_noHost = static () => default;
    static readonly Func<IReadOnlyList<Track>> s_noTracks = static () => [];
    static readonly Func<IReadOnlyList<Episode>> s_noEpisodes = static () => [];

    /// <summary>A plain track list (an album drawer, Top tracks): <paramref name="tracks"/> read live from the host's
    /// <c>SelectionModel</c>. Pass <paramref name="host"/> only where the rows belong to an editable playlist.</summary>
    public static SelectionLaneArgs ForTracks(int count, Func<IReadOnlyList<Track>> tracks, Action exit, Action selectAll,
                                              Func<PlaylistHost>? host = null, bool wasVisible = true)
        => new(SelectionLaneKind.Tracks, count, tracks, s_noEpisodes, host ?? s_noHost, exit, selectAll, wasVisible);

    /// <summary>An all-episode selection (the show reader).</summary>
    public static SelectionLaneArgs ForEpisodes(int count, Func<IReadOnlyList<Episode>> episodes, Action exit, Action selectAll,
                                                bool wasVisible = true)
        => new(SelectionLaneKind.Episodes, count, s_noTracks, episodes, s_noHost, exit, selectAll, wasVisible);
}

public readonly partial struct Track
{
    /// <summary>The selection command lane — the <c>commands</c> of <see cref="Controls.SelectionBar"/>. Fit 0 labels,
    /// 1 glyphs, 2 essentials. Every verb exits selection after it runs. Draws an empty box at <c>Count &lt;= 0</c>.</summary>
    public static Element SelectionLane(int fit, in SelectionLaneArgs a)
    {
        if (a.Count <= 0) return new BoxEl();
        switch (a.Kind)
        {
            case SelectionLaneKind.Episodes:
            {
                Episode.EnsureActions();
                var episodes = a.Episodes();
                var ctx = new ActionContext(ActionTarget.ForEpisodes(episodes), Actions.Services);
                bool allPlayed = episodes.Count > 0;
                for (int i = 0; i < episodes.Count && allPlayed; i++) allPlayed = Episode.Rules.Played(Episode.ReaderPctOf(episodes[i]));
                return LaneVerbRow(fit, in a, in ctx, SelectionVerbs.For([EntityKind.Episode]), allPlayed);
            }
            case SelectionLaneKind.Mixed:
            {
                var ctx = new ActionContext(ActionTarget.ForTracks(a.Tracks(), a.Host()), Actions.Services);
                return LaneVerbRow(fit, in a, in ctx, SelectionVerbs.For([EntityKind.Track, EntityKind.Episode]), false);
            }
            default:
                return LaneTrackBody(fit, in a);
        }
    }

    static Element LaneTrackBody(int fit, in SelectionLaneArgs a)
    {
        var tracks = a.Tracks();
        var ctx = new ActionContext(ActionTarget.ForTracks(tracks, a.Host()), Actions.Services);
        var exit = a.Exit;

        var kids = new List<Element>(12);
        if (fit <= 1 && LaneThumbs(tracks) is { Length: > 0 } thumbs)
            kids.Add(new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Children = thumbs });
        kids.Add(LaneCount(fit, a.Count, a.WasVisible));
        kids.Add(LaneDivider());
        if (LaneVerb(ActionId.Play, in ctx, fit, exit) is { } play) kids.Add(play);
        if (fit <= 1)
        {
            if (LaneVerb(ActionId.PlayNext, in ctx, fit, exit) is { } next) kids.Add(next);
            if (LaneVerb(ActionId.AddToQueue, in ctx, fit, exit) is { } queue) kids.Add(queue);
            if (LaneVerb(ActionId.ToggleLike, in ctx, fit, exit) is { } like) kids.Add(like);
            kids.Add(LaneDivider());
            kids.Add(LaneCommand(Icons.Accept, Loc.Get(Strings.Detail.SelectAll), fit, a.SelectAll, null, true));
        }
        var args = a;
        kids.Add(Embed.Comp(() => new SelectionMore(args, fit)) with { Key = "selection-more:" + fit });
        kids.Add(new BoxEl { Grow = 1f, MinWidth = 0f });
        kids.Add(ToolTip.Wrap(LaneGlyph(Icons.Cancel, exit, null, null, true), Loc.Get(Strings.Detail.ClearSelection)));
        return LaneRoot(kids);
    }

    /// <summary>The row the episode and mixed variants share: count · verbs (in <see cref="SelectionVerbs.For"/> order)
    /// · Select all · ✕. <see cref="ActionId.MarkPlayed"/> stands for the absolute-state pair and is swapped for
    /// <see cref="ActionId.MarkUnplayed"/> here — the ONE place that substitution happens for the bar, mirroring
    /// <c>Episode.Menu.cs</c>'s single-row menu.</summary>
    static Element LaneVerbRow(int fit, in SelectionLaneArgs a, in ActionContext ctx, ActionId[] verbs, bool allPlayed)
    {
        var exit = a.Exit;
        var kids = new List<Element>(12) { LaneCount(fit, a.Count, a.WasVisible), LaneDivider() };
        foreach (var raw in verbs)
        {
            if (raw == ActionId.SelectAll)
            {
                kids.Add(LaneDivider());
                kids.Add(LaneCommand(Icons.Accept, Loc.Get(Strings.Detail.SelectAll), fit, a.SelectAll, null, true));
                continue;
            }
            var id = raw == ActionId.MarkPlayed && allPlayed ? ActionId.MarkUnplayed : raw;
            if (LaneVerb(id, in ctx, fit, exit) is { } cmd) kids.Add(cmd);
        }
        kids.Add(new BoxEl { Grow = 1f, MinWidth = 0f });
        kids.Add(ToolTip.Wrap(LaneGlyph(Icons.Cancel, exit, null, null, true), Loc.Get(Strings.Detail.ClearSelection)));
        return LaneRoot(kids);
    }

    static BoxEl LaneRoot(List<Element> kids) => new()
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = 3f, Grow = 1f, MinWidth = 0f, ClipToBounds = true,
        Children = kids.ToArray(),
    };

    static Element LaneCount(int fit, int count, bool wasVisible) => new BoxEl
    {
        Key = "selection-count:" + count,
        Animate = wasVisible ? MotionRecipes.TextSwap : MotionRecipes.TextSwap with { Enter = default },
        MinWidth = fit == 2 ? 66f : float.NaN,
        Children = [Ui.Caption(Strings.Detail.SelectedCount(count)) with { Weight = 650, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }],
    };

    /// <summary>Up to three stacked covers, de-duplicated by image (an album's tracks share one).</summary>
    static Element[] LaneThumbs(IReadOnlyList<Track> tracks)
    {
        var result = new List<Element>(3);
        Span<int> seen = stackalloc int[3];
        for (int i = 0; i < tracks.Count && result.Count < 3; i++)
        {
            var t = tracks[i];
            var image = t.ImageId.IsEmpty && t.Album.IsValid ? t.Album.ImageId : t.ImageId;
            int key = image.IsEmpty ? -t.Slot : image.GetHashCode();
            bool dup = false;
            for (int j = 0; j < result.Count; j++) dup |= seen[j] == key;
            if (dup) continue;
            seen[result.Count] = key;
            result.Add(new BoxEl
            {
                Width = 28f, Height = 28f, Shrink = 0f, Corners = CornerRadius4.All(5f), ClipToBounds = true,
                Margin = new Edges4(result.Count == 0 ? 0f : -11f, 0f, 0f, 0f),
                BorderWidth = 2f, BorderColor = Tok.FillCardSecondary,
                Children = [Controls.Artwork(Controls.ArtUrl(image), 28f, 28f, 5f, decodePx: 56)],
            });
        }
        return result.ToArray();
    }

    static Element? LaneVerb(ActionId id, in ActionContext ctx, int fit, Action exit)
    {
        if (AppActions.Find(id) is not { } action) return null;
        var c = ctx;
        bool enabled = action.EnabledFor(in c);
        var icon = ActionIcons.Resolve(action.IconKey, action.CheckedFor(in c));
        Action invoke = enabled ? () => { action.Execute(c); exit(); } : static () => { };
        return LaneCommand(icon.Glyph ?? "", action.Label(c), fit, invoke, icon.Font, enabled);
    }

    static Element LaneCommand(string glyph, string label, int fit, Action invoke, string? font, bool enabled)
        => fit == 0
            ? new BoxEl
            {
                Direction = 0, Height = 32f, AlignItems = FlexAlign.Center, Gap = 6f, Padding = new Edges4(9f, 0f, 10f, 0f),
                Corners = Radii.ControlAll, IsEnabled = enabled, Focusable = enabled, Role = AutomationRole.Button, OnClick = invoke,
                Children =
                [
                    Icon(glyph, 14f, enabled ? Tok.TextSecondary : Tok.TextDisabled, family: font),
                    Ui.Caption(label) with { Weight = 600, Color = enabled ? Tok.TextSecondary : Tok.TextDisabled },
                ],
            }.Interactive(Interaction.Subtle)
            : ToolTip.Wrap(LaneGlyph(glyph, invoke, null, font, enabled), label);

    static BoxEl LaneGlyph(string glyph, Action invoke, Action<NodeHandle>? realized, string? font, bool enabled) => new BoxEl
    {
        Width = 32f, Height = 32f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
        IsEnabled = enabled, Focusable = enabled, Role = AutomationRole.Button, OnClick = invoke, OnRealized = realized,
        Children = [Icon(glyph, 13f, enabled ? Tok.TextSecondary : Tok.TextDisabled, family: font)],
    }.Interactive(Interaction.Subtle);

    static Element LaneDivider() => new BoxEl
    {
        Width = 1f, Height = 20f, Fill = Prop.Of(static () => Tok.StrokeDividerDefault), Margin = new Edges4(4f, 0f, 4f, 0f),
    };

    /// <summary>The selection "…": at the essentials fit the transport verbs move here, then the track menu's rows for the
    /// selection, then Select all. Built at OPEN, so it names the selection as it stands when clicked.</summary>
    sealed class SelectionMore(SelectionLaneArgs args, int fit) : Component
    {
        public override Element Render()
        {
            var a = args;
            var overlay = UseContext(Overlay.Service);
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);

            List<MenuFlyoutItem> Items()
            {
                var tracks = a.Tracks();
                var hostRows = a.Host();
                var ctx = new ActionContext(ActionTarget.ForTracks(tracks, hostRows), Actions.Services);
                var exit = a.Exit;
                var items = new List<MenuFlyoutItem>(16);
                if (fit >= 2)
                {
                    if (Actions.Menu.Row(ActionId.PlayNext, in ctx, exit) is { } next) items.Add(next);
                    if (Actions.Menu.Row(ActionId.AddToQueue, in ctx, exit) is { } queue) items.Add(queue);
                    if (Actions.Menu.Row(ActionId.ToggleLike, in ctx, exit) is { } like) items.Add(like);
                    if (items.Count > 0) items.Add(MenuFlyoutItem.Separator);
                }
                if (Track.Menu(tracks, new MenuOptions(Host: hostRows, ShowGoToAlbum: false, PickerOverlay: overlay)) is { } model)
                    foreach (var row in model.Rows) items.Add(WithExit(row, exit));
                items.Add(MenuFlyoutItem.Separator);
                items.Add(new MenuFlyoutItem(Loc.Get(Strings.Detail.SelectAll), Icons.Accept, true, a.SelectAll));
                return items;
            }

            void Toggle() => TableHost.ToggleOverlay(overlay, anchor, handle, () => MenuFlyout.Create(Items(), () => handle.Value?.Close()), TableHost.MenuPopup);
            return ToolTip.Wrap(LaneGlyph(Icons.More, Toggle, n => anchor.Value = n, null, true), Loc.Get(Strings.Common.More));
        }

        static MenuFlyoutItem WithExit(MenuFlyoutItem item, Action exit)
        {
            if (item.Kind == MenuItemKind.Separator) return item;
            if (item.Kind == MenuItemKind.SubMenu && item.SubItems is { } nested)
            {
                var mapped = new MenuFlyoutItem[nested.Count];
                for (int i = 0; i < nested.Count; i++) mapped[i] = WithExit(nested[i], exit);
                return item with { SubItems = mapped };
            }
            if (item.Invoke is not { } invoke) return item;
            return item with { Invoke = () => { invoke(); exit(); } };
        }
    }
}
