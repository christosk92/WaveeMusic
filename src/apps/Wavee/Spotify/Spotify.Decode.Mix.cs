// ── Spotify/Spotify.Decode.Mix.cs — CORE (#48 Mix, plan §3.2, §3.3) ───────────────────────────────────────────────────
//
// The read side of Spotify's Mix, shapes from the capture report (§3.1, §3.5, §4.1). Five extension kinds and the
// mix lens of a playlist v2 read:
//
//   217 beats        {1 beats/bar, 2[] {1 float s, 3 pos-in-bar 1..4, 4 conf, 5 1.0 = downbeat}, 3 "32-hex id"}
//   28  cue points   {1 start cue, 2 end cue, 3[] candidates, 4[] candidates}, cue = {1 ms, 2 float bpm, 3 1, 4 double}
//   225 mix state    {1 1} when mixed, empty when not
//   170 auto lens    {1 "mix"} or empty
//   235 mixability   {1 1}
//   v2 read          list format attributes (mix, mix-type, automix.*, …), f22 applied lenses {1 name, 2 etag},
//                    item attribute 11 automix.auto_transition_recipe / automix.transition_uri on the OUTGOING item,
//                    item attribute 11 not-mixable.reason, item attribute 14 the set-transition signal template,
//                    contents.5[] {1 "mix-state", 2 {1 "mix"}} the list's mix-state signal template (lens reads only)
//
// The item flags (attribute 14 present = editable transition, not-mixable) are read where the item edge is staged
// (Spotify.Decode.cs, ItemAttributes). Never throws, never allocates beyond the staging arena.

namespace Wavee;

public static partial class Spotify
{
    public static partial class Decode
    {
        /// <summary>Kind 217 → <see cref="Edges.TrackBeats"/> in <see cref="BeatGrid"/>'s packing (start ms in bits 0..30,
        /// bit 31 = downbeat: f5 = 1.0, or position 1 in the bar when f5 is absent) and the analysis id (f3) as a
        /// <see cref="TraitRelation.TrackBeatsId"/> run. An answer with no beats lands an empty grid: that is an answer.</summary>
        public static void MixBeats(ReadOnlySpan<byte> proto, ReadOnlySpan<byte> entityUri, Staging s)
        {
            var track = Identity(s, entityUri);
            if (track.IsEmpty) return;
            uint[] beats = System.Buffers.ArrayPool<uint>.Shared.Rent(BeatGrid.MaxBeats);
            try
            {
                int n = 0;
                ReadOnlySpan<byte> analysisId = default;
                for (var r = new ProtoReader(proto); r.Next();)
                {
                    if (r.Field == 2 && r.Wire == 2)
                    {
                        float seconds = -1f, downbeat = -1f;
                        long position = 0;
                        for (var b = r.Message(); b.Next();)
                        {
                            if (b.Field == 1 && b.Wire == 5) seconds = BitConverter.UInt32BitsToSingle(b.Fixed32());
                            else if (b.Field == 3 && b.Wire == 0) position = (long)b.Varint();
                            else if (b.Field == 5 && b.Wire == 5) downbeat = BitConverter.UInt32BitsToSingle(b.Fixed32());
                            else b.Skip();
                        }
                        if (seconds < 0 || n >= BeatGrid.MaxBeats) continue;
                        long ms = (long)(seconds * 1000.0 + 0.5);
                        if (ms is < 0 or > BeatGrid.MsMask) continue;
                        bool isDown = downbeat >= 0 ? downbeat >= 0.5f : position == 1;
                        beats[n++] = (uint)ms | (isDown ? BeatGrid.DownbeatBit : 0u);
                    }
                    else if (r.Field == 3 && r.Wire == 2) analysisId = r.Bytes();
                    else r.Skip();
                }
                ref var run = ref s.TraitRuns.Add();
                run.Parent = track;
                run.Relation = TraitRelation.TrackBeats;
                run.Start = s.Traits.Count;
                run.Length = 0;
                if (n > 0) run.Bytes = s.AddText(System.Runtime.InteropServices.MemoryMarshal.AsBytes(beats.AsSpan(0, n)));
                if (!analysisId.IsEmpty && analysisId.Length <= 64)
                {
                    ref var idRun = ref s.TraitRuns.Add();
                    idRun.Parent = track;
                    idRun.Relation = TraitRelation.TrackBeatsId;
                    idRun.Start = s.Traits.Count;
                    idRun.Length = 0;
                    idRun.Bytes = s.AddText(analysisId);
                }
            }
            finally { System.Buffers.ArrayPool<uint>.Shared.Return(beats); }
        }

