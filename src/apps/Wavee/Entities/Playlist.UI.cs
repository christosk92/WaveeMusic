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

using System.Globalization;
using FluentGpu;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.WindowsApi.Dialogs;
using static FluentGpu.Dsl.Ui;

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

    const int CoverDecodePx = 256;
    const float CoverSaturation = 1.18f;
    const float PencilBox = 20f;
    const float EditButtonH = 32f;
    const float InviteRoundEdge = 28f, InviteWideFrom = 260f;
    /// <summary>THE RESERVED OWNER ROW (W17/W29). Its two arms are different sizes — the owner run is a 24 DIP portrait,
    /// the collaborator pile is a 32 DIP framed face (<c>Controls.FaceOuter</c>) — and the invite pill between them is
    /// 28. Flipping "Collaborative" swaps the arms, so an unreserved row changed height AND width mid-interaction: the
    /// Play pill stepped down, the invite button stepped sideways, and the access flyout anchored to that button moved
    /// under the cursor. One height for both arms, and a grown leading arm so the invite pill keeps its x.</summary>
    const float OwnerRowHeight = Controls.FaceOuter;
    const float AccessPanelW = 300f, TunePanelW = 336f;
    const float SavedHoldMs = 1800f;
    static readonly ColorF ScrimHover = ColorF.FromRgba(0, 0, 0, 133);    // #000 @ .52
    static readonly ColorF ScrimDrop = ColorF.FromRgba(0, 0, 0, 173);     // #000 @ .68
    static readonly ColorF OnScrim = ColorF.FromRgba(255, 255, 255, 255);  // image overlays never use theme ink (§4)

    /// <summary>The 8-DIP geometry bucket every editor key folds (a sub-pixel resize must not remount an editor).</summary>
    static int Bucket(float w) => float.IsFinite(w) ? (int)Detail.VerticalLayout.BucketW(w) : -1;

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
    /// on BOTH arms, so the editable↔read-only flip is structural only).</summary>
    public static Element CoverSlot(Playlist p, float size)
        => p.IsValid && p.EditableMetadata && p.Uri.Provider == EntityProvider.Spotify
            ? Embed.Comp(new CoverProps(p.Slot, size), static () => new CoverEditor()) with { Key = "pl-cover:" + (int)size }
            : CoverArt(p, size);

    /// <summary>The playlist's own cover, else a 2×2 mosaic of member album covers (≥ 4), else the first, else the
    /// neutral placeholder (0.2.9 <c>PlaylistPicker.CoverOf</c> minus the generated art).</summary>
    internal static Element CoverArt(Playlist p, float size, int decodePx = CoverDecodePx)
    {
        string? url = Controls.ArtUrl(p.ImageId);
        if (url is { Length: > 0 }) return Controls.Artwork(url, size, size, Radii.Card, decodePx: decodePx, saturation: CoverSaturation);
        var tiles = new string[4];
        int n = MosaicTiles(p, tiles);
        return n >= 4 ? Controls.Mosaic(tiles, size, size, Radii.Card)
             : Controls.Artwork(n > 0 ? tiles[0] : null, size, size, Radii.Card, decodePx: decodePx, saturation: CoverSaturation);
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

    /// <summary>The editable cover: an always-mounted scrim whose OPACITY binds (hover · a live OS file drag anywhere ·
    /// saving), the file-drop target and the click-to-pick. The frame's framing box keeps the playlist drag source.</summary>
    sealed class CoverEditor : Component
    {
        readonly Signal<bool> _hovered = new(false), _dropOver = new(false), _saving = new(false);
        int _slot;
        readonly Action _pick;
        readonly Func<CoverStamp> _stamp;

        /// <summary>What the editor paints off the tables (W3-A3): the row (its own cover) and, for the mosaic, every
        /// member's version (the album cover a tile takes). The three counters are read inside the memo.</summary>
        readonly record struct CoverStamp(uint Epoch, uint Row, ulong Members);

        public CoverEditor()
        {
            _pick = Pick;
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
            var drag = UseDragState();
            bool fileDrag = drag.Active && drag.Payload is FileDropData;
            bool saving = _saving.Value, over = _dropOver.Value;
            float size = props.Size;

            Element overlayBody = saving
                ? StatusChip(saved: false)
                : new BoxEl
                {
                    Direction = 1, Gap = 6f, AlignItems = FlexAlign.Center, HitTestVisible = false,
                    Children =
                    [
                        Icon(Icons.Camera, 32f, OnScrim),
                        new TextEl(Loc.Get(over ? Strings.Detail.Edit.DropCover : Strings.Detail.Edit.ChangeCover))
                        {
                            Size = 12f, Weight = 600, Color = OnScrim, Width = 120f, MaxLines = 2,
                            Wrap = TextWrap.WrapWholeWords, Trim = TextTrim.CharacterEllipsis,
                        },
                    ],
                };
            var hovered = _hovered;
            var dropOver = _dropOver;
            var savingSig = _saving;
            return new BoxEl
            {
                ZStack = true, Width = size, Height = size, ClipToBounds = true, Corners = CornerRadius4.All(Radii.Card),
                Role = AutomationRole.Button, Cursor = CursorId.Hand, OnClick = _pick,
                OnHoverMove = _ => { if (!hovered.Peek()) hovered.Value = true; },
                OnPointerExit = () => hovered.Value = false,
                DropTarget = new DropTargetSpec([DropKinds.Files],
                    OnEnter: _ => dropOver.Value = true,
                    OnLeave: _ => dropOver.Value = false,
                    OnDrop: session =>
                    {
                        dropOver.Value = false;
                        if (session.Payload is FileDropData files && files.Count > 0) Upload(files.Paths[0]);
                    }),
                Children =
                [
                    CoverArt(new Playlist(props.Slot), size),
                    new BoxEl
                    {
                        Width = size, Height = size, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        Fill = over ? ScrimDrop : ScrimHover, HitTestVisible = false,
                        Opacity = (Func<float>)(() => savingSig.Value || hovered.Value || dropOver.Value || fileDrag ? 1f : 0f),
                        Transition = MotionTok.ControlNormal,
                        Children = [overlayBody],
                    },
                ],
            };
        }

        void Pick()
        {
            string? path;
            try { path = FilePicker.OpenFile(FluentApp.WindowHandle, Loc.Get(Strings.Detail.Edit.PickCover), ("JPEG", "*.jpg;*.jpeg")); }
            catch (Exception ex) { Log.Warn("playlist", "cover picker failed", ex); return; }
            if (path is { Length: > 0 }) Upload(path);
        }

        void Upload(string path)
        {
            if (!path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
            {
                Notify.Say(Loc.Get(Strings.Detail.Edit.PickCover), InfoBarSeverity.Warning);   // item 20: a Warning, not an error
                return;
            }
            var saving = _saving;
            saving.Value = true;
            Spotify.PlaylistEdits.UploadCover(new Playlist(_slot), path, _ => saving.Value = false);
        }
    }

    // ══ 2. THE STATUS CHIP AND THE EDIT BUTTONS (W14) ════════════════════════════════════════════════════════════════

    static Element StatusChip(bool saved) => new BoxEl
    {
        Key = "pl-status", Direction = 0, Gap = 6f, AlignItems = FlexAlign.Center, Shrink = 0f,
        Padding = new Edges4(8f, 3f, 10f, 3f), Corners = CornerRadius4.All(12f), Fill = Tok.FillSubtleSecondary,
        Children =
        [
            new BoxEl
            {
                Key = saved ? "ico:saved" : "ico:saving", Width = 16f, Height = 16f,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Animate = MotionRecipes.IconSwap,
                Children = [saved ? Icon(Icons.Accept, 12f, Tok.AccentTextPrimary) : ProgressRing.Indeterminate(16f, foreground: Tok.TextSecondary)],
            },
            new BoxEl
            {
                Key = saved ? "txt:saved" : "txt:saving", Animate = MotionRecipes.TextSwap,
                Children = [new TextEl(Loc.Get(saved ? Strings.Detail.Edit.Saved : Strings.Detail.Edit.Saving)) { Size = 11f, Weight = 600, Color = Tok.TextSecondary, MaxLines = 1 }],
            },
        ],
    };

    /// <summary>Save / Cancel: 32 tall, r16, pad 10/0/12/0, gap 6; Save accent-tinted (.16 / .26 / .12).</summary>
    static Element EditButton(string glyph, string label, bool accent, Action onClick) => new BoxEl
    {
        Direction = 0, Gap = 6f, Height = EditButtonH, AlignItems = FlexAlign.Center, Padding = new Edges4(10f, 0f, 12f, 0f),
        Corners = CornerRadius4.All(16f),
        Fill = accent ? Tok.AccentTextPrimary with { A = 0.16f } : ColorF.Transparent,
        HoverFill = accent ? Tok.AccentTextPrimary with { A = 0.26f } : Tok.FillSubtleSecondary,
        PressedFill = accent ? Tok.AccentTextPrimary with { A = 0.12f } : Tok.FillSubtleTertiary,
        BrushTransitionMs = Design.Motion.Faster,
        Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, OnClick = onClick,
        Children =
        [
            Icon(glyph, 13f, accent ? Tok.AccentTextPrimary : Tok.TextSecondary),
            new TextEl(label) { Size = 12f, Weight = 600, Color = accent ? Tok.AccentTextPrimary : Tok.TextSecondary },
        ],
    };

    // ══ 3. THE INLINE EDITORS (W14, W15) ═════════════════════════════════════════════════════════════════════════════

    /// <summary>The frame's Title slot: ONE box across read↔edit whose height tweens (CardResize), the status row BELOW it
    /// — never inline, so the title's measure never moves (ch 06 §0.6). Read-only renders the same hero run.</summary>
    public static Element TitleSlot(Playlist p, float size, float lineHeight)
        => Embed.Comp(new EditorProps(p.Slot, size, lineHeight, Description: false), static () => new InlineEditor())
            with { Key = "pl-title:" + (int)size + ":" + (float.IsNaN(lineHeight) ? -1 : (int)lineHeight) };

    /// <summary>The frame's Description slot (the frame invokes it only while <c>Identity.EditableMetadata</c>).</summary>
    public static Element DescriptionSlot(Playlist p, float width)
        => Embed.Comp(new EditorProps(p.Slot, width, float.NaN, Description: true), static () => new InlineEditor())
            with { Key = "pl-desc:" + Bucket(width) };

    sealed record EditorProps(int Slot, float Size, float LineHeight, bool Description);

    sealed class InlineEditor : Component
    {
        readonly Signal<bool> _editing = new(false), _hovered = new(false);
        readonly Signal<int> _status = new(0);                 // 0 idle · 1 saving · 2 saved
        readonly Signal<string> _draft = new("");
        string _startedWith = "";
        int _slot;
        bool _description;
        readonly Action _begin, _save, _cancel, _clearSaved, _takeIntent;
        readonly Action<string> _commit;
        readonly Func<RowStamp> _stamp;

        public InlineEditor()
        {
            _stamp = Stamp;
            _begin = Begin;
            _save = () => Commit(_draft.Peek());
            _cancel = () => _editing.Value = false;
            _commit = Commit;
            _clearSaved = () => { if (_status.Peek() == 2) _status.Value = 0; };
            _takeIntent = () => { if (!_description && PlaylistCreateIntent.Take(new Playlist(_slot).Uri.Text)) Begin(); };
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
            var hooks = UseContext(InputHooks.Current);
            var post = UsePost();

            Element body = editing ? EditArm(props, hooks, post) : ReadArm(props, p, text, editable);
            Element statusRow = new BoxEl
            {
                Key = "pl-status-row", Direction = 0, Justify = FlexJustify.End, Gap = 6f,
                Height = _description && status == 0 ? 0f : float.NaN, ClipToBounds = true,
                Children = status == 0 ? [] : [StatusChip(saved: status == 2)],
            };
            return new BoxEl
            {
                Direction = 1, Gap = status != 0 ? 4f : 0f, MinWidth = 0f,
                Children =
                [
                    new BoxEl { Key = "pl-swap", Direction = 1, MinWidth = 0f, Animate = MotionRecipes.CardResize, Children = [body] },
                    statusRow,
                ],
            };
        }

        Element ReadArm(EditorProps props, Playlist p, string text, bool editable)
        {
            bool blank = string.IsNullOrWhiteSpace(text);
            string shown = blank ? Loc.Get(_description ? Strings.Detail.Edit.DescriptionPlaceholder : Strings.Detail.Edit.NamePlaceholder) : text;
            // Links take the PAGE accent in both arms (§4: 0.2.9's editable arm hard-coded AccentTextPrimary — fixed).
            Element run = _description
                ? (blank ? new TextEl(shown) { Size = 12f, Color = Tok.TextTertiary, Grow = 1f, Basis = 0f, MinWidth = 0f, Wrap = TextWrap.Wrap }
                         : Controls.RichTextFlex(text, 12f, Tok.TextSecondary, AccentOf(p), 6, NavRoute))
                : Design.Type.DetailHero(shown) with
                {
                    Size = props.Size, MinSize = 18f, Weight = 600, Grow = 1f, Basis = 0f, MinWidth = 0f,
                    LineHeight = props.LineHeight, Wrap = TextWrap.WrapWholeWords, MaxLines = 3, Trim = TextTrim.CharacterEllipsis,
                    Color = blank ? Tok.TextTertiary : Tok.TextPrimary,
                };
            if (!editable) return run;
            var hovered = _hovered;
            var statusSig = _status;
            float glyph = _description ? 13f : MathF.Max(14f, props.Size * 0.4f);
            return new BoxEl
            {
                Key = "pl-read", Direction = 0, Gap = Spacing.S, MinWidth = 0f,
                AlignItems = _description ? FlexAlign.Start : FlexAlign.Center,
                Margin = new Edges4(-8f, -4f, -8f, -4f), Padding = new Edges4(8f, 4f, 8f, 4f),
                Corners = Radii.ControlAll, HoverFill = Tok.FillSubtleSecondary,
                BorderWidth = 1f, BorderColor = ColorF.Transparent, HoverBorderColor = Tok.StrokeControlDefault,
                Role = AutomationRole.Button, Cursor = CursorId.IBeam, Focusable = true, OnClick = _begin,
                OnHoverMove = _ => { if (!hovered.Peek()) hovered.Value = true; },
                OnPointerExit = () => hovered.Value = false,
                Enter = new EnterExit(Opacity: 0f, Active: true),
                Children =
                [
                    run,
                    new BoxEl
                    {
                        Width = _description ? 16f : PencilBox, Height = _description ? 18f : PencilBox, Shrink = 0f,
                        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestVisible = false,
                        Opacity = (Func<float>)(() => hovered.Value && statusSig.Value == 0 ? 1f : 0f),
                        Transition = MotionTok.ControlFast,
                        Children = [Icon(Icons.Edit, glyph, Tok.TextSecondary)],
                    },
                ],
            };
        }

        Element EditArm(EditorProps props, InputHooks? hooks, Action<Action> post)
        {
            float fieldH = _description ? 72f : MathF.Max(40f, props.Size + 12f);
            float fontSize = _description ? 12f : props.Size;
            var draft = _draft;
            var commit = _commit;
            var cancel = _cancel;
            // FocusOnMount (W14): focus WITHOUT a visual cue; with PlaceCaretAtEndOnFocus the caret sits at the end and
            // nothing is selected.
            var parts = new TemplateParts
            {
                [EditableText.PartRoot] = b => b with { OnRealized = n => post(() => hooks?.FocusNode?.Invoke(n, false)) },
            };
            string placeholder = Loc.Get(_description ? Strings.Detail.Edit.DescriptionPlaceholder : Strings.Detail.Edit.NamePlaceholder);
            return new BoxEl
            {
                Key = "pl-edit", Direction = 1, Gap = Spacing.S, MinWidth = 0f,
                Children =
                [
                    Embed.Comp(() => new EditableText
                    {
                        Text = draft, Width = float.NaN, Height = fieldH, FontSize = fontSize, Placeholder = placeholder,
                        PlaceCaretAtEndOnFocus = true, CommitOnLostFocus = true, OnCommit = commit, OnCancel = cancel,
                        Parts = parts,
                    }) with { Key = "pl-field" },
                    new BoxEl
                    {
                        Direction = 0, Gap = Spacing.S, Justify = FlexJustify.End,
                        Children =
                        [
                            EditButton(Icons.Accept, Loc.Get(Strings.Detail.Edit.Save), accent: true, _save),
                            EditButton(Icons.Cancel, Loc.Get(Strings.Detail.Edit.Cancel), accent: false, _cancel),
                        ],
                    },
                ],
            };
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

    static readonly Action<string> NavRoute = static key => Shell.GoTo(Shell.Parse(key));

    // ══ 4. THE OWNER BLOCK (rail + hero attribution, W25, W29) ═══════════════════════════════════════════════════════

    /// <summary>The frame's Attribution slot: the collaborator pile (the frame's own predicate) or the owner row, each with
    /// the self-gating invite affordance. ONE composition for both arms (a decision: the report, W29).</summary>
    public static Element AttributionSlot(Playlist p, float width)
        => Embed.Comp(new OwnerProps(p.Slot, width), static () => new OwnerBlock()) with { Key = "pl-owner:" + Bucket(width) };

    sealed record OwnerProps(int Slot, float Width);

    sealed class OwnerBlock : Component
    {
        readonly int[] _members = new int[64];
        int _memberCount, _slot;
        OverlayHandle? _membersHandle;
        NodeHandle _pileNode, _inviteNode;
        IOverlayService? _overlay;
        readonly Action _openMembers, _openAccess;
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

            // THE LEADING ARM lives in its own GROWN box, so the pile and the owner run measure the same and the invite
            // pill after them never moves sideways when "Collaborative" flips (the flyout is anchored to that pill).
            Element lead;
            if (pile) lead = PileButton(p);
            else
            {
                var owner = p.Owner;
                string name = NameOf(owner);
                lead = new BoxEl
                {
                    Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f,
                    Children =
                    [
                        PersonPicture.Create("", 24f, displayName: name, imageSourcePath: Controls.ArtUrl(owner.ImageId)),
                        Design.Type.TrackTitle(name) with { Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    ],
                };
            }
            var kids = new List<Element>(2)
            {
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Grow = 1f, Basis = 0f, MinWidth = 0f,
                    Children = [lead],
                },
            };
            if (invite) kids.Add(InviteButton(props.Width));
            return new BoxEl
            {
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Height = OwnerRowHeight,      // RESERVED: the taller arm's height, always (see the const's summary)
                MaxWidth = float.IsFinite(props.Width) && props.Width > 0f ? props.Width : float.NaN,
                Children = kids.ToArray(),
            };
        }

        Element PileButton(Playlist p)
        {
            int visible = Math.Min(4, _memberCount);
            var faces = new Controls.Face[visible];
            for (int i = 0; i < visible; i++)
            {
                var u = new User(_members[i]);
                faces[i] = new Controls.Face(NameOf(u), Controls.ArtUrl(u.ImageId));
            }
            // The keys the 0.2.9 code hard-coded around (ch 06 §9): plural-aware count · open · the member's name.
            string label = _memberCount >= 2 ? Strings.Detail.CollabCount(_memberCount)
                         : p.IsCollaborative ? Loc.Get(Strings.Detail.CollabOpen) : NameOf(new User(_members[0]));
            return ToolTip.Wrap(new BoxEl
            {
                // No VERTICAL padding: the framed face is already OwnerRowHeight tall and the reserved row is measured
                // on it — 4 DIP more here would make the collaborative arm the taller one all over again.
                Direction = 0, Gap = 4f, AlignItems = FlexAlign.Center, MinWidth = 0f, Shrink = 1f,
                Padding = new Edges4(6f, 0f, 6f, 0f), Corners = CornerRadius4.All(8f),
                HoverFill = Tok.FillCardDefault, PressedFill = Tok.FillSubtleTertiary, BrushTransitionMs = Design.Motion.Faster,
                Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, OnClick = _openMembers,
                OnRealized = n => _pileNode = n,
                OnKeyDown = e => { if (e.KeyCode is Keys.Down or Keys.F4) { OpenMembers(); e.Handled = true; } },
                Children =
                [
                    Controls.FacePile(faces, maxVisible: 4, overflow: Math.Max(0, _memberCount - 4)),
                    Icon(Icons.ChevronDownSmall, 8f, Tok.TextTertiary),
                    new TextEl(label) { Size = 14f, Weight = 700, Color = Tok.AccentTextPrimary, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                ],
            }, Loc.Get(Strings.Detail.CollabView));
        }

        /// <summary>The ROUND 28 arm under 260 DIP (glyph 14), the labelled pill above it (glyph 12). Both inks are
        /// TextPrimary in 0.3 (ch 06 §9: the two arms used to disagree).</summary>
        Element InviteButton(float width)
        {
            bool wide = float.IsFinite(width) && width >= InviteWideFrom;
            string label = Loc.Get(Strings.Detail.Edit.InviteCollaborators);
            var box = new BoxEl
            {
                Direction = 0, Gap = 4f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Shrink = 0f,
                Height = InviteRoundEdge, Width = wide ? float.NaN : InviteRoundEdge,
                Padding = wide ? new Edges4(10f, 0f, 12f, 0f) : default, Corners = CornerRadius4.All(14f),
                BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault, HoverFill = Tok.FillSubtleSecondary,
                PressedFill = Tok.FillSubtleTertiary, BrushTransitionMs = Design.Motion.Faster,
                HoverScale = Design.Motion.ScaleSubtle.Hover, PressScale = Design.Motion.ScaleSubtle.Press,
                Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, OnClick = _openAccess,
                OnRealized = n => _inviteNode = n,
                Children = wide
                    ? [Icon(Icons.Friends, 12f, Tok.TextPrimary), new TextEl(label) { Size = 12f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1 }]
                    : [Icon(Icons.Friends, 14f, Tok.TextPrimary)],
            };
            return wide ? box : ToolTip.Wrap(box, label);
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
            var rows = new Element[_memberCount];
            for (int i = 0; i < rows.Length; i++)
            {
                var u = new User(_members[i]);
                string name = NameOf(u);
                rows[i] = new BoxEl
                {
                    Direction = 0, Gap = 12f, Height = 44f, AlignItems = FlexAlign.Center, Padding = new Edges4(8f, 0f, 8f, 0f),
                    Corners = CornerRadius4.All(6f), HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
                    Role = AutomationRole.MenuItem, Cursor = CursorId.Hand, OnClick = () => handle?.Close(),
                    Children =
                    [
                        PersonPicture.Create("", 32f, displayName: name, imageSourcePath: Controls.ArtUrl(u.ImageId)),
                        new TextEl(name) { Size = 14f, Weight = 600, Color = Tok.TextPrimary, Grow = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    ],
                };
            }
            var list = new BoxEl { Direction = 1, Gap = 2f, Width = Design.Size.FacePileListW, Children = rows };
            handle = overlay.Open(_pileAnchor, () => new BoxEl
            {
                Direction = 1, Width = Design.Size.FacePileFlyoutW, MaxHeight = Design.Size.FacePileFlyoutH, Padding = Edges4.All(Spacing.S),
                Children = [ScrollView(list) with { Width = Design.Size.FacePileListW, MaxHeight = Design.Size.FacePileListH, ContentSized = true, AutoEdgeFade = true }],
            }, FlyoutPlacement.BottomEdgeAlignedLeft,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup) { ConstrainToRootBounds = false });
            _membersHandle = handle;
        }
    }

    /// <summary>A user's display name, or its username while the profile has not landed (0.2.9 shows the raw id too).</summary>
    internal static string NameOf(User u)
    {
        if (!u.IsValid) return "";
        string name = Entities.Strings.Resolve(u.NameId);
        return name.Length > 0 ? name : new string(EntityUri.IdOf(u.Uri.Text.AsSpan()));
    }

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
                d.DialogWidth = AccessPanelW + 48f;
                d.Content = Embed.Comp(new AccessProps(slot, close), static () => new AccessPanel());
            });
    }

    sealed record AccessProps(int Slot, Action Close)
    {
        public bool Equals(AccessProps? o) => o is not null && o.Slot == Slot;
        public override int GetHashCode() => Slot;
    }

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

        public AccessPanel()
        {
            _copy = CopyInvite;
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

            var kids = new List<Element>(6)
            {
                new TextEl(Loc.Get(Strings.Detail.Edit.InviteCollaborators)) { Size = 14f, Weight = 700, Color = Tok.TextPrimary },
                new TextEl(Loc.Get(Strings.Detail.Edit.InviteHint)) { Size = 12f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 3, MinWidth = 0f },
            };
            if (admin) kids.Add(InviteCta(invite));
            if (admin && p.EditableMetadata)
            {
                kids.Add(new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault, Margin = new Edges4(0f, 2f, 0f, 2f) });
                kids.Add(SwitchRow(Loc.Get(Strings.Detail.Edit.Collaborative), Loc.Get(Strings.Detail.Edit.CollaborativeHint),
                    _collaborative, _savingCollab.Value, _onCollaborative));
                kids.Add(SwitchRow(Loc.Get(Strings.Detail.Edit.PublicPlaylist),
                    Loc.Get(isPublic ? Strings.Detail.Access.PublicCaption : Strings.Detail.Access.PrivateCaption),
                    _public, _savingPublic.Value, _onPublic));
            }
            return new BoxEl { Direction = 1, Width = AccessPanelW, Padding = Edges4.All(Spacing.L), Gap = Spacing.M, Children = kids.ToArray() };
        }

        /// <summary>The "Copy invite link" CTA — the THEME accent on purpose (§4: chrome over the page, not the page).</summary>
        Element InviteCta(int state)
        {
            ColorF fill = Tok.AccentDefault;
            ColorF ink = ColorContrast.PickContrast(fill);
            Element glyph = state == 1 ? ProgressRing.Indeterminate(14f, foreground: ink) : Icon(state == 2 ? Icons.Accept : Icons.Link, 14f, ink);
            return new BoxEl
            {
                Direction = 0, Gap = Spacing.S, Height = 34f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = CornerRadius4.All(17f), Fill = fill, HoverFill = Tok.AccentSecondary, PressedFill = Tok.AccentTertiary,
                Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, IsEnabled = state != 1, OnClick = _copy,
                Children =
                [
                    new BoxEl { Key = "cta-ico:" + state, Animate = MotionRecipes.IconSwap, Children = [glyph] },
                    new BoxEl
                    {
                        Key = "cta-txt:" + (state == 2 ? 1 : 0), Animate = MotionRecipes.TextSwap,
                        Children = [new TextEl(Loc.Get(state == 2 ? Strings.Auth.Copied : Strings.Detail.Edit.CopyInviteLink)) { Size = 13f, Weight = 600, Color = ink }],
                    },
                ],
            };
        }

        static Element SwitchRow(string label, string caption, Signal<bool> value, bool saving, Action<bool> onChange) => new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
            Children =
            [
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = 2f,
                    Children =
                    [
                        new TextEl(label) { Size = 13f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1 },
                        new TextEl(caption) { Size = 11.5f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 2, MinWidth = 0f },
                    ],
                },
                saving ? StatusChip(saved: false) : new BoxEl(),
                ToggleSwitch.Create(value, onChange, isEnabled: !saving),
            ],
        };

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

    /// <summary>The frame's Pulse slot: HH:MM:SS flip cells (10×20 compact / 13×28 hero) + "Next update at {time}". Keyed on
    /// the window — a rollover is a new strip.</summary>
    public static Element DaylistStrip(Playlist p, bool compact, Func<ColorF> accent)
        => Embed.Comp(new DaylistProps(p.DaylistExpiresAt, compact, accent), static () => new DaylistHost())
            with { Key = "daylist:" + p.Slot + ":" + p.DaylistExpiresAt + ":" + (compact ? 1 : 0) };

    sealed record DaylistProps(int ExpiresAt, bool Compact, Func<ColorF> Accent)
    {
        public bool Equals(DaylistProps? o) => o is not null && o.ExpiresAt == ExpiresAt && o.Compact == Compact;
        public override int GetHashCode() => HashCode.Combine(ExpiresAt, Compact);
    }

    /// <summary>ch 06 §0.15: a wall-clock sample anchored against the frame clock, re-anchored on
    /// <see cref="DaylistCountdown"/>'s cadence (a stalled frame clock across sleep must not leave the digits behind).
    /// Past the window the strip is Rolling: zeros in tertiary ink under "Updating your daylist…", the 1 s interval
    /// still ticking until the new window remounts the strip. Hours clamp at 99 (two fixed cells).</summary>
    sealed class DaylistHost : Component
    {
        readonly Signal<int> _tick = new(0);
        long _anchorUnixMs, _anchorFrameMs, _lastTickFrameMs;
        int _lastTick;
        bool _anchored;
        int _labelFor;
        string? _nextLabel;
        readonly Action _onTick;

        public DaylistHost() => _onTick = () => _tick.Value++;

        public override Element Render()
        {
            var props = UseProps<DaylistProps>();
            int tick = _tick.Value;
            long frameNow = Design.FrameTime.NowMs;
            long sinceTick = frameNow - _lastTickFrameMs;
            if (tick != _lastTick) { _lastTick = tick; _lastTickFrameMs = frameNow; }
            if (!_anchored || DaylistCountdown.NeedsReanchor(tick, sinceTick))
            {
                _anchored = true;
                _anchorUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _anchorFrameMs = frameNow;
                if (_lastTickFrameMs == 0) _lastTickFrameMs = frameNow;
            }
            long expiresMs = props.ExpiresAt * 1000L;
            long nowMs = DaylistCountdown.Now(_anchorUnixMs, _anchorFrameMs, frameNow);
            var phase = DaylistCountdown.PhaseOf(expiresMs, nowMs);
            UseInterval(_onTick, 1000f, enabled: phase != DaylistCountdown.Phase.Idle);
            if (phase == DaylistCountdown.Phase.Idle) return new BoxEl();
            bool rolling = phase == DaylistCountdown.Phase.Rolling;
            long left = DaylistCountdown.RemainingMs(expiresMs, nowMs) / 1000L;

            float rowH = props.Compact ? Controls.FlipCompactRowHeight : Controls.FlipHeroRowHeight;
            ColorF ink = rolling ? Tok.TextTertiary : Design.Palette.TextInk(props.Accent());
            int hours = (int)Math.Min(99L, left / 3600), minutes = (int)(left / 60 % 60), seconds = (int)(left % 60);
            Element[] cells =
            [
                Controls.FlipDigit(hours / 10, rowH, ink) with { Key = "h1" },
                Controls.FlipDigit(hours % 10, rowH, ink) with { Key = "h0" },
                Colon(rowH, ink, props.Compact, "c1"),
                Controls.FlipDigit(minutes / 10, rowH, ink) with { Key = "m1" },
                Controls.FlipDigit(minutes % 10, rowH, ink) with { Key = "m0" },
                Colon(rowH, ink, props.Compact, "c2"),
                Controls.FlipDigit(seconds / 10, rowH, ink) with { Key = "s1" },
                Controls.FlipDigit(seconds % 10, rowH, ink) with { Key = "s0" },
            ];
            if (_nextLabel is null || _labelFor != props.ExpiresAt)
            {
                _labelFor = props.ExpiresAt;
                _nextLabel = Strings.Home.NextUpdateAt(DateTimeOffset.FromUnixTimeSeconds(props.ExpiresAt).ToLocalTime().ToString("t", CultureInfo.CurrentCulture));
            }
            string caption = rolling ? Loc.Get(Strings.Home.DaylistUpdating) : _nextLabel;
            return new BoxEl
            {
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Wrap = true,
                Children =
                [
                    new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, Children = cells },
                    Caption(caption) with { Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                ],
            };
        }

        static Element Colon(float rowH, ColorF ink, bool compact, string key) => new BoxEl
        {
            Key = key, Width = compact ? 6f : 8f, Height = rowH, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Children = [new TextEl(":") { Size = compact ? 14f : 20f, Weight = 300, Color = ink, FontFamily = "Segoe UI Variable Display" }],
        };
    }

    // ══ 7. RECOMMENDED SONGS (W10; ch 04 items 56 / 71) ══════════════════════════════════════════════════════════════

    /// <summary><c>TableProfile.Recommendations</c>: the table's ONE appended section. Its MOUNT is the first fetch; a
    /// spinner while asked, "No suggestions right now" beside the refresh after an empty reply, the rows with a [+]
    /// otherwise; the batch refills itself once the user has added all of it.</summary>
    public static Element RecommendationsSection(Playlist p)
        => Embed.Comp(new RecsProps(p.Slot), static () => new RecsSection()) with { Key = "pl-recs:" + p.Slot };

    sealed record RecsProps(int Slot);

    sealed class RecsSection : Component
    {
        readonly Signal<int> _adding = new(0);
        bool _hadRows;
        int _slot;
        readonly Action _demand, _refresh;
        readonly Func<RecsStamp> _stamp;

        /// <summary>What the section paints (W3-A3): the edge's state and failure (the header's spinner / "No
        /// suggestions"), its version and the recommended rows in order (title, artists, art, duration).</summary>
        readonly record struct RecsStamp(uint Epoch, EdgeState State, bool Failed, uint Edge, ulong Rows);

        public RecsSection()
        {
            _demand = Demand;
            _refresh = () => Spotify.PlaylistEdits.Extend(new Playlist(_slot));
            _stamp = Stamp;
        }

        RecsStamp Stamp()
        {
            uint epoch = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            var recs = scope.Edges.PlaylistRecs;
            _ = recs.Changed.Value;
            _ = scope.Tracks.Changed.Value;
            var p = new Playlist(_slot);
            if (!p.IsValid) return new RecsStamp(epoch, EdgeState.Unknown, false, 0, RowFold.Seed);
            return new RecsStamp(epoch, recs.State(p.Slot), recs.IsFailed(p.Slot), recs.Version(p.Slot),
                                 RowFold.Rows(scope.Tracks, p.RecommendationSlots));
        }

        public override Element Render()
        {
            var props = UseProps<RecsProps>();
            _slot = props.Slot;
            _ = UseComputed(_stamp).Value;
            var scope = Entities.Current;
            int adding = _adding.Value;
            UseEffect(_demand);

            var recs = scope.Edges.PlaylistRecs;
            var p = new Playlist(props.Slot);
            var slots = p.RecommendationSlots;
            bool loading = recs.State(p.Slot) == EdgeState.Unknown && !recs.IsFailed(p.Slot);
            bool empty = !loading && slots.Length == 0;

            var kids = new Element[slots.Length + 1];
            kids[0] = Header(loading, empty);
            for (int i = 0; i < slots.Length; i++) kids[i + 1] = RecRow(new Track(slots[i]), adding);
            return new BoxEl { Direction = 1, Children = kids };
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

        Element Header(bool loading, bool empty) => new BoxEl
        {
            Key = "rec:header", Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center,
            MinHeight = Track.RowMetrics.RowHeight, Padding = new Edges4(Spacing.L, 0f, Spacing.L, 0f),
            Children =
            [
                BodyStrong(Loc.Get(Strings.Detail.Recommended)) with { Color = Tok.TextPrimary, Grow = 1f, MinWidth = 0f, MaxLines = 1 },
                empty ? new TextEl(Loc.Get(Strings.Detail.NoSuggestions)) { Size = 12f, Color = Tok.TextTertiary, MaxLines = 1 } : new BoxEl(),
                loading
                    ? new BoxEl { Width = 32f, Height = 32f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [Track.Spinner()] }
                    : ToolTip.Wrap(new BoxEl
                    {
                        Width = 32f, Height = 32f, Corners = CornerRadius4.All(16f), AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary, BrushTransitionMs = Design.Motion.Faster,
                        HoverScale = Design.Motion.ScaleEmphatic.Hover, PressScale = Design.Motion.ScaleEmphatic.Press,
                        Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, OnClick = _refresh,
                        Children = [Icon(Icons.Refresh, 14f, Tok.TextSecondary)],
                    }, Loc.Get(Strings.Detail.Recommended)),
            ],
        };

        /// <summary>An art-forward row clipped to the row height; drags as a COPY (a single-track payload).</summary>
        Element RecRow(Track t, int adding)
        {
            string title = t.Knows(TrackFields.Title) ? t.Title : "";
            string artists = Entities.Strings.Resolve(t.ArtistLineId);
            int slot = t.Slot;
            bool busy = adding == slot;
            string uri = t.Uri.Text;
            var addingSig = _adding;
            int playlist = _slot;
            return new BoxEl
            {
                Key = "rec:" + slot, Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center,
                MinHeight = Track.RowMetrics.RowHeight, MaxHeight = Track.RowMetrics.RowHeight, ClipToBounds = true,
                Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f), Corners = Radii.ControlAll, HoverFill = Tok.FillSubtleSecondary,
                Draggable = Drag.Source(() => new DragPayload(DragKind.Track, uri, uri, title, new EntityRef(EntityKind.Track, slot), Tracks: [new Track(slot)])),
                Children =
                [
                    Controls.Artwork(Controls.ArtUrl(t.ImageId), 40f, 40f, 4f, decodePx: 80),
                    new BoxEl
                    {
                        Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f,
                        Children =
                        [
                            Design.Type.TrackTitle(title) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                            Design.Type.TrackMeta(artists) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                        ],
                    },
                    ToolTip.Wrap(new BoxEl
                    {
                        Width = 32f, Height = 32f, Corners = CornerRadius4.All(16f), AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary, IsEnabled = !busy,
                        Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true,
                        OnClick = () =>
                        {
                            addingSig.Value = slot;
                            Spotify.PlaylistEdits.AddRecommendation(new Playlist(playlist), new Track(slot),
                                _ => { if (addingSig.Peek() == slot) addingSig.Value = 0; });
                        },
                        Children = [busy ? Track.Spinner() : Icon(Icons.Add, 14f, Tok.TextSecondary)],
                    }, Loc.Get(Strings.Detail.AddToPlaylist)),
                    Design.Type.TrackMeta(Track.Format.TrackTime(t.DurationMs)) with { Width = 52f, MaxLines = 1 },
                ],
            };
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
            d.DialogWidth = TunePanelW + 48f;
            d.Content = TunePanel(options, selected, pick);
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

    static Element TunePanel(List<TuneOption> options, string? selected, Action<string> pick)
    {
        var kids = new List<Element>(options.Count + 4)
        {
            new BoxEl
            {
                Direction = 0, Gap = 12f, AlignItems = FlexAlign.Center, Padding = new Edges4(14f, 9f, 14f, 11f),
                Children =
                [
                    new BoxEl
                    {
                        Width = 36f, Height = 36f, Corners = CornerRadius4.All(10f), Fill = Tok.AccentTextPrimary with { A = 0.13f },
                        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [Icon(Icons.RefineSparkle, 18f, Tok.AccentTextPrimary)],
                    },
                    new TextEl(Loc.Get(Strings.Detail.Tuning.FlyoutSubtitle)) { Size = 12f, Color = Tok.TextSecondary, Grow = 1f, Basis = 0f, MinWidth = 0f, Wrap = TextWrap.Wrap, MaxLines = 3 },
                ],
            },
            TuneDivider(),
        };
        foreach (var option in PlaylistTuneMenuModel.VisibleChoices(options))
            kids.Add(TuneRow(option.DisplayName ?? "", string.Equals(option.Identifier, selected, StringComparison.Ordinal), option.Identifier, pick));
        if (PlaylistTuneMenuModel.ResetOption(options, selected) is { } reset)
        {
            kids.Add(TuneDivider());
            kids.Add(TuneRow(Loc.Get(Strings.Detail.Tuning.Reset), false, reset.Identifier, pick, radio: false));
        }
        return new BoxEl { Direction = 1, Width = TunePanelW, Padding = new Edges4(0f, 8f, 0f, 6f), Children = kids.ToArray() };
    }

    static Element TuneDivider() => new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault, Margin = new Edges4(8f, 3f, 8f, 4f) };

    static Element TuneRow(string label, bool current, string identifier, Action<string> pick, bool radio = true) => new BoxEl
    {
        Direction = 0, Gap = Spacing.M, Height = 36f, AlignItems = FlexAlign.Center, Padding = new Edges4(12f, 0f, 14f, 0f),
        Corners = Radii.ControlAll, HoverFill = current ? ColorF.Transparent : Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
        Role = radio ? AutomationRole.RadioButton : AutomationRole.MenuItem, Cursor = current ? CursorId.Arrow : CursorId.Hand,
        IsEnabled = !current, OnClick = current ? null : () => pick(identifier),
        Children =
        [
            radio
                ? new BoxEl
                {
                    Width = 14f, Height = 14f, Corners = CornerRadius4.All(7f), BorderWidth = 1f, Shrink = 0f,
                    BorderColor = current ? Tok.AccentTextPrimary : Tok.TextSecondary, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children = current ? [new BoxEl { Width = 6f, Height = 6f, Corners = CornerRadius4.All(3f), Fill = Tok.AccentTextPrimary }] : [],
                }
                : new BoxEl { Width = 14f, Shrink = 0f },
            new TextEl(label) { Size = 14f, Color = current ? Tok.TextSecondary : Tok.TextPrimary, Grow = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            current ? new TextEl(Loc.Get(Strings.Detail.Tuning.Current)) { Size = 12f, Color = Tok.TextTertiary } : new BoxEl(),
        ],
    };
}
