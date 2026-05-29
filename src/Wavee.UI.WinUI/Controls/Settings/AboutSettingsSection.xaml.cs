using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.ViewModels;
using Windows.System;

namespace Wavee.UI.WinUI.Controls.Settings;

[global::WinRT.GeneratedBindableCustomProperty]
public sealed partial class AboutSettingsSection : UserControl, ISettingsSearchFilter
{
    private static readonly ObservableCollection<ThirdPartyNoticeItem> s_thirdPartyNotices =
    [
        ThirdPartyNoticeItem.Brand(
            "WinUI 3 / Windows App SDK",
            "Microsoft.WindowsAppSDK 2.1.4-experimental8",
            "MIT",
            "Provides the desktop app platform: XAML UI, Windows 11 app lifecycle, packaging, and window integration.",
            "GitHub",
            "https://github.com/microsoft/WindowsAppSDK",
            FontAwesome6.EFontAwesomeIcon.Brands_Microsoft,
            "Package",
            "https://www.nuget.org/packages/Microsoft.WindowsAppSDK"),

        ThirdPartyNoticeItem.Brand(
            "Windows Community Toolkit",
            "CommunityToolkit.WinUI.* 8.3 preview, Labs controls, MVVM 8.4.2",
            "MIT",
            "Supplies settings cards, UI helpers, animations, converters, shimmer controls, and MVVM source generators used across the app.",
            "GitHub",
            "https://github.com/CommunityToolkit/Windows",
            FontAwesome6.EFontAwesomeIcon.Brands_Microsoft,
            "Package",
            "https://www.nuget.org/profiles/CommunityToolkit"),

        ThirdPartyNoticeItem.Brand(
            "Microsoft.Extensions",
            "DependencyInjection, Hosting, Http, Logging 10.0.7",
            "MIT",
            "Runs the dependency injection container, hosted services, HTTP clients, and logging abstractions shared by the UI, core, and audio host.",
            "GitHub",
            "https://github.com/dotnet/runtime",
            FontAwesome6.EFontAwesomeIcon.Brands_Microsoft,
            "Package",
            "https://www.nuget.org/packages/Microsoft.Extensions.Hosting"),

        ThirdPartyNoticeItem.Brand(
            "Microsoft.Data.Sqlite",
            "Microsoft.Data.Sqlite 10.0.7",
            "MIT",
            "Backs local caches and library metadata stores with SQLite while keeping database access in managed .NET code.",
            "GitHub",
            "https://github.com/dotnet/efcore",
            FontAwesome6.EFontAwesomeIcon.Brands_Microsoft,
            "Package",
            "https://www.nuget.org/packages/Microsoft.Data.Sqlite"),

        ThirdPartyNoticeItem.Brand(
            "Microsoft Windows AI",
            "Microsoft.WindowsAppSDK AI projection assemblies",
            "Microsoft",
            "Enables optional Phi Silica on-device AI features on Copilot+ PCs without sending prompts or lyrics to a Wavee server.",
            "Docs",
            "https://learn.microsoft.com/windows/ai/apis/",
            FontAwesome6.EFontAwesomeIcon.Brands_Microsoft,
            "Package",
            "https://www.nuget.org/packages/Microsoft.WindowsAppSDK.AI"),

        ThirdPartyNoticeItem.Brand(
            "Win2D",
            "Microsoft.Graphics.Win2D 1.4.0",
            "MIT",
            "Renders GPU-backed blur, canvas, and imaging effects used by the right panel, video surfaces, lyrics, and visual backgrounds.",
            "GitHub",
            "https://github.com/microsoft/Win2D",
            FontAwesome6.EFontAwesomeIcon.Brands_Microsoft,
            "Package",
            "https://www.nuget.org/packages/Microsoft.Graphics.Win2D"),

        ThirdPartyNoticeItem.Glyph(
            "ComputeSharp",
            "ComputeSharp.D2D1.WinUI 3.2.0",
            "MIT",
            "Compiles C# shaders for D2D/Win2D effects such as mesh gradients, fluid backgrounds, fog, rain, and snow lyrics visuals.",
            "GitHub",
            "https://github.com/Sergio0694/ComputeSharp",
            "\uE950",
            "Package",
            "https://www.nuget.org/packages/ComputeSharp.D2D1.WinUI"),

        ThirdPartyNoticeItem.Glyph(
            "FluentIcons, FontAwesome, QRCoder",
            "FluentIcons.WinUI 2.1.326, FontAwesome6.Svg.WinUI 2.5.1, QRCoder 1.8.0",
            "Mixed",
            "Provides UI symbols, recognizable brand marks, and QR-code generation for connect/login surfaces and external links.",
            "FluentIcons",
            "https://www.nuget.org/packages/FluentIcons.WinUI",
            "\uE8A5",
            "FontAwesome",
            "https://www.nuget.org/packages/FontAwesome6.Svg.WinUI"),

        ThirdPartyNoticeItem.Brand(
            "Google Protobuf and gRPC tools",
            "Google.Protobuf 3.34.1, Grpc.Tools 2.80.0",
            "BSD-3 / Apache-2.0",
            "Generates and parses Spotify protocol messages, connect-state payloads, metadata responses, and playback telemetry envelopes.",
            "Protobuf",
            "https://github.com/protocolbuffers/protobuf",
            FontAwesome6.EFontAwesomeIcon.Brands_Google,
            "gRPC",
            "https://github.com/grpc/grpc"),

        ThirdPartyNoticeItem.Glyph(
            "System.Reactive",
            "System.Reactive 7.0.0-preview.*",
            "MIT",
            "Composes asynchronous playback, connect-state, library, and UI event streams without hand-rolled observer plumbing.",
            "GitHub",
            "https://github.com/dotnet/reactive",
            "\uE895",
            "Package",
            "https://www.nuget.org/packages/System.Reactive"),

        ThirdPartyNoticeItem.Glyph(
            "Serilog",
            "Serilog 4.3.x, sinks, hosting/logging extensions",
            "Apache-2.0",
            "Captures structured logs for diagnostics, debug settings, the audio host, and the GitHub issue report package.",
            "GitHub",
            "https://github.com/serilog/serilog",
            "\uE8FD",
            "Package",
            "https://www.nuget.org/packages/Serilog"),

        ThirdPartyNoticeItem.Glyph(
            "NTextCat",
            "NTextCat 0.3.65",
            "MIT",
            "Detects lyric languages so romanization, translation display, and localized lyrics handling can make better choices.",
            "Package",
            "https://www.nuget.org/packages/NTextCat",
            "\uE774"),

        ThirdPartyNoticeItem.Glyph(
            "NAudio.Wasapi",
            "NAudio.Wasapi 2.3.0",
            "MIT",
            "Reads Windows audio-session data for spectrum analysis and lyrics visual effects that react to local playback.",
            "GitHub",
            "https://github.com/naudio/NAudio",
            "\uE8D6",
            "Package",
            "https://www.nuget.org/packages/NAudio.Wasapi"),

        ThirdPartyNoticeItem.Glyph(
            "Vortice, SpoutDx, Vanara",
            "Vortice.Direct3D11 3.8.3, SpoutDx.Net.Interop.MultiPlatform 0.1.0, Vanara.PInvoke.User32 5.0.4",
            "MIT",
            "Provides Direct3D and Win32 interop for lyrics rendering, texture sharing, floating windows, and drag-to-detach behavior.",
            "Vortice",
            "https://github.com/amerkoleci/Vortice.Windows",
            "\uE7F4",
            "Vanara",
            "https://github.com/dahall/Vanara"),

        ThirdPartyNoticeItem.Glyph(
            "BASS and ManagedBass",
            "BASS native library, ManagedBass 4.0.2",
            "Proprietary / MIT",
            "Decodes and streams local and remote audio in the out-of-process audio host; ManagedBass is the .NET wrapper used by that host.",
            "BASS",
            "https://www.un4seen.com/",
            "\uE8D6",
            "ManagedBass",
            "https://github.com/ManagedBass/ManagedBass"),

        ThirdPartyNoticeItem.Glyph(
            "PortAudioSharp2",
            "PortAudioSharp2 1.0.6",
            "MIT",
            "Enumerates and switches local output devices in the audio host, including default-device follow behavior.",
            "Package",
            "https://www.nuget.org/packages/PortAudioSharp2",
            "\uE767"),

        ThirdPartyNoticeItem.Glyph(
            "NVorbis",
            "Vendored NVorbis project",
            "MIT",
            "Provides a managed Ogg Vorbis decoder and seek path for Spotify OGG streams without introducing another native dependency.",
            "GitHub",
            "https://github.com/NVorbis/NVorbis",
            "\uE8D6"),

        ThirdPartyNoticeItem.Glyph(
            "ATL.NET",
            "z440.atl.core 7.13.0",
            "MIT",
            "Reads local-file audio tags and embedded artwork for the local library and metadata overlay flows.",
            "GitHub",
            "https://github.com/Zeugma440/atldotnet",
            "\uE8A5",
            "Package",
            "https://www.nuget.org/packages/z440.atl.core"),

        ThirdPartyNoticeItem.Glyph(
            "WinUIEx",
            "WinUIEx 2.9.0",
            "MIT",
            "Provides WinUI window helpers used by app windows, floating panels, and shell integration that WinUI does not expose directly.",
            "GitHub",
            "https://github.com/dotMorten/WinUIEx",
            "\uE78B",
            "Package",
            "https://www.nuget.org/packages/WinUIEx"),

        ThirdPartyNoticeItem.Glyph(
            "ZstdSharp.Port",
            "ZstdSharp.Port 0.8.8",
            "BSD-3",
            "Decompresses zstd-encoded Spotify metadata responses before protobuf parsing.",
            "GitHub",
            "https://github.com/oleg-st/ZstdSharp",
            "\uE8B1",
            "Package",
            "https://www.nuget.org/packages/ZstdSharp.Port"),

        ThirdPartyNoticeItem.Glyph(
            "Lyricify Lyrics Helper",
            "Vendored Lyricify.Lyrics.Helper 0.1.4",
            "Apache-2.0",
            "Searches, parses, decrypts, and normalizes multi-provider lyric formats used by the lyrics experience.",
            "GitHub",
            "https://github.com/WXRIW/Lyricify-Lyrics-Helper",
            "\uE8D6"),

        ThirdPartyNoticeItem.Glyph(
            "CJK romanization",
            "csharp-pinyin 1.0.1, WanaKana-net 1.0.0, CHTCHSConv 1.0.0",
            "MIT",
            "Converts Chinese and Japanese lyric text into romanized forms for synchronized lyric display and language-aware helpers.",
            "csharp-pinyin",
            "https://www.nuget.org/packages/csharp-pinyin",
            "\uE774",
            "WanaKana",
            "https://www.nuget.org/packages/WanaKana-net"),
    ];

