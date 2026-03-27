using System;
using System.Net.Http;
using Wavee.Core.Http;
using Wavee.UI.WinUI.Data.Models;

namespace Wavee.UI.WinUI.Helpers;

/// <summary>
/// Maps exceptions to user-friendly error messages.
/// Pure function — deterministic and testable.
/// </summary>
public static class ErrorMapper
{
    public static string ToUserMessage(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound } => "Content not found.",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests } => "Too many requests. Please wait a moment.",
        HttpRequestException => "Network error. Check your connection.",
        OperationCanceledException => "Request was cancelled.",
        SpClientException { Reason: SpClientFailureReason.Unauthorized } => "Session expired. Please reconnect.",
        SpClientException { Reason: SpClientFailureReason.NotFound } => "Content not found.",
        SpClientException { Reason: SpClientFailureReason.RateLimited } => "Too many requests. Please wait.",
        SpClientException => "Something went wrong. Please try again.",
        _ => "An unexpected error occurred."
    };

    public static string ToPlaybackMessage(PlaybackErrorKind kind) => kind switch
    {
        PlaybackErrorKind.Network => "Network error. Check your connection.",
        PlaybackErrorKind.Unauthorized => "Session expired. Please reconnect.",
        PlaybackErrorKind.DeviceUnavailable => "No active device. Open Spotify on a device first.",
        PlaybackErrorKind.PremiumRequired => "Premium required for this feature.",
        PlaybackErrorKind.RateLimited => "Too many requests. Please wait.",
        PlaybackErrorKind.NotFound => "Track or playlist not found.",
        _ => "Something went wrong with playback."
    };
}
