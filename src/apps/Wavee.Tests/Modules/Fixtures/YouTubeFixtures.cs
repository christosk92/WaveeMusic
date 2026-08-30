namespace Wavee.Tests.Modules.Fixtures;

/// <summary>
/// Sanitized <c>youtubei/v1/player</c> and <c>youtubei/v1/next</c> responses plus channel-page HTML, shaped exactly
/// like the real endpoints (member names, nesting and value types) with the identifying parts replaced. Kept as raw
/// string literals so the bodies are verbatim JSON but need no copy-to-output plumbing in the test csproj.
/// </summary>
public static class YouTubeFixtures
{
    /// <summary>The video id every fixture talks about.</summary>
    public const string VideoId = "tRsQsTMvPNg";

    /// <summary>The channel id every fixture attributes its video to.</summary>
    public const string ChannelId = "UCAAAAAAAAAAAAAAAAAAAAA";

    /// <summary>The <c>responseContext.visitorData</c> the InnerTube fixtures hand back. InnerTube returns one on
    /// EVERY response and expects it echoed on the next request; the module used to read neither.</summary>
    public const string VisitorData = "CgtBQUFBQUFBQUFBQQ%3D%3D";

    /// <summary>The channel avatar only <c>/next</c> knows about; the player response never pictures a channel.</summary>
    public const string ChannelAvatarUrl = "https://yt3.ggpht.com/ytc/AAAAAAAAAAAAAAAAAAAAAAAA=s176-c-k-c0x00ffffff-no-rj";

    /// <summary>The first up-next entry's id (a finished video).</summary>
    public const string RelatedVodId = "bbbbbbbbbbb";

    /// <summary>The second up-next entry's id (a broadcast, badged LIVE).</summary>
    public const string RelatedLiveId = "ccccccccccc";

    /// <summary>The widest thumbnail of the first up-next entry, in both rail shapes.</summary>
    public const string RelatedVodThumbnailUrl = "https://i.ytimg.com/vi/bbbbbbbbbbb/hqdefault.jpg";

    /// <summary>The widest thumbnail of the second up-next entry, in both rail shapes.</summary>
    public const string RelatedLiveThumbnailUrl = "https://i.ytimg.com/vi/ccccccccccc/hqdefault.jpg";

    /// <summary>The signed HLS master url the OK fixtures return; carries <c>/expire/1767225600/</c>.</summary>
    public const string HlsManifestUrl =
        "https://manifest.googlevideo.com/api/manifest/hls_variant/expire/1767225600/ei/AAAAAAAAAAAA/ip/" +
        "203.0.113.7/id/tRsQsTMvPNg.1/source/yt_live_broadcast/requiressl/yes/hfr/1/playlist_type/DVR/" +
        "sparams/expire%2Cei%2Cip%2Cid/sig/AAAAAAAA/playlist/index.m3u8";

    /// <summary>A live broadcast that plays: status OK, matching id, an HLS master and a session lifetime.</summary>
    public const string PlayerLiveOk = $$"""
    {
      "responseContext": { "visitorData": "CgtBQUFBQUFBQUFBQQ%3D%3D" },
      "playabilityStatus": { "status": "OK", "playableInEmbed": true },
      "streamingData": {
        "expiresInSeconds": "21540",
        "formats": [],
        "adaptiveFormats": [],
        "dashManifestUrl": "https://manifest.googlevideo.com/api/manifest/dash/expire/1767225600/x/y",
        "hlsManifestUrl": "{{HlsManifestUrl}}"
      },
      "videoDetails": {
        "videoId": "{{VideoId}}",
        "title": "Claude FM",
        "lengthSeconds": "0",
        "isLive": true,
        "channelId": "UCAAAAAAAAAAAAAAAAAAAAA",
        "isOwnerViewing": false,
        "isCrawlable": true,
        "thumbnail": {
          "thumbnails": [
            { "url": "https://i.ytimg.com/vi/tRsQsTMvPNg/default.jpg", "width": 120, "height": 90 },
            { "url": "https://i.ytimg.com/vi/tRsQsTMvPNg/maxresdefault.jpg", "width": 1280, "height": 720 }
          ]
        },
        "allowRatings": true,
        "viewCount": "1234",
        "author": "Anthropic",
        "isPrivate": false,
        "isUnpluggedCorpus": false,
        "isLiveContent": true,
        "shortDescription": "A continuous broadcast, all day every day."
      },
      "microformat": {
        "playerMicroformatRenderer": {
          "lengthSeconds": "0",
          "isFamilySafe": true,
          "liveBroadcastDetails": { "isLiveNow": true, "startTimestamp": "2026-08-20T09:00:00-07:00" }
        }
      }
    }
    """;

