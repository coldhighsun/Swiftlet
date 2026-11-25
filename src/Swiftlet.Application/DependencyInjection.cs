using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Swiftlet.Application.Abstractions;
using Swiftlet.Application.Sync;

namespace Swiftlet.Application;

/// <summary>
/// Dependency injection registration for the application layer.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registers the Mediator handlers, the synchronization service and the sync schedule.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Handlers depend on scoped repositories, so they must be scoped too.
        services.AddMediator(options => options.ServiceLifetime = ServiceLifetime.Scoped);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new MailSyncOptions());
        services.AddSingleton<SyncSchedule>();
        services.TryAddSingleton<ISyncSettings, DefaultSyncSettings>();
        services.AddScoped<MailSyncService>();
        return services;
    }
}
