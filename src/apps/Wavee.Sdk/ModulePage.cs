using System.Text.Json;
using System.Text.Json.Serialization;
using Wavee.Sdk.Protocol;

namespace Wavee.Sdk;

/// <summary>
/// A page a module describes and the app renders. Modules are out-of-process and untrusted-by-construction, so a
/// page is never code and never markup: it is a small declarative document (a hero, some actions, a list of typed
/// sections) that the app draws with its own controls, exactly the posture the sidebar extension platform takes for
/// contributed content.
/// <para>
/// Entity ids are module-namespaced strings — YouTube uses <c>video:&lt;id&gt;</c> / <c>channel:&lt;id&gt;</c>,
/// Twitch <c>channel:&lt;login&gt;</c>, Radio <c>station:&lt;url&gt;</c>. The app routes them as
/// <c>module:</c> + <see cref="ModuleUri.Encode"/>, so a module owns its own id space completely.
/// </para>
/// </summary>
/// <param name="Version">Document version; <see cref="CurrentVersion"/> today. A newer document is still rendered
/// best-effort, because unknown members and unknown section kinds are skipped rather than fatal.</param>
/// <param name="Template">Which layout to draw: <see cref="TemplateEntity"/> (hero + actions + sections) or
/// <see cref="TemplateCustom"/> (sections only; the hero is optional).</param>
/// <param name="Hero">The identity block at the top of the page, or null for a page with no hero.</param>
/// <param name="Actions">Buttons under the hero; at most one should be <see cref="PageAction.Primary"/>.</param>
/// <param name="Sections">The body, in order. A section whose <see cref="PageSection.Kind"/> the app does not know
/// is skipped, so a module may ship a new kind before the app understands it.</param>
/// <param name="ExpiresAtUnixMs">When the page's contents go stale; the app caches it until then (10 minutes by
/// default). Null means "cache for the default window".</param>
public sealed record ModulePageDoc(
    int Version,
    string Template,
    PageHero? Hero,
    PageAction[] Actions,
    PageSection[] Sections,
    long? ExpiresAtUnixMs)
{
    /// <summary>The document version this SDK build writes.</summary>
    public const int CurrentVersion = 1;

    /// <summary><see cref="Template"/> value for the standard hero + actions + sections layout.</summary>
    public const string TemplateEntity = "entity";

    /// <summary><see cref="Template"/> value for a sections-only page.</summary>
    public const string TemplateCustom = "custom";

    /// <summary>
    /// <see cref="Template"/> value for a WATCH page — an entity whose identity IS its picture (a video, a live
    /// stream). The app draws the same document differently: a full-width 16:9 stage at the top (the live video
    /// itself once that entity is playing, a poster and one play affordance before), then the title, the channel
    /// row, the actions as capsules, the <see cref="PageSection.KindFacts"/> row folded into the top of the
    /// description card, and <see cref="PageSection.KindPlayables"/> as a 16:9 shelf.
    /// <para>It is a REQUEST, not a guarantee: an app that does not know this value falls back to
    /// <see cref="TemplateEntity"/>, so a module may emit it before every app understands it. Emit it only when the
    /// entity really is video-first — a radio station with no picture reads better as an entity page.</para>
    /// </summary>
    public const string TemplateWatch = "watch";
}

