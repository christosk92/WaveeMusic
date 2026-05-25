const nodes = [];
const edges = [];

function fileNode(path, summary, tags, complexity, languageNotes) {
  const n = {id:'file:'+path,type:'file',name:path.split('/').pop(),filePath:path,summary,tags,complexity};
  if(languageNotes) n.languageNotes=languageNotes;
  nodes.push(n);
}
function classNode(path,name,s,e,summary,tags,complexity) {
  nodes.push({id:'class:'+path+':'+name,type:'class',name,filePath:path,lineRange:[s,e],summary,tags,complexity});
  edges.push({source:'file:'+path,target:'class:'+path+':'+name,type:'contains',direction:'forward',weight:1.0});
  edges.push({source:'file:'+path,target:'class:'+path+':'+name,type:'exports',direction:'forward',weight:0.8});
}
function fnNode(path,name,s,e,summary,tags,complexity) {
  nodes.push({id:'function:'+path+':'+name,type:'function',name,filePath:path,lineRange:[s,e],summary,tags,complexity});
  edges.push({source:'file:'+path,target:'function:'+path+':'+name,type:'contains',direction:'forward',weight:1.0});
}

const p1='src/Wavee.UI.WinUI/ViewModels/ArtistDiscographyPageViewModel.cs';
fileNode(p1,'ViewModel for the artist discography page, orchestrating async initialization and load of full discography data filtered by release type.',['component','view-model','discography','artist'],'moderate');
classNode(p1,'ArtistDiscographyPageViewModel',25,149,'Manages discography page state, exposes albums/singles/compilations collections, and coordinates async loading from artist data.',['view-model','discography','artist'],'moderate');
fnNode(p1,'Initialize',63,82,'Initializes the discography page with artist metadata and triggers async data load.',['initialization','lifecycle'],'simple');
fnNode(p1,'LoadAllAsync',87,148,'Asynchronously fetches and categorizes all release groups (albums, singles, compilations) for the artist.',['async','data-loading','discography'],'moderate');

const p2='src/Wavee.UI.WinUI/ViewModels/ArtistsLibraryViewModel.cs';
fileNode(p2,'Large ViewModel managing the Artists Library view with multi-mode display (saved/liked/discography), filtering, sorting, narrow layout, track panel, and breadcrumb navigation.',['view-model','library','artist','complex-state'],'complex');
classNode(p2,'ArtistsLibraryViewModel',28,1211,'Orchestrates the artists library: source switching, album selection, track panel, narrow/wide layout adaptation, sorting, and filtering across artist/album collections.',['view-model','library','artist'],'complex');
fnNode(p2,'OnSourceModeChangedCore',260,295,'Handles transitions between Saved/Liked/Discography source modes, reloading the appropriate artist list.',['event-handler','source-mode'],'moderate');
fnNode(p2,'LoadDataAsync',318,358,'Loads artist data for the current source mode, dispatching to saved or liked artist loaders.',['async','data-loading'],'moderate');
fnNode(p2,'LoadLikedArtistsAsync',360,391,'Fetches liked artists from the library and maps them to display items, applying current filter and sort.',['async','data-loading','library'],'moderate');
fnNode(p2,'OpenArtistDetails',452,482,'Navigates to the inline artist details panel for the selected artist, loading discography if needed.',['navigation','artist-detail'],'moderate');
fnNode(p2,'OnSelectedArtistChanged',571,594,'Responds to artist selection changes, updating breadcrumbs and triggering detail load.',['event-handler','selection'],'moderate');
fnNode(p2,'OnSaveStateChanged',836,905,'Reacts to library save-state changes (follow/unfollow), updating the artist list incrementally.',['event-handler','library-sync'],'complex');
fnNode(p2,'SortArtists',997,1020,'Applies the current sort order to the saved artists collection.',['sorting','utility'],'moderate');
fnNode(p2,'PlayTrackAsync',1076,1106,'Initiates playback of a selected track within the artist context.',['playback','async'],'moderate');

