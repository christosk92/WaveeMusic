const fs = require('fs');

const nodes = [];
const edges = [];

function fileNode(id, name, path, summary, tags, complexity, langNotes) {
  const n = { id, type: 'file', name, filePath: path, summary, tags, complexity };
  if (langNotes) n.languageNotes = langNotes;
  nodes.push(n);
}
function classNode(id, name, path, lineRange, summary, tags, complexity) {
  nodes.push({ id, type: 'class', name, filePath: path, lineRange, summary, tags, complexity });
}
function funcNode(id, name, path, lineRange, summary, tags, complexity) {
  nodes.push({ id, type: 'function', name, filePath: path, lineRange, summary, tags, complexity });
}
function contains(src, tgt) {
  edges.push({ source: src, target: tgt, type: 'contains', direction: 'forward', weight: 1.0 });
}
function exportsEdge(src, tgt) {
  edges.push({ source: src, target: tgt, type: 'exports', direction: 'forward', weight: 0.8 });
}

// 1. VideoGripperView.xaml
fileNode(
  'file:src/Wavee.UI.WinUI/Controls/MiniVideoPlayer/VideoGripperView.xaml',
  'VideoGripperView.xaml',
  'src/Wavee.UI.WinUI/Controls/MiniVideoPlayer/VideoGripperView.xaml',
  'XAML layout for the mini video player gripper overlay, defining the drag handle and restore button surface.',
  ['component', 'xaml', 'video-player', 'mini-player'],
  'simple'
);

// 2. VideoGripperView.xaml.cs
fileNode(
  'file:src/Wavee.UI.WinUI/Controls/MiniVideoPlayer/VideoGripperView.xaml.cs',
  'VideoGripperView.xaml.cs',
  'src/Wavee.UI.WinUI/Controls/MiniVideoPlayer/VideoGripperView.xaml.cs',
  'Code-behind for VideoGripperView that manages surface attachment/detachment lifecycle, responds to ViewModel property changes, and routes media surface ownership between MediaPlayer and UIElement modes.',
  ['component', 'video-player', 'surface-management', 'mini-player'],
  'moderate'
);
classNode(
  'class:src/Wavee.UI.WinUI/Controls/MiniVideoPlayer/VideoGripperView.xaml.cs:VideoGripperView',
  'VideoGripperView',
  'src/Wavee.UI.WinUI/Controls/MiniVideoPlayer/VideoGripperView.xaml.cs',
  [24, 162],
  'WinUI UserControl hosting the floating mini-player gripper, handling surface ownership changes and attaching/detaching video surfaces from the composition tree.',
  ['component', 'video-player', 'surface-management'],
  'moderate'
);
contains(
  'file:src/Wavee.UI.WinUI/Controls/MiniVideoPlayer/VideoGripperView.xaml.cs',
  'class:src/Wavee.UI.WinUI/Controls/MiniVideoPlayer/VideoGripperView.xaml.cs:VideoGripperView'
);
exportsEdge(
  'file:src/Wavee.UI.WinUI/Controls/MiniVideoPlayer/VideoGripperView.xaml.cs',
  'class:src/Wavee.UI.WinUI/Controls/MiniVideoPlayer/VideoGripperView.xaml.cs:VideoGripperView'
);

// 3. VideoSurfaceHost.xaml
fileNode(
  'file:src/Wavee.UI.WinUI/Controls/MiniVideoPlayer/VideoSurfaceHost.xaml',
  'VideoSurfaceHost.xaml',
  'src/Wavee.UI.WinUI/Controls/MiniVideoPlayer/VideoSurfaceHost.xaml',
  'XAML layout for VideoSurfaceHost defining the host container for the video composition surface and artwork overlay.',
  ['component', 'xaml', 'video-player', 'surface-management'],
  'simple'
);