    /// <summary>A regular (finished) video that plays: a real <c>lengthSeconds</c>, no live flags.</summary>
    public const string PlayerVodOk = $$"""
    {
      "playabilityStatus": { "status": "OK" },
      "streamingData": { "expiresInSeconds": "21540", "hlsManifestUrl": "{{HlsManifestUrl}}" },
      "videoDetails": {
        "videoId": "{{VideoId}}",
        "title": "A recorded talk",
        "lengthSeconds": "3672",
        "author": "Anthropic",
        "isLive": false,
        "isLiveContent": false,
        "viewCount": "987654",
        "shortDescription": "A recorded talk about parsers.",
        "thumbnail": { "thumbnails": [ { "url": "https://i.ytimg.com/vi/x/hq.jpg", "width": 480, "height": 360 } ] }
      },
      "microformat": {
        "playerMicroformatRenderer": {
          "liveBroadcastDetails": { "isLiveNow": false },
          "publishDate": "2026-08-20T00:00:00-07:00",
          "uploadDate": "2026-08-19T00:00:00-07:00",
          "ownerChannelName": "Anthropic",
          "externalChannelId": "UCAAAAAAAAAAAAAAAAAAAAA",
          "viewCount": "987654",
          "category": "Science & Technology"
        }
      }
    }
    """;

    /// <summary>YouTube answered about a different video — yt-dlp reads this as "your IP is being blocked".</summary>
    public const string PlayerVideoIdMismatch = $$"""
    {
      "playabilityStatus": { "status": "OK" },
      "streamingData": { "expiresInSeconds": "21540", "hlsManifestUrl": "{{HlsManifestUrl}}" },
      "videoDetails": { "videoId": "aaaaaaaaaaa", "title": "Something else", "author": "Someone",
                        "lengthSeconds": "60", "isLive": false, "isLiveContent": false }
    }
    """;

    /// <summary>Status OK but only a SABR endpoint: nothing Media Foundation can open.</summary>
    public const string PlayerSabrOnly = $$"""
    {
      "playabilityStatus": { "status": "OK" },
      "streamingData": {
        "expiresInSeconds": "21540",
        "serverAbrStreamingUrl": "https://rr1---sn-abcd.googlevideo.com/videoplayback?expire=1767225600&sabr=1",
        "adaptiveFormats": []
      },
      "videoDetails": { "videoId": "{{VideoId}}", "title": "Claude FM", "author": "Anthropic",
                        "lengthSeconds": "0", "isLive": true, "isLiveContent": true }
    }
    """;

    /// <summary>
    /// "Sign in to confirm you're not a bot" — the sign-in wall. Observed per-CLIENT on 2026-08-22 (VISIONOS walled,
    /// ANDROID served the same stream from the same IP) and per-DEVICE on 2026-08-23 (all three clients walled
    /// together for ~38 minutes), which is why the module now spends at most one alternate client on it.
    /// </summary>
    public const string PlayerBotWall = """
    {
      "responseContext": { "visitorData": "CgtBQUFBQUFBQUFBQQ%3D%3D" },
      "playabilityStatus": {
        "status": "LOGIN_REQUIRED",
        "reason": "Sign in to confirm you're not a bot",
        "errorScreen": { "playerErrorMessageRenderer": { "reason": { "simpleText": "Sign in to confirm you're not a bot" } } }
      },
      "videoDetails": { "videoId": "tRsQsTMvPNg", "title": "Claude FM", "author": "Anthropic",
                        "lengthSeconds": "0", "isLive": true, "isLiveContent": true }
    }
    """;