const p3='src/Wavee.UI.WinUI/ViewModels/ArtistViewModel.cs';
fileNode(p3,'Core ViewModel for the full artist page, coordinating hero images, spotlight projection, discography sections, music video catalog, theme derivation, and suspend/resume lifecycle.',['view-model','artist','complex-state','lifecycle'],'complex');
classNode(p3,'ArtistViewModel',42,998,'Manages the complete artist page state: metadata loading, discography sections, music video prime, hero image resolution, theme application, and hibernate/resume for nav cache.',['view-model','artist','navigation'],'complex');
fnNode(p3,'RaiseSpotlightProjection',228,281,'Builds and broadcasts the spotlight projection (hero image + color + artist name) for the page header.',['hero-image','theming','spotlight'],'moderate');
fnNode(p3,'Initialize',318,356,'Sets up the artist page from a navigation parameter, optionally prefilling from cached data.',['initialization','lifecycle'],'moderate');
fnNode(p3,'Hibernate',411,431,'Suspends background work and releases heavy resources when the page is removed from the nav cache.',['lifecycle','memory-management'],'moderate');
fnNode(p3,'ApplyOverviewState',433,469,'Switches the visible content area between biography, discography, and related-artists overview panels.',['state-management','navigation'],'moderate');
fnNode(p3,'LoadAsync',635,759,'Main async load pipeline: fetches artist metadata, biography, social links, and top tracks from SpClient/Pathfinder.',['async','data-loading','artist'],'complex');
fnNode(p3,'ApplySecondaryArtistSectionsAsync',761,810,'Loads and appends related artists, featured playlists, and appears-on sections after the primary load completes.',['async','data-loading','sections'],'complex');
fnNode(p3,'Dispose',980,997,'Disposes all reactive subscriptions and cancels in-flight async operations.',['lifecycle','disposal'],'simple');

const p4='src/Wavee.UI.WinUI/ViewModels/BrowseViewModel.cs';
fileNode(p4,'ViewModel for the Browse page, loading genre/mood categories as hero slides with derived color brushes and background imagery from the Pathfinder catalog API.',['view-model','browse','hero-image'],'moderate');
classNode(p4,'BrowseViewModel',34,335,'Loads top-level browse categories, derives hero slides and color brushes, and exposes them for the Browse page carousel and grid.',['view-model','browse','categories'],'moderate');
fnNode(p4,'ReloadCoreAsync',104,191,'Fetches browse categories from Pathfinder, maps them to slide items and color data, and populates the display collections.',['async','data-loading','browse'],'complex');
fnNode(p4,'DeriveHeroSlides',200,280,'Converts raw category items into hero slide descriptors with image URLs and accent colors.',['hero-image','data-mapping'],'moderate');
fnNode(p4,'BuildHeaderBrush',286,311,'Constructs a gradient or solid brush from an extracted category accent color for the section header.',['theming','brush','utility'],'moderate');

const p5='src/Wavee.UI.WinUI/ViewModels/ConcertViewModel.cs';
fileNode(p5,'ViewModel and supporting data-holder records for the Concert detail page, loading event metadata, artists, offers, related events, and applying theme colors.',['view-model','concert','data-model'],'moderate');
classNode(p5,'ConcertViewModel',17,322,'Loads concert event data from SpClient, maps artists/offers/related/playlists into typed sub-ViewModels, and applies a derived theme color.',['view-model','concert','theming'],'complex');
fnNode(p5,'ApplyTheme',125,185,'Extracts dominant color from the concert poster image and applies derived brushes for the page theme.',['theming','color-extraction'],'complex');
fnNode(p5,'LoadAsync',187,321,'Fetches the full concert event payload and maps it into typed sub-ViewModels (artists, offers, related).',['async','data-loading','concert'],'complex');

const p6='src/Wavee.UI.WinUI/ViewModels/ConnectStateViewModel.cs';
fileNode(p6,'Debug ViewModel for inspecting live Spotify Connect cluster state events, with filter/search over a scrolling event log.',['view-model','debug','connect','event-log'],'moderate');
classNode(p6,'ConnectStateViewModel',14,149,'Subscribes to raw dealer Connect events, maintains a bounded event log, and applies kind/search filters for the debug panel.',['view-model','debug','connect'],'moderate');
classNode(p6,'ConnectStateEventRow',151,207,'Data record representing a single Connect state event row with timestamp, kind, device name, and serialized payload.',['data-model','debug','connect'],'simple');
fnNode(p6,'OnSourceChanged',61,94,'Reacts to new Connect events from the dealer observable, appending and trimming the event log.',['event-handler','connect','reactive'],'moderate');
fnNode(p6,'Rebuild',96,109,'Rebuilds the filtered event view from the full log by re-applying current kind and search filters.',['filtering','view-refresh'],'moderate');

