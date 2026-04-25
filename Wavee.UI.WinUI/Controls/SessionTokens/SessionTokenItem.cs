// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.
// Vendored from CommunityToolkit/Labs-Windows @ b745f81 and extended for Wavee.

using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Markup;
using Wavee.UI.WinUI.Controls.Cards;

namespace Wavee.UI.WinUI.Controls.SessionTokens;

[ContentProperty(Name = nameof(Content))]
[TemplatePart(Name = TokenItemRemoveButtonName, Type = typeof(ButtonBase))]
[TemplatePart(Name = LoadingBeamName, Type = typeof(PendingBorderBeam))]
public partial class SessionTokenItem : ListViewItem
{
    internal const string IconLeftState = "IconLeft";
    internal const string IconOnlyState = "IconOnly";
    internal const string ContentOnlyState = "ContentOnly";
    internal const string RemoveButtonVisibleState = "RemoveButtonVisible";
    internal const string RemoveButtonNotVisibleState = "RemoveButtonNotVisible";
    internal const string TokenItemRemoveButtonName = "PART_RemoveButton";
    internal const string LoadingBeamName = "PART_LoadingBeam";
    internal ButtonBase? _tokenItemRemoveButton;
    private PendingBorderBeam? _loadingBeam;

    public event EventHandler<SessionTokenItemRemovingEventArgs>? Removing;

    public SessionTokenItem()
    {
        this.DefaultStyleKey = typeof(SessionTokenItem);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_tokenItemRemoveButton != null)
        {
            _tokenItemRemoveButton.Click -= TokenItemRemoveButton_Click;
        }

        _tokenItemRemoveButton = GetTemplateChild(TokenItemRemoveButtonName) as ButtonBase;

        if (_tokenItemRemoveButton != null)
        {
            _tokenItemRemoveButton.Click += TokenItemRemoveButton_Click;
        }

        _loadingBeam = GetTemplateChild(LoadingBeamName) as PendingBorderBeam;

        // Extension: composition-level implicit Offset animation. When the
        // ItemsPanel reorders (e.g. the VM calls ObservableCollection.Move to
        // hoist this token to index 0), WinUI snaps each container's layout
        // offset to its new slot — with this implicit animation attached the
        // snap becomes a ~250 ms ease-out slide. Deferred to Loaded so we
        // don't touch the Visual before the element is in the tree.
        Loaded -= SessionTokenItem_Loaded;
        Loaded += SessionTokenItem_Loaded;

        IconChanged();
        ContentChanged();
        IsRemoveableChanged();
        // Don't touch the IsLoading DP in OnApplyTemplate — during hot-reload
        // the property system can be mid-initialization, and even a default-
        // value GetValue throws NullReferenceException in that window. Any
        // transition into IsLoading=true fires OnIsLoadingPropertyChanged
        // separately, which calls UpdateLoadingState once the DP is stable.
    }

    private void SessionTokenItem_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(this);
            if (visual.ImplicitAnimations is not null) return;
            var compositor = visual.Compositor;
            var offsetAnim = compositor.CreateVector3KeyFrameAnimation();
            offsetAnim.Target = "Offset";
            offsetAnim.Duration = TimeSpan.FromMilliseconds(250);
            offsetAnim.InsertExpressionKeyFrame(1.0f, "this.FinalValue");
            var collection = compositor.CreateImplicitAnimationCollection();
            collection["Offset"] = offsetAnim;
            visual.ImplicitAnimations = collection;
        }
        catch
        {
            // Composition unavailable (design-time, virtualized-but-unrealized
            // container edge case) — reorder still works, just without the slide.
        }
    }

    private void TokenItemRemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsRemoveable)
        {
            Removing?.Invoke(this, new SessionTokenItemRemovingEventArgs(Content, this));
        }
    }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        ContentChanged();
    }

    private void ContentChanged()
    {
        if (Content != null)
        {
            VisualStateManager.GoToState(this, IconLeftState, true);
        }
        else
        {
            VisualStateManager.GoToState(this, IconOnlyState, true);
        }
    }

    protected virtual void OnIconPropertyChanged(IconElement oldValue, IconElement newValue)
    {
        IconChanged();
    }

    private void IconChanged()
    {
        if (Icon != null)
        {
            VisualStateManager.GoToState(this, IconLeftState, true);
        }
        else
        {
            VisualStateManager.GoToState(this, ContentOnlyState, true);
        }
    }

    protected virtual void OnIsRemoveablePropertyChanged(bool oldValue, bool newValue)
    {
        IsRemoveableChanged();
    }

    private void IsRemoveableChanged()
    {
        if (IsRemoveable)
        {
            VisualStateManager.GoToState(this, RemoveButtonVisibleState, true);
        }
        else
        {
            VisualStateManager.GoToState(this, RemoveButtonNotVisibleState, true);
        }
    }

    /// <summary>
    /// Extension handler: toggles the chase-around-border loading animation
    /// via the <see cref="PendingBorderBeam"/> template part.
    /// </summary>
    protected virtual void OnIsLoadingPropertyChanged(bool oldValue, bool newValue)
    {
        ApplyLoadingState(newValue);
    }

    private void ApplyLoadingState(bool isLoading)
    {
        if (_loadingBeam is null) return;
        if (isLoading)
            _loadingBeam.Start();
        else
            _loadingBeam.Stop();
    }
}
