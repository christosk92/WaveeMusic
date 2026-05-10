using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Wavee.Connect.Commands.Wire;

internal sealed class RemoteCommandEnvelope
{
    [JsonPropertyName("command")]         public required RemoteCommand Command { get; init; }
    [JsonPropertyName("connection_type")] public string ConnectionType { get; init; } = "wlan";
    [JsonPropertyName("intent_id")]       public required string IntentId { get; init; }
}

internal sealed class RemoteCommand
{
    [JsonPropertyName("endpoint")]             public required string Endpoint { get; init; }

    [JsonPropertyName("context")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RemoteContext? Context { get; init; }

    [JsonPropertyName("play_origin")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RemotePlayOrigin? PlayOrigin { get; init; }

    [JsonPropertyName("prepare_play_options")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RemotePreparePlayOptions? PreparePlayOptions { get; init; }

    [JsonPropertyName("play_options")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RemotePlayOptions? PlayOptions { get; init; }

    [JsonPropertyName("logging_params")]       public required RemoteLoggingParams LoggingParams { get; init; }
}

internal sealed class RemoteContext
{
    [JsonPropertyName("entity_uri")]   public required string EntityUri { get; init; }
    [JsonPropertyName("uri")]          public required string Uri { get; init; }
    [JsonPropertyName("url")]          public required string Url { get; init; }
    [JsonPropertyName("metadata")]     public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
    [JsonPropertyName("restrictions")] public RemoteContextRestrictions Restrictions { get; init; } = new();

    [JsonPropertyName("pages")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<RemoteContextPage>? Pages { get; init; }
}

internal sealed class RemoteContextRestrictions
{
    [JsonPropertyName("disallow_peeking_prev_reasons")]                  public IReadOnlyList<string> DisallowPeekingPrev                { get; init; } = [];
    [JsonPropertyName("disallow_peeking_next_reasons")]                  public IReadOnlyList<string> DisallowPeekingNext                { get; init; } = [];
    [JsonPropertyName("disallow_skipping_prev_reasons")]                 public IReadOnlyList<string> DisallowSkippingPrev               { get; init; } = [];
    [JsonPropertyName("disallow_skipping_next_reasons")]                 public IReadOnlyList<string> DisallowSkippingNext               { get; init; } = [];
    [JsonPropertyName("disallow_pausing_reasons")]                       public IReadOnlyList<string> DisallowPausing                    { get; init; } = [];
    [JsonPropertyName("disallow_resuming_reasons")]                      public IReadOnlyList<string> DisallowResuming                   { get; init; } = [];
    [JsonPropertyName("disallow_toggling_repeat_context_reasons")]       public IReadOnlyList<string> DisallowToggleRepeatContext        { get; init; } = [];
    [JsonPropertyName("disallow_toggling_repeat_track_reasons")]         public IReadOnlyList<string> DisallowToggleRepeatTrack          { get; init; } = [];
    [JsonPropertyName("disallow_toggling_shuffle_reasons")]              public IReadOnlyList<string> DisallowToggleShuffle              { get; init; } = [];
    [JsonPropertyName("disallow_set_queue_reasons")]                     public IReadOnlyList<string> DisallowSetQueue                   { get; init; } = [];
    [JsonPropertyName("disallow_add_to_queue_reasons")]                  public IReadOnlyList<string> DisallowAddToQueue                 { get; init; } = [];
    [JsonPropertyName("disallow_seeking_reasons")]                       public IReadOnlyList<string> DisallowSeeking                    { get; init; } = [];
    [JsonPropertyName("disallow_interrupting_playback_reasons")]         public IReadOnlyList<string> DisallowInterruptingPlayback       { get; init; } = [];
    [JsonPropertyName("disallow_transferring_playback_reasons")]         public IReadOnlyList<string> DisallowTransferringPlayback       { get; init; } = [];
    [JsonPropertyName("disallow_remote_control_reasons")]                public IReadOnlyList<string> DisallowRemoteControl              { get; init; } = [];
    [JsonPropertyName("disallow_inserting_into_next_tracks_reasons")]    public IReadOnlyList<string> DisallowInsertingIntoNextTracks    { get; init; } = [];
    [JsonPropertyName("disallow_inserting_into_context_tracks_reasons")] public IReadOnlyList<string> DisallowInsertingIntoContextTracks { get; init; } = [];
    [JsonPropertyName("disallow_reordering_in_next_tracks_reasons")]     public IReadOnlyList<string> DisallowReorderingInNextTracks     { get; init; } = [];
    [JsonPropertyName("disallow_reordering_in_context_tracks_reasons")]  public IReadOnlyList<string> DisallowReorderingInContextTracks  { get; init; } = [];
    [JsonPropertyName("disallow_removing_from_next_tracks_reasons")]     public IReadOnlyList<string> DisallowRemovingFromNextTracks     { get; init; } = [];
    [JsonPropertyName("disallow_removing_from_context_tracks_reasons")]  public IReadOnlyList<string> DisallowRemovingFromContextTracks  { get; init; } = [];
    [JsonPropertyName("disallow_updating_context_reasons")]              public IReadOnlyList<string> DisallowUpdatingContext            { get; init; } = [];
}

internal sealed class RemoteContextPage
{
    [JsonPropertyName("page_url")]      public string PageUrl     { get; init; } = "";
    [JsonPropertyName("next_page_url")] public string NextPageUrl { get; init; } = "";
    [JsonPropertyName("tracks")]        public required IReadOnlyList<RemoteContextTrack> Tracks { get; init; }
    [JsonPropertyName("metadata")]      public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

internal sealed class RemoteContextTrack
{
    [JsonPropertyName("uri")] public required string Uri { get; init; }
    [JsonPropertyName("uid")] public required string Uid { get; init; }

    [JsonPropertyName("metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

internal sealed class RemotePlayOrigin
{
    [JsonPropertyName("device_identifier")]      public string DeviceIdentifier            { get; init; } = "";
    [JsonPropertyName("feature_identifier")]     public required string FeatureIdentifier  { get; init; }
    [JsonPropertyName("feature_version")]        public required string FeatureVersion     { get; init; }
    [JsonPropertyName("view_uri")]               public string ViewUri                     { get; init; } = "";
    [JsonPropertyName("external_referrer")]      public string ExternalReferrer            { get; init; } = "";
    [JsonPropertyName("referrer_identifier")]    public string ReferrerIdentifier          { get; init; } = "";
    [JsonPropertyName("feature_classes")]        public IReadOnlyList<string> FeatureClasses { get; init; } = [];
    [JsonPropertyName("restriction_identifier")] public string RestrictionIdentifier       { get; init; } = "";
}

internal sealed class RemotePreparePlayOptions
{
    [JsonPropertyName("always_play_something")]    public bool AlwaysPlaySomething { get; init; }

    [JsonPropertyName("skip_to")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RemoteSkipTo? SkipTo { get; init; }

    [JsonPropertyName("initially_paused")]         public bool InitiallyPaused { get; init; }
    [JsonPropertyName("system_initiated")]         public bool SystemInitiated { get; init; }
    [JsonPropertyName("player_options_override")]  public RemotePlayerOptionsOverride PlayerOptionsOverride { get; init; } = new();
    [JsonPropertyName("session_id")]               public string SessionId { get; init; } = "";
    [JsonPropertyName("license")]                  public string License   { get; init; } = "premium";
    [JsonPropertyName("suppressions")]             public RemoteSuppressions Suppressions { get; init; } = new();
    [JsonPropertyName("prefetch_level")]           public string PrefetchLevel { get; init; } = "none";
    [JsonPropertyName("audio_stream")]             public string AudioStream   { get; init; } = "default";
    [JsonPropertyName("configuration_override")]   public IReadOnlyDictionary<string, string> ConfigurationOverride { get; init; } = new Dictionary<string, string>();
}

internal sealed class RemoteSkipTo
{
    [JsonPropertyName("page_index")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PageIndex { get; init; }

    [JsonPropertyName("track_uri")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TrackUri { get; init; }

    [JsonPropertyName("track_uid")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TrackUid { get; init; }

    [JsonPropertyName("track_index")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TrackIndex { get; init; }
}

internal sealed class RemotePlayerOptionsOverride
{
    [JsonPropertyName("shuffling_context")] public bool ShufflingContext { get; init; }
    [JsonPropertyName("modes")]             public RemotePlayerModes Modes { get; init; } = new();
}

internal sealed class RemotePlayerModes
{
    [JsonPropertyName("context_enhancement")] public string ContextEnhancement { get; init; } = "NONE";
}

internal sealed class RemoteSuppressions
{
    [JsonPropertyName("providers")] public IReadOnlyList<string> Providers { get; init; } = [];
}

internal sealed class RemotePlayOptions
{
    [JsonPropertyName("reason")]                public string Reason                { get; init; } = "interactive";
    [JsonPropertyName("operation")]             public string Operation             { get; init; } = "replace";
    [JsonPropertyName("trigger")]               public string Trigger               { get; init; } = "immediately";
    [JsonPropertyName("override_restrictions")] public bool   OverrideRestrictions  { get; init; }
    [JsonPropertyName("only_for_local_device")] public bool   OnlyForLocalDevice    { get; init; }
    [JsonPropertyName("system_initiated")]      public bool   SystemInitiated       { get; init; }
}

internal sealed class RemoteLoggingParams
{
    [JsonPropertyName("command_initiated_time")] public required long CommandInitiatedTime { get; init; }
    [JsonPropertyName("page_instance_ids")]      public IReadOnlyList<string> PageInstanceIds { get; init; } = [];
    [JsonPropertyName("interaction_ids")]        public IReadOnlyList<string> InteractionIds  { get; init; } = [];
    [JsonPropertyName("device_identifier")]      public string DeviceIdentifier { get; init; } = "";
    [JsonPropertyName("command_id")]             public required string CommandId { get; init; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RemoteCommandEnvelope))]
internal partial class RemotePlayCommandJsonContext : JsonSerializerContext;