const p7='src/Wavee.UI.WinUI/ViewModels/Contracts/ITrackListViewModel.cs';
fileNode(p7,'Interface contract defining the surface shared by all track-list ViewModels: playback commands, selection, queue operations, and metadata bindings.',['type-definition','interface','track-list','contract'],'moderate');
classNode(p7,'ITrackListViewModel',11,101,'Defines the common ViewModel contract for track lists across playlist, album, artist, and liked-songs views.',['interface','track-list','contract'],'moderate');

const p8='src/Wavee.UI.WinUI/ViewModels/CreatePlaylistViewModel.cs';
fileNode(p8,'ViewModel for the Create Playlist dialog, validating the name field, creating the playlist via SpClient, and closing the current tab on success.',['view-model','playlist','create'],'moderate');
classNode(p8,'CreatePlaylistViewModel',14,138,'Handles user input validation and async playlist creation, then navigates away on completion.',['view-model','playlist','create'],'moderate');
fnNode(p8,'CreateAsync',87,109,'Submits the playlist creation request to SpClient and handles success/error feedback.',['async','playlist','api-handler'],'moderate');

const p9='src/Wavee.UI.WinUI/ViewModels/DebugViewModel.cs';
fileNode(p9,'Developer debug ViewModel providing a request workbench to send raw SpClient, Pathfinder GraphQL, and extended-metadata requests with live response formatting and hex dump output.',['view-model','debug','developer-tools','api-handler'],'complex');
classNode(p9,'DebugViewModel',23,708,'Exposes a request builder for SpClient and Pathfinder calls, with preset management, protobuf decoding, JSON formatting, and hex dump rendering for developer diagnostics.',['view-model','debug','developer-tools'],'complex');
fnNode(p9,'SendAsync',205,246,'Dispatches a raw SpClient HTTP request and formats the response body for display.',['async','api-handler','debug'],'moderate');
fnNode(p9,'SendPathfinderAsync',488,535,'Executes a Pathfinder GraphQL operation and renders the JSON response in the debug panel.',['async','pathfinder','debug'],'moderate');
fnNode(p9,'FormatProtobufResponse',645,685,'Decodes a binary protobuf response body to human-readable text for the debug output pane.',['protobuf','debug','serialization'],'moderate');

const p10='src/Wavee.UI.WinUI/ViewModels/DetailViewEnvelopes.cs';
fileNode(p10,'Defines discriminated-union envelope records used to carry detail-view navigation payloads (album, artist, playlist, episode) through the nav stack.',['data-model','navigation','type-definition'],'simple');

const p11='src/Wavee.UI.WinUI/ViewModels/EpisodePageViewModel.cs';
fileNode(p11,'ViewModel for the podcast episode detail page, managing playback state, chapter timeline, comment threads, progress tracking, and sibling episode navigation.',['view-model','podcast','episode','playback'],'complex');
classNode(p11,'EpisodePageViewModel',34,989,'Loads and displays a podcast episode with playback controls, chapter seek, comment submission, progress tracking, and sibling navigation.',['view-model','podcast','episode'],'complex');
fnNode(p11,'LoadAsync',433,590,'Fetches episode metadata, transcript, chapters, and sibling list; applies initial playback state.',['async','data-loading','podcast'],'complex');
fnNode(p11,'RefreshChapterTimeline',776,819,'Rebuilds the chapter timeline items from the episode chapters list, marking the current active chapter.',['chapters','timeline','playback'],'moderate');
fnNode(p11,'Play',821,849,'Initiates or resumes playback of the episode at the given position, optionally seeking to a chapter.',['playback','async'],'moderate');
fnNode(p11,'SubmitCommentAsync',911,940,'Posts a new comment to the episode thread and prepends it to the local comment list optimistically.',['async','comments','podcast'],'moderate');
fnNode(p11,'Dispose',974,988,'Cancels active subscriptions and releases playback resources on page disposal.',['lifecycle','disposal'],'simple');

