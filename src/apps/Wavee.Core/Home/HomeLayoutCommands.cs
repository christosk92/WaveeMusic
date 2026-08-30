namespace Wavee.Core.Home;

// The Home-layout COMMAND SET — the only way a HomeLayoutDoc changes. In-memory only (never serialized), so unlike
// the payload model these ARE a record hierarchy. HomePreferences.Dispatch is the single mutation entry point:
// reduce -> if Changed, write the document signal, persist.

/// <summary>Undo/redo label loc keys, one per command shape. Kept as consts (Wavee.Core cannot see the app's
/// generated <c>Strings</c> table) so a key is spelled exactly once.</summary>
public static class HomeLayoutUndoLabels
{
    public const string HideModule = "home.customizer.undo.hide";
    public const string ShowModule = "home.customizer.undo.show";
    public const string MoveModule = "home.customizer.undo.move";
    public const string Reset = "home.customizer.undo.reset";
}

public abstract record HomeLayoutCommand
{
    public abstract string LabelLocKey { get; }
}

public sealed record SetHomeModuleHidden(HomeGroupKind Kind, bool Hidden) : HomeLayoutCommand
{
    public override string LabelLocKey => Hidden ? HomeLayoutUndoLabels.HideModule : HomeLayoutUndoLabels.ShowModule;
}

/// <summary>Reorder a module. Indices are into <see cref="HomeLayoutDoc.Modules"/> (hidden modules keep their
/// place); <paramref name="ToIndex"/> is interpreted AFTER the removal (the standard <c>Reorderable.OnReorder</c>
/// contract).</summary>
public sealed record MoveHomeModule(int FromIndex, int ToIndex) : HomeLayoutCommand
{
    public override string LabelLocKey => HomeLayoutUndoLabels.MoveModule;
}

public sealed record ResetHomeLayout() : HomeLayoutCommand
{
    public override string LabelLocKey => HomeLayoutUndoLabels.Reset;
}

/// <summary>The reducer's verdict. <c>Changed == false</c> ⇒ the caller does NOT autosave.</summary>
public readonly record struct HomeLayoutCommandResult(
    HomeLayoutDoc Layout,
    bool Changed,
    HomeLayoutRejectReason Reason)
{
    public static HomeLayoutCommandResult Ok(HomeLayoutDoc layout)
        => new(layout, true, HomeLayoutRejectReason.None);

    public static HomeLayoutCommandResult Reject(HomeLayoutDoc layout, HomeLayoutRejectReason reason)
        => new(layout, false, reason);
}

/// <summary>Why a command changed nothing. Append only — the customizer maps these to inline messages.</summary>
public enum HomeLayoutRejectReason : byte
{
    None = 0,
    UnknownModule = 1,
    NoChange = 2,
    CapReached = 3,
}
