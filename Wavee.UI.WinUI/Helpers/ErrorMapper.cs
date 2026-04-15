using System;
using System.IO;
using System.Net.Http;
using Wavee.Core.Http;
using Wavee.UI.Models;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.Helpers;

/// <summary>
/// Maps exceptions to user-friendly error messages.
/// Pure function — deterministic and testable.
/// </summary>
public static class ErrorMapper
{
    public static string ToUserMessage(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound } => AppLocalization.GetString("Error_ContentNotFound"),
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests } => AppLocalization.GetString("Error_TooManyRequestsMoment"),
        HttpRequestException => AppLocalization.GetString("Error_NetworkCheckConnection"),
        TimeoutException => AppLocalization.GetString("Error_GenericTryAgain"),
        IOException => AppLocalization.GetString("Error_NetworkCheckConnection"),
        OperationCanceledException => AppLocalization.GetString("Error_RequestCancelled"),
        SpClientException { Reason: SpClientFailureReason.Unauthorized } => AppLocalization.GetString("Error_SessionExpiredReconnect"),
        SpClientException { Reason: SpClientFailureReason.NotFound } => AppLocalization.GetString("Error_ContentNotFound"),
        SpClientException { Reason: SpClientFailureReason.RateLimited } => AppLocalization.GetString("Error_TooManyRequestsWait"),
        SpClientException { Reason: SpClientFailureReason.RequestFailed } => AppLocalization.GetString("Error_NetworkCheckConnection"),
        SpClientException { Reason: SpClientFailureReason.ServerError } => AppLocalization.GetString("Error_GenericTryAgain"),
        SpClientException => AppLocalization.GetString("Error_GenericTryAgain"),
        _ => AppLocalization.GetString("Error_Unexpected")
    };

    public static string ToPlaybackMessage(PlaybackErrorKind kind) => kind switch
    {
        PlaybackErrorKind.Network => AppLocalization.GetString("Error_NetworkCheckConnection"),
        PlaybackErrorKind.Unauthorized => AppLocalization.GetString("Error_SessionExpiredReconnect"),
        PlaybackErrorKind.DeviceUnavailable => AppLocalization.GetString("PlaybackError_NoActiveDevice"),
        PlaybackErrorKind.PremiumRequired => AppLocalization.GetString("PlaybackError_PremiumRequired"),
        PlaybackErrorKind.RateLimited => AppLocalization.GetString("Error_TooManyRequestsWait"),
        PlaybackErrorKind.NotFound => AppLocalization.GetString("PlaybackError_NotFound"),
        _ => AppLocalization.GetString("PlaybackError_Generic")
    };
}