const p12='src/Wavee.UI.WinUI/ViewModels/FeedbackViewModel.cs';
fileNode(p12,'ViewModel for the in-app feedback wizard, handling multi-step form validation, screenshot attachment, diagnostic log assembly, and GitHub issue submission.',['view-model','feedback','wizard','developer-tools'],'complex');
classNode(p12,'FeedbackViewModel',23,409,'Drives a multi-step feedback submission flow: category selection, details entry with screenshot attachments, and automated GitHub issue creation with diagnostics.',['view-model','feedback','wizard'],'complex');
fnNode(p12,'ValidateDetailsStep',78,123,'Validates the details step fields (title, description, repro steps) and surfaces per-field errors.',['validation','form','feedback'],'moderate');
fnNode(p12,'TryAddImage',186,244,'Opens a file picker and appends the selected screenshot to the attachment list after size/format validation.',['file-picker','validation','feedback'],'moderate');
fnNode(p12,'SubmitAsync',319,381,'Serializes the feedback form, uploads attachments, and creates a GitHub issue via API.',['async','api-handler','feedback'],'complex');
fnNode(p12,'BuildDiagnosticsLog',383,396,'Collects runtime diagnostics (OS, app version, settings snapshot) into a formatted string for the issue body.',['diagnostics','utility'],'moderate');

const p13='src/Wavee.UI.WinUI/ViewModels/FriendFeedRowViewModel.cs';
fileNode(p13,'ViewModel row for a single friend-feed entry, mapping Spotify friend activity to displayable metadata and providing navigation commands to artist/track/context.',['view-model','friend-feed','social','navigation'],'moderate');
classNode(p13,'FriendFeedRowViewModel',17,169,'Represents one friend activity row with avatar, friend name, track/artist/context metadata, and navigation command bindings.',['view-model','friend-feed','social'],'moderate');
fnNode(p13,'FriendFeedRowViewModel',49,82,'Constructor mapping raw friend activity data to display properties and nav commands.',['constructor','data-mapping'],'moderate');
fnNode(p13,'NavigateToContext',124,145,'Navigates to the playlist/album/show context the friend was listening within.',['navigation','social'],'moderate');

const p14='src/Wavee.UI.WinUI/ViewModels/Home/BrowseAllGrouper.cs';
fileNode(p14,'Groups a flat list of browse-all items into categorized display groups sorted by genre/mood taxonomy, for the Browse All section of the Home feed.',['utility','browse','grouping','home'],'moderate');
classNode(p14,'BrowseAllGrouper',17,83,'Partitions raw browse items into named groups based on taxonomy classification and applies sort order within each group.',['utility','browse','grouping'],'moderate');
fnNode(p14,'Group',29,59,'Executes the full grouping pipeline over a collection of BrowseAllItems into sorted BrowseAllGroup instances.',['grouping','utility'],'moderate');

const p15='src/Wavee.UI.WinUI/ViewModels/Home/BrowseAllGroupKind.cs';
fileNode(p15,'Enum defining browse-all group kind values and an extension providing the resource key string for each kind used in localization.',['type-definition','browse','enum','home'],'simple');
classNode(p15,'BrowseAllGroupKindExtensions',13,25,'Extension class providing the resource key string for each BrowseAllGroupKind enum value.',['utility','localization','browse'],'simple');

const p16='src/Wavee.UI.WinUI/ViewModels/Home/BrowseAllItem.cs';
fileNode(p16,'Data record types for a single browse-all item (genre/mood card) and its display group container.',['data-model','browse','home'],'simple');

const p17='src/Wavee.UI.WinUI/ViewModels/Home/BrowseAllParser.cs';
fileNode(p17,'Parses raw Pathfinder browse-all response JSON into typed BrowseAllItem records, handling missing fields gracefully.',['utility','parsing','browse','home'],'moderate');
classNode(p17,'BrowseAllParser',20,84,'Extracts genre and mood items from Pathfinder API response nodes into BrowseAllItem instances.',['utility','parsing','browse'],'moderate');
fnNode(p17,'TryExtract',41,83,'Attempts to extract a single BrowseAllItem from a JSON node, returning null on parse failure.',['parsing','utility','browse'],'moderate');

