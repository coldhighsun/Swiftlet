using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Swiftlet.Desktop.Localization;
using Swiftlet.Desktop.Services;
using Swiftlet.Desktop.Settings;
using Swiftlet.Desktop.ViewModels;
using Swiftlet.Infrastructure.Persistence;
using Swiftlet.Infrastructure.Persistence.Encryption;

namespace Swiftlet.Desktop;

/// <summary>
/// The Avalonia application.
/// </summary>
/// <param name="services">The application's service provider.</param>
/// <param name="startup">Starts the application once the databases are ready; omitted when there is nothing to prepare.</param>
public sealed class App(IServiceProvider services, AppStartup? startup) : Avalonia.Application
{
    /// <summary>
    /// Initializes a new instance of the <see cref="App"/> class with nothing to prepare. The XAML loader also looks for
    /// this constructor.
    /// </summary>
    /// <param name="services">The application's service provider.</param>
    public App(IServiceProvider services)
        : this(services, null)
    {
    }

    /// <summary>
    /// Loads the application XAML.
    /// </summary>
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Creates the first window once the framework is initialized: the main window, or the startup window while the
    /// databases are not ready.
    /// </summary>
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

            // The saved theme is applied before the first window, so nothing flashes in the other one.
            if (services.GetService<IAppSettings>() is { } settings)
            {
                services.GetService<IThemeApplier>()?.Apply(settings.Theme);
            }

            desktop.MainWindow = startup is null || startup.IsStarted
                ? CreateMainWindow()
                : CreateStartupWindow(desktop, startup);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Creates the main window.
    /// </summary>
    /// <returns>The window.</returns>
    private MainWindow CreateMainWindow()
    {
        return new MainWindow
        {
            DataContext = services.GetRequiredService<MainWindowViewModel>(),
        };
    }

    /// <summary>
    /// Creates the window that prepares the databases and then hands over to the main window.
    /// </summary>
    /// <param name="desktop">The desktop lifetime.</param>
    /// <param name="pending">Starts the application once the databases are ready.</param>
    /// <returns>The window.</returns>
    private StartupWindow CreateStartupWindow(IClassicDesktopStyleApplicationLifetime desktop, AppStartup pending)
    {
        var viewModel = new StartupViewModel(
            services.GetRequiredService<IDatabaseProtection>(),
            services.GetRequiredService<ILocalizer>(),
            services.GetRequiredService<IDialogService>(),
            services.GetRequiredService<IUiThread>(),
            AccountNameAsync,
            services.GetService<ILogger<StartupViewModel>>());
        var window = new StartupWindow { DataContext = viewModel };
        viewModel.QuitRequested += (_, _) => desktop.Shutdown();
        viewModel.Completed += async (_, _) =>
        {
            try
            {
                await Task.Run(pending.StartAsync);
                var main = CreateMainWindow();

                // The main window takes over first, so closing the startup window does not end the application.
                desktop.MainWindow = main;
                main.Show();
                window.Close();
            }
            catch (Exception ex)
            {
                // An event handler must not throw; without databases or background services nothing can work.
                services.GetService<ILogger<App>>()?.LogCritical(ex, "Starting the application failed");
                desktop.Shutdown(1);
            }
        };
        window.Opened += (_, _) => viewModel.RunCommand.Execute(null);
        return window;
    }

    /// <summary>
    /// Reads the address of an account from the global store.
    /// </summary>
    /// <param name="accountId">The account.</param>
    /// <returns>The address, or <see langword="null"/> when the account is unknown.</returns>
    private async Task<string?> AccountNameAsync(Guid accountId)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<GlobalDbContext>().Accounts
            .AsNoTracking()
            .Where(a => a.Id == accountId)
            .Select(a => a.EmailAddress)
            .FirstOrDefaultAsync();
    }
}
