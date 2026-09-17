// ── Platform/Modules.cs ────────────────────────────────────────────────────────────────────────────────────────────
// the pure WatchPageModel, the module route grammar, PlayableLinks and the local-file probe
//
// Role: CORE
// Owner: T
// Wave: 6
// Budget: 400 lines
// Spec: ch 09 §8 / §9.5 (WatchPageModel, ModulePages route grammar, OpenUrlOf, PlayableLinks), flac plan §6
//
// Everything here is a DECISION a module surface makes, pure over `Wavee.Sdk` documents and packed ids, so a test asks
// it for an answer rather than scanning a view: which section of a watch document becomes the fact line, the
// description and the shelf; where a module route splits; which playable a page's stage would host; where a
// now-playing span of a module playable navigates. The host (`Modules.Host.cs`) owns the processes and caches; the
// page (`Modules.UI.cs`) only draws these answers.

using System.Text;
using Wavee.Sdk;

namespace Wavee;

public static partial class Modules
{
    // ══ 1. THE WATCH PAGE, PROJECTED (0.2.9 App/WatchPageModel.cs, verbatim in behaviour) ═══════════════════════════

    /// <summary>What the stage at the top of a watch page is PAINTING right now.</summary>
    public enum WatchStageKind : byte
    {
        /// <summary>The entity's own artwork plus one play affordance — this entity is not what is playing.</summary>
        Poster,
        /// <summary>The app's ONE video surface, hosted in the page (this entity IS the playing item).</summary>
        Live,
    }

    /// <summary>One capsule under the caption — a <see cref="PageAction"/> carried verbatim; an unknown kind is the
    /// caller's to skip.</summary>
    public readonly record struct WatchChip(string Id, string Kind, string Label, bool Primary, string? PlayableId, string? Url);

    /// <summary>One cell of the 16:9 shelf: the fields a video card draws. <see cref="IsLive"/> is carried and not drawn
    /// (ch 09 §9.4 records that as a decision).</summary>
    public readonly record struct WatchItem(string Title, string? Subtitle, string? ImageUrl, string? PlayableId,
                                            string? EntityId, string? Meta, bool IsLive);