    /// <summary>
    /// A bare <c>LOGIN_REQUIRED</c>: no age marker, no age wording, and no "bot" in the reason either. The old
    /// predicate pair read this as an AGE GATE — terminal <c>NeedsAuth</c>, no next client — purely because the bot
    /// test was written above the age test and the age test ended in an unguarded <c>status == LOGIN_REQUIRED</c>.
    /// It belongs to the wall family: YouTube has reworded this demand repeatedly and an unfamiliar wording must not
    /// promote it to the one verdict nothing can recover from.
    /// </summary>
    public const string PlayerLoginRequiredBare = """
    {
      "playabilityStatus": { "status": "LOGIN_REQUIRED", "reason": "Sign in" },
      "videoDetails": { "videoId": "tRsQsTMvPNg", "title": "Claude FM", "author": "Anthropic",
                        "lengthSeconds": "0", "isLive": true, "isLiveContent": true }
    }
    """;

    /// <summary>An age-gated video: the legacy age-gate marker plus the matching reason.</summary>
    public const string PlayerAgeGate = """
    {
      "playabilityStatus": {
        "status": "LOGIN_REQUIRED",
        "reason": "Sign in to confirm your age",
        "desktopLegacyAgeGateReason": 1
      },
      "videoDetails": { "videoId": "tRsQsTMvPNg", "title": "Age restricted", "author": "Someone",
                        "lengthSeconds": "300", "isLive": false, "isLiveContent": false }
    }
    """;

    /// <summary>A scheduled broadcast that has not started.</summary>
    public const string PlayerLiveOffline = """
    {
      "playabilityStatus": {
        "status": "LIVE_STREAM_OFFLINE",
        "reason": "This live event will begin in a few moments."
      },
      "videoDetails": { "videoId": "tRsQsTMvPNg", "title": "Scheduled broadcast", "author": "Anthropic",
                        "lengthSeconds": "0", "isLive": false, "isUpcoming": true, "isLiveContent": true },
      "microformat": {
        "playerMicroformatRenderer": {
          "liveBroadcastDetails": { "isLiveNow": false, "startTimestamp": "2026-09-01T17:00:00+00:00" }
        }
      }
    }
    """;

    /// <summary>An outright refusal for this client ("made for kids", "not available on this app", …).</summary>
    public const string PlayerUnplayable = """
    {
      "playabilityStatus": {
        "status": "UNPLAYABLE",
        "reason": "This video is not available on this app."
      },
      "videoDetails": { "videoId": "tRsQsTMvPNg", "title": "Kids video", "author": "Someone",
                        "lengthSeconds": "120", "isLive": false, "isLiveContent": false }
    }
    """;

    // ---- youtubei/v1/next ----------------------------------------------------------------------------------------
    //
    // The WEB watch-next document, trimmed to the renderers the module reads and kept in its real nesting — including
    // the doubled `results.results` / `secondaryResults.secondaryResults` wrappers and the neighbouring renderers the
    // module must skip (a comment section, a continuation), because skipping them is the behaviour under test.

    /// <summary>The left column every watch-next fixture shares: a concurrent-viewer count, a "started streaming"
    /// line, an owner block with an avatar, and the comment section the module must skip. Spliced into the two rail
    /// fixtures below so they differ ONLY in the shape of the up-next rail, which is the thing under test.</summary>
    private const string NextLeftColumnLive = $$"""
          "results": {
            "results": {
              "contents": [
                {
                  "videoPrimaryInfoRenderer": {
                    "title": { "runs": [ { "text": "Claude FM" } ] },
                    "viewCount": {
                      "videoViewCountRenderer": {
                        "viewCount": { "runs": [ { "text": "12,345" }, { "text": " watching now" } ] },
                        "isLive": true,
                        "originalViewCount": "12345"
                      }
                    },
                    "dateText": { "simpleText": "Started streaming 3 hours ago" }
                  }
                },
                {
                  "videoSecondaryInfoRenderer": {
                    "owner": {
                      "videoOwnerRenderer": {
                        "thumbnail": {
                          "thumbnails": [
                            { "url": "https://yt3.ggpht.com/ytc/AAAAAAAAAAAAAAAAAAAAAAAA=s48-c-k-c0x00ffffff-no-rj", "width": 48, "height": 48 },
                            { "url": "{{ChannelAvatarUrl}}", "width": 176, "height": 176 }
                          ]
                        },
                        "title": {
                          "runs": [
                            {
                              "text": "Anthropic",
                              "navigationEndpoint": { "browseEndpoint": { "browseId": "{{ChannelId}}" } }
                            }
                          ]
                        },
                        "navigationEndpoint": {
                          "browseEndpoint": { "browseId": "{{ChannelId}}", "canonicalBaseUrl": "/@anthropic" }
                        },
                        "subscriberCountText": {
                          "accessibility": { "accessibilityData": { "label": "1.2 million subscribers" } },
                          "simpleText": "1.2M subscribers"
                        }
                      }
                    },
                    "description": { "runs": [ { "text": "A continuous broadcast, all day every day." } ] }
                  }
                },
                { "itemSectionRenderer": { "sectionIdentifier": "comment-item-section" } }
              ]
            }
          }
    """;

