using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace Wavee.UI.WinUI.Controls.ContextMenu;

/// <summary>
/// Shows a context menu at a cursor position. The root is a plain <see cref="Flyout"/>
/// (not a <see cref="CommandBarFlyout"/>) so we get full control over the primary row:
/// equal-width buttons with icon + label, as Spotify does. Secondary rows render as
/// standard labeled menu rows with optional submenus and keyboard-accelerator hints.
/// </summary>
public static class ContextMenuHost
{
    private const double PrimaryButtonHeight = 64;
    private const double PrimaryIconSize = 22;
    private const double PrimaryLabelFontSize = 12;
    private const double SecondaryRowHeight = 36;
    private const double SecondaryIconSize = 16;

    public static Flyout Show(
        FrameworkElement target,
        IReadOnlyList<ContextMenuItemModel> items,
        Point position)
    {
        var flyout = Build(items);
        flyout.ShowAt(target, new FlyoutShowOptions
        {
            Position = position,
            ShowMode = FlyoutShowMode.Standard,
            Placement = FlyoutPlacementMode.RightEdgeAlignedTop
        });
        return flyout;
    }

    public static Flyout Show(FrameworkElement target, IReadOnlyList<ContextMenuItemModel> items)
    {
        var flyout = Build(items);
        flyout.ShowAt(target);
        return flyout;
    }

    public static Flyout Build(IReadOnlyList<ContextMenuItemModel> items)
    {
        var flyout = new Flyout
        {
            Placement = FlyoutPlacementMode.RightEdgeAlignedTop,
            FlyoutPresenterStyle = BuildPresenterStyle(),
            ShouldConstrainToRootBounds = true
        };

        flyout.Content = BuildRoot(items, flyout);
        return flyout;
    }

    private static Style BuildPresenterStyle()
    {
        // BasedOn the system default so the flyout keeps its open/close
        // animations and the standard control template. Without this, the
        // overrides below replace the default style wholesale and the flyout
        // pops in without any transition.
        Style? baseStyle = null;
        if (Application.Current.Resources.TryGetValue("DefaultFlyoutPresenterStyle", out var v) && v is Style s)
            baseStyle = s;

        var style = baseStyle is null
            ? new Style(typeof(FlyoutPresenter))
            : new Style(typeof(FlyoutPresenter)) { BasedOn = baseStyle };
        style.Setters.Add(new Setter(FlyoutPresenter.PaddingProperty, new Thickness(6)));
        style.Setters.Add(new Setter(FlyoutPresenter.MinWidthProperty, 300d));
        style.Setters.Add(new Setter(FlyoutPresenter.MaxWidthProperty, 380d));
        style.Setters.Add(new Setter(FlyoutPresenter.CornerRadiusProperty, new CornerRadius(8)));
        return style;
    }

    private static UIElement BuildRoot(IReadOnlyList<ContextMenuItemModel> items, FlyoutBase host)
    {
        var visible = items.Where(i => i.ShowItem).ToList();
        var primary = visible.Where(i => i.IsPrimary && i.ItemType == ContextMenuItemType.Item).ToList();
        var secondary = visible.Except(primary).ToList();
        // Trim trailing separator.
        while (secondary.Count > 0 && secondary[^1].ItemType == ContextMenuItemType.Separator)
            secondary.RemoveAt(secondary.Count - 1);

        var root = new StackPanel { Orientation = Orientation.Vertical, Spacing = 0 };

        if (primary.Count > 0)
        {
            root.Children.Add(BuildPrimaryRow(primary, host));
        }

        foreach (var item in secondary)
        {
            root.Children.Add(BuildSecondaryElement(item, host));
        }

        return root;
    }

