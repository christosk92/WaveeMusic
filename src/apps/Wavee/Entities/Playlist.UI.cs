// ── Entities/Playlist.UI.cs ────────────────────────────────────────────────────────────────────────────────────────
// the playlist page's own controls: the cover (art · mosaic · editor), the inline title + description editors with their
// status row, the owner block (collaborator pile + member flyout · owner row · invite affordance), the invite & access
// panel, the daylist flip strip, the recommendations section, the Tune panel and the edit-error toasts
//
// Role: UI
// Owner: O
// Wave: 5
// Budget: 1400 lines
// Spec: ch 06 §9 (W14-W19, W24-W26, W10; §3 tokens; §5 motion)
//
// PROPS FREEZE AT MOUNT. Every component here takes the playlist SLOT (a handle never goes stale) plus geometry, keys on
// the GEOMETRY bucket (8 DIP, `Detail.VerticalLayout.BucketW`) and reads the model INSIDE its render — never model text
// in a key (ch 06 §9 "Traps"). The table counters it depends on are subscribed inside a `UseComputed` STAMP of exactly
// what it paints (W3-A3, the `RowFold` idiom of Album.Page.cs), so a publication that leaves the stamp equal never
// re-renders it. The daylist strip is the one identity-keyed control (a rollover is a new window).
//
// A11Y (ch 06 §9, fixed not ported): every clickable node here declares Role + Cursor + a name.
//
// T9b (themes plan 6.1): the PICTURES of these hosts are Classic's (`Themes\Classic\Structure\Views\Detail\ClassicPlaylist*.cs`; the keys and props are
// `Theming\Contracts\ViewKeys.DetailAlbumPlaylist.cs` and `DetailPlaylistProps.cs`). What stays here is each host's gate (the stamp over the tables it paints),
// its demands, its state (the edit mode, the draft and the status; the optimistic switches; the member set; the clock) and its verbs and decisions (which
// tiles the cover paints, the edit verbs, the permission verdicts); each builds one props record per render and mounts its key.

using System.Globalization;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using ViewSlots = FluentGpu.Theming.Slots;

namespace Wavee;

/// <summary>AN OPTIMISTIC SWITCH, AS A RULE (ch 06 §0.12 "optimistic, then honest", W17). The access panel's two
/// switches write through <c>Spotify.PlaylistEdits</c>, which flips the model at once and puts it back only if the
/// server refuses — and the settle then RE-READS the list. That re-read can land the pre-write value (it raced the
/// write on the server side, or it is a dealer echo of the older head), and a switch that simply mirrors the model
/// flips back on its own. So the switch does not mirror the model: it holds the user's INTENT until an authoritative
/// answer AGREES with it, and reverts exactly once, on a refusal.
/// <list type="bullet">
/// <item><see cref="Flip"/> — the click: the intent paints immediately and is held; the switch is busy.</item>
/// <item><see cref="Observe"/> — a table publication: accepted while it agrees with the intent (the write landed) or
/// while nothing is held; a DISAGREEING publication under a held intent is a read that raced the write and is recorded
/// but not painted.</item>
/// <item><see cref="Answered"/> — the write's own verdict: accepted keeps the intent held until the model catches up;
/// refused reverts ONCE, to the model the edit host has already put back.</item>
/// </list>
/// PURE — no table, no signal, no clock.</summary>
/// <param name="Model">The last value the tables published.</param>
/// <param name="Wanted">The value the user asked for (== <paramref name="Model"/> when nothing is held).</param>
/// <param name="Writing">A write is out: the row shows its busy chip and the switch is disabled.</param>
/// <param name="Holding">Paint <paramref name="Wanted"/>, not <paramref name="Model"/>.</param>
public readonly record struct OptimisticSwitch(bool Model, bool Wanted, bool Writing, bool Holding)
{
    /// <summary>What the switch paints.</summary>
    public bool Shown => Holding ? Wanted : Model;

    /// <summary>Is a write out for this switch?</summary>
    public bool Busy => Writing;

    /// <summary>Settled on <paramref name="model"/>: nothing held, nothing out.</summary>
    public static OptimisticSwitch At(bool model) => new(model, model, false, false);

    /// <summary>The user flipped it.</summary>
    public OptimisticSwitch Flip(bool wanted) => new(Model, wanted, true, true);

    /// <summary>The tables published <paramref name="model"/>.</summary>
    public OptimisticSwitch Observe(bool model)
        => Holding && model != Wanted ? this with { Model = model } : new(model, model, Writing, false);

    /// <summary>The write answered: <paramref name="ok"/>, against the model as it stands now.</summary>
    public OptimisticSwitch Answered(bool ok, bool model)
        => ok ? new(model, Wanted, false, model != Wanted) : At(model);
}

/// <summary>0.2.9 <c>PlaylistEditErrors</c>: the ONE raise — the mapped sentence and the Informational/Error split.</summary>
public static class PlaylistEditErrors
{
    public static void Raise(PlaylistMutationFailure kind, PlaylistEditVerb verb = PlaylistEditVerb.Generic)
        => Notify.Say(Loc.Get(PlaylistEditErrorKinds.KeyFor(kind, verb)),
                      PlaylistEditErrorKinds.IsInformational(kind) ? InfoBarSeverity.Informational : InfoBarSeverity.Error,
                      dedupeKey: "playlist.edit:" + (int)kind + ":" + (int)verb);
}