    /// <summary>
    /// The LIVE watch-next document as YouTube actually answers it today: the up-next rail is
    /// <c>lockupViewModel</c> entries, transcribed from a real WEB capture (2026-08-23) — the doubled
    /// <c>secondaryResults</c> nesting, the <c>image.sources</c> spelling, the duration as a thumbnail overlay badge,
    /// the positional metadata grid, the second overlay the module must skip
    /// (<c>animatedThumbnailOverlayViewModel</c>), the third metadata row that carries a "New" badge and no
    /// <c>metadataParts</c>, and the trailing continuation. Identifying values are replaced; nesting and member names
    /// are verbatim.
    /// <para>The third entry is the ONE part not taken from the capture: no live video appeared in that rail, so its
    /// LIVE badge and "watching" metadata part are the module's inferred live shape, marked as such here so nobody
    /// mistakes this fixture for evidence of it.</para>
    /// </summary>
    public const string NextWatchLive = $$"""
    {
      "responseContext": { "visitorData": "CgtBQUFBQUFBQUFBQQ%3D%3D" },
      "contents": {
        "twoColumnWatchNextResults": {
    {{NextLeftColumnLive}},
          "secondaryResults": {
            "secondaryResults": {
              "results": [
                {
                  "lockupViewModel": {
                    "contentImage": {
                      "thumbnailViewModel": {
                        "image": {
                          "sources": [
                            { "url": "https://i.ytimg.com/vi/bbbbbbbbbbb/hqdefault.jpg?sqp=-oaymwEbCKgBEF5IVfKriqkD&rs=AOn4CLAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "width": 168, "height": 94 },
                            { "url": "{{RelatedVodThumbnailUrl}}", "width": 336, "height": 188 }
                          ]
                        },
                        "overlays": [
                          {
                            "thumbnailBottomOverlayViewModel": {
                              "badges": [
                                {
                                  "thumbnailBadgeViewModel": {
                                    "text": "1:01:12",
                                    "badgeStyle": "THUMBNAIL_OVERLAY_BADGE_STYLE_DEFAULT",
                                    "rendererContext": { "accessibilityContext": { "label": "1 hour, 1 minute, 12 seconds" } }
                                  }
                                }
                              ]
                            }
                          },
                          { "animatedThumbnailOverlayViewModel": { "playbackMode": "PLAYBACK_MODE_HOVER" } }
                        ]
                      }
                    },
                    "metadata": {
                      "lockupMetadataViewModel": {
                        "title": { "content": "A recorded talk" },
                        "image": {
                          "decoratedAvatarViewModel": {
                            "avatar": {
                              "avatarViewModel": {
                                "image": { "sources": [ { "url": "{{ChannelAvatarUrl}}", "width": 68, "height": 68 } ] },
                                "avatarImageSize": "AVATAR_SIZE_M"
                              }
                            },
                            "a11yLabel": "Go to channel Anthropic"
                          }
                        },
                        "metadata": {
                          "contentMetadataViewModel": {
                            "metadataRows": [
                              { "metadataParts": [ { "text": { "content": "Anthropic" } } ] },
                              {
                                "metadataParts": [
                                  { "text": { "content": "987K views" } },
                                  { "text": { "content": "1 month ago" }, "accessibilityLabel": "1 month ago" }
                                ]
                              },
                              { "badges": [ { "badgeViewModel": { "badgeText": "New", "badgeStyle": "BADGE_DEFAULT" } } ] }
                            ],
                            "delimiter": " · "
                          }
                        }
                      }
                    },
                    "contentId": "{{RelatedVodId}}",
                    "contentType": "LOCKUP_CONTENT_TYPE_VIDEO"
                  }
                },
                {
                  "lockupViewModel": {
                    "contentImage": {
                      "thumbnailViewModel": {
                        "image": { "sources": [ { "url": "https://i.ytimg.com/vi/ddddddddddd/hqdefault.jpg", "width": 336, "height": 188 } ] }
                      }
                    },
                    "metadata": {
                      "lockupMetadataViewModel": {
                        "title": { "content": "A mix nobody can play" },
                        "metadata": {
                          "contentMetadataViewModel": {
                            "metadataRows": [ { "metadataParts": [ { "text": { "content": "YouTube" } } ] } ]
                          }
                        }
                      }
                    },
                    "contentId": "RDAMVMtRsQsTMvPNg",
                    "contentType": "LOCKUP_CONTENT_TYPE_PLAYLIST"
                  }
                },
                {
                  "lockupViewModel": {
                    "contentImage": {
                      "thumbnailViewModel": {
                        "image": { "sources": [ { "url": "{{RelatedLiveThumbnailUrl}}", "width": 336, "height": 188 } ] },
                        "overlays": [
                          {
                            "thumbnailBottomOverlayViewModel": {
                              "badges": [ { "thumbnailBadgeViewModel": { "text": "LIVE", "badgeStyle": "THUMBNAIL_OVERLAY_BADGE_STYLE_LIVE" } } ]
                            }
                          }
                        ]
                      }
                    },
                    "metadata": {
                      "lockupMetadataViewModel": {
                        "title": { "content": "Another broadcast" },
                        "metadata": {
                          "contentMetadataViewModel": {
                            "metadataRows": [
                              { "metadataParts": [ { "text": { "content": "Someone Else" } } ] },
                              { "metadataParts": [ { "text": { "content": "4,200 watching" } } ] }
                            ]
                          }
                        }
                      }
                    },
                    "contentId": "{{RelatedLiveId}}",
                    "contentType": "LOCKUP_CONTENT_TYPE_VIDEO"
                  }
                },
                { "continuationItemRenderer": { "trigger": "CONTINUATION_TRIGGER_ON_ITEM_SHOWN" } }
              ]
            }
          }
        }
      }
    }
    """;

