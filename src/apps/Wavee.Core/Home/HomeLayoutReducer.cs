namespace Wavee.Core.Home;

// The PURE Home-layout reducer. One entry point, one verdict, zero side effects: Apply never mutates its input,
// never touches disk, never localizes, and never reaches for a service. Rejections are DATA, not exceptions.

public static class HomeLayoutReducer
{
    /// <summary>Top-level module cap. v1 authors a closed set (<see cref="HomeLayoutModules.DefaultOrder"/>);
    /// the slack is for a later build that adds a known landing kind without a document migration.</summary>
    public const int MaxModules = 24;

    public static HomeLayoutCommandResult Apply(HomeLayoutDoc layout, HomeLayoutCommand command)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(command);

        return command switch
        {
            SetHomeModuleHidden c => DoSetHidden(layout, c),
            MoveHomeModule c => DoMove(layout, c),
            ResetHomeLayout => HomeLayoutCommandResult.Ok(HomeLayoutDoc.Default),
            _ => HomeLayoutCommandResult.Reject(layout, HomeLayoutRejectReason.NoChange),
        };
    }

    static HomeLayoutCommandResult DoSetHidden(HomeLayoutDoc layout, SetHomeModuleHidden c)
    {
        if (!HomeLayoutModules.IsFixedLanding(c.Kind))
            return HomeLayoutCommandResult.Reject(layout, HomeLayoutRejectReason.UnknownModule);

        int at = layout.IndexOf(c.Kind);
        if (at < 0)
        {
            if (layout.ModuleCount >= MaxModules)
                return HomeLayoutCommandResult.Reject(layout, HomeLayoutRejectReason.CapReached);
            var grown = new List<HomeModuleSpec>(layout.Modules) { new(c.Kind, c.Hidden) };
            return HomeLayoutCommandResult.Ok(layout with { Modules = grown });
        }

        var current = layout.Modules[at];
        if (current.Hidden == c.Hidden)
            return HomeLayoutCommandResult.Reject(layout, HomeLayoutRejectReason.NoChange);

        var next = new List<HomeModuleSpec>(layout.Modules);
        next[at] = current with { Hidden = c.Hidden };
        return HomeLayoutCommandResult.Ok(layout with { Modules = next });
    }

    static HomeLayoutCommandResult DoMove(HomeLayoutDoc layout, MoveHomeModule c)
    {
        var modules = layout.Modules;
        if (c.FromIndex < 0 || c.FromIndex >= modules.Count)
            return HomeLayoutCommandResult.Reject(layout, HomeLayoutRejectReason.UnknownModule);

        var items = new List<HomeModuleSpec>(modules);
        var moving = items[c.FromIndex];
        items.RemoveAt(c.FromIndex);
        int at = Math.Clamp(c.ToIndex, 0, items.Count);
        if (at == c.FromIndex)
            return HomeLayoutCommandResult.Reject(layout, HomeLayoutRejectReason.NoChange);
        items.Insert(at, moving);
        return HomeLayoutCommandResult.Ok(layout with { Modules = items });
    }
}
