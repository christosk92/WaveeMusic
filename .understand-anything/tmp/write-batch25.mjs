import { writeFileSync } from 'fs';

const nodes = [
  {
    id: 'file:src/Wavee.UI.WinUI/Controls/CanvasSyncReviewDialog.cs',
    type: 'file',
    name: 'CanvasSyncReviewDialog.cs',
    filePath: 'src/Wavee.UI.WinUI/Controls/CanvasSyncReviewDialog.cs',
    summary: 'Static dialog class that presents a side-by-side video comparison of the current and new Canvas URL for user review, building the UI entirely in code-behind using MediaPlayer and MediaPlayerElement.',
    tags: ['component', 'dialog', 'media', 'ui-builder'],
    complexity: 'moderate'
  },
  {
    id: 'class:src/Wavee.UI.WinUI/Controls/CanvasSyncReviewDialog.cs:CanvasSyncReviewDialog',
    type: 'class',
    name: 'CanvasSyncReviewDialog',
    filePath: 'src/Wavee.UI.WinUI/Controls/CanvasSyncReviewDialog.cs',
    lineRange: [19, 155],
    summary: 'Provides ShowAsync to display a ContentDialog with two MediaPlayerElement previews for Canvas URL comparison.',
    tags: ['dialog', 'media', 'component'],
    complexity: 'moderate'
  },
  {
    id: 'file:src/Wavee.UI.WinUI/Controls/Cards/AnimatedHeroBackground.xaml',
    type: 'file',
    name: 'AnimatedHeroBackground.xaml',
    filePath: 'src/Wavee.UI.WinUI/Controls/Cards/AnimatedHeroBackground.xaml',
    summary: 'XAML template for AnimatedHeroBackground, a Win2D CanvasAnimatedControl-based UserControl that renders an animated mesh-gradient background.',
    tags: ['component', 'xaml', 'ui'],
    complexity: 'simple'
  },
  {
    id: 'file:src/Wavee.UI.WinUI/Controls/Cards/AnimatedHeroBackground.xaml.cs',
    type: 'file',
    name: 'AnimatedHeroBackground.xaml.cs',
    filePath: 'src/Wavee.UI.WinUI/Controls/Cards/AnimatedHeroBackground.xaml.cs',
    summary: 'Code-behind for AnimatedHeroBackground: a Win2D CanvasAnimatedControl UserControl that renders a two-color animated mesh-gradient via ComputeSharp/MeshGradientShader with composition clip and nav-cache surface suspend/restore support.',
    tags: ['component', 'animation', 'compositing', 'win2d'],
    complexity: 'complex',
    languageNotes: 'Uses ComputeSharp pixel shaders (MeshGradientShader) via Win2D CanvasAnimatedControl draw loop; WinUI composition RectangleClip for rounded corners.'
  },
  {
    id: 'class:src/Wavee.UI.WinUI/Controls/Cards/AnimatedHeroBackground.xaml.cs:AnimatedHeroBackground',
    type: 'class',
    name: 'AnimatedHeroBackground',
    filePath: 'src/Wavee.UI.WinUI/Controls/Cards/AnimatedHeroBackground.xaml.cs',
    lineRange: [17, 237],
    summary: 'UserControl rendering a GPU-animated mesh gradient with DPs for primary/accent color, pause state, and corner-radius clip; supports nav-cache GPU surface release/restore.',
    tags: ['component', 'animation', 'win2d', 'compositing'],
    complexity: 'complex'
  },
  {
    id: 'file:src/Wavee.UI.WinUI/Controls/Cards/ArtistCircleCard.xaml',
    type: 'file',
    name: 'ArtistCircleCard.xaml',
    filePath: 'src/Wavee.UI.WinUI/Controls/Cards/ArtistCircleCard.xaml',
    summary: 'XAML template for ArtistCircleCard showing a circular artist image with display name and metadata beneath it.',
    tags: ['component', 'xaml', 'card'],
    complexity: 'simple'
  },
  {
    id: 'file:src/Wavee.UI.WinUI/Controls/Cards/ArtistCircleCard.xaml.cs',
    type: 'file',
    name: 'ArtistCircleCard.xaml.cs',
    filePath: 'src/Wavee.UI.WinUI/Controls/Cards/ArtistCircleCard.xaml.cs',
    summary: 'Code-behind for a circular artist card control with dependency properties for image URL, display name, metadata, and size; handles click and right-tap navigation.',
    tags: ['component', 'card', 'artist', 'navigation'],
    complexity: 'moderate'
  },
  {
    id: 'class:src/Wavee.UI.WinUI/Controls/Cards/ArtistCircleCard.xaml.cs:ArtistCircleCard',
    type: 'class',
    name: 'ArtistCircleCard',
    filePath: 'src/Wavee.UI.WinUI/Controls/Cards/ArtistCircleCard.xaml.cs',
    lineRange: [10, 95],
    summary: 'UserControl for displaying an artist as a circular image card; exposes ImageUrl, DisplayName, Metadata, Size DPs and fires CardClick/CardRightTapped events.',
    tags: ['component', 'card', 'artist'],
    complexity: 'moderate'
  },
  {
    id: 'file:src/Wavee.UI.WinUI/Controls/Cards/ArtistPillCard.xaml',
    type: 'file',
    name: 'ArtistPillCard.xaml',
    filePath: 'src/Wavee.UI.WinUI/Controls/Cards/ArtistPillCard.xaml',
    summary: 'XAML template for ArtistPillCard: a pill-shaped button combining a small circular artist image and name label, used in related-artists rows.',
    tags: ['component', 'xaml', 'card', 'artist'],
    complexity: 'simple'
  },
  {
    id: 'file:src/Wavee.UI.WinUI/Controls/Cards/ArtistPillCard.xaml.cs',
    type: 'file',
    name: 'ArtistPillCard.xaml.cs',
    filePath: 'src/Wavee.UI.WinUI/Controls/Cards/ArtistPillCard.xaml.cs',
    summary: 'Code-behind for ArtistPillCard; handles image URL loading via SpotifyImageHelper and navigates to the artist page on click using NavigationHelpers.',
    tags: ['component', 'card', 'artist', 'navigation'],
    complexity: 'moderate'
  },
  {
    id: 'class:src/Wavee.UI.WinUI/Controls/Cards/ArtistPillCard.xaml.cs:ArtistPillCard',
    type: 'class',
    name: 'ArtistPillCard',
    filePath: 'src/Wavee.UI.WinUI/Controls/Cards/ArtistPillCard.xaml.cs',
    lineRange: [16, 78],
    summary: 'UserControl pill-shaped artist button exposing ArtistUri, ArtistName, ImageUrl DPs; navigates to artist on click.',
    tags: ['component', 'card', 'artist'],
    complexity: 'moderate'
  },
  {
    id: 'file:src/Wavee.UI.WinUI/Controls/Cards/BaselineHomeCard.CanvasPreview.cs',
    type: 'file',
    name: 'BaselineHomeCard.CanvasPreview.cs',
    filePath: 'src/Wavee.UI.WinUI/Controls/Cards/BaselineHomeCard.CanvasPreview.cs',
    summary: 'Partial class of BaselineHomeCard handling Spotify Canvas video preview: async acquisition/release of a shared canvas preview service slot, host readiness wait, and deferred teardown.',
    tags: ['component', 'card', 'canvas-preview', 'async'],
    complexity: 'complex'
  },
  {
    id: 'file:src/Wavee.UI.WinUI/Controls/Cards/BaselineHomeCard.Hover.cs',
    type: 'file',
    name: 'BaselineHomeCard.Hover.cs',
    filePath: 'src/Wavee.UI.WinUI/Controls/Cards/BaselineHomeCard.Hover.cs',
    summary: 'Partial class of BaselineHomeCard implementing hover interaction state machine: pointer-enter/exit guards, debounced hover activation, scale/opacity animations, and coordinated audio+canvas teardown on exit.',
    tags: ['component', 'card', 'hover', 'animation', 'event-handler'],
    complexity: 'complex'
  },
  {
    id: 'file:src/Wavee.UI.WinUI/Controls/Cards/BaselineHomeCard.LazyRealization.cs',
    type: 'file',
    name: 'BaselineHomeCard.LazyRealization.cs',
    filePath: 'src/Wavee.UI.WinUI/Controls/Cards/BaselineHomeCard.LazyRealization.cs',
    summary: 'Partial class of BaselineHomeCard with lazy FindName helper methods that realize deferred XAML elements (hover chrome, canvas host, nav buttons, shimmer, preview visualizer, context play button) only on first access.',
    tags: ['component', 'card', 'lazy-loading'],
    complexity: 'simple'
  },
  {
    id: 'file:src/Wavee.UI.WinUI/Controls/Cards/BaselineHomeCard.PlaybackHighlight.cs',
    type: 'file',
    name: 'BaselineHomeCard.PlaybackHighlight.cs',
    filePath: 'src/Wavee.UI.WinUI/Controls/Cards/BaselineHomeCard.PlaybackHighlight.cs',
    summary: 'Partial class of BaselineHomeCard: tracks now-playing highlight, manages playback-pending visual state with timeout, and handles context play button click for play/pause/resume.',
    tags: ['component', 'card', 'playback', 'event-handler'],
    complexity: 'complex'
  },
  {
    id: 'file:src/Wavee.UI.WinUI/Controls/Cards/BaselineHomeCard.PreviewAudio.cs',
    type: 'file',
    name: 'BaselineHomeCard.PreviewAudio.cs',
    filePath: 'src/Wavee.UI.WinUI/Controls/Cards/BaselineHomeCard.PreviewAudio.cs',
    summary: 'Partial class of BaselineHomeCard: schedules/starts/stops hover audio preview, drives waveform visualizer state, and triggers auto-advance after preview playback ends.',
    tags: ['component', 'card', 'preview-audio', 'animation'],
    complexity: 'complex'
  },
  {
    id: 'file:src/Wavee.UI.WinUI/Controls/Cards/BaselineHomeCard.PreviewNavigation.cs',
    type: 'file',
    name: 'BaselineHomeCard.PreviewNavigation.cs',
    filePath: 'src/Wavee.UI.WinUI/Controls/Cards/BaselineHomeCard.PreviewNavigation.cs',
    summary: 'Partial class of BaselineHomeCard: manages multi-track preview navigation (prev/next buttons), animated slide transitions between tracks, and helper accessors for active track URL/image.',
    tags: ['component', 'card', 'preview-navigation', 'animation'],
    complexity: 'complex'
  },
  {
    id: 'file:src/Wavee.UI.WinUI/Controls/Cards/BaselineHomeCard.xaml',
    type: 'file',
    name: 'BaselineHomeCard.xaml',
    filePath: 'src/Wavee.UI.WinUI/Controls/Cards/BaselineHomeCard.xaml',
    summary: 'XAML template for BaselineHomeCard defining the hero image, thumbnail, hover chrome, canvas preview host, waveform visualizer, pending beam, play/nav buttons, and shimmer layers — all deferred via x:Load.',
    tags: ['component', 'xaml', 'card'],
    complexity: 'complex'
  },
  {
    id: 'file:src/Wavee.UI.WinUI/Controls/Cards/BaselineHomeCard.xaml.cs',
    type: 'file',
    name: 'BaselineHomeCard.xaml.cs',
    filePath: 'src/Wavee.UI.WinUI/Controls/Cards/BaselineHomeCard.xaml.cs',
    summary: 'Primary partial class of BaselineHomeCard, the large home-shelf card integrating hero/thumb images with hover-activated audio+Canvas preview, now-playing highlight, color extraction, and context play; wires all services via IoC.',
    tags: ['component', 'card', 'home', 'playback'],
    complexity: 'complex'
  },
  {
    id: 'class:src/Wavee.UI.WinUI/Controls/Cards/BaselineHomeCard.xaml.cs:BaselineHomeCard',
    type: 'class',
    name: 'BaselineHomeCard',
    filePath: 'src/Wavee.UI.WinUI/Controls/Cards/BaselineHomeCard.xaml.cs',
    lineRange: [59, 610],
    summary: 'Full-featured Home shelf card UserControl: hero+thumbnail image loading with retry, color-extracted gradient overlay, hover-activated 30-second audio preview with waveform visualization, Spotify Canvas video preview, now-playing highlight, and context play button.',
    tags: ['component', 'card', 'home', 'playback', 'animation'],
    complexity: 'complex',
    languageNotes: 'Spans 7 partial files (primary .xaml.cs + CanvasPreview, Hover, LazyRealization, PlaybackHighlight, PreviewAudio, PreviewNavigation). Uses CommunityToolkit.Labs AnimationBuilder for implicit animations.'
  },
  {
    id: 'file:src/Wavee.UI.WinUI/Controls/Cards/CardAspectMode.cs',
    type: 'file',
    name: 'CardAspectMode.cs',
    filePath: 'src/Wavee.UI.WinUI/Controls/Cards/CardAspectMode.cs',
    summary: 'Enum defining the image aspect-ratio mode for card controls (Square, Landscape, Portrait, etc.), consumed by ContentCard.AspectMode dependency property.',
    tags: ['type-definition', 'card'],
    complexity: 'simple'
  },
  {
    id: 'file:src/Wavee.UI.WinUI/Controls/Cards/ContentCard.DependencyProperties.cs',
    type: 'file',
    name: 'ContentCard.DependencyProperties.cs',
    filePath: 'src/Wavee.UI.WinUI/Controls/Cards/ContentCard.DependencyProperties.cs',
    summary: 'Partial class of ContentCard declaring all dependency properties (ImageUrl, Title, Subtitle, Badge, AspectMode, IsPlaying, IsLoading, etc.) and their static property-changed callbacks.',
    tags: ['component', 'card', 'dependency-properties'],
    complexity: 'complex'
  },
  {
    id: 'file:src/Wavee.UI.WinUI/Controls/Cards/ContentCard.Navigation.cs',
    type: 'file',
    name: 'ContentCard.Navigation.cs',
    filePath: 'src/Wavee.UI.WinUI/Controls/Cards/ContentCard.Navigation.cs',
    summary: 'Partial class of ContentCard implementing click/navigation logic: routes taps to NavigationHelpers by Spotify URI type, prepares connected animations, handles secondary action button, right-tap context menu, and drag payload construction.',
    tags: ['component', 'card', 'navigation', 'event-handler'],
    complexity: 'complex'
  },
  {
    id: 'file:src/Wavee.UI.WinUI/Controls/Cards/ContentCard.PlaybackHighlight.cs',
    type: 'file',
    name: 'ContentCard.PlaybackHighlight.cs',
    filePath: 'src/Wavee.UI.WinUI/Controls/Cards/ContentCard.PlaybackHighlight.cs',
    summary: 'Partial class of ContentCard: subscribes to NowPlayingHighlightService, applies playing/paused visual highlight, handles play button click for context play/pause/resume, and manages playback-pending beam animation with timeout.',
    tags: ['component', 'card', 'playback', 'event-handler'],
    complexity: 'complex'
  },
  {
    id: 'file:src/Wavee.UI.WinUI/Controls/Cards/ContentCard.xaml',
    type: 'file',
    name: 'ContentCard.xaml',
    filePath: 'src/Wavee.UI.WinUI/Controls/Cards/ContentCard.xaml',
    summary: 'XAML template for ContentCard defining square/circle image slots, play overlay, shimmer, title/subtitle text, badge, secondary action button, and lazy-realized play overlay deferred via x:Load.',
    tags: ['component', 'xaml', 'card'],
    complexity: 'complex'
  },
  {
    id: 'file:src/Wavee.UI.WinUI/Controls/Cards/ContentCard.xaml.cs',
    type: 'file',
    name: 'ContentCard.xaml.cs',
    filePath: 'src/Wavee.UI.WinUI/Controls/Cards/ContentCard.xaml.cs',
    summary: 'Primary partial class of ContentCard: manages viewport-gated image loading/release, square/circle rendering modes, aspect-ratio measure override, density mode, hover animations, now-playing highlight integration, and all interactive states.',
    tags: ['component', 'card', 'viewport-gating', 'playback'],
    complexity: 'complex'
  },
  {
    id: 'class:src/Wavee.UI.WinUI/Controls/Cards/ContentCard.xaml.cs:ContentCard',
    type: 'class',
    name: 'ContentCard',
    filePath: 'src/Wavee.UI.WinUI/Controls/Cards/ContentCard.xaml.cs',
    lineRange: [39, 1059],
    summary: 'General-purpose shelf/grid card UserControl used across Home, Search, Browse, Library, Artist, Album, Show, and Profile surfaces; supports square/circle image, category-tile mode, compact density, viewport-gated lazy image load, now-playing highlight, play overlay, connected animations, and drag payloads.',
    tags: ['component', 'card', 'reusable', 'playback', 'viewport-gating'],
    complexity: 'complex',
    languageNotes: 'Spans 4 partial files (primary .xaml.cs + DependencyProperties, Navigation, PlaybackHighlight). Viewport gating via ContentCardViewportBehavior suspends image loading for off-screen cards.'
  },
  {
    id: 'file:src/Wavee.UI.WinUI/Controls/Cards/EditorialHeroCard.xaml',
    type: 'file',
    name: 'EditorialHeroCard.xaml',
    filePath: 'src/Wavee.UI.WinUI/Controls/Cards/EditorialHeroCard.xaml',
    summary: 'XAML template for EditorialHeroCard, a wide hero card with a large background image, overlaid title/subtitle, and an editorial-style layout used on the Home page.',
    tags: ['component', 'xaml', 'card', 'hero'],
    complexity: 'moderate'
  },
  {
    id: 'file:src/Wavee.UI.WinUI/Controls/Cards/EditorialHeroCard.xaml.cs',
    type: 'file',
    name: 'EditorialHeroCard.xaml.cs',
    filePath: 'src/Wavee.UI.WinUI/Controls/Cards/EditorialHeroCard.xaml.cs',
    summary: 'Code-behind for EditorialHeroCard: loads hero image with Spotify URL normalization, handles click/right-tap navigation by Spotify URI type, integrates now-playing highlight, and drives play-button visibility.',
    tags: ['component', 'card', 'hero', 'navigation', 'playback'],
    complexity: 'complex'
  },
  {
    id: 'file:src/Wavee.UI.WinUI/Controls/Cards/EpisodeCard.xaml',
    type: 'file',
    name: 'EpisodeCard.xaml',
    filePath: 'src/Wavee.UI.WinUI/Controls/Cards/EpisodeCard.xaml',
    summary: 'XAML template for EpisodeCard showing episode artwork, title, show name, duration/date metadata, and a play-progress bar.',
    tags: ['component', 'xaml', 'card', 'podcast'],
    complexity: 'moderate'
  },
  {
    id: 'file:src/Wavee.UI.WinUI/Controls/Cards/EpisodeCard.xaml.cs',
    type: 'file',
    name: 'EpisodeCard.xaml.cs',
    filePath: 'src/Wavee.UI.WinUI/Controls/Cards/EpisodeCard.xaml.cs',
    summary: 'Code-behind for EpisodeCard: exposes dependency properties for episode metadata (title, show, duration, resume position, image), handles click navigation to episode/show page, and shows playback state overlay.',
    tags: ['component', 'card', 'podcast', 'navigation', 'playback'],
    complexity: 'complex'
  }
];