    /// <summary>The same document with the OLDER <c>compactVideoRenderer</c> rail. Both shapes are in flight upstream
    /// at once, so both branches are covered; this one is the fallback.</summary>
    public const string NextWatchCompactRail = $$"""
    {
      "responseContext": { "visitorData": "CgtBQUFBQUFBQUFBQQ%3D%3D" },
      "contents": {
        "twoColumnWatchNextResults": {
    {{NextLeftColumnLive}},
          "secondaryResults": {
            "secondaryResults": {
              "results": [
                {
                  "compactVideoRenderer": {
                    "videoId": "{{RelatedVodId}}",
                    "title": { "simpleText": "A recorded talk" },
                    "longBylineText": { "runs": [ { "text": "Anthropic" } ] },
                    "thumbnail": {
                      "thumbnails": [
                        { "url": "https://i.ytimg.com/vi/bbbbbbbbbbb/default.jpg", "width": 120, "height": 90 },
                        { "url": "{{RelatedVodThumbnailUrl}}", "width": 480, "height": 360 }
                      ]
                    },
                    "lengthText": { "simpleText": "1:01:12", "accessibility": { "accessibilityData": { "label": "1 hour, 1 minute, 12 seconds" } } },
                    "viewCountText": { "simpleText": "987K views" }
                  }
                },
                {
                  "compactVideoRenderer": {
                    "videoId": "{{RelatedLiveId}}",
                    "title": { "simpleText": "Another broadcast" },
                    "longBylineText": { "runs": [ { "text": "Someone Else" } ] },
                    "thumbnail": {
                      "thumbnails": [ { "url": "{{RelatedLiveThumbnailUrl}}", "width": 480, "height": 360 } ]
                    },
                    "viewCountText": { "runs": [ { "text": "4,200" }, { "text": " watching" } ] },
                    "badges": [ { "metadataBadgeRenderer": { "style": "BADGE_STYLE_TYPE_LIVE_NOW", "label": "LIVE" } } ]
                  }
                },
                { "continuationItemRenderer": { "trigger": "CONTINUATION_TRIGGER_ON_ITEM_SHOWN" } }
              ]
            }
          }
        }
      }
    }
    """;

