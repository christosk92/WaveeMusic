const fs = require('fs');

const nodes = [];
const edges = [];

// FILE NODES
const files = [
  ['src/Wavee.UI.WinUI/Controls/Gallery/MarqueeGalleryStrip.xaml', 'MarqueeGalleryStrip.xaml', 'XAML template for the MarqueeGalleryStrip control defining the rail and host grid containers for composition-layer marquee animation.', ['component','markup','gallery'], 'simple'],
  ['src/Wavee.UI.WinUI/Controls/Gallery/MarqueeGalleryStrip.xaml.cs', 'MarqueeGalleryStrip.xaml.cs', 'WinUI 3 UserControl implementing a horizontally scrolling marquee gallery strip with composition-layer animation, edge fade gradients, halo effect, and tap-to-navigate image tiles.', ['component','animation','gallery','composition'], 'complex'],
  ['src/Wavee.UI.WinUI/Controls/GridSplitter/GridSplitter.cs', 'GridSplitter.cs', 'Custom WinUI 3 GridSplitter control that handles drag manipulation to resize adjacent Grid rows or columns with hover/drag visual states and a ResizeCompleted event.', ['component','layout','event-handler'], 'moderate'],
  ['src/Wavee.UI.WinUI/Controls/GridSplitter/GridSplitter.xaml', 'GridSplitter.xaml', 'XAML ControlTemplate for the GridSplitter declaring visual states for Normal, PointerOver, Pressed, and Dragging.', ['component','markup','layout'], 'simple'],
  ['src/Wavee.UI.WinUI/Controls/HeartButton.xaml', 'HeartButton.xaml', 'XAML template for the HeartButton like/unlike toggle control.', ['component','markup'], 'simple'],
  ['src/Wavee.UI.WinUI/Controls/HeartButton.xaml.cs', 'HeartButton.xaml.cs', 'WinUI 3 UserControl implementing a heart-shaped like/unlike toggle with IsLiked, Command, and CommandParameter dependency properties and visual state management.', ['component','event-handler'], 'simple'],
  ['src/Wavee.UI.WinUI/Controls/HeroHeader/ArtistShyPill.xaml', 'ArtistShyPill.xaml', 'XAML layout for the ArtistShyPill overlay showing artist image, name, monthly listeners, play and follow buttons with palette-accent styling.', ['component','markup','hero-header'], 'moderate'],
  ['src/Wavee.UI.WinUI/Controls/HeroHeader/ArtistShyPill.xaml.cs', 'ArtistShyPill.xaml.cs', 'Code-behind for ArtistShyPill exposing dependency properties for artist image, name, monthly listeners, play state, follow state, palette accent brushes, and commands.', ['component','hero-header'], 'moderate'],
  ['src/Wavee.UI.WinUI/Controls/HeroHeader/HeroHeader.xaml', 'HeroHeader.xaml', 'XAML template defining the image border, overlay content presenter, and scrim layer structure for the HeroHeader control.', ['component','markup','hero-header'], 'simple'],
  ['src/Wavee.UI.WinUI/Controls/HeroHeader/HeroHeader.xaml.cs', 'HeroHeader.xaml.cs', 'WinUI 3 UserControl implementing a GPU-backed full-bleed hero image header with multi-layer composition scrim, color-blend overlay, scroll-fade, pop-in animation, and nav-cache surface lifecycle.', ['component','animation','composition','hero-header'], 'complex'],
  ['src/Wavee.UI.WinUI/Controls/HeroHeader/ShyHeaderController.cs', 'ShyHeaderController.cs', 'Controller that listens to ScrollView position changes and drives a shy-header pin/unpin transition, updating hero scroll-fade and animating between expanded and collapsed header states.', ['component','animation','hero-header','service'], 'complex'],
  ['src/Wavee.UI.WinUI/Controls/HeroHeader/ShyHeaderFade.cs', 'ShyHeaderFade.cs', 'Static factory providing fade-callback delegates that map scroll progress to hero or element opacity for scroll-driven fade effects.', ['factory','hero-header','animation'], 'simple'],
  ['src/Wavee.UI.WinUI/Controls/HeroHeader/ShyHeaderPinOffset.cs', 'ShyHeaderPinOffset.cs', 'Static factory returning pin-offset-threshold delegates based on hero height or a reference element for use by ShyHeaderController.', ['factory','hero-header'], 'simple'],
  ['src/Wavee.UI.WinUI/Controls/HtmlTextBlock.cs', 'HtmlTextBlock.cs', 'WinUI 3 UserControl parsing an HTML subset into RichTextBlock paragraphs with hyperlinks, bullet lists, bold/italic, and Spotify URI navigation.', ['component','serialization','utility'], 'complex'],
  ['src/Wavee.UI.WinUI/Controls/Imaging/CompositionImage.xaml', 'CompositionImage.xaml', 'XAML template for CompositionImage providing a host element and placeholder layer for GPU-resident image rendering.', ['component','markup','imaging'], 'simple'],
  ['src/Wavee.UI.WinUI/Controls/Imaging/CompositionImage.xaml.cs', 'CompositionImage.xaml.cs', 'GPU-resident image primitive using Windows.UI.Composition SpriteVisual and ImageCacheService with round/ellipse clip, fade-in, nav-cache surface release/restore, and per-instance diagnostic tracing.', ['component','composition','imaging','cache'], 'complex'],
  ['src/Wavee.UI.WinUI/Controls/Imaging/CrossFadeImage.xaml', 'CrossFadeImage.xaml', 'XAML template for CrossFadeImage defining two image layers for animated cross-fade transitions.', ['component','markup','imaging'], 'simple'],
  ['src/Wavee.UI.WinUI/Controls/Imaging/CrossFadeImage.xaml.cs', 'CrossFadeImage.xaml.cs', 'WinUI 3 control cross-fading between two CompositionImage layers on source change with scale-and-fade animation, fallback source, and async palette-color placeholder loading.', ['component','animation','imaging'], 'complex'],
  ['src/Wavee.UI.WinUI/Controls/InlineEditableText.xaml', 'InlineEditableText.xaml', 'XAML layout for InlineEditableText with a display frame, edit affordance button, TextBox, and Save/Cancel action buttons.', ['component','markup'], 'moderate'],
  ['src/Wavee.UI.WinUI/Controls/InlineEditableText.xaml.cs', 'InlineEditableText.xaml.cs', 'Click-to-edit-in-place text control with Escape/Enter/Ctrl+Enter keyboard handling, pointer hover affordance, optional multi-line support, and Committed/Cancelled events.', ['component','event-handler'], 'complex'],
  ['src/Wavee.UI.WinUI/Controls/InPageFilter/IInPageFilterable.cs', 'IInPageFilterable.cs', 'Interface contract for views supporting in-page text filtering, exposing FilterQuery, FilterPlaceholder, CanFilter, and an OnFilterClosed callback.', ['type-definition'], 'simple'],
  ['src/Wavee.UI.WinUI/Controls/InPageFilter/InPageFilterOverlay.xaml', 'InPageFilterOverlay.xaml', 'XAML layout for the in-page filter overlay with a search input box, close button, and slide-in animation.', ['component','markup'], 'simple'],
  ['src/Wavee.UI.WinUI/Controls/InPageFilter/InPageFilterOverlay.xaml.cs', 'InPageFilterOverlay.xaml.cs', 'Code-behind for the in-page filter overlay synchronizing query text with InPageFilterController and handling keyboard focus and Escape/close gestures.', ['component','event-handler'], 'moderate'],
  ['src/Wavee.UI.WinUI/Controls/InPageFilter/IRedirectsCtrlFToOmnibar.cs', 'IRedirectsCtrlFToOmnibar.cs', 'Marker interface pages implement to signal that Ctrl+F keystrokes should redirect to the global omnibar instead of opening an in-page filter.', ['type-definition'], 'simple'],
  ['src/Wavee.UI.WinUI/Controls/JsonRichTextBlock.cs', 'JsonRichTextBlock.cs', 'Code-built WinUI 3 control rendering JSON in a RichEditBox with syntax highlighting, search highlighting, line numbers, word-wrap toggle, async background tokenization, and clipboard copy.', ['component','utility','serialization'], 'complex'],
];