// 4. VideoSurfaceHost.xaml.cs
fileNode(
  'file:src/Wavee.UI.WinUI/Controls/MiniVideoPlayer/VideoSurfaceHost.xaml.cs',
  'VideoSurfaceHost.xaml.cs',
  'src/Wavee.UI.WinUI/Controls/MiniVideoPlayer/VideoSurfaceHost.xaml.cs',
  'Code-behind for VideoSurfaceHost: manages WinUI Composition tree setup, implicit opacity animations, nav-cache surface release/restore, album-art/poster-URL loading, corner radius, and first-frame-ready state transitions.',
  ['component', 'video-player', 'surface-management', 'composition'],
  'complex',
  'Uses Windows.UI.Composition APIs directly to host video surfaces and apply implicit animations; integrates with nav-cache lifecycle via ReleaseForNavCache/RestoreForNavCache.'
);
classNode(
  'class:src/Wavee.UI.WinUI/Controls/MiniVideoPlayer/VideoSurfaceHost.xaml.cs:VideoSurfaceHost',
  'VideoSurfaceHost',
  'src/Wavee.UI.WinUI/Controls/MiniVideoPlayer/VideoSurfaceHost.xaml.cs',
  [27, 425],
  'WinUI UserControl that hosts a video composition surface alongside album-art imagery, managing the full composition lifecycle including opacity animations, nav-cache integration, and poster loading.',
  ['component', 'video-player', 'composition', 'surface-management'],
  'complex'
);
contains(
  'file:src/Wavee.UI.WinUI/Controls/MiniVideoPlayer/VideoSurfaceHost.xaml.cs',
  'class:src/Wavee.UI.WinUI/Controls/MiniVideoPlayer/VideoSurfaceHost.xaml.cs:VideoSurfaceHost'
);
exportsEdge(
  'file:src/Wavee.UI.WinUI/Controls/MiniVideoPlayer/VideoSurfaceHost.xaml.cs',
  'class:src/Wavee.UI.WinUI/Controls/MiniVideoPlayer/VideoSurfaceHost.xaml.cs:VideoSurfaceHost'
);
funcNode(
  'function:src/Wavee.UI.WinUI/Controls/MiniVideoPlayer/VideoSurfaceHost.xaml.cs:ReleaseForNavCache',
  'ReleaseForNavCache',
  'src/Wavee.UI.WinUI/Controls/MiniVideoPlayer/VideoSurfaceHost.xaml.cs',
  [189, 196],
  'Releases the GPU composition surface and hides the video host when a page is removed from the nav cache.',
  ['surface-management', 'nav-cache', 'lifecycle'],
  'simple'
);
contains(
  'file:src/Wavee.UI.WinUI/Controls/MiniVideoPlayer/VideoSurfaceHost.xaml.cs',
  'function:src/Wavee.UI.WinUI/Controls/MiniVideoPlayer/VideoSurfaceHost.xaml.cs:ReleaseForNavCache'
);
funcNode(
  'function:src/Wavee.UI.WinUI/Controls/MiniVideoPlayer/VideoSurfaceHost.xaml.cs:RestoreForNavCache',
  'RestoreForNavCache',
  'src/Wavee.UI.WinUI/Controls/MiniVideoPlayer/VideoSurfaceHost.xaml.cs',
  [198, 209],
  'Restores the video composition surface and fades in the host when a previously cached page becomes active again.',
  ['surface-management', 'nav-cache', 'lifecycle'],
  'simple'
);
contains(
  'file:src/Wavee.UI.WinUI/Controls/MiniVideoPlayer/VideoSurfaceHost.xaml.cs',
  'function:src/Wavee.UI.WinUI/Controls/MiniVideoPlayer/VideoSurfaceHost.xaml.cs:RestoreForNavCache'
);
funcNode(
  'function:src/Wavee.UI.WinUI/Controls/MiniVideoPlayer/VideoSurfaceHost.xaml.cs:EnsureComposition',
  'EnsureComposition',
  'src/Wavee.UI.WinUI/Controls/MiniVideoPlayer/VideoSurfaceHost.xaml.cs',
  [244, 264],
  'Lazily initializes the Windows.UI.Composition tree for hosting the video surface, creating ContainerVisuals and SpriteVisuals as needed.',
  ['composition', 'surface-management', 'initialization'],
  'moderate'
);
contains(
  'file:src/Wavee.UI.WinUI/Controls/MiniVideoPlayer/VideoSurfaceHost.xaml.cs',
  'function:src/Wavee.UI.WinUI/Controls/MiniVideoPlayer/VideoSurfaceHost.xaml.cs:EnsureComposition'
);

// 5. NavigationToolbar.xaml
fileNode(
  'file:src/Wavee.UI.WinUI/Controls/NavigationToolbar/NavigationToolbar.xaml',
  'NavigationToolbar.xaml',
  'src/Wavee.UI.WinUI/Controls/NavigationToolbar/NavigationToolbar.xaml',
  'XAML layout for the app navigation toolbar including back/forward/home buttons, the search omnibar, theme toggle, friends button, and profile menu.',
  ['component', 'xaml', 'navigation', 'toolbar'],
  'complex'
);

// 6. NavigationToolbar.xaml.cs
fileNode(
  'file:src/Wavee.UI.WinUI/Controls/NavigationToolbar/NavigationToolbar.xaml.cs',
  'NavigationToolbar.xaml.cs',
  'src/Wavee.UI.WinUI/Controls/NavigationToolbar/NavigationToolbar.xaml.cs',
  'Code-behind for NavigationToolbar: wires back/forward/home navigation, search omnibar events, sidebar toggle pointer states, auth visual-state transitions, theme toggle, and profile/sign-out menu commands.',
  ['component', 'navigation', 'toolbar', 'event-handler'],
  'complex'
);
classNode(
  'class:src/Wavee.UI.WinUI/Controls/NavigationToolbar/NavigationToolbar.xaml.cs:NavigationToolbar',
  'NavigationToolbar',
  'src/Wavee.UI.WinUI/Controls/NavigationToolbar/NavigationToolbar.xaml.cs',
  [13, 422],
  'WinUI UserControl for the main app navigation bar, coordinating navigation actions, search input, auth state display, and user profile interactions.',
  ['component', 'navigation', 'toolbar'],
  'complex'
);
contains(
  'file:src/Wavee.UI.WinUI/Controls/NavigationToolbar/NavigationToolbar.xaml.cs',
  'class:src/Wavee.UI.WinUI/Controls/NavigationToolbar/NavigationToolbar.xaml.cs:NavigationToolbar'
);
exportsEdge(
  'file:src/Wavee.UI.WinUI/Controls/NavigationToolbar/NavigationToolbar.xaml.cs',
  'class:src/Wavee.UI.WinUI/Controls/NavigationToolbar/NavigationToolbar.xaml.cs:NavigationToolbar'
);

// 7. NotificationToast.xaml
fileNode(
  'file:src/Wavee.UI.WinUI/Controls/Notifications/NotificationToast.xaml',
  'NotificationToast.xaml',
  'src/Wavee.UI.WinUI/Controls/Notifications/NotificationToast.xaml',
  'XAML layout for in-app notification toasts, defining severity icon, message text, optional action button, and close button.',
  ['component', 'xaml', 'notifications', 'toast'],
  'moderate'
);

