using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using System;
using System.IO;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Wavee.WinUI.Helpers;
using Wavee.WinUI.Services;
using Wavee.WinUI.ViewModels;
using Wavee.WinUI.Views;

namespace Wavee.WinUI
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;
        private readonly IServiceProvider _serviceProvider;

        /// <summary>
        /// Gets the current application instance as <see cref="App"/>.
        /// </summary>
        public static new App Current => (App)Application.Current;

        /// <summary>
        /// Gets the service provider for dependency injection.
        /// </summary>
        public IServiceProvider Services => _serviceProvider;

        /// <summary>
        /// Gets the main application window.
        /// </summary>
        public Window Window => _window!;

        public static App Instance { get; private set; }

        public MainWindowViewModel ViewModel => (Window as MainWindow)?.ViewModel;

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            InitializeComponent();
            Instance = this;
            // Configure Serilog BEFORE building DI container
            ConfigureSerilog();

            // Register global exception handler
            UnhandledException += OnUnhandledException;

            // Configure services and build DI container
            _serviceProvider = ConfigureServices();
        }

        /// <summary>
        /// Global exception handler for unhandled exceptions.
        /// Logs the exception before the application crashes.
        /// </summary>
        private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            // Log the exception
            Log.Fatal(e.Exception, "Unhandled exception occurred: {Message}", e.Message);
            Log.CloseAndFlush();

            // By default, we let the app crash (e.Handled = false)
            // This allows Windows Error Reporting to capture crash dumps
            // If you want to prevent the crash, set e.Handled = true
            // but this can lead to undefined application state

#if DEBUG
            // In debug mode, let the exception propagate to the debugger
            e.Handled = false;
#else
            // In release mode, you could optionally handle it and show an error dialog
            // e.Handled = true;
            // ShowErrorDialog(e.Exception);
            e.Handled = false;
#endif
        }

        /// <summary>
        /// Configures Serilog for structured logging.
        /// </summary>
        private static void ConfigureSerilog()
        {
            // Get application local data folder for logs
            var logFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Wavee",
                "Logs"
            );

            // Ensure directory exists
            Directory.CreateDirectory(logFolder);

            var logFile = Path.Combine(logFolder, "wavee-.log");

            // Configure Serilog
            var loggerConfig = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
                .MinimumLevel.Override("System", LogEventLevel.Information)
                .Enrich.FromLogContext()
                .Enrich.WithThreadId();

#if DEBUG
            // In debug builds, also write to Debug output
            loggerConfig.WriteTo.Debug(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"
            );