        /// <summary>Kind 28 → one <see cref="TrackCues"/> on <see cref="Edges.TrackCues"/>: the chosen start cue (f1) and
        /// end cue (f2), and the tempo the start cue states. The candidate lists are not kept.</summary>
        public static void Cuepoints(ReadOnlySpan<byte> proto, ReadOnlySpan<byte> entityUri, Staging s)
        {
            var track = Identity(s, entityUri);
            if (track.IsEmpty) return;
            uint start = 0, end = 0;
            float bpm = 0f;
            for (var r = new ProtoReader(proto); r.Next();)
            {
                if ((r.Field == 1 || r.Field == 2) && r.Wire == 2)
                {
                    bool isStart = r.Field == 1;
                    uint ms = 0;
                    float cueBpm = 0f;
                    for (var c = r.Message(); c.Next();)
                    {
                        if (c.Field == 1 && c.Wire == 0) ms = (uint)Math.Min(c.Varint(), uint.MaxValue);
                        else if (c.Field == 2 && c.Wire == 5) cueBpm = BitConverter.UInt32BitsToSingle(c.Fixed32());
                        else c.Skip();
                    }
                    if (isStart) { start = ms; if (cueBpm > 0) bpm = cueBpm; }
                    else { end = ms; if (bpm <= 0 && cueBpm > 0) bpm = cueBpm; }
                }
                else r.Skip();
            }
            ref var run = ref s.TraitRuns.Add();
            run.Parent = track;
            run.Relation = TraitRelation.TrackCues;
            run.Start = s.Traits.Count;
            run.Length = 0;
            if (start == 0 && end == 0) return;                      // answered, but no cue: the empty run says so
            var cue = new TrackCues(start, end, bpm is > 0 and < 6553 ? (ushort)(bpm * 10 + 0.5f) : (ushort)0);
            run.Bytes = s.AddText(System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<TrackCues>(in cue)));
        }

        /// <summary>Kinds 225, 170 and 235 on a PLAYLIST → its <see cref="PlaylistMixFlags"/> XM bit (each kind speaks only
        /// for its own bit) and its <see cref="PlaylistFields"/> group, so the planner stops asking.</summary>
        public static void PlaylistMixKind(Ext kind, ReadOnlySpan<byte> proto, ReadOnlySpan<byte> entityUri, Staging s)
        {
            var id = Identity(s, entityUri);
            if (id.IsEmpty || id.Kind(s) != EntityKind.Playlist) return;
            var r = new ProtoReader(proto);
            (PlaylistMixFlags bit, PlaylistFields group, bool on) = kind switch
            {
                Ext.MixState => (PlaylistMixFlags.XmMixState, PlaylistFields.MixState, r.Varint(1) == 1),
                Ext.AutoLens => (PlaylistMixFlags.XmAutoLens, PlaylistFields.MixLens, Is(new ProtoReader(proto).Bytes(1), "mix")),
                Ext.MixabilityTrait => (PlaylistMixFlags.XmMixable, PlaylistFields.Mixability, r.Varint(1) == 1),
                _ => (PlaylistMixFlags.None, (PlaylistFields)0, false),
            };
            if (bit == PlaylistMixFlags.None) return;
            ref var row = ref s.Playlists.RowFor(id, Authority.Full, (uint)group);
            row.MixFlagsMask |= (uint)bit;
            if (on) row.MixFlags |= (uint)bit;
        }

