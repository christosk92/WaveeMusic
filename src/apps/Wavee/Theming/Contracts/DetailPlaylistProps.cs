// ── Theming/Contracts/DetailPlaylistProps.cs — the props of the playlist sub-contract's views (themes plan 6.0, T9b) ───────────────────────────
//
// The playlist page keeps its model, its demands, the optimistic switch rule, every edit verb (`Spotify.PlaylistEdits`) and every permission decision
// (`EditableMetadata`, `CanAdministratePermissions`, `IsOwner`, `Live`): the hosts of `Entities\Playlist.UI.cs` (`CoverEditor`, `InlineEditor`, `OwnerBlock`,
// `AccessPanel`, `DaylistHost`, `RecsSection`, `RecRowHost`) own the signals and effects, decide what is shown and hand the structure's view the values and
// the verbs. The picture of an edit field (the pencil, the Save / Cancel buttons, the status chip), of the cover's scrim and of the owner run is the view's.
//
// EQUALITY. The hosts build a record per render and are themselves gated by a stamp of what they paint, so a record never compares equal to another
// instance (its members are rebuilt per render, and a view that skipped on an "equal" record would keep the old closures). The one plain-data record
// is the Tune panel's, which is built once per open.
//
// WRITABLE CELLS travel as `Func<Signal<T>>` (WVTH006 keeps a writable `Signal<T>` out of a props member): the control binds the cell once at mount.

using System.Runtime.CompilerServices;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace Wavee.Theming.Contracts;

/// <summary>The props of <see cref="ViewKeys.PlaylistCoverArt"/>: which face the cover paints and at what size.</summary>
/// <param name="Size">The cover's edge.</param>
/// <param name="DecodePx">The decode bucket (device pixels).</param>
/// <param name="Mosaic">Four tiles to compose as a 2 × 2 (the server-composed mosaic token, or the first four distinct member album covers), or null.</param>
/// <param name="Single">The one image to paint when there is no mosaic (null: the neutral placeholder).</param>
public sealed record PlaylistCoverArtProps(float Size, int DecodePx, string[]? Mosaic, string? Single)
{
    /// <inheritdoc />
    public bool Equals(PlaylistCoverArtProps? other) => ReferenceEquals(this, other);

    /// <inheritdoc />
    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
}

/// <summary>The props of <see cref="ViewKeys.PlaylistCoverEditor"/>: the art, and what the editable cover's gestures run. Picking, encoding, the optimistic
/// swap and the write are `PlaylistCover`'s (#155): the click only starts the picker on its own thread and returns.</summary>
/// <param name="Art">The face under the scrim (the host reads the model).</param>
/// <param name="Busy">A cover write is out for THIS playlist (the scrim stays up with its saving chip; read inside the render and the opacity binding).</param>
/// <param name="Pick">The click: start the picker.</param>
/// <param name="Apply">A JPEG / image file dropped on the cover: its path.</param>
public sealed record PlaylistCoverEditorProps(PlaylistCoverArtProps Art, Func<bool> Busy, Action Pick, Action<string> Apply)
{
    /// <inheritdoc />
    public bool Equals(PlaylistCoverEditorProps? other) => ReferenceEquals(this, other);

    /// <inheritdoc />
    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
}

/// <summary>The props of <see cref="ViewKeys.PlaylistEditor"/>: one editable text of the playlist (its name or its description).</summary>
/// <param name="Description">The description editor (else the title's).</param>
/// <param name="Size">The title's size the frame's rule chose / the description's width.</param>
/// <param name="LineHeight">The title's line height (NaN for the description).</param>
/// <param name="Text">The stored text.</param>
/// <param name="Editable">The viewer may edit the metadata (the pencil exists only then).</param>
/// <param name="Editing">The edit arm is up.</param>
/// <param name="Status">0 idle, 1 saving, 2 saved (the status chip leaves about 1.8 s after "Saved").</param>
/// <param name="Draft">The edit field's cell.</param>
/// <param name="Begin">Enter edit mode.</param>
/// <param name="Save">The Save button: commit the draft.</param>
/// <param name="Cancel">The Cancel button / Escape: leave edit mode, discarding the draft.</param>
/// <param name="Commit">Enter / blur: commit this text.</param>
/// <param name="Accent">The page accent (a rich-text link's ink; read only where a link paints).</param>
/// <param name="NavRoute">A rich-text anchor's route key → navigation.</param>
public sealed record PlaylistEditorProps(bool Description, float Size, float LineHeight, string Text, bool Editable, bool Editing, int Status,
                                         Func<Signal<string>> Draft, Action Begin, Action Save, Action Cancel, Action<string> Commit,
                                         Func<ColorF> Accent, Action<string> NavRoute)
{
    /// <inheritdoc />
    public bool Equals(PlaylistEditorProps? other) => ReferenceEquals(this, other);

    /// <inheritdoc />
    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
}

