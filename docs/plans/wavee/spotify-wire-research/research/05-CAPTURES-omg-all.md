# omg.saz + all.saz — general browsing & search

> Workflow agent output, run `wf_5a5408b2-258`.
- **summary:** Two Fiddler captures of the Spotify 1.2.94.583 desktop client (user 31unjfmo3oefvlz36ef3eb6kj5tq, country NL, catalogue premium, locale en, TZ Europe/Amsterdam) totalling 4730 sessions (omg.saz 2440, all.saz 2290), every one of which was enumerated. The activity is cold start + login, Home (with facet chips), Browse/genre pages, incremental search typing ("w"→"wasa", "mama im a criminal") across all result tabs, artist pages + full discography paging, album pages + merch, playlist/show pages with diff-based sync, What's New feed, podcast episodes with transcripts and comments, Now-Playing-View artist panel, radio/autoplay/station continuation, playlist extension + assisted curation, DJ/Mix-lens playlist "signals", Connect device state + remote commands, and audio/video/DRM playback (playplay keys, PlayReady video license, canvas video). 33 distinct Pathfinder operationNames were observed (12 of which Wavee does not implement at all), plus 1093 extended-metadata batches covering 45,896 entity requests across 51 distinct extension kinds.

**operations:**

  ---
  - **operationName:** searchSuggestions
  - **sha256Hash:** 556f5a15b2fdd3a7113ffd377ad9805e38a3a27b8bb1ca7d6d76bad54aa8ee12
  - **variablesExample:** {"query":"wasa","limit":30,"numberOfTopResults":30,"offset":0,"includeAuthors":true,"includeAlbumPreReleases":true,"includeEpisodeContentRatingsV2":true}
  - **count:** 22
  - **responseShape:** searchV2.query, searchV2.topResultsV2.itemsV2[].item.__typename

  ---
  - **operationName:** fetchExtractedColors
  - **sha256Hash:** 36e90fcaea00d47c695fce31874efeb2519b97d4cd0ee1abfb4f8dc9348596ea
  - **variablesExample:** {"imageUris":["https://i.scdn.co/image/ab67616600001e01ec15119465a53234f0f27649"]}
  - **count:** 17
  - **responseShape:** extractedColors[].colorDark.hex, .colorLight.hex, .colorRaw.hex, each with .isFallback

  ---
  - **operationName:** queryNpvArtist
  - **sha256Hash:** b2cedf7ed0f29c713567d97ed69b848c8387294edfe58a0e439a3a5669cc27bb
  - **variablesExample:** {"artistUri":"spotify:artist:5o615XColiSVMPDWlslKSk","trackUri":"spotify:track:6PYyVPaD3pzSbrdQKEDHm6","contributorsLimit":10,"contributorsOffset":0,"enableRelatedVideos":true,"enableRelatedAudioTracks":true}
  - **count:** 16
  - **responseShape:** artistUnion.{profile.biography.text, profile.externalLinks.items[].{name,url}, stats.{followers,monthlyListeners,worldRank,topCities.items[].{city,country,region,numberOfListeners}}, visuals.avatarImage.sources[], headerImage.data.sources[], goods.concerts, onPlatformReputationTrait.verification.{isVerified,isRegistered}}; trackUnion.{canvas.{fileId,type,uri,url}, creditsTrait.contributors.items[].{name,role,uri,url}, creditsTrait.sources.items[].name, associationsV3.unmappedVideoTrackAssociations}

  ---
  - **operationName:** queryNpvArtist
  - **sha256Hash:** 047c9c225967d41a763949a4db3f0493e901c9f8689a6537408aabf9beffc177
  - **variablesExample:** {"artistUri":"spotify:artist:0qlWcS66ohOIi0M8JZwPft","trackUri":"spotify:track:7LFwi4RolcCPnVEXXXVfQP","contributorsLimit":10,"contributorsOffset":0,"enableRelatedVideos":true,"enableRelatedAudioTracks":true}
  - **count:** 1
  - **responseShape:** same as b2cedf7e except artistUnion has NO onPlatformReputationTrait — this is the older variant, and it is the one Wavee currently hardcodes. 1 sample, 200 OK.

  ---
  - **operationName:** feedBaselineLookup
  - **sha256Hash:** a950fb7c4ecdcaf2aad2f3ca9ee9c3aa4b9c43c97e1d07d05148c4d355bea7fc
  - **variablesExample:** {"uris":["spotify:album:1wDlaw15yDYPgOhE41kzvg","spotify:album:7f3eph7vSHbBnJZYNN0ZQR","spotify:album:5Lf9xfYDltNx6nA1v7mvTa"]}
  - **count:** 14
  - **responseShape:** lookup[]._uri, lookup[].data.{uri,name,previewPlayback.audioPreview.{cdnUrl,offset,transcriptUrl}, previewPlayback.videoPreview.{fileId,transcriptUrl}}

  ---
  - **operationName:** getAlbum
  - **sha256Hash:** b9bfabef66ed756e5e13f68a942deb60bd4125ec1f1be8cc42769dc0259b4b10
  - **variablesExample:** {"uri":"spotify:album:3UUFDXb07kihCozeRLTe5y","locale":"en","offset":0,"limit":50}
  - **count:** 14
  - **responseShape:** albumUnion.{name,label,courtesyLine,date.{isoString,precision},saved,isPreRelease,preReleaseEndDateTime, coverArt.{sources[],extractedColors.{colorDark,colorLight,colorRaw}.hex}, artists.items[].{id,uri,profile.name,sharingInfo.shareUrl}, copyright.items[].{text,type}, discs.items[].{number,tracks.totalCount}, tracksV2.items[].{uid,track.{uri,name,trackNumber,discNumber,playcount,saved,relinkingInformation}}, playability.{playable,reason}, sharingInfo.{shareId,shareUrl}}

  ---
  - **operationName:** home
  - **sha256Hash:** 5366cbf1f73f8c813dd0f1addc6934950f0dd529cec907107c85851e645c2d16
  - **variablesExample:** {"homeEndUserIntegration":"INTEGRATION_DESKTOP","timeZone":"Europe/Amsterdam","sp_t":"","facet":"music-chip","sectionItemsLimit":10,"includeEpisodeContentRatingsV2":true}
  - **count:** 11
  - **responseShape:** home.{greeting.{transformedLabel,translatedBaseText}, homeChips[].{id,label.{transformedLabel,translatedBaseText},highlightColor,highlightScheme,subChips[].id}, sectionContainer.{uri,sections.{totalCount,items[].uri}}}. Observed facet values: "", "music-chip", "music-following-chip", "podcasts-chip". ALL 200 OK.

  ---
  - **operationName:** home
  - **sha256Hash:** 9052ac65ff42aefe6d39c45c184d9144cf8dbcc233ea1a76f8649264ad3e7896
  - **variablesExample:** {"homeEndUserIntegration":"INTEGRATION_DESKTOP","timeZone":"Europe/Amsterdam","sp_t":"","facet":"","sectionItemsLimit":10,"includeEpisodeContentRatingsV2":true}
  - **count:** 1
  - **responseShape:** identical top-level shape to 5366cbf1 (greeting/homeChips/sectionContainer). This is the hash Wavee hardcodes; it returned 200 OK here, so it is NOT dead — 1 sample.

  ---
  - **operationName:** queryArtistOverview
  - **sha256Hash:** ae0e2958a4ab645b35ca19ac04d0495ae12d9c5d7b7286217674801a9aab281a
  - **variablesExample:** {"uri":"spotify:artist:6KImCVD70vtIoJWnq6nGn3","locale":"","preReleaseV2":true}
  - **count:** 9
  - **responseShape:** artistUnion.{id, profile, headerImage.data.sources[].{url,maxWidth,maxHeight}, onPlatformReputationTrait.verification.isRegistered, discography.{latest.{id,uri,name,type,label,date.{year,month,day,precision},tracks.totalCount,playability,sharingInfo}, popularReleasesAlbums.{totalCount,items[].{id,uri,name,type,label}}, topTracks.items[].uid, albums/singles/compilations.totalCount}, goods.{merch.items[].{uri,url,nameV2,description,price}, concerts}}

  ---
  - **operationName:** queryAlbumMerch
  - **sha256Hash:** 3ef44ed6f17be67299538fe77faffab4075aeaf9e1085f10fc835592266711b5
  - **variablesExample:** {"uri":"spotify:album:6yWUYijJvHyjvcw43abyWD"}
  - **count:** 8
  - **responseShape:** albumUnion.{name, merch.{totalCount, items[].{uri,url,nameV2,description,price}}}

  ---
  - **operationName:** getDynamicColorsByUris
  - **sha256Hash:** f0f112945d6d745bd8ff790317bbf8d310036da75df33130490e9d6dc96c59d9
  - **variablesExample:** {"imageUris":["spotify:image:ab67616d0000aa54e86f30ec6f14a30f1cf9bb9d"]}
  - **count:** 7
  - **responseShape:** dynamicColors[].{bestFit, dark|light .{encoreBaseSetTextColor.{red,green,blue,alpha}, highContrast|higherContrast .{backgroundBase, backgroundTintedBase, textBase, textSubdued, textBrightAccent} each {red,green,blue,alpha}}}. NOTE: takes spotify:image: URIs (not https URLs like fetchExtractedColors) and returns a full accessibility-graded color SET, not one hex.

  ---
  - **operationName:** recentSearches
  - **sha256Hash:** 2520a5aa49f29d20cd3187261b8778fdd55654200801df9768f8a5decf05330b
  - **variablesExample:** {"limit":50,"includeAuthors":true,"includeEpisodeContentRatingsV2":true}
  - **count:** 6
  - **responseShape:** 200 OK; data payload was empty in all 6 samples (no recent-search entries for this user)

  ---
  - **operationName:** queryWhatsNewFeed
  - **sha256Hash:** d889c8c936ab192af8ced595427f5ba2acdf63478fdc0a181c8d477f8322630e
  - **variablesExample:** {"offset":0,"limit":50,"onlyUnPlayedItems":false,"includedContentTypes":["ALBUM"],"includeEpisodeContentRatingsV2":true}
  - **count:** 6
  - **responseShape:** whatsNewFeedItems.{totalCount, pagingInfo.{offset,limit,nextOffset}, items[].{id, timestamp.isoString, state.{state,timestamp.isoString}, content.data.{uri,id,name,type,albumType,description,contents,contentRatingsV2,gatedEntityRelations}}}. Observed includedContentTypes values: [], ["ALBUM"], ["EPISODE"].

  ---
  - **operationName:** lookupChildEntities
  - **sha256Hash:** 91ce02e32b19123de231dc8de91fe4b9ab84eca087d4c015549308d77fbb6d10
  - **variablesExample:** {"uris":["spotify:track:75InM94w13mJcj0wCpyaTn","spotify:track:3aQz0z86zrKjd1mcZlonxE","spotify:track:3FhNJaCNypOAnZccdYGAWN"]}
  - **count:** 5
  - **responseShape:** lookupEntities[].{uri, visualIdentityTrait.__typename}

  ---
  - **operationName:** searchFullEpisodes
  - **sha256Hash:** d54e35fafe7520cb53883b86d012911cbad75c14ac079a917951c24cdb07c60f
  - **variablesExample:** {"searchTerm":"wasa","offset":0,"limit":30,"includeEpisodeContentRatingsV2":true}
  - **count:** 5
  - **responseShape:** 200 OK; data was empty for both query terms sampled (no episode matches). NOTE the distinct variable shape — searchTerm/offset/limit only, no includePreReleases/includeAudiobooks block.

  ---
  - **operationName:** trackPreview
  - **sha256Hash:** fc26ffc7a1a4f93bd4c2d705649f7dba1de34005b3dc2915549847a9959405d8
  - **variablesExample:** {"uris":["spotify:track:6PYyVPaD3pzSbrdQKEDHm6","spotify:track:4WFfPxJv1KRekG6mxn837K","spotify:track:1yANFRps7jfQMe1dfP3sI9"]}
  - **count:** 5
  - **responseShape:** lookup[].data.uri (batch of up to ~20 track URIs per call; called alongside playlistextender recommendations)

  ---
  - **operationName:** searchTopResultsList
  - **sha256Hash:** 63a93cc04f6d8dea84a85de315e43f396a76cb681500de9ac5ccf5fc618c84cb
  - **variablesExample:** {"query":"mama im a criminal","limit":50,"offset":0,"numberOfTopResults":50,"includeArtistHasConcertsField":false,"includeAudiobooks":true,"includeAuthors":true,"includePreReleases":true,"includeAlbumPreReleases":true,"includeEpisodeContentRatingsV2":true,"isPrefix":null,"sectionFilters":["GENERIC","VIDEO_CONTENT"]}
  - **count:** 3
  - **responseShape:** searchV2.{query, chipOrder.items[].typeName, topResultsV2.itemsV2[].{item.__typename,matchedFields[]}, and totalCount for tracksV2/albumsV2/artists/playlists/podcasts/episodes/audiobooks/authors/users/genres}

  ---
  - **operationName:** searchTracks
  - **sha256Hash:** 59ee4a659c32e9ad894a71308207594a65ba67bb6b632b183abe97303a51fa55
  - **variablesExample:** {"includePreReleases":false,"includeAlbumPreReleases":true,"numberOfTopResults":20,"searchTerm":"wasa","offset":0,"limit":20,"includeAudiobooks":true,"includeAuthors":true,"includeEpisodeContentRatingsV2":true}
  - **count:** 2
  - **responseShape:** searchV2.{query, tracksV2.{totalCount, pagingInfo.{limit,nextOffset}, items[].{item.__typename, matchedFields[]}}}

  ---
  - **operationName:** searchAlbums
  - **sha256Hash:** 64ae1fe6df380b038c0a65a2606d3361bc270de6870b2fdc99cf0848b1efa6d3
  - **variablesExample:** {"includePreReleases":false,"includeAlbumPreReleases":true,"numberOfTopResults":20,"searchTerm":"wasa","offset":0,"limit":30,"includeAudiobooks":true,"includeAuthors":true,"includeEpisodeContentRatingsV2":true}
  - **count:** 2
  - **responseShape:** searchV2.{query, albumsV2.{totalCount, pagingInfo.{limit,nextOffset}, items[].data.{uri,name,type,isAlbumPreRelease,preReleaseEndDateTime}}}. Returned 200 OK — this hash is live, not dead.

  ---
  - **operationName:** searchArtists
  - **sha256Hash:** 270905851ba5c7faca81cfe053c2dbd8ceb4f156a0e0ef4b385af75ab69ffd13
  - **variablesExample:** {"includePreReleases":false,"includeAlbumPreReleases":true,"numberOfTopResults":20,"searchTerm":"wasa","offset":0,"limit":30,"includeAudiobooks":true,"includeAuthors":true,"includeEpisodeContentRatingsV2":true}
  - **count:** 2
  - **responseShape:** searchV2.{query, artists.{totalCount, pagingInfo.{limit,nextOffset}, items[].data.uri}}

  ---
  - **operationName:** searchPodcasts
  - **sha256Hash:** 0195d9f61b43606d490bca64c3456e3593528cea6cc05c7e822c7c42beed0f4e
  - **variablesExample:** {"includePreReleases":false,"includeAlbumPreReleases":true,"numberOfTopResults":20,"searchTerm":"wasa","offset":0,"limit":30,"includeAudiobooks":true,"includeAuthors":true,"includeEpisodeContentRatingsV2":true}
  - **count:** 2
  - **responseShape:** searchV2.{query, podcasts.{totalCount, pagingInfo.{limit,nextOffset}, items[].data.{uri,name,mediaType}}}

  ---
  - **operationName:** searchAuthors
  - **sha256Hash:** 4a9d403a7cbc7e19da5520d619a865472b35382b043bfa458154e73a5c6f46bd
  - **variablesExample:** {"includePreReleases":false,"includeAlbumPreReleases":true,"numberOfTopResults":20,"searchTerm":"wasa","offset":0,"limit":30,"includeAudiobooks":true,"includeAuthors":true,"includeEpisodeContentRatingsV2":true}
  - **count:** 2
  - **responseShape:** searchV2.{query, authors.{totalCount, pagingInfo.{limit,nextOffset}, items[].data.{uri,name,biography,saved,visualIdentity}}}

  ---
  - **operationName:** queryArtistDiscographyAll
  - **sha256Hash:** 5e07d323febb57b4a56a42abbf781490e58764aa45feb6e3dc0591564fc56599
  - **variablesExample:** {"uri":"spotify:artist:1McMsnEElThX1knmY4oliG","offset":20,"limit":20,"order":"DATE_DESC"}
  - **count:** 2
  - **responseShape:** artistUnion.discography.all.totalCount (+ paged items). NOTE: this hash also hosts queryArtistDiscographyOverview — one sha256Hash, two operationNames.

  ---
  - **operationName:** saveRecentSearches
  - **sha256Hash:** 2520a5aa49f29d20cd3187261b8778fdd55654200801df9768f8a5decf05330b
  - **variablesExample:** {"uris":["spotify:show:43lKIk7Tt69S4tdCpHWCnH"]}
  - **count:** 2
  - **responseShape:** saveToRecentSearches.revisionId. Shares its hash with recentSearches (query+mutation on one document).

  ---
  - **operationName:** SetItemsStateInWhatsNewFeed
  - **sha256Hash:** d889c8c936ab192af8ced595427f5ba2acdf63478fdc0a181c8d477f8322630e
  - **variablesExample:** {"items":{"items":[{"id":"podcast_release:b7fab90e1e1616d966de6b0d5293d2d20e384d4d4dae87f0459379ee544a5859","state":"SEEN"},{"id":"music_release:2f582cca6c310d6823597d82bc96b76a5c526d643ec407131166e5556849723b","state":"SEEN"}]}}
  - **count:** 2
  - **responseShape:** setItemsStateInWhatsNewFeed.{totalCount, items[].{id, state.{state, timestamp.isoString}}}. Shares its hash with queryWhatsNewFeed.

  ---
  - **operationName:** getTrack
  - **sha256Hash:** 612585ae06ba435ad26369870deaae23b5c8800a256cd8a57e08eddc25a37294
  - **variablesExample:** {"uri":"spotify:track:7LFwi4RolcCPnVEXXXVfQP"}
  - **count:** 2
  - **responseShape:** trackUnion.{id,uri,name,mediaType,playcount,saved,duration.totalMilliseconds,contentRating.label,playability.{playable,reason},firstArtist.items[],otherArtists.items[],albumOfTrack.{uri,id,name,type,date,coverArt.sources[],copyright.items[],courtesyLine,tracks.totalCount,sharingInfo},associationsV3.{audioAssociations,videoAssociations.totalCount},sharingInfo}

  ---
  - **operationName:** searchPlaylists
  - **sha256Hash:** af1730623dc1248b75a61a18bad1f47f1fc7eff802fb0676683de88815c958d8
  - **variablesExample:** {"includePreReleases":false,"includeAlbumPreReleases":true,"numberOfTopResults":20,"searchTerm":"wasa","offset":0,"limit":30,"includeAudiobooks":true,"includeAuthors":true,"includeEpisodeContentRatingsV2":true}
  - **count:** 1
  - **responseShape:** 200 OK; data empty for the one sampled term (1 sample)

  ---
  - **operationName:** searchUsers
  - **sha256Hash:** d3f7547835dc86a4fdf3997e0f79314e7580eaf4aaf2f4cb1e71e189c5dfcb1f
  - **variablesExample:** {"includePreReleases":false,"includeAlbumPreReleases":true,"numberOfTopResults":20,"searchTerm":"wasa","offset":0,"limit":30,"includeAudiobooks":true,"includeAuthors":true,"includeEpisodeContentRatingsV2":true}
  - **count:** 1
  - **responseShape:** searchV2.{query, users.{totalCount, pagingInfo.{limit,nextOffset}, items[].data.{uri,id,username,displayName}}} (1 sample)

  ---
  - **operationName:** searchAudiobooks
  - **sha256Hash:** e05ac765d02c084f8783d3c1572b23d57761c43f47eb8b87ce2f9ccced3fa068
  - **variablesExample:** {"includePreReleases":true,"includeAlbumPreReleases":true,"numberOfTopResults":20,"searchTerm":"wasa","offset":0,"limit":30,"includeAudiobooks":true,"includeAuthors":true,"includeEpisodeContentRatingsV2":true}
  - **count:** 1
  - **responseShape:** 200 OK; data empty (1 sample). NOTE includePreReleases is TRUE here, unlike every other search* op which sends false.

  ---
  - **operationName:** queryArtistDiscographyOverview
  - **sha256Hash:** 5e07d323febb57b4a56a42abbf781490e58764aa45feb6e3dc0591564fc56599
  - **variablesExample:** {"uri":"spotify:artist:1McMsnEElThX1knmY4oliG"}
  - **count:** 1
  - **responseShape:** artistUnion.{id,uri,profile.name,discography.{all.totalCount,albums.totalCount,singles.totalCount,compilations.totalCount}} (1 sample; same hash as queryArtistDiscographyAll)

  ---
  - **operationName:** getCommentsForEntity
  - **sha256Hash:** bba34fe5f2da3aaa25ab5c90eef1fe2036d325bf32e791ae462b637665185d83
  - **variablesExample:** {"uri":"spotify:episode:2xHFjw5aIzfi1aAcnusmEp","token":null}
  - **count:** 1
  - **responseShape:** comments[].{entityUri, eligibilityStatus, totalCount, nextPageToken, items[].{uri, commentString, author.__typename, createDate.{isoString,precision}, isPinned, isSensitive, isPendingReview, numberOfRepliesWithThreads, hasUserReachedReplyLimit, replies[], coverImagesReacted[], coverImagesReplied[], reactionsMetadata.{numberOfReactions, usersReactionUnicode, highlightedReactions[]}}} (1 sample)

  ---
  - **operationName:** similarAlbumsBasedOnThisTrack
  - **sha256Hash:** 1d1f93a737498adca2c892c73af87fc0b052afe4e1a33c989540c32413dfae17
  - **variablesExample:** {"uri":"spotify:track:7LFwi4RolcCPnVEXXXVfQP","limit":24,"albumsOnly":true}
  - **count:** 1
  - **responseShape:** seoRecommendedTrackAlbum.{totalCount, items[].data.{uri,name,type,date.{year,isoString,precision},playability.playable,sharingInfo.{shareId,shareUrl}}} (1 sample)

  ---
  - **operationName:** playlistSection
  - **sha256Hash:** 2615df403a9043c1d7d3094fbeb4c9653b07b11a33d8081fbd31f0f7959ff4a1
  - **variablesExample:** {"sectionUri":"spotify:section:0JQ5DAob0LgAOAm50K90Od","playlistUri":"spotify:playlist:37i9dQZF1E8RrQBpL2fW7p"}
  - **count:** 1
  - **responseShape:** homeSections.sections[].__typename (1 sample). Resolves a spotify:section: URI in the context of a playlist — the playlist-page 'recommended' shelf.

  ---
  - **operationName:** browseAll
  - **sha256Hash:** dbd8b55e09a58afc52eab438bc228ba28fd72ac2f2148c6c26354980e4579001
  - **variablesExample:** {"pagePagination":{"offset":0,"limit":10},"sectionPagination":{"offset":0,"limit":99},"browseEndUserIntegration":"INTEGRATION_DESKTOP"}
  - **count:** 1
  - **responseShape:** browseStart.{uri, sections.items[].{uri, data.__typename}} (1 sample) — the Browse landing grid of spotify:page: URIs

  ---
  - **operationName:** browsePage
  - **sha256Hash:** f5c4e6d668f5716464a231c1cc8b22c1cbf6ad68b09929fd7de813a30581298b
  - **variablesExample:** {"pagePagination":{"offset":0,"limit":10},"sectionPagination":{"offset":0,"limit":10},"uri":"spotify:page:0JQ5DAqbMKFSi39LMRT0Cy","browseEndUserIntegration":"INTEGRATION_DESKTOP","includeEpisodeContentRatingsV2":true}
  - **count:** 1
  - **responseShape:** browse.{uri, header.{title.transformedLabel, subtitle, backgroundImage, color.hex}, sections.{totalCount, pagingInfo.nextOffset, items[].{uri, targetLocation, sectionItems.totalCount, data.{__typename,subtitle}}}} (1 sample)