    /// <summary>A finished-video watch-next document: a lifetime view count (<c>isLive</c> absent) and a plain date.</summary>
    public const string NextWatchVod = $$"""
    {
      "contents": {
        "twoColumnWatchNextResults": {
          "results": {
            "results": {
              "contents": [
                {
                  "videoPrimaryInfoRenderer": {
                    "title": { "runs": [ { "text": "A recorded talk" } ] },
                    "viewCount": {
                      "videoViewCountRenderer": {
                        "viewCount": { "simpleText": "987,654 views" },
                        "shortViewCount": { "simpleText": "987K views" }
                      }
                    },
                    "dateText": { "simpleText": "Aug 20, 2026" }
                  }
                },
                {
                  "videoSecondaryInfoRenderer": {
                    "owner": {
                      "videoOwnerRenderer": {
                        "thumbnail": { "thumbnails": [ { "url": "{{ChannelAvatarUrl}}", "width": 176, "height": 176 } ] },
                        "title": { "runs": [ { "text": "Anthropic" } ] },
                        "navigationEndpoint": { "browseEndpoint": { "browseId": "{{ChannelId}}" } },
                        "subscriberCountText": { "simpleText": "1.2M subscribers" }
                      }
                    }
                  }
                }
              ]
            }
          },
          "secondaryResults": { "secondaryResults": { "results": [] } }
        }
      }
    }
    """;

    /// <summary>A well-formed 200 that carries none of the renderers the module reads — the "shape drifted" case.</summary>
    public const string NextWatchUnknownShape = """
    {
      "responseContext": { "visitorData": "CgtBQUFBQUFBQUFBQQ%3D%3D" },
      "contents": {
        "singleColumnWatchNextResults": {
          "results": { "results": { "contents": [ { "slimVideoMetadataSectionRenderer": {} } ] } }
        }
      }
    }
    """;

    /// <summary>Not JSON at all: the HTML error page a fronting proxy serves instead of an InnerTube answer.</summary>
    public const string NextGarbage = "<html><head><title>500 Internal Server Error</title></head><body>nope</body></html>";

    /// <summary>The HLS master the preflight GET reads.</summary>
    public const string HlsMaster = """
    #EXTM3U
    #EXT-X-INDEPENDENT-SEGMENTS
    #EXT-X-STREAM-INF:BANDWIDTH=1478400,CODECS="avc1.4d401f,mp4a.40.2",RESOLUTION=854x480,FRAME-RATE=30
    https://manifest.googlevideo.com/api/manifest/hls_playlist/expire/1767225600/id/x.1/itag/93/playlist/index.m3u8
    """;

    /// <summary>A channel /live page whose player state carries the current broadcast's id.</summary>
    public const string ChannelLiveHtmlWithEndpoint = """
    <!DOCTYPE html><html><head><title>Anthropic - Live</title>
    <link rel="canonical" href="https://www.youtube.com/@anthropic">
    </head><body><script>var ytInitialData = {"responseContext":{},"contents":{},
    "currentVideoEndpoint":{"clickTrackingParams":"AAAA","commandMetadata":{"webCommandMetadata":
    {"url":"/watch?v=tRsQsTMvPNg","webPageType":"WEB_PAGE_TYPE_WATCH"}},
    "watchEndpoint":{"videoId":"tRsQsTMvPNg","params":"BBBB"}}};</script></body></html>
    """;

    /// <summary>A channel /live page with no player state, only the canonical watch link.</summary>
    public const string ChannelLiveHtmlCanonicalOnly = """
    <!DOCTYPE html><html><head><title>Anthropic - Live</title>
    <link rel="canonical" href="https://www.youtube.com/watch?v=tRsQsTMvPNg">
    </head><body><script>var ytInitialData = {"responseContext":{}};</script></body></html>
    """;

    /// <summary>A channel /live page for a channel that is not broadcasting.</summary>
    public const string ChannelLiveHtmlOffline = """
    <!DOCTYPE html><html><head><title>Anthropic</title>
    <link rel="canonical" href="https://www.youtube.com/@anthropic">
    </head><body><script>var ytInitialData = {"responseContext":{},"contents":{}};</script></body></html>
    """;
}
