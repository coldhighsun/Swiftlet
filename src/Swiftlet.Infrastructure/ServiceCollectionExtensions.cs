using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Folders;
using Swiftlet.Domain.Messages;
using Swiftlet.Infrastructure.Persistence;
using Swiftlet.Infrastructure.Persistence.Repositories;

namespace Swiftlet.Infrastructure;

/// <summary>
/// Dependency injection registration for the infrastructure layer.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the SQLite database context and repositories.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="databasePath">The path of the SQLite database file.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, string databasePath)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        services.AddDbContext<SwiftletDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
        services.AddScoped<IAccountRepository, AccountRepository>();
        services.AddScoped<IFolderRepository, FolderRepository>();
        services.AddScoped<IMessageRepository, MessageRepository>();
        return services;
    }
}