/// <summary>The identity block at the top of a page: art, a title, and the one line that says what this is.</summary>
/// <param name="Title">The page's title.</param>
/// <param name="Eyebrow">Small label above the title, e.g. <c>"Channel"</c> or <c>"Station"</c>.</param>
/// <param name="Subtitle">The line under the title (a channel name, a station's genre); rendered as a link when the
/// page also carries a subtitle entity to navigate to.</param>
/// <param name="ImageUrl">Absolute artwork url, or null.</param>
/// <param name="MetaLine">A dot-separated facts line, e.g. <c>"Live · 12,345 watching"</c>.</param>
/// <param name="IsLive">True to draw the LIVE badge next to the title.</param>
/// <param name="AvatarUrl">Absolute url of the OWNER's picture — a channel avatar, a station logo — drawn as the
/// circle beside <paramref name="Subtitle"/>. Distinct from <paramref name="ImageUrl"/>, which is the entity's own
/// artwork (a video thumbnail); a watch page shows both at once, which is why one field cannot serve both.</param>
/// <param name="SubtitleEntityId">The module-namespaced entity id <paramref name="Subtitle"/> navigates to, e.g.
/// <c>channel:UC…</c>. Before this existed a page could only link onward through a one-card
/// <see cref="PageSection.KindCards"/> shelf, because the hero carried no id of its own; that shelf still works and
/// is what an older module falls back to.</param>
public sealed record PageHero(
    string Title,
    string? Eyebrow,
    string? Subtitle,
    string? ImageUrl,
    string? MetaLine,
    bool IsLive,
    // Trailing + optional on purpose: `DefaultIgnoreCondition = WhenWritingNull` then keeps an older module's bytes
    // byte-identical on the wire, exactly as ResolvedPlayable.PageEntityId was added. No SdkJsonContext change is
    // needed either — no new TYPE appears, and [JsonSerializable(typeof(PageHero))] re-emits the metadata.
    string? AvatarUrl = null,
    string? SubtitleEntityId = null);

/// <summary>One button on a page.</summary>
/// <param name="Id">Module-private id; echoed back on <c>module/action</c> for <see cref="KindModuleAction"/>.</param>
/// <param name="Kind"><see cref="KindPlay"/>, <see cref="KindOpenUrl"/> or <see cref="KindModuleAction"/>.</param>
/// <param name="Label">Button text.</param>
/// <param name="PlayableId">What to play, for <see cref="KindPlay"/> (resolved through the normal path).</param>
/// <param name="Url">Where to go, for <see cref="KindOpenUrl"/>. The app opens http(s) only.</param>
/// <param name="Primary">True for the accent button; everything else is drawn as a secondary button.</param>
public sealed record PageAction(
    string Id,
    string Kind,
    string Label,
    string? PlayableId,
    string? Url,
    bool Primary)
{
    /// <summary><see cref="Kind"/> value: play <see cref="PlayableId"/> through <c>playback/resolve</c>.</summary>
    public const string KindPlay = "play";

    /// <summary><see cref="Kind"/> value: open <see cref="Url"/> in the user's browser.</summary>
    public const string KindOpenUrl = "openUrl";

    /// <summary><see cref="Kind"/> value: send <see cref="Id"/> back over <c>module/action</c>.</summary>
    public const string KindModuleAction = "moduleAction";

    /// <summary>Builds a play button.</summary>
    /// <param name="playableId">The playable to start.</param>
    /// <param name="label">Button text.</param>
    /// <param name="primary">True for the accent button.</param>
    /// <param name="id">Optional action id; defaults to <c>"play"</c>.</param>
    public static PageAction Play(string playableId, string label, bool primary = true, string id = "play")
        => new(id, KindPlay, label, playableId, null, primary);

    /// <summary>Builds an "open in the browser" button.</summary>
    /// <param name="url">The http(s) url to open.</param>
    /// <param name="label">Button text.</param>
    /// <param name="id">Optional action id; defaults to <c>"open"</c>.</param>
    public static PageAction OpenUrl(string url, string label, string id = "open")
        => new(id, KindOpenUrl, label, null, url, false);
}

