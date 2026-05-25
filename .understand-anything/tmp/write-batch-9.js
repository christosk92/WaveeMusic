const fs = require('fs');
const output = {
  nodes: [
    {
      id: "schema:src/Wavee/Protocol/Protos/played_state/episode_played_state.proto",
      type: "schema",
      name: "episode_played_state.proto",
      filePath: "src/Wavee/Protocol/Protos/played_state/episode_played_state.proto",
      summary: "Defines the EpisodePlayState protobuf message tracking per-episode playback progress, playability, and last-played timestamp for Spotify podcast episodes.",
      tags: ["schema-definition", "protobuf", "podcast", "played-state"],
      complexity: "simple"
    },
    {
      id: "schema:src/Wavee/Protocol/Protos/played_state/episode_played_state.proto:EpisodePlayState",
      type: "schema",
      name: "EpisodePlayState",
      filePath: "src/Wavee/Protocol/Protos/played_state/episode_played_state.proto",
      summary: "Protobuf message carrying time_left, is_playable, is_played, last_played_at, and playability_restriction for a single podcast episode.",
      tags: ["schema-definition", "protobuf", "podcast", "played-state"],
      complexity: "simple",
      lineRange: [12, 18]
    },
    {
      id: "schema:src/Wavee/Protocol/Protos/played_state/playability_restriction.proto",
      type: "schema",
      name: "playability_restriction.proto",
      filePath: "src/Wavee/Protocol/Protos/played_state/playability_restriction.proto",
      summary: "Defines the PlayabilityRestriction enum shared across episode, show, and track played-state messages, encoding reasons content cannot be played (explicit, age-restricted, not in catalogue, etc.).",
      tags: ["schema-definition", "protobuf", "shared-enum", "played-state"],
      complexity: "simple"
    },
    {
      id: "schema:src/Wavee/Protocol/Protos/played_state/playability_restriction.proto:PlayabilityRestriction",
      type: "schema",
      name: "PlayabilityRestriction",
      filePath: "src/Wavee/Protocol/Protos/played_state/playability_restriction.proto",
      summary: "Enum with six values (UNKNOWN, NO_RESTRICTION, EXPLICIT_CONTENT, AGE_RESTRICTED, NOT_IN_CATALOGUE, NOT_AVAILABLE_OFFLINE) representing why content may be unplayable.",
      tags: ["schema-definition", "protobuf", "shared-enum", "played-state"],
      complexity: "simple",
      lineRange: [10, 17]
    },
    {
      id: "schema:src/Wavee/Protocol/Protos/played_state/show_played_state.proto",
      type: "schema",
      name: "show_played_state.proto",
      filePath: "src/Wavee/Protocol/Protos/played_state/show_played_state.proto",
      summary: "Defines the ShowPlayState protobuf message representing aggregate playback state for a podcast show, including progress label (NOT_STARTED / IN_PROGRESS / COMPLETED), played percentage, and resume/latest episode links.",
      tags: ["schema-definition", "protobuf", "podcast", "played-state"],
      complexity: "simple"
    },
    {
      id: "schema:src/Wavee/Protocol/Protos/played_state/show_played_state.proto:ShowPlayState",
      type: "schema",
      name: "ShowPlayState",
      filePath: "src/Wavee/Protocol/Protos/played_state/show_played_state.proto",
      summary: "Protobuf message with show-level fields: latest_played_episode_link, played_time, is_playable, playability_restriction, Label enum (NOT_STARTED/IN_PROGRESS/COMPLETED), played_percentage, and resume_episode_link.",
      tags: ["schema-definition", "protobuf", "podcast", "played-state"],
      complexity: "simple",
      lineRange: [13, 29]
    },
    {
      id: "schema:src/Wavee/Protocol/Protos/played_state/track_played_state.proto",
      type: "schema",
      name: "track_played_state.proto",
      filePath: "src/Wavee/Protocol/Protos/played_state/track_played_state.proto",
      summary: "Defines the TrackPlayState protobuf message with minimal per-track playability status (is_playable and playability_restriction) used for music tracks.",
      tags: ["schema-definition", "protobuf", "track", "played-state"],
      complexity: "simple"
    },
    {
      id: "schema:src/Wavee/Protocol/Protos/played_state/track_played_state.proto:TrackPlayState",
      type: "schema",
      name: "TrackPlayState",
      filePath: "src/Wavee/Protocol/Protos/played_state/track_played_state.proto",
      summary: "Protobuf message with two fields (is_playable, playability_restriction) representing whether a music track can be played and why not if restricted.",
      tags: ["schema-definition", "protobuf", "track", "played-state"],
      complexity: "simple",
      lineRange: [12, 15]
    }
  ],
  edges: [
    {
      source: "schema:src/Wavee/Protocol/Protos/played_state/episode_played_state.proto",
      target: "schema:src/Wavee/Protocol/Protos/played_state/episode_played_state.proto:EpisodePlayState",
      type: "contains",
      direction: "forward",
      weight: 1.0
    },
    {
      source: "schema:src/Wavee/Protocol/Protos/played_state/playability_restriction.proto",
      target: "schema:src/Wavee/Protocol/Protos/played_state/playability_restriction.proto:PlayabilityRestriction",
      type: "contains",
      direction: "forward",
      weight: 1.0
    },
    {
      source: "schema:src/Wavee/Protocol/Protos/played_state/show_played_state.proto",
      target: "schema:src/Wavee/Protocol/Protos/played_state/show_played_state.proto:ShowPlayState",
      type: "contains",
      direction: "forward",
      weight: 1.0
    },
    {
      source: "schema:src/Wavee/Protocol/Protos/played_state/track_played_state.proto",
      target: "schema:src/Wavee/Protocol/Protos/played_state/track_played_state.proto:TrackPlayState",
      type: "contains",
      direction: "forward",
      weight: 1.0
    },
    {
      source: "schema:src/Wavee/Protocol/Protos/played_state/episode_played_state.proto",
      target: "schema:src/Wavee/Protocol/Protos/played_state/playability_restriction.proto",
      type: "depends_on",
      direction: "forward",
      weight: 0.6
    },
    {
      source: "schema:src/Wavee/Protocol/Protos/played_state/show_played_state.proto",
      target: "schema:src/Wavee/Protocol/Protos/played_state/playability_restriction.proto",
      type: "depends_on",
      direction: "forward",
      weight: 0.6
    },
    {
      source: "schema:src/Wavee/Protocol/Protos/played_state/track_played_state.proto",
      target: "schema:src/Wavee/Protocol/Protos/played_state/playability_restriction.proto",
      type: "depends_on",
      direction: "forward",
      weight: 0.6
    },
    {
      source: "schema:src/Wavee/Protocol/Protos/played_state/episode_played_state.proto",
      target: "file:src/Wavee/Protocol/Generated/played_state/EpisodePlayedState.cs",
      type: "defines_schema",
      direction: "forward",
      weight: 0.8
    },
    {
      source: "schema:src/Wavee/Protocol/Protos/played_state/playability_restriction.proto",
      target: "file:src/Wavee/Protocol/Generated/played_state/PlayabilityRestriction.cs",
      type: "defines_schema",
      direction: "forward",
      weight: 0.8
    },
    {
      source: "schema:src/Wavee/Protocol/Protos/played_state/show_played_state.proto",
      target: "file:src/Wavee/Protocol/Generated/played_state/ShowPlayedState.cs",
      type: "defines_schema",
      direction: "forward",
      weight: 0.8
    },
    {
      source: "schema:src/Wavee/Protocol/Protos/played_state/track_played_state.proto",
      target: "file:src/Wavee/Protocol/Generated/played_state/TrackPlayedState.cs",
      type: "defines_schema",
      direction: "forward",
      weight: 0.8
    }
  ]
};

const outPath = "C:\\WAVEE\\WaveeMusic\\.understand-anything\\intermediate\\batch-9.json";
fs.writeFileSync(outPath, JSON.stringify(output, null, 2), {encoding: 'utf8'});
console.log("Written: " + outPath);
console.log("Nodes: " + output.nodes.length + ", Edges: " + output.edges.length);