public readonly partial struct Playlist
{
    // ══ 0. shared geometry (ch 06 §3) ════════════════════════════════════════════════════════════════════════════════

    const float SavedHoldMs = 1800f;

    /// <summary>The popups' MEASURES: the access panel's and the Tune panel's content widths. The host that opens them owns the number (a dialog is that much
    /// wider than its content, <see cref="DialogChrome"/>); the view fills it (it travels on the props).</summary>
    const float AccessPanelWidth = 300f, TunePanelWidth = 336f, DialogChrome = 48f;

    /// <summary>How many collaborators the owner block's pile draws (its "+N" counts the rest).</summary>
    const int PileFaces = 4;

    /// <summary>Device-pixel decode for the playlist header cover: the larger of the laid-out DIP × scale and
    /// the detail artwork ladder, so a ~232 DIP header is never stuck at a 256-px thumbnail.</summary>
    public static int HeaderCoverDecodePx(float size, float scale)
    {
        int scaled = Design.ImageDecodeScale.For(size, scale);
        int ladder = Detail.VerticalLayout.ArtworkDecodePx(size);
        return Math.Max(scaled, ladder);
    }

    static string TitleOf(Playlist p) => p.Knows(PlaylistFields.Identity) ? Entities.Strings.Resolve(p.TitleId) : "";

    /// <summary>The page accent (the cover's chrome grading, else the payload accent, else the system accent) — read in a
    /// consumer's render so a late grading re-tints it (ch 06 §4). Delegates the LADDER itself to
    /// <see cref="Detail.AccentFor"/> (the one ladder with all three rungs) so a coverless playlist's chrome accent and
    /// its rich-text link accent agree; only the URL choice (keyed on <c>p.ImageId</c> alone, this type's own convention)
    /// and the <see cref="Palette.Watch"/> subscription — which must stay HERE, inside this tracked render, so a late
    /// grading re-tints — stay local.
    /// <para>Public, not internal: this assembly has no <c>InternalsVisibleTo</c>, so <c>Wavee.Tests</c> can only pin
    /// the delegation (a coverless playlist's chrome accent and its rich-text link accent must agree) through a public
    /// surface — the same reason <see cref="Detail.AccentFor"/> already is.</para></summary>
    public static ColorF AccentOf(Playlist p)
    {
        string? url = Controls.ArtUrl(p.ImageId);
        if (url is { Length: > 0 }) _ = Palette.Watch(url).Value;
        return Detail.AccentFor(url, p.Accent);
    }

    // ══ 1. THE COVER (W16) ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The frame's Cover slot: the editor for an editable Spotify playlist, else the art (item 3: saturation 1.18
    /// on BOTH arms, so the editable↔read-only flip is structural only). NOT keyed on the size: the size is a re-pushed prop, so a
    /// tier crossing that resizes the cover keeps the editor's node (owner rule: no tier, form or count in a key).</summary>
    public static Element CoverSlot(Playlist p, float size)
        => p.IsValid && p.EditableMetadata && p.Uri.Provider == EntityProvider.Spotify
            ? Embed.Comp(new CoverProps(p.Slot, size), static () => new CoverEditor()) with { Key = "pl-cover" }
            : CoverArt(p, size);

    /// <summary>The playlist's own cover (the `Playlist.CoverArt` view) — a provider <c>spotify:mosaic:</c> token (a profile card's cover-less public
    /// playlist) paints as the server-composed 2×2 — else a 2×2 mosaic of member album covers (≥ 4), else the first,
    /// else the neutral placeholder (0.2.9 <c>PlaylistPicker.CoverOf</c> minus the generated art).</summary>
    internal static Element CoverArt(Playlist p, float size, int decodePx = 0)
        => ViewSlots.View(ViewKeys.PlaylistCoverArt, CoverFace(p, size, decodePx));

    /// <summary>Which image the cover paints (the model's side of the picture): the server mosaic's four tiles, the art, the first four distinct member
    /// album covers, or the first one.</summary>
    static PlaylistCoverArtProps CoverFace(Playlist p, float size, int decodePx)
    {
        int px = decodePx > 0 ? decodePx : HeaderCoverDecodePx(size, 1f);
        var server = Controls.MosaicTiles(p.ImageId);                     // a profile card's server-composed 2×2
        if (server.Length == 4) return new PlaylistCoverArtProps(size, px, server.ToArray(), null);
        string? url = Controls.ArtUrl(p.ImageId);
        if (url is { Length: > 0 }) return new PlaylistCoverArtProps(size, px, null, url);
        var tiles = new string[4];
        int n = MosaicTiles(p, tiles);
        return n >= 4
            ? new PlaylistCoverArtProps(size, px, tiles, null)
            : new PlaylistCoverArtProps(size, px, null, n > 0 ? tiles[0] : null);
    }

    /// <summary>Up to four distinct member album covers, in membership order (0.2.9 <c>PlaylistSummary.MosaicTiles</c>).</summary>
    internal static int MosaicTiles(Playlist p, string[] into)
    {
        Span<int> albums = stackalloc int[into.Length];
        int n = 0;
        var slots = p.TrackSlots;
        for (int i = 0; i < slots.Length && n < into.Length; i++)
        {
            var t = new Track(slots[i]);
            int album = t.AlbumSlot;
            if (album <= Table.None || albums[..n].IndexOf(album) >= 0) continue;
            string? url = Controls.ArtUrl(t.ImageId);
            if (url is not { Length: > 0 }) continue;
            albums[n] = album;
            into[n++] = url;
        }
        return n;
    }

    sealed record CoverProps(int Slot, float Size);

    /// <summary>The editable cover (the `Playlist.CoverEditor` view: an always-mounted scrim whose OPACITY binds — hover · a live OS file drag anywhere ·
    /// saving — the file-drop target and the click-to-pick). The frame's framing box keeps the playlist drag source. Picking, encoding, the optimistic swap
    /// and the write are <see cref="PlaylistCover"/>'s (#155): the click only starts the picker on its own thread and returns.</summary>
    [CoreHost]
    sealed class CoverEditor : Component
    {
        int _slot;
        readonly Action _pick;
        readonly Action<string> _apply;
        readonly Func<bool> _busy;
        readonly Func<CoverStamp> _stamp;

        /// <summary>What the editor paints off the tables (W3-A3): the row (its own cover) and, for the mosaic, every
        /// member's version (the album cover a tile takes). The three counters are read inside the memo.</summary>
        readonly record struct CoverStamp(uint Epoch, uint Row, ulong Members);

        public CoverEditor()
        {
            _pick = Pick;
            _apply = path => PlaylistCover.Apply(new Playlist(_slot), path);
            _busy = () => PlaylistCover.BusySlot.Value == _slot;
            _stamp = Stamp;
        }

        CoverStamp Stamp()
        {
            uint epoch = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            _ = scope.Playlists.Changed.Value;
            _ = scope.Edges.PlaylistTracks.Changed.Value;
            _ = scope.Tracks.Changed.Value;
            var p = new Playlist(_slot);
            return p.IsValid ? new CoverStamp(epoch, p.Version, RowFold.Rows(scope.Tracks, p.TrackSlots)) : new CoverStamp(epoch, 0, RowFold.Seed);
        }

        public override Element Render()
        {
            var props = UseProps<CoverProps>();
            _slot = props.Slot;
            _ = UseComputed(_stamp).Value;
            float size = props.Size;
            float scale = UseContext(Viewport.Scale);
            int decodePx = HeaderCoverDecodePx(size, scale);
            return ViewSlots.View(ViewKeys.PlaylistCoverEditor,
                new PlaylistCoverEditorProps(CoverFace(new Playlist(props.Slot), size, decodePx), _busy, _pick, _apply));
        }

        void Pick() => PlaylistCover.Pick(new Playlist(_slot));
    }

    // ══ 3. THE INLINE EDITORS (W14, W15) ═════════════════════════════════════════════════════════════════════════════

    /// <summary>The frame's Title slot: ONE box across read↔edit whose height tweens (CardResize), the status row BELOW it
    /// — never inline, so the title's measure never moves (ch 06 §0.6). Read-only renders the same hero run.
    /// <para>NOT keyed on the size: the props are re-pushed, so the read arm follows the frame's title rule (which moves
    /// the size with the measure) without a remount — a remount would replay the read arm's fade-in at every size step
    /// of a rail drag. The edit field, whose font size freezes at mount, is keyed on its own size instead.</para></summary>
    public static Element TitleSlot(Playlist p, float size, float lineHeight)
        => Embed.Comp(new EditorProps(p.Slot, size, lineHeight, Description: false), static () => new InlineEditor())
            with { Key = "pl-title" };

    /// <summary>The frame's Description slot (the frame invokes it only while <c>Identity.EditableMetadata</c>). NOT keyed on the width: the props are
    /// re-pushed (the editor reads its measure every render), and a key built from a width bucket rebuilt the editor whenever a crossing moved its column
    /// (audit B9; the owner rule "a tier crossing keeps its parts").</summary>
    public static Element DescriptionSlot(Playlist p, float width)
        => Embed.Comp(new EditorProps(p.Slot, width, float.NaN, Description: true), static () => new InlineEditor())
            with { Key = "pl-desc" };

    sealed record EditorProps(int Slot, float Size, float LineHeight, bool Description);

    [CoreHost]
    sealed class InlineEditor : Component
    {
        readonly Signal<bool> _editing = new(false);
        readonly Signal<int> _status = new(0);                 // 0 idle · 1 saving · 2 saved
        readonly Signal<string> _draft = new("");
        string _startedWith = "";
        int _slot;
        bool _description;
        readonly Action _begin, _save, _cancel, _clearSaved, _takeIntent;
        readonly Action<string> _commit;
        readonly Func<RowStamp> _stamp;
        readonly Func<Signal<string>> _draftCell;
        readonly Func<ColorF> _accent;

        public InlineEditor()
        {
            _stamp = Stamp;
            _begin = Begin;
            _save = () => Commit(_draft.Peek());
            _cancel = () => _editing.Value = false;
            _commit = Commit;
            _clearSaved = () => { if (_status.Peek() == 2) _status.Value = 0; };
            _takeIntent = () => { if (!_description && PlaylistCreateIntent.Take(new Playlist(_slot).Uri.Text)) Begin(); };
            _draftCell = () => _draft;
            _accent = () => AccentOf(new Playlist(_slot));
        }

        /// <summary>The editor paints the row alone (title / description text, the caps): its version under the scope
        /// epoch is the whole gate (W3-A3).</summary>
        RowStamp Stamp()
        {
            uint epoch = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            _ = scope.Playlists.Changed.Value;
            return new RowStamp(_slot, epoch, RowFold.Version(scope.Playlists, _slot));
        }

        public override Element Render()
        {
            var props = UseProps<EditorProps>();
            _slot = props.Slot;
            _description = props.Description;
            _ = UseComputed(_stamp).Value;
            var p = new Playlist(props.Slot);
            bool editable = p.EditableMetadata;
            bool editing = _editing.Value && editable;
            int status = _status.Value;
            string text = _description ? Entities.Strings.Resolve(p.DescriptionId) : TitleOf(p);

            UseEffect(_takeIntent, DepKey.From(props.Slot));          // item 37: a create that navigated opens IN edit mode
            UseTimeout(_clearSaved, SavedHoldMs, DepKey.From(status)); // the "Saved" chip leaves ≈1.8 s later (frame clock)

            // The `Playlist.Editor` view: ONE box across read↔edit whose height tweens, the status row BELOW it (never inline, so the title's measure never moves).
            return ViewSlots.View(ViewKeys.PlaylistEditor, new PlaylistEditorProps(_description, props.Size, props.LineHeight, text, editable, editing, status,
                _draftCell, _begin, _save, _cancel, _commit, _accent, DetailVerbs.NavigateKey));
        }

        void Begin()
        {
            var p = new Playlist(_slot);
            if (!p.EditableMetadata) return;
            _startedWith = _description ? Entities.Strings.Resolve(p.DescriptionId) : TitleOf(p);
            _draft.Value = _startedWith;
            _editing.Value = true;
        }

        /// <summary>Enter / blur / Save. An empty (title) or unchanged trimmed value is discarded; the write carries the
        /// value as it stood when editing STARTED, so a refusal cannot restore another device's rename (§6.1).</summary>
        void Commit(string value)
        {
            if (!_editing.Peek()) return;
            _editing.Value = false;
            string next = (value ?? "").Trim();
            if (string.Equals(next, _startedWith.Trim(), StringComparison.Ordinal) || (!_description && next.Length == 0)) return;
            var status = _status;
            status.Value = 1;
            Action<bool> settled = ok => status.Value = ok ? 2 : 0;
            var p = new Playlist(_slot);
            if (_description) Spotify.PlaylistEdits.SetDescription(p, next, _startedWith, settled);
            else Spotify.PlaylistEdits.Rename(p, next, _startedWith, settled);
        }
    }

    // ══ 4. THE OWNER BLOCK (rail + hero attribution, W25, W29) ═══════════════════════════════════════════════════════

    /// <summary>The frame's Attribution slot: the collaborator pile (the frame's own predicate) or the owner row, each with
    /// the self-gating invite affordance. ONE composition for both arms (a decision: the report, W29).</summary>
    public static Element AttributionSlot(Playlist p, float width)
        // One key at every width: the width is a re-pushed prop (the block re-renders at the new one), not an identity: a key from the width bucket
        // rebuilt the owner row on every tier crossing that resized the column (1008, 1920, 3200).
        => Embed.Comp(new OwnerProps(p.Slot, width), static () => new OwnerBlock()) with { Key = "pl-owner" };

    sealed record OwnerProps(int Slot, float Width);

    [CoreHost]
    sealed class OwnerBlock : Component
    {
        readonly int[] _members = new int[64];
        int _memberCount, _slot;
        OverlayHandle? _membersHandle;
        NodeHandle _pileNode, _inviteNode;
        IOverlayService? _overlay;
        readonly Action _openMembers, _openAccess;
        readonly Action<NodeHandle> _pileRealized, _inviteRealized;
        readonly Func<NodeHandle> _pileAnchor, _inviteAnchor;
        readonly Func<OwnerStamp> _stamp;

        /// <summary>What the block paints (W3-A3): the row (collaborative flag, owner slot, caps), the owner's row (name,
        /// avatar), the collaborators' rows in order (the pile's faces and names — folded over the Users table), and
        /// the session's edit-path verdict (the invite affordance, item 58).</summary>
        readonly record struct OwnerStamp(uint Epoch, uint Row, int OwnerSlot, uint Owner, ulong People, bool EditsLive);

        public OwnerBlock()
        {
            _openMembers = OpenMembers;
            _openAccess = OpenAccess;
            _pileRealized = n => _pileNode = n;
            _inviteRealized = n => _inviteNode = n;
            _pileAnchor = () => _pileNode;
            _inviteAnchor = () => _inviteNode;
            _stamp = Stamp;
        }

        OwnerStamp Stamp()
        {
            uint epoch = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            _ = scope.Playlists.Changed.Value;
            _ = scope.Users.Changed.Value;
            _ = scope.Edges.PlaylistTracks.Changed.Value;
            bool editsLive = EditsLiveNow();
            var p = new Playlist(_slot);
            if (!p.IsValid) return new OwnerStamp(epoch, 0, Table.None, 0, RowFold.Seed, editsLive);
            Span<int> people = stackalloc int[64];
            int n = p.CollaboratorSlots(people);
            var owner = p.Owner;
            return new OwnerStamp(epoch, p.Version, owner.Slot, RowFold.Version(scope.Users, owner.Slot),
                                  RowFold.Rows(scope.Users, people[..n]), editsLive);
        }

        public override Element Render()
        {
            var props = UseProps<OwnerProps>();
            _slot = props.Slot;
            var stamp = UseComputed(_stamp).Value;
            _overlay = UseContext(Overlay.Service);
            var p = new Playlist(props.Slot);
            _memberCount = p.CollaboratorSlots(_members);
            bool pile = Detail.Text.ShowCollaborators(_memberCount, p.IsCollaborative);
            // Item 58: the invite affordance also needs a live Spotify edit path (the stamp carries the session phase).
            bool invite = stamp.EditsLive && p.Live && p.IsOwner && p.CanAdministratePermissions;

            // The `Playlist.Owner` view draws the pile or the owner run in its reserved row; what it is handed is the arm the decision picked.
            IReadOnlyList<Controls.Face> faces = [];
            int overflow = 0;
            string label = "", ownerName = "";
            string? ownerImage = null;
            Action? openOwner = null;
            if (pile)
            {
                int visible = Math.Min(PileFaces, _memberCount);
                var drawn = new Controls.Face[visible];
                for (int i = 0; i < visible; i++)
                {
                    var u = new User(_members[i]);
                    drawn[i] = new Controls.Face(NameOf(u), Controls.ArtUrl(u.ImageId));
                }
                faces = drawn;
                overflow = Math.Max(0, _memberCount - PileFaces);
                // The keys the 0.2.9 code hard-coded around (ch 06 §9): plural-aware count · open · the member's name.
                label = _memberCount >= 2 ? Strings.Detail.CollabCount(_memberCount)
                      : p.IsCollaborative ? Loc.Get(Strings.Detail.CollabOpen) : NameOf(new User(_members[0]));
            }
            else
            {
                var owner = p.Owner;
                // The local-files playlist is owned by this computer, not by a person: it states "On this device" (the page's own owner words)
                // with no portrait and no profile to open, whatever user row the store holds for it.
                bool local = p.Slot == LocalFiles.Slot;
                ownerName = local ? Loc.Get("localFile.onThisDevice") : NameOf(owner);
                ownerImage = local ? null : Controls.ArtUrl(owner.ImageId);
                // The owner opens their profile (#161): an owner row that cannot route (an invalid handle) stays the plain, inert run it was.
                var profile = local ? Shell.Route.None : ProfileRoute.For(owner);
                openOwner = profile.IsNone ? null : () => Shell.GoTo(profile);
            }
            return ViewSlots.View(ViewKeys.PlaylistOwner, new PlaylistOwnerProps(props.Width, pile, faces, overflow, label, ownerName, ownerImage, openOwner,
                invite, _openMembers, _openAccess, _pileRealized, _inviteRealized));
        }

        /// <summary>THE LATCHED OPEN (W17). The overlay host FOLLOWS a node anchor: <c>AfterAnimations</c> re-places an
        /// open, node-anchored entry whenever the resolved anchor rect drifts by more than half a DIP. This popup is
        /// anchored to a button that its OWN switches move (the eyebrow and the owner row re-read the same row), so the
        /// panel used to slide out from under the cursor mid-interaction. Opening it against a RECT captured at open
        /// time takes it out of that follow entirely (the host skips rect-anchored entries) — the panel keeps the
        /// placement it opened with for the life of the interaction, and only this popup: nothing global is disabled.
        /// The button is still passed as the OWNER, so the panel still cascade-closes and still dies with its page.</summary>
        void OpenAccess()
        {
            if (Controls.IsNullOverlay(_overlay)) return;
            var scene = Context.Scene;
            RectF latched = scene is not null && !_inviteNode.IsNull && scene.IsLive(_inviteNode) ? scene.AbsoluteRect(_inviteNode) : default;
            OpenAccessPanel(_overlay, new Playlist(_slot), _inviteAnchor, latched, FlyoutPlacement.BottomEdgeAlignedLeft);
        }

        void OpenMembers()
        {
            var overlay = _overlay;
            if (Controls.IsNullOverlay(overlay) || _memberCount == 0) return;
            if (_membersHandle is { IsOpen: true } open) { open.Close(); return; }
            OverlayHandle? handle = null;
            var rows = new PersonFlyoutRow[_memberCount];
            for (int i = 0; i < rows.Length; i++)
            {
                var u = new User(_members[i]);
                // A member opens their profile (#161); the route is built at click time so a name that landed while the flyout was open rides on it,
                // and an invalid handle only closes the flyout.
                rows[i] = new PersonFlyoutRow(NameOf(u), Controls.ArtUrl(u.ImageId),
                    () => { handle?.Close(); var profile = ProfileRoute.For(u); if (!profile.IsNone) Shell.GoTo(profile); });
            }
            var props = new PersonFlyoutProps(rows);
            handle = overlay.Open(_pileAnchor, () => ViewSlots.View(ViewKeys.PlaylistMembersFlyout, props), FlyoutPlacement.BottomEdgeAlignedLeft,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup) { ConstrainToRootBounds = false });
            _membersHandle = handle;
        }
    }

    /// <summary>A user's display name, or "" while the profile has not landed yet. 0.2.9 fell back to the raw
    /// username carved out of the uri when the name was still blank; that fallback is DEAD for a Spotify user in
    /// 0.3 — a user row's identity is always the TEXT form (`User.cs` ch 1), but nothing on the path from an
    /// `ownerV2`/`addedBy` mention to this row guarantees <c>u.Uri.Text</c> resolves to anything but "" for a bare
    /// mention, so <c>EntityUri.IdOf</c> had nothing to carve and the branch silently answered "" anyway — a dead
    /// fallback that LOOKED like it was doing something is worse than an honest blank. Deleted outright rather
    /// than kept as a no-op (no legacy paths): the real fix is <c>Spotify.Decode.Pathfinder</c>'s User arm no
    /// longer sealing Identity on a nameless mention, so <c>Entities.Ensure</c> asks the profile again and this
    /// resolves to the true name once it lands, instead of forever answering a placeholder that was never right.
    /// <para>Public, not internal: this assembly has no <c>InternalsVisibleTo</c> (see <see cref="AccentOf"/>), so
    /// <c>Wavee.Tests</c> can only pin the empty-fallback decision through a public surface.</para></summary>
    public static string NameOf(User u) => u.IsValid ? Entities.Strings.Resolve(u.NameId) : "";

    // ══ 5. INVITE & ACCESS (W17) ═════════════════════════════════════════════════════════════════════════════════════

    /// <inheritdoc cref="OpenAccessPanel(IOverlayService, Playlist, Func{NodeHandle}?, RectF, FlyoutPlacement)"/>
    internal static void OpenAccessPanel(IOverlayService overlay, Playlist p, Func<NodeHandle>? anchor, FlyoutPlacement placement)
        => OpenAccessPanel(overlay, p, anchor, default, placement);

    /// <summary>Open the access panel at <paramref name="latched"/> — the anchor's window rect AS IT STOOD when the
    /// user clicked (see <c>OwnerBlock.OpenAccess</c>) — with <paramref name="anchor"/> as the owner for the cascade
    /// and the orphan prune. With neither (the ⋯ menu is gone by invoke time) it opens as a centred dialog, the
    /// picker's own reason.</summary>
    internal static void OpenAccessPanel(IOverlayService overlay, Playlist p, Func<NodeHandle>? anchor, RectF latched,
                                         FlyoutPlacement placement)
    {
        int slot = p.Slot;
        OverlayHandle? handle = null;
        Action close = () => handle?.Close();
        var options = new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
            { ConstrainToRootBounds = false };
        if (anchor is not null && !latched.IsEmpty)
            handle = overlay.OpenAt(() => latched, () => Embed.Comp(new AccessProps(slot, close), static () => new AccessPanel()),
                placement, options, anchor);
        else if (anchor is not null)
            handle = overlay.Open(anchor, () => Embed.Comp(new AccessProps(slot, close), static () => new AccessPanel()), placement, options);
        else
            handle = ContentDialog.Show(overlay, d =>
            {
                d.Title = Loc.Get(Strings.Detail.Edit.InviteCollaborators);
                d.PrimaryText = "";
                d.CloseText = Loc.Get(Strings.Common.Close);
                d.DefaultButton = ContentDialog.DefaultBtn.Close;
                OverlayFit.Dialog(d, AccessPanelWidth + DialogChrome);
                d.Content = Embed.Comp(new AccessProps(slot, close), static () => new AccessPanel());
            });
    }

    sealed record AccessProps(int Slot, Action Close)
    {
        public bool Equals(AccessProps? o) => o is not null && o.Slot == Slot;
        public override int GetHashCode() => Slot;
    }

    [CoreHost]
    sealed class AccessPanel : Component
    {
        readonly Signal<int> _invite = new(0);                 // 0 idle · 1 busy · 2 copied
        // What the two switches PAINT and whether they are busy — the signals the render and ToggleSwitch read. The
        // decision behind them is OptimisticSwitch's; these are only its two outputs.
        readonly Signal<bool> _collaborative = new(false), _public = new(false);
        readonly Signal<bool> _savingCollab = new(false), _savingPublic = new(false);
        OptimisticSwitch _collabState, _publicState;
        int _slot;
        readonly Action _copy, _sync;
        readonly Action<bool> _onCollaborative, _onPublic;
        readonly Func<Signal<bool>> _collaborativeCell, _publicCell;

        public AccessPanel()
        {
            _copy = CopyInvite;
            _collaborativeCell = () => _collaborative;
            _publicCell = () => _public;
            _onCollaborative = OnCollaborative;
            _onPublic = OnPublic;
            // Every table publication goes through the rule: it is accepted while it agrees with what the user asked
            // for, and a settle's re-read that still carries the pre-write value cannot flip the switch back (W17).
            _sync = () =>
            {
                var p = new Playlist(_slot);
                SetCollab(_collabState.Observe(p.IsCollaborative));
                SetPublic(_publicState.Observe(p.IsPublic));
            };
        }

        void SetCollab(OptimisticSwitch next)
        {
            _collabState = next;
            _collaborative.Value = next.Shown;
            _savingCollab.Value = next.Busy;
        }

        void SetPublic(OptimisticSwitch next)
        {
            _publicState = next;
            _public.Value = next.Shown;
            _savingPublic.Value = next.Busy;
        }

        public override Element Render()
        {
            var props = UseProps<AccessProps>();
            _slot = props.Slot;
            _ = Entities.ScopeEpoch.Value;
            _ = Entities.Current.Playlists.Changed.Value;
            var p = new Playlist(props.Slot);
            UseLayoutEffect(_sync, DepKey.From((int)p.Version));
            int invite = _invite.Value;
            bool admin = p.CanAdministratePermissions;
            bool isPublic = _public.Value;
            bool editableMetadata = admin && p.EditableMetadata;
            // The two switches' busy cells are read only where the switches exist (a tracked read each).
            bool savingCollab = editableMetadata && _savingCollab.Value;
            bool savingPublic = editableMetadata && _savingPublic.Value;

            // The `Playlist.Access` view: the hint, the copy-invite call to action and — for an administrator who can edit the metadata — the two switches.
            return ViewSlots.View(ViewKeys.PlaylistAccess, new PlaylistAccessProps(AccessPanelWidth, admin, p.EditableMetadata, invite, isPublic, savingCollab,
                savingPublic, _collaborativeCell, _publicCell, _copy, _onCollaborative, _onPublic));
        }

        void CopyInvite()
        {
            var state = _invite;
            state.Value = 1;
            int slot = _slot;
            Spotify.PlaylistEdits.CreateInvite(new Playlist(slot), url =>
            {
                if (url is null) { state.Value = 0; return; }
                if (Actions.Services.Clipboard is { } copy)
                {
                    copy(url);
                    state.Value = 2;
                    Entities.Ensure(new Playlist(slot), PlaylistFields.Capabilities | PlaylistFields.Visibility);   // 0.2.9 re-read here
                }
                else { Actions.Services.OpenExternal?.Invoke(url); state.Value = 0; }
            });
        }

        void OnCollaborative(bool on)
        {
            int slot = _slot;
            SetCollab(_collabState.Flip(on));
            Spotify.PlaylistEdits.SetCollaborative(new Playlist(slot), on,
                ok => SetCollab(_collabState.Answered(ok, new Playlist(slot).IsCollaborative)));
        }

        void OnPublic(bool on)
        {
            int slot = _slot;
            SetPublic(_publicState.Flip(on));
            Spotify.PlaylistEdits.SetVisibility(new Playlist(slot), on,
                ok => SetPublic(_publicState.Answered(ok, new Playlist(slot).IsPublic)));
        }
    }

    // ══ 6. THE DAYLIST STRIP (W26) ═══════════════════════════════════════════════════════════════════════════════════

    /// <summary>The frame's Pulse slot: HH:MM:SS flip cells (10×20 compact / 13×28 hero) + "Next update at {time}", or
    /// either one alone (Settings ▸ Appearance ▸ Daylist clock, <see cref="DaylistClockShows"/>, #185). Keyed on the window
    /// — a rollover is a new strip.</summary>
    public static Element DaylistStrip(Playlist p, bool compact, Func<ColorF> accent)
        => Embed.Comp(new DaylistProps(p.DaylistExpiresAt, compact, accent), static () => new DaylistHost())
            with { Key = "daylist:" + p.Slot + ":" + p.DaylistExpiresAt + ":" + (compact ? 1 : 0) };

    sealed record DaylistProps(int ExpiresAt, bool Compact, Func<ColorF> Accent)
    {
        public bool Equals(DaylistProps? o) => o is not null && o.ExpiresAt == ExpiresAt && o.Compact == Compact;
        public override int GetHashCode() => HashCode.Combine(ExpiresAt, Compact);
    }

    /// <summary>The strip (the `Playlist.Daylist` view). ch 06 §0.15: a wall-clock sample anchored against the frame clock, re-anchored on
    /// <see cref="DaylistCountdown"/>'s cadence (a stalled frame clock across sleep must not leave the digits behind).
    /// Past the window the strip is Rolling: the 1 s interval stops and an indeterminate ring replaces the digits
    /// (the view's Rolling face) until the new window remounts the strip. Hours clamp at 99 (two fixed cells).
    /// <para>The clock MODE (<see cref="Prefs.Appearance.DaylistClock"/>, read here so a pick re-renders the strip) picks
    /// the cells, the caption or both. Without the cells nothing changes per second, so the 1 s interval never runs: a
    /// ONE-SHOT at the window's end (<see cref="DaylistClockShows.ExpiryDelayMs"/>) is what still swaps in the Rolling face
    /// on time. It re-arms when the mode changes, and re-checks the wall clock when it fires, so a timer that landed early
    /// re-arms rather than leaving the caption up past the window.</para></summary>
    [CoreHost]
    sealed class DaylistHost : Component
    {
        readonly Signal<int> _tick = new(0);
        long _anchorUnixMs, _anchorFrameMs, _lastTickFrameMs;
        int _lastTick;
        bool _anchored;
        int _labelFor;
        string? _nextLabel;
        long _expiresMs;
        TimerHandle _expiry;
        readonly Action _onTick, _onExpiry;

        public DaylistHost()
        {
            _onTick = () => _tick.Value++;
            _onExpiry = OnExpiry;
        }

        /// <summary>The window's end, per the WALL clock: re-render once (the tick bump re-anchors and flips the face), or
        /// re-arm for the rest if the timer landed early.</summary>
        void OnExpiry()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (DaylistCountdown.PhaseOf(_expiresMs, now) == DaylistCountdown.Phase.Counting)
                _expiry.RestartIn(DaylistClockShows.ExpiryDelayMs(_expiresMs, now));
            else _tick.Value++;
        }

        public override Element Render()
        {
            var props = UseProps<DaylistProps>();
            var mode = Prefs.Appearance.DaylistClock();
            int tick = _tick.Value;
            long frameNow = FrameTime.NowMs;
            long sinceTick = frameNow - _lastTickFrameMs;
            if (tick != _lastTick) { _lastTick = tick; _lastTickFrameMs = frameNow; }
            if (!_anchored || DaylistCountdown.NeedsReanchor(tick, sinceTick))
            {
                _anchored = true;
                _anchorUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _anchorFrameMs = frameNow;
                if (_lastTickFrameMs == 0) _lastTickFrameMs = frameNow;
            }
            long expiresMs = _expiresMs = props.ExpiresAt * 1000L;
            long nowMs = DaylistCountdown.Now(_anchorUnixMs, _anchorFrameMs, frameNow);
            var phase = DaylistCountdown.PhaseOf(expiresMs, nowMs);
            // Past the window nothing counts: the tick stops, and the ladder's signal (read here, so it re-renders this
            // strip) decides what stands in for the digits.
            var face = DaylistClockFace.Of(phase, Home.Feeds.RolloverState.Value);
            UseInterval(_onTick, 1000f, enabled: DaylistClockShows.SecondTick(mode, phase));
            _expiry = UseTimeout(_onExpiry, DaylistClockShows.ExpiryDelayMs(expiresMs, nowMs), DepKey.From(props.ExpiresAt, (int)mode));
            // Idle: nothing to show. Rolling: the ring stands in for the digits ("Updating your daylist…", or — once the ladder gave up — "running late").
            if (phase != DaylistCountdown.Phase.Counting)
                return Mount(props, phase, face == DaylistClockFace.Face.Late, null, false, 0, 0, 0);

            string? caption = null;
            if (DaylistClockShows.UpdateTime(mode))
            {
                if (_nextLabel is null || _labelFor != props.ExpiresAt)
                {
                    _labelFor = props.ExpiresAt;
                    _nextLabel = Strings.Home.NextUpdateAt(DateTimeOffset.FromUnixTimeSeconds(props.ExpiresAt).ToLocalTime().ToString("t", CultureInfo.CurrentCulture));
                }
                caption = _nextLabel;
            }
            bool cells = DaylistClockShows.Countdown(mode);
            if (!cells) return Mount(props, phase, false, caption, false, 0, 0, 0);

            long left = DaylistCountdown.RemainingMs(expiresMs, nowMs) / 1000L;
            int hours = (int)Math.Min(99L, left / 3600), minutes = (int)(left / 60 % 60), seconds = (int)(left % 60);
            return Mount(props, phase, false, caption, true, hours, minutes, seconds);
        }

        static Element Mount(DaylistProps props, DaylistCountdown.Phase phase, bool late, string? caption, bool cells, int hours, int minutes, int seconds)
            => ViewSlots.View(ViewKeys.PlaylistDaylist,
                new PlaylistDaylistProps(phase, late, caption, cells, hours, minutes, seconds, props.Compact, props.Accent));
    }

    // ══ 7. RECOMMENDED SONGS (W10; ch 04 items 56 / 71) ══════════════════════════════════════════════════════════════

    /// <summary><c>TableProfile.Recommendations</c>: the table's ONE appended section. Its MOUNT is the first fetch; a
    /// spinner while asked, "No suggestions right now" beside the refresh after an empty reply, the rows with a [+]
    /// otherwise; the batch refills itself once the user has added all of it.</summary>
    public static Element RecommendationsSection(Playlist p)
        => Embed.Comp(new RecsProps(p.Slot), static () => new RecsSection()) with { Key = "pl-recs:" + p.Slot };

    sealed record RecsProps(int Slot);

    [CoreHost]
    sealed class RecsSection : Component
    {
        readonly Signal<int> _adding = new(0);
        bool _hadRows;
        int _slot;
        readonly Action _demand, _refresh;
        readonly Action<int> _add, _play;
        readonly Func<RecsStamp> _stamp;

        /// <summary>What the section paints (W3-A3): the edge's state and failure (the header's spinner / "No
        /// suggestions") and its version — which rows, in which order. A row paints its own facts (title, artists, art,
        /// duration) from its own subscription (its `Playlist.RecRow` view slot), so a track edge re-renders one row and never
        /// the section.</summary>
        readonly record struct RecsStamp(uint Epoch, EdgeState State, bool Failed, uint Edge, bool ListEmpty);

        /// <summary>The empty playlist's lead (C-Empty-Compact): its field looks for songs to add, through Search.</summary>
        static readonly PlaylistRecsLead s_lead = new(static query =>
        {
            string q = query.Trim();
            if (q.Length > 0) Shell.GoTo(new Shell.Route(Shell.RouteKind.Search, default, Entities.Strings.Intern(q)));
        });

        public RecsSection()
        {
            _demand = Demand;
            _refresh = () => Spotify.PlaylistEdits.Extend(new Playlist(_slot));
            _add = AddRecommendation;
            _play = PlayRecommendation;
            _stamp = Stamp;
        }

        RecsStamp Stamp()
        {
            uint epoch = Entities.ScopeEpoch.Value;
            var recs = Entities.Current.Edges.PlaylistRecs;
            _ = recs.Changed.Value;
            _ = Entities.Current.Edges.PlaylistTracks.Changed.Value;   // the lead follows the list emptying and filling
            var p = new Playlist(_slot);
            if (!p.IsValid) return new RecsStamp(epoch, EdgeState.Unknown, false, 0, false);
            return new RecsStamp(epoch, recs.State(p.Slot), recs.IsFailed(p.Slot), recs.Version(p.Slot),
                p.RowsState(0) == PlaylistRowsState.Empty);
        }

        /// <summary>The "+" of one row: that track is "adding" (its button becomes a spinner) until the write settles.</summary>
        void AddRecommendation(int track)
        {
            var adding = _adding;
            adding.Value = track;
            Spotify.PlaylistEdits.AddRecommendation(new Playlist(_slot), new Track(track),
                _ => { if (adding.Peek() == track) adding.Value = 0; });
        }

        /// <summary>The row click: play the track (the deck's row toggles pause, like every list).</summary>
        void PlayRecommendation(int track)
        {
            var t = new Track(track);
            Track.Invoke(t, () => Playback.PlayContext(t.Id));
        }

        public override Element Render()
        {
            var props = UseProps<RecsProps>();
            _slot = props.Slot;
            var stamp = UseComputed(_stamp).Value;
            var scope = Entities.Current;
            int adding = _adding.Value;
            UseEffect(_demand);

            var recs = scope.Edges.PlaylistRecs;
            var p = new Playlist(props.Slot);
            var slots = p.RecommendationSlots;
            bool loading = recs.State(p.Slot) == EdgeState.Unknown && !recs.IsFailed(p.Slot);
            bool empty = !loading && slots.Length == 0;

            // The `Playlist.Recs` view: the header over one slot per row (a row reads the track table, so a title landing re-renders that row and not the
            // section; a row's props are plain data with this section's two stable handlers, so an untouched row re-renders nothing).
            var rows = new PlaylistRecRowProps[slots.Length];
            for (int i = 0; i < rows.Length; i++) rows[i] = new PlaylistRecRowProps(slots[i], adding == slots[i], _add, _play);
            return ViewSlots.View(ViewKeys.PlaylistRecs, new PlaylistRecsProps(loading, empty, _refresh, rows) { Lead = stamp.ListEmpty ? s_lead : null });
        }

        void Demand()
        {
            _ = Entities.ScopeEpoch.Value;
            var recs = Entities.Current.Edges.PlaylistRecs;
            _ = recs.Changed.Value;
            var p = new Playlist(_slot);
            if (!p.IsValid) return;
            int n = recs.Count(p.Slot);
            if (n > 0) _hadRows = true;
            bool firstAsk = recs.State(p.Slot) == EdgeState.Unknown && !recs.WasAsked(p.Slot, 0) && !recs.IsFailed(p.Slot);
            bool refill = recs.State(p.Slot) == EdgeState.Complete && n == 0 && _hadRows;
            if (!firstAsk && !refill) return;
            _hadRows = false;
            Spotify.PlaylistEdits.Extend(p);
        }
    }

    // ══ 8. TUNE (W24) ════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary><c>TableProfile.Tune</c> hands no anchor, so the panel opens as a centred dialog (the picker's reason). The
    /// content is W24's: the accent tile header, the choices (the current one disabled with "Current" trailing) and
    /// "Reset tuning" behind a separator ONLY while a tuning is selected (item 56).</summary>
    internal static void OpenTune(IOverlayService overlay, Playlist p)
    {
        var options = TuneOptionsOf(p);
        string? selected = p.TuningSelectedId.IsEmpty ? null : Entities.Strings.Resolve(p.TuningSelectedId);
        OverlayHandle? handle = null;
        Action<string> pick = id =>
        {
            handle?.Close();
            Spotify.PlaylistEdits.Tune(p, id, null);
        };
        handle = ContentDialog.Show(overlay, d =>
        {
            d.Title = Loc.Get(Strings.Detail.Tuning.FlyoutTitle);
            d.PrimaryText = "";
            d.CloseText = Loc.Get(Strings.Common.Close);
            d.DefaultButton = ContentDialog.DefaultBtn.Close;
            OverlayFit.Dialog(d, TunePanelWidth + DialogChrome);
            d.Content = ViewSlots.View(ViewKeys.PlaylistTune, new PlaylistTuneProps(TunePanelWidth, options, selected, pick));   // the `Playlist.Tune` view
        });
    }

    /// <summary>The Tune options as the pure model's input (cold: the panel's open).</summary>
    internal static List<TuneOption> TuneOptionsOf(Playlist p)
    {
        var rows = p.TuningOptions;
        var list = new List<TuneOption>(rows.Length);
        for (int i = 0; i < rows.Length; i++)
        {
            string label = Entities.Strings.Resolve(rows[i].DisplayName);
            list.Add(new TuneOption(Entities.Strings.Resolve(rows[i].Identifier), label.Length == 0 ? null : label, (TuningOptionKind)rows[i].Kind));
        }
        return list;
    }
}