/// <summary>The props of <see cref="ViewKeys.PlaylistOwner"/>: the owner row of the rail and the hero (ONE composition for both arms).</summary>
/// <param name="Width">The line's measure (NaN: unconstrained).</param>
/// <param name="Pile">The collaborator pile is the leading arm (else the owner run).</param>
/// <param name="Faces">The pile's faces (at most four), without a click of their own.</param>
/// <param name="Overflow">How many collaborators the pile does not draw (its "+N").</param>
/// <param name="Label">The pile's label (the plural-aware count, "Collaborative", or the member's name).</param>
/// <param name="OwnerName">The owner's name ("" while the profile has not landed).</param>
/// <param name="OwnerImageUrl">The owner's avatar.</param>
/// <param name="OpenOwner">The owner's profile link (null: the run stays inert, like a local playlist's).</param>
/// <param name="Invite">The invite affordance exists (an owner who can administrate permissions, with a live edit path).</param>
/// <param name="OpenMembers">The pile's click / Down / F4: the collaborator flyout.</param>
/// <param name="OpenAccess">The invite button's click: the access panel.</param>
/// <param name="PileRealized">The pile's node (the flyout's anchor).</param>
/// <param name="InviteRealized">The invite button's node (the access panel's anchor).</param>
public sealed record PlaylistOwnerProps(float Width, bool Pile, IReadOnlyList<Controls.Face> Faces, int Overflow, string Label, string OwnerName,
                                        string? OwnerImageUrl, Action? OpenOwner, bool Invite, Action OpenMembers, Action OpenAccess,
                                        Action<NodeHandle> PileRealized, Action<NodeHandle> InviteRealized)
{
    /// <inheritdoc />
    public bool Equals(PlaylistOwnerProps? other) => ReferenceEquals(this, other);

    /// <inheritdoc />
    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
}

/// <summary>The props of <see cref="ViewKeys.PlaylistAccess"/>: what the panel shows and the verbs behind its call to action and its two switches. The
/// switches hold the user's INTENT until an authoritative answer agrees with it (`OptimisticSwitch`); the cells below are its two outputs.</summary>
/// <param name="Width">The panel's measure (the host's: the popup, or the dialog's content less its padding).</param>
/// <param name="Admin">The viewer can administrate permissions (the invite call to action exists).</param>
/// <param name="EditableMetadata">The viewer can edit the metadata (the two switches exist; with <paramref name="Admin"/>).</param>
/// <param name="InviteState">0 idle, 1 busy (a link is being made), 2 copied.</param>
/// <param name="IsPublic">The visibility switch shows Public (its caption says so).</param>
/// <param name="SavingCollab">A collaborative write is out.</param>
/// <param name="SavingPublic">A visibility write is out.</param>
/// <param name="Collaborative">The collaborative switch's cell.</param>
/// <param name="Public">The visibility switch's cell.</param>
/// <param name="CopyInvite">Create the invite link and copy it.</param>
/// <param name="OnCollaborative">The collaborative switch flipped.</param>
/// <param name="OnPublic">The visibility switch flipped.</param>
public sealed record PlaylistAccessProps(float Width, bool Admin, bool EditableMetadata, int InviteState, bool IsPublic, bool SavingCollab, bool SavingPublic,
                                         Func<Signal<bool>> Collaborative, Func<Signal<bool>> Public, Action CopyInvite,
                                         Action<bool> OnCollaborative, Action<bool> OnPublic)
{
    /// <inheritdoc />
    public bool Equals(PlaylistAccessProps? other) => ReferenceEquals(this, other);

    /// <inheritdoc />
    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
}

