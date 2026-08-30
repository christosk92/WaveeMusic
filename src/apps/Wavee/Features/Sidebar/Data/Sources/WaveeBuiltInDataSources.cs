using System;
using System.Collections.Generic;
using Wavee.Core;

namespace Wavee;

// The FIRST-PARTY data-source registration — the "wavee" trusted extension's data half.
//
// M1's forward-compat guardrail, made concrete: every built-in source is registered through the SAME
// IWaveeExtensionRegistrar.RegisterDataSource an external extension will use, and the sidebar resolves it through a host
// interface. M3's sandboxed host swaps the host implementation; nothing here changes, and no UI ever switches on an
// extension id.
//
// The action half is m1b's hand-written BuiltInExtensionTable.RegisterAll (a source generator emits the same call shape in
// M4 — no rework).

public static class WaveeBuiltInDataSources
{
    /// <summary>
    /// Construct + register the nine first-party sources. They are registered into BOTH the platform registrar (the
    /// registry of record — the customizer's palette, M3's permission checks) and the returned
    /// <see cref="SidebarDataSourceTable"/> the binder resolves through, using the SAME instances, so the two views can
    /// never disagree.
    /// </summary>
    /// <param name="registrar">m1b's registrar. Null is legal (a headless/host-less construction): the table alone still
    /// serves the sidebar.</param>
    /// <param name="snapshot">The binder's live projection (see <see cref="ISidebarProjectionSnapshot"/>).</param>
    public static SidebarDataSourceTable RegisterAll(
        IWaveeExtensionRegistrar? registrar,
        ISidebarProjectionSnapshot snapshot,
        IMusicLibrary? library = null,
        IWhatsNewService? whatsNew = null,
        IConcertService? concerts = null,
        PlaybackBridge? playback = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var table = new SidebarDataSourceTable();

        Register(registrar, table, new SidebarLibrarySource(snapshot));
        Register(registrar, table, new SidebarVisitedSource(snapshot));
        Register(registrar, table, new SidebarPlayedSource(snapshot));
        Register(registrar, table, new SidebarPlaylistTreeSource(snapshot));
        Register(registrar, table, new SidebarArtistTopTracksSource(library));
        Register(registrar, table, new SidebarNewReleasesSource(whatsNew, snapshot));
        Register(registrar, table, new SidebarConcertsSource(concerts));
        Register(registrar, table, new SidebarQueueSource(playback));
        Register(registrar, table, new SidebarNowPlayingSource(playback));

        return table;
    }

    static void Register(IWaveeExtensionRegistrar? registrar, SidebarDataSourceTable table, ISidebarDataSource source)
    {
        table.Add(source);
        // A registrar that rejects or throws on one contribution must not cost the other eight.
        try { registrar?.RegisterDataSource(source); }
        catch (Exception) { }
    }

    /// <summary>Contribute an already-built table into the platform registry under the first-party extension id. The
    /// registry is built from the composition root with the <c>ActionServices</c> bag (<c>WaveeExtensionRegistry.Build</c>),
    /// which the SHELL owns — so the sources are constructed early (they are the sidebar's own dependency) and published
    /// into the registry here, whenever that build happens. Idempotent by the registry's own first-wins duplicate policy.</summary>
    public static void Publish(WaveeExtensionRegistry registry, SidebarDataSourceTable table)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(table);
        registry.Register(SidebarContributions.WaveeExtensionId, r =>
        {
            foreach (var source in table.All) r.RegisterDataSource(source);
        });
    }

    /// <summary>
    /// The M1 contribution host: the first-party table FIRST (it is the only one with an enable flag), then the platform
    /// registry for anything a trusted extension contributed there. One class so the binder and the planner have exactly
    /// ONE resolution seam — nothing anywhere switches on an extension id — and so M3's sandboxed host is a drop-in
    /// replacement for this type alone.
    /// </summary>
    public sealed class ContributionHost : ISidebarContributionHost
    {
        readonly SidebarDataSourceTable _table;

        public ContributionHost(SidebarDataSourceTable table) => _table = table;

        public SidebarDataSourceTable Table => _table;

        public ISidebarDataSource? Resolve(string sourceId, out SidebarContributionAvailability availability)
        {
            var source = _table.Resolve(sourceId, out availability);
            // A DISABLED first-party contribution must stay disabled — never fall through to a second lookup that would
            // silently re-enable it.
            if (source is not null || availability == SidebarContributionAvailability.Disabled) return source;

            var registry = WaveeExtensionRegistry.Current;
            if (registry is not null && registry.TryGetSource(sourceId, out var contributed) && contributed is not null)
            {
                availability = SidebarContributionAvailability.Live;
                return contributed;
            }
            availability = SidebarContributionAvailability.Missing;
            return null;
        }
    }

    /// <summary>Attach the UI-thread marshaller to every source that owns async work, and wire <paramref name="onChanged"/>
    /// to each source's Changed. Called once by the binder's <c>Start</c>; the returned action detaches everything.</summary>
    public static Action Attach(SidebarDataSourceTable table, Action<Action> post, Action onChanged)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(onChanged);

        var attached = new List<ISidebarDataSource>();
        foreach (var source in table.All)
        {
            (source as ISidebarDataSourceLifecycle)?.Attach(post);
            source.Changed += onChanged;
            attached.Add(source);
        }

        return () =>
        {
            for (int i = 0; i < attached.Count; i++)
            {
                attached[i].Changed -= onChanged;
                (attached[i] as ISidebarDataSourceLifecycle)?.Detach();
            }
            attached.Clear();
        };
    }

#if DEBUG || FLUENTGPU_DIAG
    /// <summary>Diagnostic-only labelled variant of <see cref="Attach(SidebarDataSourceTable,Action{Action},Action)"/>.
    /// Each source needs its own handler so a wake capture can identify the source that raised <c>Changed</c>.</summary>
    public static Action Attach(SidebarDataSourceTable table, Action<Action> post, Action<string> onChanged)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(onChanged);

        var attached = new List<(ISidebarDataSource Source, Action Handler)>();
        foreach (var source in table.All)
        {
            (source as ISidebarDataSourceLifecycle)?.Attach(post);
            string sourceId = source.Id;
            Action handler = () => onChanged(sourceId);
            source.Changed += handler;
            attached.Add((source, handler));
        }

        return () =>
        {
            for (int i = 0; i < attached.Count; i++)
            {
                var entry = attached[i];
                entry.Source.Changed -= entry.Handler;
                (entry.Source as ISidebarDataSourceLifecycle)?.Detach();
            }
            attached.Clear();
        };
    }
#endif
}