const p18='src/Wavee.UI.WinUI/ViewModels/Home/BrowseAllTaxonomy.cs';
fileNode(p18,'Defines the static genre/mood taxonomy used to assign browse items to display groups and sort orders in the Browse All section.',['data-model','browse','taxonomy','home'],'moderate');
classNode(p18,'BrowseAllTaxonomy',11,95,'Holds a static lookup table mapping Spotify category IDs to taxonomy entries (group kind and display order).',['data-model','browse','taxonomy'],'moderate');

const p19='src/Wavee.UI.WinUI/ViewModels/Home/HomeFeedViewModel.cs';
fileNode(p19,'ViewModel managing the Home feed sections collection: applies cache/fresh/background data, handles local library section injection, chip selection, user preference persistence, and section pin/reorder.',['view-model','home','feed','sections'],'complex');
classNode(p19,'HomeFeedViewModel',34,941,'Orchestrates the Home feed: snapshot application, local section upsert, chip-based facet filtering, section visibility/pin/order preferences, and background refresh coordination.',['view-model','home','feed'],'complex');
fnNode(p19,'RefreshLocalSectionAsync',281,370,'Rebuilds the local library section content (recently played, saved albums/artists) and upserts it into the feed.',['async','local-library','home'],'complex');
fnNode(p19,'ApplyChips',579,611,'Maps the available home feed chips (facets) to display items and marks the active chip.',['chips','filtering','home'],'moderate');
fnNode(p19,'ApplyPreferences',613,686,'Restores user preference state (section order, visibility, pins) from persisted settings onto the current feed sections.',['preferences','persistence','home'],'complex');
fnNode(p19,'SelectChipAsync',802,875,'Handles a chip tap: updates the active facet and triggers a feed refetch for the selected chip.',['async','chips','navigation'],'complex');

const p20='src/Wavee.UI.WinUI/ViewModels/Home/HomeGreetingViewModel.cs';
fileNode(p20,'ViewModel for the Home page greeting row, displaying a time-of-day greeting and the authenticated user display name.',['view-model','home','greeting','auth'],'moderate');
classNode(p20,'HomeGreetingViewModel',20,115,'Binds the greeting string and user display name, updating both on auth state change and deriving the appropriate time-of-day salutation.',['view-model','home','greeting'],'moderate');

const p21='src/Wavee.UI.WinUI/ViewModels/Home/HomeHeroAdapter.cs';
fileNode(p21,'Adapter that observes the HomeFeedViewModel sections collection and rebuilds hero slide descriptors, side-card items, and hero region lists for the Home page carousel.',['view-model','home','hero-image','adapter'],'complex');
classNode(p21,'HomeHeroAdapter',54,561,'Transforms feed section items into hero slides and side cards for the Home hero carousel, debouncing rapid section collection changes.',['adapter','home','hero-image'],'complex');
classNode(p21,'SideCardItem',29,37,'Lightweight record holding the display data for a single side card item in the Home hero area.',['data-model','home','hero-image'],'simple');
fnNode(p21,'RebuildRegions',216,305,'Partitions the visible feed sections into hero, side-card, and overflow region buckets according to position and content-type rules.',['home','hero-image','sections'],'complex');
fnNode(p21,'RebuildHeroSlides',319,413,'Constructs the ordered list of hero slide descriptors from the hero-region items, including image and accent-color metadata.',['home','hero-image','slides'],'complex');

const p22='src/Wavee.UI.WinUI/ViewModels/Home/HomeRecommendationsViewModel.cs';
fileNode(p22,'ViewModel managing the Home recommendations baseline: maps recently-played items to enriched preview tracks and coordinates async metadata enrichment from Pathfinder.',['view-model','home','recommendations','recently-played'],'moderate');
classNode(p22,'HomeRecommendationsViewModel',35,351,'Maintains the baseline recommendation list from recently played history, enriching each item with full track metadata asynchronously.',['view-model','home','recommendations'],'moderate');
fnNode(p22,'BeginBaselineEnrichment',157,189,'Starts background enrichment of baseline recently-played items by scheduling an async metadata fetch.',['async','enrichment','recently-played'],'moderate');
fnNode(p22,'EnrichBaselineItemsAsync',191,224,'Fetches full track metadata for the baseline items and applies it to the display collection.',['async','data-loading','enrichment'],'moderate');

