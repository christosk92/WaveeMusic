// ── Entities/Episode.Selection.cs ──────────────────────────────────────────────────────────────────────────────────
// bulk selection over the show reader's rows (owner report 5): the model, what a row binds, and the shared selection
// bar's command lane for an all-episode selection
//
// Role: UI (with pure statics)
// Owner: A1 (S-reader)
// Spec: report 5 — "no selection at all, and when there was one it said 0 selected"
//
// ── ONE SELECTION MODEL, ONE INDEX SPACE ─────────────────────────────────────────────────────────────────────────────
//
// The reader is a bound `ItemsView` whose item space is the SNAPSHOT's: index 0 is the sticky rail, 1 the visit head,
// 2 the "episodes N" header, then month Groups interleaved with Rows, then the Foot. The engine's `SelectionModel`
// indexes that same flat space (the persistent prefix included — `Track.Table` does the same through its `TrackStart`),
// so a row's own index IS its selection index and no mapping table exists to drift.
//
// A GROUP HEADER IS NOT SELECTABLE and is never counted: only `ItemKind.Row` carries the check lane / the toggle
// handlers (`Episode.ReaderRowContent`'s selection arm), `SelectAll` walks the rows alone, and `SelectedCount` counts
// the rows among the model's selected indices rather than asking the model how many indices it holds. That is why
// "Select all" on a show with twelve months does not report twelve phantom selections.
//
// THE BAR APPEARS ONLY ABOVE ZERO. `Controls.SelectionBar(count, …, minCount: 1)` renders nothing at 0 — the reader
// passes the LIVE row count, never 0-with-minCount-0 (the table's arm, which keeps its own bar mounted), so the "0
// selected" bar the owner saw cannot come back.
//
// ── THE VERBS ARE THE SHARED ONES ────────────────────────────────────────────────────────────────────────────────────
//
// `SelectionVerbs.For([EntityKind.Episode])` (Track.Rules.cs) names them — Play · Play next · Add to queue ·
// Save/Unsave · Mark played/unplayed · Select all — and each one resolves through the app's ONE action table
// (`AppActions.Find` + `ActionTarget.ForEpisodes`), exactly as `Track.Table.Chrome.cs` composes its own episode bar.
// The composition is duplicated here rather than shared, because the table's builder is a method on its host and
// reaches into that host's selection, its row kinds and its playlist host; what is copied is the SHAPE, and the verbs,
// the icons and the labels all still come from the one table.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>Bulk selection over a reader whose items interleave rows with headers. Built ONCE per reader host; the
/// toolbar's select toggle arms it, Escape and the bar's ✕ clear it.</summary>
public sealed class EpisodeSelection
{
    /// <summary>The engine's model, handed to the list through <c>ListOptions.Selection</c> so the view's own
    /// keyboard/pointer semantics (Ctrl, Shift, the anchor) drive it.</summary>
    public SelectionModel Model { get; } = new() { Mode = ItemsSelectionMode.Extended };

    /// <summary>Is multi-select ARMED — the check lane's presence, and the click-vs-toggle decision a row makes.</summary>
    public Signal<bool> Selecting { get; } = new(false);

    /// <summary>What a row binds (<see cref="Episode.RowContext.Selection"/>).</summary>
    public Episode.RowSelectionContext Rows { get; }

    /// <summary>Clear the selection AND disarm — the bar's ✕ and a row's Escape.</summary>
    public Action Exit { get; }

    /// <summary>Select every REAL row and nothing else (never a month header).</summary>
    public Action SelectAll { get; }

    readonly Func<int, Episode> _episodeAt;
    readonly Func<int> _itemCount;
    readonly Func<int, bool> _isRow;

    /// <param name="episodeAt">the episode at a flat item index; <c>default</c> when that item is not a row.</param>
    /// <param name="itemCount">how many items the reader's list holds right now.</param>
    /// <param name="isRow">is the item at this flat index a selectable row (never a month header).</param>
    public EpisodeSelection(Func<int, Episode> episodeAt, Func<int> itemCount, Func<int, bool> isRow)
    {
        _episodeAt = episodeAt;
        _itemCount = itemCount;
        _isRow = isRow;
        Exit = () => { Model.DeselectAll(); Selecting.Value = false; };
        SelectAll = SelectEveryRow;
        Rows = new Episode.RowSelectionContext { Selecting = Selecting, Clear = Exit };
    }

    /// <summary>Arm or disarm. Disarming always drops the selection with it: a check lane that vanishes while rows stay
    /// selected leaves a bar nobody can see the cause of.</summary>
    public void Arm(bool on)
    {
        if (!on) { Model.DeselectAll(); Selecting.Value = false; return; }
        Selecting.Value = true;
    }

    /// <summary>How many EPISODES are selected — the bar's count. Reads <see cref="SelectionModel.Version"/>, so a
    /// caller's memo re-fires on every real change.</summary>
    public int SelectedCount
    {
        get
        {
            _ = Model.Version.Value;
            int n = 0;
            for (int r = 0; r < Model.RangeCount; r++)
            {
                var (s, e) = Model.GetRange(r);
                for (int i = s; i <= e; i++) if (_isRow(i)) n++;
            }
            return n;
        }
    }

    /// <summary>The selected episodes, in item order.</summary>
    public List<Episode> Selected()
    {
        var list = new List<Episode>();
        for (int r = 0; r < Model.RangeCount; r++)
        {
            var (s, e) = Model.GetRange(r);
            for (int i = s; i <= e; i++)
            {
                var episode = _episodeAt(i);
                if (episode.IsValid) list.Add(episode);
            }
        }
        return list;
    }

