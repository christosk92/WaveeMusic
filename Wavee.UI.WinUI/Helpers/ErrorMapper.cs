using System;
using System.Net.Http;
using Wavee.Core.Http;

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
}