for (const [fp, name, summary, tags, complexity] of files) {
  nodes.push({ id: 'file:' + fp, type: 'file', name, filePath: fp, summary, tags, complexity });
}

// CLASS NODES
const classes = [
  ['src/Wavee.UI.WinUI/Controls/Gallery/MarqueeGalleryStrip.xaml.cs', 'MarqueeGalleryStrip', 28, 426, 'UserControl rendering a horizontally scrolling marquee strip of image tiles using composition animation, edge fade gradients, and halo glow.', ['component','animation','gallery','composition'], 'complex'],
  ['src/Wavee.UI.WinUI/Controls/GridSplitter/GridSplitter.cs', 'GridSplitter', 19, 184, 'WinUI 3 Control for drag-resizing adjacent Grid rows or columns, supporting horizontal and vertical orientations with visual state feedback.', ['component','layout','event-handler'], 'moderate'],
  ['src/Wavee.UI.WinUI/Controls/HeartButton.xaml.cs', 'HeartButton', 11, 79, 'Toggleable heart icon button with IsLiked, Command, and CommandParameter dependency properties and tooltip update on state change.', ['component','event-handler'], 'simple'],
  ['src/Wavee.UI.WinUI/Controls/HeroHeader/ArtistShyPill.xaml.cs', 'ArtistShyPill', 18, 120, 'Overlay pill control for artist pages exposing image, name, monthly listeners, play/follow state, palette accent brushes, and commands as dependency properties.', ['component','hero-header'], 'moderate'],
  ['src/Wavee.UI.WinUI/Controls/HeroHeader/HeroHeader.xaml.cs', 'HeroHeader', 19, 742, 'Full-bleed hero image control with multi-layer composition scrim, color-blend gradient, scroll fade, pop-in animation, and nav-cache GPU surface lifecycle management.', ['component','animation','composition','hero-header'], 'complex'],
  ['src/Wavee.UI.WinUI/Controls/HeroHeader/ShyHeaderController.cs', 'ShyHeaderController', 26, 241, 'IDisposable controller attaching to a ScrollView to evaluate pin-offset thresholds and drive shy-header pin/unpin transition animations.', ['component','hero-header','animation','service'], 'complex'],
  ['src/Wavee.UI.WinUI/Controls/HeroHeader/ShyHeaderFade.cs', 'ShyHeaderFade', 14, 44, 'Static factory providing fade callbacks for HeroHeader scroll-fade, composition-visual opacity, and element opacity.', ['factory','hero-header'], 'simple'],
  ['src/Wavee.UI.WinUI/Controls/HeroHeader/ShyHeaderPinOffset.cs', 'ShyHeaderPinOffset', 12, 47, 'Static factory returning pin-offset threshold delegates based on hero height or a reference element.', ['factory','hero-header'], 'simple'],
  ['src/Wavee.UI.WinUI/Controls/HtmlTextBlock.cs', 'HtmlTextBlock', 16, 234, 'RichTextBlock-backed control parsing HTML fragments into styled paragraphs with hyperlinks, bullet lists, and Spotify URI navigation.', ['component','serialization','utility'], 'complex'],
  ['src/Wavee.UI.WinUI/Controls/Imaging/CompositionImage.xaml.cs', 'CompositionImage', 36, 857, 'Core GPU-resident image primitive managing a SpriteVisual via ImageCacheService with round/circle clip, placeholder fade-out, and nav-cache lifecycle.', ['component','composition','imaging','cache'], 'complex'],
  ['src/Wavee.UI.WinUI/Controls/Imaging/CrossFadeImage.xaml.cs', 'CrossFadeImage', 40, 357, 'Two-layer cross-fade image control using composition opacity/scale animations with palette-color placeholder loading.', ['component','animation','imaging'], 'complex'],
  ['src/Wavee.UI.WinUI/Controls/InlineEditableText.xaml.cs', 'InlineEditableText', 22, 377, 'Click-to-edit-in-place text control with keyboard handling, hover affordance, multi-line support, and Committed/Cancelled events.', ['component','event-handler'], 'complex'],
  ['src/Wavee.UI.WinUI/Controls/InPageFilter/IInPageFilterable.cs', 'IInPageFilterable', 15, 32, 'Interface for filterable views exposing FilterQuery, FilterPlaceholder, CanFilter, and OnFilterClosed.', ['type-definition'], 'simple'],
  ['src/Wavee.UI.WinUI/Controls/InPageFilter/InPageFilterOverlay.xaml.cs', 'InPageFilterOverlay', 18, 123, 'In-page filter overlay UserControl bound to InPageFilterController handling query sync, keyboard focus, and Escape/close.', ['component','event-handler'], 'moderate'],
  ['src/Wavee.UI.WinUI/Controls/JsonRichTextBlock.cs', 'JsonRichTextBlock', 21, 642, 'JSON syntax-highlighting RichEditBox control with async tokenization, search highlighting, line numbers, word-wrap toggle, and clipboard copy.', ['component','utility','serialization'], 'complex'],
];