const p23='src/Wavee.UI.WinUI/ViewModels/Home/HomeRegion.cs';
fileNode(p23,'Defines the HomeRegion discriminated-union type and its factory, used to classify each Home feed section into hero, section, or chip regions.',['data-model','home','sections','type-definition'],'moderate');
classNode(p23,'HomeRegion',31,114,'Encapsulates a Home feed region classification (hero, section-row, chip) with typed payload for the HomeHeroAdapter partitioning logic.',['data-model','home','sections'],'moderate');

const p24='src/Wavee.UI.WinUI/ViewModels/Home/HomeSectionClassifier.cs';
fileNode(p24,'Stateless classifier that maps a HomeFeedViewModel section item to a HomeRegion kind based on content type, position, and display rules.',['utility','home','sections','classifier'],'moderate');
classNode(p24,'HomeSectionClassifier',19,89,'Provides the ClassifyRegion method that inspects a feed section and returns the appropriate HomeRegion variant.',['utility','home','sections'],'moderate');
fnNode(p24,'ClassifyRegion',29,88,'Returns the HomeRegion classification for a section item based on its type, index, and hero eligibility rules.',['classification','home','sections'],'moderate');

const p25='src/Wavee.UI.WinUI/ViewModels/HomeViewModel.cs';
fileNode(p25,'Master ViewModel for the Home page, orchestrating feed loading, section/item mapping from Pathfinder JSON, nav-cache hibernate/resume, theme derivation from hero content, and page bleed updates.',['view-model','home','entry-point','complex-state'],'complex');
classNode(p25,'HomeViewModel',40,1133,'Root Home page ViewModel: owns the Sections collection, drives load/refresh via HomeFeedViewModel, maps Pathfinder response JSON to typed section items, handles hibernate/resume for nav cache, and derives hero/page-bleed theming.',['view-model','home','feed'],'complex');
classNode(p25,'HomeSection',1145,1275,'Observable ViewModel for a single Home feed section row, holding the section title, content cards, and visibility/pin state.',['view-model','home','sections'],'complex');
classNode(p25,'HomeSectionItem',1277,1623,'Observable ViewModel wrapping a single Home feed card item with content type, image, title, subtitle, and navigation target.',['view-model','home','sections','data-model'],'complex');
classNode(p25,'HomeBaselinePreviewTrack',1634,1648,'Lightweight data record for a recently-played baseline preview track displayed in the recommendations strip.',['data-model','home','recently-played'],'simple');
classNode(p25,'HomeChipViewModel',1650,1661,'Simple data record for a Home page facet chip with label and selection state.',['data-model','home','chips'],'simple');
fnNode(p25,'HomeViewModel',128,246,'Constructor subscribing to playback, auth, and library change observables to drive live section updates.',['constructor','initialization','reactive'],'complex');
fnNode(p25,'LoadAsync',294,360,'Initiates Home page load: checks cache, triggers fresh fetch, populates sections, and applies baseline recommendations.',['async','data-loading','home'],'complex');
fnNode(p25,'MapSectionsFromResponse',520,584,'Converts the Pathfinder home feed response into typed HomeSection objects with their item collections.',['data-mapping','home','sections'],'complex');
fnNode(p25,'MapSectionItem',586,624,'Maps a single Pathfinder response item node to a HomeSectionItem with content-type detection.',['data-mapping','home','sections'],'moderate');
fnNode(p25,'NavigateToItem',896,941,'Handles a card tap by routing to the appropriate page (artist/album/playlist/podcast) based on item content type.',['navigation','home'],'moderate');
fnNode(p25,'ApplyTheme',957,1013,'Derives the page accent color from the hero carousel item and applies gradient brushes to the page header.',['theming','hero-image','home'],'complex');
fnNode(p25,'Dispose',1086,1100,'Disposes all reactive subscriptions and cancels pending async operations.',['lifecycle','disposal'],'simple');

