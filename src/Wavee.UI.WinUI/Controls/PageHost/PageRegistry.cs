using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml.Controls;

namespace Wavee.UI.WinUI.Controls.PageHost;

/// <summary>
/// Explicit factory registry mapping <see cref="UserControl"/> types to
/// construction lambdas. Replaces <c>Activator.CreateInstance(pageType)</c>
/// on the nav hot path — no reflection, AOT-friendly, and a missing
/// registration becomes a clear startup-time error instead of a silent
/// runtime failure.
/// </summary>
public static class PageRegistry
{
    private static readonly Dictionary<Type, Func<UserControl>> _factories = new();
    private static readonly HashSet<Type> _pinned = new();

    public static void Register<TPage>(Func<TPage> factory, bool pinned = false) where TPage : UserControl
    {
        _factories[typeof(TPage)] = factory;
        if (pinned)
            _pinned.Add(typeof(TPage));
    }

    public static UserControl Create(Type pageType)
    {
        if (!_factories.TryGetValue(pageType, out var factory))
            throw new InvalidOperationException(
                $"No factory registered for page type {pageType.FullName}. " +
                "Add a PageRegistry.Register<>() call in PageRegistration.RegisterAll().");

        return factory();
    }

    public static bool IsRegistered(Type pageType) => _factories.ContainsKey(pageType);

    /// <summary>
    /// True when this page type is <em>pinned</em>: created once per tab and
    /// reused for the tab's lifetime — never evicted by <see cref="PageHost"/>'s
    /// LRU cache or the cross-tab ceiling, so heavy browsing never re-pays
    /// <c>new Page()</c> + <c>InitializeComponent</c> + VM-init (the cause of
    /// progressive nav slowdown). Pinned pages going off-screen still hibernate
    /// (the VM unsubscribes from singletons) and shed GPU surfaces via
    /// <c>NavCacheSurfaces</c>, so the standing cost is only a managed visual
    /// tree. They are still torn down on tab close / tab sleep.
    /// </summary>
    public static bool IsPinned(Type pageType) => _pinned.Contains(pageType);
}
