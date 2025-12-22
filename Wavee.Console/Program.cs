using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Spectre.Console;
using Wavee.Console;
using Wavee.Core.Authentication;
using Wavee.Core.Session;
using Wavee.OAuth;

// Enable UTF-8 output for Korean/Unicode characters
Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

// Create SpectreUI instance (will be configured with Serilog later)
using var spectreUI = new SpectreUI();

// Configure Serilog with SpectreUI sink
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
    .Enrich.FromLogContext()
    .WriteTo.SpectreUI(spectreUI, LogEventLevel.Debug)
    .CreateLogger();

// Setup Microsoft.Extensions.Logging with Serilog
using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddSerilog(Log.Logger, dispose: false);
    builder.SetMinimumLevel(LogLevel.Debug);
});

var logger = loggerFactory.CreateLogger<Program>();
var sessionLogger = loggerFactory.CreateLogger("Wavee.Core.Session.Session");

// Setup dependency injection with HttpClient
var services = new ServiceCollection();
services.AddHttpClient("Wavee", client => { client.Timeout = TimeSpan.FromSeconds(30); });
var serviceProvider = services.BuildServiceProvider();
var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();

// Setup credentials cache
var credentialsCache = new CredentialsCache(logger: loggerFactory.CreateLogger<CredentialsCache>());

try
{
    // Show startup banner
    AnsiConsole.Write(new FigletText("Wavee").Color(Color.Green));
    AnsiConsole.MarkupLine("[dim]Spotify Connect Client[/]");
    AnsiConsole.WriteLine();

    // 1. Create session configuration
    var deviceId = GetOrCreateDeviceId();
    AnsiConsole.MarkupLine($"[dim]Device ID:[/] {deviceId[..8]}...");

    var config = new SessionConfig
    {
        DeviceId = deviceId,
        DeviceName = "Wavee Console",
        DeviceType = DeviceType.Computer
    };

    // 2. Try to load stored credentials
    var lastUsername = await credentialsCache.LoadLastUsernameAsync();
    var storedCredentials = await credentialsCache.LoadCredentialsAsync(lastUsername);

    Credentials credentials;
    if (storedCredentials != null)
    {
        AnsiConsole.MarkupLine("[green]Found stored credentials[/] - skipping OAuth");
        credentials = storedCredentials;
    }
    else
    {
        AnsiConsole.MarkupLine("[yellow]No stored credentials[/] - OAuth required");
        AnsiConsole.WriteLine();

        var oauthLogger = loggerFactory.CreateLogger("OAuth");
        var accessToken = await GetAccessTokenAsync(config.GetClientId(), oauthLogger);
        credentials = Credentials.WithAccessToken(accessToken);
    }

    // 3. Create and connect session
    AnsiConsole.MarkupLine("[dim]Creating session...[/]");
    await using var session = Session.Create(config, httpClientFactory, sessionLogger);

    // 4. Connect with status spinner
    await AnsiConsole.Status()
        .Spinner(Spinner.Known.Dots)
        .StartAsync("Connecting to Spotify...",
            async ctx => { await session.ConnectAsync(credentials, credentialsCache); });

    var userData = session.GetUserData();
    if (userData != null)
    {
        AnsiConsole.MarkupLine($"[green]Connected![/] Username: [bold]{userData.Username}[/]");

        // Get country and account info
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Fetching account info...", async ctx =>
            {
                var countryCode = await session.GetCountryCodeAsync();
                var accountType = await session.GetAccountTypeAsync();
                AnsiConsole.MarkupLine($"[dim]Country:[/] {countryCode}  [dim]Account:[/] {accountType}");
            });
    }

    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("[dim]Starting interactive console...[/]");
    AnsiConsole.MarkupLine("[dim]Press any key to continue[/]");
    System.Console.ReadKey(intercept: true);

    // 5. Run interactive Connect console
    var httpClient = httpClientFactory.CreateClient("Wavee");
    var audioPipelineLogger = loggerFactory.CreateLogger("Wavee.Audio.Pipeline");

    // Initialize UI with device info
    spectreUI.UpdateDevice(config.DeviceName, config.DeviceId, false);

    await using var connectConsole = new ConnectConsole(session, httpClient, spectreUI, audioPipelineLogger);
    await connectConsole.RunAsync();

    // 6. Cleanup
    Log.Information("Session closed successfully.");
}
catch (OAuthException ex)
{
    AnsiConsole.MarkupLine($"[red]OAuth failed:[/] {ex.Message}");
    AnsiConsole.MarkupLine($"[dim]Reason:[/] {ex.Reason}");
}
catch (AuthenticationException ex)
{
    AnsiConsole.MarkupLine($"[red]Authentication failed:[/] {ex.Message}");
    AnsiConsole.MarkupLine($"[dim]Reason:[/] {ex.Reason}");
}
catch (SessionException ex)
{
    AnsiConsole.MarkupLine($"[red]Session error:[/] {ex.Message}");
    AnsiConsole.MarkupLine($"[dim]Reason:[/] {ex.Reason}");
}
catch (Exception ex)
{
    AnsiConsole.Write(ex.ToString());
}

AnsiConsole.WriteLine();
AnsiConsole.MarkupLine("[dim]Press any key to exit...[/]");
System.Console.ReadKey(intercept: true);

// Helper methods

static async Task<string> GetAccessTokenAsync(string clientId, Microsoft.Extensions.Logging.ILogger logger)
{
    var flow = AnsiConsole.Prompt(
        new SelectionPrompt<OAuthFlow>()
            .Title("Select [green]OAuth authorization method[/]:")
            .AddChoices(OAuthFlow.AuthorizationCode, OAuthFlow.DeviceCode)
            .UseConverter(f => f switch
            {
                OAuthFlow.AuthorizationCode => "Authorization Code Flow (opens browser)",
                OAuthFlow.DeviceCode => "Device Code Flow (enter code manually)",
                _ => f.ToString()
            }));

    AnsiConsole.WriteLine();

    OAuthToken newToken;

    if (flow == OAuthFlow.DeviceCode)
    {
        AnsiConsole.MarkupLine("[cyan]Using Device Code Flow[/]");
        AnsiConsole.MarkupLine("[dim]You'll need to visit a URL and enter a code to authorize.[/]");
        AnsiConsole.WriteLine();

        var client = OAuthClient.CreateCustom(
            clientId,
            ["streaming", "user-read-playback-state", "user-modify-playback-state"],
            flow: OAuthFlow.DeviceCode,
            logger: logger);

        newToken = await client.GetAccessTokenAsync();
    }
    else
    {
        AnsiConsole.MarkupLine("[cyan]Using Authorization Code Flow[/]");
        AnsiConsole.MarkupLine("[dim]Your browser will open to authorize Wavee.[/]");
        AnsiConsole.WriteLine();

        var client = OAuthClient.CreateCustom(
            clientId,
            ["streaming", "user-read-playback-state", "user-modify-playback-state"],
            flow: OAuthFlow.AuthorizationCode,
            openBrowser: true,
            logger: logger);

        newToken = await client.GetAccessTokenAsync();
    }

    AnsiConsole.MarkupLine("[green]Successfully obtained access token![/]");
    return newToken.AccessToken;
}

static string GetOrCreateDeviceId()
{
    var deviceIdPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Wavee",
        "device_id.txt"
    );

    if (File.Exists(deviceIdPath))
    {
        return File.ReadAllText(deviceIdPath).Trim();
    }

    var deviceId = Guid.NewGuid().ToString();

    var directory = Path.GetDirectoryName(deviceIdPath);
    if (directory != null)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(deviceIdPath, deviceId);
    }

    return deviceId;
}