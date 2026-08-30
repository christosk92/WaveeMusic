using System;
using System.Collections.Generic;
using System.Text;
using Wavee.Sdk;

namespace Wavee;

/// <summary>What the stage at the top of a watch page is PAINTING right now.</summary>
public enum WatchStageKind
{
    /// <summary>The entity's own artwork plus one play affordance — this entity is not what is playing.</summary>
    Poster,

    /// <summary>The app's ONE video surface, hosted in the page. Reached only when the page's entity IS the playing
    /// item, which is the single condition <c>DockedVideoHosting.PageStageHosts</c> arbitrates.</summary>
    Live,
}

/// <summary>One capsule under the caption — a projection of a <see cref="PageAction"/>, carried verbatim so the view
/// never has to know a document shape and the model never has to know a control.</summary>
/// <param name="Id">The action's module-private id, echoed back on <c>module/action</c>.</param>
/// <param name="Kind"><see cref="PageAction.KindPlay"/> / <see cref="PageAction.KindOpenUrl"/> /
/// <see cref="PageAction.KindModuleAction"/>, unmapped: an unknown kind is the CALLER's to skip, exactly as the
/// entity layout already skips one.</param>
/// <param name="Label">The capsule's text.</param>
/// <param name="Primary">True for the accent capsule (at most one, by the document's own contract).</param>
/// <param name="PlayableId">What <see cref="PageAction.KindPlay"/> plays.</param>
/// <param name="Url">Where <see cref="PageAction.KindOpenUrl"/> goes; still subject to the app's http(s) guard.</param>
public readonly record struct WatchChip(string Id, string Kind, string Label, bool Primary, string? PlayableId, string? Url);

/// <summary>One cell of the shelf under the description card. A flattened <see cref="PageItem"/>: the fields a 16:9
/// card actually draws, with nothing the shelf cannot use.</summary>
/// <param name="Title">The cell's title.</param>
/// <param name="Subtitle">Its secondary line (a channel name).</param>
/// <param name="ImageUrl">Its 16:9 thumbnail.</param>
/// <param name="PlayableId">What the cell PLAYS, or null.</param>
/// <param name="EntityId">The module page the cell NAVIGATES to, or null.</param>
/// <param name="Meta">A short trailing fact (a duration, a viewer count).</param>
/// <param name="IsLive">True when the cell is itself a live stream.</param>
public readonly record struct WatchItem(string Title, string? Subtitle, string? ImageUrl, string? PlayableId,
                                        string? EntityId, string? Meta, bool IsLive);