for (const [fp, name, sl, el, summary, tags, complexity] of classes) {
  nodes.push({ id: 'class:' + fp + ':' + name, type: 'class', name, filePath: fp, lineRange: [sl, el], summary, tags, complexity });
}

// contains + exports edges
const containsPairs = [
  ['src/Wavee.UI.WinUI/Controls/Gallery/MarqueeGalleryStrip.xaml.cs', 'MarqueeGalleryStrip'],
  ['src/Wavee.UI.WinUI/Controls/GridSplitter/GridSplitter.cs', 'GridSplitter'],
  ['src/Wavee.UI.WinUI/Controls/HeartButton.xaml.cs', 'HeartButton'],
  ['src/Wavee.UI.WinUI/Controls/HeroHeader/ArtistShyPill.xaml.cs', 'ArtistShyPill'],
  ['src/Wavee.UI.WinUI/Controls/HeroHeader/HeroHeader.xaml.cs', 'HeroHeader'],
  ['src/Wavee.UI.WinUI/Controls/HeroHeader/ShyHeaderController.cs', 'ShyHeaderController'],
  ['src/Wavee.UI.WinUI/Controls/HeroHeader/ShyHeaderFade.cs', 'ShyHeaderFade'],
  ['src/Wavee.UI.WinUI/Controls/HeroHeader/ShyHeaderPinOffset.cs', 'ShyHeaderPinOffset'],
  ['src/Wavee.UI.WinUI/Controls/HtmlTextBlock.cs', 'HtmlTextBlock'],
  ['src/Wavee.UI.WinUI/Controls/Imaging/CompositionImage.xaml.cs', 'CompositionImage'],
  ['src/Wavee.UI.WinUI/Controls/Imaging/CrossFadeImage.xaml.cs', 'CrossFadeImage'],
  ['src/Wavee.UI.WinUI/Controls/InlineEditableText.xaml.cs', 'InlineEditableText'],
  ['src/Wavee.UI.WinUI/Controls/InPageFilter/IInPageFilterable.cs', 'IInPageFilterable'],
  ['src/Wavee.UI.WinUI/Controls/InPageFilter/InPageFilterOverlay.xaml.cs', 'InPageFilterOverlay'],
  ['src/Wavee.UI.WinUI/Controls/JsonRichTextBlock.cs', 'JsonRichTextBlock'],
];