        /// <summary>The mix lens etag a list answer names in f22 (a full read, a signal's reply, or a <c>/diff</c> answer, which carries
        /// f6 and f22 only: protocol §2); null when it names no mix lens. PURE: the api thread's cache-identity check reads it.</summary>
        public static string? MixLensEtag(ReadOnlySpan<byte> selectedListContent)
        {
            ReadOnlySpan<byte> etag = MixLensEtagBytes(selectedListContent, out _);
            return etag.IsEmpty ? null : System.Text.Encoding.ASCII.GetString(etag);
        }

        /// <summary>f22, the applied lenses. Seen as `22 { {1 "auto" 2 "auto" 3{…}} {1 "mix" 2 "10b30ddc" 3{…}} }`: every f22 is read
        /// either as one lens entry or as a holder of entries. The mix lens's etag, or empty; <paramref name="sawLenses"/>: f22 was there.</summary>
        static ReadOnlySpan<byte> MixLensEtagBytes(ReadOnlySpan<byte> selectedListContent, out bool sawLenses)
        {
            ReadOnlySpan<byte> mixEtag = default;
            sawLenses = false;
            for (var r = new ProtoReader(selectedListContent); r.Next();)
            {
                if (r.Field != 22 || r.Wire != 2) { r.Skip(); continue; }
                sawLenses = true;
                var entry = r.Bytes();
                if (LensEntry(entry, out var name, out var etag)) { if (Is(name, "mix")) mixEtag = etag; continue; }
                for (var h = new ProtoReader(entry); h.Next();)
                {
                    if (h.Wire != 2) { h.Skip(); continue; }
                    if (LensEntry(h.Bytes(), out name, out etag) && Is(name, "mix")) mixEtag = etag;
                }
            }
            return mixEtag;
        }

        static bool LensEntry(ReadOnlySpan<byte> entry, out ReadOnlySpan<byte> name, out ReadOnlySpan<byte> etag)
        {
            name = etag = default;
            var r = new ProtoReader(entry);
            while (r.Next())
            {
                if (r.Field == 1 && r.Wire == 2) name = r.Bytes();
                else if (r.Field == 2 && r.Wire == 2) etag = r.Bytes();
                else r.Skip();
            }
            // A lens name is a short token ("auto", "mix"); a holder's f1 is a nested message, which never is.
            if (name.IsEmpty || name.Length > 32) return false;
            foreach (byte b in name) if (b < 0x20 || b > 0x7E) return false;
            return true;
        }

