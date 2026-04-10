using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Media.Imaging;
using Wavee.Core.Feedback;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.ViewModels;

public sealed partial class FeedbackViewModel : ObservableObject
{
    private readonly IFeedbackService _feedbackService;
    private readonly ISettingsService _settingsService;
    private readonly InMemorySink _inMemorySink;
    private readonly ILogger? _logger;

    public TabItemParameter TabItemParameter { get; } = new()
    {
        InitialPageType = typeof(Views.FeedbackPage)
    };

    public FeedbackViewModel(
        IFeedbackService feedbackService,
        ISettingsService settingsService,
        InMemorySink inMemorySink,
        ILogger<FeedbackViewModel>? logger = null)
    {
        _feedbackService = feedbackService;
        _settingsService = settingsService;
        _inMemorySink = inMemorySink;
        _logger = logger;

        // Initialize from persisted consent defaults
        var s = _settingsService.Settings;
        _includeDiagnostics = s.FeedbackIncludeDiagnostics;
        _includeDeviceMetadata = s.FeedbackIncludeDeviceMetadata;
        _isAnonymous = s.FeedbackAnonymous;
    }

    // ── Inline validation ──

    [ObservableProperty]
    private string _titleError = "";

    [ObservableProperty]
    private string _bodyError = "";

    public bool HasTitleError => !string.IsNullOrEmpty(TitleError);
    public bool HasBodyError => !string.IsNullOrEmpty(BodyError);

    [ObservableProperty]
    private string _severityError = "";

    [ObservableProperty]
    private string _reproducibilityError = "";

    public bool HasSeverityError => !string.IsNullOrEmpty(SeverityError);
    public bool HasReproducibilityError => !string.IsNullOrEmpty(ReproducibilityError);

    partial void OnTitleErrorChanged(string value) => OnPropertyChanged(nameof(HasTitleError));
    partial void OnBodyErrorChanged(string value) => OnPropertyChanged(nameof(HasBodyError));
    partial void OnSeverityErrorChanged(string value) => OnPropertyChanged(nameof(HasSeverityError));
    partial void OnReproducibilityErrorChanged(string value) => OnPropertyChanged(nameof(HasReproducibilityError));

    private bool ValidateDetailsStep()
    {
        var valid = true;

        if (string.IsNullOrWhiteSpace(Title))
        {
            TitleError = "Title is required.";
            valid = false;
        }
        else
            TitleError = "";

        if (string.IsNullOrWhiteSpace(Body))
        {
            BodyError = "Description is required.";
            valid = false;
        }
        else
            BodyError = "";

        if (FeedbackTypeIndex == 0) // Bug
        {
            if (SeverityIndex < 0)
            {
                SeverityError = "Severity is required for bug reports.";
                valid = false;
            }
            else
                SeverityError = "";

            if (ReproducibilityIndex < 0)
            {
                ReproducibilityError = "Reproducibility is required for bug reports.";
                valid = false;
            }
            else
                ReproducibilityError = "";
        }
        else
        {
            SeverityError = "";
            ReproducibilityError = "";
        }

        return valid;
    }

    // ── Form fields ──

    [ObservableProperty]
    private int _feedbackTypeIndex;

    [ObservableProperty]
    private int _severityIndex = -1; // Unselected

    partial void OnSeverityIndexChanged(int value)
    {
        if (HasSeverityError && value >= 0)
            SeverityError = "";
    }

    [ObservableProperty]
    private int _reproducibilityIndex = -1; // Unselected

    partial void OnReproducibilityIndexChanged(int value)
    {
        if (HasReproducibilityError && value >= 0)
            ReproducibilityError = "";
    }

    [ObservableProperty]
    private string _title = "";

    partial void OnTitleChanged(string value)
    {
        if (HasTitleError && !string.IsNullOrWhiteSpace(value))
            TitleError = "";
    }

    [ObservableProperty]
    private string _body = "";

    partial void OnBodyChanged(string value)
    {
        if (HasBodyError && !string.IsNullOrWhiteSpace(value))
            BodyError = "";
    }

    [ObservableProperty]
    private string _contactEmail = "";

    // ── Image attachments ──

    public const int MaxImages = 5;
    public const long MaxImageBytes = 5 * 1024 * 1024; // 5 MB

    private static readonly string[] AllowedExtensions = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"];

    public ObservableCollection<AttachedImage> AttachedImages { get; } = [];

    public bool CanAddMoreImages => AttachedImages.Count < MaxImages;

    [ObservableProperty]
    private string _imageError = "";

    public bool HasImageError => !string.IsNullOrEmpty(ImageError);
    partial void OnImageErrorChanged(string value) => OnPropertyChanged(nameof(HasImageError));

    public bool TryAddImage(string filePath, out string? error)
    {
        error = null;

        if (AttachedImages.Count >= MaxImages)
        {
            error = $"Maximum {MaxImages} images allowed.";
            ImageError = error;
            return false;
        }

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (!AllowedExtensions.Contains(ext))
        {
            error = "Unsupported image format. Use PNG, JPG, GIF, WebP, or BMP.";
            ImageError = error;
            return false;
        }

        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists)
        {
            error = "File not found.";
            ImageError = error;
            return false;
        }

        if (fileInfo.Length > MaxImageBytes)
        {
            error = "Image must be smaller than 5 MB.";
            ImageError = error;
            return false;
        }

