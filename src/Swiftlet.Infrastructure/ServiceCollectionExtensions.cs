using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Swiftlet.Application.Abstractions;
using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Folders;
using Swiftlet.Domain.Messages;
using Swiftlet.Infrastructure.Mail;
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
    /// Registers persistence, secret storage, MailKit IMAP/SMTP services and the background sync worker.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="dataDirectory">The directory holding the SQLite database and the secret store.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        Directory.CreateDirectory(dataDirectory);
        var databasePath = Path.Combine(dataDirectory, "swiftlet.db");

        services.AddDbContext<SwiftletDbContext>(options => options.UseSqlite($"Data Source={databasePath};Default Timeout=30"));
        services.AddScoped<IAccountRepository, AccountRepository>();
        services.AddScoped<IFolderRepository, FolderRepository>();
        services.AddScoped<IMessageRepository, MessageRepository>();

        services.AddSingleton<ISecretStore>(_ => new DataProtectionSecretStore(dataDirectory));
        services.AddSingleton<IImapSessionFactory, MailKitImapSessionFactory>();
        services.AddSingleton<IMailSender, MailKitMailSender>();
        services.AddHostedService<AccountSyncWorker>();
        return services;
    }
}
