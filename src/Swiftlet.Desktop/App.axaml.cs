using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Swiftlet.Application.Sync;
using Swiftlet.Desktop.Hosting;
using Swiftlet.Desktop.Localization;
using Swiftlet.Desktop.Services;
using Swiftlet.Desktop.Settings;
using Swiftlet.Desktop.ViewModels;
using Swiftlet.Desktop.ViewModels.Main;
using Swiftlet.Desktop.Views;
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
    /// Brings the current main window to the front.
    /// </summary>
    private WindowActivator? _activator;

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

            if (startup is null || startup.IsStarted)
            {
                var main = CreateMainWindow();
                AdoptMain(desktop, main);
                KeepInTray(desktop, main);
                AnnounceNewMail(desktop, main);
            }
            else
            {
                Adopt(desktop, CreateStartupWindow(desktop, startup));
            }

            ShowWindowOnRequest();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Creates the main window.
    /// </summary>
    /// <returns>The window.</returns>
    private MainWindow CreateMainWindow()
    {
        var window = new MainWindow
        {
            DataContext = services.GetRequiredService<MainWindowViewModel>(),
        };

        // Attached here, before the tray handling, which hides the window while it is closing.
        if (services.GetService<IAppSettings>() is { } settings)
        {
            RememberWindowPlacement.Attach(window, settings, services.GetService<ILogger<RememberWindowPlacement>>());
        }

        return window;
    }

    /// <summary>
    /// Makes a window the main window and the one shown on request.
    /// </summary>
    /// <param name="desktop">The desktop lifetime.</param>
    /// <param name="window">The window.</param>
    private void Adopt(IClassicDesktopStyleApplicationLifetime desktop, Window window)
    {
        desktop.MainWindow = window;
        _activator = new WindowActivator(window);
    }

    /// <summary>
    /// Makes the main window the main window, or, when the setting says so, keeps it hidden until something shows it
    /// (the tray icon, a second start, a new mail pop-up). The framework shows <c>MainWindow</c> on its own, so a hidden
    /// start leaves it unset until the window is first opened, and ends the application by itself when the window closes.
    /// </summary>
    /// <param name="desktop">The desktop lifetime.</param>
    /// <param name="window">The main window.</param>
    /// <returns><see langword="true"/> if the window was left hidden.</returns>
    private bool AdoptMain(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window)
    {
        if (services.GetService<IAppSettings>() is not { StartMinimized: true })
        {
            Adopt(desktop, window);
            return false;
        }

        _activator = new WindowActivator(window);
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        window.Closed += (_, _) => desktop.Shutdown();
        window.Opened += (_, _) => desktop.MainWindow ??= window;
        return true;
    }

    /// <summary>
    /// Shows the current window when another instance was started, or when the macOS Dock icon is clicked.
    /// </summary>
    private void ShowWindowOnRequest()
    {
        services.GetService<SingleInstance>()?.SetActivationHandler(
            () => Dispatcher.UIThread.Post(() => _activator?.BringToFront()));
        if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
        {
            activatable.Activated += (_, e) =>
            {
                if (e.Kind == ActivationKind.Reopen)
                {
                    _activator?.BringToFront();
                }
            };
        }
    }

    /// <summary>
    /// Adds the tray icon and lets closing the main window hide it, as long as the setting says so.
    /// </summary>
    /// <param name="desktop">The desktop lifetime.</param>
    /// <param name="window">The main window.</param>
    private void KeepInTray(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window)
    {
        if (services.GetService<IAppSettings>() is not { } settings || window.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var tray = new TrayViewModel(
            viewModel,
            services.GetRequiredService<ILocalizer>(),
            settings,
            () => _activator?.BringToFront(),
            () => desktop.Shutdown());
        var icon = new AvaloniaTrayIcon(this, tray);
        desktop.Exit += (_, _) =>
        {
            icon.Dispose();
            tray.Dispose();
        };
        CloseToTray.Attach(window, () => settings.CloseToTray);
    }

    /// <summary>
    /// Shows a pop-up when new mail arrives; clicking it brings up the main window with all inboxes.
    /// </summary>
    /// <param name="desktop">The desktop lifetime.</param>
    /// <param name="window">The main window.</param>
    private void AnnounceNewMail(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window)
    {
        if (services.GetService<IAppSettings>() is not { } settings || window.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var notifier = new NewMailNotifier(
            services.GetRequiredService<ISyncEvents>(),
            settings,
            services.GetRequiredService<IRequestDispatcher>(),
            services.GetRequiredService<ILocalizer>(),
            services.GetRequiredService<IUiThread>(),
            new AvaloniaNewMailPresenter(),
            shown =>
            {
                _activator?.BringToFront();
                _ = viewModel.ShowMessageAsync(shown);
            },
            viewModel.RefreshAfterLocalChange,
            services.GetService<ILogger<NewMailNotifier>>(),
            services.GetService<IContactDirectory>());
        desktop.Exit += (_, _) => notifier.Dispose();
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
                var hidden = AdoptMain(desktop, main);
                KeepInTray(desktop, main);
                AnnounceNewMail(desktop, main);
                if (!hidden)
                {
                    main.Show();
                }

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
