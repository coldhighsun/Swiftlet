using Microsoft.Extensions.DependencyInjection;
using Swiftlet.Application.Abstractions;
using Swiftlet.Application.Ai;
using Swiftlet.Desktop.Localization;

namespace Swiftlet.Desktop.Settings;

/// <summary>
/// Registers the settings store.
/// </summary>
public static class SettingsServiceCollectionExtensions
{
    /// <summary>
    /// Registers one <see cref="AppSettings"/> instance as the language store, the sync settings, the AI settings and the
    /// editable settings.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="path">The settings file path.</param>
    /// <returns>The registered instance.</returns>
    public static AppSettings AddAppSettings(this IServiceCollection services, string path)
    {
        var settings = new AppSettings(path);
        // Call this after AddApplication(): that registers default sync and AI settings with TryAdd, and the last
        // registration wins when the service is resolved.
        services.AddSingleton<ILanguagePreferenceStore>(settings);
        services.AddSingleton<ISyncSettings>(settings);
        services.AddSingleton<IAppSettings>(settings);
        services.AddSingleton<IAiSettings>(settings);
        return settings;
    }
}
