// ── Home/Board/HomeCommands.cs: the pure reducer (design 1.2.3, spec 8.4) ───────────────────────────────────────────
//
// The only way a HomeDoc changes. Commands address ENTRIES of the board as shown (feed sections and fill cells are not
// in the document yet); a command on a feed section other than Hide first takes control.

using System.Collections.Immutable;

namespace Wavee;

public enum HomeResetScope : byte { Page, Everything }

public abstract record HomeCommand
{
    public sealed record Pin(string PinId, string Name, string Cover, string? GroupId = null) : HomeCommand;
    public sealed record Unpin(string PinId) : HomeCommand;
    public sealed record Add(HomeSource Source, string GroupId, int AtIndex = -1, HomeSize Size = HomeSize.Auto) : HomeCommand;
    public sealed record Remove(string EntryId) : HomeCommand;            // a widget or a pin leaves; a section is hidden
    public sealed record Hide(SectionRef Ref, bool WholeFamily = false) : HomeCommand;
    public sealed record Show(SectionRef Ref) : HomeCommand;
    public sealed record Keep(string EntryId) : HomeCommand;              // a fill cell becomes the user's pin
    public sealed record MoveOnto(string EntryId, string TargetEntryId) : HomeCommand;   // the before / after rule
    public sealed record MoveBy(string EntryId, int Delta) : HomeCommand;
    public sealed record MoveToEdge(string EntryId, bool ToStart) : HomeCommand;         // Stick / Lower
    public sealed record MoveToGroup(string EntryId, string GroupId, int AtIndex) : HomeCommand;
    /// <summary>A shortcut cell to another place in the shortcuts grid (its index among the grid's cells, in the order shown): the grid's order
    /// becomes the user's (the fill's cells it passes become their pins, the fill gives up as many places).</summary>
    public sealed record ReorderShortcut(string EntryId, int ToIndex) : HomeCommand;
    public sealed record SetSize(string EntryId, HomeSize Size) : HomeCommand;
    public sealed record CycleSize(string EntryId) : HomeCommand;
    public sealed record SetOptions(string EntryId, ImmutableSortedDictionary<string, string> Patch) : HomeCommand;
    public sealed record Reset(HomeResetScope Scope) : HomeCommand;
}

/// <summary>What a command did: whether the document changed, the item it acted on (the new item for a pin or an add;
/// the existing one for a pin of something already pinned), the group the change was in (null: several / the page),
/// and the label key of the undo step.</summary>
public readonly record struct HomeApplied(bool Changed, string? ItemId, string? GroupId, string LabelKey);

public static partial class HomeCommands
{
    /// <summary>Pure. `shown` is the board as projected right now: commands address ENTRIES (which include feed sections
    /// and fill cells that are not in the document yet), and take-control needs the order shown.</summary>
    public static HomeDoc Apply(HomeDoc doc, HomeCommand command, BoardModel shown, HomeSourceRegistry sources,
        long nowMs, out HomeApplied applied)
        => HomeShortcuts.Normalize(ApplyCore(doc, command, shown, sources, nowMs, out applied));
}
