// ── Entities/Track.SelectionDock.cs — the selection bar's verbs as DATA, for a theme that docks the bar (contract 1.47) ───────────────────────
//
// `Track.SelectionLane` draws the in-flow bar (Classic's picture). A theme whose frame provides a `SelectionDockHost` draws its own bar from this model:
// the same verbs, in the same order, with the same enabled / checked / labelled facts, each running its action and then exiting selection (the lane's
// rule). The "..." is the lane's own component, so the selection's menu stays the core's.
//
// Role: UI (with pure statics)

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using static FluentGpu.Dsl.Ui;
using Wavee.Theming.Contracts;

namespace Wavee;

public readonly partial struct Track
{
    /// <summary>The docked bar's model for a selection: the lane's verbs (tracks: play · play next · add to queue · like, and remove on an owned
    /// playlist where it is enabled; episodes and mixed: <see cref="SelectionVerbs"/>), the "..." and the two ends (select all, done).
    /// <paramref name="total"/> is the list's row count (0 = unknown).</summary>
    internal static SelectionDockProps SelectionDock(in SelectionLaneArgs a, int total)
    {
        var exit = a.Exit;
        ActionContext ctx;
        ActionId[] verbs;
        bool allPlayed = false;
        switch (a.Kind)
        {
            case SelectionLaneKind.Episodes:
            {
                Episode.EnsureActions();
                var episodes = a.Episodes();
                ctx = new ActionContext(ActionTarget.ForEpisodes(episodes), Actions.Services);
                allPlayed = episodes.Count > 0;
                for (int i = 0; i < episodes.Count && allPlayed; i++) allPlayed = Episode.Rules.Played(Episode.ReaderPctOf(episodes[i]));
                verbs = SelectionVerbs.For([EntityKind.Episode]);
                break;
            }
            case SelectionLaneKind.Mixed:
                ctx = new ActionContext(ActionTarget.ForTracks(a.Tracks(), a.Host()), Actions.Services);
                verbs = SelectionVerbs.For([EntityKind.Track, EntityKind.Episode]);
                break;
            default:
                ctx = new ActionContext(ActionTarget.ForTracks(a.Tracks(), a.Host()), Actions.Services);
                verbs = [ActionId.Play, ActionId.PlayNext, ActionId.AddToQueue, ActionId.ToggleLike, ActionId.RemoveFromThisPlaylist];
                break;
        }

        var commands = new List<SelectionDockCommand>(verbs.Length);
        foreach (var raw in verbs)
        {
            if (raw == ActionId.SelectAll) continue;
            var id = raw == ActionId.MarkPlayed && allPlayed ? ActionId.MarkUnplayed : raw;
            if (AppActions.Find(id) is not { } action) continue;
            var c = ctx;
            bool enabled = action.EnabledFor(in c);
            // Remove is the board's fifth ring, but only an owned playlist's rows can be removed: elsewhere it is not offered, never shown dead.
            if (id == ActionId.RemoveFromThisPlaylist && !enabled) continue;
            var icon = ActionIcons.Resolve(action.IconKey, action.CheckedFor(in c));
            Action invoke = enabled ? () => { action.Execute(c); exit(); } : static () => { };
            commands.Add(new SelectionDockCommand(id.ToString(), action.Label(c), icon.Glyph ?? "", icon.Font, enabled, id == ActionId.Play, invoke));
        }

        var args = a;
        return new SelectionDockProps
        {
            Count = a.Count,
            Total = total,
            CountLabel = Strings.Detail.SelectedCount(a.Count),
            Commands = commands,
            More = Embed.Comp(() => new SelectionMore(args, 1)) with { Key = "selection-dock-more" },
            SelectAllLabel = Loc.Get(Strings.Detail.SelectAll),
            SelectAll = a.SelectAll,
            DoneLabel = Loc.Get(Strings.Detail.DoneSelecting),
            Done = exit,
        };
    }
}