/// <summary>
/// One block of a page. Exactly which of <see cref="Text"/> / <see cref="Rows"/> / <see cref="Items"/> is populated
/// depends on <see cref="Kind"/>; a kind the app does not know is skipped rather than treated as an error.
/// </summary>
/// <param name="Kind"><see cref="KindText"/>, <see cref="KindFacts"/>, <see cref="KindPlayables"/>,
/// <see cref="KindCards"/> or <see cref="KindLinks"/>.</param>
/// <param name="Title">Section heading, or null for an unheaded block.</param>
/// <param name="Text">The body for <see cref="KindText"/>.</param>
/// <param name="Rows">Label/value pairs for <see cref="KindFacts"/> — each row is <c>[label, value]</c>.</param>
/// <param name="Items">The entries for <see cref="KindPlayables"/>, <see cref="KindCards"/> and
/// <see cref="KindLinks"/>.</param>
/// <remarks>
/// <see cref="Extra"/> is deliberately NOT a positional parameter, and deliberately has a plain setter:
/// <c>System.Text.Json</c> refuses to bind a <c>[JsonExtensionData]</c> property to a deserialization-constructor
/// parameter (<c>ExtensionDataCannotBindToCtorParam</c>), and its source generator models an <c>init</c>-only
/// property as exactly such a parameter (<c>IsMemberInitializer</c>) — so an <c>init</c> extension-data property
/// throws at first use in AOT mode. The six-argument constructor still exists and still takes the extension data
/// last, so every call site reads the same; only the record's positional list (and therefore its
/// <c>Deconstruct</c>) stops at <paramref name="Items"/>.
/// </remarks>
[method: JsonConstructor]
public sealed record PageSection(
    string Kind,
    string? Title,
    string? Text,
    string[][]? Rows,
    PageItem[]? Items)
{
    /// <summary>Builds a section with explicit extension data (the six-argument shape).</summary>
    /// <param name="kind">The section kind.</param>
    /// <param name="title">Section heading.</param>
    /// <param name="text">Prose body.</param>
    /// <param name="rows">Fact rows.</param>
    /// <param name="items">Section entries.</param>
    /// <param name="extra">Members the SDK does not know.</param>
    public PageSection(string kind, string? title, string? text, string[][]? rows, PageItem[]? items,
        Dictionary<string, JsonElement>? extra)
        : this(kind, title, text, rows, items)
        => Extra = extra;

    /// <summary>
    /// Every member the SDK does not know, kept verbatim. That is what makes a new section kind additive: a newer
    /// module can ship one and an older app preserves (and ignores) its payload. Serialized FLAT — these members sit
    /// beside <see cref="Kind"/> on the wire, not nested under an <c>"extra"</c> object.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary><see cref="Kind"/> value: a paragraph of prose in <see cref="Text"/>.</summary>
    public const string KindText = "text";

    /// <summary><see cref="Kind"/> value: label/value tiles from <see cref="Rows"/>.</summary>
    public const string KindFacts = "facts";

    /// <summary><see cref="Kind"/> value: a list of playable rows from <see cref="Items"/>.</summary>
    public const string KindPlayables = "playables";

    /// <summary><see cref="Kind"/> value: an art-card shelf from <see cref="Items"/>.</summary>
    public const string KindCards = "cards";

    /// <summary><see cref="Kind"/> value: rows that open <see cref="PageItem.Url"/> in the browser.</summary>
    public const string KindLinks = "links";

    /// <summary>Builds a <see cref="KindText"/> section.</summary>
    /// <param name="text">The prose.</param>
    /// <param name="title">Optional heading.</param>
    public static PageSection FromText(string text, string? title = null)
        => new(KindText, title, text, null, null, null);

    /// <summary>Builds a <see cref="KindFacts"/> section.</summary>
    /// <param name="rows">Label/value pairs.</param>
    /// <param name="title">Optional heading.</param>
    public static PageSection FromFacts(string[][] rows, string? title = null)
        => new(KindFacts, title, null, rows, null, null);

    /// <summary>Builds a <see cref="KindPlayables"/> section.</summary>
    /// <param name="items">The playable rows.</param>
    /// <param name="title">Optional heading.</param>
    public static PageSection FromPlayables(PageItem[] items, string? title = null)
        => new(KindPlayables, title, null, null, items, null);

    /// <summary>Builds a <see cref="KindCards"/> section.</summary>
    /// <param name="items">The cards.</param>
    /// <param name="title">Optional heading.</param>
    public static PageSection FromCards(PageItem[] items, string? title = null)
        => new(KindCards, title, null, null, items, null);

    /// <summary>Builds a <see cref="KindLinks"/> section.</summary>
    /// <param name="items">The link rows.</param>
    /// <param name="title">Optional heading.</param>
    public static PageSection FromLinks(PageItem[] items, string? title = null)
        => new(KindLinks, title, null, null, items, null);
}

