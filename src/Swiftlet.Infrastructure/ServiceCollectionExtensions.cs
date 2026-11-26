using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Swiftlet.Application.Abstractions;
using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Folders;
using Swiftlet.Domain.Labels;
using Swiftlet.Domain.Messages;
using Swiftlet.Infrastructure.Mail;
using Swiftlet.Infrastructure.OAuth;
using Swiftlet.Infrastructure.Persistence;
using Swiftlet.Infrastructure.Persistence.Repositories;
using Swiftlet.Infrastructure.Security;
using Swiftlet.Infrastructure.Sync;

namespace Swiftlet.Infrastructure;

/// <summary>
/// Dependency injection registration for the infrastructure layer.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers persistence, secret storage, OAuth 2.0, MailKit IMAP/SMTP services and the background sync worker.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="dataDirectory">The directory holding the global SQLite database, the per-account mail databases (accounts subdirectory), settings and file-based secrets.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        Directory.CreateDirectory(dataDirectory);
        var databasePath = Path.Combine(dataDirectory, "swiftlet.db");
        var oauthSettingsPath = Path.Combine(dataDirectory, "oauth.json");

        services.AddDbContext<GlobalDbContext>(options => options.UseSqlite($"Data Source={databasePath};Default Timeout=30"));
        services.AddSingleton<IAccountDbContextFactory>(_ => new AccountDbContextFactory(Path.Combine(dataDirectory, "accounts")));
        services.AddScoped<AccountContextProvider>();
        services.AddScoped<IAccountRepository, AccountRepository>();
        services.AddScoped<IFolderRepository, FolderRepository>();
        services.AddScoped<IMessageRepository, MessageRepository>();
        services.AddScoped<ILabelRepository, LabelRepository>();

        services.AddSingleton<ISecretStore>(sp => PlatformSecretStore.Create(dataDirectory, sp.GetRequiredService<ILoggerFactory>().CreateLogger("Swiftlet.Secrets")));

        services.AddSingleton(_ => OAuthClientConfiguration.Load(oauthSettingsPath, Environment.GetEnvironmentVariable));
        services.AddSingleton(_ => new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) })
        {
            Timeout = TimeSpan.FromSeconds(30),
        });
        services.AddSingleton<OAuthTokenClient>();
        services.AddSingleton<IBrowserLauncher, ProcessBrowserLauncher>();
        services.AddSingleton<IAccessTokenProvider, OAuthTokenService>();
        services.AddSingleton<IOAuthAuthorizer>(sp => new OAuthAuthorizer(
            sp.GetRequiredService<OAuthTokenClient>(),
            sp.GetRequiredService<IBrowserLauncher>(),
            sp.GetRequiredService<OAuthClientConfiguration>(),
            oauthSettingsPath));

        services.AddSingleton<IImapSessionFactory, MailKitImapSessionFactory>();
        services.AddSingleton<IMailSender, MailKitMailSender>();
        services.AddHostedService<AccountSyncWorker>();
        services.AddHostedService<InboxWatcher>();
        return services;
    }
}