const nodeCount = nodes.length;
const edgeCount = edges.length;
console.log('Nodes:', nodeCount, 'Edges:', edgeCount);

const fs = require('fs');

if (nodeCount <= 60 && edgeCount <= 120) {
  fs.writeFileSync('C:/WAVEE/WaveeMusic/.understand-anything/intermediate/batch-49.json', JSON.stringify({nodes, edges}, null, 2));
  console.log('Written single part');
} else {
  const parts = Math.ceil(Math.max(nodeCount / 60, edgeCount / 120));
  console.log('Splitting into', parts, 'parts');
  // Get file paths in alphabetical order
  const filePaths = [
    'src/Wavee.UI.WinUI/ViewModels/ArtistDiscographyPageViewModel.cs',
    'src/Wavee.UI.WinUI/ViewModels/ArtistsLibraryViewModel.cs',
    'src/Wavee.UI.WinUI/ViewModels/ArtistViewModel.cs',
    'src/Wavee.UI.WinUI/ViewModels/BrowseViewModel.cs',
    'src/Wavee.UI.WinUI/ViewModels/ConcertViewModel.cs',
    'src/Wavee.UI.WinUI/ViewModels/ConnectStateViewModel.cs',
    'src/Wavee.UI.WinUI/ViewModels/Contracts/ITrackListViewModel.cs',
    'src/Wavee.UI.WinUI/ViewModels/CreatePlaylistViewModel.cs',
    'src/Wavee.UI.WinUI/ViewModels/DebugViewModel.cs',
    'src/Wavee.UI.WinUI/ViewModels/DetailViewEnvelopes.cs',
    'src/Wavee.UI.WinUI/ViewModels/EpisodePageViewModel.cs',
    'src/Wavee.UI.WinUI/ViewModels/FeedbackViewModel.cs',
    'src/Wavee.UI.WinUI/ViewModels/FriendFeedRowViewModel.cs',
    'src/Wavee.UI.WinUI/ViewModels/Home/BrowseAllGrouper.cs',
    'src/Wavee.UI.WinUI/ViewModels/Home/BrowseAllGroupKind.cs',
    'src/Wavee.UI.WinUI/ViewModels/Home/BrowseAllItem.cs',
    'src/Wavee.UI.WinUI/ViewModels/Home/BrowseAllParser.cs',
    'src/Wavee.UI.WinUI/ViewModels/Home/BrowseAllTaxonomy.cs',
    'src/Wavee.UI.WinUI/ViewModels/Home/HomeFeedViewModel.cs',
    'src/Wavee.UI.WinUI/ViewModels/Home/HomeGreetingViewModel.cs',
    'src/Wavee.UI.WinUI/ViewModels/Home/HomeHeroAdapter.cs',
    'src/Wavee.UI.WinUI/ViewModels/Home/HomeRecommendationsViewModel.cs',
    'src/Wavee.UI.WinUI/ViewModels/Home/HomeRegion.cs',
    'src/Wavee.UI.WinUI/ViewModels/Home/HomeSectionClassifier.cs',
    'src/Wavee.UI.WinUI/ViewModels/HomeViewModel.cs'
  ];

  const chunkSize = Math.ceil(filePaths.length / parts);
  for (let k = 0; k < parts; k++) {
    const partFiles = new Set(filePaths.slice(k * chunkSize, (k+1) * chunkSize));
    const partNodes = nodes.filter(n => {
      const fp = n.filePath;
      return fp && partFiles.has(fp);
    });
    const partNodeIds = new Set(partNodes.map(n => n.id));
    const partEdges = edges.filter(e => partNodeIds.has(e.source));

    fs.writeFileSync(
      'C:/WAVEE/WaveeMusic/.understand-anything/intermediate/batch-49-part-' + (k+1) + '.json',
      JSON.stringify({nodes: partNodes, edges: partEdges}, null, 2)
    );
    console.log('Part', k+1, '- nodes:', partNodes.length, 'edges:', partEdges.length, 'files:', [...partFiles].map(f=>f.split('/').pop()).join(', '));
  }
}