// 8. NotificationToast.xaml.cs
fileNode(
  'file:src/Wavee.UI.WinUI/Controls/Notifications/NotificationToast.xaml.cs',
  'NotificationToast.xaml.cs',
  'src/Wavee.UI.WinUI/Controls/Notifications/NotificationToast.xaml.cs',
  'Code-behind for NotificationToast: handles open/close animation, severity icon/color mapping, message and action label updates, and action click routing.',
  ['component', 'notifications', 'toast', 'event-handler'],
  'moderate'
);
classNode(
  'class:src/Wavee.UI.WinUI/Controls/Notifications/NotificationToast.xaml.cs:NotificationToast',
  'NotificationToast',
  'src/Wavee.UI.WinUI/Controls/Notifications/NotificationToast.xaml.cs',
  [16, 128],
  'WinUI UserControl for in-app notification toasts with informational/warning/error severity variants, an optional action button, and animated open/close transitions.',
  ['component', 'notifications', 'toast'],
  'moderate'
);
contains(
  'file:src/Wavee.UI.WinUI/Controls/Notifications/NotificationToast.xaml.cs',
  'class:src/Wavee.UI.WinUI/Controls/Notifications/NotificationToast.xaml.cs:NotificationToast'
);
exportsEdge(
  'file:src/Wavee.UI.WinUI/Controls/Notifications/NotificationToast.xaml.cs',
  'class:src/Wavee.UI.WinUI/Controls/Notifications/NotificationToast.xaml.cs:NotificationToast'
);

// 9. BoldMatchTextBlock.cs
fileNode(
  'file:src/Wavee.UI.WinUI/Controls/Omnibar/BoldMatchTextBlock.cs',
  'BoldMatchTextBlock.cs',
  'src/Wavee.UI.WinUI/Controls/Omnibar/BoldMatchTextBlock.cs',
  'Custom TextBlock control that renders a search query match in bold by splitting text into run segments based on a Query dependency property.',
  ['component', 'omnibar', 'text-rendering', 'utility'],
  'moderate'
);
classNode(
  'class:src/Wavee.UI.WinUI/Controls/Omnibar/BoldMatchTextBlock.cs:BoldMatchTextBlock',
  'BoldMatchTextBlock',
  'src/Wavee.UI.WinUI/Controls/Omnibar/BoldMatchTextBlock.cs',
  [12, 92],
  'WinUI Control that boldifies the portion of displayed text matching a Query string, used in search suggestion items to highlight matched terms.',
  ['component', 'omnibar', 'text-rendering'],
  'moderate'
);
contains(
  'file:src/Wavee.UI.WinUI/Controls/Omnibar/BoldMatchTextBlock.cs',
  'class:src/Wavee.UI.WinUI/Controls/Omnibar/BoldMatchTextBlock.cs:BoldMatchTextBlock'
);
exportsEdge(
  'file:src/Wavee.UI.WinUI/Controls/Omnibar/BoldMatchTextBlock.cs',
  'class:src/Wavee.UI.WinUI/Controls/Omnibar/BoldMatchTextBlock.cs:BoldMatchTextBlock'
);
funcNode(
  'function:src/Wavee.UI.WinUI/Controls/Omnibar/BoldMatchTextBlock.cs:UpdateText',
  'UpdateText',
  'src/Wavee.UI.WinUI/Controls/Omnibar/BoldMatchTextBlock.cs',
  [53, 91],
  'Rebuilds the TextBlock Run segments to apply bold formatting to the matched query substring within the full text.',
  ['text-rendering', 'utility'],
  'moderate'
);
contains(
  'file:src/Wavee.UI.WinUI/Controls/Omnibar/BoldMatchTextBlock.cs',
  'function:src/Wavee.UI.WinUI/Controls/Omnibar/BoldMatchTextBlock.cs:UpdateText'
);

// 10. Omnibar.cs
fileNode(
  'file:src/Wavee.UI.WinUI/Controls/Omnibar/Omnibar.cs',
  'Omnibar.cs',
  'src/Wavee.UI.WinUI/Controls/Omnibar/Omnibar.cs',
  'Custom templated search control combining a text box with a flyout panel for live suggestions, groups, loading/error states, and keyboard-driven selection; exposes TextChanged, QuerySubmitted, SuggestionChosen, ActionButtonClicked, and RetryRequested events.',
  ['component', 'omnibar', 'search', 'event-handler'],
  'complex',
  'Contains four inner types (Omnibar plus three EventArgs); uses DispatcherQueue for async focus restore and Popup for the suggestion flyout.'
);
classNode(
  'class:src/Wavee.UI.WinUI/Controls/Omnibar/Omnibar.cs:Omnibar',
  'Omnibar',
  'src/Wavee.UI.WinUI/Controls/Omnibar/Omnibar.cs',
  [18, 546],
  'Core templated WinUI control for the search/omnibar, managing suggestion flyout visibility, keyboard navigation, loading indicators, and error states.',
  ['component', 'omnibar', 'search'],
  'complex'
);
contains('file:src/Wavee.UI.WinUI/Controls/Omnibar/Omnibar.cs', 'class:src/Wavee.UI.WinUI/Controls/Omnibar/Omnibar.cs:Omnibar');
exportsEdge('file:src/Wavee.UI.WinUI/Controls/Omnibar/Omnibar.cs', 'class:src/Wavee.UI.WinUI/Controls/Omnibar/Omnibar.cs:Omnibar');

classNode(
  'class:src/Wavee.UI.WinUI/Controls/Omnibar/Omnibar.cs:OmnibarTextChangedEventArgs',
  'OmnibarTextChangedEventArgs',
  'src/Wavee.UI.WinUI/Controls/Omnibar/Omnibar.cs',
  [548, 551],
  'Event args carrying the new search text when the omnibar text changes.',
  ['type-definition', 'omnibar', 'event-handler'],
  'simple'
);
contains('file:src/Wavee.UI.WinUI/Controls/Omnibar/Omnibar.cs', 'class:src/Wavee.UI.WinUI/Controls/Omnibar/Omnibar.cs:OmnibarTextChangedEventArgs');

