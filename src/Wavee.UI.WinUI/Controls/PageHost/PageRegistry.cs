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

    public static void Register<TPage>(Func<TPage> factory) where TPage : UserControl
    {
        _factories[typeof(TPage)] = factory;
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
}