        /// <summary>The list half of a v2 read (beside <see cref="PlaylistFormatAttributes"/>): the Mix format attributes,
        /// f22's mix lens etag, and — when the answer was read THROUGH the mix lens — the stored recipes, signal templates and
        /// not-mixable reasons of its items and the list's mix-state template, as one staged run (a first page replaces the
        /// playlist's set; a later page of a truncated list adds to it).
        /// A diff answer with no attributes says nothing about the list attributes and stages none of them.</summary>
        static void MixList(ReadOnlySpan<byte> attributes, ReadOnlySpan<byte> lenses22Owner, ReadOnlySpan<byte> contents,
                            bool sawContents, in StagedId id, Staging s)
        {
            ReadOnlySpan<byte> mixEtag = MixLensEtagBytes(lenses22Owner, out bool sawLenses);
            bool lensApplied = !mixEtag.IsEmpty;
            if (attributes.IsEmpty && !sawLenses) return;

            ref var row = ref s.Playlists.RowFor(id, Authority.Full, attributes.IsEmpty ? 0u : (uint)PlaylistFields.Mix);
            uint flags = lensApplied ? (uint)PlaylistMixFlags.LensApplied : 0u;
            if (!attributes.IsEmpty)
            {
                for (var a = new ProtoReader(attributes); a.Next();)
                {
                    if (a.Field != 12 || a.Wire != 2) { a.Skip(); continue; }
                    a.Message().Fields(1, 2, out var key, out var value);
                    if (key.IsEmpty) continue;
                    bool yes = Is(value, "true");
                    if (Is(key, "mix")) { if (yes) flags |= (uint)PlaylistMixFlags.ListMix; }
                    else if (Is(key, "mix-type")) { if (Is(value, "allow-custom")) flags |= (uint)PlaylistMixFlags.AllowCustom; }
                    else if (Is(key, "has-custom-transitions")) { if (yes) flags |= (uint)PlaylistMixFlags.HasCustom; }
                    else if (Is(key, "can-view-transition")) { if (yes) flags |= (uint)PlaylistMixFlags.CanViewTransition; }
                    else if (Is(key, "automix.ignore_setting")) { if (yes) flags |= (uint)PlaylistMixFlags.IgnoreSetting; }
                    else if (Is(key, "automix.queue")) { if (yes) flags |= (uint)PlaylistMixFlags.AutomixQueue; }
                    else if (Is(key, "automix.autoplay")) { if (yes) flags |= (uint)PlaylistMixFlags.AutomixAutoplay; }
                }
                row.MixFlagsMask |= PlaylistMixRules.ListMask;
            }
            else row.MixFlagsMask |= (uint)PlaylistMixFlags.LensApplied;
            row.MixFlags |= flags;
            if (lensApplied) row.MixLensEtag = s.AddText(mixEtag);

            // The recipes and the signal templates: only a lens read carries them, and only an answer with contents speaks for
            // the items. Per item (contents.3[].2 attributes): 11 {key, value} pairs (the recipe, its transition uri,
            // not-mixable.reason), 12 the item id, 14 the set-transition template; on the list, contents.5[] its signals, of
            // which the lens adds {1 "mix-state", 2 {1 "mix"}}.
            if (!lensApplied || !sawContents) return;
            var recipes = s.MixRecipes;
            int first = recipes.Count;
            int pos = 0;
            ReadOnlySpan<byte> mixState = default;
            for (var c = new ProtoReader(contents); c.Next();)
            {
                if (c.Field == 1 && c.Wire == 0) { pos = c.Int32(); continue; }
                if (c.Field == 5 && c.Wire == 2)
                {
                    var signal = c.Bytes();
                    if (Is(new ProtoReader(signal).Bytes(1), "mix-state")) mixState = signal;
                    continue;
                }
                if (c.Field != 3 || c.Wire != 2) { c.Skip(); continue; }
                ReadOnlySpan<byte> itemId = default, recipe = default, uri = default, template = default, reason = default;
                for (var item = c.Message(); item.Next();)
                {
                    if (item.Field != 2 || item.Wire != 2) { item.Skip(); continue; }
                    for (var a = item.Message(); a.Next();)
                    {
                        if (a.Field == 12 && a.Wire == 2) itemId = a.Bytes();
                        else if (a.Field == 11 && a.Wire == 2)
                        {
                            a.Message().Fields(1, 2, out var key, out var value);
                            if (Is(key, "automix.auto_transition_recipe")) recipe = value;
                            else if (Is(key, "automix.transition_uri")) uri = value;
                            else if (Is(key, "not-mixable.reason")) reason = value;
                        }
                        else if (a.Field == 14 && a.Wire == 2)
                        {
                            var signal = a.Bytes();
                            if (Is(new ProtoReader(signal).Bytes(1), "set-transition")) template = signal;
                        }
                        else a.Skip();
                    }
                }
                if (itemId.IsEmpty || (recipe.IsEmpty && template.IsEmpty && reason.IsEmpty)) continue;
                ref var staged = ref recipes.Add();
                staged.Playlist = id;
                staged.ItemId = Hex(s, itemId);
                staged.Recipe = s.AddText(recipe);
                staged.TransitionUri = s.AddText(uri);
                staged.Template = s.AddText(template);
                staged.NotMixableReason = s.AddText(reason);
            }
            if (recipes.Count > first)
            {
                ref var head = ref recipes.Span[first];
                head.ListTemplate = s.AddText(mixState);
                if (pos > 0) head.Merge = true;
                return;
            }
            if (pos > 0) return;                                     // a later page with no item of the mix changes nothing
            ref var none = ref recipes.Add();                        // a first page with none: "this list has no edits"
            none.Playlist = id;
            none.ListTemplate = s.AddText(mixState);
        }
    }
}