classNode(
  'class:src/Wavee.UI.WinUI/Controls/Omnibar/Omnibar.cs:OmnibarQuerySubmittedEventArgs',
  'OmnibarQuerySubmittedEventArgs',
  'src/Wavee.UI.WinUI/Controls/Omnibar/Omnibar.cs',
  [553, 557],
  'Event args for a committed search query, carrying the submitted text string.',
  ['type-definition', 'omnibar', 'event-handler'],
  'simple'
);
contains('file:src/Wavee.UI.WinUI/Controls/Omnibar/Omnibar.cs', 'class:src/Wavee.UI.WinUI/Controls/Omnibar/Omnibar.cs:OmnibarQuerySubmittedEventArgs');

classNode(
  'class:src/Wavee.UI.WinUI/Controls/Omnibar/Omnibar.cs:OmnibarSuggestionChosenEventArgs',
  'OmnibarSuggestionChosenEventArgs',
  'src/Wavee.UI.WinUI/Controls/Omnibar/Omnibar.cs',
  [559, 562],
  'Event args carrying the chosen suggestion item when the user selects a result from the flyout.',
  ['type-definition', 'omnibar', 'event-handler'],
  'simple'
);
contains('file:src/Wavee.UI.WinUI/Controls/Omnibar/Omnibar.cs', 'class:src/Wavee.UI.WinUI/Controls/Omnibar/Omnibar.cs:OmnibarSuggestionChosenEventArgs');

funcNode(
  'function:src/Wavee.UI.WinUI/Controls/Omnibar/Omnibar.cs:ShowPopup',
  'ShowPopup',
  'src/Wavee.UI.WinUI/Controls/Omnibar/Omnibar.cs',
  [249, 288],
  'Opens the suggestions flyout, positions it below the search box, and triggers state evaluation.',
  ['omnibar', 'search', 'ui-interaction'],
  'moderate'
);
contains('file:src/Wavee.UI.WinUI/Controls/Omnibar/Omnibar.cs', 'function:src/Wavee.UI.WinUI/Controls/Omnibar/Omnibar.cs:ShowPopup');

funcNode(
  'function:src/Wavee.UI.WinUI/Controls/Omnibar/Omnibar.cs:UpdateFlyoutState',
  'UpdateFlyoutState',
  'src/Wavee.UI.WinUI/Controls/Omnibar/Omnibar.cs',
  [296, 349],
  'Determines which visual state to show in the flyout (loading shimmer, error, results, groups, or empty) based on current bound data.',
  ['omnibar', 'search', 'state-management'],
  'complex'
);
contains('file:src/Wavee.UI.WinUI/Controls/Omnibar/Omnibar.cs', 'function:src/Wavee.UI.WinUI/Controls/Omnibar/Omnibar.cs:UpdateFlyoutState');

funcNode(
  'function:src/Wavee.UI.WinUI/Controls/Omnibar/Omnibar.cs:TryShowCurrentState',
  'TryShowCurrentState',
  'src/Wavee.UI.WinUI/Controls/Omnibar/Omnibar.cs',
  [381, 423],
  'Delegates to SearchFlyoutPanel to render the correct UI state, coordinating between result items, suggestion groups, and loading/error conditions.',
  ['omnibar', 'search', 'state-management'],
  'complex'
);
contains('file:src/Wavee.UI.WinUI/Controls/Omnibar/Omnibar.cs', 'function:src/Wavee.UI.WinUI/Controls/Omnibar/Omnibar.cs:TryShowCurrentState');

// 11. Omnibar.xaml
fileNode(
  'file:src/Wavee.UI.WinUI/Controls/Omnibar/Omnibar.xaml',
  'Omnibar.xaml',
  'src/Wavee.UI.WinUI/Controls/Omnibar/Omnibar.xaml',
  'XAML control template for Omnibar: defines the TextBox, action/search buttons, loading ring, and the Popup container hosting SearchFlyoutPanel.',
  ['component', 'xaml', 'omnibar', 'search'],
  'complex'
);

// 12. SearchFlyoutPanel.xaml
fileNode(
  'file:src/Wavee.UI.WinUI/Controls/Omnibar/SearchFlyoutPanel.xaml',
  'SearchFlyoutPanel.xaml',
  'src/Wavee.UI.WinUI/Controls/Omnibar/SearchFlyoutPanel.xaml',
  'XAML layout for the search suggestion flyout panel containing shimmer placeholders, error state, item ListView, grouped results ListView, and a retry button.',
  ['component', 'xaml', 'omnibar', 'search'],
  'complex'
);

// 13. SearchFlyoutPanel.xaml.cs
fileNode(
  'file:src/Wavee.UI.WinUI/Controls/Omnibar/SearchFlyoutPanel.xaml.cs',
  'SearchFlyoutPanel.xaml.cs',
  'src/Wavee.UI.WinUI/Controls/Omnibar/SearchFlyoutPanel.xaml.cs',
  'Code-behind for SearchFlyoutPanel: manages keyboard selection movement, shimmer/error/result rendering, bold-match text application, suggestion image prefetching, action button visibility, and group header logic.',
  ['component', 'omnibar', 'search', 'event-handler'],
  'complex'
);
classNode(
  'class:src/Wavee.UI.WinUI/Controls/Omnibar/SearchFlyoutPanel.xaml.cs:SearchFlyoutPanel',
  'SearchFlyoutPanel',
  'src/Wavee.UI.WinUI/Controls/Omnibar/SearchFlyoutPanel.xaml.cs',
  [14, 348],
  'WinUI UserControl for the search suggestion dropdown, handling item rendering, keyboard focus movement, shimmer states, and bold-match text highlighting.',
  ['component', 'omnibar', 'search'],
  'complex'
);
contains('file:src/Wavee.UI.WinUI/Controls/Omnibar/SearchFlyoutPanel.xaml.cs', 'class:src/Wavee.UI.WinUI/Controls/Omnibar/SearchFlyoutPanel.xaml.cs:SearchFlyoutPanel');
exportsEdge('file:src/Wavee.UI.WinUI/Controls/Omnibar/SearchFlyoutPanel.xaml.cs', 'class:src/Wavee.UI.WinUI/Controls/Omnibar/SearchFlyoutPanel.xaml.cs:SearchFlyoutPanel');