    /// <summary>A watch document's every layout DECISION: the template gate, the channel identity (hero first, then the
    /// legacy one-card shelf), the dissolved fact line (VALUES only), the description, playables-outrank-cards for the
    /// shelf, and the whitespace rule. Defensive by construction — a page arrives over a pipe.</summary>
    public sealed record WatchPageModel(
        string Title, string? MetaLine, bool IsLive,
        string? ChannelName, string? ChannelAvatarUrl, string? ChannelEntityId,
        string? PosterUrl, string? FactLine, string? Description, string? ShelfTitle,
        WatchChip[] Chips, WatchItem[] Shelf, WatchStageKind Stage)
    {
        /// <summary>The separator the fact values are joined with — the meta line's own dot.</summary>
        public const string FactSeparator = " · ";

        static readonly WatchChip[] NoChips = [];
        static readonly WatchItem[] NoItems = [];

        /// <summary>The module-private PLAYABLE id this page's stage would host, or null (not a watch document, or no
        /// play action). A module's entity ids and playable ids are different namespaces (<c>video:abc</c> vs
        /// <c>abc</c>); the arbitration compares against the playing uri, so this is that one id space at its source.</summary>
        public static string? StagePlayableIdOf(ModulePageDoc? doc)
        {
            if (doc is null || !string.Equals(doc.Template, ModulePageDoc.TemplateWatch, StringComparison.Ordinal)) return null;
            PageAction[] actions = doc.Actions ?? [];
            for (int i = 0; i < actions.Length; i++)
            {
                if (actions[i] is not { } a || !string.Equals(a.Kind, PageAction.KindPlay, StringComparison.Ordinal)) continue;
                if (Trimmed(a.PlayableId) is { } playableId) return playableId;
            }
            return null;
        }

        /// <summary>Project <paramref name="doc"/> onto the watch layout, or null when it is not a watch document (the
        /// entity layout draws instead). The template is the ONE gate: the app never guesses from the data's shape.</summary>
        public static WatchPageModel? From(ModulePageDoc? doc, bool isPlayingEntity)
        {
            if (doc is null || !string.Equals(doc.Template, ModulePageDoc.TemplateWatch, StringComparison.Ordinal)) return null;
            PageHero? hero = doc.Hero;
            PageSection[] sections = doc.Sections ?? [];

            string? channelName = Trimmed(hero?.Subtitle);
            string? channelAvatar = Trimmed(hero?.AvatarUrl);
            string? channelEntity = Trimmed(hero?.SubtitleEntityId);
            int channelCard = -1;
            if (channelEntity is null && FindChannelCard(sections) is (int index, PageItem card))
            {
                channelCard = index;
                channelEntity = Trimmed(card.EntityId);
                channelName ??= Trimmed(card.Title);
                channelAvatar ??= Trimmed(card.ImageUrl);
            }
            // No NAME ⇒ no identity at all, and the one-card section goes back to the shelf.
            if (channelName is null) { channelAvatar = null; channelEntity = null; channelCard = -1; }

            string? factLine = null, description = null;
            int shelfIndex = -1;
            bool shelfIsPlayables = false;
            for (int i = 0; i < sections.Length; i++)
            {
                if (sections[i] is not { } section) continue;
                switch (section.Kind)
                {
                    case PageSection.KindFacts: factLine ??= JoinFactValues(section.Rows); break;
                    case PageSection.KindText: description ??= Trimmed(section.Text); break;
                    case PageSection.KindPlayables:
                        if (!shelfIsPlayables && HasItems(section)) { shelfIndex = i; shelfIsPlayables = true; }
                        break;
                    case PageSection.KindCards:
                        if (shelfIndex < 0 && i != channelCard && HasItems(section)) shelfIndex = i;
                        break;
                }
            }

            PageSection? shelfSection = shelfIndex >= 0 ? sections[shelfIndex] : null;
            return new WatchPageModel(
                Title: Trimmed(hero?.Title) ?? "",
                MetaLine: Trimmed(hero?.MetaLine),
                IsLive: hero?.IsLive ?? false,
                ChannelName: channelName,
                ChannelAvatarUrl: channelAvatar,
                ChannelEntityId: channelEntity,
                PosterUrl: Trimmed(hero?.ImageUrl),
                FactLine: factLine,
                Description: description,
                ShelfTitle: Trimmed(shelfSection?.Title),
                Chips: ChipsOf(doc.Actions),
                Shelf: ItemsOf(shelfSection),
                Stage: isPlayingEntity ? WatchStageKind.Live : WatchStageKind.Poster);
        }

        /// <summary>The first cards section carrying EXACTLY ONE item with both an entity id and a title — never the
        /// first cell of a related shelf.</summary>
        static (int Index, PageItem Card)? FindChannelCard(PageSection[] sections)
        {
            for (int i = 0; i < sections.Length; i++)
            {
                if (sections[i] is not { } section || !string.Equals(section.Kind, PageSection.KindCards, StringComparison.Ordinal)) continue;
                if (section.Items is not { Length: 1 } items || items[0] is not { } item) continue;
                if (Trimmed(item.EntityId) is null || Trimmed(item.Title) is null) continue;
                return (i, item);
            }
            return null;
        }

        static string? JoinFactValues(string[][]? rows)
        {
            if (rows is not { Length: > 0 }) return null;
            var sb = new StringBuilder();
            for (int i = 0; i < rows.Length; i++)
            {
                if (rows[i] is not { Length: >= 2 } row || Trimmed(row[1]) is not { } value) continue;
                if (sb.Length > 0) sb.Append(FactSeparator);
                sb.Append(value);
            }
            return sb.Length == 0 ? null : sb.ToString();
        }

        static bool HasItems(PageSection section)
        {
            if (section.Items is not { Length: > 0 } items) return false;
            for (int i = 0; i < items.Length; i++)
                if (items[i] is { } item && Trimmed(item.Title) is not null) return true;
            return false;
        }

        static WatchChip[] ChipsOf(PageAction[]? actions)
        {
            if (actions is not { Length: > 0 }) return NoChips;
            var chips = new List<WatchChip>(actions.Length);
            for (int i = 0; i < actions.Length; i++)
            {
                if (actions[i] is not { } a || Trimmed(a.Label) is not { } label || Trimmed(a.Kind) is not { } kind) continue;
                chips.Add(new WatchChip(a.Id ?? "", kind, label, a.Primary, Trimmed(a.PlayableId), Trimmed(a.Url)));
            }
            return chips.Count == 0 ? NoChips : chips.ToArray();
        }

        static WatchItem[] ItemsOf(PageSection? section)
        {
            if (section?.Items is not { Length: > 0 } items) return NoItems;
            var cells = new List<WatchItem>(items.Length);
            for (int i = 0; i < items.Length; i++)
            {
                if (items[i] is not { } item || Trimmed(item.Title) is not { } title) continue;
                cells.Add(new WatchItem(title, Trimmed(item.Subtitle), Trimmed(item.ImageUrl), Trimmed(item.PlayableId),
                    Trimmed(item.EntityId), Trimmed(item.Meta), item.IsLive));
            }
            return cells.Count == 0 ? NoItems : cells.ToArray();
        }
    }