    public SettingsViewModel ViewModel { get; }
    public ObservableCollection<ThirdPartyNoticeItem> ThirdPartyNotices => s_thirdPartyNotices;

    public AboutSettingsSection(SettingsViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
    }

    public void ApplySearchFilter(string? groupKey)
        => SettingsGroupFilter.Apply(SettingsGroupsRoot, groupKey);

    private async void WhatsNew_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new WhatsNewDialog { XamlRoot = XamlRoot };
        await dialog.ShowAsync();
    }

    // Kept for revival once the in-app feedback destination is built; the
    // owning SettingsCard is currently Visibility=Collapsed.
    private void Feedback_Click(object sender, RoutedEventArgs e)
    {
        NavigationHelpers.OpenFeedback();
    }

    private async void GitHub_Click(object sender, RoutedEventArgs e)
    {
        await Launcher.LaunchUriAsync(new Uri("https://github.com/christosk92/WaveeMusic"));
    }

    private async void License_Click(object sender, RoutedEventArgs e)
    {
        await Launcher.LaunchUriAsync(new Uri("https://github.com/christosk92/WaveeMusic/blob/master/LICENSE"));
    }

    private async void ReportOnGitHub_Click(object sender, RoutedEventArgs e)
    {
        await CrashReportPackager.OpenIssueReportAsync();
    }
}