#endif

            // Always write to file (with daily rolling)
            loggerConfig.WriteTo.Async(a => a.File(
                new CompactJsonFormatter(),
                logFile,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 31,
                buffered: true,
                flushToDiskInterval: TimeSpan.FromSeconds(1)
            ));

            Log.Logger = loggerConfig.CreateLogger();

            // Log startup
            Log.Information("=== Wavee.WinUI Application Starting ===");
            Log.Information("Log file location: {LogFolder}", logFolder);
            Log.Information("OS: {OS}, Version: {Version}",
                Environment.OSVersion.Platform,
                Environment.OSVersion.Version);
        }

        /// <summary>
        /// Configures the dependency injection container.
        /// </summary>
        /// <returns>The configured service provider.</returns>
        private static IServiceProvider ConfigureServices()
        {
            var services = new ServiceCollection();

            // HTTP Client Factory
            services.AddHttpClient("Wavee", client =>
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Wavee/1.0");
            });

            // Logging - integrate Serilog
            services.AddLogging(builder =>
            {
                builder.ClearProviders(); // Remove default providers
                builder.AddSerilog(dispose: true); // Add Serilog

#if DEBUG
                builder.SetMinimumLevel(LogLevel.Debug);
#else
                builder.SetMinimumLevel(LogLevel.Information);
#endif
            });

            // Services
            services.AddSingleton<IAuthenticationService, AuthenticationService>();
            services.AddSingleton<INavigationService, NavigationService>();

            // ViewModels
            services.AddTransient<SplashViewModel>();
            services.AddTransient<LoginViewModel>();
            services.AddTransient<MainWindowViewModel>();
            services.AddTransient<ShellViewModel>();
            services.AddTransient<HomeViewModel>();

            return services.BuildServiceProvider();
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            // Register for process exit to flush logs
            AppDomain.CurrentDomain.ProcessExit += (s, e) =>
            {
                Log.Information("=== Wavee.WinUI Application Shutting Down ===");
                Log.CloseAndFlush();
            };

            // Show splash screen and perform authentication initialization
            await ShowSplashAndInitializeAsync();
        }

        /// <summary>
        /// Shows the splash screen, initializes authentication, and navigates to appropriate page.
        /// </summary>
        private async System.Threading.Tasks.Task ShowSplashAndInitializeAsync()
        {
            Log.Information("ShowSplashAndInitializeAsync starting");

            var authService = Services.GetService(typeof(IAuthenticationService)) as IAuthenticationService;
            if (authService == null)
            {
                Log.Fatal("IAuthenticationService not registered in DI container");
                throw new InvalidOperationException("IAuthenticationService not registered in DI container");
            }

            Log.Debug("AuthService resolved successfully");

            // Create startup window for splash/login flow
            Log.Debug("Creating startup window for splash/login flow");
            _window = new MainWindow();

            var rootFrame = new Microsoft.UI.Xaml.Controls.Frame();
            _window.Content = rootFrame;

            // Navigate to splash page
            Log.Information("Navigating to SplashPage");
            rootFrame.Navigate(typeof(Views.SplashPage));

            // Show the window
            Log.Debug("Activating startup window");
            _window.Activate();

            // Wait for initialization to complete
            var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
            bool hasTimedOut = false;

            // Subscribe to authentication state changes for handling post-timeout logins
            void OnLoginSuccess(object? sender, AuthenticationStateChangedEventArgs e)
            {
                if (e.IsAuthenticated)
                {
                    Log.Information("Login successful, transitioning to main window");
                    authService.AuthenticationStateChanged -= OnLoginSuccess;

                    _window.DispatcherQueue.TryEnqueue(() =>
                    {
                        // If we've timed out and are on LoginPage, navigate to Shell and recreate window
                        if (hasTimedOut)
                        {
                            Log.Debug("Post-timeout login: navigating to Shell");
                            (_window.Content as Frame)?.Navigate(typeof(Shell), null,
                                new EntranceNavigationTransitionInfo());
                        }

                        // Initialize theme cache for thread-safe access from XAML bindings
                        Log.Debug("Initializing theme cache");
                        ElementThemeHelper.InitializeThemeCache();
                    });

                    // Signal completion
                    tcs.TrySetResult(true);
                }
            }

            void OnAuthStateChanged(object? sender, AuthenticationStateChangedEventArgs e)
            {
                Log.Information("Authentication state changed: IsAuthenticated={IsAuthenticated}", e.IsAuthenticated);
                authService.AuthenticationStateChanged -= OnAuthStateChanged;

                // If authenticated before timeout, complete immediately
                if (e.IsAuthenticated && !hasTimedOut)
                {
                    tcs.TrySetResult(true);
                }
            }

            // Subscribe to both handlers
            authService.AuthenticationStateChanged += OnAuthStateChanged;
            authService.AuthenticationStateChanged += OnLoginSuccess;
            Log.Debug("Subscribed to AuthenticationStateChanged event");

            // If initialization doesn't complete within 5 seconds (no cached credentials), show login
            Log.Debug("Starting authentication initialization task (5 second timeout)");
            var initTask = System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay(5000);
                if (!tcs.Task.IsCompleted)
                {
                    hasTimedOut = true;
                    Log.Information("Authentication initialization timed out, showing login page");
                    // Navigation needs to happen on UI thread
                    _window.DispatcherQueue.TryEnqueue(() =>
                    {
                        Log.Debug("Navigating to LoginPage");
                        rootFrame.Navigate(typeof(Views.LoginPage));
                    });

                    // Wait for login
                    return await tcs.Task;
                }
                Log.Debug("Authentication initialization completed before timeout");
                return await tcs.Task;
            });

            var isAuthenticated = await initTask;
            Log.Information("Authentication result: {IsAuthenticated}", isAuthenticated);

            if (isAuthenticated && !hasTimedOut)
            {
                Log.Information("User authenticated via cached credentials");

                // Initialize theme cache for thread-safe access from XAML bindings
                Log.Debug("Initializing theme cache");
                ElementThemeHelper.InitializeThemeCache();
            }
            else
            {
                Log.Debug("Waiting for post-timeout authentication completion");
                // Authentication will be handled by OnLoginSuccess
            }
        }
    }
}