const edges = [
  { source: 'file:src/Wavee.UI.WinUI/Controls/CanvasSyncReviewDialog.cs', target: 'class:src/Wavee.UI.WinUI/Controls/CanvasSyncReviewDialog.cs:CanvasSyncReviewDialog', type: 'contains', direction: 'forward', weight: 1.0 },
  { source: 'file:src/Wavee.UI.WinUI/Controls/Cards/AnimatedHeroBackground.xaml.cs', target: 'class:src/Wavee.UI.WinUI/Controls/Cards/AnimatedHeroBackground.xaml.cs:AnimatedHeroBackground', type: 'contains', direction: 'forward', weight: 1.0 },
  { source: 'file:src/Wavee.UI.WinUI/Controls/Cards/AnimatedHeroBackground.xaml', target: 'file:src/Wavee.UI.WinUI/Controls/Cards/AnimatedHeroBackground.xaml.cs', type: 'related', direction: 'forward', weight: 0.5 },
  { source: 'file:src/Wavee.UI.WinUI/Controls/Cards/ArtistCircleCard.xaml.cs', target: 'class:src/Wavee.UI.WinUI/Controls/Cards/ArtistCircleCard.xaml.cs:ArtistCircleCard', type: 'contains', direction: 'forward', weight: 1.0 },
  { source: 'file:src/Wavee.UI.WinUI/Controls/Cards/ArtistCircleCard.xaml', target: 'file:src/Wavee.UI.WinUI/Controls/Cards/ArtistCircleCard.xaml.cs', type: 'related', direction: 'forward', weight: 0.5 },
  { source: 'file:src/Wavee.UI.WinUI/Controls/Cards/ArtistPillCard.xaml.cs', target: 'class:src/Wavee.UI.WinUI/Controls/Cards/ArtistPillCard.xaml.cs:ArtistPillCard', type: 'contains', direction: 'forward', weight: 1.0 },
  { source: 'file:src/Wavee.UI.WinUI/Controls/Cards/ArtistPillCard.xaml', target: 'file:src/Wavee.UI.WinUI/Controls/Cards/ArtistPillCard.xaml.cs', type: 'related', direction: 'forward', weight: 0.5 },
  { source: 'file:src/Wavee.UI.WinUI/Controls/Cards/BaselineHomeCard.xaml.cs', target: 'class:src/Wavee.UI.WinUI/Controls/Cards/BaselineHomeCard.xaml.cs:BaselineHomeCard', type: 'contains', direction: 'forward', weight: 1.0 },
  { source: 'file:src/Wavee.UI.WinUI/Controls/Cards/BaselineHomeCard.CanvasPreview.cs', target: 'file:src/Wavee.UI.WinUI/Controls/Cards/BaselineHomeCard.xaml.cs', type: 'related', direction: 'forward', weight: 0.5 },
  { source: 'file:src/Wavee.UI.WinUI/Controls/Cards/BaselineHomeCard.Hover.cs', target: 'file:src/Wavee.UI.WinUI/Controls/Cards/BaselineHomeCard.xaml.cs', type: 'related', direction: 'forward', weight: 0.5 },
  { source: 'file:src/Wavee.UI.WinUI/Controls/Cards/BaselineHomeCard.LazyRealization.cs', target: 'file:src/Wavee.UI.WinUI/Controls/Cards/BaselineHomeCard.xaml.cs', type: 'related', direction: 'forward', weight: 0.5 },
  { source: 'file:src/Wavee.UI.WinUI/Controls/Cards/BaselineHomeCard.PlaybackHighlight.cs', target: 'file:src/Wavee.UI.WinUI/Controls/Cards/BaselineHomeCard.xaml.cs', type: 'related', direction: 'forward', weight: 0.5 },
  { source: 'file:src/Wavee.UI.WinUI/Controls/Cards/BaselineHomeCard.PreviewAudio.cs', target: 'file:src/Wavee.UI.WinUI/Controls/Cards/BaselineHomeCard.xaml.cs', type: 'related', direction: 'forward', weight: 0.5 },
  { source: 'file:src/Wavee.UI.WinUI/Controls/Cards/BaselineHomeCard.PreviewNavigation.cs', target: 'file:src/Wavee.UI.WinUI/Controls/Cards/BaselineHomeCard.xaml.cs', type: 'related', direction: 'forward', weight: 0.5 },
  { source: 'file:src/Wavee.UI.WinUI/Controls/Cards/BaselineHomeCard.xaml', target: 'file:src/Wavee.UI.WinUI/Controls/Cards/BaselineHomeCard.xaml.cs', type: 'related', direction: 'forward', weight: 0.5 },
  { source: 'file:src/Wavee.UI.WinUI/Controls/Cards/ContentCard.xaml.cs', target: 'class:src/Wavee.UI.WinUI/Controls/Cards/ContentCard.xaml.cs:ContentCard', type: 'contains', direction: 'forward', weight: 1.0 },
  { source: 'file:src/Wavee.UI.WinUI/Controls/Cards/ContentCard.DependencyProperties.cs', target: 'file:src/Wavee.UI.WinUI/Controls/Cards/ContentCard.xaml.cs', type: 'related', direction: 'forward', weight: 0.5 },
  { source: 'file:src/Wavee.UI.WinUI/Controls/Cards/ContentCard.Navigation.cs', target: 'file:src/Wavee.UI.WinUI/Controls/Cards/ContentCard.xaml.cs', type: 'related', direction: 'forward', weight: 0.5 },
  { source: 'file:src/Wavee.UI.WinUI/Controls/Cards/ContentCard.PlaybackHighlight.cs', target: 'file:src/Wavee.UI.WinUI/Controls/Cards/ContentCard.xaml.cs', type: 'related', direction: 'forward', weight: 0.5 },
  { source: 'file:src/Wavee.UI.WinUI/Controls/Cards/ContentCard.xaml', target: 'file:src/Wavee.UI.WinUI/Controls/Cards/ContentCard.xaml.cs', type: 'related', direction: 'forward', weight: 0.5 },
  { source: 'file:src/Wavee.UI.WinUI/Controls/Cards/ContentCard.DependencyProperties.cs', target: 'file:src/Wavee.UI.WinUI/Controls/Cards/CardAspectMode.cs', type: 'depends_on', direction: 'forward', weight: 0.6 },
  { source: 'file:src/Wavee.UI.WinUI/Controls/Cards/BaselineHomeCard.xaml.cs', target: 'file:src/Wavee.UI.WinUI/Controls/Cards/AnimatedHeroBackground.xaml.cs', type: 'depends_on', direction: 'forward', weight: 0.6 },
  { source: 'file:src/Wavee.UI.WinUI/Controls/Cards/EditorialHeroCard.xaml', target: 'file:src/Wavee.UI.WinUI/Controls/Cards/EditorialHeroCard.xaml.cs', type: 'related', direction: 'forward', weight: 0.5 },
  { source: 'file:src/Wavee.UI.WinUI/Controls/Cards/EpisodeCard.xaml', target: 'file:src/Wavee.UI.WinUI/Controls/Cards/EpisodeCard.xaml.cs', type: 'related', direction: 'forward', weight: 0.5 }
];

const output = { nodes, edges };
const outPath = 'C:/WAVEE/WaveeMusic/.understand-anything/intermediate/batch-25.json';
writeFileSync(outPath, JSON.stringify(output, null, 2), 'utf8');
console.log('Written batch-25.json: nodes=' + nodes.length + ', edges=' + edges.length);
