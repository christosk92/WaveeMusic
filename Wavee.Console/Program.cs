using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Wavee.Core.Authentication;
using Wavee.Core.Session;

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
    // 1. Get credentials from user
    Log.Information("Spotify no longer supports username/password authentication for third-party clients.");
    Log.Information("You need an OAuth access token to authenticate.");
    Log.Information("");
    Log.Information("To get an access token:");
    Log.Information("  1. Go to: https://developer.spotify.com/console/get-current-user/");
    Log.Information("  2. Click 'Get Token' and authorize");
    Log.Information("  3. Copy the access token");
    Log.Information("");
    Log.Information("Enter your Spotify access token:");
    var token = Console.ReadLine();

    if (string.IsNullOrWhiteSpace(token))
    {
        Log.Error("Access token is required!");
        return;
    }

    // 2. Create session configuration
    var deviceId = GetOrCreateDeviceId();
    Log.Information("Using device ID: {DeviceId}", deviceId);

    var config = new SessionConfig
    {
        DeviceId = deviceId,
        DeviceName = "Wavee Test Console",
        DeviceType = DeviceType.Computer
    };

    // 3. Create credentials
    var credentials = Credentials.WithAccessToken(token);

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
