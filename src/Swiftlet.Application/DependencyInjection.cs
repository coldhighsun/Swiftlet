using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Swiftlet.Application.Abstractions;
using Swiftlet.Application.Logging;
using Swiftlet.Application.Messages;
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
        services.AddMediator(options =>
        {
            options.ServiceLifetime = ServiceLifetime.Scoped;
            options.PipelineBehaviors = [typeof(LoggingBehavior<,>)];
        });
        services.AddSingleton<LogNames>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new MailSyncOptions());
        services.AddSingleton<SyncSchedule>();
        services.TryAddSingleton<ISyncSettings, DefaultSyncSettings>();
        services.AddSingleton<AttachmentPreviewCache>();
        services.AddSingleton<SyncCoordinator>();
        services.AddSingleton<AccountSyncStatusTracker>();
        services.AddSingleton<AccountConnectionRevisions>();
        services.AddSingleton<SyncEvents>();
        services.AddSingleton<ISyncEvents>(static sp => sp.GetRequiredService<SyncEvents>());
        services.AddScoped<MailSyncService>();
        services.AddScoped<MailBackfillService>();
        services.AddSingleton<DeletionSignal>();
        services.AddScoped<PendingDeletionService>();
        services.AddSingleton<FlagCoordinator>();
        services.AddSingleton<FlagPushSignal>();
        services.AddScoped<PendingFlagService>();
        return services;
    }
}