funcNode(
  'function:src/Wavee.UI.WinUI/Controls/Omnibar/SearchFlyoutPanel.xaml.cs:MoveSelection',
  'MoveSelection',
  'src/Wavee.UI.WinUI/Controls/Omnibar/SearchFlyoutPanel.xaml.cs',
  [38, 72],
  'Moves keyboard focus up/down through the suggestion list, skipping non-selectable group headers and wrapping around the edges.',
  ['omnibar', 'keyboard-navigation', 'ui-interaction'],
  'moderate'
);
contains('file:src/Wavee.UI.WinUI/Controls/Omnibar/SearchFlyoutPanel.xaml.cs', 'function:src/Wavee.UI.WinUI/Controls/Omnibar/SearchFlyoutPanel.xaml.cs:MoveSelection');

funcNode(
  'function:src/Wavee.UI.WinUI/Controls/Omnibar/SearchFlyoutPanel.xaml.cs:ApplyBoldMatching',
  'ApplyBoldMatching',
  'src/Wavee.UI.WinUI/Controls/Omnibar/SearchFlyoutPanel.xaml.cs',
  [233, 249],
  'Walks the visual tree of a rendered suggestion item to find BoldMatchTextBlock controls and sets their Text/Query for bold-match rendering.',
  ['omnibar', 'text-rendering', 'utility'],
  'moderate'
);
contains('file:src/Wavee.UI.WinUI/Controls/Omnibar/SearchFlyoutPanel.xaml.cs', 'function:src/Wavee.UI.WinUI/Controls/Omnibar/SearchFlyoutPanel.xaml.cs:ApplyBoldMatching');

// 14. SearchSuggestionTemplateSelector.cs
fileNode(
  'file:src/Wavee.UI.WinUI/Controls/Omnibar/SearchSuggestionTemplateSelector.cs',
  'SearchSuggestionTemplateSelector.cs',
  'src/Wavee.UI.WinUI/Controls/Omnibar/SearchSuggestionTemplateSelector.cs',
  'DataTemplateSelector and ItemContainerStyleSelector for search suggestions, routing each result type (track, album, artist, playlist, etc.) to its appropriate DataTemplate and container style.',
  ['component', 'omnibar', 'search', 'factory'],
  'moderate'
);
classNode(
  'class:src/Wavee.UI.WinUI/Controls/Omnibar/SearchSuggestionTemplateSelector.cs:SearchSuggestionTemplateSelector',
  'SearchSuggestionTemplateSelector',
  'src/Wavee.UI.WinUI/Controls/Omnibar/SearchSuggestionTemplateSelector.cs',
  [7, 34],
  'DataTemplateSelector that maps search result item types to the correct DataTemplate for rendering in the suggestion list.',
  ['factory', 'omnibar', 'search'],
  'moderate'
);
contains('file:src/Wavee.UI.WinUI/Controls/Omnibar/SearchSuggestionTemplateSelector.cs', 'class:src/Wavee.UI.WinUI/Controls/Omnibar/SearchSuggestionTemplateSelector.cs:SearchSuggestionTemplateSelector');
exportsEdge('file:src/Wavee.UI.WinUI/Controls/Omnibar/SearchSuggestionTemplateSelector.cs', 'class:src/Wavee.UI.WinUI/Controls/Omnibar/SearchSuggestionTemplateSelector.cs:SearchSuggestionTemplateSelector');

classNode(
  'class:src/Wavee.UI.WinUI/Controls/Omnibar/SearchSuggestionTemplateSelector.cs:SearchSuggestionContainerStyleSelector',
  'SearchSuggestionContainerStyleSelector',
  'src/Wavee.UI.WinUI/Controls/Omnibar/SearchSuggestionTemplateSelector.cs',
  [41, 60],
  'StyleSelector that applies a distinct ListViewItemStyle to non-selectable group header items in the search suggestion list.',
  ['factory', 'omnibar', 'search'],
  'moderate'
);
contains('file:src/Wavee.UI.WinUI/Controls/Omnibar/SearchSuggestionTemplateSelector.cs', 'class:src/Wavee.UI.WinUI/Controls/Omnibar/SearchSuggestionTemplateSelector.cs:SearchSuggestionContainerStyleSelector');

// 15. IPageHostAware.cs
fileNode(
  'file:src/Wavee.UI.WinUI/Controls/PageHost/IPageHostAware.cs',
  'IPageHostAware.cs',
  'src/Wavee.UI.WinUI/Controls/PageHost/IPageHostAware.cs',
  'Interface marking a page as aware of its PageHost container, enabling pages to respond to hosting lifecycle events.',
  ['type-definition', 'page-host', 'navigation'],
  'simple'
);
classNode(
  'class:src/Wavee.UI.WinUI/Controls/PageHost/IPageHostAware.cs:IPageHostAware',
  'IPageHostAware',
  'src/Wavee.UI.WinUI/Controls/PageHost/IPageHostAware.cs',
  [3, 16],
  'Marker interface for pages that need to observe PageHost lifecycle callbacks.',
  ['type-definition', 'navigation'],
  'simple'
);
contains('file:src/Wavee.UI.WinUI/Controls/PageHost/IPageHostAware.cs', 'class:src/Wavee.UI.WinUI/Controls/PageHost/IPageHostAware.cs:IPageHostAware');
exportsEdge('file:src/Wavee.UI.WinUI/Controls/PageHost/IPageHostAware.cs', 'class:src/Wavee.UI.WinUI/Controls/PageHost/IPageHostAware.cs:IPageHostAware');