for (const [fp, cls] of containsPairs) {
  edges.push({ source: 'file:' + fp, target: 'class:' + fp + ':' + cls, type: 'contains', direction: 'forward', weight: 1.0 });
  edges.push({ source: 'file:' + fp, target: 'class:' + fp + ':' + cls, type: 'exports', direction: 'forward', weight: 0.8 });
}

// XAML -> code-behind related edges
const xamlPairs = [
  ['src/Wavee.UI.WinUI/Controls/Gallery/MarqueeGalleryStrip.xaml', 'src/Wavee.UI.WinUI/Controls/Gallery/MarqueeGalleryStrip.xaml.cs'],
  ['src/Wavee.UI.WinUI/Controls/GridSplitter/GridSplitter.xaml', 'src/Wavee.UI.WinUI/Controls/GridSplitter/GridSplitter.cs'],
  ['src/Wavee.UI.WinUI/Controls/HeartButton.xaml', 'src/Wavee.UI.WinUI/Controls/HeartButton.xaml.cs'],
  ['src/Wavee.UI.WinUI/Controls/HeroHeader/ArtistShyPill.xaml', 'src/Wavee.UI.WinUI/Controls/HeroHeader/ArtistShyPill.xaml.cs'],
  ['src/Wavee.UI.WinUI/Controls/HeroHeader/HeroHeader.xaml', 'src/Wavee.UI.WinUI/Controls/HeroHeader/HeroHeader.xaml.cs'],
  ['src/Wavee.UI.WinUI/Controls/Imaging/CompositionImage.xaml', 'src/Wavee.UI.WinUI/Controls/Imaging/CompositionImage.xaml.cs'],
  ['src/Wavee.UI.WinUI/Controls/Imaging/CrossFadeImage.xaml', 'src/Wavee.UI.WinUI/Controls/Imaging/CrossFadeImage.xaml.cs'],
  ['src/Wavee.UI.WinUI/Controls/InlineEditableText.xaml', 'src/Wavee.UI.WinUI/Controls/InlineEditableText.xaml.cs'],
  ['src/Wavee.UI.WinUI/Controls/InPageFilter/InPageFilterOverlay.xaml', 'src/Wavee.UI.WinUI/Controls/InPageFilter/InPageFilterOverlay.xaml.cs'],
];

for (const [xaml, cs] of xamlPairs) {
  edges.push({ source: 'file:' + xaml, target: 'file:' + cs, type: 'related', direction: 'forward', weight: 0.5 });
}

console.log('nodes:', nodes.length, 'edges:', edges.length);
const outPath = 'C:/WAVEE/WaveeMusic/.understand-anything/intermediate/batch-28.json';
fs.writeFileSync(outPath, JSON.stringify({ nodes, edges }, null, 2), 'utf8');
console.log('Written', outPath);
