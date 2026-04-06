using System.IO;
using System.Windows;
using HPPAQDeploy.App.Services;
using HPPAQDeploy.App.ViewModels;
using HPPAQDeploy.Core.Interfaces;
using HPPAQDeploy.Infrastructure.Data;
using HPPAQDeploy.Infrastructure.Hpia;
using HPPAQDeploy.Infrastructure.Network;
using HPPAQDeploy.Infrastructure.Remote;
using HPPAQDeploy.Infrastructure.Security;
using HPPAQDeploy.Infrastructure.Services;
using HPPAQDeploy.Shared.Configuration;
using HPPAQDeploy.Shared.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace HPPAQDeploy.App;

public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        try
        {
            base.OnStartup(e);

            // Global exception handlers to prevent silent crashes
            var showingError = false;
            DispatcherUnhandledException += (_, args) =>
            {
                Log.Error(args.Exception, "Unhandled UI exception");
                args.Handled = true;
                if (showingError) return; // Prevent cascading dialogs
                showingError = true;
                MessageBox.Show(
                    $"An unexpected error occurred:\n\n{args.Exception.Message}\n\nDetails have been logged.",
                    "HPPAQDeploy Error", MessageBoxButton.OK, MessageBoxImage.Error);
                showingError = false;
            };
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                Log.Error(args.Exception, "Unobserved task exception");
                args.SetObserved();
            };

            // Load persisted settings, then ensure directories exist
            AppSettings.Load();
            Directory.CreateDirectory(AppSettings.LogPath);
            Directory.CreateDirectory(Path.GetDirectoryName(AppSettings.DatabasePath)!);

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
                .MinimumLevel.Override("Microsoft.Extensions.Hosting", Serilog.Events.LogEventLevel.Warning)
                .Enrich.FromLogContext()
                .WriteTo.File(
                    Path.Combine(AppSettings.LogPath, "hppaq-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{Properties:j}{NewLine}{Exception}")
                .CreateLogger();

            _host = Host.CreateDefaultBuilder()
                .UseSerilog()
                .ConfigureServices((context, services) =>
                {
                    // Database
                    services.AddDbContext<AppDbContext>(ServiceLifetime.Transient);

                    // Infrastructure services
                    services.AddSingleton(new CircuitBreaker(failureThreshold: 3, openDuration: TimeSpan.FromMinutes(2)));
                    services.AddSingleton<INetworkScanner, PingSweeper>();
                    services.AddSingleton<IDeviceDiscovery, WmiDeviceDiscovery>();
                    services.AddTransient<ICredentialStore, DpapiCredentialStore>();
                    services.AddSingleton<IRemoteExecutor, DcomRemoteExecutor>();
                    services.AddSingleton<IFileTransfer, SmbFileTransfer>();
                    services.AddSingleton<IHpiaManager, HpiaManager>();
                    services.AddTransient<IDeviceRepository, DeviceRepository>();
                    services.AddTransient<IDeviceGroupRepository, DeviceGroupRepository>();
                    services.AddTransient<IDeploymentHistoryRepository, DeploymentHistoryRepository>();
                    services.AddSingleton<HpiaExtractor>();
                    services.AddSingleton<HpiaReportParser>();
                    services.AddSingleton<RepositorySyncer>();
                    services.AddSingleton<IEmailService, EmailService>();

                    // Background services
                    services.AddSingleton<ScheduledScanService>();

                    // ViewModels — Singleton so background work survives tab switches
                    services.AddSingleton<MainViewModel>();
                    services.AddSingleton<DashboardViewModel>();
                    services.AddSingleton<DevicesViewModel>();
                    services.AddSingleton<GroupsViewModel>();
                    services.AddSingleton<DeployViewModel>();
                    services.AddSingleton<HistoryViewModel>();
                    services.AddSingleton<CredentialManagerViewModel>();
                    services.AddSingleton<LogViewModel>();
                    services.AddSingleton<SettingsViewModel>();
                    services.AddSingleton<AboutViewModel>();

                    // Main Window
                    services.AddSingleton<MainWindow>();
                })
                .Build();

            await _host.StartAsync();

            // Ensure database is created and schema is up-to-date
            using (var scope = _host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Initialize();
            }

            // Start scheduled scan service
            var scheduledScanService = _host.Services.GetRequiredService<ScheduledScanService>();
            scheduledScanService.Start();

            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            mainWindow.DataContext = _host.Services.GetRequiredService<MainViewModel>();
            mainWindow.Show();

            Log.Information("HPPAQDeploy started");
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application startup failed");
            MessageBox.Show(
                $"Failed to start HPPAQDeploy:\n\n{ex.Message}",
                "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            Log.Information("HPPAQDeploy shutting down");
            if (_host is not null)
            {
                _host.Services.GetRequiredService<ScheduledScanService>().Dispose();
                await _host.StopAsync();
                _host.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during application shutdown");
        }
        finally
        {
            Log.CloseAndFlush();
            base.OnExit(e);
        }
    }
}