// 16. PageHost.cs
fileNode(
  'file:src/Wavee.UI.WinUI/Controls/PageHost/PageHost.cs',
  'PageHost.cs',
  'src/Wavee.UI.WinUI/Controls/PageHost/PageHost.cs',
  'Custom WinUI navigation control implementing an LRU-cached page stack: navigates, maintains back/forward stacks, pre-warms pages, evicts least-recently-used cached pages, and raises navigating/navigated events.',
  ['component', 'navigation', 'page-host', 'caching'],
  'complex',
  'Implements its own LRU eviction with configurable MaxCacheSize and pre-warming; uses Dictionary + ordering list rather than a standard NavigationView.'
);
classNode(
  'class:src/Wavee.UI.WinUI/Controls/PageHost/PageHost.cs:PageHost',
  'PageHost',
  'src/Wavee.UI.WinUI/Controls/PageHost/PageHost.cs',
  [25, 386],
  'Core navigation panel managing cached page instances with LRU eviction, back/forward stacks, pre-warming, and IPageHostAware lifecycle notifications.',
  ['component', 'navigation', 'page-host'],
  'complex'
);
contains('file:src/Wavee.UI.WinUI/Controls/PageHost/PageHost.cs', 'class:src/Wavee.UI.WinUI/Controls/PageHost/PageHost.cs:PageHost');
exportsEdge('file:src/Wavee.UI.WinUI/Controls/PageHost/PageHost.cs', 'class:src/Wavee.UI.WinUI/Controls/PageHost/PageHost.cs:PageHost');

funcNode(
  'function:src/Wavee.UI.WinUI/Controls/PageHost/PageHost.cs:NavigateCore',
  'NavigateCore',
  'src/Wavee.UI.WinUI/Controls/PageHost/PageHost.cs',
  [261, 332],
  'Core navigation implementation: resolves target page from cache or creates a new instance, updates back/forward stacks, fires navigating/navigated events, and triggers LRU eviction.',
  ['navigation', 'page-host', 'caching'],
  'complex'
);
contains('file:src/Wavee.UI.WinUI/Controls/PageHost/PageHost.cs', 'function:src/Wavee.UI.WinUI/Controls/PageHost/PageHost.cs:NavigateCore');

funcNode(
  'function:src/Wavee.UI.WinUI/Controls/PageHost/PageHost.cs:EvictLruIfNeeded',
  'EvictLruIfNeeded',
  'src/Wavee.UI.WinUI/Controls/PageHost/PageHost.cs',
  [356, 385],
  'Checks the cached page count against MaxCacheSize and evicts the least-recently-used pages until within the limit.',
  ['navigation', 'caching', 'memory-management'],
  'moderate'
);
contains('file:src/Wavee.UI.WinUI/Controls/PageHost/PageHost.cs', 'function:src/Wavee.UI.WinUI/Controls/PageHost/PageHost.cs:EvictLruIfNeeded');

funcNode(
  'function:src/Wavee.UI.WinUI/Controls/PageHost/PageHost.cs:Prewarm',
  'Prewarm',
  'src/Wavee.UI.WinUI/Controls/PageHost/PageHost.cs',
  [220, 242],
  'Pre-instantiates and hides pages for registered routes so first navigation has no creation cost.',
  ['navigation', 'performance', 'page-host'],
  'moderate'
);
contains('file:src/Wavee.UI.WinUI/Controls/PageHost/PageHost.cs', 'function:src/Wavee.UI.WinUI/Controls/PageHost/PageHost.cs:Prewarm');

// 17. PageHostExtensions.cs
fileNode(
  'file:src/Wavee.UI.WinUI/Controls/PageHost/PageHostExtensions.cs',
  'PageHostExtensions.cs',
  'src/Wavee.UI.WinUI/Controls/PageHost/PageHostExtensions.cs',
  'Extension method to walk the WinUI visual tree upward from any element to find the nearest ancestor PageHost.',
  ['utility', 'page-host', 'navigation'],
  'simple'
);
classNode(
  'class:src/Wavee.UI.WinUI/Controls/PageHost/PageHostExtensions.cs:PageHostExtensions',
  'PageHostExtensions',
  'src/Wavee.UI.WinUI/Controls/PageHost/PageHostExtensions.cs',
  [6, 24],
  'Static extensions class providing FindHostingPageHost to locate a PageHost ancestor in the visual tree.',
  ['utility', 'navigation'],
  'simple'
);
contains('file:src/Wavee.UI.WinUI/Controls/PageHost/PageHostExtensions.cs', 'class:src/Wavee.UI.WinUI/Controls/PageHost/PageHostExtensions.cs:PageHostExtensions');
exportsEdge('file:src/Wavee.UI.WinUI/Controls/PageHost/PageHostExtensions.cs', 'class:src/Wavee.UI.WinUI/Controls/PageHost/PageHostExtensions.cs:PageHostExtensions');