    private static UIElement BuildPrimaryRow(List<ContextMenuItemModel> primary, FlyoutBase host)
    {
        var grid = new Grid
        {
            Margin = new Thickness(0, 0, 0, 2)
        };

        // Interleave star columns for buttons and 1 px auto columns for vertical separators.
        // e.g. [btn *] [sep 1px] [btn *] [sep 1px] [btn *] [sep 1px] [btn *]
        var separatorBrush = GetBrush("ContextMenuSeparatorBrush", "DividerStrokeColorDefaultBrush")
                             ?? new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));

        int column = 0;
        for (int i = 0; i < primary.Count; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var btn = BuildPrimaryButton(primary[i], host);
            Grid.SetColumn(btn, column);
            grid.Children.Add(btn);
            column++;

            if (i < primary.Count - 1)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var sep = new Rectangle
                {
                    Width = 1,
                    Margin = new Thickness(0, 10, 0, 10),
                    Fill = separatorBrush,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                Grid.SetColumn(sep, column);
                grid.Children.Add(sep);
                column++;
            }
        }
        return grid;
    }

    private static Button BuildPrimaryButton(ContextMenuItemModel model, FlyoutBase host)
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        // Preferred path: a keyed two-layer AccentIcon style. Falls back to Segoe FontIcon.
        if (!string.IsNullOrEmpty(model.AccentIconStyleKey)
            && Application.Current.Resources.TryGetValue(model.AccentIconStyleKey!, out var styleObj)
            && styleObj is Style accentStyle)
        {
            var accentIcon = new AccentIcon.AccentIcon
            {
                Style = accentStyle,
                IconSize = PrimaryIconSize,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            content.Children.Add(accentIcon);
        }
        else if (!string.IsNullOrEmpty(model.Glyph))
        {
            var icon = new FontIcon
            {
                Glyph = model.Glyph,
                FontSize = PrimaryIconSize,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            if (!string.IsNullOrEmpty(model.GlyphFontFamilyName) &&
                Application.Current.Resources.TryGetValue(model.GlyphFontFamilyName!, out var ff) &&
                ff is FontFamily family)
            {
                icon.FontFamily = family;
            }
            var accentBrush = GetBrush("App.Theme.AccentBrush", "SystemAccentColorBrush", "SystemAccentColor");
            if (accentBrush is not null) icon.Foreground = accentBrush;
            content.Children.Add(icon);
        }

        if (!string.IsNullOrEmpty(model.Text))
        {
            content.Children.Add(new TextBlock
            {
                Text = model.Text,
                FontSize = PrimaryLabelFontSize,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
                HorizontalAlignment = HorizontalAlignment.Center
            });
        }

        var button = new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Height = PrimaryButtonHeight,
            Padding = new Thickness(4),
            CornerRadius = new CornerRadius(6),
            IsEnabled = model.IsEnabled,
            Style = GetRowButtonStyle()
        };

        if (!string.IsNullOrEmpty(model.Text))
            ToolTipService.SetToolTip(button, model.Text);

        WireInvocation(button, model, host);
        return button;
    }

    private static UIElement BuildSecondaryElement(ContextMenuItemModel model, FlyoutBase host)
    {
        if (model.ItemType == ContextMenuItemType.Separator)
        {
            return new Rectangle
            {
                Height = 1,
                Margin = new Thickness(4, 4, 4, 4),
                Fill = GetBrush("ContextMenuSeparatorBrush", "DividerStrokeColorDefaultBrush")
                       ?? new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF))
            };
        }

        return BuildSecondaryRow(model, host);
    }

    private static Button BuildSecondaryRow(ContextMenuItemModel model, FlyoutBase host)
    {
        // Columns: icon | label | accel | chevron
        var grid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Height = SecondaryRowHeight
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (!string.IsNullOrEmpty(model.Glyph))
        {
            var icon = new FontIcon
            {
                Glyph = model.Glyph,
                FontSize = SecondaryIconSize,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (!string.IsNullOrEmpty(model.GlyphFontFamilyName) &&
                Application.Current.Resources.TryGetValue(model.GlyphFontFamilyName!, out var ff) &&
                ff is FontFamily family)
            {
                icon.FontFamily = family;
            }
            Grid.SetColumn(icon, 0);
            grid.Children.Add(icon);
        }

        var label = new TextBlock
        {
            Text = model.Text ?? string.Empty,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(8, 0, 8, 0)
        };
        if (model.IsDestructive)
        {
            var destructive = GetBrush("ContextMenuDestructiveForegroundBrush", "SystemFillColorCriticalBrush");
            if (destructive is not null) label.Foreground = destructive;
        }
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        if (!string.IsNullOrEmpty(model.KeyboardAcceleratorTextOverride))
        {
            var accel = new TextBlock
            {
                Text = model.KeyboardAcceleratorTextOverride,
                FontSize = 11,
                Opacity = 0.6,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 8, 0)
            };
            Grid.SetColumn(accel, 2);
            grid.Children.Add(accel);
        }

        var hasSubmenu = model.Items is { Count: > 0 } || model.LoadSubMenuAsync is not null;
        if (hasSubmenu)
        {
            var chevron = new FontIcon
            {
                Glyph = Wavee.UI.WinUI.Styles.FluentGlyphs.ChevronRight,
                FontSize = 10,
                Opacity = 0.6,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0)
            };
            Grid.SetColumn(chevron, 3);
            grid.Children.Add(chevron);
        }

        var button = new Button
        {
            Content = grid,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Padding = new Thickness(8, 0, 8, 0),
            CornerRadius = new CornerRadius(4),
            IsEnabled = model.IsEnabled,
            Style = GetRowButtonStyle()
        };

        if (model.IsDestructive)
        {
            var destructive = GetBrush("ContextMenuDestructiveForegroundBrush", "SystemFillColorCriticalBrush");
            if (destructive is not null) button.Foreground = destructive;
        }

        if (hasSubmenu)
        {
            AttachSubmenu(button, model, host);
        }
        else
        {
            WireInvocation(button, model, host);
        }

        return button;
    }

    private static void WireInvocation(Button button, ContextMenuItemModel model, FlyoutBase host)
    {
        button.Click += (_, _) =>
        {
            try
            {
                if (model.Invoke is not null)
                {
                    model.Invoke();
                }
                else if (model.Command is { } cmd && cmd.CanExecute(model.CommandParameter))
                {
                    cmd.Execute(model.CommandParameter);
                }
            }
            finally
            {
                host.Hide();
            }
        };
    }

    private static void AttachSubmenu(Button button, ContextMenuItemModel model, FlyoutBase parent)
    {
        var subFlyout = new Flyout
        {
            Placement = FlyoutPlacementMode.RightEdgeAlignedTop,
            FlyoutPresenterStyle = BuildPresenterStyle(),
            ShouldConstrainToRootBounds = true
        };

        IReadOnlyList<ContextMenuItemModel>? loadedChildren = model.Items;
        var loaded = loadedChildren is not null;

        subFlyout.Opening += async (_, _) =>
        {
            if (!loaded && model.LoadSubMenuAsync is not null)
            {
                try
                {
                    loadedChildren = await model.LoadSubMenuAsync();
                    loaded = true;
                }
                catch
                {
                    loadedChildren = Array.Empty<ContextMenuItemModel>();
                }
                subFlyout.Content = BuildRoot(loadedChildren ?? Array.Empty<ContextMenuItemModel>(), subFlyout);
            }
            else if (subFlyout.Content is null && loadedChildren is not null)
            {
                subFlyout.Content = BuildRoot(loadedChildren, subFlyout);
            }
        };

        if (loaded && loadedChildren is not null)
        {
            subFlyout.Content = BuildRoot(loadedChildren, subFlyout);
        }
        else
        {
            // Placeholder so the flyout has something to render if Opening fires before the async completes.
            subFlyout.Content = new TextBlock
            {
                Text = "…",
                Margin = new Thickness(12, 8, 12, 8),
                Opacity = 0.6
            };
        }

        FlyoutBase.SetAttachedFlyout(button, subFlyout);
        button.Click += (_, _) => FlyoutBase.ShowAttachedFlyout(button);
    }

    private static Brush? GetBrush(params string[] keys)
    {
        foreach (var k in keys)
        {
            if (Application.Current.Resources.TryGetValue(k, out var v) && v is Brush b)
                return b;
        }
        return null;
    }

    private static Style? GetRowButtonStyle()
    {
        if (Application.Current.Resources.TryGetValue("ContextMenuRowButtonStyle", out var v) && v is Style s)
            return s;
        return null;
    }
}
