// ── Shell/Visualizer.Zune.UI.cs ────────────────────────────────────────────────────────────────────────────────────
// The Zune family: Type (giant drifting words), Mosaic (a Metro wall of covers that flip on the beat), Spotlight (the
// artist's photos, full-bleed, with a slow Ken Burns pan and a cross-fade every eight bars)
//
// Role: UI
// Owner: K
// Wave: 7
// Budget: 620 lines
// Spec: viz-app-plan.md §2.15-§2.17, §3.6
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// THE FACE CONTRACT, WITH ENTITY DATA. A face is a static builder run once per host render (Visualizer.UI.cs §3); these
// three paint the playing track's WORDS, the queue's COVERS and the artist's PHOTOS, which a static builder cannot read
// (`FaceSpec` carries only the cover url). So each face is a thin frame around one small component per data source:
//
//   TypeBody       the track key → three keyed `TypeRow`s (a word is SHAPE: a new word remounts its row)
//   MosaicBody     queue + recents → the `CoverPool` (Visualizer.Covers.Collect) → a `MosaicWall` keyed per song
//   SpotlightBody  the artist's gallery (ensured with the overview) → keyed `SpotlightPhoto` layers, one per 8-bar cycle
//
// Everything that MOVES is still a bound channel or a keyframe: the rows' TranslateX and the tiles' ScaleX are signals a
// per-frame ticker writes (`ZunePacer` mounts it only while playing, motion allowed and the window visible); the photos'
// pan and the name's drift are keyframes (zero ticks); the column veils, the title's kick and the scrim bind the slab.
// Every choice — which cover, which tile, which photo, how fast — is Visualizer.Covers (CORE).
//
// BOTH ARMS. All ink is StageInk. Text over a photo or a wall sits on a scrim that follows the arm (Veil on the wall,
// the cover accent toward Deep on dark / toward Veil on light under the photos), so ink reads on #0A0A0A and #F5F5F5
// alike. Reduced motion is a VALUE: the drift and the pan rest, the flips become plain swaps, cross-fades stay.

using System.Globalization;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;

using Ink = Wavee.Design.StageInk;

namespace Wavee;

public static partial class Visualizer
{
    // ══ 1. SHARED ════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The prototype's stage height: every Zune size is authored at it and scaled by <c>H / ZuneRefH</c>.</summary>
    const float ZuneRefH = 920f;

    /// <summary>The playing track for a face leaf: the stage's key when the stage provides it (the gallery previews share
    /// it), else the transport's current row. Reading it subscribes the caller.</summary>
    static Track ZuneTrack(Stage.StageCtx? ctx)
    {
        if (ctx is not null) return ctx.RowValue();
        var cur = Playback.Current.Value;
        return cur.Kind == EntityKind.Track && !cur.IsNone ? new Track(cur.Slot) : default;
    }

    /// <summary>The lead artist's name (Rail.NowPlayingArtist, the stage's resolver), else the track's artist line.</summary>
    static string ZuneArtist(Track track)
    {
        if (!track.IsValid) return "";
        var artist = Rail.NowPlayingArtist(track);
        string name = artist.IsValid ? artist.Name : "";
        return name.Length > 0 ? name : Entities.Strings.Resolve(track.ArtistLineId);
    }

    static string ZuneAlbum(Track track) => track.IsValid && track.Album.IsValid ? track.Album.Title : "";

    /// <summary>Zune's lowercase (the globalization mode is invariant, so this is the invariant fold; CJK is unaffected).</summary>
    static string ZuneLower(string s) => s.Length == 0 ? s : s.ToLowerInvariant();

    /// <summary>A cover image id (the raw <c>StringId.Value</c>, 0 = none) for a queue or recents row.</summary>
    static int ZuneImageOf(EntityKind kind, int slot) => kind switch
    {
        EntityKind.Track when new Track(slot) is { IsValid: true } t => t.ForDisplay.ImageId.Value,
        EntityKind.Episode when new Episode(slot) is { IsValid: true } e => e.ImageId.Value,
        EntityKind.Album when new Album(slot) is { IsValid: true } a => a.ImageId.Value,
        EntityKind.Playlist when new Playlist(slot) is { IsValid: true } p => p.ImageId.Value,
        EntityKind.Show => LibraryRows.ImageOf(kind, slot).Value,
        _ => 0,
    };

    /// <summary>Mounts a face's per-frame ticker only while the stage plays, motion is allowed and the window is visible —
    /// an unmounted ticker is no wake reason, so a paused or occluded stage idles (Controls.FrameTicker's contract).</summary>
    sealed class ZunePacer : Component
    {
        public Action Tick = static () => { };
        public Func<Action, Component> Make = static t => new TypeFrames(t);

