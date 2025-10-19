using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Wavee.Core.Authentication;
using Wavee.Core.Session;
using Wavee.OAuth;

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Verbose() // Verbose = Trace level
    .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

Log.Information("=== Wavee Session Test ===");

// Setup logging
using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddSerilog(Log.Logger, dispose: false);
    builder.SetMinimumLevel(LogLevel.Trace); // Trace level shows Ping/Pong
});

var logger = loggerFactory.CreateLogger<Program>();

try
{
    // 1. Create session configuration
    var deviceId = GetOrCreateDeviceId();
    Log.Information("Using device ID: {DeviceId}", deviceId);

    var config = new SessionConfig
    {
        DeviceId = deviceId,
        DeviceName = "Wavee Test Console",
        DeviceType = DeviceType.Computer
    };

    // 2. Get access token via OAuth
    Log.Information("");
    var oauthLogger = loggerFactory.CreateLogger("OAuth");
    var accessToken = await GetAccessTokenAsync(config.GetClientId(), oauthLogger);

    // 3. Create credentials
    var credentials = Credentials.WithAccessToken(accessToken);

    // 4. Create and connect session
    Log.Information("Creating session...");
    await using var session = Session.Create(config, logger);

    // Subscribe to events
    session.PacketReceived += (sender, e) =>
    {
        logger.LogInformation("Received packet: {PacketType} ({PayloadSize} bytes)",
            e.PacketType, e.Payload.Length);
    };

    session.Disconnected += (sender, e) =>
    {
        logger.LogWarning("Session disconnected!");
    };

    // 5. Connect
    Log.Information("Connecting to Spotify...");
    await session.ConnectAsync(credentials);

    var userData = session.GetUserData();
    if (userData != null)
    {
        Log.Information("Successfully connected!");
        Log.Information("  Username: {Username}", userData.Username);

        // Wait for country code and account type from server packets
        Log.Information("  Country:  Waiting for server data...");
        var countryCode = await session.GetCountryCodeAsync();
        Log.Information("  Country:  {Country}", countryCode);

        Log.Information("  Account:  Waiting for server data...");
        var accountType = await session.GetAccountTypeAsync();
        Log.Information("  Account:  {Account}", accountType);

        Log.Information("");
    }

    // 6. Keep session alive for a while to see packets
    Log.Information("Session running... Press any key to disconnect.");

    // Wait for user input to stop
    await Task.Run(() => Console.ReadKey(true));

    // 7. Cleanup (await using handles disposal automatically)
    Log.Information("Disconnecting...");
    // Session disposed here by await using

    Log.Information("Session closed successfully.");
}
catch (OAuthException ex)
{
    logger.LogError(ex, "OAuth failed: {Reason}", ex.Reason);
    Log.Error("OAuth authentication failed: {Message}", ex.Message);
    Log.Error("  Reason: {Reason}", ex.Reason);
}
catch (AuthenticationException ex)
{
    logger.LogError(ex, "Authentication failed: {Reason}", ex.Reason);
    Log.Error("Authentication failed: {Message}", ex.Message);
    Log.Error("  Reason: {Reason}", ex.Reason);
}
catch (SessionException ex)
{
    logger.LogError(ex, "Session error: {Reason}", ex.Reason);
    Log.Error("Session error: {Message}", ex.Message);
    Log.Error("  Reason: {Reason}", ex.Reason);
}
catch (Exception ex)
{
    logger.LogError(ex, "Unexpected error");
    Log.Error("Unexpected error: {Message}", ex.Message);
}

Log.Information("Press any key to exit...");
Console.ReadKey();

// Helper methods

static async Task<string> GetAccessTokenAsync(string clientId, Microsoft.Extensions.Logging.ILogger logger)
{
    var refreshToken = LoadRefreshToken();

    // If we have a cached refresh token, ask user if they want to use it
    if (!string.IsNullOrEmpty(refreshToken))
    {
        if (AskUseStoredToken())
        {
            Log.Information("Attempting to use stored refresh token...");

            var oauthClient = OAuthClient.Create(
                clientId,
                ["streaming", "user-read-playback-state", "user-modify-playback-state"],
                openBrowser: false,
                logger: logger);

            try
            {
                var token = await oauthClient.RefreshTokenAsync(refreshToken);
                Log.Information("Successfully refreshed access token!");
                return token.AccessToken;
            }
            catch (OAuthException ex)
            {
                Log.Warning("Failed to refresh token ({Reason}), will perform full OAuth flow", ex.Reason);
            }
        }
        else
        {
            Log.Information("User chose to perform fresh authorization");
        }
    }

    // No cached token, user declined, or refresh failed - perform full OAuth flow
    var flow = SelectOAuthFlow();

    Log.Information("");
    Log.Information("Starting OAuth authorization...");
    Log.Information("");

    OAuthToken newToken;

    if (flow == OAuthFlow.DeviceCode)
    {
        Log.Information("Using Device Code Flow");
        Log.Information("You'll need to visit a URL and enter a code to authorize.");
        Log.Information("");

        var client = OAuthClient.CreateCustom(
            clientId,
            ["streaming", "user-read-playback-state", "user-modify-playback-state"],
            flow: OAuthFlow.DeviceCode,
            logger: logger);

        newToken = await client.GetAccessTokenAsync();
    }
    else
    {
        Log.Information("Using Authorization Code Flow");
        Log.Information("Your browser will open to authorize Wavee.");
        Log.Information("");

        var client = OAuthClient.CreateCustom(
            clientId,
            ["streaming", "user-read-playback-state", "user-modify-playback-state"],
            flow: OAuthFlow.AuthorizationCode,
            openBrowser: true,
            logger: logger);

        newToken = await client.GetAccessTokenAsync();
    }

    // Save refresh token for next time
    SaveRefreshToken(newToken.RefreshToken);
    Log.Information("Successfully obtained access token via OAuth!");

    return newToken.AccessToken;
}