    /// <summary>A string that is actually THERE, or null — whitespace-only buys no row.</summary>
    public static string? Trimmed(string? s)
    {
        if (s is null) return null;
        string t = s.Trim();
        return t.Length == 0 ? null : t;
    }

    // ══ 2. THE ROUTE GRAMMAR (0.2.9 Backend/Modules/ModulePages.cs, the algebra half) ════════════════════════════════

    /// <summary><c>module:</c> + <c>wavee:module:&lt;id&gt;:&lt;b64url(entityId)&gt;</c> — the route family, and the
    /// small rules every module surface asks of a document.</summary>
    public static partial class Pages
    {
        public const string RoutePrefix = "module:";

        /// <summary>The manifest capability a module declares to answer <c>module/page</c>. Declared, never probed.</summary>
        public const string PagesCapability = "pages";

        public static bool IsRoute(string? routeKey)
            => routeKey is { Length: > 0 } && routeKey.Length > RoutePrefix.Length
               && routeKey.StartsWith(RoutePrefix, StringComparison.Ordinal);

        /// <summary>The page uri a route key addresses, or null.</summary>
        public static string? UriOf(string? routeKey) => IsRoute(routeKey) ? routeKey![RoutePrefix.Length..] : null;

        public static bool TryParseRoute(string? routeKey, out string moduleId, out string entityId)
        {
            moduleId = string.Empty;
            entityId = string.Empty;
            return UriOf(routeKey) is { } uri && ModuleUri.TryDecode(uri, out moduleId, out entityId);
        }

        /// <summary>The route key for one module-private entity id, or null when either half is missing — a link with
        /// nowhere to go must be INERT, never a route that paints as a fallback.</summary>
        public static string? RouteForEntity(string? moduleId, string? entityId)
            => moduleId is { Length: > 0 } && entityId is { Length: > 0 }
                ? RoutePrefix + ModuleUri.Encode(moduleId, entityId)
                : null;

        /// <summary>The PLAYABLE uri a page's stage would host, or "" (the id space the playing uri speaks).</summary>
        public static string StagePlayableUri(string moduleId, ModulePageDoc? doc)
            => WatchPageModel.StagePlayableIdOf(doc) is { } playableId ? ModuleUri.Encode(moduleId, playableId) : "";

        /// <summary>Is the item in the bar the very thing this page's stage would host? One ordinal compare.</summary>
        public static bool IsPlayingEntity(string stagePlayable, string? nowUri)
            => stagePlayable.Length > 0 && nowUri is { Length: > 0 } && string.Equals(stagePlayable, nowUri, StringComparison.Ordinal);

        /// <summary>The FIRST <c>openUrl</c> action whose url passes the web guard — the failed state's escape hatch and
        /// the "Open on &lt;Module&gt;" menu row.</summary>
        public static string? OpenUrlOf(ModulePageDoc? doc)
        {
            PageAction[] actions = doc?.Actions ?? [];
            for (int i = 0; i < actions.Length; i++)
                if (actions[i] is { } a && string.Equals(a.Kind, PageAction.KindOpenUrl, StringComparison.Ordinal)
                    && Actions.PlayLinkRules.IsWebUrl(a.Url))
                    return a.Url;
            return null;
        }