**endpoints:**

  ---
  - **method:** POST
  - **url:** https://gew4-spclient.spotify.com/extended-metadata/v0/extended-metadata
  - **count:** 1093
  - **purpose:** The single biggest surface. Batched entity metadata + traits. All 1093 returned 200 OK. 45,896 entity requests total; batch size min 1 / median 5 / p95 299 / max 1425 entities per POST.
  - **bodyShape:** Request header f1={f1:"NL", f2:"premium"} in every single request (no task_id observed). 51 distinct kinds requested. Kind->type_url observed on 200s: 3 PodcastTopics, 4 podcast_segments.PodcastSegments, 5 audiofiles.AudioFilesExtensionResponse, 6 descriptorextension.ExtensionDescriptorData, 8 metadata.Artist, 9 metadata.Album, 10 metadata.Track, 11 metadata.Show, 12 metadata.Episode, 15 identity.v3.UserProfile, 16 canvaz.cache.EntityCanvazResponse.Canvaz, 21 corex.transcripts.metadata.EpisodeTranscript, 28 automix.proto.Cuepoints, 37 ratings.PodcastRating, 54 podcast.extensions.PodcastHtmlDescription, 78 bumblebee.playability.v1.Playability, 80 traits.v1.ShareTrait, 83 audiobookgenres.AudiobookGenres, 85 bumblebee.originalvideo.v1.OriginalVideo, 86 smartshuffle.SmartShuffle, 98 bumblebee.audio_associations.v1.AudioAssociations, 99 bumblebee.video_associations.v1.VideoAssociations, 114 watchfeedextensions.api.v1.EntityExplorerEntrypointResponse, 142 playlist.tuner.extension.ListTunerAudioAnalysis, 149 traits.v1.RootlistabilityTrait, 151 artistsectionprovider.v1.RecommendedPlaylists, 164 gatedentityrelations.v1.GatedEntityRelations, 170 autolensextension.v1.AutoLens, 178/179/182/183/185/220/239/246/249 contentagnostic.v2.{Identity,VisualIdentity,ConsumptionExperience,PublishingMetadata,OnPlatformReputation,EntityType,ContentCapability,CurationExperience,ContentExperience}Trait, 205 list.v1.model.Attributes, 212 contentagnostic.v2.PlaybackTrait, 217/218/219/222/225 playlistmixing.extensions.{mixbeats.Beats,mixvocalactivity.VocalActivity,mixability.Mixability,audio_attributes.v2.AudioAttributes,mixstate.MixState}

  ---
  - **method:** GET
  - **url:** https://gew4-spclient.spotify.com/playlist-permission/v1/playlist/{id}/permission/base
  - **count:** 783
  - **purpose:** Per-playlist permission probe, fired once per playlist the client touches (783 calls — by far the noisiest single-entity endpoint). Also exists as /playlist-permission/v1/show/{id}/... and /playlist-permission/v1/list/whats-new/podcasts/...
  - **bodyShape:** protobuf response; body is essentially the single string 'default'

  ---
  - **method:** GET
  - **url:** https://gew4-spclient.spotify.com/playlist/v2/playlist/{id}/diff?revision={rev}&handlesContent=
  - **count:** 481
  - **purpose:** Incremental playlist sync. revision is the '{decimal},{48-hex}' pair from the previous fetch.
  - **bodyShape:** protobuf SelectedListContent: f1=revision bytes, f2=length, f3=attributes{f1 name, f11 format, f12 repeated {key,value} format_attributes, f13 repeated picture{size,url}}, f5=contents{f3 repeated item{f1 uri, f2 meta}}, f15=timestamp ms, f16=owner, f18=capabilities, f22=transition/automix descriptors, f23=flag

  ---
  - **method:** POST
  - **url:** https://gew4-spclient.spotify.com/gabo-receiver-service/v3/events
  - **count:** 251
  - **purpose:** Telemetry event batch upload (also seen at /public/v3/events and on spclient.wg host). Pure analytics — nothing Wavee needs.
  - **bodyShape:** binary event batch

  ---
  - **method:** POST
  - **url:** https://api-partner.spotify.com/pathfinder/v2/query
  - **count:** 177
  - **purpose:** All GraphQL persisted queries — 33 distinct operationNames (see operations). 8 additional OPTIONS preflights. Some requests carry an ARRAY of operations in one POST body.
  - **bodyShape:** {"variables":{...},"operationName":"NAME","extensions":{"persistedQuery":{"version":1,"sha256Hash":"..."}}} — or a JSON array of such objects

  ---
  - **method:** GET
  - **url:** https://gew4-spclient.spotify.com/playlist/v2/playlist/{id}
  - **count:** 146
  - **purpose:** Full playlist snapshot (non-diff). Sibling forms: /playlist/v2/show/{id}, /playlist/v2/user/{user}/rootlist, /playlist/v2/list/recents/main/diff, /playlist/v2/list/whats-new/podcasts, /playlist/v2/list/podcast-chapters/{uri}, /playlist/v2/list/popular-release-segments-main-roles/artist_{id}
  - **bodyShape:** same SelectedListContent protobuf as the diff endpoint

  ---
  - **method:** GET
  - **url:** https://audio-cf.spotifycdn.com/audio/{hex}?verify=...
  - **count:** 126
  - **purpose:** Encrypted audio file fetch (also audio-ak.spotifycdn.com and audio-fa.scdn.co). Range-requested.
  - **bodyShape:** binary

  ---
  - **method:** PUT
  - **url:** https://gew4-spclient.spotify.com/connect-state/v1/devices/{deviceId}
  - **count:** 82
  - **purpose:** Connect device-state publish (PutState).
  - **bodyShape:** protobuf PutStateRequest

  ---
  - **method:** GET
  - **url:** https://heads-fa-tls13.spotifycdn.com/head/{hex}
  - **count:** 75
  - **purpose:** Audio-file header prefetch (first bytes of an encrypted file, used to start decode before the full range lands).
  - **bodyShape:** binary

  ---
  - **method:** GET
  - **url:** https://gew4-spclient.spotify.com/storage-resolve/files/audio/interactive/{fileId}
  - **count:** 74
  - **purpose:** Resolve a fileId to CDN URLs. A v2 form also exists: /storage-resolve/v2/files/audio/interactive/{n}/{fileId}?product=0
  - **bodyShape:** protobuf: f1=result enum(0), f2=repeated cdnurl string (audio-fa.scdn.co / audio-cf.spotifycdn.com / audio-ak.spotifycdn.com, each with its own token scheme), f4=fileid, f5=ttl seconds (86400)

  ---
  - **method:** GET
  - **url:** https://image-cdn-fa.spotifycdn.com/image/{hex}
  - **count:** 110
  - **purpose:** Cover/avatar image fetch (also image-cdn-ak, i.scdn.co, pickasso.spotifycdn.com for generated mix art, seed-mix-image.spotifycdn.com for mood-descriptor art, daylist.spotifycdn.com).
  - **bodyShape:** jpeg/webp

  ---
  - **method:** POST
  - **url:** https://gew4-spclient.spotify.com/connect-state/v1/player/command/from/{deviceId}/to/{deviceId}
  - **count:** 55
  - **purpose:** Connect remote command dispatch (play/pause/seek/skip/set_options).
  - **bodyShape:** JSON command envelope

  ---
  - **method:** GET
  - **url:** https://gew4-spclient.spotify.com/popcount/v2/playlist/{id}/count
  - **count:** 54
  - **purpose:** Playlist follower count. Wavee does NOT implement this.
  - **bodyShape:** protobuf: f1=0, f2=1, f7=count varint (e.g. 128345311), f8=1

  ---
  - **method:** GET
  - **url:** https://spclient.wg.spotify.com/metadata/{n}/track/{hex}?market=from_token
  - **count:** 83
  - **purpose:** Legacy single-entity metadata (v4 track). Mostly OPTIONS preflights + GETs.
  - **bodyShape:** protobuf spotify.metadata.Track

  ---
  - **method:** POST
  - **url:** https://gew4-spclient.spotify.com/collection/v2/delta
  - **count:** 42
  - **purpose:** Incremental library (collection) sync since a timestamp.
  - **bodyShape:** REQUEST protobuf: f1=username, f2=set name ("collection"), f3=last sync timestamp ms as STRING. RESPONSE: f1=count, f2=repeated{f1 uri, f2 added_at seconds}, f3=new sync token string

  ---
  - **method:** POST
  - **url:** https://gew4-spclient.spotify.com/herodotus/spotify.resumption.v1.ResumePointRevisionService/CreateResumePointRevision
  - **count:** 40
  - **purpose:** Resume-point (playback position) write. Siblings: BatchCreateResumePointRevisions, ListResumePointRevisions, and spotify.resumption.v1.CurrentStateService/ListCurrentStates.
  - **bodyShape:** gRPC-framed protobuf. ListResumePointRevisions request = {f2:"spotify:list:play-history:v1", f3:500}. Response items = {f1 uuid string, f2 {f1 list uri, f2 track uri}, f3 {timestamp,position}, f4 {timestamp,duration}}

  ---
  - **method:** GET
  - **url:** https://gew4-spclient.spotify.com/context-resolve/v1/{uri}
  - **count:** 37
  - **purpose:** Resolve any context URI (playlist/album/station/list) to its playable page of tracks WITH metadata. Handles spotify:station:* and spotify:list:popular-release-segments-main-roles:artist_{id} too.
  - **bodyShape:** JSON: {metadata:{context_description, context_long_description, image_url, header_image_url_desktop, primary_color, context_owner, playlist.revision, uri, is_video_first, isAlgotorial, format_list_type, episode_description, recs.hasArtists (comma-joined artist URIs), moveFollowersJobId, correlation-id, status}, pages:[{tracks:[{uri, uid, metadata:{added_at, added_by_username, highlight_id, decision_id}}]}]}

  ---
  - **method:** GET
  - **url:** https://spclient.wg.spotify.com/clip-transcript/v1/transcripts/{episodeUri}?offsets.start=0.000s&offsets.end=60.000s
  - **count:** 32
  - **purpose:** Word-level podcast clip transcript with speaker diarization — powers the animated preview-clip captions. Wavee does NOT implement this.
  - **bodyShape:** JSON: {words:[{word, offsets:{start:"0.160s", end:"0.440s"}, speakerId:"1"}, ...]}

  ---
  - **method:** POST
  - **url:** https://gew4-spclient.spotify.com/playplay/v1/key/{fileId}
  - **count:** 32
  - **purpose:** PlayPlay audio key derivation request.
  - **bodyShape:** protobuf

  ---
  - **method:** POST
  - **url:** https://gew4-spclient.spotify.com/melody/v1/msg/batch
  - **count:** 27
  - **purpose:** Playback-stats telemetry batch (jssdk_playback_stats). Also GET /melody/v1/time for clock sync.
  - **bodyShape:** JSON {messages:[{type:"jssdk_playback_stats", message:{play_track:"spotify:canvas:...", file_id, playback_id, internal_play_id, memory_cached, persistent_cached, audio_format, video_format, ...}}]}

  ---
  - **method:** GET
  - **url:** https://pickasso.spotifycdn.com/image/ab67c0de0000deef/dt/v1/img/radio|artistmix|daily|topic|thisisv3|dw|release-radar-v4/{id}/{locale}
  - **count:** 50
  - **purpose:** Server-generated mix/radio cover art. The path template encodes the mix TYPE and the seed entity id plus a locale suffix — reproducible client-side without an API call.
  - **bodyShape:** jpeg

  ---
  - **method:** GET
  - **url:** https://gew4-spclient.spotify.com/manifests/v9/json/sources/{fileId}/options/supports_drm
  - **count:** 19
  - **purpose:** Video/canvas manifest: the adaptive profile ladder for a video source.
  - **bodyShape:** JSON {contents:[{encoding_id, segment_length, start_time_millis, end_time_millis, profiles:[{id, file_type:"mp4", mime_type:"video/mp4", max_bitrate, video_bitrate, video_codec:"avc1.4d400d", video_width, video_height, video_resolution}]}]}

  ---
  - **method:** GET
  - **url:** https://gew4-spclient.spotify.com/color-lyrics/v2/track/{id}
  - **count:** 14
  - **purpose:** Synced lyrics with per-line colors.
  - **bodyShape:** JSON

  ---
  - **method:** POST
  - **url:** https://spclient.wg.spotify.com/playlistextender/extendp/
  - **count:** 13
  - **purpose:** 'Enhance / add recommended tracks' at the bottom of a user playlist. Returns FULLY-HYDRATED track objects, no extended-metadata round trip needed. Wavee references this path but the response fields below are worth checking against.
  - **bodyShape:** REQUEST {"playlistURI":"spotify:playlist:{id}","trackSkipIDs":[],"numResults":25}. RESPONSE {recommendedTracks:[{id, originalId, name, duration, explicit, popularity, score (float), contentRating:[], artists:[{id,name}], album:{id,name,imageUrl,largeImageUrl}}]}

  ---
  - **method:** PUT
  - **url:** https://gew4-spclient.spotify.com/connect-state/v1/connect/volume/from/{deviceId}/to/{deviceId}
  - **count:** 12
  - **purpose:** Connect remote volume set.
  - **bodyShape:** protobuf

  ---
  - **method:** GET
  - **url:** https://apresolve.spotify.com/?type=dealer&type=spclient&type=accesspoint
  - **count:** 12
  - **purpose:** Endpoint discovery for dealer/spclient/accesspoint hosts.
  - **bodyShape:** JSON host lists

  ---
  - **method:** GET
  - **url:** https://aet.spotify.com/v2/t?p={token}
  - **count:** 8
  - **purpose:** Ad viewability/impression beacon.
  - **bodyShape:** opaque token in query

  ---
  - **method:** GET
  - **url:** https://gew4-spclient.spotify.com/quicksilver/v2/messages?ctv_type=web-modal&trigger={uri}&action=DISMISS&action=URL&action=EXTERNAL_URL&locale=en&trig_type=URI
  - **count:** 5
  - **purpose:** In-app promo/modal message fetch, driven by a URI trigger. Returned {} (no message) in every sample.
  - **bodyShape:** JSON {} when nothing to show

  ---
  - **method:** GET
  - **url:** https://gew4-spclient.spotify.com/quicksilver/v2/triggers?trig_type=URI&trig_type=CLIENT_EVENT&ctv_type=web-modal
  - **count:** 2
  - **purpose:** Fetches the LIST of URI patterns that should trigger a quicksilver/messages call — a client-side routing table, so the client only calls /messages on matching navigations.
  - **bodyShape:** JSON [{"type":"URI","pattern":"spotify:artist:?","format":"web-modal","cache":false},{"type":"URI","pattern":"spotify:home",...},{"type":"CLIENT_EVENT","pattern":"app:update:eol",...}] — patterns seen: spotify:artist:?, spotify:home, spotify:search, spotify:collection, spotify:collections, spotify:album:?, spotify:open, plus CLIENT_EVENT app:update:eol

  ---
  - **method:** POST
  - **url:** https://gew4-spclient.spotify.com/context-resolve/v1/autoplay
  - **count:** 5
  - **purpose:** Autoplay continuation: given a context and the tracks already played, returns the next page of tracks. This is the queue-never-ends mechanism.
  - **bodyShape:** REQUEST protobuf: f1=context uri, f2=repeated recently-played track uri. RESPONSE JSON {pages:[{tracks:[{uri, uid, metadata:{decision_id:"ssp~..."}}]}]}

  ---
  - **method:** POST
  - **url:** https://gew4-spclient.spotify.com/playready-license/v1/video/license
  - **count:** 5
  - **purpose:** PlayReady DRM license acquisition for protected video.
  - **bodyShape:** binary challenge/response

  ---
  - **method:** GET
  - **url:** https://gew4-spclient.spotify.com/radio-apollo/v3/tracks/{stationUri}?salt={n}&autoplay=true&count=50&isVideo=false&prev_tracks={csv}&pageNum={n}
  - **count:** 2
  - **purpose:** Station/radio track generation with explicit paging. prev_tracks is a comma-separated base62 id list (not URIs) that grows each page.
  - **bodyShape:** JSON {next_page_url:"hm://radio-router/v3/tracks/...&pageNum=3&minimal=true", correlation_id:"ssp~...", tracks:[{uid, metadata:{decision_id}}]}

  ---
  - **method:** POST
  - **url:** https://gew4-spclient.spotify.com/playlist/v2/playlist/{id}/signals
  - **count:** 2
  - **purpose:** Sets a playlist-level 'signal' (the DJ/Mix lens toggle). Returns the full updated SelectedListContent so the client re-renders in one round trip.
  - **bodyShape:** REQUEST protobuf: f1=24-byte revision, f2={f1:"mix-state", f2:{f1:"mix"}, f3:{f1:1}}. RESPONSE = SelectedListContent whose f3 attributes now contain automix.queue=true, mix=true, mix-type=allow-custom, automix.mode=auto, automix.autoplay=true, automix.ignore_setting=true, has-custom-transitions=false, can-view-transition=true; f5 gains a signal record {f1:"mix-state", f2:{f1:"mix"}}; f22 gains a second transition descriptor {f1:"mix", f2:"29b8de7e"}

  ---
  - **method:** POST
  - **url:** https://spclient.wg.spotify.com/assisted-curation/v1/recommendations/curation/uri
  - **count:** 1
  - **purpose:** Suggested SHOWS (audiobook/podcast) to add to a playlist — the sibling of playlistextender for non-music content. 1 sample.
  - **bodyShape:** REQUEST {"curation_uri":"spotify:playlist:{id}","suggested_audiobooks":{},"skip_item_uris":[],"limit":5}. RESPONSE {"uris":["spotify:show:2IK8MY7S2UlDSaBTHDM8Cy", ...]}

  ---
  - **method:** GET
  - **url:** https://spclient.wg.spotify.com/gander/v2/GetNotifications?locale=&limit=20
  - **count:** 4
  - **purpose:** In-app notification inbox. Siblings: GetUserHasUnreadNotification?postFix=a and POST ResetLatestCursor. Wavee does not implement the inbox UI.
  - **bodyShape:** JSON {notifications:[{id, createdTimestamp ISO, title, action:{uri:"spotify:concert:{id}", type:"NAVIGATE"}, entityImage.imageUrl, isNew, storageId:"{sortkey}#{id}", messagingMetadata:{opportunityId, messageId}}]}. GetUserHasUnreadNotification -> {"userHasUnreadNotification":false}

  ---
  - **method:** GET
  - **url:** https://gew4-spclient.spotify.com/recently-played/v3/recently-played?limit=50&filter=default,collection-new-episodes
  - **count:** 2
  - **purpose:** The 'Recently played' rail source. Wavee does NOT implement this — it is a single call that gives the whole rail.
  - **bodyShape:** protobuf, content-type vnd.spotify/collection-favorites: repeated f1={f1 context uri (playlist/album/artist/station/user-collection), f2 played_at ms, f3 last track uri}

  ---
  - **method:** GET
  - **url:** https://gew4-spclient.spotify.com/device-capabilities/v1/capabilities?device_type=computer&client_id={id}&device_model=...&client_version=...
  - **count:** 2
  - **purpose:** Server-declared client capabilities/entitlements at startup. Determines HiFi eligibility, DJ support, supported media types.
  - **bodyShape:** JSON {license:"tft", effective_license:"premium", supported_media_types:["audio/track","audio/episode","audio/dj","audio/media","audio/agnostic","audio/ad","audio/interruption"], supported_audio_quality:"HIFI", audio_quality:"HIFI_24", supports_hifi:{fully_supported,user_eligible,device_supported}, supports_dj:true, supports_observing:true, supports_external_episodes:true, supports_v2_playlist_uris:false, supports_playback_speed:false, ad_beacon_reporting:false, is_dynamic_device:false, is_voice_enabled:false, debug_client_type:"client-zelda"}

  ---
  - **method:** POST
  - **url:** https://gew4-spclient.spotify.com/playback-settings/spotify.playbacksettings.PlaybackSettingsService/GetAllStoredContentValues
  - **count:** 2
  - **purpose:** Per-context stored playback settings (shuffle/repeat/etc) for EVERY context the user has touched, returned in one gRPC call. Siblings: GetSettingsDeviceSelection, WriteContentValue.
  - **bodyShape:** REQUEST protobuf {f1: 1000}. RESPONSE gRPC: a long list of context URIs (spotify:album:*, spotify:track:*, spotify:playlist:*, spotify:artist:*, spotify:list:popular-release-segments-main-roles:artist_*) each with its stored value

  ---
  - **method:** POST
  - **url:** https://gew4-spclient.spotify.com/ads/v3/ads?slots=hpto
  - **count:** 6
  - **purpose:** Home-page-takeover ad slot fetch (still called on a premium account; returns a playlist-promo unit).
  - **bodyShape:** REQUEST {session_id, user_agent, request_id, pod:{context:1,session:1}, slots:["hpto"]}. RESPONSE {pod:{hpto:[{id, dummy:false, clickthrough:"https://open.spotify.com/playlist/{id}", tracking_events:{viewability:["https://aet.spotify.com/v2/t?p=..."]}}]}}

  ---
  - **method:** GET
  - **url:** https://gew4-spclient.spotify.com/user-profile-view/v3/profile/{username}?market=from_token
  - **count:** 5
  - **purpose:** Public user profile with their public playlists — used for playlist-owner chips and the profile page.
  - **bodyShape:** JSON {uri, name, image_url, followers_count, following_count, public_playlists:[{uri,name,image_url,followers_count,owner_name,owner_uri}]}

  ---
  - **method:** GET
  - **url:** https://gew4-spclient.spotify.com/playlist/v2/list/popular-release-segments-main-roles/artist_{artistId}
  - **count:** 8
  - **purpose:** The artist page's 'Popular' track list, served as a PLAYLIST (not GraphQL). Wavee already uses this. Also has a /diff form.
  - **bodyShape:** SelectedListContent protobuf: f3.f1="{Artist} Popular", f3.f11="popular-release-segments-main-roles", f3.f12 format_attributes include play_count=0, reporting.uri=spotify:artist:{id}, total_number_of_tracks=45; f5.f3 repeated items each {f1 track uri, f2 {f12 8-byte play-count blob}}

  ---
  - **method:** GET
  - **url:** https://subtitles.spotifycdn.com/subtitles/v1.1/sources/{fileId}/en-us.webvtt?__token__=exp=...~acl=...~hmac=...
  - **count:** 1
  - **purpose:** Video-podcast subtitles as WebVTT. 1 sample.
  - **bodyShape:** WEBVTT text with HH:MM:SS.mmm --> cue ranges

  ---
  - **method:** POST
  - **url:** https://gew4-spclient.spotify.com/offline/v1/devices/{deviceId}/cache/{cacheId}/disable
  - **count:** 2
  - **purpose:** Offline-cache lifecycle. Sibling: POST /offline/v1/devices/{deviceId}/cache/{cacheId}/resources:delta. Returns 204.
  - **bodyShape:** empty

  ---
  - **method:** POST
  - **url:** https://spclient.wg.spotify.com/listening-activity/v1/audience
  - **count:** 1
  - **purpose:** Friend-activity audience list. 1 sample. Sibling: POST /profile-privacy/v2/read-settings (protobuf req = username). Also GET /presence-view/v2/init-friend-feed/{base64-dealer-token} and GET /social-connect/v2/sessions/current?alt=protobuf (404 = no jam).
  - **bodyShape:** REQUEST {"unused":true}. RESPONSE {"users":[]}

  ---
  - **method:** POST
  - **url:** https://gew4-spclient.spotify.com/playlist-publish/v1/subscription/playlist/{id}
  - **count:** 3
  - **purpose:** Subscribe to a playlist's publish/update stream (fired when opening an editorial playlist). Empty 200.
  - **bodyShape:** empty request and response

  ---
  - **method:** PUT
  - **url:** https://gew4-spclient.spotify.com/clientsettings/api/v1/
  - **count:** 2
  - **purpose:** Pushes a client setting to the server. Observed setting the preferred locale.
  - **bodyShape:** protobuf {f1:"preferred-locale", f2:{f4:{f1:"en"}}}

  ---
  - **method:** GET
  - **url:** https://gew4-spclient.spotify.com/net-fortune/v2/fortune
  - **count:** 3
  - **purpose:** Network-quality/bandwidth hint token issued at startup.
  - **bodyShape:** protobuf {f1: uuid string, f2: 1400000}

  ---
  - **method:** GET
  - **url:** https://spclient.wg.spotify.com/library-import/v1/eligible
  - **count:** 2
  - **purpose:** Whether the 'import your library from another service' flow should be offered.
  - **bodyShape:** JSON {"eligible":false}

  ---
  - **method:** GET
  - **url:** https://gew4-spclient.spotify.com/socialgraph/v4/{username}/is-following?limit=1000
  - **count:** 2
  - **purpose:** Bulk following check for the current user. Empty body in both samples.
  - **bodyShape:** empty/protobuf

  ---
  - **method:** POST
  - **url:** https://gew4-spclient.spotify.com/connect-state/v1/cluster/wake-devices
  - **count:** 1
  - **purpose:** Wakes idle Connect devices in the cluster before showing the device picker. 1 sample, empty response.
  - **bodyShape:** empty

  ---
  - **method:** POST
  - **url:** https://gew4-spclient.spotify.com/capping-api/spotify.cappingapi.v1.CappingApi/PutConsumption
  - **count:** 2
  - **purpose:** Reports content consumption against a cap (podcast/audiobook entitlement metering).
  - **bodyShape:** gRPC protobuf

  ---
  - **method:** GET
  - **url:** https://spclient.wg.spotify.com/desktop-update/v2/update?client_version=1.2.94.583&ct=S
  - **count:** 2
  - **purpose:** Desktop auto-update check.
  - **bodyShape:** protobuf {f2: 14288}

  ---
  - **method:** POST
  - **url:** https://spclient.wg.spotify.com/podcast-ap4p/leavebehinds/ads
  - **count:** 4
  - **purpose:** Podcast 'leave-behind' companion-ad fetch, fired on episode playback.
  - **bodyShape:** JSON

  ---
  - **method:** POST
  - **url:** https://clienttoken.spotify.com/v1/clienttoken
  - **count:** 1
  - **purpose:** Client token issuance (bootstrap). Sibling: POST https://login5.spotify.com/v3/login and POST https://accounts.spotify.com/api/token.
  - **bodyShape:** protobuf

  ---
  - **method:** GET
  - **url:** https://raw.githubusercontent.com/amll-dev/amll-ttml-db/main/spotify-lyrics/{trackId}.ttml
  - **count:** 14
  - **purpose:** NOT Spotify — a third-party TTML lyrics database being polled by a Spicetify-style mod running in the captured client. Ignore for Wavee API surface.
  - **bodyShape:** TTML

