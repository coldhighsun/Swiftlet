using Avalonia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Swiftlet.Application;
using Swiftlet.Application.Abstractions;
using Swiftlet.Desktop.Localization;
using Swiftlet.Desktop.Services;
using Swiftlet.Desktop.Settings;
using Swiftlet.Desktop.ViewModels;
using Swiftlet.Infrastructure;
using Swiftlet.Infrastructure.Mail;
using Swiftlet.Infrastructure.Persistence;
using Swiftlet.Infrastructure.Security;

namespace Swiftlet.Desktop;

/// <summary>
/// Application entry point.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Starts the host (database, background sync) and then the desktop application.
    /// </summary>
    /// <param name="args">The command-line arguments.</param>
    [STAThread]
    public static void Main(string[] args)
    {
        MailEncodings.Register();
        var builder = Host.CreateApplicationBuilder(args);
        var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Swiftlet");
        var logger = LogSetup.Create(Path.Combine(dataDirectory, "logs"));
        builder.Services.AddSerilog(logger, dispose: true);
        LogProcessWideFailures(logger);
        logger.Information(
            "Swiftlet starting: version {Version}, {OS}, data directory {DataDirectory}",
            typeof(Program).Assembly.GetName().Version,
            System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            dataDirectory);
        ProtectDataDirectory(dataDirectory, logger);
        builder.Services.AddApplication();
        builder.Services.AddInfrastructure(dataDirectory);
        var settings = builder.Services.AddAppSettings(Path.Combine(dataDirectory, "settings.json"));
        LocalizationManager.Instance.SetPreference(settings.Load());
        builder.Services.AddSingleton<ILocalizer>(LocalizationManager.Instance);
        builder.Services.AddSingleton<LanguageSelectorViewModel>();
        builder.Services.AddSingleton<IRequestDispatcher, ScopedRequestDispatcher>();
        builder.Services.AddSingleton<IDialogService, AvaloniaDialogService>();
        builder.Services.AddSingleton<IUiThread, AvaloniaUiThread>();
        builder.Services.AddSingleton(sp => new HtmlPreviewStore(Path.GetTempPath(), sp.GetService<ILogger<HtmlPreviewStore>>()));
        builder.Services.AddSingleton<MainWindowViewModel>();
        builder.Services.AddTransient<AddAccountViewModel>();
        builder.Services.AddTransient<LabelManagerViewModel>();
        builder.Services.AddTransient<SettingsViewModel>();
        builder.Services.ConfigureHostShutdown();

        using var host = builder.Build();
        InitializeDatabaseAsync(host.Services).GetAwaiter().GetResult();
        // Mail previews are plain text: those a crashed run left behind are removed now, this run's own ones on exit.
        var previews = host.Services.GetRequiredService<HtmlPreviewStore>();
        previews.RemoveLeftovers();
        host.Start();
        try
        {
            BuildAvaloniaApp(host.Services).StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            previews.Clear();
            HostShutdown.Stop(host, logger);
            logger.Information("Swiftlet stopped");
        }
    }

    /// <summary>
    /// Keeps other local users out of the mail data.
    /// </summary>
    /// <param name="dataDirectory">The application data directory.</param>
    /// <param name="logger">The logger.</param>
    private static void ProtectDataDirectory(string dataDirectory, Serilog.ILogger logger)
    {
        try
        {
            DataDirectoryProtection.RestrictToCurrentUser(dataDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Some file systems refuse permission changes; the application still works, so it starts anyway.
            logger.Warning(ex, "Could not restrict access to the data directory {DataDirectory}", dataDirectory);
        }
    }

    /// <summary>
    /// Records failures nobody handled (any thread, unobserved tasks, the UI thread) so they are not lost; they are not swallowed.
    /// </summary>
    /// <param name="logger">The logger.</param>
    private static void LogProcessWideFailures(Serilog.ILogger logger)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            logger.Fatal(e.ExceptionObject as Exception, "Unhandled exception (terminating: {Terminating})", e.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, e) =>
            logger.Error(e.Exception, "Unobserved task exception");
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
            logger.Error(e.Exception, "Unhandled exception on the UI thread");
    }

    /// <summary>
    /// Builds the Avalonia application configuration.
    /// </summary>
    /// <param name="services">The application's service provider.</param>
    /// <returns>The configured app builder.</returns>
    public static AppBuilder BuildAvaloniaApp(IServiceProvider services)
    {
        return AppBuilder.Configure(() => new App(services))
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }

    /// <summary>
    /// Creates the database schema before anything reads from it.
    /// </summary>
    /// <param name="services">The service provider.</param>
    /// <returns>A task that completes when the schema is ready.</returns>
    private static async Task InitializeDatabaseAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var global = scope.ServiceProvider.GetRequiredService<GlobalDbContext>();
        await SqliteSchemaInitializer.InitializeGlobalAsync(global);

        await services.GetRequiredService<IRemoteImageRules>().LoadAsync();

        // Existing account databases are opened once so their schema upgrades run before the UI reads them.
        var factory = scope.ServiceProvider.GetRequiredService<IAccountDbContextFactory>();
        foreach (var accountId in await global.Accounts.AsNoTracking().Select(a => a.Id).ToListAsync())
        {
            await using var accountContext = await factory.CreateAsync(accountId);
        }
    }
}