    /// <summary>Push the reader's current item count into the model — the model refuses an index past it. The hosting
    /// <c>ItemsView</c> does this on every render; a caller driving the model directly calls it first.</summary>
    public void Sync()
    {
        int n = _itemCount();
        if (Model.ItemCount != n) Model.ItemCount = n;
    }

    void SelectEveryRow()
    {
        Sync();
        int n = _itemCount();
        if (n <= 0) return;
        Selecting.Value = true;
        Model.DeselectAll();
        // One index at a time, never SelectAll(): a month header sits in the same index space and must stay unselected.
        for (int i = 0; i < n; i++) if (_isRow(i)) Model.Select(i);
    }

    // ══ THE COMMAND LANE ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Stacked thumbnails · "N selected" · the episode verbs · Select all · ✕, at the shared bar's fit tier
    /// (0 = labels, 1 = glyphs, 2 = essentials — <c>Controls.SelectionFitFor</c>). Every verb exits selection after it
    /// runs, as the table's bar does.</summary>
    public static Element Commands(int fit, EpisodeSelection selection)
    {
        _ = selection.Model.Version.Value;
        var episodes = selection.Selected();
        if (episodes.Count == 0) return new BoxEl();

        Episode.EnsureActions();
        var ctx = new ActionContext(ActionTarget.ForEpisodes(episodes), Actions.Services);
        bool allPlayed = true;
        for (int i = 0; i < episodes.Count && allPlayed; i++) allPlayed = Episode.Rules.Played(Episode.ReaderPctOf(episodes[i]));

        var kids = new List<Element>(12);
        if (fit <= 1 && Thumbs(episodes) is { Length: > 0 } thumbs)
            kids.Add(new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Children = thumbs });
        kids.Add(new BoxEl
        {
            Key = "ep-selection-count:" + episodes.Count,
            Animate = MotionRecipes.TextSwap,
            MinWidth = fit == 2 ? 66f : float.NaN,
            Children =
            [
                new TextEl(Strings.Detail.SelectedCount(episodes.Count))
                {
                    Size = 12f, Weight = 650, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                },
            ],
        });
        kids.Add(Divider());
        foreach (var raw in SelectionVerbs.For([EntityKind.Episode]))
        {
            if (raw == ActionId.SelectAll)
            {
                if (fit > 1) continue;                       // essentials: the transport verbs win the width
                kids.Add(Divider());
                kids.Add(Command(Icons.Accept, Loc.Get(Strings.Detail.SelectAll), fit, selection.SelectAll, null, true));
                continue;
            }
            // MarkPlayed stands for the absolute-state PAIR; the swap happens here, as it does in Episode.Menu.cs.
            var id = raw == ActionId.MarkPlayed && allPlayed ? ActionId.MarkUnplayed : raw;
            if (VerbCommand(id, in ctx, fit, selection.Exit) is { } cmd) kids.Add(cmd);
        }
        kids.Add(new BoxEl { Grow = 1f, MinWidth = 0f });
        kids.Add(Controls.Named(GlyphButton(Icons.Cancel, selection.Exit, true), Loc.Get(Strings.Detail.ClearSelection)));
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = 3f, Grow = 1f, MinWidth = 0f, ClipToBounds = true,
            Children = kids.ToArray(),
        };
    }

    /// <summary>Up to three stacked covers, de-duplicated by image (a show's episodes usually share one).</summary>
    static Element[] Thumbs(List<Episode> episodes)
    {
        var result = new List<Element>(3);
        Span<int> seen = stackalloc int[3];
        for (int i = 0; i < episodes.Count && result.Count < 3; i++)
        {
            var e = episodes[i];
            var image = e.IsValid && e.Knows(EpisodeFields.Image) ? e.ImageId : default;
            int key = image.IsEmpty ? -e.Slot : image.GetHashCode();
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

    static Element? VerbCommand(ActionId id, in ActionContext ctx, int fit, Action exit)
    {
        if (AppActions.Find(id) is not { } action) return null;
        var c = ctx;
        bool enabled = action.EnabledFor(in c);
        var icon = ActionIcons.Resolve(action.IconKey, action.CheckedFor(in c));
        Action invoke = enabled ? () => { action.Execute(c); exit(); } : static () => { };
        return Command(icon.Glyph ?? "", action.Label(c), fit, invoke, icon.Font, enabled);
    }

    static Element Command(string glyph, string label, int fit, Action invoke, string? font, bool enabled)
        => fit == 0
            ? new BoxEl
            {
                Direction = 0, Height = 32f, AlignItems = FlexAlign.Center, Gap = 6f, Padding = new Edges4(9f, 0f, 10f, 0f),
                Corners = Radii.ControlAll, IsEnabled = enabled, Focusable = enabled, Role = AutomationRole.Button,
                OnClick = invoke,
                Children =
                [
                    Icon(glyph, 14f, enabled ? Tok.TextSecondary : Tok.TextDisabled, family: font),
                    new TextEl(label) { Size = 12f, Weight = 600, Color = enabled ? Tok.TextSecondary : Tok.TextDisabled },
                ],
            }.Interactive(Interaction.Subtle)
            : Controls.Named(GlyphButton(glyph, invoke, enabled, font), label);

    static Element GlyphButton(string glyph, Action invoke, bool enabled, string? font = null) => new BoxEl
    {
        Width = 32f, Height = 32f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
        IsEnabled = enabled, Focusable = enabled, Role = AutomationRole.Button, OnClick = invoke,
        Children = [Icon(glyph, 13f, enabled ? Tok.TextSecondary : Tok.TextDisabled, family: font)],
    }.Interactive(Interaction.Subtle);

    static Element Divider() => new BoxEl
    {
        Width = 1f, Height = 20f, Fill = Prop.Of(static () => Tok.StrokeDividerDefault), Margin = new Edges4(4f, 0f, 4f, 0f),
    };
}