**notable:**
  1. 12 Pathfinder operations in these captures have NO implementation in Wavee at all (grepped src/apps for both hash and operationName, zero hits): getDynamicColorsByUris, recentSearches, saveRecentSearches, lookupChildEntities, trackPreview, searchPodcasts, searchAuthors, searchUsers, searchAudiobooks, searchFullEpisodes, queryArtistDiscographyAll/Overview, browseAll, browsePage, playlistSection, getCommentsForEntity. browseAll+browsePage together are an entire missing top-level surface (the Browse/genre tab).
  2. CORRECTION to the prior 'stale hashes' finding: all three supposedly-stale hashes returned 200 OK in these captures. home 5366cbf1f73f8c813dd0f1addc6934950f0dd529cec907107c85851e645c2d16 (11 calls) and Wavee's 9052ac65ff42aefe6d39c45c184d9144cf8dbcc233ea1a76f8649264ad3e7896 (1 call) BOTH work and return the identical top-level shape {greeting, homeChips, sectionContainer}. searchAlbums 64ae1fe6df380b038c0a65a2606d3361bc270de6870b2fdc99cf0848b1efa6d3 returned 200 with full albumsV2 data. So these are live coexisting document versions, not dead hashes.
  3. queryArtistOverview is a real divergence: the client sends hash ae0e2958a4ab645b35ca19ac04d0495ae12d9c5d7b7286217674801a9aab281a with preReleaseV2:true; Wavee (src/apps/Wavee/SpotifyLive/PathfinderClient.cs:98) hardcodes 7f86ff63e38c24973a2842b672abe44c910c1973978dc8a4a0cb648edef34527 with preReleaseV2:false. The captured hash never appears in the repo. Wavee's variant is presumably an older document that still resolves, but the captured one is what the shipping client uses and it returns onPlatformReputationTrait.verification.isRegistered which Wavee's shape does not obviously carry.
  4. Same pattern on queryNpvArtist: the client overwhelmingly uses b2cedf7ed0f29c713567d97ed69b848c8387294edfe58a0e439a3a5669cc27bb (16 calls) which adds artistUnion.onPlatformReputationTrait; Wavee hardcodes 047c9c225967d41a763949a4db3f0493e901c9f8689a6537408aabf9beffc177 (1 call in capture, 200 OK). Both live; the newer one is a strict superset.
  5. One sha256Hash can host MULTIPLE operationNames and this happens three times here: 2520a5aa... = recentSearches + saveRecentSearches; d889c8c9... = queryWhatsNewFeed + SetItemsStateInWhatsNewFeed; 5e07d323... = queryArtistDiscographyAll + queryArtistDiscographyOverview. Any hash->operation map keyed on hash alone will be wrong.
  6. getDynamicColorsByUris (f0f112945d6d745bd8ff790317bbf8d310036da75df33130490e9d6dc96c59d9) is a materially better theming primitive than the fetchExtractedColors Wavee already uses. It takes spotify:image: URIs (NOT https URLs) and returns a complete pre-graded palette per image: dark and light variants, each with highContrast and higherContrast tiers containing backgroundBase, backgroundTintedBase, textBase, textSubdued, textBrightAccent as {red,green,blue,alpha}, plus encoreBaseSetTextColor and a bestFit selector. That removes all client-side contrast math from the Wavee palette-tint work.
  7. /playlist/v2/playlist/{id}/signals is the DJ/Mix lens control plane. POST {revision, {"mix-state",{"mix"},{1}}} flips the playlist into mix mode and the SAME response returns the fully-updated SelectedListContent (attributes gain automix.queue/mix/mix-type=allow-custom/automix.mode=auto/automix.autoplay/automix.ignore_setting/has-custom-transitions/can-view-transition, f22 gains a second transition descriptor {"mix", "29b8de7e"}). This is the exact request that causes the AUTO_LENS(170)=="mix" state and therefore the 217/218/219/222/225 mixbeats/vocalactivity/mixability/audio_attributes/mixstate extension family. Wavee already touches /signals in 7 files — worth verifying it sends this exact shape.
  8. The extended-metadata request header carries ONLY f1=country ("NL") and f2=catalogue ("premium") in all 1093 requests — no f3 task_id was ever sent. All 1093 returned HTTP 200 (per-entity status lives in the response, not the HTTP code).
  9. Batching is far more aggressive than a naive client would do: median 5 entities per extended-metadata POST but p95 = 299 and max = 1425, for 45,896 entity requests over 1093 POSTs. TRACK_V4(10) alone was requested 29,325 times. Any Wavee implementation that does not coalesce aggressively will make an order of magnitude more requests.
  10. High-cardinality per-entity 404s are NORMAL and must not be treated as errors: AUDIO_ASSOCIATIONS(98) was 1772/1773 404, EPISODE_ACCESS(30) 494 404s, PODCAST_SUBSCRIPTIONS(22) 86/86 404, CANVAS_V1(16) 239 404s, VIDEO_ASSOCIATIONS(99) 973 404s. EPISODE_V4(12) returned 451 (legal/geo block) 50 times and IDENTITY_TRAIT(178) returned 451 24 times — a 451 per-entity status is a real, expected case.
  11. Newly type_url-identified kinds beyond the previously established set: 142 LIST_TUNER_AUDIO_ANALYSIS -> spotify.playlist.tuner.extension.ListTunerAudioAnalysis, 151 RECOMMENDED_PLAYLISTS -> spotify.artistsectionprovider.v1.RecommendedPlaylists, 205 LIST_METADATA_V2 -> spotify.list.v1.model.Attributes, 114 WATCH_FEED_ENTITY_EXPLORER -> spotify.watchfeedextensions.api.v1.EntityExplorerEntrypointResponse, 164 GATED_ENTITY_RELATIONS -> spotify.gatedentityrelations.v1.GatedEntityRelations, 170 AUTO_LENS -> spotify.autolensextension.v1.AutoLens, 182 CONSUMPTION_EXPERIENCE_TRAIT -> spotify.contentagnostic.v2.ConsumptionExperienceTrait, 78 PLAYABILITY -> spotify.bumblebee.playability.v1.Playability, 83 AUDIOBOOK_GENRE -> spotify.audiobookgenres.AudiobookGenres, 37 PODCAST_RATING -> spotify.ratings.PodcastRating, 28 CUEPOINTS -> spotify.automix.proto.Cuepoints, 3 PODCAST_TOPICS -> spotify.podcast.extensions.PodcastTopics.
  12. Kind sets are strictly scheme-scoped — the client never asks for a kind an entity type cannot answer. Observed: track=[6,10,16,22,28,85,86,98,99,142,164,178,179,182,185,212,217,218,219,220,222,239,249]; episode=[4,12,21,22,30,31,54,58,80,164,178,179,182,212,239,249]; album=[9,86,151,179,182,212,249]; playlist=[86,114,149,170,178,179,182,205,212,225,249]; show=[3,11,21,30,31,37,52,54,64,78,80,83,88,138,164,178,179,183,239,246]; artist=[8,179,249]; list=[86,114,149,170,178,179]; local=[178,179,182,212,249]; user=[15]; station/internal=[86]; audio=[5]; collection=[249]. Note artist only ever asks for 3 kinds.
  13. quicksilver is a two-step protocol Wavee doesn't implement: GET /quicksilver/v2/triggers returns the URI-pattern routing table (spotify:artist:?, spotify:home, spotify:search, spotify:collection, spotify:album:?, spotify:open, plus CLIENT_EVENT app:update:eol), and only a navigation MATCHING one of those patterns triggers GET /quicksilver/v2/messages?trigger={uri}. Fetching messages on every navigation would be wrong.
  14. GET /recently-played/v3/recently-played?limit=50&filter=default,collection-new-episodes returns the entire 'Recently played' rail in one protobuf call ({context uri, played_at ms, last track uri} triples, including spotify:station:* and spotify:user:{id}:collection entries). Wavee has zero references to this path — it is a cheap, complete replacement for whatever it reconstructs client-side.
  15. GET /device-capabilities/v1/capabilities is the authoritative source for HiFi/DJ entitlement (supports_hifi.{fully_supported,user_eligible,device_supported}, audio_quality:"HIFI_24", supports_dj:true, supported_media_types including audio/dj). Wavee does not call it. Guessing quality tiers instead of reading this is a correctness risk.
  16. POST /playback-settings/.../GetAllStoredContentValues with {f1:1000} returns the stored per-context playback settings for EVERY context the user has ever touched in one gRPC response — including spotify:list:popular-release-segments-main-roles:artist_* entries, confirming the artist-Popular list is a first-class playback context.
  17. The playlist 'add more' surface is TWO different endpoints, not one: POST /playlistextender/extendp/ for tracks (returns fully-hydrated track objects with artists, album, imageUrl, popularity and a float score — no extended-metadata follow-up needed) and POST /assisted-curation/v1/recommendations/curation/uri for shows (returns bare spotify:show: URIs). Wavee references playlistextender in 2 files but has zero references to assisted-curation.
  18. Autoplay/radio continuation is two distinct mechanisms with different shapes: POST /context-resolve/v1/autoplay takes a protobuf {context uri, repeated recently-played track uris} and returns JSON pages; GET /radio-apollo/v3/tracks/{stationUri} takes prev_tracks as a comma-separated list of BARE base62 ids (not URIs) with an explicit pageNum and returns a next_page_url pointing at an hm:// URI. Every returned track carries a metadata.decision_id ("ssp~...") that should be echoed back in playback telemetry.
  19. /clip-transcript/v1/transcripts/{episodeUri}?offsets.start=0.000s&offsets.end=60.000s returns word-level timings WITH speakerId diarization — this is what drives the animated caption on podcast preview cards. 32 calls; Wavee has no reference to it.
  20. /playlist-permission/v1/playlist/{id}/permission/base is called 783 times — one per playlist the client so much as glances at, and the entire useful payload is the string 'default'. If Wavee mirrors this it should cache hard; if it is only needed for edit affordances it can be lazy.
  21. The pickasso.spotifycdn.com mix-art URLs are fully TEMPLATED and need no API call: /image/ab67c0de0000deef/dt/v1/img/{radio|artistmix|daily|topic|thisisv3|dw|release-radar-v4}/{seedId}/{locale}. Likewise seed-mix-image.spotifycdn.com/v6/img/desc/{Mood Name}/{locale}/default for mood-descriptor covers (observed: Feel Good Happy, Eurodance, Chill Happy, Moody Sad, Breakup, Nostalgia, Badass, Wedding, Cardio Pop, ...). Wavee can construct these directly.
  22. Two prefixes of pure noise to filter when reading these captures: 111+54+28 CONNECT tunnels (Fiddler HTTPS interception artifacts, not requests), ~60 OPTIONS CORS preflights, and 14 GETs to raw.githubusercontent.com/amll-dev/amll-ttml-db for TTML lyrics plus google.com/reddit.com hits — those are a third-party lyrics mod and ordinary browsing in the captured session, not Spotify API surface.
  23. searchAudiobooks is the only search* operation that sends includePreReleases:true; every other one (searchTracks/Albums/Artists/Playlists/Podcasts/Authors/Users) sends false while still sending includeAlbumPreReleases:true. searchFullEpisodes has a completely different, much smaller variable shape ({searchTerm, offset, limit, includeEpisodeContentRatingsV2}) with no include* block at all.
  24. searchSuggestions fires per keystroke — 22 calls for typing 'wasa' (one for 'w', 'wa', 'was', 'wasa' plus repeats) with limit:30/numberOfTopResults:30. The heavy searchTopResultsList (limit:50, sectionFilters:["GENERIC","VIDEO_CONTENT"]) fires only on commit. Wavee should replicate that split rather than debouncing one query.