        /// <summary>The three templates (ch 09 §0.16): <c>custom</c> wears a hero ONLY when the module supplied one;
        /// <c>entity</c> always does, synthesised from the navigating surface's label (or the fallback title) when the
        /// document carries none.</summary>
        public static PageHero? HeroFor(ModulePageDoc doc, string? routeArg, string fallbackTitle)
        {
            if (doc.Hero is { } hero) return hero;
            if (string.Equals(doc.Template, ModulePageDoc.TemplateCustom, StringComparison.Ordinal)) return null;
            return new PageHero(Trimmed(routeArg) ?? fallbackTitle, null, null, null, null, false);
        }

        /// <summary>The shared item budget, spent in document order (ch 09 §0.18): how many of <paramref name="count"/>
        /// entries a block may take from <paramref name="remaining"/>.</summary>
        public static int Take(int count, int remaining) => count <= 0 || remaining <= 0 ? 0 : Math.Min(count, remaining);
    }

    // ══ 3. PLAYABLE LINKS (0.2.9 Actions/PlayableLinks.cs, the module arms; the Spotify arms are Shell.LinkFor) ═════

    public static partial class PlayableLinks
    {
        /// <summary>Does this identity belong to a playback module? The uri routes; nothing else is asked.</summary>
        public static bool IsModule(EntityId id) => id.Provider == EntityProvider.Module;

        /// <summary>The route KEY a module playable's identity slot navigates to, from its resolve answer: art and title
        /// open the playable's own page, the subtitle its publisher. Null ⇒ the span stays inert (a module that named no
        /// page, an unresolved playable, a non-module uri).</summary>
        public static string? RouteKeyFor(string? playableUri, Shell.LinkSlot slot, ResolvedPlayable? resolved)
        {
            if (!ModuleUri.TryDecode(playableUri, out string moduleId, out _) || resolved is null) return null;
            string? entityId = slot == Shell.LinkSlot.Artist ? resolved.SubtitleEntityId : resolved.PageEntityId;
            return Pages.RouteForEntity(moduleId, entityId);
        }

        /// <summary>The display name that rides the route (the tab strip and the breadcrumb): the subtitle's first artist,
        /// else the title.</summary>
        public static string? LabelFor(Shell.LinkSlot slot, ResolvedPlayable? resolved)
            => resolved is null ? null
             : slot == Shell.LinkSlot.Artist && resolved.Artists is { Length: > 0 } a ? Trimmed(a[0]) ?? resolved.Title
             : resolved.Title;

        /// <summary>The user-facing extension gate for a dropped/picked audio file.</summary>
        public static bool IsSupportedAudioFile(ReadOnlySpan<char> path)
            => path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".flac", StringComparison.OrdinalIgnoreCase);

        /// <summary>What the first bytes of a local FLAC say (flac plan §6): duration, rate, depth and the tag ranges.</summary>
        public readonly record struct LocalProbe(Spotify.Audio.Format Format, long DurationMs, int SampleRate, byte Bps,
                                                 Playback.Flac.Tags Tags);

        public static LocalProbe ProbeFlac(ReadOnlySpan<byte> head)
        {
            Playback.Flac.Headers h = Playback.Flac.ParseHeaders(head, Span<Playback.Flac.SeekPoint>.Empty);
            if (!h.Valid) return default;
            var format = h.Info.Bps > 16 ? Spotify.Audio.Format.Flac24 : Spotify.Audio.Format.Flac;
            return new LocalProbe(format, h.Info.DurationMs, h.Info.SampleRate, h.Info.Bps, h.Tags);
        }
    }

    /// <summary>G-212: is this row a playback-module playable? The player bar and the stage pass it to
    /// <c>Shell.LinkFor</c> so a module span never falls through to the Spotify arms.</summary>
    public static bool IsModulePlayable(EntityId id) => PlayableLinks.IsModule(id);

    /// <summary>The route a module track's identity slot navigates to, read from the process-wide resolve cache, or null
    /// for an inert span (and for every non-module track). UI thread (the parse interns the display name).</summary>
    public static Shell.Route? LinkRouteFor(Track track, Shell.LinkSlot slot)
    {
        if (!track.IsValid || !IsModulePlayable(track.Id)) return null;
        string uri = track.Id.Text;
        ResolvedPlayable? resolved = Playables.Get(uri);
        if (PlayableLinks.RouteKeyFor(uri, slot, resolved) is not { } key) return null;
        return Shell.Parse(key, PlayableLinks.LabelFor(slot, resolved));
    }
}