/// <summary>
/// The WATCH page, projected out of a module's <see cref="ModulePageDoc"/> — every layout DECISION a YouTube-shaped
/// page makes, and nothing that needs a GPU. It is deliberately pure (System + <c>Wavee.Sdk</c>, no FluentGpu type
/// anywhere) so it compiles into the test assembly and the decisions are asserted as VALUES rather than scanned for in
/// the view's source text, which is the only kind of gate this repo accepts.
///
/// <para><b>The dissolve.</b> A watch document is the SAME document an entity page draws — the template is a request
/// for a different reading of it, never a second schema. So the sections are re-homed rather than re-authored: the
/// first <see cref="PageSection.KindFacts"/> block collapses from a row of grey tiles into <see cref="FactLine"/> (the
/// VALUES only — "1.2M views", "2 days ago"; the labels were the tiles' whole reason to exist), the first
/// <see cref="PageSection.KindText"/> block becomes <see cref="Description"/> inside the card under it, and the first
/// <see cref="PageSection.KindPlayables"/> block — or, failing that, the first <see cref="PageSection.KindCards"/> one
/// — becomes the 16:9 <see cref="Shelf"/>.</para>
///
/// <para><b>The channel fallback is not politeness, it is compatibility.</b> Before <see cref="PageHero.AvatarUrl"/>
/// and <see cref="PageHero.SubtitleEntityId"/> existed, a module could only link onward to its channel through a
/// one-card <see cref="PageSection.KindCards"/> shelf, because the hero carried no id at all. An un-updated module
/// still emits exactly that, so the projection reads it and turns it into the channel row — and then must NOT also
/// hand the same card to the shelf, or the page shows the channel twice and calls the second one "related".</para>
///
/// <para><b>Defensive by construction.</b> Every array may be null, every string may be blank, the hero may be
/// missing, a section may be null in the middle of the list: a page arrives over a pipe from an out-of-process module,
/// so "the SDK validated it" is only true of a module that used the SDK.</para>
/// </summary>
/// <param name="Title">The video's title — the caption's first line.</param>
/// <param name="MetaLine">The dot-separated facts line under the title, or null.</param>
/// <param name="IsLive">True to draw the LIVE pill beside the meta line.</param>
/// <param name="ChannelName">The owner's display name, or null when there is no channel row to draw.</param>
/// <param name="ChannelAvatarUrl">The owner's circular picture, or null (the row then shows initials).</param>
/// <param name="ChannelEntityId">The module-namespaced entity the channel row navigates to, or null — a row with a
/// name but no id is INERT text, never a link to nowhere.</param>
/// <param name="PosterUrl">The entity's own artwork, painted as the stage's ground under the video hole.</param>
/// <param name="FactLine">The dissolved facts row, values joined with <c>" · "</c>, or null.</param>
/// <param name="Description">The prose body of the description card, or null.</param>
/// <param name="ShelfTitle">The shelf's heading, or null for an unheaded shelf.</param>
/// <param name="Chips">The document's actions, in document order, as capsules.</param>
/// <param name="Shelf">The 16:9 cells under the description card.</param>
/// <param name="Stage">What the stage paints — see <see cref="WatchStageKind"/>.</param>
public sealed record WatchPageModel(
    string Title, string? MetaLine, bool IsLive,
    string? ChannelName, string? ChannelAvatarUrl, string? ChannelEntityId,
    string? PosterUrl, string? FactLine, string? Description, string? ShelfTitle,
    WatchChip[] Chips, WatchItem[] Shelf, WatchStageKind Stage)
{
    /// <summary>The separator the dissolved fact values are joined with — the same dot the meta line already uses, so
    /// a module that pre-joined its facts into <see cref="PageHero.MetaLine"/> and one that shipped them as a facts
    /// section read identically on the page.</summary>
    public const string FactSeparator = " · ";

    static readonly WatchChip[] NoChips = [];
    static readonly WatchItem[] NoItems = [];

    /// <summary>
    /// The module-private PLAYABLE id this page's stage would host, or <b>null</b> when it would host nothing (the
    /// document is not a watch page, or it names no play action).
    ///
    /// <para><b>Why this is not the page's entity id.</b> A module's entity ids and its playable ids are different
    /// namespaces by design — YouTube's page is entity <c>video:tRsQsTMvPNg</c> while the thing that plays is playable
    /// <c>tRsQsTMvPNg</c> — and the document does not state its own playable anywhere except on its play ACTION. The
    /// arbitration that decides whether the stage or the rail hosts the one video surface compares against
    /// <c>PlaybackBridge.CurrentTrack.Uri</c>, which is always a PLAYABLE uri; taking the entity id there made the two
    /// terms permanently unequal on the one module the feature exists for, so the stage never lit while its own video
    /// played. This is that ONE id space, at its source.</para>
    /// </summary>
    /// <param name="doc">The module's page document, or null.</param>
    public static string? StagePlayableIdOf(ModulePageDoc? doc)
    {
        if (doc is null) return null;
        if (!string.Equals(doc.Template, ModulePageDoc.TemplateWatch, StringComparison.Ordinal)) return null;
        PageAction[] actions = doc.Actions ?? [];
        for (int i = 0; i < actions.Length; i++)
        {
            if (actions[i] is not { } a) continue;
            if (!string.Equals(a.Kind, PageAction.KindPlay, StringComparison.Ordinal)) continue;
            if (Trimmed(a.PlayableId) is { } playableId) return playableId;
        }
        return null;
    }

    /// <summary>Projects <paramref name="doc"/> onto the watch layout, or returns <b>null</b> when the document is not
    /// a watch page — the caller then draws the existing entity layout, which is what every non-video module still
    /// gets. The template value is the ONE gate: a module opts in, the app never guesses from the shape of the data.</summary>
    /// <param name="doc">The document the module answered <c>module/page</c> with, or null while it is still loading.</param>
    /// <param name="isPlayingEntity">True when this page's entity IS the item in the player bar, which is what makes
    /// the stage <see cref="WatchStageKind.Live"/> instead of a poster.</param>
    public static WatchPageModel? From(ModulePageDoc? doc, bool isPlayingEntity)
    {
        if (doc is null) return null;
        if (!string.Equals(doc.Template, ModulePageDoc.TemplateWatch, StringComparison.Ordinal)) return null;

        PageHero? hero = doc.Hero;
        PageSection[] sections = doc.Sections ?? [];

        // ── the channel row, hero-first then the legacy one-card shelf ────────────────────────────────────────────
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

        // A row with no NAME is not a row: an avatar circle beside nothing reads as a rendering bug, and it would also
        // consume the card the shelf could have used. Drop the whole identity together rather than half of it.
        if (channelName is null)
        {
            channelAvatar = null;
            channelEntity = null;
            channelCard = -1;
        }

        // ── the dissolved sections ────────────────────────────────────────────────────────────────────────────────
        string? factLine = null;
        string? description = null;
        int shelfIndex = -1;
        bool shelfIsPlayables = false;

        for (int i = 0; i < sections.Length; i++)
        {
            if (sections[i] is not { } section) continue;
            switch (section.Kind)
            {
                case PageSection.KindFacts:
                    factLine ??= JoinFactValues(section.Rows);
                    break;

                case PageSection.KindText:
                    description ??= Trimmed(section.Text);
                    break;

                case PageSection.KindPlayables:
                    // Playables OUTRANK cards whatever their order in the document: a shelf of things you can play is
                    // the up-next rail, and a card shelf is the consolation prize when the module shipped no such list.
                    if (!shelfIsPlayables && HasItems(section))
                    {
                        shelfIndex = i;
                        shelfIsPlayables = true;
                    }
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

    // ── projection helpers ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The first <see cref="PageSection.KindCards"/> section carrying EXACTLY ONE item with an entity id —
    /// the pre-<see cref="PageHero.SubtitleEntityId"/> spelling of "and here is the channel". One item, never the
    /// first of many: a related-videos shelf's first card also has an entity id, and promoting it to the channel row
    /// would relabel a recommendation as the author.</summary>
    static (int Index, PageItem Card)? FindChannelCard(PageSection[] sections)
    {
        for (int i = 0; i < sections.Length; i++)
        {
            if (sections[i] is not { } section) continue;
            if (!string.Equals(section.Kind, PageSection.KindCards, StringComparison.Ordinal)) continue;
            if (section.Items is not { Length: 1 } items) continue;
            if (items[0] is not { } item) continue;
            if (Trimmed(item.EntityId) is null || Trimmed(item.Title) is null) continue;
            return (i, item);
        }
        return null;
    }

    /// <summary>The facts row, dissolved: the VALUES only. The labels ("Views", "Published") existed to caption a grey
    /// tile; on a watch page the numbers sit in one bold line where the tile grid used to be, and a label per number
    /// would double the line's length to say what "1.2M views" already says.</summary>
    static string? JoinFactValues(string[][]? rows)
    {
        if (rows is not { Length: > 0 }) return null;
        var sb = new StringBuilder();
        for (int i = 0; i < rows.Length; i++)
        {
            if (rows[i] is not { Length: >= 2 } row) continue;
            if (Trimmed(row[1]) is not { } value) continue;
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
            if (actions[i] is not { } a) continue;
            if (Trimmed(a.Label) is not { } label) continue;
            if (Trimmed(a.Kind) is not { } kind) continue;   // an action with no kind cannot be honoured by anyone
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
            if (items[i] is not { } item) continue;
            if (Trimmed(item.Title) is not { } title) continue;
            cells.Add(new WatchItem(title, Trimmed(item.Subtitle), Trimmed(item.ImageUrl),
                Trimmed(item.PlayableId), Trimmed(item.EntityId), Trimmed(item.Meta), item.IsLive));
        }
        return cells.Count == 0 ? NoItems : cells.ToArray();
    }

    /// <summary>A string that is actually THERE, or null. Whitespace-only is null on purpose: a module that pads a
    /// missing field with a space must not buy a channel row, a fact line or a description card with it.</summary>
    static string? Trimmed(string? s)
    {
        if (s is null) return null;
        string t = s.Trim();
        return t.Length == 0 ? null : t;
    }
}
