using Avalonia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Swiftlet.Application;
using Swiftlet.Desktop.Localization;
using Swiftlet.Desktop.Services;
using Swiftlet.Desktop.Settings;
using Swiftlet.Desktop.ViewModels;
using Swiftlet.Infrastructure;
using Swiftlet.Infrastructure.Persistence;

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
        var builder = Host.CreateApplicationBuilder(args);
        var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Swiftlet");
        builder.Services.AddApplication();
        builder.Services.AddInfrastructure(dataDirectory);
        var settings = builder.Services.AddAppSettings(Path.Combine(dataDirectory, "settings.json"));
        LocalizationManager.Instance.SetPreference(settings.Load());
        builder.Services.AddSingleton<ILocalizer>(LocalizationManager.Instance);
        builder.Services.AddSingleton<LanguageSelectorViewModel>();
        builder.Services.AddSingleton<IRequestDispatcher, ScopedRequestDispatcher>();
        builder.Services.AddSingleton<IDialogService, AvaloniaDialogService>();
        builder.Services.AddSingleton<IUiThread, AvaloniaUiThread>();
        builder.Services.AddSingleton<MainWindowViewModel>();
        builder.Services.AddTransient<AddAccountViewModel>();
        builder.Services.AddTransient<LabelManagerViewModel>();
        builder.Services.AddTransient<SettingsViewModel>();

        using var host = builder.Build();
        InitializeDatabaseAsync(host.Services).GetAwaiter().GetResult();
        host.Start();
        try
        {
            BuildAvaloniaApp(host.Services).StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            host.StopAsync().GetAwaiter().GetResult();
        }
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

        // Existing account databases are opened once so their schema upgrades run before the UI reads them.
        var factory = scope.ServiceProvider.GetRequiredService<IAccountDbContextFactory>();
        foreach (var accountId in await global.Accounts.AsNoTracking().Select(a => a.Id).ToListAsync())
        {
            await using var accountContext = await factory.CreateAsync(accountId);
        }
    }
}