/// <summary>
/// One entry inside a section. <see cref="PlayableId"/> plays through the normal resolve path;
/// <see cref="EntityId"/> navigates to another page of the SAME module; <see cref="Url"/> opens the browser.
/// </summary>
/// <param name="Title">The entry's title.</param>
/// <param name="Subtitle">Secondary line.</param>
/// <param name="ImageUrl">Absolute art url, or null.</param>
/// <param name="PlayableId">What invoking the entry plays, or null when it is not playable.</param>
/// <param name="EntityId">The module page the entry navigates to, or null.</param>
/// <param name="Url">The http(s) url the entry opens, or null.</param>
/// <param name="Form">Audio or video, when the entry is playable and the module already knows.</param>
/// <param name="IsLive">True to draw a LIVE badge on the entry.</param>
/// <param name="Meta">A short trailing fact, e.g. a duration or a viewer count.</param>
public sealed record PageItem(
    string Title,
    string? Subtitle,
    string? ImageUrl,
    string? PlayableId,
    string? EntityId,
    string? Url,
    MediaForm? Form,
    bool IsLive,
    string? Meta);

/// <summary>
/// The hard ceilings on a page document. They are checked on BOTH sides of the wire — the module's runner refuses
/// to send an over-budget page and the app refuses to accept one — and they are checked by <b>rejecting</b>, never
/// by truncating: a page that is silently half-rendered is a bug report nobody can diagnose, while a typed
/// <see cref="ModuleErrorCode.Unsupported"/> names the module and the limit it blew.
/// </summary>
public static class ModulePageBudget
{
    /// <summary>The most sections one document may carry.</summary>
    public const int MaxSections = 40;

    /// <summary>The most items (section entries plus fact rows) one document may carry in total.</summary>
    public const int MaxItems = 500;

    /// <summary>The most bytes one serialized section may occupy.</summary>
    public const int MaxSectionBytes = 64 * 1024;

    /// <summary>The most bytes the whole serialized document may occupy.</summary>
    public const int MaxDocBytes = 2 * 1024 * 1024;

    /// <summary>
    /// Throws when <paramref name="doc"/> is over budget; returns quietly otherwise. Never modifies the document.
    /// </summary>
    /// <param name="doc">The page to check.</param>
    /// <exception cref="ModuleException">The document is over one of the limits (<see cref="ModuleErrorCode.Unsupported"/>).</exception>
    public static void Validate(ModulePageDoc doc)
    {
        ArgumentNullException.ThrowIfNull(doc);

        PageSection[] sections = doc.Sections ?? [];
        if (sections.Length > MaxSections)
        {
            throw new ModuleException(ModuleErrorCode.Unsupported,
                $"This page has {sections.Length} sections; the limit is {MaxSections}.");
        }

        int items = 0;
        for (int i = 0; i < sections.Length; i++)
        {
            PageSection section = sections[i];
            if (section is null)
            {
                throw new ModuleException(ModuleErrorCode.Unsupported, $"Section {i} of this page is null.");
            }

            items += section.Items?.Length ?? 0;
            items += section.Rows?.Length ?? 0;
            if (items > MaxItems)
            {
                throw new ModuleException(ModuleErrorCode.Unsupported,
                    $"This page carries more than {MaxItems} items.");
            }

            int bytes = JsonSerializer.SerializeToUtf8Bytes(section, SdkJsonContext.Default.PageSection).Length;
            if (bytes > MaxSectionBytes)
            {
                throw new ModuleException(ModuleErrorCode.Unsupported,
                    $"Section {i} ('{section.Kind}') of this page is {bytes} bytes; the limit is {MaxSectionBytes}.");
            }
        }

        int total = JsonSerializer.SerializeToUtf8Bytes(doc, SdkJsonContext.Default.ModulePageDoc).Length;
        if (total > MaxDocBytes)
        {
            throw new ModuleException(ModuleErrorCode.Unsupported,
                $"This page is {total} bytes; the limit is {MaxDocBytes}.");
        }
    }
}