// 18. PageHostNavigatedEventArgs.cs
fileNode(
  'file:src/Wavee.UI.WinUI/Controls/PageHost/PageHostNavigatedEventArgs.cs',
  'PageHostNavigatedEventArgs.cs',
  'src/Wavee.UI.WinUI/Controls/PageHost/PageHostNavigatedEventArgs.cs',
  'Event args emitted after a PageHost navigation completes, carrying the target page type and parameter.',
  ['type-definition', 'navigation', 'page-host'],
  'simple'
);
classNode(
  'class:src/Wavee.UI.WinUI/Controls/PageHost/PageHostNavigatedEventArgs.cs:PageHostNavigatedEventArgs',
  'PageHostNavigatedEventArgs',
  'src/Wavee.UI.WinUI/Controls/PageHost/PageHostNavigatedEventArgs.cs',
  [5, 17],
  'Carries page type and parameter for the Navigated event of PageHost.',
  ['type-definition', 'navigation'],
  'simple'
);
contains('file:src/Wavee.UI.WinUI/Controls/PageHost/PageHostNavigatedEventArgs.cs', 'class:src/Wavee.UI.WinUI/Controls/PageHost/PageHostNavigatedEventArgs.cs:PageHostNavigatedEventArgs');
exportsEdge('file:src/Wavee.UI.WinUI/Controls/PageHost/PageHostNavigatedEventArgs.cs', 'class:src/Wavee.UI.WinUI/Controls/PageHost/PageHostNavigatedEventArgs.cs:PageHostNavigatedEventArgs');

// 19. PageHostNavigatingEventArgs.cs
fileNode(
  'file:src/Wavee.UI.WinUI/Controls/PageHost/PageHostNavigatingEventArgs.cs',
  'PageHostNavigatingEventArgs.cs',
  'src/Wavee.UI.WinUI/Controls/PageHost/PageHostNavigatingEventArgs.cs',
  'Event args emitted before a PageHost navigation, carrying the target page type and a Cancel property.',
  ['type-definition', 'navigation', 'page-host'],
  'simple'
);
classNode(
  'class:src/Wavee.UI.WinUI/Controls/PageHost/PageHostNavigatingEventArgs.cs:PageHostNavigatingEventArgs',
  'PageHostNavigatingEventArgs',
  'src/Wavee.UI.WinUI/Controls/PageHost/PageHostNavigatingEventArgs.cs',
  [5, 18],
  'Carries page type and cancellation flag for the Navigating event of PageHost.',
  ['type-definition', 'navigation'],
  'simple'
);
contains('file:src/Wavee.UI.WinUI/Controls/PageHost/PageHostNavigatingEventArgs.cs', 'class:src/Wavee.UI.WinUI/Controls/PageHost/PageHostNavigatingEventArgs.cs:PageHostNavigatingEventArgs');
exportsEdge('file:src/Wavee.UI.WinUI/Controls/PageHost/PageHostNavigatingEventArgs.cs', 'class:src/Wavee.UI.WinUI/Controls/PageHost/PageHostNavigatingEventArgs.cs:PageHostNavigatingEventArgs');

// 20. PageHostNavigationFailedEventArgs.cs
fileNode(
  'file:src/Wavee.UI.WinUI/Controls/PageHost/PageHostNavigationFailedEventArgs.cs',
  'PageHostNavigationFailedEventArgs.cs',
  'src/Wavee.UI.WinUI/Controls/PageHost/PageHostNavigationFailedEventArgs.cs',
  'Event args emitted when a PageHost navigation fails, carrying the exception and target page type.',
  ['type-definition', 'navigation', 'page-host'],
  'simple'
);
classNode(
  'class:src/Wavee.UI.WinUI/Controls/PageHost/PageHostNavigationFailedEventArgs.cs:PageHostNavigationFailedEventArgs',
  'PageHostNavigationFailedEventArgs',
  'src/Wavee.UI.WinUI/Controls/PageHost/PageHostNavigationFailedEventArgs.cs',
  [5, 18],
  'Carries the exception and target page type for the NavigationFailed event of PageHost.',
  ['type-definition', 'navigation'],
  'simple'
);
contains('file:src/Wavee.UI.WinUI/Controls/PageHost/PageHostNavigationFailedEventArgs.cs', 'class:src/Wavee.UI.WinUI/Controls/PageHost/PageHostNavigationFailedEventArgs.cs:PageHostNavigationFailedEventArgs');
exportsEdge('file:src/Wavee.UI.WinUI/Controls/PageHost/PageHostNavigationFailedEventArgs.cs', 'class:src/Wavee.UI.WinUI/Controls/PageHost/PageHostNavigationFailedEventArgs.cs:PageHostNavigationFailedEventArgs');

// 21. PageHostNavigationMode.cs
fileNode(
  'file:src/Wavee.UI.WinUI/Controls/PageHost/PageHostNavigationMode.cs',
  'PageHostNavigationMode.cs',
  'src/Wavee.UI.WinUI/Controls/PageHost/PageHostNavigationMode.cs',
  'Enum defining PageHost navigation modes: New, Back, Forward, and Refresh.',
  ['type-definition', 'navigation', 'page-host'],
  'simple'
);

// 22. PageRegistration.cs
fileNode(
  'file:src/Wavee.UI.WinUI/Controls/PageHost/PageRegistration.cs',
  'PageRegistration.cs',
  'src/Wavee.UI.WinUI/Controls/PageHost/PageRegistration.cs',
  'Static class containing RegisterAll that bulk-registers all application page types with PageRegistry, serving as the page factory registry initializer.',
  ['factory', 'navigation', 'page-host'],
  'moderate'
);
classNode(
  'class:src/Wavee.UI.WinUI/Controls/PageHost/PageRegistration.cs:PageRegistration',
  'PageRegistration',
  'src/Wavee.UI.WinUI/Controls/PageHost/PageRegistration.cs',
  [11, 61],
  'Startup registration class that maps all view model and page types to their factory functions in PageRegistry.',
  ['factory', 'navigation'],
  'moderate'
);
contains('file:src/Wavee.UI.WinUI/Controls/PageHost/PageRegistration.cs', 'class:src/Wavee.UI.WinUI/Controls/PageHost/PageRegistration.cs:PageRegistration');
exportsEdge('file:src/Wavee.UI.WinUI/Controls/PageHost/PageRegistration.cs', 'class:src/Wavee.UI.WinUI/Controls/PageHost/PageRegistration.cs:PageRegistration');