/// <summary>The props of <see cref="ViewKeys.PlaylistTune"/>: the options as the pure model's input, which one is selected and the pick verb (it closes the
/// dialog and writes). The model (`PlaylistTuneMenuModel`) decides which choices show and whether a reset row stands.</summary>
/// <param name="Width">The panel's measure (the dialog's content width).</param>
public sealed record PlaylistTuneProps(float Width, IReadOnlyList<TuneOption> Options, string? Selected, Action<string> Pick);

/// <summary>The props of <see cref="ViewKeys.PlaylistDaylist"/>: the strip's face this second. The clock (the wall-clock anchor re-anchored against the
/// frame clock, the one-second interval, the expiry timer, the rollover ladder) is the host's.</summary>
/// <param name="Phase">Idle (nothing to show), Counting, or Rolling (past the window: the ring stands in for the digits).</param>
/// <param name="Late">The rollover ladder gave up (a full static ring, "Your next daylist is running late"), else "Updating your daylist…".</param>
/// <param name="Caption">The "Next update at {time}" caption, or null (the clock mode shows the cells alone).</param>
/// <param name="Cells">The clock mode shows the flip cells.</param>
/// <param name="Hours">The hours left, clamped at 99 (two fixed cells).</param>
/// <param name="Minutes">The minutes left.</param>
/// <param name="Seconds">The seconds left.</param>
/// <param name="Compact">The rail's compact cells (10 × 20) rather than the hero's (13 × 28).</param>
/// <param name="Accent">The page accent thunk (the digits' ink; read in the view so a late grading re-tints it).</param>
public sealed record PlaylistDaylistProps(DaylistCountdown.Phase Phase, bool Late, string? Caption, bool Cells, int Hours, int Minutes, int Seconds,
                                          bool Compact, Func<ColorF> Accent)
{
    /// <inheritdoc />
    public bool Equals(PlaylistDaylistProps? other) => ReferenceEquals(this, other);

    /// <inheritdoc />
    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
}

/// <summary>The props of <see cref="ViewKeys.PlaylistRecs"/>: the Recommended section's header and its rows, in order.</summary>
/// <param name="Loading">The batch has not answered yet (a spinner stands in for the refresh).</param>
/// <param name="Empty">The batch answered with nothing ("No suggestions right now" beside the refresh).</param>
/// <param name="Refresh">Ask for a new batch.</param>
/// <param name="Rows">One per recommended track.</param>
public sealed record PlaylistRecsProps(bool Loading, bool Empty, Action Refresh, IReadOnlyList<PlaylistRecRowProps> Rows)
{
    /// <summary>The playlist has no songs: what a structure may lead the section with (C-Empty-Compact: "Nothing here yet", a line, and a field that
    /// finds songs to add). Null while the playlist has rows. Appended (contract 1.22).</summary>
    public PlaylistRecsLead? Lead { get; init; }

    /// <inheritdoc />
    public bool Equals(PlaylistRecsProps? other) => ReferenceEquals(this, other);

    /// <inheritdoc />
    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
}

/// <summary>The lead of an EMPTY playlist's Recommended section (<see cref="PlaylistRecsProps.Lead"/>, contract 1.22).</summary>
/// <param name="Find">The field's submit: look for songs to add (the core opens Search for the query).</param>
public sealed record PlaylistRecsLead(Action<string> Find);

/// <summary>The props of <see cref="ViewKeys.PlaylistRecRow"/>: one recommended track. Plain data with the section's two stable handlers (its own method
/// groups), so a section re-render that leaves a row alone re-renders nothing: the compiler's record equality is the gate.</summary>
/// <param name="Slot">The track's slot (the view reads the track table, so a title landing re-renders this row and not the section).</param>
/// <param name="Busy">THIS row's add is in flight (its button becomes a spinner).</param>
/// <param name="Add">The section's one add handler.</param>
/// <param name="Play">The section's one play handler: the row click plays the track (the deck's row toggles pause, like every list).</param>
public sealed record PlaylistRecRowProps(int Slot, bool Busy, Action<int> Add, Action<int> Play);