        var bytes = File.ReadAllBytes(filePath);
        var base64 = Convert.ToBase64String(bytes);
        var contentType = ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            _ => "application/octet-stream"
        };

        var image = new AttachedImage
        {
            FileName = fileInfo.Name,
            ContentType = contentType,
            Base64Data = base64,
            FilePath = filePath
        };

        AttachedImages.Add(image);
        ImageError = "";
        OnPropertyChanged(nameof(CanAddMoreImages));
        return true;
    }

    [RelayCommand]
    private void RemoveImage(AttachedImage image)
    {
        AttachedImages.Remove(image);
        ImageError = "";
        OnPropertyChanged(nameof(CanAddMoreImages));
    }

    // ── Consent toggles ──

    [ObservableProperty]
    private bool _includeDiagnostics;

    [ObservableProperty]
    private bool _includeDeviceMetadata;

    [ObservableProperty]
    private bool _isAnonymous;

    partial void OnIncludeDiagnosticsChanged(bool value) =>
        _settingsService.Update(s => s.FeedbackIncludeDiagnostics = value);

    partial void OnIncludeDeviceMetadataChanged(bool value) =>
        _settingsService.Update(s => s.FeedbackIncludeDeviceMetadata = value);

    partial void OnIsAnonymousChanged(bool value) =>
        _settingsService.Update(s => s.FeedbackAnonymous = value);

    // ── Submission state ──

    [ObservableProperty]
    private bool _isSubmitting;

    [ObservableProperty]
    private bool _isSuccess;

    [ObservableProperty]
    private bool _isError;

    [ObservableProperty]
    private string _errorMessage = "";

    [ObservableProperty]
    private string _successId = "";

    // ── Computed ──

    public bool ShowSeverity => FeedbackTypeIndex == 0; // Bug
    public bool ShowReproducibility => FeedbackTypeIndex == 0; // Bug
    public bool ShowContactEmail => !IsAnonymous;

    partial void OnFeedbackTypeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(ShowSeverity));
        OnPropertyChanged(nameof(ShowReproducibility));
        OnPropertyChanged(nameof(IsBugSelected));
        OnPropertyChanged(nameof(IsTicketSelected));
        OnPropertyChanged(nameof(IsFeatureSelected));
        OnPropertyChanged(nameof(IsGeneralSelected));
    }

    public bool IsBugSelected => FeedbackTypeIndex == 0;
    public bool IsTicketSelected => FeedbackTypeIndex == 1;
    public bool IsFeatureSelected => FeedbackTypeIndex == 2;
    public bool IsGeneralSelected => FeedbackTypeIndex == 3;

    partial void OnIsAnonymousChanging(bool value)
    {
        OnPropertyChanged(nameof(ShowContactEmail));
    }

    // ── Commands ──

    [RelayCommand]
    private async Task SubmitAsync(CancellationToken ct)
    {
        if (!ValidateDetailsStep())
            return;

        IsSubmitting = true;
        IsError = false;
        IsSuccess = false;

        try
        {
            var request = new FeedbackSubmitRequest
            {
                Type = (FeedbackType)FeedbackTypeIndex,
                Severity = SeverityIndex >= 0 ? (FeedbackSeverity)SeverityIndex : FeedbackSeverity.Low,
                Reproducibility = ReproducibilityIndex >= 0 ? (FeedbackReproducibility)ReproducibilityIndex : FeedbackReproducibility.NotApplicable,
                Title = Title,
                Body = Body,
                AppVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0",
                OsVersion = IncludeDeviceMetadata ? RuntimeInformation.OSDescription : "",
                DeviceInfo = IncludeDeviceMetadata ? $"{RuntimeInformation.OSArchitecture} / {Environment.ProcessorCount} cores" : "",
                IncludeDiagnostics = IncludeDiagnostics,
                IncludeDeviceMetadata = IncludeDeviceMetadata,
                IsAnonymous = IsAnonymous,
                DiagnosticsLog = IncludeDiagnostics ? BuildDiagnosticsLog() : null,
                ContactEmail = IsAnonymous ? null : (string.IsNullOrWhiteSpace(ContactEmail) ? null : ContactEmail.Trim()),
                Attachments = AttachedImages.Count > 0
                    ? AttachedImages.Select(img => new FeedbackAttachment
                    {
                        FileName = img.FileName,
                        ContentType = img.ContentType,
                        Base64Data = img.Base64Data
                    }).ToList()
                    : null
            };

            var response = await _feedbackService.SubmitAsync(request, ct);

            if (response is not null)
            {
                IsSuccess = true;
                SuccessId = response.Id;
                ResetForm();
                _logger?.LogInformation("Feedback submitted: {Id}", response.Id);
            }
            else
            {
                IsError = true;
                ErrorMessage = AppLocalization.GetString("Feedback_SubmitFailed");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Feedback submission error");
            IsError = true;
            ErrorMessage = AppLocalization.GetString("Error_Unexpected");
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    private string BuildDiagnosticsLog()
    {
        var sb = new StringBuilder();
        var entries = _inMemorySink.Entries;
        var count = Math.Min(entries.Count, 500);
        for (var i = entries.Count - count; i < entries.Count; i++)
        {
            var e = entries[i];
            sb.AppendLine($"[{e.LevelShort}] {e.TimeString} {e.Category}: {e.Message}");
            if (e.Exception is not null)
                sb.AppendLine($"  {e.Exception}");
        }
        return sb.ToString();
    }

    private void ResetForm()
    {
        Title = "";
        Body = "";
        ContactEmail = "";
        FeedbackTypeIndex = 0;
        SeverityIndex = -1;
        ReproducibilityIndex = -1;
        AttachedImages.Clear();
        OnPropertyChanged(nameof(CanAddMoreImages));
    }
}

public sealed class AttachedImage
{
    public required string FileName { get; init; }
    public required string ContentType { get; init; }
    public required string Base64Data { get; init; }
    public required string FilePath { get; init; }
}