// 23. PageRegistry.cs
fileNode(
  'file:src/Wavee.UI.WinUI/Controls/PageHost/PageRegistry.cs',
  'PageRegistry.cs',
  'src/Wavee.UI.WinUI/Controls/PageHost/PageRegistry.cs',
  'Dictionary-based registry mapping page type keys to factory functions, used by PageHost to instantiate pages without reflection.',
  ['factory', 'navigation', 'page-host'],
  'simple',
  'Follows the factory-registry pattern to avoid Activator.CreateInstance reflection overhead.'
);
classNode(
  'class:src/Wavee.UI.WinUI/Controls/PageHost/PageRegistry.cs:PageRegistry',
  'PageRegistry',
  'src/Wavee.UI.WinUI/Controls/PageHost/PageRegistry.cs',
  [14, 32],
  'Thread-safe dictionary registry for page factory delegates keyed by type, used by PageHost for page instantiation.',
  ['factory', 'navigation'],
  'simple'
);
contains('file:src/Wavee.UI.WinUI/Controls/PageHost/PageRegistry.cs', 'class:src/Wavee.UI.WinUI/Controls/PageHost/PageRegistry.cs:PageRegistry');
exportsEdge('file:src/Wavee.UI.WinUI/Controls/PageHost/PageRegistry.cs', 'class:src/Wavee.UI.WinUI/Controls/PageHost/PageRegistry.cs:PageRegistry');

// 24. PageStackEntry.cs
fileNode(
  'file:src/Wavee.UI.WinUI/Controls/PageHost/PageStackEntry.cs',
  'PageStackEntry.cs',
  'src/Wavee.UI.WinUI/Controls/PageHost/PageStackEntry.cs',
  'Immutable record representing a single entry in the PageHost back/forward stack, holding page type and parameter.',
  ['type-definition', 'navigation', 'page-host'],
  'simple'
);
classNode(
  'class:src/Wavee.UI.WinUI/Controls/PageHost/PageStackEntry.cs:PageStackEntry',
  'PageStackEntry',
  'src/Wavee.UI.WinUI/Controls/PageHost/PageStackEntry.cs',
  [5, 14],
  'Holds a page type and its navigation parameter for the PageHost navigation stack.',
  ['type-definition', 'navigation'],
  'simple'
);
contains('file:src/Wavee.UI.WinUI/Controls/PageHost/PageStackEntry.cs', 'class:src/Wavee.UI.WinUI/Controls/PageHost/PageStackEntry.cs:PageStackEntry');
exportsEdge('file:src/Wavee.UI.WinUI/Controls/PageHost/PageStackEntry.cs', 'class:src/Wavee.UI.WinUI/Controls/PageHost/PageStackEntry.cs:PageStackEntry');

// 25. PlayActionDialog.cs
fileNode(
  'file:src/Wavee.UI.WinUI/Controls/PlayActionDialog.cs',
  'PlayActionDialog.cs',
  'src/Wavee.UI.WinUI/Controls/PlayActionDialog.cs',
  'ContentDialog that presents playback action options (Play now, Add to queue, etc.) for a given content URI, returning the user-chosen PlayAction result.',
  ['component', 'dialog', 'playback', 'ui-interaction'],
  'moderate'
);
classNode(
  'class:src/Wavee.UI.WinUI/Controls/PlayActionDialog.cs:PlayActionDialog',
  'PlayActionDialog',
  'src/Wavee.UI.WinUI/Controls/PlayActionDialog.cs',
  [24, 142],
  'Static helper class that builds and shows a ContentDialog offering playback action choices, returning the selected option asynchronously.',
  ['component', 'dialog', 'playback'],
  'moderate'
);
contains('file:src/Wavee.UI.WinUI/Controls/PlayActionDialog.cs', 'class:src/Wavee.UI.WinUI/Controls/PlayActionDialog.cs:PlayActionDialog');
exportsEdge('file:src/Wavee.UI.WinUI/Controls/PlayActionDialog.cs', 'class:src/Wavee.UI.WinUI/Controls/PlayActionDialog.cs:PlayActionDialog');
funcNode(
  'function:src/Wavee.UI.WinUI/Controls/PlayActionDialog.cs:ShowAsync',
  'ShowAsync',
  'src/Wavee.UI.WinUI/Controls/PlayActionDialog.cs',
  [30, 118],
  'Builds the list of available play actions from a URI and shows a ContentDialog; awaits user selection and returns the chosen PlayAction or null on cancel.',
  ['dialog', 'playback', 'async'],
  'complex'
);
contains('file:src/Wavee.UI.WinUI/Controls/PlayActionDialog.cs', 'function:src/Wavee.UI.WinUI/Controls/PlayActionDialog.cs:ShowAsync');

const result = { nodes, edges };
console.log('nodeCount:', nodes.length, 'edgeCount:', edges.length);

const outDir = 'C:/WAVEE/WaveeMusic/.understand-anything/intermediate';
if (!require('fs').existsSync(outDir)) require('fs').mkdirSync(outDir, { recursive: true });
fs.writeFileSync(outDir + '/batch-30.json', JSON.stringify(result, null, 2), 'utf8');
console.log('Written batch-30.json');