static bool AskUseStoredToken()
{
    Console.WriteLine();
    Console.WriteLine("╔════════════════════════════════════════════════════════╗");
    Console.WriteLine("║           Stored Credentials Found                    ║");
    Console.WriteLine("╠════════════════════════════════════════════════════════╣");
    Console.WriteLine("║                                                        ║");
    Console.WriteLine("║  A stored refresh token was found.                     ║");
    Console.WriteLine("║                                                        ║");
    Console.WriteLine("║  [Y] Use stored token (Quick & Easy)                   ║");
    Console.WriteLine("║      → Login automatically with saved credentials      ║");
    Console.WriteLine("║                                                        ║");
    Console.WriteLine("║  [N] Perform fresh authorization (Default)             ║");
    Console.WriteLine("║      → Start new OAuth flow                            ║");
    Console.WriteLine("║      → Recommended if you want to switch accounts      ║");
    Console.WriteLine("║                                                        ║");
    Console.WriteLine("╚════════════════════════════════════════════════════════╝");
    Console.WriteLine();
    Console.Write("Use stored token? [y/N] (default: N): ");

    var input = Console.ReadLine()?.Trim().ToLowerInvariant();

    return input == "y" || input == "yes";
}

static OAuthFlow SelectOAuthFlow()
{
    Console.WriteLine();
    Console.WriteLine("╔════════════════════════════════════════════════════════╗");
    Console.WriteLine("║        Select OAuth Authorization Method              ║");
    Console.WriteLine("╠════════════════════════════════════════════════════════╣");
    Console.WriteLine("║                                                        ║");
    Console.WriteLine("║  [1] Authorization Code Flow (Recommended)            ║");
    Console.WriteLine("║      → Opens browser automatically                     ║");
    Console.WriteLine("║      → Quick and seamless                              ║");
    Console.WriteLine("║      → Best for desktop use                            ║");
    Console.WriteLine("║                                                        ║");
    Console.WriteLine("║  [2] Device Code Flow                                  ║");
    Console.WriteLine("║      → Authorize on any device                         ║");
    Console.WriteLine("║      → Enter code manually                             ║");
    Console.WriteLine("║      → Best for headless/remote systems                ║");
    Console.WriteLine("║                                                        ║");
    Console.WriteLine("╚════════════════════════════════════════════════════════╝");
    Console.WriteLine();
    Console.Write("Enter your choice [1 or 2] (default: 1): ");

    var input = Console.ReadLine()?.Trim();

    return input == "2"
        ? OAuthFlow.DeviceCode
        : OAuthFlow.AuthorizationCode;
}

static string? LoadRefreshToken()
{
    var tokenPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Wavee",
        "refresh_token.txt"
    );

    if (File.Exists(tokenPath))
    {
        return File.ReadAllText(tokenPath).Trim();
    }

    return null;
}

static void SaveRefreshToken(string refreshToken)
{
    var tokenPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Wavee",
        "refresh_token.txt"
    );

    var directory = Path.GetDirectoryName(tokenPath);
    if (directory != null)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(tokenPath, refreshToken);
    }
}

static string ReadPassword()
{
    var password = string.Empty;
    ConsoleKey key;

    do
    {
        var keyInfo = Console.ReadKey(intercept: true);
        key = keyInfo.Key;

        if (key == ConsoleKey.Backspace && password.Length > 0)
        {
            password = password[0..^1];
            Console.Write("\b \b");
        }
        else if (!char.IsControl(keyInfo.KeyChar))
        {
            password += keyInfo.KeyChar;
            Console.Write("*");
        }
    } while (key != ConsoleKey.Enter);

    return password;
}

static string GetOrCreateDeviceId()
{
    // Try to load from file
    var deviceIdPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Wavee",
        "device_id.txt"
    );

    if (File.Exists(deviceIdPath))
    {
        return File.ReadAllText(deviceIdPath).Trim();
    }

    // Create new device ID
    var deviceId = Guid.NewGuid().ToString();

    // Save for next time
    var directory = Path.GetDirectoryName(deviceIdPath);
    if (directory != null)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(deviceIdPath, deviceId);
    }

    return deviceId;
}