        public override Element Render()
        {
            var hooks = UseContext(InputHooks.Current);
            bool run = Playback.IsPlaying.Value && !Design.Reduced && !(hooks.WindowOccluded?.Value ?? false);
            var tick = Tick; var make = Make;
            return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false, Children = run ? [Embed.Comp(() => make(tick))] : [] };
        }
    }

    /// <summary>The Type face's drift clock and the Mosaic face's flip clock, named for the <c>[wake]</c> census.</summary>
    sealed class TypeFrames(Action tick) : Controls.FrameTicker(tick, paceable: true);
    sealed class MosaicFrames(Action tick) : Controls.FrameTicker(tick, paceable: true);

    /// <summary>A frame-time delta in ms since <paramref name="last"/>; a gap over 100 ms (the ticker was unmounted) is a
    /// fresh start, never a jump.</summary>
    static float ZuneDeltaMs(ref long last)
    {
        long now = Design.FrameTime.NowMs;
        long gap = last == 0 ? 0 : now - last;
        last = now;
        return gap is <= 0 or > 100 ? 0f : gap;
    }

    // ══ 2. TYPE — the words are the picture ══════════════════════════════════════════════════════════════════════════

    /// <summary>Three giant lowercase rows (artist 420/300, title 300/600 in the accent, album 150/300) cropped by the
    /// edges and drifting at speeds the energy drives; the title brightens on the kick. A "now playing" eyebrow under the
    /// now-playing card. Preview: the three rows, static.</summary>
    public static Element TypeFace(Slab slab, in Palette pal, in FaceSpec spec)
    {
        float w = spec.W, h = spec.H;
        bool preview = spec.Preview;
        return FaceFrame(spec,
        [
            new CanvasChild(0f, 0f, Embed.Comp(() => new TypeBody { Slab = slab, W = w, H = h, Preview = preview })
                with { Key = "type:" + (int)w + "x" + (int)h }),
        ]);
    }

    sealed class TypeBody : Component
    {
        public Slab Slab = null!;
        public float W, H;
        public bool Preview;
        readonly FloatSignal[] _offset = [new(0f), new(0f), new(0f)];
        readonly float[] _period = new float[Covers.Drift.Rows];
        readonly Action _tick, _write;
        Stage.StageCtx? _ctx;
        long _lastMs;
        float _dtMs;

        public TypeBody() { _tick = Tick; _write = Write; }

        float Scale => H / ZuneRefH * (Preview ? 1.4f : 1f);

        public override Element Render()
        {
            var ctx = UseContext(Stage.StageContext);
            _ctx = ctx;
            _ = Entities.ScopeEpoch.Value;                       // FIRST: a scope switch re-points the table reads below
            _ = Entities.Current.Artists.Changed.Value;
            _ = Entities.Current.Albums.Changed.Value;
            var track = ZuneTrack(ctx);
            string artist = ZuneLower(ZuneArtist(track)), title = ZuneLower(track.IsValid ? track.Title : ""), album = ZuneLower(ZuneAlbum(track));
            float k = Scale;
            bool dark = Ink.IsDark;
            var slab = Slab;
            var kids = new List<CanvasChild>(Covers.Drift.Rows + 2);
            for (int r = 0; r < Covers.Drift.Rows; r++)
            {
                string word = r switch { 0 => artist, 1 => title, _ => album };
                if (word.Length == 0) continue;
                float size = Covers.Drift.SizeOf(r) * k;
                // the line box is one em tall, so the baseline sits ≈ 0.86 em below its top (Segoe UI Variable's metrics)
                float y = Covers.Drift.BaselineOf(r) * H - 0.86f * size;
                int index = r; float w = W; bool still = Preview; var offset = _offset[r]; var periods = _period;
                ushort weight = Covers.Drift.WeightOf(r);
                Prop<ColorF> color = r == 1 ? Prop.Bind(slab.A) : Ink.Ink with { A = Covers.Drift.InkAlpha(r, dark) };
                Prop<float> opacity = r == 1 ? Prop.Of(() => Covers.Drift.TitleAlpha(slab.Kick.Value, slab.Low.Value)) : 1f;
                kids.Add(new CanvasChild(0f, y, Embed.Comp(() => new TypeRow
                {
                    Word = word, Size = size, Weight = weight, W = w, Gap = Covers.Drift.GapEm * size,
                    Offset = offset, Periods = periods, Index = index, Still = still, Color = color, Opacity = opacity,
                }) with { Key = "row:" + index + ":" + word }));
            }
            if (!Preview)
            {
                if (track.IsValid)
                {
                    string line = artist.Length > 0 && title.Length > 0 ? artist + " · " + title : artist + title;
                    kids.Add(new CanvasChild(Stage.Layout.NowPlayingCardX + 4f, Stage.Layout.NowPlayingCardY + Stage.Layout.NowPlayingCardH + 28f, new BoxEl
                    {
                        Direction = 1, HitTestVisible = false,
                        Children =
                        [
                            new TextEl(ZuneLower(Loc.Get(Strings.Player.NowPlaying))) { Size = 20f, LineHeight = 28f, Weight = 600, FontFamily = Design.Type.DisplayFace, Color = Ink.Ink with { A = 0.8f } },
                            new TextEl(line) { Size = 20f, LineHeight = 28f, FontFamily = Design.Type.DisplayFace, Color = Ink.Ink with { A = 0.55f }, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MaxWidth = Stage.Layout.NowPlayingCardW },
                        ],
                    }));
                }
                var tick = _tick;
                kids.Add(new CanvasChild(0f, 0f, Embed.Comp(() => new ZunePacer { Tick = tick, Make = static t => new TypeFrames(t) })));
            }
            return new BoxEl { Width = W, Height = H, HitTestVisible = false, Children = [Canvas.Create(W, H, kids)] };
        }

        void Tick()
        {
            _dtMs = ZuneDeltaMs(ref _lastMs);
            if (_dtMs <= 0f) return;
            if (Context.Runtime is { } rt) rt.Batch(_write); else Write();
        }

        /// <summary>Three translates per frame, wrapped into one period each (Covers.Drift.Step); zero allocation.</summary>
        void Write()
        {
            float drive = Covers.Drift.Drive(Slab.Energy.Peek(), Slab.Level.Peek());
            float pace = Scale * (_ctx?.Calm.Peek() == true ? 0.5f : 1f);
            for (int r = 0; r < Covers.Drift.Rows; r++)
                _offset[r].Value = Covers.Drift.Step(_offset[r].Peek(), Covers.Drift.Speed(r, drive) * pace, _dtMs, _period[r]);
        }
    }

    /// <summary>One giant row: copies of its word side by side, enough to cover the face at any offset, translated as one
    /// strip by the bound offset folded into one period (word + gap, measured from the first copy).</summary>
    sealed class TypeRow : Component
    {
        public string Word = "";
        public float Size, W, Gap;
        public ushort Weight;
        public Prop<ColorF> Color;
        public Prop<float> Opacity = 1f;
        public FloatSignal Offset = null!;
        public float[] Periods = null!;
        public int Index;
        public bool Still;

        public override Element Render()
        {
            var measured = UseSignal(0f);
            float tw = measured.Value, period = tw > 0f ? tw + Gap : 0f;
            int copies = Still || period <= 0f ? 1 : Math.Min(8, (int)MathF.Ceiling(W / period) + 1);
            var kids = new Element[copies];
            int index = Index; float gap = Gap; var periods = Periods;
            kids[0] = new BoxEl
            {
                Shrink = 0f, HitTestVisible = false,
                OnBoundsChanged = b => { periods[index] = b.W > 0f ? b.W + gap : 0f; if (b.W != measured.Peek()) measured.Value = b.W; },
                Children = [Glyphs()],
            };
            for (int i = 1; i < copies; i++) kids[i] = Glyphs();
            var offset = Offset;
            return new BoxEl
            {
                Direction = 0, Gap = Gap, HitTestVisible = false, Opacity = Opacity,
                Transform = Still ? Affine2D.Identity : Prop.Of(() => Affine2D.Translation(-Covers.Drift.Wrap(offset.Value, period), 0f)),
                Children = kids,
            };
        }

        TextEl Glyphs() => new(Word)
        {
            Size = Size, LineHeight = Size, Weight = Weight, FontFamily = Design.Type.DisplayFace, Color = Color,
            Wrap = TextWrap.NoWrap, MaxLines = 1, Shrink = 0f,
        };
    }

    // ══ 3. MOSAIC — the wall ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A Metro wall of the queue's and recently played covers (12 columns, ~14 % 2×2): on every beat two tiles flip (bound ScaleX, the source swapped edge-on), each column dims and brightens with
    /// its band, and the title sits big over a bottom fade. Preview: a 6 × 3 static wall.</summary>
    public static Element MosaicFace(Slab slab, in Palette pal, in FaceSpec spec)
    {
        bool preview = spec.Preview;
        int cols = preview ? Covers.Tiles.PreviewCols : Covers.Tiles.Cols;
        int maxRows = preview ? Covers.Tiles.PreviewMaxRows : Covers.Tiles.MaxRows;
        float w = spec.W, h = spec.H, cell = w / cols;
        int rows = Covers.Tiles.RowsFor(w, h, cols, maxRows);
        float y0 = (h - rows * cell) * 0.5f;
        bool big = !preview;
        int decode = Covers.Tiles.DecodeFor(cell, big ? 2 : 1, preview);
        var kids = new List<CanvasChild>(cols + 4)
        {
            new CanvasChild(0f, y0, Embed.Comp(() => new MosaicBody { Slab = slab, Cols = cols, Rows = rows, Cell = cell, Decode = decode, Big = big, Preview = preview })
                with { Key = "mosaic:" + cols + "x" + rows + ":" + (int)cell }),
        };
        if (!preview)
        {
            // one veil per COLUMN (not per tile): the column's band clears it — 12 nodes, quantised by the slab
            ColorF veil = Ink.Veil;
            for (int c = 0; c < cols; c++)
            {
                var band = slab.Bands[Covers.Tiles.BandOf(c, cols)];
                kids.Add(new CanvasChild(c * cell, y0, new BoxEl
                {
                    Width = cell, Height = rows * cell, Fill = veil, HitTestVisible = false,
                    Opacity = Prop.Of(() => Covers.Tiles.VeilAlpha(band.Value)),
                }));
            }
            kids.Add(new CanvasChild(0f, 0.55f * h, new BoxEl
            {
                Width = w, Height = 0.45f * h, HitTestVisible = false,
                Gradient = new GradientSpec(GradientShape.Linear, 90f, [new GradientStop(0f, veil with { A = 0f }), new GradientStop(1f, veil with { A = 0.92f })]),
            }));
            kids.Add(new CanvasChild(0f, 0f, Embed.Comp(() => new MosaicCaption { Slab = slab, W = w, H = h })));
        }
        return FaceFrame(spec, kids);
    }

    /// <summary>The covers the wall draws from, resolved to urls. The body publishes it (in an effect) when the queue,
    /// the recents or the playing track change; the wall reads it at seed and flip time.</summary>
    sealed class CoverPool
    {
        public readonly string?[] Urls = new string?[Covers.Cap];
        public int Count;
        public readonly Signal<int> Version = new(0);
        public readonly Signal<bool> Ready = new(false);

        public void Publish(ReadOnlySpan<int> ids)
        {
            int n = 0;
            bool changed = false;
            for (int i = 0; i < ids.Length && n < Urls.Length; i++)
            {
                string? url = Controls.ArtUrl(new StringId(ids[i]));
                if (url is null) continue;
                if (!string.Equals(Urls[n], url, StringComparison.Ordinal)) changed = true;
                Urls[n++] = url;
            }
            if (n != Count) changed = true;
            for (int i = n; i < Count; i++) Urls[i] = null;
            Count = n;
            if (changed) Version.Value = Version.Peek() + 1;
            Ready.SetIfChanged(Covers.Ready(n));
        }
    }

    /// <summary>Reads the sources (the up-next run, the recents, the playing track), publishes the pool and mounts the
    /// wall KEYED per song: a new song re-lays the wall (cross-faded) with its cover in the lead 2×2.</summary>
    sealed class MosaicBody : Component
    {
        public Slab Slab = null!;
        public int Cols, Rows, Decode;
        public float Cell;
        public bool Big, Preview;
        readonly CoverPool _pool = new();
        readonly int[] _ids = new int[Covers.Cap], _upNext = new int[Covers.Cap], _recent = new int[Covers.Cap];
        static readonly Action s_ensure = EnsureSources;

        public override Element Render()
        {
            var ctx = UseContext(Stage.StageContext);
            _ = Entities.ScopeEpoch.Value;                       // FIRST: a scope switch re-points the table reads below
            _ = Entities.Current.Edges.Queue.Changed.Value;
            _ = Entities.Current.Tracks.Changed.Value;
            _ = Entities.Current.Albums.Changed.Value;
            _ = Recents.Changed.Value;
            var track = ZuneTrack(ctx);
            int n = Gather(track);
            int hash = n;
            for (int i = 0; i < n; i++) hash = unchecked(hash * 31 + _ids[i]);
            UseEffect(() => _pool.Publish(_ids.AsSpan(0, n)), DepKey.From(hash, n));
            UseEffect(s_ensure, DepKey.From(((long)Entities.Current.Epoch << 32) | Queue.Version));
            int song = track.IsValid ? track.Slot : 0;
            var pool = _pool; var slab = Slab;
            int cols = Cols, rows = Rows, decode = Decode; float cell = Cell; bool big = Big, preview = Preview;
            return new BoxEl
            {
                Width = cols * cell, Height = rows * cell, ZStack = true, HitTestVisible = false,
                Children =
                [
                    new BoxEl
                    {
                        Key = "wall:" + song, Width = cols * cell, Height = rows * cell, HitTestVisible = false,
                        Enter = preview ? null : new EnterExit(Opacity: 0f, Active: true), Exit = preview ? null : new EnterExit(Opacity: 0f, Active: true),
                        Transition = MotionTokenDef.Eased(550f, Easing.FluentDecelerate, ReducedMotionPolicy.KeepFade),
                        Children = [Embed.Comp(() => new MosaicWall { Slab = slab, Pool = pool, Cols = cols, Rows = rows, Cell = cell, Decode = decode, Big = big, Preview = preview, Seed = (uint)song })],
                    },
                ],
            };
        }

        /// <summary>Current cover → up next → recents (newest first; a group row with no image of its own lends its first
        /// member's) into <c>_ids</c> via Covers.Collect. Alloc-free.</summary>
        int Gather(Track track)
        {
            int current = track.IsValid ? track.ForDisplay.ImageId.Value : 0;
            int nu = 0;
            if (Queue.UpNext(out int start, out int length))
                for (int i = start; i < start + length && nu < _upNext.Length; i++)
                {
                    var r = Queue.RefAt(i);
                    int img = r.IsNone ? 0 : ZuneImageOf(r.Kind, r.Slot);
                    if (img != 0) _upNext[nu++] = img;
                }
            int nr = 0;
            var me = Recents.Me;
            if (me.Slot > 0)
            {
                var rows = me.Rows; var slots = me.Slots;
                for (int i = 0; i < rows.Length && nr < _recent.Length; i++)
                {
                    int img = i < slots.Length && slots[i] > 0 ? ZuneImageOf(rows[i].Entity, slots[i]) : 0;
                    if (img == 0 && rows[i].MembersLen > 0)
                    {
                        var memberSlots = me.MemberSlots(in rows[i]); var members = me.Members(in rows[i]);
                        if (memberSlots.Length > 0 && members.Length > 0 && memberSlots[0] > 0) img = ZuneImageOf(members[0].Entity, memberSlots[0]);
                    }
                    if (img != 0) _recent[nr++] = img;
                }
            }
            return Covers.Collect(current, _upNext.AsSpan(0, nu), _recent.AsSpan(0, nr), _ids);
        }

        /// <summary>The up-next rows' covers need their identity group; the recents need their snapshot (the queue pane
        /// and Home ask the same, so this is usually already answered).</summary>
        static void EnsureSources()
        {
            if (Queue.UpNext(out int start, out int length))
            {
                Span<int> slots = stackalloc int[Covers.Cap];
                int n = 0;
                for (int i = start; i < start + length && n < slots.Length; i++)
                {
                    var r = Queue.RefAt(i);
                    if (r.Kind == EntityKind.Track && !r.IsNone) slots[n++] = r.Slot;
                }
                if (n > 0) Entities.Ensure(Entities.Current.Tracks, slots[..n], (uint)TrackFields.Identity, FetchPriority.Prefetch);
            }
            int me = Recents.Me.Slot;
            if (me > 0) Entities.EnsureEdge(FetchEdge.Recents, me);
        }
    }

    /// <summary>The tiles. Each is a square, unrounded BoxEl (a Metro tile — a card flip may scale it in X) holding ONE
    /// ImageEl whose Source binds the tile's url signal, so a flip swaps the art edge-on without a render. The flip clock
    /// (zero allocation) arms two tiles per beat (Covers.Flips.Pick) and steps the armed ones.</summary>
    sealed class MosaicWall : Component
    {
        public Slab Slab = null!;
        public CoverPool Pool = null!;
        public int Cols, Rows, Decode;
        public float Cell;
        public bool Big, Preview;
        public uint Seed;
        Covers.Tile[] _plan = [];
        Signal<string>[] _url = [];
        FloatSignal[] _scale = [];
        float[] _flip = [];
        string?[] _pending = [];
        int[] _cover = [];
        int _lead, _active, _lastBeat = int.MinValue, _lastA = -1, _lastB = -1;
        long _lastMs;
        float _dtSec;
        Stage.StageCtx? _ctx;
        readonly Action _tick, _write, _seed, _swap;

        public MosaicWall() { _tick = Tick; _write = Write; _seed = SeedTiles; _swap = SwapOnBeat; }

        public override Element Render()
        {
            _ctx = UseContext(Stage.StageContext);
            if (_plan.Length == 0) Init();
            bool ready = Pool.Ready.Value;
            var pool = Pool; var seed = _seed;
            UseSignalEffect(() => { _ = pool.Version.Value; Reactive.Untrack(seed); });
            // reduced motion: no flip clock (ZunePacer), so a beat swaps the art directly — the image's reveal fade is the
            // cross-fade; a stopped slab clock simply never moves the beat
            var slab = Slab; bool preview = Preview; var swap = _swap;
            UseSignalEffect(() => { if (preview || !Design.Reduced) return; _ = slab.BeatIndex.Value; Reactive.Untrack(swap); });

            float gap = preview ? 1f : 4f;
            var kids = new List<CanvasChild>(_plan.Length + 1);
            for (int t = 0; t < _plan.Length; t++)
            {
                var tile = _plan[t];
                float s = tile.Size * Cell - gap;
                var url = _url[t]; var scale = _scale[t];
                var image = Ui.Image("", ImageFit.Cover, 1f, Decode) with
                {
                    Source = Prop.Bind(url),
                    Placeholder = Prop.Of(() => Ink.ArtStandIn(url.Value)),
                    Saturation = ready ? 1f : Covers.Tiles.FallbackSaturation(t, Seed),
                };
                kids.Add(new CanvasChild(tile.Col * Cell + gap * 0.5f, tile.Row * Cell + gap * 0.5f, new BoxEl
                {
                    Width = s, Height = s, ZStack = true, ClipToBounds = true, HitTestVisible = false,
                    Transform = preview ? Affine2D.Identity : Prop.Of(() => Affine2D.Scale(scale.Value, 1f)),
                    Children = [image],
                }));
            }
            if (!preview)
            {
                var tick = _tick;
                kids.Add(new CanvasChild(0f, 0f, Embed.Comp(() => new ZunePacer { Tick = tick, Make = static t => new MosaicFrames(t) })));
            }
            return Canvas.Create(Cols * Cell, Rows * Cell, kids);
        }

        void Init()
        {
            _plan = Covers.Tiles.Plan(Cols, Rows, Seed, Big);
            int n = _plan.Length;
            _lead = Covers.Tiles.LeadOf(_plan);
            _url = new Signal<string>[n]; _scale = new FloatSignal[n]; _flip = new float[n]; _pending = new string?[n]; _cover = new int[n];
            Covers.Tiles.Assign(_plan, Pool.Count, Seed, _cover);
            for (int t = 0; t < n; t++)
            {
                string? u = _cover[t] >= 0 ? Pool.Urls[_cover[t]] : null;
                _url[t] = new Signal<string>(u ?? "");
                _scale[t] = new FloatSignal(1f);
                _flip[t] = 1f;
            }
        }

        /// <summary>A new pool: re-point every tile's pool index at the url it shows (the pool may have re-ordered under it —
        /// the flips pick only covers a tile already shows), fill the tiles still blank, and keep the lead tile on the
        /// current cover (the pool's head).</summary>
        void SeedTiles()
        {
            if (_plan.Length == 0) return;
            for (int t = 0; t < _plan.Length; t++) _cover[t] = PoolIndexOf(_pending[t] ?? _url[t].Peek());
            if (Pool.Count == 0) return;
            Span<int> fresh = stackalloc int[_plan.Length];
            Covers.Tiles.Assign(_plan, Pool.Count, Seed, fresh);
            for (int t = 0; t < _plan.Length; t++)
            {
                bool lead = t == _lead;
                if (_pending[t] is not null || (!lead && _url[t].Peek().Length > 0)) continue;
                _cover[t] = fresh[t];
                _url[t].Value = Pool.Urls[fresh[t]] ?? "";
            }
        }

        void SwapOnBeat()
        {
            int beat = Slab.BeatIndex.Peek();
            if (beat == _lastBeat) return;
            bool first = _lastBeat == int.MinValue;
            _lastBeat = beat;
            if (first || Pool.Count < 2) return;
            int got = Covers.Flips.Pick(beat, _plan.Length, _lastA, _lastB, 2, out int a, out int b);
            _lastA = a; _lastB = b;
            if (got > 0) Arm(a, beat, instant: true);
            if (got > 1) Arm(b, beat, instant: true);
        }

        void Tick()
        {
            _dtSec = ZuneDeltaMs(ref _lastMs) * 0.001f;
            if (Context.Runtime is { } rt) rt.Batch(_write); else Write();
        }

        /// <summary>Arm the beat's picks, then step every armed flip: swap the url as it passes edge-on (0), land at −1.</summary>
        void Write()
        {
            int beat = Slab.BeatIndex.Peek();
            if (beat != _lastBeat)
            {
                bool first = _lastBeat == int.MinValue;
                _lastBeat = beat;
                if (!first && Pool.Count >= 2)
                {
                    int got = Covers.Flips.Pick(beat, _plan.Length, _lastA, _lastB, 2, out int a, out int b);
                    _lastA = a; _lastB = b;
                    if (got > 0) Arm(a, beat, instant: false);
                    if (got > 1) Arm(b, beat, instant: false);
                }
            }
            if (_active == 0 || _dtSec <= 0f) return;
            float step = Covers.Flips.Step(_dtSec, _ctx?.Calm.Peek() == true);
            for (int t = 0; t < _pending.Length; t++)
            {
                if (_pending[t] is not { } next) continue;
                float before = _flip[t], after = before - step;
                if (before >= 0f && after < 0f) _url[t].Value = next;
                if (after <= -1f) { after = 1f; _pending[t] = null; _active--; }
                _flip[t] = after;
                _scale[t].Value = MathF.Round(Covers.Flips.ScaleOf(after) * 256f) / 256f;
            }
        }

        /// <summary>The pool index of <paramref name="url"/>, −1 when it is blank or no longer in the pool. Alloc-free.</summary>
        int PoolIndexOf(string url)
        {
            if (url.Length == 0) return -1;
            for (int i = 0; i < Pool.Count; i++) if (string.Equals(Pool.Urls[i], url, StringComparison.Ordinal)) return i;
            return -1;
        }

        /// <summary>Choose the tile's next cover among those another tile already shows (Covers.Flips.NextResident: a shown
        /// cover is decoded at the wall's one size, so the flip never reveals a placeholder); a tile already mid-flip keeps
        /// its flip.</summary>
        void Arm(int t, int beat, bool instant)
        {
            if ((uint)t >= (uint)_pending.Length || _pending[t] is not null) return;
            int c = Covers.Flips.NextResident(_cover[t], beat, t, _cover, Pool.Count);
            if (c < 0) return;
            string? url = Pool.Urls[c];
            if (url is null || string.Equals(url, _url[t].Peek(), StringComparison.Ordinal)) return;
            _cover[t] = c;
            if (instant) { _url[t].Value = url; return; }
            _pending[t] = url;
            _flip[t] = 1f;
            _active++;
        }
    }

    /// <summary>The song's title at 150/300 over the bottom fade, the artist at 28/600 in the accent above it — both
    /// kept above the transport.</summary>
    sealed class MosaicCaption : Component
    {
        public Slab Slab = null!;
        public float W, H;

        public override Element Render()
        {
            var ctx = UseContext(Stage.StageContext);
            _ = Entities.ScopeEpoch.Value;
            _ = Entities.Current.Artists.Changed.Value;
            var track = ZuneTrack(ctx);
            string title = ZuneLower(track.IsValid ? track.Title : ""), artist = ZuneLower(ZuneArtist(track));
            float k = H / ZuneRefH, x = 56f * k, size = 150f * k;
            float floor = MathF.Min(H - 40f * k, (ctx?.Layout.Value.TransportTop ?? H) - 16f);
            float titleY = floor - size, artistY = titleY - 40f * k;
            var kids = new List<CanvasChild>(2);
            if (artist.Length > 0)
                kids.Add(new CanvasChild(x + 8f * k, artistY, new TextEl(artist)
                {
                    Size = 28f * k, LineHeight = 36f * k, Weight = 600, FontFamily = Design.Type.DisplayFace, Color = Prop.Bind(Slab.A),
                    MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MaxWidth = W - 2f * x,
                }));
            if (title.Length > 0)
                kids.Add(new CanvasChild(x, titleY, new TextEl(title)
                {
                    Size = size, LineHeight = size, Weight = 300, FontFamily = Design.Type.DisplayFace, Color = Ink.Ink with { A = 0.92f },
                    MaxLines = 1, Trim = TextTrim.Clip, MaxWidth = W - x,
                }));
            return new BoxEl { Width = W, Height = H, HitTestVisible = false, Children = [Canvas.Create(W, H, kids)] };
        }
    }

    // ══ 4. SPOTLIGHT — the artist's photos ═══════════════════════════════════════════════════════════════════════════

    /// <summary>The artist's gallery full-bleed: a Ken Burns pan per photo (keyframes), a cross-fade to the next ON every
    /// 8-bar downbeat, a cover-accent scrim (Level lifts it ±0.1), the artist name huge and cropped across the bottom.
    /// No gallery: the artist's header/portrait, then the cover blurred — never a blank. Preview: the first photo and the
    /// name, static.</summary>
    public static Element SpotlightFace(Slab slab, in Palette pal, in FaceSpec spec)
    {
        float w = spec.W, h = spec.H;
        bool preview = spec.Preview, dark = Ink.IsDark;
        // the scrim: clear over the top third, toward the accent mixed into Deep (dark) or the Veil (light) at the foot,
        // so the stage ink on top reads in either arm
        ColorF a = pal.A, foot = ColorF.Lerp(pal.A, dark ? pal.Deep : Ink.Veil, dark ? 0.55f : 0.7f);
        var scrim = new GradientSpec(GradientShape.Linear, 90f,
        [
            new GradientStop(0f, a with { A = 0f }), new GradientStop(0.35f, a with { A = 0f }),
            new GradientStop(0.7f, foot with { A = 0.5f }), new GradientStop(1f, foot with { A = 0.88f }),
        ]);
        string size = (int)w + "x" + (int)h;
        return FaceFrame(spec,
        [
            new CanvasChild(0f, 0f, Embed.Comp(() => new SpotlightBody { Slab = slab, W = w, H = h, Preview = preview }) with { Key = "spot:" + size }),
            new CanvasChild(0f, 0f, new BoxEl
            {
                Width = w, Height = h, Gradient = scrim, HitTestVisible = false,
                Opacity = preview ? 1f : Prop.Of(() => Covers.Spot.ScrimAlpha(slab.Level.Value)),
            }),
            new CanvasChild(0f, 0f, Embed.Comp(() => new SpotlightType { W = w, H = h, Preview = preview }) with { Key = "spottype:" + size }),
        ]);
    }

    /// <summary>One mounted photo layer: its key (cycle + url — the same photo in the next cycle is a NEW layer with the pan
    /// reversed), its url, its cycle and whether it is the blurred-cover fallback.</summary>
    readonly record struct SpotLayer(string? Key, string Url, int Cycle, bool Blur);

    /// <summary>The Spotlight clock, one signal: the track it was computed for (a render for another track reads cycle 0),
    /// its 8-bar cycle, and whether the next photo is due to decode under the current one (Covers.Spot.Preload).</summary>
    readonly record struct SpotClock(int Slot, int Cycle, bool Preload);

    /// <summary>The photo stack: the previous layer (held under only while the current one fades in over it — dropped once
    /// the fade lands), the current one, and — in the last bars of the cycle only — the NEXT photo mounted invisible at the
    /// same decode, so the cross-fade never reveals a placeholder: two full-res photos resident, three only briefly. The
    /// cycle follows the slab's downbeat bar (Covers.Spot.CycleOf over min(Downbeat, Bar)), so a photo changes only ON an
    /// 8-bar downbeat, and a new track starts at cycle 0.</summary>
    sealed class SpotlightBody : Component
    {
        public Slab Slab = null!;
        public float W, H;
        public bool Preview;
        readonly string[] _urls = new string[Covers.Spot.MaxPhotos];
        readonly Signal<int> _layers = new(0);
        readonly Action _clearPrevious;
        SpotLayer _shown, _previous;
        int _shownSeq;
        // the clock's track-change guard: the slab's (Downbeat, Bar) as they stood at the change — still the OLD track's
        // until the visualizer clock ticks the new one, so they must not pick the new track's cycle
        int _clockSlot = int.MinValue, _staleDownbeat = int.MinValue, _staleBar = int.MinValue;

        public SpotlightBody() { _clearPrevious = ClearPrevious; }

        public override Element Render()
        {
            var ctx = UseContext(Stage.StageContext);
            _ = Entities.ScopeEpoch.Value;                       // FIRST: a scope switch re-points the table reads below
            uint epoch = Entities.ScopeEpoch.Peek();
            _ = Entities.Current.Artists.Changed.Value;
            _ = Entities.Current.Edges.ArtistGallery.Changed.Value;
            _ = _layers.Value;                                   // the fade-out timeout's "drop the previous layer"
            var track = ZuneTrack(ctx);
            var artist = Rail.NowPlayingArtist(track);
            int artistSlot = artist.Slot;
            // the gallery rides the artist overview (Spotify.Decode.Artist: an overview with no gallery lands it Complete-empty)
            UseEffect(() => { if (artistSlot > 0) Entities.Ensure(new Artist(artistSlot), ArtistFields.Overview); }, DepKey.From(artistSlot, (int)epoch));
            var clock = UseSignal(default(SpotClock));
            var slab = Slab; bool preview = Preview;
            // per bar (and per track change), allocation-free: re-renders only when the cycle, the preload or the track moves
            UseSignalEffect(() => { if (!preview) clock.SetIfChanged(Step(ZuneTrack(ctx), slab.Downbeat.Value, slab.Bar.Value)); });
            var sc = clock.Value;
            bool mine = !preview && sc.Slot == (track.IsValid ? track.Slot : 0);
            int cycle = mine ? sc.Cycle : 0;
            int n = Photos(artist, track, out bool blur);
            int tempo = track.IsValid && track.Knows(TrackFields.Audio) ? track.Tempo : 0;
            float span = Covers.Spot.SpanMs(tempo), fade = Covers.Spot.FadeMs(span);
            int decode = Covers.Spot.DecodeFor(W, preview);
            var kids = new List<Element>(3);
            if (n == 0)
                kids.Add(new BoxEl { Width = W, Height = H, Fill = Ink.ArtStandIn(""), HitTestVisible = false });
            else
            {
                string url = _urls[Covers.Spot.PhotoAt(cycle, n)];
                string key = "sp:" + cycle.ToString(CultureInfo.InvariantCulture) + ":" + url;
                if (!string.Equals(key, _shown.Key, StringComparison.Ordinal)) { _previous = _shown; _shown = new SpotLayer(key, url, cycle, blur); _shownSeq++; }
                bool hasPrevious = !preview && _previous.Key is { Length: > 0 };
                if (hasPrevious) kids.Add(Layer(_previous, fadeIn: false, span, fade, decode));
                kids.Add(Layer(_shown, fadeIn: hasPrevious, span, fade, decode));
                if (!preview && n > 1 && mine && sc.Preload)
                {
                    string next = _urls[Covers.Spot.PhotoAt(cycle + 1, n)];
                    kids.Add(new BoxEl { Key = "pre:" + next, Width = W, Height = H, ZStack = true, Opacity = 0f, HitTestVisible = false, Children = [SpotImage(next, decode, false)] });
                }
            }
            // the previous photo leaves once the cross-fade has landed (restarted by every new shown layer)
            UseTimeout(_clearPrevious, fade + 150f, DepKey.From(_shownSeq));
            return new BoxEl { Width = W, Height = H, ZStack = true, ClipToBounds = true, HitTestVisible = false, Children = kids.ToArray() };
        }

        void ClearPrevious()
        {
            if (_previous.Key is null) return;
            _previous = default;
            _layers.Value = _layers.Peek() + 1;
        }

        /// <summary>The clock for the slab's bar pair: a track change resets to cycle 0 and holds it while the bars are still
        /// the old track's; afterwards the cycle of min(Downbeat, Bar) (a new track's bar 0 wins over the old downbeat) and
        /// the preload window. The first run (a mount mid-song) trusts the bars.</summary>
        SpotClock Step(Track track, int downbeat, int bar)
        {
            int slot = track.IsValid ? track.Slot : 0;
            if (slot != _clockSlot)
            {
                bool mount = _clockSlot == int.MinValue;
                _clockSlot = slot;
                if (!mount) { _staleDownbeat = downbeat; _staleBar = bar; return new SpotClock(slot, 0, false); }
            }
            else if (downbeat == _staleDownbeat && bar == _staleBar) return new SpotClock(slot, 0, false);
            _staleDownbeat = _staleBar = int.MinValue;
            int cycle = Covers.Spot.CycleOf(Math.Min(downbeat, bar));
            int tempo = track.IsValid && track.Knows(TrackFields.Audio) ? track.Tempo : 0;
            return new SpotClock(slot, cycle, Covers.Spot.Preload(bar, cycle, Covers.Spot.SpanMs(tempo)));
        }

        Element Layer(SpotLayer l, bool fadeIn, float span, float fade, int decode)
        {
            float w = W, h = H; bool still = Preview;
            var pan = Covers.Spot.Pan(l.Cycle);
            return Embed.Comp(() => new SpotlightPhoto { Url = l.Url, W = w, H = h, Decode = decode, Blur = l.Blur, Fade = fadeIn, Still = still, Pan = pan, SpanMs = span, FadeMs = fade })
                with { Key = l.Key };
        }

        /// <summary>The gallery (capped), else the header/portrait, else the playing cover blurred (<paramref name="blur"/>).</summary>
        int Photos(Artist artist, Track track, out bool blur)
        {
            blur = false;
            int n = 0;
            if (artist.IsValid)
            {
                var gallery = artist.GallerySlots;
                for (int i = 0; i < gallery.Length && n < _urls.Length; i++)
                    if (Controls.ArtUrl(gallery[i]) is { } u) _urls[n++] = u;
                if (n == 0 && Controls.ArtUrl(artist.HeroImageId) is { } hero) _urls[n++] = hero;
            }
            if (n == 0 && track.IsValid && Controls.ArtUrl(track.ImageId) is { } cover) { _urls[n++] = cover; blur = true; }
            return n;
        }
    }

    /// <summary>A full-bleed photo. Its root carries the Ken Burns keyframes (uniform scale + pan over 1.25 spans, so it is
    /// still moving under the next cross-fade) and, when it arrives over a previous photo, an opacity Enter (kept under
    /// reduced motion — a fade orients, it does not move). Reduced or preview: the rest pose, no pan.</summary>
    sealed class SpotlightPhoto : Component
    {
        public string Url = "";
        public float W, H, SpanMs, FadeMs;
        public int Decode;
        public bool Blur, Fade, Still;
        public Covers.Spot.KenBurns Pan;

        public override Element Render()
        {
            bool still = Still || Design.Reduced;
            var p = Pan;
            float dur = MathF.Max(1f, SpanMs * 1.25f);
            Keyframe[] scale = still ? [new Keyframe(0f, p.ScaleFrom), new Keyframe(1f, p.ScaleFrom)] : [new Keyframe(0f, p.ScaleFrom), new Keyframe(1f, p.ScaleTo, Easing.Linear)];
            Keyframe[] tx = still ? [new Keyframe(0f, 0f), new Keyframe(1f, 0f)] : [new Keyframe(0f, 0f), new Keyframe(1f, p.Dx * W, Easing.Linear)];
            Keyframe[] ty = still ? [new Keyframe(0f, 0f), new Keyframe(1f, 0f)] : [new Keyframe(0f, 0f), new Keyframe(1f, p.Dy * H, Easing.Linear)];
            var key = DepKey.From(still);
            UseKeyframes(AnimChannel.ScaleX, scale, dur, loop: false, key);
            UseKeyframes(AnimChannel.ScaleY, scale, dur, loop: false, key);
            UseKeyframes(AnimChannel.TranslateX, tx, dur, loop: false, key);
            UseKeyframes(AnimChannel.TranslateY, ty, dur, loop: false, key);
            return new BoxEl
            {
                Width = W, Height = H, ZStack = true, HitTestVisible = false,
                Enter = Fade ? new EnterExit(Opacity: 0f, Active: true) : null,
                Transition = Fade ? MotionTokenDef.Eased(FadeMs, Easing.Linear, ReducedMotionPolicy.KeepFade) : null,
                Children = [SpotImage(Url, Decode, Blur)],
            };
        }
    }

    static ImageEl SpotImage(string url, int decode, bool blur) => Ui.Image(url, ImageFit.Cover, float.NaN, decode, 0f, Ink.ArtStandIn(url)) with
    {
        AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, BakedBlur = blur ? new BakedBlurSpec(40f, 0.5f) : null,
    };

    /// <summary>The artist name at 300/300, cropped by the bottom edge and drifting slowly; the title (22/600) and
    /// "album · year" (20) above it. Stage ink at 0.9 over the scrim.</summary>
    sealed class SpotlightType : Component
    {
        public float W, H;
        public bool Preview;

        public override Element Render()
        {
            var ctx = UseContext(Stage.StageContext);
            _ = Entities.ScopeEpoch.Value;
            _ = Entities.Current.Artists.Changed.Value;
            _ = Entities.Current.Albums.Changed.Value;
            var track = ZuneTrack(ctx);
            string name = ZuneLower(ZuneArtist(track));
            float k = H / ZuneRefH, nameSize = Preview ? 0.5f * H : 300f * k;
            // the line box is one em; its cap top sits ≈ 0.16 em down, and 20 % of the box hangs below the face's edge
            float top = H - 0.80f * nameSize;
            var kids = new List<CanvasChild>(3);
            if (name.Length > 0)
            {
                float size = nameSize, travel = Preview ? 0f : 60f * k;
                bool still = Preview;
                kids.Add(new CanvasChild(Preview ? -0.03f * W : -30f * k, top,
                    Embed.Comp(() => new SpotlightName { Word = name, Size = size, Travel = travel, Still = still }) with { Key = "name:" + name }));
            }
            if (!Preview && track.IsValid)
            {
                string album = ZuneAlbum(track);
                int year = track.Album.IsValid && track.Album.Year > 0 ? track.Album.Year : track.Year;
                string meta = year > 0 ? (album.Length > 0 ? album + " · " : "") + year.ToString(CultureInfo.InvariantCulture) : album;
                float x = 64f * k, metaY = top + 0.16f * nameSize - 20f * k - 28f * k, titleY = metaY - 30f * k;
                kids.Add(new CanvasChild(x, titleY, new TextEl(ZuneLower(track.Title))
                {
                    Size = 22f * k, LineHeight = 30f * k, Weight = 600, FontFamily = Design.Type.DisplayFace, Color = Ink.Ink with { A = 0.9f },
                    MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MaxWidth = W - 2f * x,
                }));
                if (meta.Length > 0)
                    kids.Add(new CanvasChild(x, metaY, new TextEl(ZuneLower(meta))
                    {
                        Size = 20f * k, LineHeight = 28f * k, FontFamily = Design.Type.DisplayFace, Color = Ink.InkSecondary,
                        MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MaxWidth = W - 2f * x,
                    }));
            }
            return new BoxEl { Width = W, Height = H, HitTestVisible = false, Children = [Canvas.Create(W, H, kids)] };
        }
    }

    /// <summary>The giant name: a 36 s ping-pong drift on keyframes (zero ticks). Reduced or preview: REST keys (the engine
    /// does not snap a looping track — the Field blob's rule).</summary>
    sealed class SpotlightName : Component
    {
        const float LoopMs = 36_000f;
        public string Word = "";
        public float Size, Travel;
        public bool Still;

        public override Element Render()
        {
            bool still = Still || Design.Reduced || Travel <= 0f;
            Keyframe[] keys = still
                ? [new Keyframe(0f, 0f), new Keyframe(1f, 0f)]
                : [new Keyframe(0f, 0f), new Keyframe(0.5f, -Travel, Easing.EaseInOut), new Keyframe(1f, 0f, Easing.EaseInOut)];
            UseKeyframes(AnimChannel.TranslateX, keys, LoopMs, loop: !still, DepKey.From(still));
            return new BoxEl
            {
                HitTestVisible = false,
                Children =
                [
                    new TextEl(Word)
                    {
                        Size = Size, LineHeight = Size, Weight = 300, FontFamily = Design.Type.DisplayFace, Color = Ink.Ink with { A = 0.9f },
                        Wrap = TextWrap.NoWrap, MaxLines = 1, Shrink = 0f,
                    },
                ],
            };
        }
    }
}