[global::WinRT.GeneratedBindableCustomProperty]
public sealed partial class ThirdPartyNoticeItem
{
    private ThirdPartyNoticeItem(
        string name,
        string packageDisplay,
        string license,
        string reason,
        string primaryLinkLabel,
        string primaryUrl,
        FontAwesome6.EFontAwesomeIcon brandIcon,
        bool hasBrandIcon,
        string fallbackGlyph,
        string? secondaryLinkLabel,
        string? secondaryUrl)
    {
        Name = name;
        PackageDisplay = packageDisplay;
        License = license;
        Reason = reason;
        PrimaryLinkLabel = primaryLinkLabel;
        PrimaryUri = new Uri(primaryUrl);
        BrandIcon = brandIcon;
        HasBrandIcon = hasBrandIcon;
        FallbackGlyph = fallbackGlyph;
        SecondaryLinkLabel = secondaryLinkLabel ?? string.Empty;
        SecondaryUri = string.IsNullOrWhiteSpace(secondaryUrl) ? PrimaryUri : new Uri(secondaryUrl);
    }

    public string Name { get; }
    public string PackageDisplay { get; }
    public string License { get; }
    public string Reason { get; }
    public string PrimaryLinkLabel { get; }
    public Uri PrimaryUri { get; }
    public FontAwesome6.EFontAwesomeIcon BrandIcon { get; }
    public string FallbackGlyph { get; }
    public string SecondaryLinkLabel { get; }
    public Uri SecondaryUri { get; }
    public bool HasBrandIcon { get; }
    public Visibility BrandIconVisibility => HasBrandIcon ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FallbackIconVisibility => HasBrandIcon ? Visibility.Collapsed : Visibility.Visible;
    public Visibility SecondaryLinkVisibility => string.IsNullOrWhiteSpace(SecondaryLinkLabel)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public static ThirdPartyNoticeItem Brand(
        string name,
        string packageDisplay,
        string license,
        string reason,
        string primaryLinkLabel,
        string primaryUrl,
        FontAwesome6.EFontAwesomeIcon brandIcon,
        string? secondaryLinkLabel = null,
        string? secondaryUrl = null)
        => new(
            name,
            packageDisplay,
            license,
            reason,
            primaryLinkLabel,
            primaryUrl,
            brandIcon,
            true,
            "\uE8A5",
            secondaryLinkLabel,
            secondaryUrl);

    public static ThirdPartyNoticeItem Glyph(
        string name,
        string packageDisplay,
        string license,
        string reason,
        string primaryLinkLabel,
        string primaryUrl,
        string fallbackGlyph,
        string? secondaryLinkLabel = null,
        string? secondaryUrl = null)
        => new(
            name,
            packageDisplay,
            license,
            reason,
            primaryLinkLabel,
            primaryUrl,
            FontAwesome6.EFontAwesomeIcon.Solid_ArrowUpRightFromSquare,
            false,
            fallbackGlyph,
            secondaryLinkLabel,
            secondaryUrl);
}
